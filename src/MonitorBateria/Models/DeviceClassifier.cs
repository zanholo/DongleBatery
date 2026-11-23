using System;
using System.Collections.Generic;
using System.Linq;

namespace MonitorBateria.Models;

/// <summary>
/// Descobre o tipo, a marca e o ícone de um dispositivo a partir do nome,
/// das categorias do contêiner do Windows e do Class of Device do Bluetooth.
/// </summary>
public static class DeviceClassifier
{
    // Glyphs da fonte "Segoe Fluent Icons" (Windows 11) / "Segoe MDL2 Assets" (Windows 10).
    public static string GlyphFor(DeviceKind kind) => kind switch
    {
        DeviceKind.Mouse => "",
        DeviceKind.Keyboard => "",
        DeviceKind.Gamepad => "",
        DeviceKind.Headphones => "",
        DeviceKind.Headset => "",
        DeviceKind.Earbuds => "", // na UI os fones intra-auriculares usam um desenho vetorial próprio
        DeviceKind.Speaker => "",
        DeviceKind.Phone => "",
        DeviceKind.Tablet => "",
        DeviceKind.Pen => "",
        DeviceKind.Watch => "",
        DeviceKind.Touchpad => "",
        DeviceKind.Remote => "",
        DeviceKind.Laptop => "",
        _ => "", // Bluetooth
    };

    public static string LabelFor(DeviceKind kind) => kind switch
    {
        DeviceKind.Mouse => "Mouse",
        DeviceKind.Keyboard => "Teclado",
        DeviceKind.Gamepad => "Controle",
        DeviceKind.Headphones => "Headphone",
        DeviceKind.Headset => "Headset",
        DeviceKind.Earbuds => "Fone intra-auricular",
        DeviceKind.Speaker => "Caixa de som",
        DeviceKind.Phone => "Celular",
        DeviceKind.Tablet => "Tablet",
        DeviceKind.Pen => "Caneta",
        DeviceKind.Watch => "Relógio",
        DeviceKind.Touchpad => "Touchpad",
        DeviceKind.Remote => "Controle remoto",
        DeviceKind.Laptop => "Bateria do PC",
        _ => "Dispositivo sem fio",
    };

    private static readonly (string[] keys, DeviceKind kind)[] NameRules =
    {
        (new[] { "buds", "airpods", "earbud", "freebuds", "wf-", "tws", "soundcore liberty", "pixel buds", "beats fit", "powerbeats", "jabra elite" }, DeviceKind.Earbuds),
        (new[] { "controller", "gamepad", "dualsense", "dualshock", "joy-con", "joycon", "8bitdo", "xbox", "pro controller", "stadia", "edge controller" }, DeviceKind.Gamepad),
        (new[] { "mouse", "mx master", "mx anywhere", "mx ergo", "trackball", "deathadder", "viper", "basilisk", "g305", "g502", "g703", "g903", "pro x superlight", "superlight", "orochi", "pebble m", "m720", "m590", "m650", "lift" }, DeviceKind.Mouse),
        (new[] { "keyboard", "teclado", "keychron", "mx keys", "k380", "k780", "k850", "nuphy", "magic keyboard", "pop keys", "craft" }, DeviceKind.Keyboard),
        (new[] { "trackpad", "touchpad", "magic trackpad" }, DeviceKind.Touchpad),
        (new[] { "headset", "arctis", "hyperx cloud", "blackshark", "barracuda", "kraken", "virtuoso", "hs80", "g pro x", "g733", "g435" }, DeviceKind.Headset),
        (new[] { "headphone", "wh-", "xm3", "xm4", "xm5", "quietcomfort", "qc35", "qc45", "noise cancelling", "momentum", "studio", "solo", "tune 7", "live 6", "space one", "major iv", "major v" }, DeviceKind.Headphones),
        (new[] { "speaker", "soundlink", "jbl flip", "jbl charge", "jbl go", "jbl clip", "boombox", "megaboom", "wonderboom", "echo", "homepod", "soundbar" }, DeviceKind.Speaker),
        (new[] { "pen", "pencil", "stylus", "caneta", "s pen" }, DeviceKind.Pen),
        (new[] { "watch", "relógio", "band", "fitbit", "amazfit", "garmin" }, DeviceKind.Watch),
        (new[] { "iphone", "galaxy s", "galaxy a", "galaxy z", "galaxy note", "pixel", "redmi", "moto ", "motorola", "xiaomi", "poco", "phone" }, DeviceKind.Phone),
        (new[] { "ipad", "galaxy tab", "tablet" }, DeviceKind.Tablet),
        (new[] { "remote", "controle remoto", "presenter", "spotlight", "r500", "r800" }, DeviceKind.Remote),
    };

    private static readonly (string[] keys, string brand)[] BrandRules =
    {
        (new[] { "pixel", "google", "stadia" }, "Google"),
        (new[] { "huawei", "freebuds" }, "Huawei"),
        (new[] { "galaxy", "samsung", "s pen" }, "Samsung"),
        (new[] { "airpods", "iphone", "ipad", "magic ", "apple", "pencil" }, "Apple"),
        (new[] { "beats", "powerbeats" }, "Beats"),
        (new[] { "xbox", "surface", "microsoft" }, "Microsoft"),
        (new[] { "dualsense", "dualshock", "wh-", "wf-", "sony", "playstation" }, "Sony"),
        (new[] { "logitech", "logi ", "mx master", "mx keys", "mx anywhere", "mx ergo", "lift", "pop keys", "g305", "g502", "g703", "g903", "g733", "g435", "superlight", "k380", "k780", "m720", "m590", "m650", "craft" }, "Logitech"),
        (new[] { "razer", "deathadder", "viper", "basilisk", "orochi", "blackshark", "barracuda", "kraken" }, "Razer"),
        (new[] { "keychron" }, "Keychron"),
        (new[] { "nuphy" }, "NuPhy"),
        (new[] { "jbl" }, "JBL"),
        (new[] { "bose", "quietcomfort", "soundlink" }, "Bose"),
        (new[] { "8bitdo" }, "8BitDo"),
        (new[] { "joy-con", "joycon", "pro controller", "nintendo" }, "Nintendo"),
        (new[] { "corsair", "virtuoso", "hs80" }, "Corsair"),
        (new[] { "hyperx" }, "HyperX"),
        (new[] { "arctis", "steelseries" }, "SteelSeries"),
        (new[] { "jabra" }, "Jabra"),
        (new[] { "sennheiser", "momentum" }, "Sennheiser"),
        (new[] { "soundcore", "anker" }, "Anker"),
        (new[] { "xiaomi", "redmi", "poco", "mi band" }, "Xiaomi"),
        (new[] { "motorola", "moto " }, "Motorola"),
        (new[] { "marshall" }, "Marshall"),
        (new[] { "edifier" }, "Edifier"),
        (new[] { "qcy" }, "QCY"),
        (new[] { "garmin" }, "Garmin"),
        (new[] { "amazfit" }, "Amazfit"),
        (new[] { "buds" }, "Samsung"),
    };

    // Chaves curtas (ex.: "pen", "band", "echo") só casam no início de uma palavra, para evitar falsos positivos ("open" ≠ "pen").
    private static bool Matches(string text, string key)
    {
        var k = key.Trim();
        if (k.Length > 4 || key.EndsWith(' ') || key.EndsWith('-')) return text.Contains(key);
        return System.Text.RegularExpressions.Regex.IsMatch(text, $@"(^|[^a-z0-9]){System.Text.RegularExpressions.Regex.Escape(k)}");
    }

    public static DeviceKind Classify(string? name, IEnumerable<string>? categories, uint? classOfDevice)
    {
        var n = (name ?? string.Empty).ToLowerInvariant();

        // 1) Categoria informada pelo Windows (mais confiável)
        var cats = (categories ?? Array.Empty<string>()).Select(c => c.ToLowerInvariant()).ToList();
        DeviceKind? fromCategory = null;
        foreach (var c in cats)
        {
            if (c.Contains("earbud")) { fromCategory = DeviceKind.Earbuds; break; }
            if (c.Contains("gamepad") || c.Contains("gaming")) { fromCategory = DeviceKind.Gamepad; break; }
            if (c.Contains("mouse")) { fromCategory = DeviceKind.Mouse; break; }
            if (c.Contains("keyboard")) { fromCategory = DeviceKind.Keyboard; break; }
            if (c.Contains("pen")) { fromCategory = DeviceKind.Pen; break; }
            if (c.Contains("touchpad") || c.Contains("trackpad")) { fromCategory = DeviceKind.Touchpad; break; }
            if (c.Contains("headphone")) { fromCategory = DeviceKind.Headphones; break; }
            if (c.Contains("speaker")) { fromCategory = DeviceKind.Speaker; break; }
            if (c.Contains("phone") && !c.Contains("headphone")) { fromCategory = DeviceKind.Phone; break; }
            if (c.Contains("watch") || c.Contains("wearable")) { fromCategory = DeviceKind.Watch; break; }
            if (c.Contains("headset")) { fromCategory ??= DeviceKind.Headset; }
        }

        // 2) Nome / modelo
        DeviceKind? fromName = null;
        foreach (var (keys, kind) in NameRules)
        {
            if (keys.Any(k => Matches(n, k))) { fromName = kind; break; }
        }

        // "Headset" genérico do Windows é usado também para fones intra-auriculares e headphones;
        // nesse caso o nome costuma ser mais preciso.
        if (fromCategory == DeviceKind.Headset && fromName is DeviceKind.Earbuds or DeviceKind.Headphones)
            return fromName.Value;
        if (fromCategory is not null) return fromCategory.Value;
        if (fromName is not null) return fromName.Value;

        // 3) Bluetooth Class of Device (major/minor)
        if (classOfDevice is uint cod)
        {
            uint major = (cod >> 8) & 0x1F;
            uint minor = (cod >> 2) & 0x3F;
            switch (major)
            {
                case 0x02: return DeviceKind.Phone;
                case 0x04: // Audio/Video
                    return minor switch
                    {
                        0x01 or 0x02 => DeviceKind.Headset,
                        0x06 => DeviceKind.Headphones,
                        0x05 or 0x07 => DeviceKind.Speaker,
                        _ => DeviceKind.Headphones,
                    };
                case 0x05: // Peripheral
                    {
                        uint kb = (minor >> 4) & 0x3;
                        uint sub = minor & 0x0F;
                        if (sub is 0x01 or 0x02) return DeviceKind.Gamepad;
                        if (sub == 0x03) return DeviceKind.Remote;
                        if (sub == 0x05) return DeviceKind.Pen;
                        if (kb == 0x1) return DeviceKind.Keyboard;
                        if (kb == 0x2) return DeviceKind.Mouse;
                        if (kb == 0x3) return DeviceKind.Keyboard;
                        break;
                    }
                case 0x07: return DeviceKind.Watch;
            }
        }

        return DeviceKind.Generic;
    }

    public static string? DetectBrand(string? name, string? manufacturer)
    {
        var n = (name ?? string.Empty).ToLowerInvariant();
        foreach (var (keys, brand) in BrandRules)
            if (keys.Any(k => Matches(n, k))) return brand;

        if (!string.IsNullOrWhiteSpace(manufacturer))
        {
            var m = manufacturer.Trim();
            // fabricantes genéricos que não ajudam
            if (m.Equals("Microsoft", StringComparison.OrdinalIgnoreCase) && !n.Contains("xbox") && !n.Contains("surface"))
                return null;
            if (m.StartsWith("(") || m.Contains("Padrão") || m.Contains("Standard")) return null;
            return m;
        }
        return null;
    }
}
