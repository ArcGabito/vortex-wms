using VortexWms.Core.Domain;
using VortexWms.Core.Integration;
using VortexWms.Core.Labels;
using VortexWms.Core.Pdf;
using VortexWms.Core.Services;

namespace VortexWms.Core.Tests;

/// <summary>Fase 6c: etiquetas de artículo, ubicación y bulto en ZPL y PDF.</summary>
public sealed class LabelsTests : IAsyncLifetime
{
    private readonly TestWorkspace _t = new("AAAA");

    public Task InitializeAsync() => _t.InitializeAsync();
    public Task DisposeAsync() => _t.DisposeAsync();

    [Fact]
    public void Code128_usa_el_juego_c_para_los_numeros_y_calcula_el_control()
    {
        // "PJJ123C" va todo en juego B: control = (104 + 48·1 + 42·2 + 42·3 + 17·4 + 18·5 + 19·6 + 35·7) mod 103 = 55.
        Assert.Equal([104, 48, 42, 42, 17, 18, 19, 35, 55, 106], Code128.Encode("PJJ123C"));
        // "7750123" (impar): 775012 en C y el último 3 en B.
        Assert.Equal([105, 77, 50, 12, 100, 19], Code128.Encode("7750123").Take(6));
        var modules = Code128.Modules("A-01-02");
        Assert.Equal(11 * (Code128.Encode("A-01-02").Count - 1) + 13, modules.Sum());
        Assert.Throws<ArgumentException>(() => Code128.Encode("Ñ"));
    }

    [Fact]
    public async Task Etiquetas_de_articulo_ubicacion_y_bulto_en_zpl_y_pdf()
    {
        var items = await _t.Wms.Labels.ForItemsAsync([_t.Coffee], withPrice: true);
        var item = Assert.Single(items);
        Assert.Equal("Café tostado 250 g", item.Title);
        Assert.NotNull(item.Footer);

        await _t.Wms.Locations.SaveAsync(null, new LocationDraft(_t.Central, "A-01-02", LocationKinds.Storage));
        var location = Assert.Single(await _t.Wms.Labels.ForLocationsAsync(_t.Central));
        Assert.Equal(("A-01-02", "A-01-02"), (location.Barcode, location.Big));

        await _t.Wms.Receipts.ReceiveDirectAsync(_t.Central, null, [new DocumentLine(_t.Coffee, 10)]);
        var order = Guid.NewGuid();
        await _t.Wms.SyncHandlers.HandleOrderCreatedAsync(TestWorkspace.Event(SuiteEvents.OrderCreated,
            new OrderCreated(order, "PED-8", Guid.NewGuid(), _t.Central, [new OrderLine(_t.Coffee, 8, 18.5m)], 148m, "PEN"), source: "ERP"));
        await Assert.ThrowsAsync<WmsValidationException>(() => _t.Wms.Labels.ForPackagesAsync(order));
        await _t.Wms.Orders.ConfirmPickAsync(order, _t.Coffee, null, 8);
        await _t.Wms.Orders.AddPackageAsync(order, 3.2m, [new DocumentLine(_t.Coffee, 5)]);
        await _t.Wms.Orders.AddPackageAsync(order, null, [new DocumentLine(_t.Coffee, 3)]);
        var packages = await _t.Wms.Labels.ForPackagesAsync(order);
        Assert.Equal([("PED-8-1", "1/2", "3.2 kg"), ("PED-8-2", "2/2", null)], packages.Select(p => (p.Barcode, p.Big, p.Footer)));

        var zpl = Zpl.Render(packages, LabelSize.Medium, copies: 2);
        Assert.Equal(2, zpl.Split("^XA").Length - 1);
        Assert.Contains("^BCN,", zpl);
        Assert.Contains("^FDPED-8-1^FS", zpl);
        Assert.Contains("^PQ2", zpl);
        Assert.Contains("^PW800^LL400", zpl);   // 100 × 50 mm a 8 puntos por mm

        var pdf = LabelPdf.Render([item, location, .. packages], LabelSize.Medium);
        Assert.Equal("%PDF", System.Text.Encoding.ASCII.GetString(pdf, 0, 4));
        var sheet = LabelPdf.Render(Enumerable.Repeat(item, 30).ToList(), LabelSize.Small, a4Sheet: true);
        Assert.Equal("%PDF", System.Text.Encoding.ASCII.GetString(sheet, 0, 4));
    }
}
