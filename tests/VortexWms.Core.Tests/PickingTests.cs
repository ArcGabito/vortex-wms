using VortexWms.Core.Domain;
using VortexWms.Core.Integration;

namespace VortexWms.Core.Tests;

/// <summary>Fase 4b1 de la hoja de ruta: picking guiado por ubicación, despacho parcial y pedidos por aprobar.</summary>
public sealed class PickingTests : IAsyncLifetime
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

    private async Task<Guid> LocationAsync(string code, string kind = LocationKinds.Storage) =>
        await _a.Wms.Locations.SaveAsync(null, new LocationDraft(_a.Central, code, kind));

    private async Task<Guid> OrderAsync(decimal coffee, decimal sugar = 0, string? approval = null)
    {
        var id = Guid.NewGuid();
        List<OrderLine> lines = [new OrderLine(_a.Coffee, coffee, 18.5m)];
        if (sugar > 0)
            lines.Add(new OrderLine(_a.Sugar, sugar, 4m));
        var e = TestWorkspace.Event(SuiteEvents.OrderCreated,
            new OrderCreated(id, "PED-1", Guid.NewGuid(), _a.Central, lines, 100, "PEN", ApprovalStatus: approval), source: "ERP");
        await _a.Wms.SyncHandlers.HandleOrderCreatedAsync(e);
        await _b.Wms.SyncHandlers.HandleOrderCreatedAsync(e);
        return id;
    }

    [Fact]
    public async Task El_recorrido_va_por_ubicacion_y_lo_tomado_sale_de_esas_ubicaciones()
    {
        var b1 = await LocationAsync("B-01-01");
        var a2 = await LocationAsync("A-02-01");
        var pick = await LocationAsync("P-01", LocationKinds.Picking);
        await _a.Wms.Receipts.ReceiveDirectAsync(_a.Central, null, [new DocumentLine(_a.Coffee, 20), new DocumentLine(_a.Sugar, 10)]);
        await _a.Wms.Locations.RelocateAsync(_a.Central, _a.Coffee, null, pick, 3);
        await _a.Wms.Locations.RelocateAsync(_a.Central, _a.Coffee, null, b1, 10);
        await _a.Wms.Locations.RelocateAsync(_a.Central, _a.Sugar, null, a2, 10);
        var orderId = await OrderAsync(coffee: 8, sugar: 4);

        // Café: primero picking (3), después almacenamiento (5 de B-01-01); azúcar de A-02-01. Ordenado por código.
        var list = (await _a.Wms.Orders.GetPickListAsync(orderId))!;
        Assert.Equal([("A-02-01", 4m), ("B-01-01", 5m), ("P-01", 3m)], list.Stops.Select(s => (s.LocationCode!, s.ToPick)));
        Assert.All(list.Needs, n => Assert.Equal(0, n.Missing));

        await _a.Wms.Orders.ConfirmPickAsync(orderId, _a.Coffee, pick, 3);
        await _a.Wms.Orders.ConfirmPickAsync(orderId, _a.Coffee, b1, 5);
        await _a.Wms.Orders.ConfirmPickAsync(orderId, _a.Sugar, a2, 4);
        Assert.Equal(OrderStatuses.Picking, (await _a.Wms.Orders.GetAsync(orderId))!.Status);
        await Assert.ThrowsAsync<WmsValidationException>(() => _a.Wms.Orders.ConfirmPickAsync(orderId, _a.Coffee, b1, 1));   // ya está todo
        Assert.True((await _a.Wms.Orders.GetPickListAsync(orderId))!.Complete);

        await _a.Wms.Orders.DispatchAsync(orderId, "Shalom", null);
        Assert.Equal(OrderStatuses.Dispatched, (await _a.Wms.Orders.GetAsync(orderId))!.Status);
        decimal In(Guid loc, Guid item, IReadOnlyList<LocationStockRow> rows) => rows.Where(r => r.ItemId == item).Sum(r => r.Quantity);
        Assert.Equal(0, In(pick, _a.Coffee, await _a.Wms.Locations.GetLocationStockAsync(pick)));
        Assert.Equal(5, In(b1, _a.Coffee, await _a.Wms.Locations.GetLocationStockAsync(b1)));
        Assert.Equal(6, In(a2, _a.Sugar, await _a.Wms.Locations.GetLocationStockAsync(a2)));
        Assert.Equal(12, await _a.OnHandAsync(_a.Central, _a.Coffee));   // los 7 sin ubicación siguen ahí
        Assert.Contains((await _a.Wms.Locations.GetPendingPutawayAsync(_a.Central)), p => p.ItemId == _a.Coffee && p.Pending == 7);
    }

    [Fact]
    public async Task Con_faltante_sale_un_despacho_parcial_y_el_resto_queda_reservado()
    {
        await _a.Wms.Receipts.ReceiveDirectAsync(_a.Central, null, [new DocumentLine(_a.Coffee, 6)]);
        await _b.Wms.Receipts.ReceiveDirectAsync(_a.Central, null, [new DocumentLine(_a.Coffee, 6)]);
        var orderId = await OrderAsync(coffee: 10);

        var list = (await _a.Wms.Orders.GetPickListAsync(orderId))!;
        Assert.Equal(4, list.Needs.Single().Missing);   // hay 6 de 10
        await _a.Wms.Orders.ConfirmPickAsync(orderId, _a.Coffee, null, 6);

        await Assert.ThrowsAsync<WmsValidationException>(() => _a.Wms.Orders.DispatchAsync(orderId, null, null));
        await _a.Wms.Orders.DispatchAsync(orderId, null, null, allowPartial: true);
        var order = (await _a.Wms.Orders.GetAsync(orderId))!;
        Assert.Equal((OrderStatuses.Partial, 6m), (order.Status, order.Shipped));
        var stock = (await _a.Wms.Inventory.GetStockRowAsync(_a.Central, _a.Coffee))!;
        Assert.Equal((0m, 4m), (stock.OnHand, stock.Reserved));

        var shipped = (await _a.OutboxAsync()).Single(e => e.EventType == SuiteEvents.ShipmentDispatched);
        var payload = shipped.Payload!.Value;
        Assert.True(payload.GetProperty("partial").GetBoolean());
        Assert.Equal(6, payload.GetProperty("lines")[0].GetProperty("quantity").GetDecimal());

        // El otro equipo ve el despacho parcial: el pedido sigue abierto con 4 reservados.
        await _b.Wms.SyncHandlers.HandleShipmentDispatchedAsync(new VortexApi.Client.VortexEvent(shipped.Id, 1, shipped.EventType!, "WMS", 1,
            shipped.SubjectType, shipped.SubjectId, shipped.CreatedAt, shipped.CreatedAt, null, shipped.Payload!.Value));
        var alla = (await _b.Wms.Orders.GetAsync(orderId))!;
        Assert.Equal((OrderStatuses.Partial, 6m), (alla.Status, alla.Shipped));
        Assert.Equal(4, (await _b.Wms.Inventory.GetStockRowAsync(_a.Central, _a.Coffee))!.Reserved);

        // Llega el resto y sale el segundo despacho (sin picking: todo lo que falta).
        await _a.Wms.Receipts.ReceiveDirectAsync(_a.Central, null, [new DocumentLine(_a.Coffee, 4)]);
        await _a.Wms.Orders.DispatchAsync(orderId, null, null);
        Assert.Equal((OrderStatuses.Dispatched, 10m), ((await _a.Wms.Orders.GetAsync(orderId))!.Status, (await _a.Wms.Orders.GetAsync(orderId))!.Shipped));
        var envios = (await _a.OutboxAsync()).Where(e => e.EventType == SuiteEvents.ShipmentDispatched).ToList();
        Assert.Equal(2, envios.Select(e => e.Id).Distinct().Count());
        Assert.Equal(0, (await _a.Wms.Inventory.GetStockRowAsync(_a.Central, _a.Coffee))!.Reserved);
    }

    [Fact]
    public async Task Un_pedido_por_aprobar_no_reserva_ni_se_prepara_hasta_order_approved()
    {
        await _a.Wms.Receipts.ReceiveDirectAsync(_a.Central, null, [new DocumentLine(_a.Coffee, 10)]);
        var orderId = await OrderAsync(coffee: 4, approval: OrderApprovals.Pending);
        Assert.True((await _a.Wms.Orders.GetAsync(orderId))!.PendingApproval);
        Assert.Equal(0, (await _a.Wms.Inventory.GetStockRowAsync(_a.Central, _a.Coffee))!.Reserved);
        await Assert.ThrowsAsync<WmsValidationException>(() => _a.Wms.Orders.ConfirmPickAsync(orderId, _a.Coffee, null, 4));
        await Assert.ThrowsAsync<WmsValidationException>(() => _a.Wms.Orders.DispatchAsync(orderId, null, null));

        var approved = TestWorkspace.Event(SuiteEvents.OrderApproved, new OrderApproved(orderId, "PED-1", "Ana"), source: "ERP");
        await _a.Wms.SyncHandlers.HandleOrderApprovedAsync(approved);
        await _a.Wms.SyncHandlers.HandleOrderApprovedAsync(approved);   // repetido: reserva una sola vez
        Assert.Equal(4, (await _a.Wms.Inventory.GetStockRowAsync(_a.Central, _a.Coffee))!.Reserved);
        await _a.Wms.Orders.ConfirmPickAsync(orderId, _a.Coffee, null, 4);
        await _a.Wms.Orders.DispatchAsync(orderId, null, null);
        Assert.Equal(6, await _a.OnHandAsync(_a.Central, _a.Coffee));
    }
}
