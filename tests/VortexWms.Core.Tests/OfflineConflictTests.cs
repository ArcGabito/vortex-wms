using VortexWms.Core.Domain;
using VortexWms.Core.Integration;
using VortexWms.Core.Services;

namespace VortexWms.Core.Tests;

/// <summary>
/// Fase 0: dos equipos del mismo almacén trabajan sin conexión sobre los mismos documentos y después sincronizan con el
/// hub. El stock nunca se cuenta dos veces, los estados llegan a todos y, si hicieron lo mismo con datos distintos,
/// gana la versión que el hub recibió primero y el otro equipo lo ve como conflicto.
/// </summary>
public sealed class OfflineConflictTests : IAsyncLifetime
{
    private readonly TestWorkspace _a = new("AAAA");
    private readonly TestWorkspace _b = new("BBBB");
    private readonly FakeHub _hub = new();

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
    public async Task Dos_equipos_despachan_el_mismo_pedido_sin_conexion_y_el_stock_baja_una_sola_vez()
    {
        var orderId = await OrderWithStockAsync(_a.Central, 10, 4);

        await _a.Wms.Orders.DispatchAsync(orderId, "Shalom", null);
        await _b.Wms.Orders.DispatchAsync(orderId, "Shalom", null);
        await _hub.SyncAsync(_a, _b);

        Assert.Single(_hub.Events, e => e.Type == SuiteEvents.ShipmentDispatched);
        foreach (var device in new[] { _a, _b })
        {
            var stock = (await device.Wms.Inventory.GetStockRowAsync(device.Central, device.Coffee))!;
            Assert.Equal(6, stock.OnHand);
            Assert.Equal(0, stock.Reserved);
            Assert.Empty(await device.Wms.Conflicts.GetOpenAsync());
        }
    }

    [Fact]
    public async Task Despachado_desde_sedes_distintas_queda_la_version_del_hub_y_el_conflicto_a_la_vista()
    {
        var orderId = await OrderWithStockAsync(_a.Central, 10, 4);
        await _a.Wms.Receipts.ReceiveDirectAsync(_a.Store, null, [new DocumentLine(_a.Coffee, 10)]);
        await _hub.SyncAsync(_a, _b);

        await _a.Wms.Orders.DispatchAsync(orderId, "Shalom", "SH-1");
        await _b.Wms.Orders.DispatchAsync(orderId, "Olva", "OL-9", _b.Store);
        await _hub.SyncAsync(_a, _b);   // A llega primero: gana su despacho

        foreach (var device in new[] { _a, _b })
        {
            Assert.Equal(6, await device.OnHandAsync(device.Central, device.Coffee));
            Assert.Equal(10, await device.OnHandAsync(device.Store, device.Coffee));
            var order = Assert.Single((await device.Wms.Orders.GetOrdersAsync(null, OrderStatuses.Dispatched, 1, 10)).Items);
            Assert.Equal("SH-1", order.TrackingNumber);
            Assert.Equal(device.Central, order.SiteId);
        }
        Assert.Empty(await _a.Wms.Conflicts.GetOpenAsync());
        var conflicts = await _b.Wms.Conflicts.GetOpenAsync();
        Assert.Contains(conflicts, c => c.Kind == SyncConflictKinds.DispatchedTwice && c.DocumentId == orderId);
        Assert.Contains(conflicts, c => c.Kind == SyncConflictKinds.Movement);

        await _b.Wms.Conflicts.DismissAsync(conflicts[0].Id);
        Assert.Equal(conflicts.Count - 1, (await _b.Wms.Conflicts.GetOpenAsync()).Count);
    }

    [Fact]
    public async Task Cancelar_un_pedido_en_un_equipo_libera_la_reserva_en_los_demas()
    {
        var orderId = await OrderWithStockAsync(_a.Central, 10, 4);
        Assert.Equal(4, (await _b.Wms.Inventory.GetStockRowAsync(_b.Central, _b.Coffee))!.Reserved);

        await _a.Wms.Orders.CancelAsync(orderId, "El cliente desistió");
        await _hub.SyncAsync(_a, _b);

        Assert.Equal(0, (await _b.Wms.Inventory.GetStockRowAsync(_b.Central, _b.Coffee))!.Reserved);
        Assert.Single((await _b.Wms.Orders.GetOrdersAsync(null, OrderStatuses.Cancelled, 1, 10)).Items);
        Assert.Equal(10, await _b.OnHandAsync(_b.Central, _b.Coffee));
    }

    [Fact]
    public async Task Cancelado_en_un_equipo_y_despachado_en_otro_prevalece_el_despacho()
    {
        var orderId = await OrderWithStockAsync(_a.Central, 10, 4);

        await _a.Wms.Orders.CancelAsync(orderId);
        await _b.Wms.Orders.DispatchAsync(orderId, null, null);
        await _hub.SyncAsync(_b, _a);

        foreach (var device in new[] { _a, _b })
        {
            Assert.Equal(6, await device.OnHandAsync(device.Central, device.Coffee));
            Assert.Equal(0, (await device.Wms.Inventory.GetStockRowAsync(device.Central, device.Coffee))!.Reserved);
            Assert.Single((await device.Wms.Orders.GetOrdersAsync(null, OrderStatuses.Dispatched, 1, 10)).Items);
        }
        Assert.Contains(await _a.Wms.Conflicts.GetOpenAsync(), c => c.Kind == SyncConflictKinds.DispatchedAfterCancel);
        Assert.Contains(await _b.Wms.Conflicts.GetOpenAsync(), c => c.Kind == SyncConflictKinds.CancelAfterDispatch);
    }

    [Fact]
    public async Task La_preparacion_empezada_en_un_equipo_se_ve_en_el_otro()
    {
        var orderId = await OrderWithStockAsync(_a.Central, 10, 4);
        await _a.Wms.Orders.StartPickingAsync(orderId);
        await _hub.SyncAsync(_a, _b);

        Assert.Single((await _b.Wms.Orders.GetOrdersAsync(null, OrderStatuses.Picking, 1, 10)).Items);
    }

    [Fact]
    public async Task La_misma_orden_de_compra_recibida_en_dos_equipos_con_cantidades_distintas_converge()
    {
        var receiptId = await PurchaseOrderAsync(12);

        await _a.Wms.Receipts.ReceiveExpectedAsync(receiptId, new Dictionary<int, decimal> { [1] = 12 });
        await _b.Wms.Receipts.ReceiveExpectedAsync(receiptId, new Dictionary<int, decimal> { [1] = 10 });
        await _hub.SyncAsync(_a, _b);

        foreach (var device in new[] { _a, _b })
        {
            Assert.Equal(12, await device.OnHandAsync(device.Central, device.Sugar));
            Assert.Equal(12, Assert.Single((await device.Wms.Receipts.GetReceiptsAsync(null, ReceiptStatuses.Received, 1, 10)).Items).ReceivedUnits);
        }
        Assert.Empty(await _a.Wms.Conflicts.GetOpenAsync());
        Assert.Contains(await _b.Wms.Conflicts.GetOpenAsync(), c => c.Kind == SyncConflictKinds.ReceiptQuantities && c.DocumentId == receiptId);
    }

    [Fact]
    public async Task Anular_una_recepcion_esperada_llega_a_los_demas_equipos()
    {
        var receiptId = await PurchaseOrderAsync(12);
        await _a.Wms.Receipts.CancelAsync(receiptId);
        await _hub.SyncAsync(_a, _b);

        Assert.Equal(0, (await _b.Wms.Receipts.GetReceiptsAsync(null, ReceiptStatuses.Expected, 1, 10)).Total);
        Assert.Equal(1, (await _b.Wms.Receipts.GetReceiptsAsync(null, ReceiptStatuses.Cancelled, 1, 10)).Total);
    }

    [Fact]
    public async Task Ubicacion_y_minimo_se_comparten_y_gana_el_cambio_mas_reciente()
    {
        await _a.Wms.Inventory.UpdateStockSettingsAsync(_a.Central, _a.Coffee, "a-01-01", 5);
        _b.Clock.Advance(TimeSpan.FromMinutes(10));
        await _b.Wms.Inventory.UpdateStockSettingsAsync(_b.Central, _b.Coffee, "b-02-03", 8);
        await _hub.SyncAsync(_b, _a);   // el más nuevo (B) llega primero; el viejo (A) no lo pisa

        foreach (var device in new[] { _a, _b })
        {
            var stock = (await device.Wms.Inventory.GetStockRowAsync(device.Central, device.Coffee))!;
            Assert.Equal("B-02-03", stock.Bin);
            Assert.Equal(8, stock.ReorderPoint);
        }
    }

    [Fact]
    public async Task Los_eventos_aplicados_hace_mas_de_seis_meses_se_olvidan()
    {
        await _a.Wms.SyncHandlers.HandleSaleCompletedAsync(TestWorkspace.Event(SuiteEvents.SaleCompleted,
            new { saleId = Guid.NewGuid(), siteId = _a.Store, lines = Array.Empty<object>() }));
        Assert.Equal(0, await _a.Wms.SyncHandlers.PruneProcessedEventsAsync());

        _a.Clock.Advance(TimeSpan.FromDays(181));
        Assert.Equal(1, await _a.Wms.SyncHandlers.PruneProcessedEventsAsync());
    }

    /// <summary>Stock en la sede (recibido en A) y un pedido del CRM que llega a los dos equipos.</summary>
    private async Task<Guid> OrderWithStockAsync(Guid site, decimal stock, decimal ordered)
    {
        await _a.Wms.Receipts.ReceiveDirectAsync(site, null, [new DocumentLine(_a.Coffee, stock)]);
        var orderId = Guid.NewGuid();
        var order = TestWorkspace.Event(SuiteEvents.OrderCreated,
            new OrderCreated(orderId, "PED-77", Guid.NewGuid(), site, [new OrderLine(_a.Coffee, ordered, 18.5m)], 74, "PEN"), source: "CRM");
        await _a.Wms.SyncHandlers.HandleOrderCreatedAsync(order);
        await _b.Wms.SyncHandlers.HandleOrderCreatedAsync(order);
        await _hub.SyncAsync(_a, _b);
        return orderId;
    }

    /// <summary>Orden de compra del ERP que llega a los dos equipos; devuelve el Id de la recepción esperada.</summary>
    private async Task<Guid> PurchaseOrderAsync(decimal quantity)
    {
        var po = TestWorkspace.Event(SuiteEvents.PurchaseOrderCreated, new
        {
            purchaseOrderId = Guid.NewGuid(), supplierId = Guid.NewGuid(), siteId = _a.Central,
            lines = new[] { new { itemId = _a.Sugar, quantity, unitCost = 3m } },
        }, source: "ERP");
        await _a.Wms.SyncHandlers.HandlePurchaseOrderCreatedAsync(po);
        await _b.Wms.SyncHandlers.HandlePurchaseOrderCreatedAsync(po);
        return Assert.Single((await _a.Wms.Receipts.GetReceiptsAsync(null, ReceiptStatuses.Expected, 1, 10)).Items).Id;
    }
}
