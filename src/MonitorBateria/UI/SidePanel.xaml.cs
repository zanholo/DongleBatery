using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Interop;
using System.Windows.Media.Animation;
using MonitorBateria.Models;
using MonitorBateria.Services;
using Forms = System.Windows.Forms;

namespace MonitorBateria.UI;

public partial class SidePanel : Window
{
    private const double PanelWidth = 384;

    private readonly DeviceMonitor _monitor;
    private readonly AppSettings _settings;
    private readonly System.Collections.ObjectModel.ObservableCollection<BatteryDevice> _visibleItems = new();
    private readonly System.Collections.ObjectModel.ObservableCollection<BatteryDevice> _hiddenItems = new();

    private bool _pinned;
    private bool _isClosing;
    private bool _loadingSettings = true; // vira false ao final do construtor
    private int _suppressAutoHide;
    private DateTime _lastAutoHide = DateTime.MinValue;

    public event EventHandler? SettingsChanged;
    public event EventHandler? ExitRequested;

    private sealed record Choice(string Label, string Key);

    public SidePanel(DeviceMonitor monitor, AppSettings settings)
    {
        _monitor = monitor;
        _settings = settings;
        InitializeComponent();

        DeviceList.ItemsSource = _visibleItems;
        HiddenList.ItemsSource = _hiddenItems;
        AllDevicesList.ItemsSource = _monitor.Devices;

        _monitor.Updated += (_, _) => UpdateHeader();
        Deactivated += (_, _) => OnDeactivated();
        DpiChanged += (_, _) => Dispatcher.BeginInvoke(PositionWindow);
        Microsoft.Win32.SystemEvents.DisplaySettingsChanged += (_, _) => Dispatcher.BeginInvoke(() => { if (IsVisible) PositionWindow(); });

        UpdateHeader();
        _loadingSettings = false;
    }

    // ------------------------------------------------------------------ exibição

    public void Toggle()
    {
        if (IsVisible && !_isClosing) { HidePanel(); return; }
        // Clique no ícone da bandeja faz o painel perder o foco antes do clique chegar.
        if ((DateTime.Now - _lastAutoHide).TotalMilliseconds < 400) return;
        ShowPanel();
    }

    public void ShowPanel(bool settings = false)
    {
        ShowPage(settings);
        _isClosing = false;

        var hwnd = new WindowInteropHelper(this).EnsureHandle();
        PositionWindow();
        if (!IsVisible) Show();
        PositionWindow();
        Activate();
        Native.SetForegroundWindow(hwnd);

        Sheet.BorderThickness = _settings.Side == PanelSide.Right ? new Thickness(1, 0, 0, 0) : new Thickness(0, 0, 1, 0);
        // Animação leve do conteúdo (a janela em si é opaca: janelas "layered" do WPF
        // não se comportam bem ao serem movidas entre monitores).
        double from = (_settings.Side == PanelSide.Right ? 1 : -1) * 48;
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        Slide.BeginAnimation(System.Windows.Media.TranslateTransform.XProperty,
            new DoubleAnimation(from, 0, TimeSpan.FromMilliseconds(220)) { EasingFunction = ease });
        Body.BeginAnimation(OpacityProperty,
            new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(180)) { EasingFunction = ease });
        UpdateHeader();
    }

    public void HidePanel()
    {
        if (!IsVisible || _isClosing) return;
        _isClosing = true;
        double to = (_settings.Side == PanelSide.Right ? 1 : -1) * 48;
        var ease = new CubicEase { EasingMode = EasingMode.EaseIn };
        var anim = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(120)) { EasingFunction = ease };
        anim.Completed += (_, _) =>
        {
            if (!_isClosing) return;
            Hide();
            _isClosing = false;
            Slide.BeginAnimation(System.Windows.Media.TranslateTransform.XProperty, null);
            Body.BeginAnimation(OpacityProperty, null);
            ShowPage(false);
        };
        Slide.BeginAnimation(System.Windows.Media.TranslateTransform.XProperty,
            new DoubleAnimation(0, to, TimeSpan.FromMilliseconds(120)) { EasingFunction = ease });
        Body.BeginAnimation(OpacityProperty, anim);
    }

    private void OnDeactivated()
    {
        if (_pinned || !_settings.AutoHidePanel || _suppressAutoHide > 0 || !IsVisible) return;
        _lastAutoHide = DateTime.Now;
        HidePanel();
    }

    private Forms.Screen ResolveScreen()
    {
        var screens = Forms.Screen.AllScreens;
        if (_settings.Monitor == "*cursor")
            return Forms.Screen.FromPoint(Forms.Cursor.Position);
        var s = screens.FirstOrDefault(x => x.DeviceName == _settings.Monitor);
        return s ?? Forms.Screen.PrimaryScreen ?? screens[0];
    }

    /// <summary>Encaixa a janela na lateral do monitor escolhido (coordenadas físicas, ciente de DPI).</summary>
    private void PositionWindow()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero) return;

        var wa = ResolveScreen().WorkingArea;
        uint dpi = Native.DpiForPoint(wa.Left + wa.Width / 2, wa.Top + wa.Height / 2);
        int w = (int)Math.Round(PanelWidth * dpi / 96.0);
        int x = _settings.Side == PanelSide.Right ? wa.Right - w : wa.Left;

        // Duas vezes: a primeira pode trocar o DPI da janela (monitor diferente) e o WPF reajusta o tamanho.
        for (int i = 0; i < 2; i++)
            Native.SetWindowPos(hwnd, Native.HWND_TOPMOST, x, wa.Top, w, wa.Height, Native.SWP_NOACTIVATE);
    }

    // ------------------------------------------------------------------ cabeçalho / páginas

    private void SyncLists()
    {
        ListSync.Sync(_visibleItems, _monitor.Devices.Where(d => !d.IsHidden && (_settings.ShowDisconnected || d.IsConnected)));
        ListSync.Sync(_hiddenItems, _monitor.Devices.Where(d => d.IsHidden));
    }

    private void UpdateHeader()
    {
        SyncLists();
        int connected = _monitor.Devices.Count(d => d.IsConnected && !d.IsHidden);
        int hidden = _monitor.Devices.Count(d => d.IsHidden);

        if (SettingsPage.Visibility != Visibility.Visible)
        {
            string when = _monitor.LastRefresh == default ? "Procurando dispositivos…" : $"Atualizado às {_monitor.LastRefresh:HH:mm:ss}";
            SubtitleText.Text = $"{connected} conectado{(connected == 1 ? "" : "s")} · {when}";
        }

        HiddenExpander.Visibility = hidden > 0 ? Visibility.Visible : Visibility.Collapsed;
        HiddenExpander.Header = $"Ocultos ({hidden})";
        EmptyState.Visibility = _monitor.LastRefresh != default && _visibleItems.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        if (SettingsPage.Visibility == Visibility.Visible) RefreshMainTrayChoices();
    }

    private void ShowPage(bool settings)
    {
        DevicesPage.Visibility = settings ? Visibility.Collapsed : Visibility.Visible;
        SettingsPage.Visibility = settings ? Visibility.Visible : Visibility.Collapsed;
        TitleText.Text = settings ? "Configurações" : "Dispositivos";
        SettingsButton.Content = settings ? "" : "";
        SettingsButton.ToolTip = settings ? "Voltar" : "Configurações";
        FooterText.Visibility = settings ? Visibility.Collapsed : Visibility.Visible;
        if (settings)
        {
            SubtitleText.Text = "Posição, alertas e dispositivos";
            LoadSettings();
        }
        else UpdateHeader();
    }

    private void LoadSettings()
    {
        _loadingSettings = true;
        try
        {
            MonitorCombo.Items.Clear();
            MonitorCombo.Items.Add(new ComboBoxItem { Content = "Monitor principal", Tag = "" });
            MonitorCombo.Items.Add(new ComboBoxItem { Content = "Monitor onde está o mouse", Tag = "*cursor" });
            foreach (var s in Forms.Screen.AllScreens.OrderBy(s => ScreenNumber(s)))
            {
                var label = $"Monitor {ScreenNumber(s)} — {s.Bounds.Width}×{s.Bounds.Height}{(s.Primary ? " (principal)" : "")}";
                MonitorCombo.Items.Add(new ComboBoxItem { Content = label, Tag = s.DeviceName });
            }
            MonitorCombo.SelectedItem = MonitorCombo.Items.Cast<ComboBoxItem>().FirstOrDefault(i => (string)i.Tag == _settings.Monitor)
                                        ?? MonitorCombo.Items[0];

            SideRight.IsChecked = _settings.Side == PanelSide.Right;
            SideLeft.IsChecked = _settings.Side == PanelSide.Left;
            AutoHideCheck.IsChecked = _settings.AutoHidePanel;
            ShowDisconnectedCheck.IsChecked = _settings.ShowDisconnected;
            StartupCheck.IsChecked = StartupManager.IsEnabled;
            AlertCheck.IsChecked = _settings.LowBatteryAlert;
            PromoteCheck.IsChecked = _settings.PromoteTrayIcons;
            WidgetCheck.IsChecked = _settings.ShowWidget;
            ThresholdSlider.Value = _settings.LowBatteryThreshold;
            ThresholdText.Text = $"{_settings.LowBatteryThreshold}%";

            IntervalCombo.SelectedItem = IntervalCombo.Items.Cast<ComboBoxItem>()
                .OrderBy(i => Math.Abs(int.Parse((string)i.Tag) - _settings.RefreshSeconds)).First();

            RefreshMainTrayChoices();
        }
        finally { _loadingSettings = false; }
    }

    private void RefreshMainTrayChoices()
    {
        var was = _loadingSettings;
        _loadingSettings = true;
        try
        {
            var items = new List<Choice> { new("Dispositivo com menor bateria", "") };
            items.AddRange(_monitor.Devices.Where(d => !d.IsHidden).Select(d => new Choice($"{d.DisplayName} ({d.KindLabel})", d.Key)));
            if (MainTrayCombo.ItemsSource is List<Choice> cur && cur.Select(c => c.Key + c.Label).SequenceEqual(items.Select(c => c.Key + c.Label)))
                return;
            MainTrayCombo.ItemsSource = items;
            MainTrayCombo.SelectedItem = items.FirstOrDefault(i => i.Key == _settings.MainTrayDevice) ?? items[0];
        }
        finally { _loadingSettings = was; }
    }

    private static int ScreenNumber(Forms.Screen s)
    {
        var digits = new string(s.DeviceName.Where(char.IsDigit).ToArray());
        return int.TryParse(digits, out var n) ? n : 0;
    }

    // ------------------------------------------------------------------ ações do cabeçalho

    private async void Refresh_Click(object sender, RoutedEventArgs e)
    {
        RefreshButton.IsEnabled = false;
        SubtitleText.Text = "Atualizando…";
        await _monitor.RefreshAsync();
        RefreshButton.IsEnabled = true;
        UpdateHeader();
    }

    private void Settings_Click(object sender, RoutedEventArgs e)
        => ShowPage(SettingsPage.Visibility != Visibility.Visible);

    private void Pin_Click(object sender, RoutedEventArgs e) => _pinned = PinButton.IsChecked == true;

    private void Close_Click(object sender, RoutedEventArgs e) => HidePanel();

    private void Exit_Click(object sender, RoutedEventArgs e) => ExitRequested?.Invoke(this, EventArgs.Empty);

    // ------------------------------------------------------------------ ações por dispositivo

    private static BatteryDevice? DeviceOf(object sender) => (sender as FrameworkElement)?.DataContext as BatteryDevice;

    private void TrayToggle_Click(object sender, RoutedEventArgs e)
    {
        if (DeviceOf(sender) is not { } d) return;
        d.ShowInTray = !d.ShowInTray;
        if (sender is ToggleButton tb) tb.IsChecked = d.ShowInTray;
        if (sender is MenuItem mi) mi.IsChecked = d.ShowInTray;
        _monitor.SavePrefs(d);
    }

    private void MainTray_Click(object sender, RoutedEventArgs e)
    {
        if (DeviceOf(sender) is not { } d) return;
        _settings.MainTrayDevice = _settings.MainTrayDevice == d.Key ? "" : d.Key;
        foreach (var x in _monitor.Devices) x.IsMainTray = x.Key == _settings.MainTrayDevice;
        _settings.Save();
        SettingsChanged?.Invoke(this, EventArgs.Empty);
    }

    private void Hide_Click(object sender, RoutedEventArgs e)
    {
        if (DeviceOf(sender) is not { } d) return;
        d.IsHidden = true;
        _monitor.SavePrefs(d);
        UpdateHeader();
    }

    private void Unhide_Click(object sender, RoutedEventArgs e)
    {
        if (DeviceOf(sender) is not { } d) return;
        d.IsHidden = false;
        _monitor.SavePrefs(d);
        UpdateHeader();
    }

    private void VisibleCheck_Click(object sender, RoutedEventArgs e)
    {
        if (DeviceOf(sender) is not { } d || sender is not CheckBox cb) return;
        d.IsHidden = cb.IsChecked != true;
        _monitor.SavePrefs(d);
        UpdateHeader();
    }

    private void Rename_Click(object sender, RoutedEventArgs e)
    {
        if (DeviceOf(sender) is not { } d) return;
        _suppressAutoHide++;
        try
        {
            var dlg = new RenameDialog(d.DisplayName, d.Name) { Owner = this };
            if (dlg.ShowDialog() == true)
            {
                d.CustomName = string.IsNullOrWhiteSpace(dlg.Result) || dlg.Result == d.Name ? null : dlg.Result.Trim();
                _monitor.SavePrefs(d);
            }
        }
        finally
        {
            _suppressAutoHide--;
            Activate();
        }
    }

    // ------------------------------------------------------------------ configurações

    private void MonitorCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingSettings || MonitorCombo.SelectedItem is not ComboBoxItem item) return;
        _settings.Monitor = (string)item.Tag;
        _settings.Save();
        ReopenAtNewPosition();
    }

    private void Side_Checked(object sender, RoutedEventArgs e)
    {
        if (_loadingSettings) return;
        _settings.Side = SideLeft.IsChecked == true ? PanelSide.Left : PanelSide.Right;
        _settings.Save();
        ReopenAtNewPosition();
    }

    private void ReopenAtNewPosition()
    {
        Sheet.BorderThickness = _settings.Side == PanelSide.Right ? new Thickness(1, 0, 0, 0) : new Thickness(0, 0, 1, 0);
        _suppressAutoHide++;
        PositionWindow();
        Activate();
        Dispatcher.BeginInvoke(() => _suppressAutoHide--, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
    }

    private void Option_Changed(object sender, RoutedEventArgs e)
    {
        if (_loadingSettings) return;
        _settings.AutoHidePanel = AutoHideCheck.IsChecked == true;
        _settings.ShowDisconnected = ShowDisconnectedCheck.IsChecked == true;
        _settings.LowBatteryAlert = AlertCheck.IsChecked == true;
        _settings.PromoteTrayIcons = PromoteCheck.IsChecked == true;
        _settings.ShowWidget = WidgetCheck.IsChecked == true;
        _settings.Save();
        SyncLists();
        SettingsChanged?.Invoke(this, EventArgs.Empty);
    }

    private void Startup_Click(object sender, RoutedEventArgs e)
    {
        try { StartupManager.Set(StartupCheck.IsChecked == true); }
        catch { StartupCheck.IsChecked = StartupManager.IsEnabled; }
    }

    private void Interval_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingSettings || IntervalCombo.SelectedItem is not ComboBoxItem item) return;
        _settings.RefreshSeconds = int.Parse((string)item.Tag);
        _settings.Save();
        SettingsChanged?.Invoke(this, EventArgs.Empty);
    }

    private void MainTray_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingSettings || MainTrayCombo.SelectedItem is not Choice c) return;
        _settings.MainTrayDevice = c.Key;
        foreach (var x in _monitor.Devices) x.IsMainTray = x.Key == c.Key;
        _settings.Save();
        SettingsChanged?.Invoke(this, EventArgs.Empty);
    }

    private void Threshold_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (ThresholdText is null || !IsInitialized) return;
        ThresholdText.Text = $"{(int)ThresholdSlider.Value}%";
        if (_loadingSettings) return;
        _settings.LowBatteryThreshold = (int)ThresholdSlider.Value;
        _settings.Save();
    }

    private void Identify_Click(object sender, RoutedEventArgs e)
    {
        _suppressAutoHide++;
        foreach (var s in Forms.Screen.AllScreens)
            new IdentifyWindow(ScreenNumber(s), s).Show();
        Activate();
        Dispatcher.BeginInvoke(() => _suppressAutoHide--, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
    }
}
