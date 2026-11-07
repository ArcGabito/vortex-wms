using VortexApi.Client;
using VortexWms.Core.Domain;

namespace VortexWms.Core.Tests;

/// <summary>Fase 2: presentaciones, varios códigos de barras y datos de almacén del artículo.</summary>
public sealed class PresentationTests : IAsyncLifetime
{
    private readonly TestWorkspace _t = new();

    public Task InitializeAsync() => _t.InitializeAsync();
    public Task DisposeAsync() => _t.DisposeAsync();

    private static readonly ItemPresentation Box = new("caja", "Caja × 12", 12, "17750000000014", "bx");

    private Task<Guid> CreateWaterAsync() => _t.Wms.Master.CreateItemAsync(new ItemDraft(
        "agua-625", "Agua 625 ml", "unidad", "Bebidas", "7750000000017", 1.5m, "PEN",
        UnitCode: "niu", WeightKg: 0.65m, Tracking: ItemTrackings.Lot,
        Presentations: [Box, new ItemPresentation("pack6", "Paquete × 6", 6, "27750000000011")]));

    [Fact]
    public async Task Recibir_2_cajas_de_12_suma_24_unidades_y_el_costo_queda_por_unidad()
    {
        var water = await CreateWaterAsync();
        var item = (await _t.Wms.Master.GetItemAsync(water))!;

        var (quantity, unitCost) = item.ToBase("CAJA", 2, unitPrice: 18m);
        Assert.Equal(24, quantity);
        Assert.Equal(1.5m, unitCost);

        await _t.Wms.Receipts.ReceiveDirectAsync(_t.Central, null, [new DocumentLine(water, quantity, unitCost)]);
        Assert.Equal(24, await _t.OnHandAsync(_t.Central, water));

        // Sin presentación, la cantidad ya está en la unidad base.
        Assert.Equal((5m, (decimal?)null), item.ToBase(null, 5));
    }

    [Fact]
    public async Task Escanear_el_DUN14_elige_la_presentacion_caja_sin_conexion()
    {
        var water = await CreateWaterAsync();

        var box = await _t.Wms.Master.ScanAsync("17750000000014");
        Assert.Equal(water, box!.Item.Id);
        Assert.Equal("CAJA", box.Presentation!.Code);
        Assert.Equal(12, box.Presentation.Factor);

        var unit = await _t.Wms.Master.ScanAsync(" 7750000000017 ");
        Assert.Equal(water, unit!.Item.Id);
        Assert.Null(unit.Presentation);

        var bySku = await _t.Wms.Master.ScanAsync("agua-625");
        Assert.Equal(water, bySku!.Item.Id);
        Assert.Null(await _t.Wms.Master.ScanAsync("0000000000000"));

        // El buscador general también encuentra el artículo por el código de una presentación.
        Assert.Equal(water, Assert.Single(await _t.Wms.Master.SearchItemsAsync("27750000000011")).Id);
    }

    [Fact]
    public async Task Los_campos_nuevos_viajan_al_hub_y_vuelven_del_feed()
    {
        var water = await CreateWaterAsync();
        var item = (await _t.Wms.Master.GetItemAsync(water))!;
        Assert.Equal("NIU", item.UnitCode);
        Assert.Equal(0.65m, item.WeightKg);
        Assert.Equal(ItemTrackings.Lot, item.Tracking);
        Assert.Equal(["CAJA", "PACK6"], item.Presentations.Select(p => p.Code));

        var upsert = (await _t.OutboxAsync()).Single(e => e.EntityId == water);
        var body = upsert.Payload!.Value;
        Assert.Equal("NIU", body.GetProperty("unitCode").GetString());
        Assert.Equal("Lot", body.GetProperty("tracking").GetString());
        Assert.Equal(12, body.GetProperty("presentations")[0].GetProperty("factor").GetDecimal());

        // Otro sistema cambia la caja a × 24 con otro código: el feed reemplaza las presentaciones y el índice de códigos.
        var change = TestWorkspace.Item(water, "AGUA-625", "Agua 625 ml", version: 2);
        await _t.Wms.Master.ApplyItemAsync(change with
        {
            Data = change.Data with
            {
                Barcode = "7750000000017", UnitCode = "NIU", Tracking = "Lot",
                Presentations = [new VortexItemPresentation("CAJA", "Caja × 24", 24, "37750000000018")],
            },
        });
        Assert.Null(await _t.Wms.Master.ScanAsync("17750000000014"));
        Assert.Equal(24, (await _t.Wms.Master.ScanAsync("37750000000018"))!.Presentation!.Factor);
    }

    [Fact]
    public async Task Un_codigo_de_barras_no_puede_ser_de_dos_articulos_ni_repetirse()
    {
        await CreateWaterAsync();

        var ex = await Assert.ThrowsAsync<WmsValidationException>(() => _t.Wms.Master.CreateItemAsync(new ItemDraft(
            "agua-1l", "Agua 1 L", "unidad", null, null, 2, "PEN", Presentations: [Box with { Code = "caja1l" }])));
        Assert.Contains("AGUA-625", ex.Message);

        await Assert.ThrowsAsync<WmsValidationException>(() => _t.Wms.Master.CreateItemAsync(new ItemDraft(
            "x-1", "X", "unidad", null, "123", 1, "PEN", Presentations: [new ItemPresentation("c", "Caja", 10, "123")])));
        await Assert.ThrowsAsync<WmsValidationException>(() => _t.Wms.Master.CreateItemAsync(new ItemDraft(
            "x-2", "X", "unidad", null, null, 1, "PEN", Presentations: [new ItemPresentation("c", "Caja", 0)])));
    }

    [Fact]
    public async Task Un_servicio_no_lleva_lote_ni_serie()
    {
        var id = await _t.Wms.Master.CreateItemAsync(new ItemDraft(
            "flete", "Flete", "servicio", null, null, 20, "PEN", Kind: "Service", TrackInventory: false, Tracking: ItemTrackings.Serial));
        Assert.Equal(ItemTrackings.None, (await _t.Wms.Master.GetItemAsync(id))!.Tracking);
    }
}
