using VortexApi.Client;
using VortexApi.Client.Sync;
using VortexWms.Core.Domain;
using VortexWms.Core.Integration;

namespace VortexWms.Core.Tests;

/// <summary>Dos equipos del mismo almacén que se ven solo a través de los eventos del hub.</summary>
public sealed class MultiDeviceTests : IAsyncLifetime
{
    private readonly TestWorkspace _a = new("AAAA");
    private readonly TestWorkspace _b = new("BBBB");

    public async Task InitializeAsync()
    {
        await _a.InitializeAsync();
        await _b.InitializeAsync();
    }

    public async Task DisposeAsync()
    {
        await _a.DisposeAsync();
        await _b.DisposeAsync();
    }

    [Fact]
    public async Task Recepcion_transferencia_y_ajuste_de_un_equipo_se_replican_en_el_otro()
    {
        await _a.Wms.Receipts.ReceiveDirectAsync(_a.Central, null, [new DocumentLine(_a.Coffee, 50)]);
        await _a.Wms.Inventory.TransferAsync(_a.Central, _a.Store, [new DocumentLine(_a.Coffee, 20)], null);
        await _b.Wms.Receipts.ReceiveDirectAsync(_b.Central, null, [new DocumentLine(_b.Coffee, 5)]);

        await DeliverAsync(_a, _b);
        await DeliverAsync(_b, _a);
        await DeliverAsync(_a, _a);   // cada equipo recibe también lo propio (IncludeOwnEvents)
        await DeliverAsync(_b, _b);

        foreach (var device in new[] { _a, _b })
        {
            Assert.Equal(35, await device.OnHandAsync(device.Central, device.Coffee));
            Assert.Equal(20, await device.OnHandAsync(device.Store, device.Coffee));
            Assert.Equal(2, (await device.Wms.Receipts.GetReceiptsAsync(null, ReceiptStatuses.Received, 1, 10)).Total);
        }
    }

    [Fact]
    public async Task Venta_procesada_por_ambos_equipos_se_descuenta_una_sola_vez()
    {
        await _a.Wms.Receipts.ReceiveDirectAsync(_a.Store, null, [new DocumentLine(_a.Coffee, 10)]);
        await DeliverAsync(_a, _b);

        var sale = TestWorkspace.Event(SuiteEvents.SaleCompleted, new
        {
            saleId = Guid.NewGuid(), number = "B001-9", siteId = _a.Store,
            lines = new[] { new { itemId = _a.Coffee, quantity = 1m }, new { itemId = _a.Coffee, quantity = 2m } },
        });
        await _a.Wms.SyncHandlers.HandleSaleCompletedAsync(sale);
        await _b.Wms.SyncHandlers.HandleSaleCompletedAsync(sale);

        // Ambos publican el mismo stock.adjusted (mismo Id): el hub deduplica y cada uno ignora el del otro.
        var fromA = (await _a.OutboxAsync()).Last(e => e.EventType == SuiteEvents.StockAdjusted);
        var fromB = (await _b.OutboxAsync()).Last(e => e.EventType == SuiteEvents.StockAdjusted);
        Assert.Equal(fromA.Id, fromB.Id);

        await DeliverAsync(_a, _b);
        await DeliverAsync(_b, _a);
        Assert.Equal(7, await _a.OnHandAsync(_a.Store, _a.Coffee));
        Assert.Equal(7, await _b.OnHandAsync(_b.Store, _b.Coffee));
    }

    [Fact]
    public async Task Despacho_en_un_equipo_cierra_el_pedido_y_libera_la_reserva_en_el_otro()
    {
        await _a.Wms.Receipts.ReceiveDirectAsync(_a.Central, null, [new DocumentLine(_a.Coffee, 10)]);
        await DeliverAsync(_a, _b);
        var orderId = Guid.NewGuid();
        var order = TestWorkspace.Event(SuiteEvents.OrderCreated,
            new OrderCreated(orderId, "PED-77", Guid.NewGuid(), _a.Central, [new OrderLine(_a.Coffee, 4, 18.5m)], 74, "PEN"), source: "CRM");
        await _a.Wms.SyncHandlers.HandleOrderCreatedAsync(order);
        await _b.Wms.SyncHandlers.HandleOrderCreatedAsync(order);

        await _a.Wms.Orders.DispatchAsync(orderId, "Shalom", "SH-1");
        await DeliverAsync(_a, _b);

        var stock = await _b.Wms.Inventory.GetStockRowAsync(_b.Central, _b.Coffee);
        Assert.Equal(6, stock!.OnHand);
        Assert.Equal(0, stock.Reserved);
        var dispatched = Assert.Single((await _b.Wms.Orders.GetOrdersAsync(null, OrderStatuses.Dispatched, 1, 10)).Items);
        Assert.Equal("SH-1", dispatched.TrackingNumber);
    }

    [Fact]
    public async Task Recepcion_esperada_cerrada_en_un_equipo_se_cierra_en_el_otro()
    {
        var po = TestWorkspace.Event(SuiteEvents.PurchaseOrderCreated, new
        {
            purchaseOrderId = Guid.NewGuid(), supplierId = Guid.NewGuid(), siteId = _a.Central,
            lines = new[] { new { itemId = _a.Sugar, quantity = 12m, unitCost = 3m } },
        }, source: "ERP");
        await _a.Wms.SyncHandlers.HandlePurchaseOrderCreatedAsync(po);
        await _b.Wms.SyncHandlers.HandlePurchaseOrderCreatedAsync(po);

        var receipt = Assert.Single((await _a.Wms.Receipts.GetReceiptsAsync(null, ReceiptStatuses.Expected, 1, 10)).Items);
        await _a.Wms.Receipts.ReceiveExpectedAsync(receipt.Id, new Dictionary<int, decimal> { [1] = 12 });
        await DeliverAsync(_a, _b);

        Assert.Equal(0, (await _b.Wms.Receipts.GetReceiptsAsync(null, ReceiptStatuses.Expected, 1, 10)).Total);
        Assert.Equal(12, await _b.OnHandAsync(_b.Central, _b.Sugar));
    }

    /// <summary>Entrega a <paramref name="to"/> los eventos que <paramref name="from"/> tiene en su cola, como lo haría el feed.</summary>
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
                SuiteEvents.ShipmentDispatched => handlers.HandleShipmentDispatchedAsync(e),
                SuiteEvents.OrderCreated => handlers.HandleOrderCreatedAsync(e),
                _ => Task.CompletedTask,
            });
        }
    }
}
