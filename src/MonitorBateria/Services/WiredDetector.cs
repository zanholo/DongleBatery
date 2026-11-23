using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Windows.Devices.Enumeration;

namespace MonitorBateria.Services;

/// <summary>
/// Detecta teclados/dispositivos que não informam bateria pelo Bluetooth, mas aparecem no USB quando
/// estão no cabo (ex.: Keychron no modo cabo = ligado e carregando).
/// </summary>
public static class WiredDetector
{
    // VID USB → marca (como o DeviceClassifier a identifica)
    private static readonly Dictionary<string, string> Vendors = new(StringComparer.OrdinalIgnoreCase)
    {
        ["3434"] = "Keychron",
        ["19F5"] = "NuPhy",
    };

    public static async Task<int> CountUsbAsync(string vid)
    {
        var aqs = "System.Devices.Present:=System.StructuredQueryType.Boolean#True AND " +
                  $"System.Devices.DeviceInstanceId:~<\"USB\\VID_{vid}\"";
        var list = await DeviceInformation.FindAllAsync(aqs, Array.Empty<string>(), DeviceInformationKind.Device).AsTask().ConfigureAwait(false);
        return list.Count;
    }

    /// <summary>Marcas que estão presentes no USB agora.</summary>
    public static async Task<HashSet<string>> BrandsOnUsbAsync()
    {
        var found = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (vid, brand) in Vendors)
        {
            try
            {
                if (await CountUsbAsync(vid).ConfigureAwait(false) > 0) found.Add(brand);
            }
            catch { }
        }
        return found;
    }
}
