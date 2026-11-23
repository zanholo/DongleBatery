using System;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using MonitorBateria.Models;
using MonitorBateria.Services;
using Forms = System.Windows.Forms;

namespace MonitorBateria.UI;

/// <summary>
/// Barrinha flutuante com o nível de cada dispositivo conectado.
/// Pode ser arrastada para qualquer lugar de qualquer monitor; a posição fica salva.
/// </summary>
public partial class FloatingWidget : Window, INotifyPropertyChanged
{
    private readonly DeviceMonitor _monitor;
    private readonly AppSettings _settings;
    private readonly System.Collections.ObjectModel.ObservableCollection<BatteryDevice> _items = new();
    private StackPanel? _host;

    public event EventHandler? OpenPanelRequested;
    public event EventHandler? HiddenByUser;
    public event PropertyChangedEventHandler? PropertyChanged;

    public bool ShowNames => _settings.WidgetShowNames;

    public FloatingWidget(DeviceMonitor monitor, AppSettings settings)
    {
        _monitor = monitor;
        _settings = settings;
        InitializeComponent();
        DataContext = this;

        Items.ItemsSource = _items;

        Topmost = _settings.WidgetTopmost;
        _monitor.Updated += (_, _) => UpdateEmpty();
        SizeChanged += (_, _) => Dispatcher.BeginInvoke(() =>
        {
            // Sem posição salva: fica ancorado no canto inferior direito. Com posição: mantém a borda
            // âncora no lugar e recentraliza na barra de tarefas.
            if (_settings.WidgetX is null) RestorePosition();
            else ReanchorAfterResize();
        }, System.Windows.Threading.DispatcherPriority.Background);
        SourceInitialized += (_, _) => RestorePosition();
        Microsoft.Win32.SystemEvents.DisplaySettingsChanged += (_, _) => Dispatcher.BeginInvoke(EnsureOnScreen);
        UpdateEmpty();
    }

    private void UpdateEmpty()
    {
        ListSync.Sync(_items, _monitor.Devices.Where(d => !d.IsHidden && d.IsConnected));
        EmptyText.Visibility = _items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ItemsHost_Loaded(object sender, RoutedEventArgs e)
    {
        _host = (StackPanel)sender;
        ApplyOrientation();
    }

    private void ApplyOrientation()
    {
        if (_host != null) _host.Orientation = _settings.WidgetVertical ? Orientation.Vertical : Orientation.Horizontal;
        Grip.Text = _settings.WidgetVertical ? "" : "";
        DockPanel.SetDock(Grip, _settings.WidgetVertical ? Dock.Top : Dock.Left);
        Grip.HorizontalAlignment = _settings.WidgetVertical ? HorizontalAlignment.Center : HorizontalAlignment.Left;
        Grip.Margin = _settings.WidgetVertical ? new Thickness(0, 0, 0, 2) : new Thickness(0, 0, 6, 0);
    }

    // ---------------------------------------------------------------- arrastar / posição

    private void Frame_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
        {
            OpenPanelRequested?.Invoke(this, EventArgs.Empty);
            return;
        }
        try { DragMove(); } catch { }
        SnapToTaskbarIfOver();
        SavePosition();
    }

    private void SavePosition()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero || !GetWindowRect(hwnd, out var r)) return;
        _settings.WidgetX = r.Left;
        _settings.WidgetY = r.Top;
        _settings.WidgetRight = r.Right;
        _settings.Save();
    }

    /// <summary>
    /// O widget mudou de tamanho (entrou/saiu dispositivo). Mantém fixa a borda mais próxima da
    /// lateral da tela: na metade direita (ex.: ao lado do relógio) cresce para a esquerda; na
    /// metade esquerda cresce para a direita. Depois recentraliza na barra de tarefas, se estiver nela.
    /// </summary>
    private void ReanchorAfterResize()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero || !GetWindowRect(hwnd, out var r)) return;
        int w = r.Right - r.Left, h = r.Bottom - r.Top;
        int savedLeft = _settings.WidgetX ?? r.Left;
        int savedRight = _settings.WidgetRight ?? savedLeft + w;
        int savedTop = _settings.WidgetY ?? r.Top;

        var screen = Forms.Screen.FromPoint(new System.Drawing.Point((savedLeft + savedRight) / 2, savedTop + h / 2));
        var b = screen.Bounds;
        bool anchorRight = (savedLeft + savedRight) / 2 > b.Left + b.Width / 2;
        int x = anchorRight ? savedRight - w : savedLeft;
        x = Math.Clamp(x, b.Left, Math.Max(b.Left, b.Right - w));
        int y = Math.Clamp(savedTop, b.Top, Math.Max(b.Top, b.Bottom - h));

        Native.SetWindowPos(hwnd, IntPtr.Zero, x, y, 0, 0, SWP_NOSIZE | Native.SWP_NOZORDER | Native.SWP_NOACTIVATE);
        SnapToTaskbarIfOver();               // se estiver na barra, centraliza nela e salva
        if (!_onTaskbar) SavePosition();
    }

    private void RestorePosition()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (_settings.WidgetX is int x && _settings.WidgetY is int y && IsVisibleSpot(x, y))
        {
            Native.SetWindowPos(hwnd, IntPtr.Zero, x, y, 0, 0, SWP_NOSIZE | Native.SWP_NOZORDER | Native.SWP_NOACTIVATE);
            Dispatcher.BeginInvoke(ReanchorAfterResize, System.Windows.Threading.DispatcherPriority.Loaded);
            return;
        }
        // Padrão: canto inferior direito do monitor principal, acima da barra de tarefas.
        SetTaskbarMode(false);
        Dispatcher.BeginInvoke(() =>
        {
            var wa = (Forms.Screen.PrimaryScreen ?? Forms.Screen.AllScreens[0]).WorkingArea;
            if (!GetWindowRect(hwnd, out var r)) return;
            int w = r.Right - r.Left, h = r.Bottom - r.Top;
            Native.SetWindowPos(hwnd, IntPtr.Zero, wa.Right - w - 16, wa.Bottom - h - 16, 0, 0, SWP_NOSIZE | Native.SWP_NOZORDER | Native.SWP_NOACTIVATE);
        }, System.Windows.Threading.DispatcherPriority.Loaded);
    }

    private void EnsureOnScreen()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero || !GetWindowRect(hwnd, out var r)) return;
        if (!IsVisibleSpot(r.Left, r.Top))
        {
            _settings.WidgetX = _settings.WidgetY = null;
            RestorePosition();
        }
    }

    /// <summary>O widget precisa cair dentro de algum monitor (inclui a área da barra de tarefas).</summary>
    private static bool IsVisibleSpot(int x, int y)
        => Forms.Screen.AllScreens.Any(s => s.Bounds.Contains(x + 20, y + 10));

    // ---------------------------------------------------------------- modo "em cima da barra de tarefas"

    private bool _onTaskbar;
    private bool _hiddenForFullscreen;
    private System.Windows.Threading.DispatcherTimer? _zTimer;

    /// <summary>Faixa da barra de tarefas (em cima ou embaixo) do monitor; vazio se não houver.</summary>
    private static System.Drawing.Rectangle TaskbarBand(Forms.Screen s)
    {
        var b = s.Bounds;
        var wa = s.WorkingArea;
        if (wa.Bottom < b.Bottom) return new System.Drawing.Rectangle(b.Left, wa.Bottom, b.Width, b.Bottom - wa.Bottom);
        if (wa.Top > b.Top) return new System.Drawing.Rectangle(b.Left, b.Top, b.Width, wa.Top - b.Top);
        return System.Drawing.Rectangle.Empty;
    }

    /// <summary>Soltou em cima da barra de tarefas: centraliza na altura da barra e usa o visual compacto.</summary>
    private void SnapToTaskbarIfOver()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero || !GetWindowRect(hwnd, out var r)) return;
        int cx = (r.Left + r.Right) / 2, cy = (r.Top + r.Bottom) / 2;
        var screen = Forms.Screen.FromPoint(new System.Drawing.Point(cx, cy));
        var band = TaskbarBand(screen);
        bool over = !band.IsEmpty && cy >= band.Top - 8 && cy <= band.Bottom + 8;
        SetTaskbarMode(over);
        if (!over) return;

        // Depois do novo layout (compacto), centraliza verticalmente na barra.
        Dispatcher.BeginInvoke(() =>
        {
            if (!GetWindowRect(hwnd, out var rr)) return;
            int h = rr.Bottom - rr.Top, w = rr.Right - rr.Left;
            int y = band.Top + Math.Max(0, (band.Height - h) / 2);
            int x = Math.Clamp(rr.Left, band.Left, Math.Max(band.Left, band.Right - w));
            Native.SetWindowPos(hwnd, Native.HWND_TOPMOST, x, y, 0, 0, SWP_NOSIZE | Native.SWP_NOACTIVATE);
            SavePosition();
        }, System.Windows.Threading.DispatcherPriority.Background);
    }

    private void SetTaskbarMode(bool on)
    {
        _onTaskbar = on;
        if (on)
        {
            // Visual que se mistura com a barra: sem sombra, sem borda e fundo quase transparente
            // (alfa mínimo para continuar recebendo cliques e arrastos).
            Frame.Margin = new Thickness(0);
            Frame.Effect = null;
            Frame.BorderThickness = new Thickness(0);
            Frame.Padding = new Thickness(4, 0, 6, 0);
            Frame.Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(1, 0, 0, 0));
            Topmost = true;
            _zTimer ??= CreateZTimer();
            _zTimer.Start();
        }
        else
        {
            Frame.Margin = new Thickness(6);
            Frame.Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 12, ShadowDepth = 2, Opacity = 0.35 };
            Frame.BorderThickness = new Thickness(1);
            Frame.Padding = new Thickness(6, 5, 10, 5);
            Frame.SetResourceReference(Border.BackgroundProperty, "Panel.Background");
            Topmost = _settings.WidgetTopmost;
            _zTimer?.Stop();
            if (_hiddenForFullscreen && _settings.ShowWidget) { _hiddenForFullscreen = false; Show(); }
        }
    }

    /// <summary>
    /// A barra de tarefas também fica "sempre por cima" e volta para a frente quando clicada;
    /// então reafirmamos a posição de tempos em tempos. O widget some enquanto um app/jogo
    /// estiver em tela cheia naquele monitor.
    /// </summary>
    private System.Windows.Threading.DispatcherTimer CreateZTimer()
    {
        var t = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(800) };
        t.Tick += (_, _) =>
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd == IntPtr.Zero || !_onTaskbar || !_settings.ShowWidget) return;

            bool fullscreen = IsForegroundFullscreen(hwnd);
            if (fullscreen && IsVisible) { _hiddenForFullscreen = true; Hide(); return; }
            if (!fullscreen && _hiddenForFullscreen) { _hiddenForFullscreen = false; Show(); }

            Native.SetWindowPos(hwnd, Native.HWND_TOPMOST, 0, 0, 0, 0, SWP_NOSIZE | SWP_NOMOVE | Native.SWP_NOACTIVATE);
        };
        return t;
    }

    private static bool IsForegroundFullscreen(IntPtr self)
    {
        var fg = GetForegroundWindow();
        if (fg == IntPtr.Zero || fg == self || fg == GetShellWindow() || fg == GetDesktopWindow()) return false;
        var cls = new System.Text.StringBuilder(64);
        GetClassName(fg, cls, cls.Capacity);
        if (cls.ToString() is "WorkerW" or "Progman" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd") return false;
        if (!GetWindowRect(fg, out var r) || !GetWindowRect(self, out var me)) return false;
        var screen = Forms.Screen.FromHandle(fg).Bounds;
        bool sameMonitor = screen.Contains((me.Left + me.Right) / 2, (me.Top + me.Bottom) / 2);
        return sameMonitor && r.Left <= screen.Left && r.Top <= screen.Top && r.Right >= screen.Right && r.Bottom >= screen.Bottom;
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern IntPtr GetShellWindow();
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern IntPtr GetDesktopWindow();
    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hWnd, System.Text.StringBuilder name, int max);
    private const uint SWP_NOMOVE = 0x0002;

    // ---------------------------------------------------------------- menu

    private void OpenPanel_Click(object sender, RoutedEventArgs e) => OpenPanelRequested?.Invoke(this, EventArgs.Empty);

    private void Vertical_Click(object sender, RoutedEventArgs e)
    {
        _settings.WidgetVertical = VerticalItem.IsChecked;
        _settings.Save();
        ApplyOrientation();
    }

    private void Names_Click(object sender, RoutedEventArgs e)
    {
        _settings.WidgetShowNames = NamesItem.IsChecked;
        _settings.Save();
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ShowNames)));
    }

    private void Topmost_Click(object sender, RoutedEventArgs e)
    {
        _settings.WidgetTopmost = TopmostItem.IsChecked;
        Topmost = _settings.WidgetTopmost || _onTaskbar;
        _settings.Save();
    }

    private void ResetPosition_Click(object sender, RoutedEventArgs e)
    {
        _settings.WidgetX = _settings.WidgetY = null;
        _settings.Save();
        RestorePosition();
    }

    private void HideWidget_Click(object sender, RoutedEventArgs e)
    {
        _settings.ShowWidget = false;
        _settings.Save();
        Hide();
        HiddenByUser?.Invoke(this, EventArgs.Empty);
    }

    protected override void OnContextMenuOpening(ContextMenuEventArgs e)
    {
        VerticalItem.IsChecked = _settings.WidgetVertical;
        NamesItem.IsChecked = _settings.WidgetShowNames;
        TopmostItem.IsChecked = _settings.WidgetTopmost;
        base.OnContextMenuOpening(e);
    }

    public void ApplySettings()
    {
        Topmost = _settings.WidgetTopmost || _onTaskbar;
        ApplyOrientation();
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ShowNames)));
        if (_settings.ShowWidget && !IsVisible) Show();
        else if (!_settings.ShowWidget && IsVisible) Hide();
    }

    private const uint SWP_NOSIZE = 0x0001;

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out Native.RECT rect);
}
