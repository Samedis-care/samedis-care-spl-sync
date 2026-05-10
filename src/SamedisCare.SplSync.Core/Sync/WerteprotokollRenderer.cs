using SamedisCare.SplSync.Core.Actimed;
using SamedisCare.SplSync.Core.Config;
using SkiaSharp;

namespace SamedisCare.SplSync.Core.Sync;

/// <summary>
/// Renders the "Werteprotokoll" PNG that Samedis embeds into its final issue PDF (5.6).
///
/// Layout: A4 portrait at 200 dpi (1654 x 2339 px).
///   Header:   Logo (optional) + "Werteprotokoll" title + Test/Device/Date/Tester block
///   Body:     Table — Schritt | Soll-Bereich | Einheit | Ist-Wert | OK?
///   Footer:   Gesamtergebnis with green/red bar
/// </summary>
public class WerteprotokollRenderer
{
    private const int PageW = 1654;
    private const int PageH = 2339;
    private const int Margin = 80;

    private static readonly SKColor Black   = new(0x12, 0x12, 0x12);
    private static readonly SKColor Grey    = new(0x66, 0x66, 0x66);
    private static readonly SKColor Light   = new(0xE5, 0xE5, 0xE5);
    private static readonly SKColor Ok      = new(0x2E, 0x7D, 0x32);
    private static readonly SKColor NotOk   = new(0xC6, 0x28, 0x28);
    private static readonly SKColor White   = SKColors.White;

    private readonly BrandingConfig _branding;

    public WerteprotokollRenderer(BrandingConfig branding) => _branding = branding;

    public class TestData
    {
        public required ActimedFinishedTest Header { get; init; }
        public required IReadOnlyList<ActimedFinishedTestItem> Items { get; init; }
        public required IReadOnlyList<ActimedFinishedTestResult> Results { get; init; }
        public string? DeviceLabel { get; init; }
    }

    /// <summary>Render to PNG bytes.</summary>
    public byte[] Render(TestData data)
    {
        using var bitmap = new SKBitmap(PageW, PageH);
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(White);

        var titleFont = new SKFont(SKTypeface.FromFamilyName("Arial", SKFontStyle.Bold), 48);
        var headerFont = new SKFont(SKTypeface.FromFamilyName("Arial", SKFontStyle.Bold), 24);
        var labelFont = new SKFont(SKTypeface.FromFamilyName("Arial"), 22);
        var smallFont = new SKFont(SKTypeface.FromFamilyName("Arial"), 18);

        using var paintTitle = new SKPaint { Color = Black, IsAntialias = true };
        using var paintLabel = new SKPaint { Color = Black, IsAntialias = true };
        using var paintGrey  = new SKPaint { Color = Grey,  IsAntialias = true };
        using var paintLine  = new SKPaint { Color = Light, StrokeWidth = 1.5f, Style = SKPaintStyle.Stroke };

        // y is float so we can mix it with SkiaSharp metrics (font.Size etc.) without casting.
        float y = Margin;

        // Logo (optional)
        if (!string.IsNullOrWhiteSpace(_branding.LogoPath) && File.Exists(_branding.LogoPath))
        {
            try
            {
                using var logo = SKBitmap.Decode(_branding.LogoPath);
                if (logo != null)
                {
                    var maxLogoH = 120;
                    var ratio = (float)logo.Width / logo.Height;
                    var h = Math.Min(maxLogoH, logo.Height);
                    var w = (int)(h * ratio);
                    var dst = new SKRect(Margin, y, Margin + w, y + h);
                    canvas.DrawBitmap(logo, dst);
                }
            }
            catch { /* logo missing or invalid format — skip silently */ }
        }

        // Title (right-aligned to logo row)
        canvas.DrawText("Werteprotokoll", PageW - Margin - 360, y + 56, titleFont, paintTitle);
        if (!string.IsNullOrWhiteSpace(_branding.ServiceProviderName))
            canvas.DrawText(_branding.ServiceProviderName, PageW - Margin - 360, y + 92, smallFont, paintGrey);

        y += 160;
        canvas.DrawLine(Margin, y, PageW - Margin, y, paintLine);
        y += 30;

        // Header block: 2 columns
        var col1X = Margin;
        var col2X = PageW / 2 + 20;
        DrawKv(canvas, "Prüfberichtsnummer", data.Header.Pruefberichtsnummer, col1X, y, headerFont, labelFont, paintLabel, paintGrey);
        DrawKv(canvas, "Prüfdatum", data.Header.TestDate.ToString("dd.MM.yyyy"), col2X, y, headerFont, labelFont, paintLabel, paintGrey);
        y += 70;
        DrawKv(canvas, "Gerät", data.DeviceLabel ?? data.Header.DevId.ToString(), col1X, y, headerFont, labelFont, paintLabel, paintGrey);
        DrawKv(canvas, "Prüfer", data.Header.TesterName, col2X, y, headerFont, labelFont, paintLabel, paintGrey);
        y += 70;
        DrawKv(canvas, "Prüfvorschrift", data.Header.PvsName, col1X, y, headerFont, labelFont, paintLabel, paintGrey);
        y += 70;

        canvas.DrawLine(Margin, y, PageW - Margin, y, paintLine);
        y += 30;

        // Table header
        var colSchritt = Margin;
        var colSoll    = Margin + 700;
        var colEinheit = Margin + 950;
        var colIst     = Margin + 1100;
        var colOk      = PageW - Margin - 80;

        canvas.DrawText("Prüfschritt",  colSchritt, y, headerFont, paintLabel);
        canvas.DrawText("Soll-Bereich", colSoll,    y, headerFont, paintLabel);
        canvas.DrawText("Einheit",      colEinheit, y, headerFont, paintLabel);
        canvas.DrawText("Ist-Wert",     colIst,     y, headerFont, paintLabel);
        canvas.DrawText("OK",           colOk,      y, headerFont, paintLabel);
        y += 12;
        canvas.DrawLine(Margin, y, PageW - Margin, y, paintLine);
        y += 32;

        // Build a quick lookup item-id -> ws_dscr
        var itemById = data.Items.ToDictionary(i => i.TestItemId, i => i);

        foreach (var r in data.Results)
        {
            if (y > PageH - 200)
            {
                // Spillover: draw "fortgesetzt..." marker — for now we just stop.
                canvas.DrawText("(weitere Schritte abgeschnitten)", Margin, y, smallFont, paintGrey);
                y += 30;
                break;
            }

            var step = itemById.TryGetValue(r.TestItemId, out var item) ? item.WsDscr : r.ItemDscr;
            var soll = FormatSoll(r.Limit1, r.Limit2);
            var einheit = r.Unit ?? "";
            var ist = r.Value ?? "";

            DrawWrapped(canvas, step, colSchritt, y, colSoll - colSchritt - 20, smallFont, paintLabel, out var stepBottom);
            canvas.DrawText(soll,    colSoll,    y, smallFont, paintLabel);
            canvas.DrawText(einheit, colEinheit, y, smallFont, paintLabel);
            canvas.DrawText(ist,     colIst,     y, smallFont, paintLabel);

            // OK-Symbol als Vektor zeichnen (kein Font-Lookup, daher keine "Tofu"-Quadrate
            // wenn Arial ✓/✗ nicht hat). Mittig in der OK-Spalte, gleiche Hoehe wie Text.
            DrawCheckOrCross(canvas, r.Success, colOk, y - 18, 26, r.Success ? Ok : NotOk);

            y = Math.Max(y, stepBottom) + 18;
        }

        y += 20;
        canvas.DrawLine(Margin, y, PageW - Margin, y, paintLine);
        y += 30;

        // Footer: Gesamtergebnis
        var passed = ResultMapping.FromActimed(data.Header.Pruefergebnis) ?? "unbekannt";
        var color = passed switch
        {
            "passed"               => Ok,
            "passed_conditionally" => new SKColor(0xF9, 0xA8, 0x25),
            "not_passed"           => NotOk,
            _                      => Grey
        };
        using (var paintBox = new SKPaint { Color = color, IsAntialias = true })
        {
            canvas.DrawRect(new SKRect(Margin, y, PageW - Margin, y + 80), paintBox);
        }
        using (var paintText = new SKPaint { Color = White, IsAntialias = true })
        {
            canvas.DrawText($"Gesamtergebnis: {data.Header.Pruefergebnis}", Margin + 30, y + 50, headerFont, paintText);
        }

        // Encode
        using var image = SKImage.FromBitmap(bitmap);
        using var encoded = image.Encode(SKEncodedImageFormat.Png, 95);
        return encoded.ToArray();
    }

    /// <summary>Render to a file path; returns the path.</summary>
    public string RenderToFile(TestData data, string outputPath)
    {
        var dir = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        File.WriteAllBytes(outputPath, Render(data));
        return outputPath;
    }

    private static void DrawKv(SKCanvas canvas, string key, string value, float x, float y,
        SKFont keyFont, SKFont valFont, SKPaint paintLabel, SKPaint paintGrey)
    {
        canvas.DrawText(key, x, y, valFont, paintGrey);
        canvas.DrawText(string.IsNullOrWhiteSpace(value) ? "—" : value, x, y + 36, keyFont, paintLabel);
    }

    /// <summary>
    /// Zeichnet ein Häkchen (success=true) oder ein X (success=false) als reine Vektor-Form.
    /// Unabhängig von verfügbaren Fonts — wir hatten zuvor "✓" / "✗" via DrawText, was bei
    /// fehlenden Glyphen in Arial als leere Quadrate (.notdef / Tofu) gerendert wurde.
    /// </summary>
    /// <param name="x">Linke obere Ecke der Bounding Box.</param>
    /// <param name="y">Linke obere Ecke der Bounding Box.</param>
    /// <param name="size">Kantenlänge der Bounding Box (Symbol ist quadratisch).</param>
    private static void DrawCheckOrCross(SKCanvas canvas, bool success, float x, float y, float size, SKColor color)
    {
        using var paint = new SKPaint
        {
            Color = color,
            IsAntialias = true,
            StrokeWidth = Math.Max(2f, size * 0.18f),
            Style = SKPaintStyle.Stroke,
            StrokeCap = SKStrokeCap.Round,
            StrokeJoin = SKStrokeJoin.Round
        };

        using var path = new SKPath();
        if (success)
        {
            // Häkchen: kurzer Aufstrich von links-unten zur Mitte, dann lang nach rechts-oben.
            path.MoveTo(x + size * 0.15f, y + size * 0.55f);
            path.LineTo(x + size * 0.42f, y + size * 0.80f);
            path.LineTo(x + size * 0.88f, y + size * 0.22f);
        }
        else
        {
            // X: zwei diagonale Linien.
            path.MoveTo(x + size * 0.18f, y + size * 0.18f);
            path.LineTo(x + size * 0.82f, y + size * 0.82f);
            path.MoveTo(x + size * 0.82f, y + size * 0.18f);
            path.LineTo(x + size * 0.18f, y + size * 0.82f);
        }
        canvas.DrawPath(path, paint);
    }

    private static void DrawWrapped(SKCanvas canvas, string text, float x, float y, float maxWidth,
        SKFont font, SKPaint paint, out float bottomY)
    {
        bottomY = y;
        if (string.IsNullOrEmpty(text)) return;

        var words = text.Split(' ');
        var line = string.Empty;
        var lineH = font.Size + 6;

        foreach (var w in words)
        {
            var trial = string.IsNullOrEmpty(line) ? w : line + " " + w;
            // SkiaSharp 2.88.8 only exposes SKFont.MeasureText(ReadOnlySpan<ushort> glyphs).
            // Cleanest stable substitute is SKTextBlob.Create — gives us bounds.Width.
            var width = MeasureWidth(font, trial);
            if (width > maxWidth && !string.IsNullOrEmpty(line))
            {
                canvas.DrawText(line, x, y, font, paint);
                y += lineH;
                line = w;
            }
            else
            {
                line = trial;
            }
        }
        if (!string.IsNullOrEmpty(line))
        {
            canvas.DrawText(line, x, y, font, paint);
            y += lineH;
        }
        bottomY = y;
    }

    /// <summary>
    /// Measures the rendered width of <paramref name="text"/> in <paramref name="font"/>'s metrics.
    /// Implemented via SKTextBlob because in SkiaSharp 2.88.8 the convenience MeasureText overloads
    /// are obsolete-with-error, leaving only the glyph-id (ReadOnlySpan&lt;ushort&gt;) overload usable
    /// — which we don't want to feed by hand for arbitrary UTF-16 input.
    /// </summary>
    private static float MeasureWidth(SKFont font, string text)
    {
        if (string.IsNullOrEmpty(text)) return 0f;
        using var blob = SKTextBlob.Create(text, font);
        return blob?.Bounds.Width ?? text.Length * font.Size * 0.55f;
    }

    private static string FormatSoll(string? limit1, string? limit2)
    {
        var l1 = OaDate.TryParseDecimalLoose(limit1);
        var l2 = OaDate.TryParseDecimalLoose(limit2);
        if (!l1.HasValue && !l2.HasValue) return "—";
        if (l1.HasValue && l2.HasValue && Math.Abs(l1.Value - l2.Value) < 1e-9)
            return OaDate.FormatDe(l1.Value);
        if (l1.HasValue && l2.HasValue)
            return $"{OaDate.FormatDe(Math.Min(l1.Value, l2.Value))} … {OaDate.FormatDe(Math.Max(l1.Value, l2.Value))}";
        return l1.HasValue ? OaDate.FormatDe(l1.Value) : OaDate.FormatDe(l2!.Value);
    }
}
