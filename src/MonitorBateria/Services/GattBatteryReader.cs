using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Threading.Tasks;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Storage.Streams;

namespace MonitorBateria.Services;

/// <summary>
/// Para dispositivos BLE conectados que o Windows não reporta a bateria, tenta ler
/// diretamente o Battery Service (0x180F / característica 0x2A19).
/// Dispositivos HID costumam bloquear esse acesso; nesse caso o resultado é nulo e
/// não tentamos de novo por um tempo.
/// </summary>
public sealed class GattBatteryReader
{
    private readonly ConcurrentDictionary<string, DateTime> _backoff = new();
    private readonly ConcurrentDictionary<string, (int level, DateTime at)> _cache = new();

    public async Task<int?> TryReadAsync(string aepId)
    {
        if (_cache.TryGetValue(aepId, out var c) && (DateTime.Now - c.at).TotalSeconds < 50) return c.level;
        if (_backoff.TryGetValue(aepId, out var until) && DateTime.Now < until) return null;

        try
        {
            var readTask = ReadCoreAsync(aepId);
            var done = await Task.WhenAny(readTask, Task.Delay(TimeSpan.FromSeconds(6))).ConfigureAwait(false);
            int? level = done == readTask ? await readTask.ConfigureAwait(false) : null;
            if (level is int l)
            {
                _cache[aepId] = (l, DateTime.Now);
                return l;
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"GATT {aepId}: {ex.Message}");
        }
        _backoff[aepId] = DateTime.Now.AddMinutes(10);
        return null;
    }

    private static async Task<int?> ReadCoreAsync(string aepId)
    {
        using var dev = await BluetoothLEDevice.FromIdAsync(aepId).AsTask().ConfigureAwait(false);
        if (dev is null || dev.ConnectionStatus != BluetoothConnectionStatus.Connected) return null;

        var services = await dev.GetGattServicesForUuidAsync(GattServiceUuids.Battery, BluetoothCacheMode.Cached).AsTask().ConfigureAwait(false);
        if (services.Status != GattCommunicationStatus.Success || services.Services.Count == 0) return null;

        try
        {
            var svc = services.Services[0];
            var chars = await svc.GetCharacteristicsForUuidAsync(GattCharacteristicUuids.BatteryLevel, BluetoothCacheMode.Cached).AsTask().ConfigureAwait(false);
            if (chars.Status != GattCommunicationStatus.Success || chars.Characteristics.Count == 0) return null;

            var val = await chars.Characteristics[0].ReadValueAsync(BluetoothCacheMode.Uncached).AsTask().ConfigureAwait(false);
            if (val.Status != GattCommunicationStatus.Success || val.Value is null || val.Value.Length < 1) return null;

            using var reader = DataReader.FromBuffer(val.Value);
            int level = reader.ReadByte();
            return level is >= 0 and <= 100 ? level : null;
        }
        finally
        {
            foreach (var s in services.Services) s.Dispose();
        }
    }
}
