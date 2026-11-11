using System.Globalization;
using MigraDoc.DocumentObjectModel;
using MigraDoc.DocumentObjectModel.Tables;
using MigraDoc.Rendering;
using VortexWms.Core.Services;
using static VortexWms.Core.Texts.CoreText;

namespace VortexWms.Core.Pdf;

/// <summary>Cualquier reporte del almacén en PDF (A4, horizontal si tiene muchas columnas), con su fila de totales.</summary>
public static class ReportPdf
{
    private static readonly Color Accent = new(0xEA, 0x58, 0x0C);
    private static readonly Color Light = new(0xF3, 0xF4, 0xF6);
    private static readonly Color Muted = new(0x6B, 0x72, 0x80);

    public static byte[] Render(WmsReport report, string company, CultureInfo culture)
    {
        FontResolver.Register();
        var doc = new Document();
        doc.Info.Title = report.Title;
        doc.Info.Author = company;
        var normal = doc.Styles[StyleNames.Normal]!;
        normal.Font.Name = FontResolver.Family;
        normal.Font.Size = 8;

        var section = doc.AddSection();
        section.PageSetup = doc.DefaultPageSetup.Clone();
        section.PageSetup.PageFormat = PageFormat.A4;
        section.PageSetup.Orientation = report.Columns.Count > 7 ? Orientation.Landscape : Orientation.Portrait;
        section.PageSetup.TopMargin = section.PageSetup.BottomMargin = Unit.FromCentimeter(1.5);
        section.PageSetup.LeftMargin = section.PageSetup.RightMargin = Unit.FromCentimeter(1.5);

        var title = section.AddParagraph(company);
        title.Format.Font.Size = 14;
        title.Format.Font.Bold = true;
        title.Format.Font.Color = Accent;
        var sub = section.AddParagraph($"{report.Title} · {report.Subtitle}{(report.CurrencyCode is { } cur ? $" · {cur}" : "")}");
        sub.Format.Font.Color = Muted;
        sub.Format.SpaceAfter = Unit.FromPoint(10);

        var width = (section.PageSetup.Orientation == Orientation.Landscape ? 29.7 : 21.0) - 3.0;
        var weights = report.Columns.Select(c => c.Type == ReportColumnType.Text ? 2.0 : 1.0).ToArray();
        var table = section.AddTable();
        table.Borders.Bottom.Width = 0.4;
        table.Borders.Bottom.Color = Light;
        foreach (var w in weights)
            table.AddColumn(Unit.FromCentimeter(width * w / weights.Sum()));

        var head = table.AddRow();
        head.HeadingFormat = true;
        head.Shading.Color = Light;
        head.Format.Font.Bold = true;
        for (var i = 0; i < report.Columns.Count; i++)
            Cell(head.Cells[i], report.Columns[i].Header, report.Columns[i].Type);

        foreach (var row in report.Rows)
            AddRow(table, report, row, culture, bold: false);
        if (report.Totals is { } totals)
            AddRow(table, report, totals, culture, bold: true);
        if (report.Rows.Count == 0)
            section.AddParagraph(T("reportes.sin_datos")).Format.SpaceBefore = Unit.FromPoint(8);

        var footer = section.Footers.Primary.AddParagraph("Vortex WMS");
        footer.Format.Alignment = ParagraphAlignment.Center;
        footer.Format.Font.Size = 7;
        footer.Format.Font.Color = Muted;

        var renderer = new PdfDocumentRenderer { Document = doc };
        renderer.RenderDocument();
        using var memory = new MemoryStream();
        renderer.PdfDocument.Save(memory);
        return memory.ToArray();
    }

    private static void AddRow(Table table, WmsReport report, object?[] values, CultureInfo culture, bool bold)
    {
        var row = table.AddRow();
        row.Format.Font.Bold = bold;
        for (var i = 0; i < report.Columns.Count && i < values.Length; i++)
            Cell(row.Cells[i], values[i] is null && !bold ? "—" : values[i] is null ? "" : ReportService.Format(values[i], report.Columns[i].Type, culture),
                report.Columns[i].Type);
    }

    private static void Cell(Cell cell, string text, ReportColumnType type)
    {
        var p = cell.AddParagraph(text);
        if (type != ReportColumnType.Text)
            p.Format.Alignment = ParagraphAlignment.Right;
    }
}
