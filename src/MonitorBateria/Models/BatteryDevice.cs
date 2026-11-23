using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;

namespace MonitorBateria.Models;

/// <summary>Leitura de uma parte do dispositivo (ex.: cada fone de um par de earbuds).</summary>
public sealed class BatteryPart
{
    public string Label { get; init; } = "";
    public int Level { get; init; }
    public string Text => $"{Label} {Level}%";
}

/// <summary>Leitura bruta produzida por um provedor.</summary>
public sealed class DeviceReading
{
    public required string Key { get; init; }
    public required string Name { get; init; }
    public DeviceKind Kind { get; init; }
    public string? Brand { get; init; }
    public string? Model { get; init; }
    public string Source { get; init; } = "";
    public int? Level { get; init; }
    public bool IsConnected { get; init; }
    /// <summary>Estado informado pelo próprio dispositivo (quando existe). Senão é inferido pelo histórico.</summary>
    public PowerState? ReportedState { get; init; }
    public bool Approximate { get; init; }
    public DateTime? LevelTimestamp { get; init; }
    public IReadOnlyList<BatteryPart> Parts { get; init; } = Array.Empty<BatteryPart>();
}

/// <summary>Estado de um dispositivo exibido na UI.</summary>
public sealed class BatteryDevice : ObservableObject
{
    private readonly List<(DateTime t, int level)> _history = new();

    public BatteryDevice(string key) { Key = key; }

    public string Key { get; }

    private string _name = "";
    public string Name { get => _name; set { if (Set(ref _name, value)) OnPropertyChanged(nameof(DisplayName)); } }

    private string? _customName;
    public string? CustomName { get => _customName; set { if (Set(ref _customName, value)) OnPropertyChanged(nameof(DisplayName)); } }
    public string DisplayName => string.IsNullOrWhiteSpace(CustomName) ? Name : CustomName!;

    private DeviceKind _kind;
    public DeviceKind Kind
    {
        get => _kind;
        set
        {
            if (Set(ref _kind, value))
            {
                OnPropertyChanged(nameof(Glyph));
                OnPropertyChanged(nameof(KindLabel));
                OnPropertyChanged(nameof(IsEarbuds));
                OnPropertyChanged(nameof(Subtitle));
            }
        }
    }

    public string Glyph => DeviceClassifier.GlyphFor(Kind);
    public string KindLabel => DeviceClassifier.LabelFor(Kind);
    public bool IsEarbuds => Kind == DeviceKind.Earbuds;

    private string? _brand;
    public string? Brand { get => _brand; set { if (Set(ref _brand, value)) { OnPropertyChanged(nameof(HasBrand)); OnPropertyChanged(nameof(Subtitle)); } } }
    public bool HasBrand => !string.IsNullOrEmpty(Brand);

    private string? _model;
    public string? Model { get => _model; set { if (Set(ref _model, value)) OnPropertyChanged(nameof(Subtitle)); } }

    private string _source = "";
    public string Source { get => _source; set { if (Set(ref _source, value)) OnPropertyChanged(nameof(Subtitle)); } }

    public string Subtitle
    {
        get
        {
            var parts = new List<string> { KindLabel };
            if (!string.IsNullOrWhiteSpace(Model) && !string.Equals(Model, Name, StringComparison.OrdinalIgnoreCase))
                parts.Add(Model!);
            if (!string.IsNullOrWhiteSpace(Source)) parts.Add(Source);
            return string.Join(" · ", parts);
        }
    }

    private int? _level;
    public int? Level
    {
        get => _level;
        private set
        {
            if (Set(ref _level, value))
            {
                OnPropertyChanged(nameof(HasLevel));
                OnPropertyChanged(nameof(LevelText));
                OnPropertyChanged(nameof(LevelFraction));
                OnPropertyChanged(nameof(LevelBand));
                OnPropertyChanged(nameof(ColorKey));
            }
        }
    }

    public bool HasLevel => Level.HasValue;
    public double LevelFraction => (Level ?? 0) / 100.0;

    private bool _approximate;
    public bool Approximate { get => _approximate; private set { if (Set(ref _approximate, value)) OnPropertyChanged(nameof(LevelText)); } }

    public string LevelText => Level is int l ? (Approximate ? $"~{l}%" : $"{l}%") : "—";

    /// <summary>0 = sem dado, 1 = crítico, 2 = baixo, 3 = médio, 4 = bom</summary>
    public int LevelBand => Level switch
    {
        null => 0,
        <= 15 => 1,
        <= 30 => 2,
        <= 60 => 3,
        _ => 4,
    };

    /// <summary>Chave de cor usada pela UI: charge, crit, warn, ok, none.</summary>
    public string ColorKey => !IsConnected || Level is null ? "none"
        : IsCharging ? "charge"
        : Level <= 15 ? "crit"
        : Level <= 30 ? "warn"
        : "ok";

    private bool _isConnected;
    public bool IsConnected { get => _isConnected; private set { if (Set(ref _isConnected, value)) { OnPropertyChanged(nameof(StatusText)); OnPropertyChanged(nameof(ColorKey)); } } }

    private PowerState _state;
    public PowerState State
    {
        get => _state;
        private set
        {
            if (Set(ref _state, value))
            {
                OnPropertyChanged(nameof(StatusText));
                OnPropertyChanged(nameof(StateGlyph));
                OnPropertyChanged(nameof(IsCharging));
                OnPropertyChanged(nameof(ColorKey));
            }
        }
    }

    public bool IsCharging => State is PowerState.Charging or PowerState.Wired;

    public string StateGlyph => State switch
    {
        PowerState.Charging => "",     // raio
        PowerState.Wired => "",        // plugue
        PowerState.Discharging => "",  // seta para baixo
        PowerState.Full => "",         // check
        PowerState.Stable => "",       // traço
        _ => "",                       // interrogação
    };

    private string? _estimate;
    public string? Estimate { get => _estimate; private set { if (Set(ref _estimate, value)) OnPropertyChanged(nameof(StatusText)); } }

    private DateTime _lastSeen;
    public DateTime LastSeen { get => _lastSeen; private set { if (Set(ref _lastSeen, value)) OnPropertyChanged(nameof(StatusText)); } }

    public string StatusText
    {
        get
        {
            if (!IsConnected)
                return Level.HasValue ? "Desconectado · último valor conhecido" : "Desligado ou fora de alcance";
            if (!Level.HasValue)
                return State == PowerState.Wired
                    ? "No cabo · carregando (o teclado não informa o %)"
                    : "Conectado · o dispositivo não informa a bateria";
            var s = State switch
            {
                PowerState.Charging => "Carregando",
                PowerState.Wired => "Ligado no cabo",
                PowerState.Discharging => "Descarregando",
                PowerState.Full => "Carga completa",
                PowerState.Stable => "Estável",
                _ => "Conectado",
            };
            return string.IsNullOrEmpty(Estimate) ? s : $"{s} · {Estimate}";
        }
    }

    public ObservableCollection<BatteryPart> Parts { get; } = new();
    private bool _hasParts;
    public bool HasParts { get => _hasParts; private set => Set(ref _hasParts, value); }

    // Preferências do usuário
    private bool _isHidden;
    public bool IsHidden { get => _isHidden; set => Set(ref _isHidden, value); }

    private bool _showInTray;
    public bool ShowInTray { get => _showInTray; set => Set(ref _showInTray, value); }

    private bool _isMainTray;
    public bool IsMainTray { get => _isMainTray; set => Set(ref _isMainTray, value); }

    /// <summary>Aplica uma nova leitura, atualiza o histórico e infere carregando/descarregando.</summary>
    public void Apply(DeviceReading r, DateTime now)
    {
        Name = r.Name;
        Kind = r.Kind;
        Brand = r.Brand;
        Model = r.Model;
        Source = r.Source;
        IsConnected = r.IsConnected;
        Approximate = r.Approximate;
        Level = r.Level;
        if (r.IsConnected) LastSeen = now;

        var partsChanged = Parts.Count != r.Parts.Count ||
                           Parts.Zip(r.Parts).Any(p => p.First.Label != p.Second.Label || p.First.Level != p.Second.Level);
        if (partsChanged)
        {
            Parts.Clear();
            foreach (var p in r.Parts) Parts.Add(p);
        }
        HasParts = Parts.Count > 1;

        if (r.Level is int lvl && r.IsConnected)
        {
            if (_history.Count == 0 || _history[^1].level != lvl || (now - _history[^1].t).TotalMinutes >= 5)
                _history.Add((now, lvl));
            _history.RemoveAll(h => (now - h.t).TotalHours > 6);
        }

        State = r.ReportedState ?? InferState(now);
        Estimate = ComputeEstimate(now);
    }

    private PowerState InferState(DateTime now)
    {
        if (!IsConnected || Level is null || _history.Count == 0) return PowerState.Unknown;
        var current = _history[^1];

        // Procura a última mudança de nível nos últimos 45 min.
        for (int i = _history.Count - 2; i >= 0; i--)
        {
            var h = _history[i];
            if ((now - h.t).TotalMinutes > 45) break;
            if (h.level < current.level) return current.level >= 100 ? PowerState.Full : PowerState.Charging;
            if (h.level > current.level) return PowerState.Discharging;
        }
        if (current.level >= 100) return PowerState.Full;
        // Sem variação recente: provavelmente descarregando devagar (a maioria dos dispositivos
        // só reporta de 5 em 5% ou de 10 em 10%).
        return _history.Count > 1 ? PowerState.Stable : PowerState.Unknown;
    }

    private string? ComputeEstimate(DateTime now)
    {
        if (Level is not int lvl || _history.Count < 2) return null;
        if (State is not (PowerState.Discharging or PowerState.Charging)) return null;

        var window = _history.Where(h => (now - h.t).TotalHours <= 3).ToList();
        if (window.Count < 2) return null;
        var first = window[0];
        var last = window[^1];
        var hours = (last.t - first.t).TotalHours;
        var delta = last.level - first.level;
        if (hours < 0.15 || delta == 0) return null;
        var ratePerHour = delta / hours;

        double remainingH = State == PowerState.Charging
            ? (100 - lvl) / ratePerHour
            : lvl / -ratePerHour;
        if (remainingH <= 0 || double.IsInfinity(remainingH) || remainingH > 24 * 14) return null;

        var ts = TimeSpan.FromHours(remainingH);
        var txt = ts.TotalHours >= 24 ? $"{(int)ts.TotalDays}d {ts.Hours}h"
                : ts.TotalHours >= 1 ? $"{(int)ts.TotalHours}h {ts.Minutes:00}min"
                : $"{ts.Minutes}min";
        return State == PowerState.Charging ? $"cheio em ~{txt}" : $"~{txt} restantes";
    }

    public string TooltipLine => $"{DisplayName}: {LevelText}";
}
