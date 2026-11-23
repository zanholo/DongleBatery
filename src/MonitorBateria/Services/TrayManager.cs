using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Text;
using System.Windows.Forms;
using MonitorBateria.Models;

namespace MonitorBateria.Services;

/// <summary>Ícone principal na área de notificação + ícones opcionais por dispositivo.</summary>
public sealed class TrayManager : IDisposable
{
    private readonly DeviceMonitor _monitor;
    private readonly AppSettings _settings;
    private readonly NotifyIcon _main;
    private readonly Dictionary<string, (NotifyIcon icon, IntPtr handle, string sig)> _deviceIcons = new();
    private IntPtr _mainHandle;
    private string _mainSig = "";
    private readonly ToolStripMenuItem _startupItem;
    private readonly ToolStripMenuItem _widgetItem;
    public event EventHandler<bool>? ToggleWidgetRequested;

    public event EventHandler? TogglePanelRequested;
    public event EventHandler? OpenSettingsRequested;
    public event EventHandler? ExitRequested;

    public TrayManager(DeviceMonitor monitor, AppSettings settings)
    {
        _monitor = monitor;
        _settings = settings;

        var menu = new ContextMenuStrip { ShowImageMargin = false, ShowCheckMargin = true, Font = new Font("Segoe UI", 9.5f) };
        menu.Renderer = new ToolStripProfessionalRenderer(new MenuColors(!ThemeHelper.AppsLight));
        if (!ThemeHelper.AppsLight) menu.ForeColor = Color.FromArgb(240, 240, 240);

        menu.Items.Add("Abrir painel", null, (_, _) => TogglePanelRequested?.Invoke(this, EventArgs.Empty)).Font = new Font("Segoe UI", 9.5f, FontStyle.Bold);
        menu.Items.Add("Atualizar agora", null, async (_, _) => await _monitor.RefreshAsync());
        menu.Items.Add("Configurações…", null, (_, _) => OpenSettingsRequested?.Invoke(this, EventArgs.Empty));
        _widgetItem = new ToolStripMenuItem("Widget flutuante") { CheckOnClick = true };
        _widgetItem.Click += (_, _) => ToggleWidgetRequested?.Invoke(this, _widgetItem.Checked);
        menu.Items.Add(_widgetItem);
        menu.Items.Add(new ToolStripSeparator());
        _startupItem = new ToolStripMenuItem("Iniciar com o Windows") { CheckOnClick = true };
        _startupItem.Click += (_, _) => StartupManager.Set(_startupItem.Checked);
        menu.Items.Add(_startupItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Sair", null, (_, _) => ExitRequested?.Invoke(this, EventArgs.Empty));
        menu.Opening += (_, _) =>
        {
            _startupItem.Checked = StartupManager.IsEnabled;
            _widgetItem.Checked = _settings.ShowWidget;
            if (!ThemeHelper.AppsLight) _widgetItem.ForeColor = Color.FromArgb(240, 240, 240);
            if (!ThemeHelper.AppsLight) _startupItem.ForeColor = Color.FromArgb(240, 240, 240);
        };

        _main = new NotifyIcon
        {
            Text = "Monitor de Bateria",
            ContextMenuStrip = menu,
            Visible = true,
        };
        _main.MouseClick += (_, e) =>
        {
            if (e.Button == MouseButtons.Left) TogglePanelRequested?.Invoke(this, EventArgs.Empty);
        };
        _main.BalloonTipClicked += (_, _) => TogglePanelRequested?.Invoke(this, EventArgs.Empty);

        Update();
    }

    public void ShowLowBattery(BatteryDevice d)
    {
        _main.ShowBalloonTip(8000, "Bateria fraca",
            $"{d.DisplayName} está com {d.LevelText} de bateria.", ToolTipIcon.Warning);
    }

    public void Update()
    {
        bool light = ThemeHelper.TaskbarLight;
        var visible = _monitor.Devices.Where(d => !d.IsHidden).ToList();

        // ---- Ícone principal ----
        BatteryDevice? target = null;
        if (!string.IsNullOrEmpty(_settings.MainTrayDevice))
            target = visible.FirstOrDefault(d => d.Key == _settings.MainTrayDevice);
        target ??= visible.Where(d => d.IsConnected && d.Level.HasValue).OrderBy(d => d.Level).FirstOrDefault();

        int? lvl = target?.IsConnected == true ? target.Level : null;
        bool chg = target?.IsCharging ?? false;
        var sig = $"{lvl}|{chg}|{light}|{TrayIconRenderer.IconSize}";
        if (sig != _mainSig)
        {
            var old = _mainHandle;
            _main.Icon = TrayIconRenderer.RenderMain(lvl, chg, light, out _mainHandle);
            if (old != IntPtr.Zero) Native.DestroyIcon(old);
            _mainSig = sig;
        }
        _main.Text = BuildTooltip(visible);

        // ---- Ícones por dispositivo ----
        var wanted = visible.Where(d => d.ShowInTray).ToList();
        foreach (var key in _deviceIcons.Keys.ToList())
        {
            if (wanted.Any(d => d.Key == key)) continue;
            var (icon, handle, _) = _deviceIcons[key];
            icon.Visible = false;
            icon.Dispose();
            if (handle != IntPtr.Zero) Native.DestroyIcon(handle);
            _deviceIcons.Remove(key);
        }
        foreach (var d in wanted)
        {
            var dsig = $"{d.Level}|{d.IsConnected}|{d.IsCharging}|{d.Kind}|{light}|{TrayIconRenderer.IconSize}";
            if (!_deviceIcons.TryGetValue(d.Key, out var entry))
            {
                var ni = new NotifyIcon { ContextMenuStrip = _main.ContextMenuStrip };
                ni.MouseClick += (_, e) => { if (e.Button == MouseButtons.Left) TogglePanelRequested?.Invoke(this, EventArgs.Empty); };
                entry = (ni, IntPtr.Zero, "");
            }
            if (entry.sig != dsig)
            {
                var old = entry.handle;
                entry.icon.Icon = TrayIconRenderer.RenderDevice(d, light, out var h);
                if (old != IntPtr.Zero) Native.DestroyIcon(old);
                entry = (entry.icon, h, dsig);
            }
            entry.icon.Text = Truncate($"{d.DisplayName}\n{d.LevelText} · {d.StatusText}", 127);
            entry.icon.Visible = true;
            _deviceIcons[d.Key] = entry;
        }

        if (_settings.PromoteTrayIcons) SchedulePromote();
    }

    private System.Windows.Forms.Timer? _promoteTimer;
    private int _promoteRuns;

    /// <summary>
    /// O Windows cria a entrada de cada ícone no registro alguns instantes depois de ele aparecer;
    /// por isso tentamos promover algumas vezes.
    /// </summary>
    public void SchedulePromote()
    {
        _promoteRuns = 0;
        if (_promoteTimer != null) { _promoteTimer.Stop(); _promoteTimer.Start(); return; }
        _promoteTimer = new System.Windows.Forms.Timer { Interval = 1500 };
        _promoteTimer.Tick += (_, _) =>
        {
            TrayPromoter.PromoteOwnIcons();
            if (++_promoteRuns >= 4) _promoteTimer.Stop();
        };
        _promoteTimer.Start();
    }

    private static string BuildTooltip(List<BatteryDevice> devices)
    {
        var sb = new StringBuilder();
        var list = devices.Where(d => d.IsConnected).ToList();
        if (list.Count == 0) return "Monitor de Bateria\nNenhum dispositivo conectado";
        foreach (var d in list)
        {
            var line = $"{Short(d.DisplayName)}: {d.LevelText}{(d.IsCharging ? " ⚡" : "")}";
            if (sb.Length + line.Length + 1 > 127) break;
            if (sb.Length > 0) sb.Append('\n');
            sb.Append(line);
        }
        return sb.ToString();
    }

    private static string Short(string s) => s.Length > 22 ? s[..21] + "…" : s;
    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..(max - 1)] + "…";

    public void Dispose()
    {
        foreach (var (icon, handle, _) in _deviceIcons.Values)
        {
            icon.Visible = false;
            icon.Dispose();
            if (handle != IntPtr.Zero) Native.DestroyIcon(handle);
        }
        _deviceIcons.Clear();
        _main.Visible = false;
        _main.Dispose();
        if (_mainHandle != IntPtr.Zero) Native.DestroyIcon(_mainHandle);
    }

    private sealed class MenuColors : ProfessionalColorTable
    {
        private readonly bool _dark;
        public MenuColors(bool dark) { _dark = dark; UseSystemColors = false; }
        private Color Bg => _dark ? Color.FromArgb(44, 44, 44) : Color.FromArgb(249, 249, 249);
        private Color Hover => _dark ? Color.FromArgb(61, 61, 61) : Color.FromArgb(230, 230, 230);
        private Color Line => _dark ? Color.FromArgb(70, 70, 70) : Color.FromArgb(220, 220, 220);
        public override Color ToolStripDropDownBackground => Bg;
        public override Color MenuBorder => Line;
        public override Color MenuItemBorder => Hover;
        public override Color MenuItemSelected => Hover;
        public override Color MenuItemSelectedGradientBegin => Hover;
        public override Color MenuItemSelectedGradientEnd => Hover;
        public override Color SeparatorDark => Line;
        public override Color SeparatorLight => Bg;
        public override Color ImageMarginGradientBegin => Bg;
        public override Color ImageMarginGradientMiddle => Bg;
        public override Color ImageMarginGradientEnd => Bg;
        public override Color CheckBackground => Hover;
        public override Color CheckSelectedBackground => Hover;
        public override Color CheckPressedBackground => Hover;
    }
}
