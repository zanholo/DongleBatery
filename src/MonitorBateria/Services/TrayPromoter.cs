using System;
using Microsoft.Win32;

namespace MonitorBateria.Services;

/// <summary>
/// O Windows 11 esconde ícones novos da bandeja no menu "^". A visibilidade de cada ícone fica em
/// HKCU\Control Panel\NotifyIconSettings\&lt;id&gt; (IsPromoted = 1 → aparece na barra).
/// Aqui marcamos como visíveis apenas os ícones deste executável.
/// </summary>
public static class TrayPromoter
{
    private const string Root = @"Control Panel\NotifyIconSettings";

    /// <returns>Quantos ícones foram alterados.</returns>
    public static int PromoteOwnIcons()
    {
        int changed = 0;
        try
        {
            var exe = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exe)) return 0;
            using var root = Registry.CurrentUser.OpenSubKey(Root);
            if (root is null) return 0;
            foreach (var name in root.GetSubKeyNames())
            {
                using var k = Registry.CurrentUser.OpenSubKey($@"{Root}\{name}", writable: true);
                if (k?.GetValue("ExecutablePath") is not string path) continue;
                if (!string.Equals(path, exe, StringComparison.OrdinalIgnoreCase)) continue;
                if (k.GetValue("IsPromoted") is int v && v == 1) continue;
                k.SetValue("IsPromoted", 1, RegistryValueKind.DWord);
                changed++;
            }
        }
        catch { }
        return changed;
    }
}
