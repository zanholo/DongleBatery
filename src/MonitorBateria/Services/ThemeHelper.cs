using Microsoft.Win32;

namespace MonitorBateria.Services;

public static class ThemeHelper
{
    private const string Key = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";

    /// <summary>Tema dos aplicativos (painel).</summary>
    public static bool AppsLight => Read("AppsUseLightTheme", 0) == 1;

    /// <summary>Tema da barra de tarefas (ícones da bandeja).</summary>
    public static bool TaskbarLight => Read("SystemUsesLightTheme", 0) == 1;

    private static int Read(string name, int def)
    {
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(Key);
            return k?.GetValue(name) is int v ? v : def;
        }
        catch { return def; }
    }
}
