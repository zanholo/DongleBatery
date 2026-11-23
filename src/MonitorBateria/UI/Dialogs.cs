using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using MonitorBateria.Services;
using Forms = System.Windows.Forms;

namespace MonitorBateria.UI;

/// <summary>Caixa simples para renomear um dispositivo.</summary>
public sealed class RenameDialog : Window
{
    private readonly TextBox _box;
    public string Result => _box.Text;

    public RenameDialog(string current, string original)
    {
        Title = "Renomear dispositivo";
        Width = 360;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        Topmost = true;

        var root = new StackPanel { Margin = new Thickness(20) };
        root.Children.Add(new TextBlock { Text = "Nome exibido", Margin = new Thickness(0, 0, 0, 6) });
        _box = new TextBox { Text = current };
        root.Children.Add(_box);
        root.Children.Add(new TextBlock
        {
            Text = $"Nome original: {original}. Deixe em branco para voltar ao original.",
            TextWrapping = TextWrapping.Wrap,
            FontSize = 12,
            Opacity = 0.7,
            Margin = new Thickness(0, 6, 0, 0),
        });

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0) };
        var ok = new Button { Content = "Salvar", IsDefault = true, MinWidth = 90, Margin = new Thickness(0, 0, 8, 0) };
        ok.Click += (_, _) => DialogResult = true;
        var cancel = new Button { Content = "Cancelar", IsCancel = true, MinWidth = 90 };
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);
        root.Children.Add(buttons);
        Content = root;

        Loaded += (_, _) => { _box.Focus(); _box.SelectAll(); };
    }
}

/// <summary>Mostra o número do monitor no centro de cada tela por alguns segundos.</summary>
public sealed class IdentifyWindow : Window
{
    private readonly Forms.Screen _screen;

    public IdentifyWindow(int number, Forms.Screen screen)
    {
        _screen = screen;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        Background = new SolidColorBrush(Color.FromRgb(0x1F, 0x1F, 0x1F));
        ShowInTaskbar = false;
        Topmost = true;
        ShowActivated = false;
        Width = 220;
        Height = 220;

        Content = new Border
        {
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x4C, 0xC2, 0xFF)),
            BorderThickness = new Thickness(3),
            Child = new StackPanel
            {
                VerticalAlignment = VerticalAlignment.Center,
                Children =
                {
                    new TextBlock { Text = number.ToString(), FontSize = 110, FontWeight = FontWeights.SemiBold, Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center },
                    new TextBlock { Text = screen.Primary ? "principal" : $"{screen.Bounds.Width}×{screen.Bounds.Height}", FontSize = 14, Foreground = Brushes.LightGray, HorizontalAlignment = HorizontalAlignment.Center },
                },
            },
        };

        SourceInitialized += (_, _) => Place();
        var t = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2.5) };
        t.Tick += (_, _) => { t.Stop(); Close(); };
        Loaded += (_, _) => { Place(); t.Start(); };
    }

    private void Place()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        var b = _screen.Bounds;
        uint dpi = Native.DpiForPoint(b.Left + b.Width / 2, b.Top + b.Height / 2);
        int size = (int)(220 * dpi / 96.0);
        for (int i = 0; i < 2; i++)
            Native.SetWindowPos(hwnd, Native.HWND_TOPMOST, b.Left + (b.Width - size) / 2, b.Top + (b.Height - size) / 2, size, size, Native.SWP_NOACTIVATE);
    }
}
