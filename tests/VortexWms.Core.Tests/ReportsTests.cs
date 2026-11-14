using System.Globalization;
using VortexWms.Core.Domain;
using VortexWms.Core.Integration;
using VortexWms.Core.Pdf;
using VortexWms.Core.Services;

namespace VortexWms.Core.Tests;

/// <summary>Fase 6b: reportes del almacén con exportación a CSV y PDF.</summary>
public sealed class ReportsTests : IAsyncLifetime
{
    private static readonly DateOnly From = new(2026, 11, 1), To = new(2026, 11, 30);
    private readonly TestWorkspace _t = new("AAAA");

    public async Task InitializeAsync()
    {
        await _t.InitializeAsync();
        _t.Clock.Advance(TimeSpan.FromMinutes(1));
        await _t.Wms.Receipts.ReceiveDirectAsync(_t.Central, null, [new DocumentLine(_t.Coffee, 100, 6m), new DocumentLine(_t.Sugar, 10, 2m)]);
        _t.Clock.Advance(TimeSpan.FromDays(2));
        await _t.Wms.SyncHandlers.HandleSaleCompletedAsync(TestWorkspace.Event(SuiteEvents.SaleCompleted, new
        {
            saleId = Guid.NewGuid(), number = "B001-1", siteId = _t.Central,
            lines = new[] { new { itemId = _t.Coffee, quantity = 60m, unitPrice = 10m, discount = 0m, total = 600m } },
        }) with { OccurredAt = _t.Clock.GetUtcNow() });
    }

    public Task DisposeAsync() => _t.DisposeAsync();

    private Task<WmsReport> Report(string kind, int days = 60) => _t.Wms.Reports.GetAsync(new ReportQuery(kind, _t.Central, From, To, days));

    private static object? Cell(WmsReport r, string sku, int column) => r.Rows.Single(x => (string?)x[0] == sku)[column];

    [Fact]
    public async Task Rotacion_abc_y_kardex_valorizado_del_periodo()
    {
        var rotation = await Report(ReportKinds.Rotation);
        Assert.Equal((60m, 360m, 20m, 3m, "A"), ((decimal)Cell(rotation, "CAFE-250", 3)!, (decimal)Cell(rotation, "CAFE-250", 4)!,
            (decimal)Cell(rotation, "CAFE-250", 5)!, (decimal)Cell(rotation, "CAFE-250", 6)!, (string)Cell(rotation, "CAFE-250", 8)!));
        Assert.Equal("C", Cell(rotation, "AZU-1K", 8));

        var kardex = await Report(ReportKinds.Kardex);
        var row = kardex.Rows.Single(x => (string?)x[0] == "CAFE-250");
        Assert.Equal(new object?[] { "CAFE-250", row[1], 0m, 0m, 100m, 600m, 60m, 360m, 40m, 240m }, row);
        Assert.Equal(260m, kardex.Totals![9]);   // 240 café + 20 azúcar

        var sites = await Report(ReportKinds.Sites);
        Assert.Equal(260m, sites.Rows.Single()[3]);

        var xlsx = ReportService.ToXlsx(kardex, "Vortex SAC");
        using (var zip = new System.IO.Compression.ZipArchive(new MemoryStream(xlsx)))
        {
            var sheet = System.Xml.Linq.XDocument.Load(zip.GetEntry("xl/worksheets/sheet1.xml")!.Open());
            var values = sheet.Descendants().Where(e => e.Name.LocalName == "c").Select(e => e.Value).ToList();
            Assert.Contains("CAFE-250", values);
            Assert.Contains("240", values);   // valor final del café, como número
        }
        var csv = ReportService.ToCsv(kardex);
        Assert.Contains("CAFE-250", csv);
        var pdf = ReportPdf.Render(kardex, "Vortex SAC", CultureInfo.GetCultureInfo("es-PE"));
        Assert.Equal("%PDF", System.Text.Encoding.ASCII.GetString(pdf, 0, 4));
    }

    [Fact]
    public async Task Exactitud_productividad_y_sin_movimiento()
    {
        var count = await _t.Wms.Counts.CreateAsync(new CountDraft(_t.Central, CountScopes.Items, ItemIds: [_t.Coffee, _t.Sugar], RecountPercent: null));
        await _t.Wms.Counts.RecordAsync(count, 1, 40);
        await _t.Wms.Counts.RecordAsync(count, 2, 9);
        await _t.Wms.Counts.SubmitAsync(count);
        await _t.Wms.Counts.ApproveAsync(count);

        var accuracy = await Report(ReportKinds.Accuracy);
        Assert.Equal((2, 1, 50m), ((int)accuracy.Totals![3]!, (int)accuracy.Totals[4]!, (decimal)accuracy.Totals[5]!));

        var productivity = await Report(ReportKinds.Productivity);
        var luis = productivity.Rows.Single(r => (string?)r[0] == "Luis");
        Assert.Equal((2, 2), ((int)luis[1]!, (int)luis[6]!));   // 2 líneas recibidas, 2 contadas

        _t.Clock.Advance(TimeSpan.FromDays(100));
        var idle = await _t.Wms.Reports.GetAsync(new ReportQuery(ReportKinds.Idle, _t.Central, From, To, 90));
        Assert.Equal(2, idle.Rows.Count);

        // Sin permiso de costos, los valores no se ven.
        _t.Context.Role = WmsPermissions.Operator;
        Assert.All((await Report(ReportKinds.Kardex)).Rows, r => Assert.Null(r[3]));
        await Assert.ThrowsAsync<WmsValidationException>(() => _t.Wms.Reports.GetAsync(new ReportQuery(ReportKinds.Kardex, _t.Central, To, From)));
    }
}
