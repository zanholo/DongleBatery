using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;
using MonitorBateria.Models;

namespace MonitorBateria.Services;

/// <summary>
/// Junta todos os provedores, mantém a lista de dispositivos e dispara alertas.
/// Deve ser criado na thread da UI.
/// </summary>
public sealed class DeviceMonitor
{
    private readonly AppSettings _settings;
    private readonly BluetoothProvider _bt = new();
    private readonly XInputProvider _xinput = new();
    private readonly SystemBatteryProvider _sys = new();
    private readonly GattBatteryReader _gatt = new();
    private readonly Logitech.LogitechProvider _logi = new();
    public Logitech.LogitechProvider Logitech => _logi;
    private readonly DispatcherTimer _timer;
    private readonly Dispatcher _dispatcher;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly HashSet<string> _alerted = new();

    public ObservableCollection<BatteryDevice> Devices { get; } = new();
    public BluetoothProvider Bluetooth => _bt;

    public event EventHandler? Updated;
    public event EventHandler<BatteryDevice>? LowBattery;

    private bool _isRefreshing;
    public bool IsRefreshing => _isRefreshing;
    public DateTime LastRefresh { get; private set; }

    public DeviceMonitor(AppSettings settings)
    {
        _settings = settings;
        _dispatcher = Dispatcher.CurrentDispatcher;
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(Math.Max(10, settings.RefreshSeconds)) };
        _timer.Tick += async (_, _) => await RefreshAsync();

        // Conectou/desconectou: atualiza logo (com pequeno atraso para o Windows ler a bateria).
        _debounce = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _debounce.Tick += async (_, _) => { _debounce.Stop(); await RefreshAsync(); };
        _bt.ConnectionChanged += () => _dispatcher.BeginInvoke(() =>
        {
            _debounce.Stop();
            _debounce.Start();
        });
        _bt.StartWatcher();
    }

    private readonly DispatcherTimer _debounce;

    public void Start()
    {
        _timer.Start();
        _ = RefreshAsync();
    }

    public void ApplyInterval()
    {
        _timer.Interval = TimeSpan.FromSeconds(Math.Max(10, _settings.RefreshSeconds));
    }

    public async Task RefreshAsync()
    {
        if (!await _gate.WaitAsync(0)) return;
        _isRefreshing = true;
        try
        {
            var readings = await Task.Run(CollectAsync);
            Merge(readings);
            LastRefresh = DateTime.Now;
        }
        catch (Exception ex)
        {
            Debug.WriteLine("Refresh: " + ex);
        }
        finally
        {
            _isRefreshing = false;
            _gate.Release();
            Updated?.Invoke(this, EventArgs.Empty);
        }
    }

    private async Task<List<DeviceReading>> CollectAsync()
    {
        var all = new List<DeviceReading>();

        var bt = await _bt.ReadAsync();

        // Tenta GATT para BLE conectados sem bateria
        foreach (var (key, aepId) in _bt.LeCandidatesWithoutBattery)
        {
            var lvl = await _gatt.TryReadAsync(aepId);
            if (lvl is null) continue;
            var idx = bt.FindIndex(r => r.Key == key);
            if (idx < 0) continue;
            var r = bt[idx];
            bt[idx] = new DeviceReading
            {
                Key = r.Key, Name = r.Name, Kind = r.Kind, Brand = r.Brand, Model = r.Model,
                Source = r.Source + " (GATT)", Level = lvl, IsConnected = r.IsConnected, Parts = r.Parts,
            };
        }
        // Teclados que só "aparecem" no USB quando estão no cabo (Keychron etc.)
        var wired = await WiredDetector.BrandsOnUsbAsync();
        for (int i = 0; i < bt.Count; i++)
        {
            var r = bt[i];
            if (r.Brand is null || !wired.Contains(r.Brand) || r.Kind != DeviceKind.Keyboard) continue;
            bt[i] = new DeviceReading
            {
                Key = r.Key, Name = r.Name, Kind = r.Kind, Brand = r.Brand, Model = r.Model, Source = "Cabo USB",
                Level = r.Level, IsConnected = true, Parts = r.Parts, ReportedState = PowerState.Wired,
            };
        }
        all.AddRange(bt);

        int btPads = bt.Count(r => r.Kind == DeviceKind.Gamepad && r.IsConnected && r.Level.HasValue);
        all.AddRange(_xinput.Read(btPads));
        try { all.AddRange(await _logi.ReadAsync()); } catch (Exception ex) { Debug.WriteLine("Logitech: " + ex); }
        all.AddRange(_sys.Read());
        return all;
    }

    private void Merge(List<DeviceReading> readings)
    {
        var now = DateTime.Now;
        var seen = new HashSet<string>();

        foreach (var r in readings)
        {
            seen.Add(r.Key);
            var dev = Devices.FirstOrDefault(d => d.Key == r.Key);
            if (dev is null)
            {
                dev = new BatteryDevice(r.Key);
                var prefs = _settings.For(r.Key);
                dev.IsHidden = prefs.Hidden;
                dev.ShowInTray = prefs.TrayIcon;
                dev.CustomName = prefs.CustomName;
                Devices.Add(dev);
            }
            dev.Apply(r, now);
            dev.IsMainTray = _settings.MainTrayDevice == dev.Key;
            CheckAlert(dev);
        }

        // Dispositivos que sumiram (ex.: controle XInput desligado) — remove
        for (int i = Devices.Count - 1; i >= 0; i--)
            if (!seen.Contains(Devices[i].Key)) Devices.RemoveAt(i);

        // Ordena: conectados primeiro, depois por nível
        var ordered = Devices
            .OrderByDescending(d => d.IsConnected)
            .ThenBy(d => d.Level.HasValue ? 0 : 1)
            .ThenBy(d => d.Level ?? 101)
            .ThenBy(d => d.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
        for (int i = 0; i < ordered.Count; i++)
        {
            int cur = Devices.IndexOf(ordered[i]);
            if (cur != i) Devices.Move(cur, i);
        }
    }

    private void CheckAlert(BatteryDevice d)
    {
        if (!_settings.LowBatteryAlert || d.IsHidden) return;
        if (d.Level is int l && d.IsConnected && !d.IsCharging && l <= _settings.LowBatteryThreshold)
        {
            if (_alerted.Add(d.Key)) LowBattery?.Invoke(this, d);
        }
        else if (d.Level is int l2 && l2 > _settings.LowBatteryThreshold + 5)
        {
            _alerted.Remove(d.Key);
        }
    }

    public void SavePrefs(BatteryDevice d)
    {
        var p = _settings.For(d.Key);
        p.Hidden = d.IsHidden;
        p.TrayIcon = d.ShowInTray;
        p.CustomName = d.CustomName;
        _settings.Save();
        Updated?.Invoke(this, EventArgs.Empty);
    }
}
