using VortexApi.Client;
using VortexApi.Client.Sync;
using VortexWms.Core.Domain;
using VortexWms.Core.Integration;

namespace VortexWms.Core.Tests;

public sealed class SyncTests : IAsyncLifetime
{
    private readonly TestWorkspace _t = new();

    public Task InitializeAsync() => _t.InitializeAsync();
    public Task DisposeAsync() => _t.DisposeAsync();

    [Fact]
    public async Task Venta_del_POS_descuenta_una_sola_vez_y_la_anulacion_la_revierte()
    {
        await _t.Wms.Receipts.ReceiveDirectAsync(_t.Store, null, [new DocumentLine(_t.Coffee, 10)]);
        var saleId = Guid.NewGuid();
        var sale = TestWorkspace.Event(SuiteEvents.SaleCompleted, new
        {
            saleId, number = "B001-000123", siteId = _t.Store, customerId = (Guid?)null, currencyCode = "PEN",
            subtotal = 50.76m, tax = 9.14m, total = 59.90m,
            lines = new[]
            {
                new { itemId = _t.Coffee, sku = "CAFE-250", quantity = 2m, unitPrice = 29.95m, discount = 0m, total = 59.90m },
                new { itemId = _t.Delivery, sku = "SRV-DELIVERY", quantity = 1m, unitPrice = 0m, discount = 0m, total = 0m },
            },
            payments = new[] { new { method = "cash", amount = 59.90m } },
        });

        await _t.Wms.SyncHandlers.HandleSaleCompletedAsync(sale);
        await _t.Wms.SyncHandlers.HandleSaleCompletedAsync(sale);   // el feed puede repetir un evento
        Assert.Equal(8, await _t.OnHandAsync(_t.Store, _t.Coffee));

        var voided = TestWorkspace.Event(SuiteEvents.SaleVoided, new { saleId, number = "B001-000123", reason = "Error de cobro" });
        await _t.Wms.SyncHandlers.HandleSaleVoidedAsync(voided);
        await _t.Wms.SyncHandlers.HandleSaleVoidedAsync(TestWorkspace.Event(SuiteEvents.SaleVoided, new { saleId }));
        Assert.Equal(10, await _t.OnHandAsync(_t.Store, _t.Coffee));

        var reasons = (await _t.OutboxAsync()).Where(e => e.EventType == SuiteEvents.StockAdjusted)
            .Select(e => e.Payload!.Value.GetProperty("reason").GetString());
        Assert.Equal(["receipt", "sale", "sale_void"], reasons);
    }

    [Fact]
    public async Task Venta_puede_dejar_stock_negativo_porque_ya_ocurrio_en_caja()
    {
        await _t.Wms.SyncHandlers.HandleSaleCompletedAsync(TestWorkspace.Event(SuiteEvents.SaleCompleted, new
        {
            saleId = Guid.NewGuid(), siteId = _t.Store, lines = new[] { new { itemId = _t.Sugar, quantity = 3m } },
        }));
        Assert.Equal(-3, await _t.OnHandAsync(_t.Store, _t.Sugar));
    }

    [Fact]
    public async Task Orden_de_compra_crea_recepcion_esperada_una_vez()
    {
        var po = TestWorkspace.Event(SuiteEvents.PurchaseOrderCreated, new
        {
            purchaseOrderId = Guid.NewGuid(), supplierId = Guid.NewGuid(), siteId = _t.Central,
            lines = new[] { new { itemId = _t.Coffee, quantity = 24m, unitCost = 11.2m } },
        }, source: "ERP");
        await _t.Wms.SyncHandlers.HandlePurchaseOrderCreatedAsync(po);
        await _t.Wms.SyncHandlers.HandlePurchaseOrderCreatedAsync(TestWorkspace.Event(SuiteEvents.PurchaseOrderCreated,
            po.Data, source: "ERP"));   // mismo pedido con otro Id de evento

        var receipt = Assert.Single((await _t.Wms.Receipts.GetReceiptsAsync(null, ReceiptStatuses.Expected, 1, 10)).Items);
        var line = Assert.Single(await _t.Wms.Receipts.GetLinesAsync(receipt.Id));
        await _t.Wms.Receipts.ReceiveExpectedAsync(receipt.Id, new Dictionary<int, decimal> { [line.LineNo] = 20 });

        Assert.Equal(20, await _t.OnHandAsync(_t.Central, _t.Coffee));
        var goods = (await _t.OutboxAsync()).Single(e => e.EventType == SuiteEvents.GoodsReceived);
        Assert.Equal(receipt.PurchaseOrderId, goods.Payload!.Value.GetProperty("purchaseOrderId").GetGuid());
    }

    [Fact]
    public async Task Eventos_de_esquema_futuro_o_ilegibles_se_ignoran_sin_trabar_el_feed()
    {
        await _t.Wms.SyncHandlers.HandleSaleCompletedAsync(TestWorkspace.Event(SuiteEvents.SaleCompleted,
            new { saleId = Guid.NewGuid(), siteId = _t.Store, lines = new[] { new { itemId = _t.Coffee, quantity = 1m } } },
            schemaVersion: 2));
        await _t.Wms.SyncHandlers.HandleSaleCompletedAsync(TestWorkspace.Event(SuiteEvents.SaleCompleted,
            new { saleId = "no-es-un-guid", siteId = 5 }));

        Assert.Equal(0, await _t.OnHandAsync(_t.Store, _t.Coffee));
    }

    [Fact]
    public async Task Feed_de_articulos_aplica_por_version_y_limpia_pendientes()
    {
        await _t.Wms.Master.ApplyItemAsync(TestWorkspace.Item(_t.Coffee, "CAFE-250", "Café tostado 250 g (v3)", version: 3));
        await _t.Wms.Master.ApplyItemAsync(TestWorkspace.Item(_t.Coffee, "CAFE-250", "Café viejo", version: 2));
        Assert.Equal("Café tostado 250 g (v3)", (await _t.Wms.Master.GetItemAsync(_t.Coffee))!.Name);

        var id = await _t.Wms.Master.CreateItemAsync(new ItemDraft("TE-20", "Té verde x20", "caja", null, null, 7, "PEN"));
        Assert.True((await _t.Wms.Master.GetItemAsync(id))!.Pending);
        await _t.Wms.Master.ApplyItemAsync(TestWorkspace.Item(id, "TE-20", "Té verde x20", version: 1));
        Assert.False((await _t.Wms.Master.GetItemAsync(id))!.Pending);

        await _t.Wms.Master.ApplyItemAsync(TestWorkspace.Item(id, "TE-20", "Té verde x20", version: 2, op: VortexChangeOperation.Deleted));
        Assert.Equal(0, (await _t.Wms.Master.GetItemsAsync("TE-20", null, 1, 10)).Total);
    }

    [Fact]
    public async Task Store_de_sincronizacion_conserva_orden_cursores_y_rechazos()
    {
        var store = _t.Wms.SyncStore;
        var first = new VortexOutboxEntry(Guid.NewGuid(), VortexOutboxKind.Event, DateTimeOffset.UtcNow, EventType: "a.b",
            Payload: System.Text.Json.JsonSerializer.SerializeToElement(new { n = 1 }));
        var second = first with { Id = Guid.NewGuid(), EventType = "c.d" };
        await store.EnqueueAsync(first);
        await store.EnqueueAsync(second);
        await store.EnqueueAsync(first);   // reencolar el mismo Id no lo duplica

        Assert.Equal([first.Id, second.Id], (await store.PeekAsync(10)).Select(e => e.Id));

        await store.FailAsync(first.Id, "invalid_event_type", "Tipo desconocido", DateTimeOffset.UtcNow);
        Assert.Equal(second.Id, Assert.Single(await store.PeekAsync(10)).Id);
        Assert.Equal("invalid_event_type", Assert.Single(await store.GetFailedAsync()).ErrorCode);

        await store.SaveCursorAsync(VortexSync.MasterStream, 42);
        await store.SaveCursorAsync(VortexSync.MasterStream, 57);
        Assert.Equal(57, await store.GetCursorAsync(VortexSync.MasterStream));
        Assert.Equal(0, await store.GetCursorAsync(VortexSync.EventStream));

        await store.DismissFailedAsync(first.Id);
        Assert.Empty(await store.GetFailedAsync());
    }
}
