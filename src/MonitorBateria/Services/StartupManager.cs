using System;
using Microsoft.Win32;

namespace MonitorBateria.Services;

/// <summary>Inicializar com o Windows (HKCU\...\Run).</summary>
public static class StartupManager
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "MonitorBateria";

    private static string Command => $"\"{Environment.ProcessPath}\" --tray";

    public static bool IsEnabled
    {
        get
        {
            using var k = Registry.CurrentUser.OpenSubKey(RunKey);
            return k?.GetValue(ValueName) is string s && s.Length > 0;
        }
    }

    public static void Set(bool enabled)
    {
        using var k = Registry.CurrentUser.CreateSubKey(RunKey);
        if (enabled) k.SetValue(ValueName, Command);
        else k.DeleteValue(ValueName, false);
    }

    /// <summary>Se o exe mudou de lugar, atualiza o caminho registrado.</summary>
    public static void Refresh()
    {
        if (IsEnabled) Set(true);
    }
}
