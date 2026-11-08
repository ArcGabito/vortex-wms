using VortexApi.Client;
using VortexApi.Client.Sync;
using VortexWms.Core.Domain;
using VortexWms.Core.Integration;

namespace VortexWms.Core.Tests;

/// <summary>Fase 4a de la hoja de ruta: mapa de ubicaciones, stock por ubicación, traslados internos y guardado guiado.</summary>
public sealed class LocationsTests : IAsyncLifetime
{
    private readonly TestWorkspace _t = new("AAAA");

    public Task InitializeAsync() => _t.InitializeAsync();
    public Task DisposeAsync() => _t.DisposeAsync();

    private async Task<Guid> LocationAsync(string code, string kind = LocationKinds.Storage, Guid? fixedItem = null) =>
        await _t.Wms.Locations.SaveAsync(null, new LocationDraft(_t.Central, code, kind, FixedItemId: fixedItem));

    private async Task<decimal> InAsync(Guid location, Guid item) =>
        (await _t.Wms.Locations.GetLocationStockAsync(location)).Where(s => s.ItemId == item).Sum(s => s.Quantity);

    [Fact]
    public async Task Alta_por_rango_crea_los_codigos_y_salta_los_que_ya_existen()
    {
        await LocationAsync("A-01-01");
        var creadas = await _t.Wms.Locations.CreateRangeAsync(new LocationRange(_t.Central, "a", 1, 3, 1, 2, LocationKinds.Storage));
        Assert.Equal(5, creadas);   // 3 pasillos × 2 niveles, menos A-01-01
        var codes = (await _t.Wms.Locations.GetLocationsAsync(_t.Central)).Select(l => l.Code).ToList();
        Assert.Equal(["A-01-01", "A-01-02", "A-02-01", "A-02-02", "A-03-01", "A-03-02"], codes);

        var ex = await Assert.ThrowsAsync<WmsValidationException>(() => LocationAsync("a-02-01"));
        Assert.Contains("Ya existe", ex.Message);
        Assert.NotNull(await _t.Wms.Locations.FindByCodeAsync(_t.Central, " a-03-02 "));
    }

    [Fact]
    public async Task Lo_recibido_queda_por_guardar_y_se_sugiere_la_fija_la_del_mismo_articulo_o_una_vacia()
    {
        var vacia1 = await LocationAsync("B-01-01");
        await LocationAsync("B-01-02");
        await _t.Wms.Receipts.ReceiveDirectAsync(_t.Central, null, [new DocumentLine(_t.Coffee, 30), new DocumentLine(_t.Sugar, 10)]);

        var pending = await _t.Wms.Locations.GetPendingPutawayAsync(_t.Central);
        var cafe = pending.Single(p => p.ItemId == _t.Coffee);
        Assert.Equal(30, cafe.Pending);
        Assert.Equal((vacia1, PutawayReasons.Empty), (cafe.SuggestedLocationId!.Value, cafe.SuggestionReason));

        // Guardar 20 en B-01-01: el resto del café ahora se sugiere ahí (mismo artículo).
        await _t.Wms.Locations.RelocateAsync(_t.Central, _t.Coffee, null, vacia1, 20, "REC-1");
        Assert.Equal(20, await InAsync(vacia1, _t.Coffee));
        cafe = (await _t.Wms.Locations.GetPendingPutawayAsync(_t.Central)).Single(p => p.ItemId == _t.Coffee);
        Assert.Equal((10m, PutawayReasons.SameItem, "B-01-01"), (cafe.Pending, cafe.SuggestionReason, cafe.SuggestedCode));

        // Una ubicación fija del artículo gana.
        var fija = await LocationAsync("P-01", LocationKinds.Picking, _t.Sugar);
        var azucar = (await _t.Wms.Locations.GetPendingPutawayAsync(_t.Central)).Single(p => p.ItemId == _t.Sugar);
        Assert.Equal((fija, PutawayReasons.Fixed), (azucar.SuggestedLocationId!.Value, azucar.SuggestionReason));

        // No se guarda más de lo que hay sin ubicación; el stock de la sede no cambia.
        var ex = await Assert.ThrowsAsync<WmsValidationException>(() => _t.Wms.Locations.RelocateAsync(_t.Central, _t.Coffee, null, vacia1, 11));
        Assert.Contains("10", ex.Message);
        Assert.Equal(30, await _t.OnHandAsync(_t.Central, _t.Coffee));
    }

    [Fact]
    public async Task Traslado_interno_y_salidas_descuentan_las_ubicaciones_en_orden_fijo()
    {
        var almacen = await LocationAsync("C-01-01");
        var picking = await LocationAsync("C-PICK", LocationKinds.Picking);
        await _t.Wms.Receipts.ReceiveDirectAsync(_t.Central, null, [new DocumentLine(_t.Coffee, 30)]);
        await _t.Wms.Locations.RelocateAsync(_t.Central, _t.Coffee, null, almacen, 25);
        await _t.Wms.Locations.RelocateAsync(_t.Central, _t.Coffee, almacen, picking, 10);
        Assert.Equal((15m, 10m), (await InAsync(almacen, _t.Coffee), await InAsync(picking, _t.Coffee)));
        Assert.Equal(2, (await _t.Wms.Locations.GetRelocationsAsync(_t.Central)).Count);

        // Salen 12 sin decir de dónde: primero lo que está sin ubicación (5), después picking (7).
        await _t.Wms.Inventory.AdjustAsync(_t.Central, _t.Coffee, null, -12, AdjustmentReasons.Loss, null);
        Assert.Equal((15m, 3m), (await InAsync(almacen, _t.Coffee), await InAsync(picking, _t.Coffee)));
        Assert.Empty((await _t.Wms.Locations.GetPendingPutawayAsync(_t.Central)).Where(p => p.ItemId == _t.Coffee));

        // No se desactiva una ubicación con stock.
        var ex = await Assert.ThrowsAsync<WmsValidationException>(() =>
            _t.Wms.Locations.SaveAsync(picking, new LocationDraft(_t.Central, "C-PICK", LocationKinds.Picking, IsActive: false)));
        Assert.Contains("tiene stock", ex.Message);
    }

    [Fact]
    public async Task El_mapa_y_los_traslados_llegan_a_otro_equipo_una_sola_vez()
    {
        var otro = new TestWorkspace("BBBB");
        await otro.InitializeAsync();
        try
        {
            var loc = await LocationAsync("D-01-01");
            await _t.Wms.Receipts.ReceiveDirectAsync(_t.Central, null, [new DocumentLine(_t.Coffee, 8)]);
            await _t.Wms.Locations.RelocateAsync(_t.Central, _t.Coffee, null, loc, 8);

            var events = (await _t.OutboxAsync()).Where(e => e.Kind == VortexOutboxKind.Event).ToList();
            Assert.Contains(events, e => e.EventType == SuiteEvents.StockRelocated);
            foreach (var entry in events.Concat(events))   // repetidos: se aplican una sola vez
            {
                var e = new VortexEvent(entry.Id, 1, entry.EventType!, "WMS", 1, entry.SubjectType, entry.SubjectId, entry.CreatedAt,
                    entry.CreatedAt, null, entry.Payload!.Value);
                var h = otro.Wms.SyncHandlers;
                await (e.Type switch
                {
                    SuiteEvents.StockAdjusted => h.HandleStockAdjustedAsync(e),
                    SuiteEvents.RecordUpserted => h.HandleRecordUpsertedAsync(e),
                    SuiteEvents.StockRelocated => h.HandleStockRelocatedAsync(e),
                    _ => Task.CompletedTask,
                });
            }

            var alla = (await otro.Wms.Locations.FindByCodeAsync(_t.Central, "D-01-01"))!;
            Assert.Equal(loc, alla.Id);
            Assert.Equal(8, (await otro.Wms.Locations.GetLocationStockAsync(loc)).Single().Quantity);
            Assert.Equal(8, await otro.OnHandAsync(_t.Central, _t.Coffee));
        }
        finally
        {
            await otro.DisposeAsync();
        }
    }

    [Fact]
    public async Task Un_operario_guarda_pero_no_configura_el_mapa()
    {
        var loc = await LocationAsync("E-01-01");
        await _t.Wms.Receipts.ReceiveDirectAsync(_t.Central, null, [new DocumentLine(_t.Coffee, 4)]);
        _t.Context.Role = WmsPermissions.Operator;
        await _t.Wms.Locations.RelocateAsync(_t.Central, _t.Coffee, null, loc, 4);
        await Assert.ThrowsAsync<WmsValidationException>(() => LocationAsync("E-01-02"));
    }
}
