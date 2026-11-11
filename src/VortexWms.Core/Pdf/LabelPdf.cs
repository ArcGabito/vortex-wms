using PdfSharp.Drawing;
using PdfSharp.Pdf;
using VortexWms.Core.Labels;

namespace VortexWms.Core.Pdf;

/// <summary>
/// Etiquetas en PDF para impresoras comunes (fase 6c): una por página del tamaño de la etiqueta (impresoras de etiquetas con
/// su driver) o en una hoja A4 de etiquetas adhesivas (3 × 8 de 70 × 37 mm).
/// </summary>
public static class LabelPdf
{
    private const double PtPerMm = 72 / 25.4;

    public static byte[] Render(IReadOnlyList<LabelData> labels, LabelSize size, bool a4Sheet = false, int copies = 1)
    {
        FontResolver.Register();
        var all = labels.SelectMany(l => Enumerable.Repeat(l, Math.Clamp(copies, 1, 500))).ToList();
        using var doc = new PdfDocument();
        doc.Info.Title = "Vortex WMS";
        if (a4Sheet)
        {
            const int cols = 3, rows = 8;
            var w = 70 * PtPerMm;
            var h = 37 * PtPerMm;
            for (var start = 0; start < all.Count; start += cols * rows)
            {
                var page = doc.AddPage();
                page.Width = XUnit.FromMillimeter(210);
                page.Height = XUnit.FromMillimeter(297);
                using var g = XGraphics.FromPdfPage(page);
                var top = (297 - rows * 37) / 2.0 * PtPerMm;
                for (var i = 0; i < cols * rows && start + i < all.Count; i++)
                    Draw(g, all[start + i], new XRect(i % cols * w, top + i / cols * h, w, h));
            }
        }
        else
        {
            foreach (var label in all)
            {
                var page = doc.AddPage();
                page.Width = XUnit.FromMillimeter(size.WidthMm);
                page.Height = XUnit.FromMillimeter(size.HeightMm);
                using var g = XGraphics.FromPdfPage(page);
                Draw(g, label, new XRect(0, 0, size.WidthMm * PtPerMm, size.HeightMm * PtPerMm));
            }
        }
        if (doc.PageCount == 0)
            doc.AddPage();
        using var memory = new MemoryStream();
        doc.Save(memory);
        return memory.ToArray();
    }

    private static void Draw(XGraphics g, LabelData l, XRect box)
    {
        var margin = Math.Min(box.Width, box.Height) * 0.07;
        var inner = new XRect(box.X + margin, box.Y + margin, box.Width - 2 * margin, box.Height - 2 * margin);
        var scale = inner.Height / (40 * PtPerMm);
        var titleFont = new XFont(FontResolver.Family, Math.Clamp(11 * scale, 6, 16), XFontStyleEx.Bold);
        var smallFont = new XFont(FontResolver.Family, Math.Clamp(8 * scale, 5, 11), XFontStyleEx.Regular);
        var y = inner.Y;
        g.DrawString(Fit(g, l.Title, titleFont, inner.Width), titleFont, XBrushes.Black, new XRect(inner.X, y, inner.Width, titleFont.Height), XStringFormats.TopLeft);
        y += titleFont.Height;
        if (l.Subtitle is { Length: > 0 } sub)
        {
            g.DrawString(Fit(g, sub, smallFont, inner.Width), smallFont, XBrushes.Gray, new XRect(inner.X, y, inner.Width, smallFont.Height), XStringFormats.TopLeft);
            y += smallFont.Height;
        }
        if (l.Big is { Length: > 0 } big && inner.Height > 27 * PtPerMm)
        {
            var bigFont = new XFont(FontResolver.Family, Math.Clamp(22 * scale, 10, 48), XFontStyleEx.Bold);
            g.DrawString(Fit(g, big, bigFont, inner.Width), bigFont, XBrushes.Black, new XRect(inner.X, y, inner.Width, bigFont.Height), XStringFormats.TopCenter);
            y += bigFont.Height;
        }
        var footerHeight = l.Footer is { Length: > 0 } ? smallFont.Height : 0;
        var textHeight = smallFont.Height;
        var barHeight = inner.Bottom - y - footerHeight - textHeight - 2;
        if (barHeight > 8)
        {
            var modules = Code128.Modules(l.Barcode);
            var total = modules.Sum() + 20;   // zona muda de 10 módulos a cada lado
            var module = inner.Width / total;
            var x = inner.X + 10 * module;
            var bar = true;
            foreach (var m in modules)
            {
                if (bar)
                    g.DrawRectangle(XBrushes.Black, x, y + 1, m * module, barHeight);
                x += m * module;
                bar = !bar;
            }
            y += barHeight + 1;
            g.DrawString(l.Barcode, smallFont, XBrushes.Black, new XRect(inner.X, y, inner.Width, textHeight), XStringFormats.TopCenter);
        }
        if (footerHeight > 0)
            g.DrawString(l.Footer!, new XFont(FontResolver.Family, smallFont.Size, XFontStyleEx.Bold), XBrushes.Black,
                new XRect(inner.X, inner.Bottom - footerHeight, inner.Width, footerHeight), XStringFormats.TopRight);
    }

    /// <summary>Recorta el texto con "…" para que quepa en el ancho.</summary>
    private static string Fit(XGraphics g, string text, XFont font, double width)
    {
        if (g.MeasureString(text, font).Width <= width)
            return text;
        var t = text;
        while (t.Length > 1 && g.MeasureString(t + "…", font).Width > width)
            t = t[..^1];
        return t + "…";
    }
}
