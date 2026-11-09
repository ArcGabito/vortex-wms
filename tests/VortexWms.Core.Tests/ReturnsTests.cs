using VortexApi.Client;
using VortexWms.Core.Domain;
using VortexWms.Core.Integration;
using VortexWms.Core.Services;

namespace VortexWms.Core.Tests;

/// <summary>Fase 5b: devoluciones de clientes (stock, cuarentena o merma) y a proveedores; mermas con foto y aprobación.</summary>
public sealed class ReturnsTests : IAsyncLifetime
{
    private const string Png = "data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==";
    private static readonly Guid Customer = Guid.Parse("33333333-0000-0000-0000-000000000001");
    private static readonly Guid Supplier = Guid.Parse("33333333-0000-0000-0000-000000000003");
    private readonly TestWorkspace _t = new("AAAA");

    public async Task InitializeAsync()
    {
        await _t.InitializeAsync();
        await _t.Wms.Receipts.ReceiveDirectAsync(_t.Central, null, [new DocumentLine(_t.Coffee, 20, 6m), new DocumentLine(_t.Sugar, 10, 2m)]);
    }

    public Task DisposeAsync() => _t.DisposeAsync();

    private async Task<Guid> DispatchedOrderAsync(decimal coffee)
    {
        var id = Guid.NewGuid();
        await _t.Wms.SyncHandlers.HandleOrderCreatedAsync(TestWorkspace.Event(SuiteEvents.OrderCreated,
            new OrderCreated(id, "PED-9", Customer, _t.Central, [new OrderLine(_t.Coffee, coffee, 18.5m)], coffee * 18.5m, "PEN"), source: "ERP"));
        await _t.Wms.Orders.DispatchAsync(id, "Shalom", null);
        return id;
    }

    private async Task<VortexApi.Client.Sync.VortexOutboxEntry> LastAsync(string type) => (await _t.OutboxAsync()).Last(e => e.EventType == type);

    [Fact]
    public async Task La_devolucion_de_un_cliente_vuelve_al_stock_a_cuarentena_o_a_merma_sin_pasar_lo_despachado()
    {
        var order = await DispatchedOrderAsync(8);   // quedan 12
        var quarantine = await _t.Wms.Locations.SaveAsync(null, new LocationDraft(_t.Central, "CUA-01", LocationKinds.Quarantine));
        _t.Context.Role = WmsPermissions.Operator;

        var number = await _t.Wms.Returns.ReceiveCustomerReturnAsync(new CustomerReturnDraft(_t.Central, null, order,
            [new ReturnLineDraft(_t.Coffee, 3), new ReturnLineDraft(_t.Coffee, 2, Disposition: ReturnDispositions.Quarantine),
             new ReturnLineDraft(_t.Coffee, 1, Disposition: ReturnDispositions.Scrap)], "Llegó golpeado"));
        Assert.Equal(17m, await _t.OnHandAsync(_t.Central, _t.Coffee));   // 12 + 3 + 2 (la merma no vuelve)
        Assert.Equal(2m, (await _t.Wms.Locations.GetLocationStockAsync(quarantine)).Sum(s => s.Quantity));

        var e = (await LastAsync(SuiteEvents.ShipmentReturned)).Payload!.Value;
        Assert.Equal((number, Customer, order), (e.GetProperty("number").GetString(), e.GetProperty("customerId").GetGuid(), e.GetProperty("orderId").GetGuid()));
        Assert.Equal(["restock", "quarantine", "scrap"], e.GetProperty("lines").EnumerateArray().Select(l => l.GetProperty("disposition").GetString()));

        // Ya se devolvieron 6 de 8: no se pueden devolver 3 más.
        await Assert.ThrowsAsync<WmsValidationException>(() => _t.Wms.Returns.ReceiveCustomerReturnAsync(
            new CustomerReturnDraft(_t.Central, null, order, [new ReturnLineDraft(_t.Coffee, 3)], "Otra vez")));
        Assert.Single(await _t.Wms.Returns.GetReturnsAsync(ReturnKinds.Customer));
    }

    [Fact]
    public async Task La_devolucion_al_proveedor_sale_al_costo_promedio_y_solo_la_hace_el_supervisor()
    {
        _t.Context.Role = WmsPermissions.Operator;
        await Assert.ThrowsAnyAsync<WmsValidationException>(() => _t.Wms.Returns.ReturnToSupplierAsync(
            new SupplierReturnDraft(_t.Central, Supplier, null, [new ReturnLineDraft(_t.Sugar, 4)], "Vencido")));
        _t.Context.Role = WmsPermissions.Supervisor;
        await _t.Wms.Returns.ReturnToSupplierAsync(new SupplierReturnDraft(_t.Central, Supplier, null, [new ReturnLineDraft(_t.Sugar, 4)], "Vencido"));
        Assert.Equal(6m, await _t.OnHandAsync(_t.Central, _t.Sugar));
        var e = (await LastAsync(SuiteEvents.GoodsReturned)).Payload!.Value;
        var line = e.GetProperty("lines")[0];
        Assert.Equal((Supplier, 4m, 2m), (e.GetProperty("supplierId").GetGuid(), line.GetProperty("quantity").GetDecimal(), line.GetProperty("unitCost").GetDecimal()));
        var kardex = (await _t.Wms.Inventory.GetMovementsAsync(new MovementQuery(ItemId: _t.Sugar))).Items.First();
        Assert.Equal((MovementKinds.SupplierReturn, "06"), (kardex.Kind, kardex.OperationType));
        await Assert.ThrowsAsync<WmsValidationException>(() => _t.Wms.Returns.ReturnToSupplierAsync(
            new SupplierReturnDraft(_t.Central, Supplier, null, [new ReturnLineDraft(_t.Sugar, 7)], "Más de lo que hay")));
    }

    [Fact]
    public async Task La_merma_con_foto_baja_el_stock_solo_cuando_el_supervisor_la_aprueba()
    {
        _t.Context.Role = WmsPermissions.Operator;
        var rota = await _t.Wms.Returns.ReportShrinkageAsync(_t.Central, _t.Coffee, 2, AdjustmentReasons.Damage, "Se cayó del rack", Png);
        var perdida = await _t.Wms.Returns.ReportShrinkageAsync(_t.Central, _t.Coffee, 1, AdjustmentReasons.Loss, null, null);
        await Assert.ThrowsAsync<WmsValidationException>(() => _t.Wms.Returns.ReportShrinkageAsync(_t.Central, _t.Coffee, 1, AdjustmentReasons.Damage, null,
            "data:image/svg+xml;base64,PHN2Zz48L3N2Zz4="));
        Assert.Equal(20m, await _t.OnHandAsync(_t.Central, _t.Coffee));
        await Assert.ThrowsAnyAsync<WmsValidationException>(() => _t.Wms.Returns.DecideShrinkageAsync(rota, true));

        _t.Context.Role = WmsPermissions.Supervisor;
        var pending = await _t.Wms.Returns.GetShrinkageAsync(pendingOnly: true);
        Assert.Equal(2, pending.Count);
        Assert.Equal(Png, pending.Single(p => p.Id == rota).Photo);
        await _t.Wms.Returns.DecideShrinkageAsync(rota, true);
        await _t.Wms.Returns.DecideShrinkageAsync(perdida, false, "Apareció");
        Assert.Equal(18m, await _t.OnHandAsync(_t.Central, _t.Coffee));
        Assert.Empty(await _t.Wms.Returns.GetShrinkageAsync(pendingOnly: true));
        Assert.Equal(6m, (await _t.Wms.Returns.GetShrinkageAsync()).Single(s => s.Id == rota).UnitCost);
        await Assert.ThrowsAsync<WmsValidationException>(() => _t.Wms.Returns.DecideShrinkageAsync(rota, true));
    }
}
