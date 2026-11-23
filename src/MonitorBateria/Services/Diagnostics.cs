using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace MonitorBateria.Services;

/// <summary>Gera um relatório texto do que foi encontrado (MonitorBateria.exe --dump).</summary>
public static class Diagnostics
{
    public static async Task DumpAsync(string path)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Monitor de Bateria — diagnóstico {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        try
        {
            var settings = new AppSettings();
            var monitor = new DeviceMonitor(settings);
            monitor.Logitech.Verbose = true;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            await monitor.Bluetooth.WaitEnumeratedAsync(TimeSpan.FromSeconds(10));
            sb.AppendLine($"Watcher pronto em {sw.ElapsedMilliseconds} ms");
            sw.Restart();
            await monitor.RefreshAsync();
            sb.AppendLine($"Leitura em {sw.ElapsedMilliseconds} ms, {monitor.Devices.Count} dispositivo(s)");
            sb.AppendLine();
            foreach (var d in monitor.Devices)
            {
                sb.AppendLine($"[{d.Key}]");
                sb.AppendLine($"  Nome:      {d.Name}");
                sb.AppendLine($"  Tipo:      {d.KindLabel} ({d.Kind})");
                sb.AppendLine($"  Marca:     {d.Brand}   Modelo: {d.Model}");
                sb.AppendLine($"  Origem:    {d.Source}");
                sb.AppendLine($"  Bateria:   {d.LevelText}   Partes: {string.Join(", ", d.Parts.Select(p => p.Text))}");
                sb.AppendLine($"  Conectado: {d.IsConnected}   Estado: {d.State}   Status: {d.StatusText}");
            }
            // Amostras dos ícones da bandeja ao lado do relatório
            var dir = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(path))!, "icones");
            Directory.CreateDirectory(dir);
            foreach (var d in monitor.Devices)
            {
                var ico = TrayIconRenderer.RenderDevice(d, ThemeHelper.TaskbarLight, out var h);
                using (var bmp = ico.ToBitmap()) bmp.Save(Path.Combine(dir, d.Key.Replace(':', '_') + ".png"));
                Native.DestroyIcon(h);
            }
            foreach (var (lvl, chg) in new (int?, bool)[] { (85, false), (25, false), (10, false), (60, true), (null, false) })
            {
                var ico = TrayIconRenderer.RenderMain(lvl, chg, ThemeHelper.TaskbarLight, out var h);
                using (var bmp = ico.ToBitmap()) bmp.Save(Path.Combine(dir, $"main_{lvl}_{chg}.png"));
                Native.DestroyIcon(h);
            }
            sb.AppendLine();
            sb.AppendLine("Endpoints Bluetooth pareados:");
            foreach (var ep in monitor.Bluetooth.Endpoints)
                sb.AppendLine($"  {(ep.IsLe ? "LE " : "BR ")} conectado={ep.Connected,-5} {ep.Name}  container={ep.Container}  cat=[{string.Join(",", ep.Categories)}] cod={ep.Cod} fab={ep.Manufacturer} mod={ep.Model}");
            sb.AppendLine("  tempos: " + BluetoothProvider.Timings);
            sb.AppendLine();
            sb.AppendLine($"USB presentes: 046D={await WiredDetector.CountUsbAsync("046D")} 3434={await WiredDetector.CountUsbAsync("3434")}");
            sb.AppendLine("Interfaces HID Logitech:");
            foreach (var l in Logitech.LogitechProvider.AllInterfaces) sb.AppendLine("  " + l);
            sb.AppendLine("Logitech (HID++):");
            foreach (var l in monitor.Logitech.DebugLog) sb.AppendLine("  " + l);
            if (BluetoothProvider.LastError != null) sb.AppendLine("  erro: " + BluetoothProvider.LastError);
        }
        catch (Exception ex)
        {
            sb.AppendLine("ERRO: " + ex);
        }
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        await File.WriteAllTextAsync(path, sb.ToString(), Encoding.UTF8);
    }
}
