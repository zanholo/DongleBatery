using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using MonitorBateria.Models;
using Windows.Devices.Enumeration;

namespace MonitorBateria.Services.Logitech;

/// <summary>
/// Bateria de dispositivos Logitech ligados por dongle (LIGHTSPEED, Unifying, Bolt) ou com
/// receptor próprio (headsets G533/G733/G935/PRO X…), via protocolo HID++ 2.0.
/// </summary>
public sealed class LogitechProvider
{
    private const ushort LogitechVid = 0x046D;

    // Features HID++ 2.0
    private const ushort FRoot = 0x0000;
    private const ushort FDeviceName = 0x0005;
    private const ushort FBatteryStatus = 0x1000;
    private const ushort FBatteryVoltage = 0x1001;
    private const ushort FUnifiedBattery = 0x1004;
    private const ushort FAdcMeasurement = 0x1F20;

    private const string PUsagePage = "System.DeviceInterface.Hid.UsagePage";
    private const string PUsageId = "System.DeviceInterface.Hid.UsageId";
    private const string PProductId = "System.DeviceInterface.Hid.ProductId";
    private const string PVendorId = "System.DeviceInterface.Hid.VendorId";

    // Receptores conhecidos (dispositivos ficam nos índices 1..6)
    private static readonly HashSet<ushort> ReceiverPids = new()
    {
        0xC52B, 0xC532, 0xC534, 0xC539, 0xC53A, 0xC53D, 0xC53F, 0xC541, 0xC545, 0xC547, 0xC548, 0xC54D,
    };

    // Nomes dos headsets com dongle próprio (quando o dispositivo não informa o nome)
    private static readonly Dictionary<ushort, string> KnownNames = new()
    {
        [0x0A66] = "Logitech G533", [0x0A5B] = "Logitech G933", [0x0A87] = "Logitech G935",
        [0x0AB5] = "Logitech G733", [0x0AFE] = "Logitech G733", [0x0ABA] = "Logitech G PRO X",
        [0x0AF7] = "Logitech G PRO X 2", [0x0A89] = "Logitech G635", [0x0ACB] = "Logitech G435",
        [0x0AAF] = "Logitech G915", [0x0B1A] = "Logitech G PRO X 2",
    };

    private sealed class Info
    {
        public string Name = "";
        public DeviceKind Kind;
        public Dictionary<ushort, byte> Features = new();
    }

    /// <summary>Nome/tipo/índices de features por (dongle, índice), para não perguntar toda vez.</summary>
    private readonly ConcurrentDictionary<string, Info> _info = new();
    /// <summary>Últimas leituras, para continuar mostrando quando o dispositivo desligar.</summary>
    private readonly ConcurrentDictionary<string, DeviceReading> _last = new();

    public List<string> DebugLog { get; } = new();
    public static List<string> AllInterfaces { get; } = new();
    /// <summary>Grava os bytes trocados no DebugLog (só no modo --dump).</summary>
    public bool Verbose { get; set; }

    // Índices que responderam em cada receptor; a varredura completa (que pode acordar
    // dispositivos) é feita só de vez em quando.
    private readonly ConcurrentDictionary<ushort, HashSet<byte>> _knownIndices = new();
    private readonly ConcurrentDictionary<ushort, DateTime> _lastFullScan = new();

    public async Task<List<DeviceReading>> ReadAsync()
    {
        DebugLog.Clear();
        AllInterfaces.Clear();
        HidPpChannel.Trace = Verbose ? DebugLog : null;
        var result = new List<DeviceReading>();
        var seen = new HashSet<string>();

        foreach (var (path, pid) in await FindInterfacesAsync().ConfigureAwait(false))
        {
            using var ch = HidPpChannel.Open(path);
            if (ch is null) { Log($"{pid:X4}: não abriu {path}"); continue; }

            bool isReceiver = ReceiverPids.Contains(pid) || ch.Product.Contains("Receiver", StringComparison.OrdinalIgnoreCase);
            Log($"{pid:X4} '{ch.Product}' receptor={isReceiver}");
            var indices = isReceiver ? new byte[] { 1, 2, 3, 4, 5, 6 } : new byte[] { 0xFF };
            if (isReceiver)
            {
                bool full = !_lastFullScan.TryGetValue(pid, out var last) || (DateTime.Now - last).TotalMinutes >= 5
                            || !_knownIndices.TryGetValue(pid, out var known) || known.Count == 0;
                if (full) _lastFullScan[pid] = DateTime.Now;
                else indices = _knownIndices[pid].OrderBy(i => i).ToArray();
            }

            var alive = await ch.PingAsync(indices, 2500).ConfigureAwait(false);
            Log($"{pid:X4}: responderam {string.Join(",", alive.Select(a => a.ToString("X2")))}");
            if (isReceiver && alive.Count > 0)
                _knownIndices.AddOrUpdate(pid, _ => new HashSet<byte>(alive), (_, set) => { set.UnionWith(alive); return set; });

            foreach (var idx in indices)
            {
                var key = $"logi:{pid:x4}:{idx:x2}";
                if (!seen.Add(key)) continue;
                try
                {
                    var r = alive.Contains(idx) ? await ReadDeviceAsync(ch, pid, idx, key, isReceiver).ConfigureAwait(false) : null;
                    if (r != null)
                    {
                        _last[key] = r;
                        result.Add(r);
                    }
                    else if (_last.TryGetValue(key, out var prev))
                    {
                        // Desligado / fora de alcance: mantém o último valor como desconectado.
                        result.Add(Clone(prev, connected: false));
                    }
                    else if (_info.TryGetValue(key, out var known))
                    {
                        // Já identificado (ex.: headset desligado com o dongle conectado), sem leitura ainda.
                        result.Add(new DeviceReading
                        {
                            Key = key, Name = known.Name, Kind = known.Kind, Brand = "Logitech",
                            Source = isReceiver ? "LIGHTSPEED / receptor USB" : "Receptor USB",
                            IsConnected = false,
                        });
                    }
                }
                catch (Exception ex)
                {
                    Log($"{key}: {ex.Message}");
                }
            }
        }
        return result;
    }

    private async Task<DeviceReading?> ReadDeviceAsync(HidPpChannel ch, ushort pid, byte idx, string key, bool isReceiver)
    {
        if (!_info.TryGetValue(key, out var info))
        {
            info = new Info();
            foreach (var f in new[] { FDeviceName, FUnifiedBattery, FBatteryStatus, FBatteryVoltage, FAdcMeasurement })
            {
                var r = await ch.RequestAsync(idx, 0x00, 0x0, new[] { (byte)(f >> 8), (byte)f }).ConfigureAwait(false);
                if (r != null && r[0] != 0) info.Features[f] = r[0];
            }
            Log($"{key}: features " + string.Join(", ", info.Features.Select(kv => $"{kv.Key:X4}@{kv.Value}")));

            info.Name = await ReadNameAsync(ch, idx, info).ConfigureAwait(false)
                        ?? (KnownNames.TryGetValue(pid, out var kn) ? kn : (string.IsNullOrWhiteSpace(ch.Product) ? "Dispositivo Logitech" : ch.Product));
            info.Kind = await ReadKindAsync(ch, idx, info).ConfigureAwait(false)
                        ?? DeviceClassifier.Classify(info.Name, null, null);
            if (info.Kind == DeviceKind.Generic && !isReceiver) info.Kind = DeviceKind.Headset;
            // A marca já aparece no selo "Logitech" do card.
            if (info.Name.StartsWith("Logitech ", StringComparison.OrdinalIgnoreCase))
                info.Name = info.Name[9..].Trim();
            // Só guarda no cache se a identificação veio completa; senão tenta de novo na próxima.
            if (!info.Features.ContainsKey(FDeviceName) || info.Kind != DeviceKind.Generic) _info[key] = info;
        }

        var (level, state, approx) = await ReadBatteryAsync(ch, idx, info).ConfigureAwait(false);
        if (level is null && state is null)
        {
            Log($"{key}: sem leitura de bateria (erro {ch.LastError})");
            if (info.Features.Count == 0) { _info.TryRemove(key, out _); return null; }
            // Headset com dongle próprio: o dongle responde, mas o headset está desligado.
            if (!isReceiver && info.Features.ContainsKey(FAdcMeasurement))
            {
                Log($"{key}: headset desligado");
                return null;
            }
        }

        return new DeviceReading
        {
            Key = key,
            Name = info.Name,
            Kind = info.Kind,
            Brand = "Logitech",
            Source = isReceiver ? "LIGHTSPEED / receptor USB" : "Receptor USB",
            Level = level,
            IsConnected = true,
            ReportedState = state,
            Approximate = approx,
        };
    }

    private static async Task<string?> ReadNameAsync(HidPpChannel ch, byte idx, Info info)
    {
        if (!info.Features.TryGetValue(FDeviceName, out var fi)) return null;
        var cnt = await ch.RequestAsync(idx, fi, 0x0).ConfigureAwait(false);
        if (cnt is null || cnt[0] == 0) return null;
        var sb = new StringBuilder();
        while (sb.Length < cnt[0])
        {
            var part = await ch.RequestAsync(idx, fi, 0x1, new[] { (byte)sb.Length }).ConfigureAwait(false);
            if (part is null) break;
            var chunk = Encoding.ASCII.GetString(part).TrimEnd('\0');
            if (chunk.Length == 0) break;
            sb.Append(chunk);
        }
        var name = sb.ToString().Trim();
        return name.Length == 0 ? null : name;
    }

    private static async Task<DeviceKind?> ReadKindAsync(HidPpChannel ch, byte idx, Info info)
    {
        if (!info.Features.TryGetValue(FDeviceName, out var fi)) return null;
        var t = await ch.RequestAsync(idx, fi, 0x2).ConfigureAwait(false);
        if (t is null) return null;
        return t[0] switch
        {
            0 => DeviceKind.Keyboard,
            1 => DeviceKind.Remote,
            2 => DeviceKind.Keyboard, // numpad
            3 => DeviceKind.Mouse,
            4 => DeviceKind.Touchpad,
            5 => DeviceKind.Mouse,    // trackball
            6 => DeviceKind.Remote,   // presenter
            8 => DeviceKind.Headset,
            12 => DeviceKind.Gamepad,
            14 => DeviceKind.Speaker,
            _ => null,
        };
    }

    private static async Task<(int? level, PowerState? state, bool approx)> ReadBatteryAsync(HidPpChannel ch, byte idx, Info info)
    {
        // 0x1004 Unified Battery
        if (info.Features.TryGetValue(FUnifiedBattery, out var fi))
        {
            var r = await ch.RequestAsync(idx, fi, 0x1).ConfigureAwait(false);
            if (r != null)
            {
                int? soc = r[0] is > 0 and <= 100 ? r[0] : null;
                bool approx = false;
                if (soc is null)
                {
                    approx = true;
                    soc = (r[1] & 0x08) != 0 ? 100 : (r[1] & 0x04) != 0 ? 60 : (r[1] & 0x02) != 0 ? 25 : (r[1] & 0x01) != 0 ? 5 : null;
                }
                PowerState? st = r[2] switch
                {
                    0 => PowerState.Discharging,
                    1 or 2 => PowerState.Charging,
                    3 => PowerState.Full,
                    _ => null,
                };
                return (soc, st, approx);
            }
        }

        // 0x1000 Battery Level Status
        if (info.Features.TryGetValue(FBatteryStatus, out fi))
        {
            var r = await ch.RequestAsync(idx, fi, 0x0).ConfigureAwait(false);
            if (r != null)
            {
                PowerState? st = r[2] switch
                {
                    0 => PowerState.Discharging,
                    1 or 2 or 4 => PowerState.Charging,
                    3 => PowerState.Full,
                    _ => null,
                };
                return (r[0] is > 0 and <= 100 ? r[0] : null, st, false);
            }
        }

        // 0x1001 Battery Voltage
        if (info.Features.TryGetValue(FBatteryVoltage, out fi))
        {
            var r = await ch.RequestAsync(idx, fi, 0x0).ConfigureAwait(false);
            if (r != null)
            {
                int mv = (r[0] << 8) | r[1];
                bool charging = (r[2] & 0x80) != 0;
                return (VoltageToPercent(mv), charging ? PowerState.Charging : PowerState.Discharging, true);
            }
        }

        // 0x1F20 ADC Measurement (headsets G533/G933/G935/G733/PRO X)
        if (info.Features.TryGetValue(FAdcMeasurement, out fi))
        {
            var r = await ch.RequestAsync(idx, fi, 0x0).ConfigureAwait(false);
            if (r != null)
            {
                int mv = (r[0] << 8) | r[1];
                if (mv == 0) return (null, null, false); // headset desligado (só o dongle responde)
                // Status: 0x01 descarregando, 0x03 carregando, 0x07 carga completa (ainda no cabo).
                // Com a carga completa não há corrente de carga, então a tensão não está elevada.
                if (r[2] == 0x07) return (100, PowerState.Full, false);
                bool charging = r[2] == 0x03;
                return (HeadsetVoltageToPercent(mv, charging), charging ? PowerState.Charging : PowerState.Discharging, true);
            }
        }

        return (null, null, false);
    }

    /// <summary>
    /// Headsets Logitech (G533/G933/G935…): curva de descarga usada pelo G HUB/HeadsetControl.
    /// Carregando, o carregador eleva a tensão medida (~240 mV), então descontamos para não
    /// mostrar mais do que o real (calibrado com o G HUB: 4028 mV carregando ≈ 39%).
    /// </summary>
    internal static int HeadsetVoltageToPercent(int mv, bool charging)
    {
        if (charging) mv -= 240;
        (int mv, int pct)[] curve =
        {
            (4175, 100), (4000, 80), (3880, 60), (3817, 50), (3766, 30), (3730, 20), (3664, 5), (3310, 0),
        };
        return Interpolate(mv, curve);
    }

    private static int Interpolate(int mv, (int mv, int pct)[] curve)
    {
        if (mv >= curve[0].mv) return curve[0].pct;
        for (int i = 1; i < curve.Length; i++)
        {
            if (mv >= curve[i].mv)
            {
                var (hiV, hiP) = curve[i - 1];
                var (loV, loP) = curve[i];
                return (int)Math.Round(loP + (double)(mv - loV) / (hiV - loV) * (hiP - loP));
            }
        }
        return 0;
    }

    /// <summary>Curva típica de célula Li-ion (mV → %).</summary>
    internal static int VoltageToPercent(int mv)
    {
        (int mv, int pct)[] curve =
        {
            (4186, 100), (4067, 90), (3989, 80), (3922, 70), (3859, 60), (3811, 50),
            (3778, 40), (3751, 30), (3717, 20), (3671, 10), (3646, 5), (3579, 2), (3500, 0),
        };
        if (mv >= curve[0].mv) return 100;
        for (int i = 1; i < curve.Length; i++)
        {
            if (mv >= curve[i].mv)
            {
                var (hiV, hiP) = curve[i - 1];
                var (loV, loP) = curve[i];
                return (int)Math.Round(loP + (double)(mv - loV) / (hiV - loV) * (hiP - loP));
            }
        }
        return 0;
    }

    private static async Task<List<(string path, ushort pid)>> FindInterfacesAsync()
    {
        var list = new List<(string, ushort)>();
        try
        {
            // HID++ "long": usage page 0xFF00 / usage 0x0002 (receptores) ou 0xFF43 / 0x0602 (dispositivos mais novos)
            var aqs = "System.Devices.InterfaceClassGuid:=\"{4d1e55b2-f16f-11cf-88cb-001111000030}\" AND " +
                      "System.Devices.InterfaceEnabled:=System.StructuredQueryType.Boolean#True AND " +
                      $"{PVendorId}:={LogitechVid}";
            var all = await DeviceInformation.FindAllAsync(aqs, new[] { PUsagePage, PUsageId, PProductId, PVendorId })
                                             .AsTask().ConfigureAwait(false);
            foreach (var d in all)
            {
                ushort page = Convert.ToUInt16(d.Properties.TryGetValue(PUsagePage, out var p) && p != null ? p : 0);
                ushort usage = Convert.ToUInt16(d.Properties.TryGetValue(PUsageId, out var u) && u != null ? u : 0);
                ushort pid = Convert.ToUInt16(d.Properties.TryGetValue(PProductId, out var pp) && pp != null ? pp : 0);
                bool longHidpp = (page == 0xFF00 && usage == 0x0002) || (page == 0xFF43 && (usage & 0xFF) == 0x02);
                AllInterfaces.Add($"{pid:X4} page={page:X4} usage={usage:X4} {(longHidpp ? "<- HID++ long" : "")}");
                if (longHidpp) list.Add((d.Id, pid));
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine("Logitech FindInterfaces: " + ex);
        }
        return list;
    }

    private static DeviceReading Clone(DeviceReading r, bool connected) => new()
    {
        Key = r.Key, Name = r.Name, Kind = r.Kind, Brand = r.Brand, Model = r.Model, Source = r.Source,
        Level = r.Level, IsConnected = connected, Approximate = r.Approximate, Parts = r.Parts,
        ReportedState = connected ? r.ReportedState : null,
    };

    private void Log(string s)
    {
        lock (DebugLog) DebugLog.Add(s);
        Debug.WriteLine("Logitech " + s);
    }
}
