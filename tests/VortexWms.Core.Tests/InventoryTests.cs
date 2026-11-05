using VortexApi.Client;
using VortexApi.Client.Sync;
using VortexWms.Core.Domain;
using VortexWms.Core.Integration;

namespace VortexWms.Core.Tests;

public sealed class InventoryTests : IAsyncLifetime
{
    private readonly TestWorkspace _t = new();

    public Task InitializeAsync() => _t.InitializeAsync();
    public Task DisposeAsync() => _t.DisposeAsync();

    [Fact]
    public async Task Recepcion_directa_sube_stock_y_encola_eventos_en_orden()
    {
        var number = await _t.Wms.Receipts.ReceiveDirectAsync(_t.Central, null,
            [new DocumentLine(_t.Coffee, 30, 12.5m), new DocumentLine(_t.Coffee, 10), new DocumentLine(_t.Sugar, 5)]);

        Assert.StartsWith("REC-TEST-", number);
        Assert.Equal(40, await _t.OnHandAsync(_t.Central, _t.Coffee));
        Assert.Equal(5, await _t.OnHandAsync(_t.Central, _t.Sugar));

        var outbox = await _t.OutboxAsync();
        Assert.Equal([SuiteEvents.StockAdjusted, SuiteEvents.StockAdjusted, SuiteEvents.GoodsReceived], outbox.Select(e => e.EventType));
        var adjusted = outbox[0].Payload!.Value.Deserialize<StockAdjusted>();
        Assert.Equal(40, adjusted.QuantityOnHand);
        Assert.Equal("receipt", adjusted.Reason);
        var received = outbox[2].Payload!.Value.Deserialize<GoodsReceived>();
        Assert.Equal(number, received.Number);
        Assert.Equal(2, received.Lines.Count);
    }

    [Fact]
    public async Task Ajuste_por_conteo_deja_el_saldo_exacto_y_no_permite_negativos()
    {
        await _t.Wms.Receipts.ReceiveDirectAsync(_t.Central, null, [new DocumentLine(_t.Coffee, 20)]);

        var onHand = await _t.Wms.Inventory.AdjustAsync(_t.Central, _t.Coffee, countedQuantity: 17, delta: null, AdjustmentReasons.Count, "Conteo cíclico");
        Assert.Equal(17, onHand);

        await Assert.ThrowsAsync<WmsValidationException>(() =>
            _t.Wms.Inventory.AdjustAsync(_t.Central, _t.Coffee, null, delta: -18, AdjustmentReasons.Damage, null));
        await Assert.ThrowsAsync<WmsValidationException>(() =>
            _t.Wms.Inventory.AdjustAsync(_t.Central, _t.Delivery, 5, null, AdjustmentReasons.Count, null));

        var movements = await _t.Wms.Inventory.GetMovementsAsync(new MovementQuery(Kind: MovementKinds.Adjustment));
        Assert.Equal(-3, Assert.Single(movements.Items).Quantity);
    }

    [Fact]
    public async Task Transferencia_mueve_stock_entre_sedes_y_valida_disponible()
    {
        await _t.Wms.Receipts.ReceiveDirectAsync(_t.Central, null, [new DocumentLine(_t.Coffee, 10)]);

        await Assert.ThrowsAsync<WmsValidationException>(() =>
            _t.Wms.Inventory.TransferAsync(_t.Central, _t.Store, [new DocumentLine(_t.Coffee, 11)], null));

        var number = await _t.Wms.Inventory.TransferAsync(_t.Central, _t.Store, [new DocumentLine(_t.Coffee, 4)], "Reposición");
        Assert.Equal(6, await _t.OnHandAsync(_t.Central, _t.Coffee));
        Assert.Equal(4, await _t.OnHandAsync(_t.Store, _t.Coffee));

        var transferred = (await _t.OutboxAsync()).Single(e => e.EventType == SuiteEvents.StockTransferred);
        Assert.Equal(number, transferred.SubjectId);
        Assert.Equal(_t.Store, transferred.Payload!.Value.Deserialize<StockTransferred>().ToSiteId);
    }

    [Fact]
    public async Task Pedido_reserva_y_al_despachar_descuenta_y_publica_shipment()
    {
        await _t.Wms.Receipts.ReceiveDirectAsync(_t.Central, null, [new DocumentLine(_t.Coffee, 10)]);
        var customer = Guid.NewGuid();
        var orderId = Guid.NewGuid();
        await _t.Wms.SyncHandlers.HandleOrderCreatedAsync(TestWorkspace.Event(SuiteEvents.OrderCreated,
            new OrderCreated(orderId, "PED-0001", customer, _t.Central,
                [new OrderLine(_t.Coffee, 3, 18.5m), new OrderLine(_t.Delivery, 1, 10)], 65.5m, "PEN"), source: "ERP"));

        var stock = await _t.Wms.Inventory.GetStockRowAsync(_t.Central, _t.Coffee);
        Assert.Equal(3, stock!.Reserved);
        Assert.Equal(7, stock.Available);

        await _t.Wms.Orders.StartPickingAsync(orderId);
        var shipment = await _t.Wms.Orders.DispatchAsync(orderId, "Olva Courier", "OLV123");

        stock = await _t.Wms.Inventory.GetStockRowAsync(_t.Central, _t.Coffee);
        Assert.Equal(7, stock!.OnHand);
        Assert.Equal(0, stock.Reserved);
        Assert.Null(await _t.Wms.Inventory.GetStockRowAsync(_t.Central, _t.Delivery));

        var dispatched = (await _t.OutboxAsync()).Single(e => e.EventType == SuiteEvents.ShipmentDispatched);
        var data = dispatched.Payload!.Value.Deserialize<ShipmentDispatched>();
        Assert.Equal(orderId, data.OrderId);
        Assert.Equal(customer, data.CustomerId);
        Assert.Equal(shipment, data.Number);
        await Assert.ThrowsAsync<WmsValidationException>(() => _t.Wms.Orders.DispatchAsync(orderId, null, null));
    }

    [Fact]
    public async Task Panel_y_exportacion_reflejan_el_stock()
    {
        await _t.Wms.Receipts.ReceiveDirectAsync(_t.Central, null, [new DocumentLine(_t.Coffee, 10, 12m), new DocumentLine(_t.Sugar, 2, 3m)]);
        await _t.Wms.Inventory.UpdateStockSettingsAsync(_t.Central, _t.Sugar, "a-01-02", reorderPoint: 5);

        var summary = await _t.Wms.Inventory.GetDashboardAsync();
        Assert.Equal(12, summary.UnitsOnHand);
        Assert.Equal(1, summary.LowStock);
        Assert.Equal(10 * 12m + 2 * 3m, summary.StockValue);   // a costo (fase 1)

        var low = await _t.Wms.Inventory.GetStockAsync(new StockQuery(Status: StockStatus.Low));
        Assert.Equal("A-01-02", Assert.Single(low.Items).Bin);

        var csv = await _t.Wms.Inventory.ExportStockCsvAsync(new StockQuery(Search: "café"));
        Assert.Contains("CAFE-250,Café tostado 250 g,Abarrotes,ALM-01,,10,0,10,,unidad,18.5,PEN,185,12,120", csv);
    }

    [Fact]
    public async Task Alta_de_articulo_queda_pendiente_y_se_encola_para_el_hub()
    {
        var id = await _t.Wms.Master.CreateItemAsync(new ItemDraft("leche-1l", "Leche UHT 1 L", "unidad", "Lácteos", null, 5.9m, "PEN"));

        var item = await _t.Wms.Master.GetItemAsync(id);
        Assert.Equal("LECHE-1L", item!.Sku);
        Assert.True(item.Pending);
        var entry = Assert.Single(await _t.OutboxAsync());
        Assert.Equal(VortexOutboxKind.MasterUpsert, entry.Kind);
        Assert.Equal(VortexMasterResource.Items, entry.Resource);
        Assert.Equal(id, entry.EntityId);

        await Assert.ThrowsAsync<WmsValidationException>(() =>
            _t.Wms.Master.CreateItemAsync(new ItemDraft("CAFE-250", "Duplicado", "unidad", null, null, 1, "PEN")));
        await Assert.ThrowsAsync<WmsValidationException>(() =>
            _t.Wms.Master.CreatePartyAsync(new PartyDraft("Bodega", "Company", true, false, "RUC", "2012345678", null, null, null)));
    }
}

file static class JsonExtensions
{
    public static T Deserialize<T>(this System.Text.Json.JsonElement element) =>
        System.Text.Json.JsonSerializer.Deserialize<T>(element, VortexJson.Options)!;
}
