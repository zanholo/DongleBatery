using System;
using System.Linq;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using MonitorBateria.Models;

namespace MonitorBateria.Services;

/// <summary>Desenha os ícones da bandeja em tempo real (nível em número / barra).</summary>
public static class TrayIconRenderer
{
    public static int IconSize
    {
        get
        {
            try
            {
                uint dpi = Native.DpiForPoint(0, 0);
                var s = Native.GetSystemMetricsForDpi(Native.SM_CXSMICON, dpi);
                return s > 0 ? s : 16;
            }
            catch { return 16; }
        }
    }

    public static Color LevelColor(int? level, bool charging, bool lightTaskbar)
    {
        if (charging) return lightTaskbar ? Color.FromArgb(0, 95, 184) : Color.FromArgb(76, 194, 255);
        return level switch
        {
            null => lightTaskbar ? Color.FromArgb(110, 110, 110) : Color.FromArgb(160, 160, 160),
            <= 15 => lightTaskbar ? Color.FromArgb(196, 43, 28) : Color.FromArgb(255, 99, 99),
            <= 30 => lightTaskbar ? Color.FromArgb(157, 93, 0) : Color.FromArgb(252, 185, 0),
            _ => lightTaskbar ? Color.FromArgb(15, 123, 15) : Color.FromArgb(108, 203, 95),
        };
    }

    /// <summary>Ícone de um dispositivo: porcentagem em número + barra colorida embaixo.</summary>
    public static Icon RenderDevice(BatteryDevice d, bool lightTaskbar, out IntPtr handle)
    {
        int size = IconSize;
        using var bmp = new Bitmap(size, size);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            g.Clear(Color.Transparent);

            var fg = lightTaskbar ? Color.FromArgb(20, 20, 20) : Color.White;
            var accent = LevelColor(d.Level, d.IsCharging, lightTaskbar);
            if (d.Level is int l && l <= 15 && !d.IsCharging) fg = accent;

            int barH = Math.Max(2, size / 8);
            if (d.Level is int lvl && d.IsConnected)
            {
                string text = lvl >= 100 ? "100" : lvl.ToString();
                bool chg = d.IsCharging;
                float fontPx = chg ? (text.Length >= 3 ? size * 0.44f : size * 0.60f)
                                   : (text.Length >= 3 ? size * 0.60f : size * 0.80f);
                using var font = new Font(PickFont(), fontPx, FontStyle.Bold, GraphicsUnit.Pixel);
                using var brush = new SolidBrush(fg);
                // Carregando: número à esquerda e um raio à direita.
                var area = chg ? new RectangleF(-2, -size * 0.06f, size * 0.66f + 2, size - barH + 1)
                               : new RectangleF(-2, -size * 0.06f, size + 4, size - barH + 1);
                if (chg) DrawBolt(g, new RectangleF(size * 0.62f, 0, size * 0.38f, size - barH - 1), accent, lightTaskbar);
                using var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center, FormatFlags = StringFormatFlags.NoWrap | StringFormatFlags.NoClip, Trimming = StringTrimming.None };
                g.DrawString(text, font, brush, area, sf);

                // barra de nível
                using var track = new SolidBrush(Color.FromArgb(lightTaskbar ? 60 : 80, fg));
                g.FillRectangle(track, 0, size - barH, size, barH);
                using var fill = new SolidBrush(accent);
                g.FillRectangle(fill, 0, size - barH, Math.Max(1, size * lvl / 100f), barH);
            }
            else
            {
                // sem leitura: glyph do tipo de dispositivo em cinza
                using var font = new Font("Segoe Fluent Icons", size * 0.78f, FontStyle.Regular, GraphicsUnit.Pixel);
                using var brush = new SolidBrush(Color.FromArgb(150, fg));
                using var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
                g.DrawString(DeviceClassifier.GlyphFor(d.Kind), font, brush, new RectangleF(0, 0, size, size), sf);
            }
        }
        handle = bmp.GetHicon();
        return Icon.FromHandle(handle);
    }

    /// <summary>Ícone principal: uma bateria preenchida conforme o nível.</summary>
    public static Icon RenderMain(int? level, bool charging, bool lightTaskbar, out IntPtr handle)
    {
        int size = IconSize;
        using var bmp = new Bitmap(size, size);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);
            var fg = lightTaskbar ? Color.FromArgb(25, 25, 25) : Color.FromArgb(240, 240, 240);
            var accent = LevelColor(level, charging, lightTaskbar);

            float s = size / 16f;
            float stroke = Math.Max(1f, 1.15f * s);
            var body = new RectangleF(1f * s, 4.5f * s, 12.2f * s, 7.5f * s);
            using (var pen = new Pen(fg, stroke))
            using (var path = Rounded(body, 1.6f * s))
                g.DrawPath(pen, path);
            using (var cap = new SolidBrush(fg))
                g.FillRectangle(cap, 13.6f * s, 6.6f * s, 1.6f * s, 3.3f * s);

            if (level is int l)
            {
                float pad = 1.9f * s;
                var inner = new RectangleF(body.X + pad, body.Y + pad, (body.Width - pad * 2) * Math.Clamp(l, 4, 100) / 100f, body.Height - pad * 2);
                using var fill = new SolidBrush(accent);
                using var p2 = Rounded(inner, 0.6f * s);
                g.FillPath(fill, p2);
            }

            if (charging)
            {
                // pequeno raio
                var bolt = new[]
                {
                    new PointF(8.4f * s, 2.2f * s), new PointF(4.8f * s, 8.8f * s), new PointF(7.4f * s, 8.8f * s),
                    new PointF(6.2f * s, 14.2f * s), new PointF(10.6f * s, 7.2f * s), new PointF(7.9f * s, 7.2f * s),
                };
                using var outline = new Pen(lightTaskbar ? Color.White : Color.FromArgb(32, 32, 32), Math.Max(1f, 1.4f * s)) { LineJoin = LineJoin.Round };
                g.DrawPolygon(outline, bolt);
                using var bb = new SolidBrush(accent);
                g.FillPolygon(bb, bolt);
            }
        }
        handle = bmp.GetHicon();
        return Icon.FromHandle(handle);
    }

    /// <summary>Raio (símbolo de carregando) encaixado no retângulo, com contorno para destacar.</summary>
    private static void DrawBolt(Graphics g, RectangleF r, Color color, bool lightTaskbar)
    {
        // Raio em coordenadas 0..1 (largura × altura)
        (float x, float y)[] shape = { (0.70f, 0f), (0.05f, 0.58f), (0.45f, 0.58f), (0.30f, 1f), (0.95f, 0.40f), (0.55f, 0.40f) };
        var pts = shape.Select(p => new PointF(r.X + p.x * r.Width, r.Y + p.y * r.Height)).ToArray();
        using var outline = new Pen(lightTaskbar ? Color.White : Color.FromArgb(32, 32, 32), Math.Max(1f, r.Width / 7f)) { LineJoin = LineJoin.Round };
        g.DrawPolygon(outline, pts);
        using var fill = new SolidBrush(color);
        g.FillPolygon(fill, pts);
    }

    private static string? _font;
    private static string PickFont()
    {
        if (_font != null) return _font;
        using var fonts = new InstalledFontCollection();
        foreach (var f in fonts.Families)
            if (f.Name == "Bahnschrift SemiCondensed") return _font = f.Name;
        foreach (var f in fonts.Families)
            if (f.Name == "Bahnschrift") return _font = f.Name;
        return _font = "Segoe UI";
    }

    private static GraphicsPath Rounded(RectangleF r, float radius)
    {
        var p = new GraphicsPath();
        float d = Math.Min(radius * 2, Math.Min(r.Width, r.Height));
        if (d <= 0.5f) { p.AddRectangle(r); return p; }
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }
}
