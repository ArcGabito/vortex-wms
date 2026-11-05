using VortexWms.Core.Domain;
using VortexWms.Core.Integration;

namespace VortexWms.Core.Tests;

/// <summary>Fase 1: costo promedio ponderado móvil por artículo y sede, y kardex valorizado.</summary>
public sealed class CostingTests : IAsyncLifetime
{
    private readonly TestWorkspace _t = new();

    public Task InitializeAsync() => _t.InitializeAsync();

    public Task DisposeAsync() => _t.DisposeAsync();

    [Fact]
    public async Task Una_secuencia_conocida_da_el_promedio_esperado()
    {
        _t.Clock.Advance(TimeSpan.FromMinutes(1));
        await _t.Wms.Receipts.ReceiveDirectAsync(_t.Central, null, [new DocumentLine(_t.Coffee, 10, 5m)]);
        _t.Clock.Advance(TimeSpan.FromMinutes(1));
        await _t.Wms.Receipts.ReceiveDirectAsync(_t.Central, null, [new DocumentLine(_t.Coffee, 10, 7m)]);   // (50 + 70) / 20 = 6
        _t.Clock.Advance(TimeSpan.FromMinutes(1));
        await _t.Wms.Inventory.AdjustAsync(_t.Central, _t.Coffee, null, -5, AdjustmentReasons.Damage, null);   // sale a 6
        _t.Clock.Advance(TimeSpan.FromMinutes(1));
        await _t.Wms.Receipts.ReceiveDirectAsync(_t.Central, null, [new DocumentLine(_t.Coffee, 5, 8m)]);    // (90 + 40) / 20 = 6.5

        var stock = (await _t.Wms.Inventory.GetStockRowAsync(_t.Central, _t.Coffee))!;
        Assert.Equal(20, stock.OnHand);
        Assert.Equal(6.5m, stock.AvgCost);
        Assert.Equal(130m, stock.CostValue);

        var kardex = (await _t.Wms.Inventory.GetMovementsAsync(new MovementQuery(ItemId: _t.Coffee))).Items.Reverse().ToList();
        Assert.Equal([5m, 7m, 6m, 8m], kardex.Select(m => m.UnitCost!.Value));
        Assert.Equal([50m, 70m, -30m, 40m], kardex.Select(m => m.TotalCost!.Value));
        Assert.Equal([50m, 120m, 90m, 130m], kardex.Select(m => m.ValueAfter!.Value));
        Assert.Equal(["02", "02", "13", "02"], kardex.Select(m => m.OperationType!));
    }

    [Fact]
    public async Task La_transferencia_llega_con_el_costo_del_origen()
    {
        _t.Clock.Advance(TimeSpan.FromMinutes(1));
        await _t.Wms.Receipts.ReceiveDirectAsync(_t.Central, null, [new DocumentLine(_t.Coffee, 10, 5m)]);
        _t.Clock.Advance(TimeSpan.FromMinutes(1));
        await _t.Wms.Receipts.ReceiveDirectAsync(_t.Store, null, [new DocumentLine(_t.Coffee, 10, 9m)]);
        _t.Clock.Advance(TimeSpan.FromMinutes(1));
        await _t.Wms.Inventory.TransferAsync(_t.Central, _t.Store, [new DocumentLine(_t.Coffee, 10)], null);

        Assert.Equal(0m, (await _t.Wms.Inventory.GetStockRowAsync(_t.Central, _t.Coffee))!.CostValue);
        var store = (await _t.Wms.Inventory.GetStockRowAsync(_t.Store, _t.Coffee))!;
        Assert.Equal(7m, store.AvgCost);   // (90 + 50) / 20
        Assert.Equal(140m, store.CostValue);
    }

    [Fact]
    public async Task Ventas_anulaciones_y_despachos_dan_el_costo_de_lo_vendido_y_el_margen()
    {
        _t.Clock.Advance(TimeSpan.FromMinutes(1));
        await _t.Wms.Receipts.ReceiveDirectAsync(_t.Store, null, [new DocumentLine(_t.Coffee, 10, 6m)]);
        var saleId = Guid.NewGuid();
        _t.Clock.Advance(TimeSpan.FromMinutes(1));
        await _t.Wms.SyncHandlers.HandleSaleCompletedAsync(TestWorkspace.Event(SuiteEvents.SaleCompleted, new
        {
            saleId, number = "B001-1", siteId = _t.Store,
            lines = new[] { new { itemId = _t.Coffee, quantity = 3m, unitPrice = 18.5m, discount = 0m, total = 55.5m } },
        }));
        _t.Clock.Advance(TimeSpan.FromMinutes(1));
        await _t.Wms.SyncHandlers.HandleSaleCompletedAsync(TestWorkspace.Event(SuiteEvents.SaleCompleted, new
        {
            saleId = Guid.NewGuid(), number = "B001-2", siteId = _t.Store,
            lines = new[] { new { itemId = _t.Coffee, quantity = 2m, unitPrice = 18.5m, discount = 0m, total = 37m } },
        }));
        _t.Clock.Advance(TimeSpan.FromMinutes(1));
        await _t.Wms.SyncHandlers.HandleSaleVoidedAsync(TestWorkspace.Event(SuiteEvents.SaleVoided, new { saleId, number = "B001-1" }));

        var summary = await _t.Wms.Inventory.GetDashboardAsync();
        Assert.Equal(48m, summary.StockValue);       // 8 × 6
        Assert.Equal(12m, summary.CostOfSales30d);   // solo la venta que quedó: 2 × 6
        Assert.Equal(37m, summary.Sales30d);
        Assert.Equal(25m, summary.Margin30d);
    }

    [Fact]
    public async Task Los_eventos_llevan_el_costo()
    {
        _t.Clock.Advance(TimeSpan.FromMinutes(1));
        await _t.Wms.Receipts.ReceiveDirectAsync(_t.Central, null, [new DocumentLine(_t.Coffee, 4, 5.25m)]);
        var outbox = await _t.OutboxAsync();
        var adjusted = outbox.Single(e => e.EventType == SuiteEvents.StockAdjusted).Payload!.Value;
        Assert.Equal(5.25m, adjusted.GetProperty("unitCost").GetDecimal());
        Assert.Equal(21m, adjusted.GetProperty("totalCost").GetDecimal());
        var line = outbox.Single(e => e.EventType == SuiteEvents.GoodsReceived).Payload!.Value.GetProperty("lines")[0];
        Assert.Equal(5.25m, line.GetProperty("unitCost").GetDecimal());
        Assert.Equal(21m, line.GetProperty("totalCost").GetDecimal());
    }

    [Fact]
    public async Task Solo_los_roles_con_permiso_ven_los_costos()
    {
        _t.Clock.Advance(TimeSpan.FromMinutes(1));
        await _t.Wms.Receipts.ReceiveDirectAsync(_t.Central, null, [new DocumentLine(_t.Coffee, 10, 5m)]);
        _t.Context.Role = WmsPermissions.Operator;

        var stock = (await _t.Wms.Inventory.GetStockRowAsync(_t.Central, _t.Coffee))!;
        Assert.Null(stock.AvgCost);
        Assert.Null(stock.CostValue);
        Assert.Null((await _t.Wms.Inventory.GetDashboardAsync()).StockValue);
        Assert.All((await _t.Wms.Inventory.GetMovementsAsync(new MovementQuery())).Items, m => Assert.Null(m.UnitCost));
        Assert.DoesNotContain("costo_promedio", await _t.Wms.Inventory.ExportStockCsvAsync(new StockQuery()));

        _t.Context.Role = WmsPermissions.Supervisor;
        Assert.Equal(5m, (await _t.Wms.Inventory.GetStockRowAsync(_t.Central, _t.Coffee))!.AvgCost);
    }
}

/// <summary>El kardex valorizado depende del orden: un movimiento atrasado deja el mismo resultado en todos los equipos.</summary>
public sealed class CostingMultiDeviceTests : IAsyncLifetime
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
    public async Task Un_movimiento_atrasado_deja_el_mismo_kardex_valorizado_en_dos_equipos()
    {
        // 15:00 A recibe 10 a 5. 15:30 B (sin conexión) recibe 10 a 8. 16:00 A vende 4 sin saber de B.
        await _a.Wms.Receipts.ReceiveDirectAsync(_a.Central, null, [new DocumentLine(_a.Coffee, 10, 5m)]);
        _b.Clock.Advance(TimeSpan.FromMinutes(30));
        await _b.Wms.Receipts.ReceiveDirectAsync(_b.Central, null, [new DocumentLine(_b.Coffee, 10, 8m)]);
        _a.Clock.Advance(TimeSpan.FromHours(1));
        await _a.Wms.Inventory.AdjustAsync(_a.Central, _a.Coffee, null, -4, AdjustmentReasons.Loss, null);
        Assert.Equal(5m, (await _a.Wms.Inventory.GetMovementsAsync(new MovementQuery(PageSize: 1))).Items[0].UnitCost);

        await _hub.SyncAsync(_a, _b);

        foreach (var device in new[] { _a, _b })
        {
            var stock = (await device.Wms.Inventory.GetStockRowAsync(device.Central, device.Coffee))!;
            Assert.Equal(16, stock.OnHand);
            Assert.Equal(6.5m, stock.AvgCost);
            Assert.Equal(104m, stock.CostValue);
            var kardex = (await device.Wms.Inventory.GetMovementsAsync(new MovementQuery(ItemId: device.Coffee))).Items.Reverse().ToList();
            Assert.Equal([5m, 8m, 6.5m], kardex.Select(m => m.UnitCost!.Value));
            Assert.Equal([10m, 20m, 16m], kardex.Select(m => m.OnHandAfter));
        }
    }
}
