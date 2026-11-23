using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MonitorBateria.Services;

public enum PanelSide { Right, Left }

public sealed class DevicePrefs
{
    public bool Hidden { get; set; }
    public bool TrayIcon { get; set; }
    public string? CustomName { get; set; }
}

public sealed class AppSettings
{
    /// <summary>Nome do monitor (ex.: \\.\DISPLAY2). Vazio = principal; "*cursor" = onde estiver o mouse.</summary>
    public string Monitor { get; set; } = "";
    public PanelSide Side { get; set; } = PanelSide.Right;
    public int RefreshSeconds { get; set; } = 30;
    public bool ShowDisconnected { get; set; } = true;
    public bool AutoHidePanel { get; set; } = true;
    public bool LowBatteryAlert { get; set; } = true;
    public int LowBatteryThreshold { get; set; } = 20;
    /// <summary>Chave do dispositivo mostrado no ícone principal. Vazio = o de menor bateria.</summary>
    public string MainTrayDevice { get; set; } = "";
    public Dictionary<string, DevicePrefs> Devices { get; set; } = new();

    /// <summary>Deixa os ícones sempre visíveis na barra (fora do menu "^").</summary>
    public bool PromoteTrayIcons { get; set; } = true;

    // Widget flutuante
    public bool ShowWidget { get; set; } = true;
    public bool WidgetVertical { get; set; }
    public bool WidgetTopmost { get; set; } = true;
    public bool WidgetShowNames { get; set; }
    /// <summary>Posição em pixels físicos (canto superior esquerdo). null = automático.</summary>
    public int? WidgetX { get; set; }
    public int? WidgetY { get; set; }

    [JsonIgnore]
    public static string Folder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MonitorBateria");
    [JsonIgnore]
    private static string FilePath => Path.Combine(Folder, "settings.json");

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public DevicePrefs For(string key)
    {
        if (!Devices.TryGetValue(key, out var p))
        {
            p = new DevicePrefs();
            Devices[key] = p;
        }
        return p;
    }

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath), Json) ?? new AppSettings();
        }
        catch { }
        return new AppSettings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Folder);
            var tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(this, Json));
            File.Move(tmp, FilePath, true);
        }
        catch { }
    }
}
