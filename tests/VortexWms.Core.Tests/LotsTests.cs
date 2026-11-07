using VortexApi.Client;
using VortexApi.Client.Sync;
using VortexWms.Core.Domain;
using VortexWms.Core.Integration;

namespace VortexWms.Core.Tests;

/// <summary>Fase 3 de la hoja de ruta: lotes con vencimiento, FEFO, estados de lote, vida útil, series y trazabilidad.</summary>
public sealed class LotsTests : IAsyncLifetime
{
    private readonly TestWorkspace _t = new();
    private readonly Guid _yogurt = Guid.Parse("22222222-0000-0000-0000-000000000010");
    private readonly Guid _phone = Guid.Parse("22222222-0000-0000-0000-000000000011");

    public async Task InitializeAsync()
    {
        await _t.InitializeAsync();
        await SetupItemsAsync(_t);
    }

    public Task DisposeAsync() => _t.DisposeAsync();

    private async Task SetupItemsAsync(TestWorkspace t)
    {
        var yogurt = TestWorkspace.Item(_yogurt, "YOG-1L", "Yogur natural 1 L");
        await t.Wms.Master.ApplyItemAsync(yogurt with { Data = yogurt.Data with { Tracking = ItemTrackings.Lot } });
        var phone = TestWorkspace.Item(_phone, "CEL-A15", "Celular A15");
        await t.Wms.Master.ApplyItemAsync(phone with { Data = phone.Data with { Tracking = ItemTrackings.Serial } });
    }

    /// <summary>Hoy en las pruebas: 1 de noviembre de 2026 (hora de Lima).</summary>
    private static readonly DateOnly Today = new(2026, 11, 1);

    private Task ReceiveYogurtAsync(TestWorkspace? t = null) => (t ?? _t).Wms.Receipts.ReceiveDirectAsync(_t.Central, null,
        [new DocumentLine(_yogurt, 15, 4m, [new("l-a", 10, Today.AddDays(30)), new("L-B", 5, Today.AddDays(19))])]);

    private async Task<Guid> OrderAsync(Guid item, decimal quantity, Guid? customer = null)
    {
        var orderId = Guid.NewGuid();
        await _t.Wms.SyncHandlers.HandleOrderCreatedAsync(TestWorkspace.Event(SuiteEvents.OrderCreated,
            new OrderCreated(orderId, $"PED-{orderId.ToString()[..4]}", customer ?? Guid.NewGuid(), _t.Central,
                [new OrderLine(item, quantity, 10m)], quantity * 10, "PEN"), source: "ERP"));
        return orderId;
    }

    private async Task<Dictionary<string, decimal>> LotsAsync(TestWorkspace? t = null) =>
        (await (t ?? _t).Wms.Lots.GetLotsAsync(new LotQuery(_t.Central, _yogurt, IncludeEmpty: true))).Items
        .ToDictionary(l => l.LotCode, l => l.Quantity);

    // ---------- Recepción ----------

    [Fact]
    public async Task Recibir_un_articulo_con_lote_exige_el_lote_y_guarda_su_vencimiento()
    {
        var ex = await Assert.ThrowsAsync<WmsValidationException>(() =>
            _t.Wms.Receipts.ReceiveDirectAsync(_t.Central, null, [new DocumentLine(_yogurt, 5)]));
        Assert.Contains("Indica el lote", ex.Message);
        await Assert.ThrowsAsync<WmsValidationException>(() => _t.Wms.Receipts.ReceiveDirectAsync(_t.Central, null,
            [new DocumentLine(_yogurt, 5, null, [new("X", 3)])]));   // no suman

        await ReceiveYogurtAsync();
        var lots = (await _t.Wms.Lots.GetLotsAsync(new LotQuery(ItemId: _yogurt))).Items;
        Assert.Equal(["L-B", "L-A"], lots.Select(l => l.LotCode));   // primero el que vence antes; códigos en mayúsculas
        Assert.Equal((Today.AddDays(19), 5m, 19), (lots[0].ExpiresAt!.Value, lots[0].Quantity, lots[0].DaysLeft!.Value));
        Assert.Equal(15, await _t.OnHandAsync(_t.Central, _yogurt));

        var outbox = await _t.OutboxAsync();
        var adjusted = outbox.Single(e => e.EventType == SuiteEvents.StockAdjusted).Payload!.Value;
        Assert.Equal(2, adjusted.GetProperty("lots").GetArrayLength());
        var received = outbox.Single(e => e.EventType == SuiteEvents.GoodsReceived).Payload!.Value;
        Assert.Equal("L-A", received.GetProperty("lines")[0].GetProperty("lots")[0].GetProperty("lotCode").GetString());
    }

    // ---------- FEFO ----------

    [Fact]
    public async Task El_despacho_saca_primero_lo_que_vence_antes()
    {
        await ReceiveYogurtAsync();
        var order = await OrderAsync(_yogurt, 7);
        Assert.Equal([("L-B", 5m), ("L-A", 2m)], (await _t.Wms.Lots.SuggestAsync(_t.Central, _yogurt, 7)).Select(l => (l.LotCode, l.Quantity)));

        await _t.Wms.Orders.DispatchAsync(order, null, null);
        Assert.Equal(new Dictionary<string, decimal> { ["L-A"] = 8, ["L-B"] = 0 }, await LotsAsync());
        var shipment = (await _t.OutboxAsync()).Single(e => e.EventType == SuiteEvents.ShipmentDispatched).Payload!.Value;
        Assert.Equal(2, shipment.GetProperty("lines")[0].GetProperty("lots").GetArrayLength());
    }

    [Fact]
    public async Task Elegir_otro_lote_que_FEFO_requiere_un_supervisor()
    {
        await ReceiveYogurtAsync();
        var order = await OrderAsync(_yogurt, 3);
        _t.Context.Role = WmsPermissions.Operator;
        var ex = await Assert.ThrowsAsync<WmsValidationException>(() => _t.Wms.Orders.DispatchAsync(order, null, null,
            picks: [new DocumentLine(_yogurt, 3, null, [new("L-A", 3)])]));
        Assert.Contains("L-B × 3", ex.Message);

        _t.Context.Role = WmsPermissions.Supervisor;
        await _t.Wms.Orders.DispatchAsync(order, null, null, picks: [new DocumentLine(_yogurt, 3, null, [new("L-A", 3)])]);
        Assert.Equal(7m, (await LotsAsync())["L-A"]);
    }

    [Fact]
    public async Task Lo_vencido_o_en_cuarentena_no_sale_pero_se_puede_dar_de_baja()
    {
        await ReceiveYogurtAsync();
        await _t.Wms.Receipts.ReceiveDirectAsync(_t.Central, null, [new DocumentLine(_yogurt, 4, null, [new("L-OLD", 4, Today.AddDays(-2))])]);
        await _t.Wms.Lots.SetStatusAsync(_t.Central, _yogurt, "l-b", LotStatuses.Quarantine, "Control de calidad");

        Assert.Equal([("L-A", 6m)], (await _t.Wms.Lots.SuggestAsync(_t.Central, _yogurt, 6)).Select(l => (l.LotCode, l.Quantity)));
        var order = await OrderAsync(_yogurt, 12);
        var ex = await Assert.ThrowsAsync<WmsValidationException>(() => _t.Wms.Orders.DispatchAsync(order, null, null));
        Assert.Contains("No hay lotes vigentes suficientes", ex.Message);
        ex = await Assert.ThrowsAsync<WmsValidationException>(() => _t.Wms.Orders.DispatchAsync(order, null, null,
            picks: [new DocumentLine(_yogurt, 12, null, [new("L-A", 8), new("L-OLD", 4)])]));
        Assert.Contains("venció", ex.Message);

        await _t.Wms.Inventory.AdjustAsync(_t.Central, _yogurt, null, -4, AdjustmentReasons.Damage, "Vencido", lotCode: "L-OLD");
        Assert.Equal(0m, (await LotsAsync())["L-OLD"]);
        Assert.Equal((2, 0), await _t.Wms.Lots.GetExpiringCountAsync());   // L-B (19 días) y L-A (30); L-OLD ya no tiene saldo
    }

    [Fact]
    public async Task El_conteo_de_un_articulo_con_lote_es_por_lote()
    {
        await ReceiveYogurtAsync();
        await Assert.ThrowsAsync<WmsValidationException>(() =>
            _t.Wms.Inventory.AdjustAsync(_t.Central, _yogurt, 9, null, AdjustmentReasons.Count, null));
        await _t.Wms.Inventory.AdjustAsync(_t.Central, _yogurt, 9, null, AdjustmentReasons.Count, null, lotCode: "L-A");
        Assert.Equal(9m, (await LotsAsync())["L-A"]);
        Assert.Equal(14, await _t.OnHandAsync(_t.Central, _yogurt));
    }

    // ---------- Vida útil ----------

    [Fact]
    public async Task La_vida_util_completa_el_vencimiento_y_los_dias_minimos_frenan_lotes_cortos()
    {
        await _t.Wms.Lots.SetShelfLifeAsync(_yogurt, 60, 30);
        await _t.Wms.Receipts.ReceiveDirectAsync(_t.Central, null, [new DocumentLine(_yogurt, 2, null, [new("L-NUEVO", 2)])]);
        Assert.Equal(Today.AddDays(60), (await _t.Wms.Lots.GetLotsAsync(new LotQuery(ItemId: _yogurt))).Items.Single().ExpiresAt);

        _t.Context.Role = WmsPermissions.Operator;
        var ex = await Assert.ThrowsAsync<WmsValidationException>(() => _t.Wms.Receipts.ReceiveDirectAsync(_t.Central, null,
            [new DocumentLine(_yogurt, 2, null, [new("L-CORTO", 2, Today.AddDays(10))])]));
        Assert.Contains("10 días", ex.Message);
        _t.Context.Role = WmsPermissions.Supervisor;
        await _t.Wms.Receipts.ReceiveDirectAsync(_t.Central, null, [new DocumentLine(_yogurt, 2, null, [new("L-CORTO", 2, Today.AddDays(10))])]);
    }

    // ---------- Series ----------

    [Fact]
    public async Task Las_series_entran_y_salen_una_por_unidad_y_se_rastrean()
    {
        await _t.Wms.Receipts.ReceiveDirectAsync(_t.Central, null, [new DocumentLine(_phone, 2, 500m, Serials: ["sn-001", "SN-002"])]);
        Assert.Equal(["SN-001", "SN-002"], (await _t.Wms.Lots.GetSerialsAsync(_phone, _t.Central)).Select(s => s.Serial));
        await Assert.ThrowsAsync<WmsValidationException>(() => _t.Wms.Receipts.ReceiveDirectAsync(_t.Central, null,
            [new DocumentLine(_phone, 1, 500m, Serials: ["SN-001"])]));   // ya está en stock
        await Assert.ThrowsAsync<WmsValidationException>(() => _t.Wms.Receipts.ReceiveDirectAsync(_t.Central, null,
            [new DocumentLine(_phone, 2, 500m, Serials: ["SN-009"])]));   // una por unidad

        var customer = Guid.NewGuid();
        var order = await OrderAsync(_phone, 1, customer);
        var ex = await Assert.ThrowsAsync<WmsValidationException>(() => _t.Wms.Orders.DispatchAsync(order, null, null));
        Assert.Contains("series", ex.Message);
        await _t.Wms.Orders.DispatchAsync(order, null, null, picks: [new DocumentLine(_phone, 1, Serials: ["SN-002"])]);
        Assert.Equal(["SN-001"], (await _t.Wms.Lots.GetSerialsAsync(_phone, _t.Central)).Select(s => s.Serial));

        var trace = await _t.Wms.Lots.TraceAsync("sn-002");
        Assert.Equal([MovementKinds.Receipt, MovementKinds.Shipment], trace.Select(r => r.Kind));
        Assert.Equal(customer, trace[1].PartyId);
    }

    // ---------- Ventas de caja ----------

    [Fact]
    public async Task Una_venta_de_caja_sale_por_FEFO_sin_trabarse_y_la_anulacion_devuelve_a_los_mismos_lotes()
    {
        await ReceiveYogurtAsync();
        var saleId = Guid.NewGuid();
        await _t.Wms.SyncHandlers.HandleSaleCompletedAsync(TestWorkspace.Event(SuiteEvents.SaleCompleted, new
        {
            saleId, number = "B001-1", siteId = _t.Central, lines = new[] { new { itemId = _yogurt, quantity = 18m, total = 180m } },
        }));
        Assert.Equal(-3, await _t.OnHandAsync(_t.Central, _yogurt));   // la venta ya ocurrió: queda negativo
        Assert.Equal(new Dictionary<string, decimal> { ["L-A"] = 0, ["L-B"] = 0 }, await LotsAsync());

        await _t.Wms.SyncHandlers.HandleSaleVoidedAsync(TestWorkspace.Event(SuiteEvents.SaleVoided, new { saleId, number = "B001-1" }));
        Assert.Equal(15, await _t.OnHandAsync(_t.Central, _yogurt));
        Assert.Equal(new Dictionary<string, decimal> { ["L-A"] = 10, ["L-B"] = 5 }, await LotsAsync());
    }

    [Fact]
    public async Task El_stock_de_antes_de_controlar_lote_sale_como_sin_lote()
    {
        var plain = TestWorkspace.Item(_yogurt, "YOG-1L", "Yogur natural 1 L", version: 2);
        await _t.Wms.Master.ApplyItemAsync(plain with { Data = plain.Data with { Tracking = ItemTrackings.None } });
        await _t.Wms.Receipts.ReceiveDirectAsync(_t.Central, null, [new DocumentLine(_yogurt, 6)]);
        var lot = TestWorkspace.Item(_yogurt, "YOG-1L", "Yogur natural 1 L", version: 3);
        await _t.Wms.Master.ApplyItemAsync(lot with { Data = lot.Data with { Tracking = ItemTrackings.Lot } });
        await ReceiveYogurtAsync();

        // FEFO: primero los lotes con vencimiento y al final lo que no tiene lote.
        var suggestion = await _t.Wms.Lots.SuggestAsync(_t.Central, _yogurt, 18);
        Assert.Equal([("L-B", 5m), ("L-A", 10m), ("", 3m)], suggestion.Select(l => (l.LotCode, l.Quantity)));
        await _t.Wms.Orders.DispatchAsync(await OrderAsync(_yogurt, 18), null, null);
        Assert.Equal(3, await _t.OnHandAsync(_t.Central, _yogurt));
    }

    // ---------- Varios equipos y trazabilidad ----------

    [Fact]
    public async Task Lotes_estados_y_transferencias_llegan_a_otro_equipo()
    {
        var other = new TestWorkspace("OTRO");
        await other.InitializeAsync();
        try
        {
            await SetupItemsAsync(other);
            await ReceiveYogurtAsync();
            await _t.Wms.Inventory.TransferAsync(_t.Central, _t.Store, [new DocumentLine(_yogurt, 6)], null);
            await _t.Wms.Lots.SetStatusAsync(_t.Central, _yogurt, "L-A", LotStatuses.Blocked, "Retiro del mercado");
            await DeliverAsync(_t, other);
            await DeliverAsync(_t, other);   // repetir no cambia nada

            foreach (var device in new[] { _t, other })
            {
                Assert.Equal(new Dictionary<string, decimal> { ["L-A"] = 9, ["L-B"] = 0 }, await LotsAsync(device));
                var store = (await device.Wms.Lots.GetLotsAsync(new LotQuery(_t.Store, _yogurt))).Items;
                Assert.Equal([("L-B", 5m), ("L-A", 1m)], store.Select(l => (l.LotCode, l.Quantity)));
                Assert.Equal(LotStatuses.Blocked,
                    (await device.Wms.Lots.GetLotsAsync(new LotQuery(_t.Central, _yogurt))).Items.Single().Status);
            }
        }
        finally
        {
            await other.DisposeAsync();
        }
    }

    [Fact]
    public async Task La_trazabilidad_de_un_lote_llega_al_cliente()
    {
        await ReceiveYogurtAsync();
        var customer = Guid.NewGuid();
        await _t.Wms.Orders.DispatchAsync(await OrderAsync(_yogurt, 7, customer), null, null);

        var trace = await _t.Wms.Lots.TraceAsync("L-A");
        Assert.Equal([(MovementKinds.Receipt, 10m), (MovementKinds.Shipment, -2m)], trace.Select(r => (r.Kind, r.Quantity)));
        Assert.Equal(customer, trace[1].PartyId);
    }

    private static async Task DeliverAsync(TestWorkspace from, TestWorkspace to)
    {
        foreach (var entry in (await from.OutboxAsync()).Where(e => e.Kind == VortexOutboxKind.Event))
        {
            var e = new VortexEvent(entry.Id, 1, entry.EventType!, "WMS", 1, entry.SubjectType, entry.SubjectId, entry.CreatedAt,
                entry.CreatedAt, null, entry.Payload!.Value);
            var handlers = to.Wms.SyncHandlers;
            await (e.Type switch
            {
                SuiteEvents.StockAdjusted => handlers.HandleStockAdjustedAsync(e),
                SuiteEvents.GoodsReceived => handlers.HandleGoodsReceivedAsync(e),
                SuiteEvents.RecordUpserted => handlers.HandleRecordUpsertedAsync(e),
                _ => Task.CompletedTask,
            });
        }
    }
}
