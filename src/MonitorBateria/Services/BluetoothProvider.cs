using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using MonitorBateria.Models;
using Windows.Devices.Enumeration;

namespace MonitorBateria.Services;

/// <summary>
/// Lê a bateria que o próprio Windows obtém dos dispositivos Bluetooth (clássico e LE):
/// a propriedade DEVPKEY_Bluetooth_Battery que aparece em Configurações &gt; Bluetooth.
/// Agrupa os nós de dispositivo pelo ContainerId (um contêiner = um dispositivo físico).
/// </summary>
public sealed class BluetoothProvider
{
    // DEVPKEY_Bluetooth_Battery (byte 0..100) e data/hora da última atualização
    private const string PKeyBattery = "{104EA319-6EE2-4701-BD47-8DDBF425BBE5} 2";
    private const string PKeyBatteryTime = "{104EA319-6EE2-4701-BD47-8DDBF425BBE5} 7";
    // DEVPKEY_Device_IsConnected (nó do dispositivo)
    private const string PKeyDevIsConnected = "{83DA6326-97A6-4088-9453-A1923F573B29} 15";
    private const string PKeyContainerId = "System.Devices.ContainerId";
    private const string PKeyBtAddress = "{2BD67D8B-8BEB-48D5-87E0-6CDA3428040A} 1"; // DEVPKEY_Bluetooth_DeviceAddress
    private const string PKeyBtCod = "{2BD67D8B-8BEB-48D5-87E0-6CDA3428040A} 10";   // DEVPKEY_Bluetooth_ClassOfDevice
    private const string PKeyInstanceId = "System.Devices.DeviceInstanceId";

    // Endpoints de associação (o que aparece em Configurações > Bluetooth e dispositivos)
    private const string AepIsConnected = "System.Devices.Aep.IsConnected";
    private const string AepIsPaired = "System.Devices.Aep.IsPaired";
    private const string AepContainerId = "System.Devices.Aep.ContainerId";
    private const string AepProtocolId = "System.Devices.Aep.ProtocolId";

    private const string BtClassicProtocol = "{e0cbf06c-cd8b-4647-bb8a-263b43f0f974}";
    private const string BtLeProtocol = "{bb7bb05e-5972-42b5-94fc-76eaa7084d49}";

    private const string ContainerCategory = "System.Devices.Category";
    private const string ContainerConnected = "System.Devices.Connected";
    private const string ContainerManufacturer = "System.Devices.Manufacturer";
    private const string ContainerModel = "System.Devices.ModelName";

    private sealed class Node
    {
        public string InstanceId = "";
        public string Name = "";
        public Guid Container;
        public int? Battery;
        public DateTime? BatteryTime;
        public bool Connected;
        public string? Address;
        public uint? Cod;
        public bool IsLe;
    }

    public sealed class Endpoint
    {
        public string Id = "";
        public string Name = "";
        public Guid Container;
        public bool Connected;
        public bool IsLe;
        public string[] Categories = Array.Empty<string>();
        public string? Manufacturer;
        public string? Model;
        public uint? Cod;
    }

    /// <summary>Ids de endpoints BLE conectados sem bateria informada (para o leitor GATT tentar).</summary>
    public IReadOnlyList<(string key, string aepId)> LeCandidatesWithoutBattery { get; private set; } = Array.Empty<(string, string)>();

    internal static string? LastError;
    internal static string? Timings;

    public async Task<List<DeviceReading>> ReadAsync()
    {
        var sw = Stopwatch.StartNew();
        StartWatcher();
        var nodes = await ReadNodesAsync().ConfigureAwait(false);
        var tNodes = sw.ElapsedMilliseconds;
        var endpoints = Endpoints.ToList();

        var containers = new HashSet<Guid>(endpoints.Select(e => e.Container));
        foreach (var n in nodes.Where(n => n.Battery.HasValue)) containers.Add(n.Container);
        containers.Remove(Guid.Empty);

        var result = new List<DeviceReading>();
        var leCandidates = new List<(string, string)>();

        foreach (var cid in containers)
        {
            var eps = endpoints.Where(e => e.Container == cid).ToList();
            var cnodes = nodes.Where(n => n.Container == cid).ToList();
            var withBattery = cnodes.Where(n => n.Battery.HasValue).ToList();

            bool connected = eps.Any(e => e.Connected) || cnodes.Any(n => n.Connected);
            var info = await ReadContainerAsync(cid).ConfigureAwait(false);
            if (info.connected == true) connected = true;

            // Mostra apenas dispositivos pareados que tenham bateria ou estejam conectados agora.
            if (withBattery.Count == 0 && !connected) continue;

            string name = info.name
                          ?? eps.Select(e => e.Name).FirstOrDefault(n => !string.IsNullOrWhiteSpace(n))
                          ?? cnodes.Select(n => n.Name).FirstOrDefault(n => !string.IsNullOrWhiteSpace(n))
                          ?? "Dispositivo Bluetooth";

            var categories = info.categories.Concat(eps.SelectMany(e => e.Categories)).ToList();
            uint? cod = eps.Select(e => e.Cod).FirstOrDefault(c => c is > 0) ?? cnodes.Select(n => n.Cod).FirstOrDefault(c => c is > 0);
            var kind = DeviceClassifier.Classify(name, categories, cod);
            var manufacturer = info.manufacturer ?? eps.Select(e => e.Manufacturer).FirstOrDefault(m => !string.IsNullOrWhiteSpace(m));
            var model = info.model ?? eps.Select(e => e.Model).FirstOrDefault(m => !string.IsNullOrWhiteSpace(m));
            // Nomes genéricos do driver não ajudam ("Bluetooth LE Device 3cfa…", "Dispositivo Periférico Bluetooth")
            if (model != null && (model.Contains("Bluetooth", StringComparison.OrdinalIgnoreCase) || model.Contains("Dispositivo", StringComparison.OrdinalIgnoreCase)))
                model = null;

            // Uma leitura por endereço Bluetooth (fones TWS têm um endereço por lado).
            var perAddress = withBattery
                .GroupBy(n => (n.Address ?? n.InstanceId).ToLowerInvariant())
                .Select(g => g
                    .OrderByDescending(n => n.BatteryTime ?? DateTime.MinValue)
                    .ThenByDescending(n => n.IsLe)
                    .First())
                .ToList();

            int? level = perAddress.Count > 0 ? perAddress.Min(n => n.Battery) : null;
            var parts = new List<BatteryPart>();
            if (perAddress.Count > 1)
            {
                int i = 1;
                foreach (var n in perAddress.OrderBy(n => n.Address))
                {
                    var label = kind == DeviceKind.Earbuds ? $"Fone {i}" : $"Parte {i}";
                    parts.Add(new BatteryPart { Label = label, Level = n.Battery!.Value });
                    i++;
                }
            }

            bool isLe = eps.Any(e => e.IsLe) || cnodes.Any(n => n.IsLe);
            bool isClassic = eps.Any(e => !e.IsLe) || cnodes.Any(n => !n.IsLe);
            string source = isLe && isClassic ? "Bluetooth" : isLe ? "Bluetooth LE" : "Bluetooth";

            var key = "bt:" + cid.ToString("D");
            result.Add(new DeviceReading
            {
                Key = key,
                Name = name,
                Kind = kind,
                Brand = DeviceClassifier.DetectBrand(name, manufacturer),
                Model = model,
                Source = source,
                Level = level,
                IsConnected = connected,
                LevelTimestamp = perAddress.Select(n => n.BatteryTime).Max(),
                Parts = parts,
            });

            if (level is null && connected)
            {
                var le = eps.FirstOrDefault(e => e.IsLe);
                if (le != null) leCandidates.Add((key, le.Id));
            }
        }

        LeCandidatesWithoutBattery = leCandidates;
        Timings = $"nós={tNodes}ms endpoints={endpoints.Count} total={sw.ElapsedMilliseconds}ms";
        Debug.WriteLine($"BluetoothProvider: {result.Count} dispositivos em {sw.ElapsedMilliseconds} ms");
        return result;
    }

    private static async Task<List<Node>> ReadNodesAsync()
    {
        var list = new List<Node>();
        try
        {
            var props = new[] { PKeyBattery, PKeyBatteryTime, PKeyDevIsConnected, PKeyContainerId, PKeyBtAddress, PKeyBtCod, PKeyInstanceId };
            // Só nós Bluetooth presentes (BTHENUM / BTHLE)
            const string aqs = "System.Devices.Present:=System.StructuredQueryType.Boolean#True";
            var all = await DeviceInformation.FindAllAsync(aqs, props, DeviceInformationKind.Device).AsTask().ConfigureAwait(false);
            foreach (var d in all)
            {
                var id = d.Id ?? "";
                bool isLe = id.StartsWith("BTHLE\\", StringComparison.OrdinalIgnoreCase);
                bool isBt = isLe || id.StartsWith("BTHENUM\\", StringComparison.OrdinalIgnoreCase);
                var battery = Get<byte?>(d, PKeyBattery);
                if (!isBt && battery is null) continue;

                list.Add(new Node
                {
                    InstanceId = id,
                    Name = d.Name ?? "",
                    Container = Get<Guid?>(d, PKeyContainerId) ?? Guid.Empty,
                    Battery = battery is byte b && b <= 100 ? b : null,
                    BatteryTime = Get<DateTimeOffset?>(d, PKeyBatteryTime)?.LocalDateTime,
                    Connected = Get<bool?>(d, PKeyDevIsConnected) ?? false,
                    Address = NormalizeAddress(Get<string>(d, PKeyBtAddress)),
                    Cod = Get<uint?>(d, PKeyBtCod),
                    IsLe = isLe,
                });
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine("ReadNodesAsync: " + ex);
        }
        return list;
    }

    // ---------------------------------------------------------------- endpoints (watcher contínuo)
    // FindAllAsync em AssociationEndpoint pode levar ~30 s (faz varredura BLE). O watcher entrega
    // a lista de pareados rapidamente e depois avisa conexões/desconexões em tempo real.

    private readonly ConcurrentDictionary<string, Endpoint> _endpoints = new();
    private DeviceWatcher? _watcher;
    private readonly TaskCompletionSource _enumerated = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Disparado quando um dispositivo conecta, desconecta ou é pareado/removido.</summary>
    public event Action? ConnectionChanged;

    public IReadOnlyCollection<Endpoint> Endpoints => _endpoints.Values.ToList();

    public Task WaitEnumeratedAsync(TimeSpan timeout) => Task.WhenAny(_enumerated.Task, Task.Delay(timeout));

    public void StartWatcher()
    {
        if (_watcher != null) return;
        try
        {
            var props = new[] { AepIsConnected, AepIsPaired, AepContainerId, AepProtocolId };
            var aqs = $"(System.Devices.Aep.ProtocolId:=\"{BtClassicProtocol}\" OR System.Devices.Aep.ProtocolId:=\"{BtLeProtocol}\") AND System.Devices.Aep.IsPaired:=System.StructuredQueryType.Boolean#True";
            _watcher = DeviceInformation.CreateWatcher(aqs, props, DeviceInformationKind.AssociationEndpoint);
            _watcher.Added += (_, d) =>
            {
                var proto = Get<Guid?>(d, AepProtocolId);
                _endpoints[d.Id] = new Endpoint
                {
                    Id = d.Id,
                    Name = d.Name ?? "",
                    Container = Get<Guid?>(d, AepContainerId) ?? Guid.Empty,
                    Connected = Get<bool?>(d, AepIsConnected) ?? false,
                    IsLe = proto.HasValue && proto.Value == Guid.Parse(BtLeProtocol),
                };
                if (_enumerated.Task.IsCompleted) ConnectionChanged?.Invoke();
            };
            _watcher.Updated += (_, u) =>
            {
                if (!_endpoints.TryGetValue(u.Id, out var ep)) return;
                if (u.Properties.TryGetValue(AepIsConnected, out var v) && v is bool c && c != ep.Connected)
                {
                    ep.Connected = c;
                    ConnectionChanged?.Invoke();
                }
            };
            _watcher.Removed += (w, u) =>
            {
                if (_endpoints.TryRemove(u.Id, out _)) ConnectionChanged?.Invoke();
            };
            _watcher.EnumerationCompleted += (_, _) => { _enumerated.TrySetResult(); ConnectionChanged?.Invoke(); };
            _watcher.Stopped += (_, _) => { LastError = "watcher parado"; };
            _watcher.Start();
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            Debug.WriteLine("StartWatcher: " + ex);
        }
    }

    private static async Task<(string? name, bool? connected, string[] categories, string? manufacturer, string? model)> ReadContainerAsync(Guid cid)
    {
        try
        {
            var props = new[] { ContainerCategory, ContainerConnected, ContainerManufacturer, ContainerModel };
            var d = await DeviceInformation.CreateFromIdAsync("{" + cid.ToString("D") + "}", props, DeviceInformationKind.DeviceContainer).AsTask().ConfigureAwait(false);
            return (string.IsNullOrWhiteSpace(d.Name) ? null : d.Name,
                    Get<bool?>(d, ContainerConnected),
                    Get<string[]>(d, ContainerCategory) ?? Array.Empty<string>(),
                    Get<string>(d, ContainerManufacturer),
                    Get<string>(d, ContainerModel));
        }
        catch
        {
            return (null, null, Array.Empty<string>(), null, null);
        }
    }

    private static string? NormalizeAddress(string? a)
        => string.IsNullOrWhiteSpace(a) ? null : a.Replace(":", "").Replace("-", "").ToLowerInvariant();

    private static T? Get<T>(DeviceInformation d, string key)
    {
        if (!d.Properties.TryGetValue(key, out var v) || v is null) return default;
        try
        {
            var target = Nullable.GetUnderlyingType(typeof(T)) ?? typeof(T);
            if (target.IsInstanceOfType(v)) return (T)v;
            if (target == typeof(Guid) && v is string s && Guid.TryParse(s, out var g)) return (T)(object)g;
            if (target.IsPrimitive) return (T)Convert.ChangeType(v, target);
        }
        catch { }
        return default;
    }
}
