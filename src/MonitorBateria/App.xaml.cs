using System;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Media;
using Microsoft.Win32;
using MonitorBateria.Services;
using MonitorBateria.UI;

namespace MonitorBateria;

public partial class App : Application
{
    private const string MutexName = "MonitorBateria.SingleInstance.v1";
    private const string ShowEventName = "MonitorBateria.ShowPanel.v1";

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool SetProcessDpiAwarenessContext(IntPtr value);

    static App()
    {
        // Per-Monitor V2: coordenadas físicas reais em cada monitor (necessário para encaixar o painel
        // corretamente em monitores com escalas diferentes).
        try { SetProcessDpiAwarenessContext(new IntPtr(-4)); } catch { }
    }

    private Mutex? _mutex;
    private EventWaitHandle? _showEvent;
    private RegisteredWaitHandle? _showWait;

    private AppSettings _settings = null!;
    private DeviceMonitor _monitor = null!;
    private TrayManager _tray = null!;
    private SidePanel _panel = null!;
    private FloatingWidget _widget = null!;

    protected override async void OnStartup(StartupEventArgs e)
    {
        // Diagnóstico: MonitorBateria.exe --dump [arquivo]  → grava o que foi encontrado e sai.
        int dumpIdx = Array.FindIndex(e.Args, a => a.Equals("--dump", StringComparison.OrdinalIgnoreCase));
        if (dumpIdx >= 0)
        {
            var path = dumpIdx + 1 < e.Args.Length ? e.Args[dumpIdx + 1]
                : System.IO.Path.Combine(AppSettings.Folder, "diagnostico.txt");
            await Diagnostics.DumpAsync(path);
            Shutdown();
            return;
        }

        _mutex = new Mutex(true, MutexName, out bool first);
        if (!first)
        {
            // Já está rodando: pede para a instância existente abrir o painel.
            try { EventWaitHandle.OpenExisting(ShowEventName).Set(); } catch { }
            Shutdown();
            return;
        }

        base.OnStartup(e);
        DispatcherUnhandledException += (_, ex) => { ex.Handled = true; System.Diagnostics.Debug.WriteLine(ex.Exception); };

        ApplyTheme();
        SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;

        _settings = AppSettings.Load();
        StartupManager.Refresh();

        _monitor = new DeviceMonitor(_settings);
        _panel = new SidePanel(_monitor, _settings);
        _tray = new TrayManager(_monitor, _settings);

        _tray.TogglePanelRequested += (_, _) => _panel.Toggle();
        _tray.OpenSettingsRequested += (_, _) => _panel.ShowPanel(settings: true);
        _tray.ExitRequested += (_, _) => ExitApp();

        _widget = new FloatingWidget(_monitor, _settings);
        _widget.OpenPanelRequested += (_, _) => _panel.ShowPanel();
        _tray.ToggleWidgetRequested += (_, on) => { _settings.ShowWidget = on; _settings.Save(); _widget.ApplySettings(); };
        if (_settings.ShowWidget) _widget.Show();
        _monitor.Updated += (_, _) => _tray.Update();
        _monitor.LowBattery += (_, d) => _tray.ShowLowBattery(d);
        _panel.SettingsChanged += (_, _) => { _monitor.ApplyInterval(); _tray.Update(); _widget?.ApplySettings(); };
        _panel.ExitRequested += (_, _) => ExitApp();

        _showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);
        _showWait = ThreadPool.RegisterWaitForSingleObject(_showEvent,
            (_, _) => Dispatcher.BeginInvoke(() => _panel.ShowPanel()), null, Timeout.Infinite, false);

        _monitor.Start();

        bool startHidden = e.Args.Any(a => a.Equals("--tray", StringComparison.OrdinalIgnoreCase));
        bool openSettings = e.Args.Any(a => a.Equals("--settings", StringComparison.OrdinalIgnoreCase));
        if (openSettings) _panel.ShowPanel(settings: true);
        else if (!startHidden) _panel.ShowPanel();
    }

    private void OnUserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
    {
        if (e.Category is UserPreferenceCategory.General or UserPreferenceCategory.Color or UserPreferenceCategory.VisualStyle)
            Dispatcher.BeginInvoke(() => { ApplyTheme(); _tray?.Update(); });
    }

    private void ApplyTheme()
    {
        bool light = ThemeHelper.AppsLight;
        void Set(string key, string hex) => Resources[key] = Freeze(new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex)));

        if (light)
        {
            Set("Level.ok", "#0F7B0F");
            Set("Level.warn", "#9D5D00");
            Set("Level.crit", "#C42B1C");
            Set("Level.charge", "#005FB8");
            Set("Level.none", "#8A8A8A");
            Set("Panel.Background", "#FFF3F3F3");
            Set("Panel.Card", "#FFFBFBFB");
            Set("Panel.CardHover", "#0F000000");
            Set("Panel.Stroke", "#14000000");
            Set("Panel.Text", "#E4000000");
            Set("Panel.SubText", "#9E000000");
            Set("Panel.Track", "#1F000000");
        }
        else
        {
            Set("Level.ok", "#6CCB5F");
            Set("Level.warn", "#FCB900");
            Set("Level.crit", "#FF6363");
            Set("Level.charge", "#4CC2FF");
            Set("Level.none", "#8A8A8A");
            Set("Panel.Background", "#FF202020");
            Set("Panel.Card", "#FF2B2B2B");
            Set("Panel.CardHover", "#17FFFFFF");
            Set("Panel.Stroke", "#1AFFFFFF");
            Set("Panel.Text", "#FFFFFFFF");
            Set("Panel.SubText", "#C5FFFFFF");
            Set("Panel.Track", "#26FFFFFF");
        }
    }

    private static SolidColorBrush Freeze(SolidColorBrush b) { b.Freeze(); return b; }

    private void ExitApp()
    {
        SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
        _settings.Save();
        _tray.Dispose();
        _showWait?.Unregister(null);
        _showEvent?.Dispose();
        _mutex?.ReleaseMutex();
        Shutdown();
    }
}
