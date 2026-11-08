using System.Text.Json;
using VortexApi.Client;
using VortexWms.Core.Domain;
using VortexWms.Core.Integration;

namespace VortexWms.Core.Tests;

/// <summary>Fase 4b2 de la hoja de ruta: reabastecimiento, tareas por operario, empaque y picking por grupo.</summary>
public sealed class TasksTests : IAsyncLifetime
{
    private readonly TestWorkspace _a = new("AAAA", WmsPermissions.Supervisor);
    private readonly TestWorkspace _b = new("BBBB", WmsPermissions.Operator);
    private readonly FakeHub _hub = new();
    private static readonly Guid Rosa = Guid.Parse("00000000-0000-0000-0000-0000000000a2");

    public async Task InitializeAsync()
    {
        await _a.InitializeAsync();
        await _b.InitializeAsync();
        (_b.Context.UserId, _b.Context.UserName) = (Rosa, "Rosa");
    }

    public async Task DisposeAsync()
    {
        await _a.DisposeAsync();
        await _b.DisposeAsync();
    }

    private async Task<Guid> OrderAsync(string number, decimal coffee, params TestWorkspace[] devices)
    {
        var id = Guid.NewGuid();
        var e = TestWorkspace.Event(SuiteEvents.OrderCreated,
            new OrderCreated(id, number, Guid.NewGuid(), _a.Central, [new OrderLine(_a.Coffee, coffee, 18.5m)], coffee * 18.5m, "PEN"), source: "ERP");
        foreach (var device in devices.Length == 0 ? [_a] : devices)
            await device.Wms.SyncHandlers.HandleOrderCreatedAsync(e);
        return id;
    }

    [Fact]
    public async Task La_ubicacion_de_picking_bajo_su_minimo_se_reabastece_desde_almacenamiento()
    {
        var pick = await _a.Wms.Locations.SaveAsync(null, new LocationDraft(_a.Central, "P-01", LocationKinds.Picking, FixedItemId: _a.Coffee,
            MinQuantity: 2, MaxQuantity: 10));
        var b1 = await _a.Wms.Locations.SaveAsync(null, new LocationDraft(_a.Central, "B-01", LocationKinds.Storage));
        await Assert.ThrowsAsync<WmsValidationException>(() => _a.Wms.Locations.SaveAsync(pick,
            new LocationDraft(_a.Central, "P-01", LocationKinds.Picking, FixedItemId: _a.Coffee, MinQuantity: 5, MaxQuantity: 3)));
        await _a.Wms.Receipts.ReceiveDirectAsync(_a.Central, null, [new DocumentLine(_a.Coffee, 21)]);
        await _a.Wms.Locations.RelocateAsync(_a.Central, _a.Coffee, null, b1, 20);
        await _a.Wms.Locations.RelocateAsync(_a.Central, _a.Coffee, null, pick, 1);

        var need = Assert.Single(await _a.Wms.Locations.GetReplenishmentsAsync(_a.Central));
        Assert.Equal((pick, 1m, 9m, (Guid?)b1), (need.LocationId, need.Current, need.Suggested, need.FromLocationId));

        // El supervisor lo asigna; al reabastecer, la tarea queda hecha.
        var task = await _a.Wms.Tasks.CreateAsync(new TaskDraft(_a.Central, TaskKinds.Replenish, _a.Context.UserId!.Value, "Luis", LocationId: pick));
        await _a.Wms.Locations.ReplenishAsync(pick, b1, need.Suggested);
        Assert.Empty(await _a.Wms.Locations.GetReplenishmentsAsync(_a.Central));
        Assert.Equal(10, (await _a.Wms.Locations.GetLocationStockAsync(pick)).Sum(s => s.Quantity));
        Assert.Equal(TaskStatuses.Done, (await _a.Wms.Tasks.GetTasksAsync(mine: false, includeClosed: true)).Single(t => t.Id == task).Status);
    }

    [Fact]
    public async Task Un_pedido_asignado_solo_lo_prepara_su_operario_y_la_tarea_se_cierra_al_despachar()
    {
        await _b.Wms.Tasks.RegisterCurrentUserAsync();
        await _hub.SyncAsync(_b, _a);
        var rosa = Assert.Single(await _a.Wms.Tasks.GetOperatorsAsync());
        Assert.Equal(("Rosa", WmsPermissions.Operator), (rosa.Name, rosa.Role));

        await _a.Wms.Receipts.ReceiveDirectAsync(_a.Central, null, [new DocumentLine(_a.Coffee, 10)]);
        await _hub.SyncAsync(_a, _b);
        var orderId = await OrderAsync("PED-7", 4, _a, _b);

        // Un operario no asigna tareas.
        await Assert.ThrowsAsync<WmsValidationException>(() =>
            _b.Wms.Tasks.CreateAsync(new TaskDraft(_a.Central, TaskKinds.Pick, Rosa, "Rosa", OrderId: orderId)));
        await _a.Wms.Tasks.CreateAsync(new TaskDraft(_a.Central, TaskKinds.Pick, Rosa, "Rosa", OrderId: orderId));
        await _hub.SyncAsync(_a, _b);
        var mine = Assert.Single(await _b.Wms.Tasks.GetTasksAsync(mine: true));
        Assert.Equal(("Preparar el pedido PED-7", orderId), (mine.Title, mine.OrderId));

        // Otro operario no lo prepara; Rosa sí.
        _a.Context.Role = WmsPermissions.Operator;
        await Assert.ThrowsAsync<WmsValidationException>(() => _a.Wms.Orders.ConfirmPickAsync(orderId, _a.Coffee, null, 4));
        _b.Clock.Advance(TimeSpan.FromMinutes(5));
        await _b.Wms.Orders.ConfirmPickAsync(orderId, _a.Coffee, null, 4);
        await _b.Wms.Orders.DispatchAsync(orderId, null, null);
        Assert.Empty(await _b.Wms.Tasks.GetTasksAsync(mine: true));

        await _hub.SyncAsync(_b, _a);
        Assert.Equal(TaskStatuses.Done, Assert.Single(await _a.Wms.Tasks.GetTasksAsync(mine: false, includeClosed: true)).Status);
    }

    [Fact]
    public async Task Lo_preparado_se_empaca_en_bultos_que_salen_con_el_despacho()
    {
        await _a.Wms.Receipts.ReceiveDirectAsync(_a.Central, null, [new DocumentLine(_a.Coffee, 10)]);
        var orderId = await OrderAsync("PED-8", 8);
        await _a.Wms.Orders.ConfirmPickAsync(orderId, _a.Coffee, null, 8);

        Assert.Equal(1, await _a.Wms.Orders.AddPackageAsync(orderId, 3.2m, [new DocumentLine(_a.Coffee, 5)]));
        await Assert.ThrowsAsync<WmsValidationException>(() => _a.Wms.Orders.AddPackageAsync(orderId, 2m, [new DocumentLine(_a.Coffee, 4)]));
        Assert.Equal(2, await _a.Wms.Orders.AddPackageAsync(orderId, 2m, [new DocumentLine(_a.Coffee, 3)]));
        var plan = await _a.Wms.Orders.GetPackingAsync(orderId);
        Assert.True(plan.AllPacked);
        Assert.Equal(5.2m, plan.TotalWeight);

        // Lo empacado no se deshace del picking sin deshacer antes el bulto.
        var pick = Assert.Single((await _a.Wms.Orders.GetPickListAsync(orderId))!.Picked);
        await Assert.ThrowsAsync<WmsValidationException>(() => _a.Wms.Orders.UndoPickAsync(pick.Id));

        await _a.Wms.Orders.DispatchAsync(orderId, "Shalom", null);
        var dispatched = (await _a.OutboxAsync()).Single(e => e.EventType == SuiteEvents.ShipmentDispatched);
        var packages = dispatched.Payload!.Value.GetProperty("packages");
        Assert.Equal(2, packages.GetArrayLength());
        Assert.Equal(3.2m, packages[0].GetProperty("weightKg").GetDecimal());
        Assert.All((await _a.Wms.Orders.GetPackingAsync(orderId)).Packages, p => Assert.NotNull(p.ShipmentId));
    }

    [Fact]
    public async Task El_picking_por_grupo_hace_un_recorrido_y_reparte_entre_los_pedidos()
    {
        var pick = await _a.Wms.Locations.SaveAsync(null, new LocationDraft(_a.Central, "P-01", LocationKinds.Picking));
        var b1 = await _a.Wms.Locations.SaveAsync(null, new LocationDraft(_a.Central, "B-01", LocationKinds.Storage));
        await _a.Wms.Receipts.ReceiveDirectAsync(_a.Central, null, [new DocumentLine(_a.Coffee, 16)]);
        await _a.Wms.Locations.RelocateAsync(_a.Central, _a.Coffee, null, pick, 6);
        await _a.Wms.Locations.RelocateAsync(_a.Central, _a.Coffee, null, b1, 10);
        var first = await OrderAsync("PED-1", 5);
        var second = await OrderAsync("PED-2", 4);

        var plan = await _a.Wms.Orders.GetBatchPickAsync([first, second]);
        Assert.Equal([("B-01", 3m), ("P-01", 6m)], plan.Stops.Select(s => (s.LocationCode!, s.ToPick)));
        Assert.Equal([("PED-1", 5m), ("PED-2", 1m)], plan.Stops[1].Splits.Select(s => (s.OrderNumber, s.Quantity)));

        await _a.Wms.Orders.ConfirmBatchPickAsync([first, second], _a.Coffee, pick, 6);
        await _a.Wms.Orders.ConfirmBatchPickAsync([first, second], _a.Coffee, b1, 3);
        await Assert.ThrowsAsync<WmsValidationException>(() => _a.Wms.Orders.ConfirmBatchPickAsync([first, second], _a.Coffee, b1, 1));
        Assert.True((await _a.Wms.Orders.GetBatchPickAsync([first, second])).Complete);
        Assert.True((await _a.Wms.Orders.GetPickListAsync(first))!.Complete);
        Assert.Equal(4, (await _a.Wms.Orders.GetPickListAsync(second))!.Picked.Sum(p => p.Quantity));
    }
}
