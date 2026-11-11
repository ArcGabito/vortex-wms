using VortexWms.Core.Domain;
using VortexWms.Core.Integration;
using VortexWms.Core.Services;

namespace VortexWms.Core.Tests;

/// <summary>Fase 6a: mínimo y máximo por sede, consumo promedio, cobertura y sugerencia de compra que sale al ERP.</summary>
public sealed class SupplyTests : IAsyncLifetime
{
    private readonly TestWorkspace _t = new("AAAA");
    private Guid _supplier;

    public async Task InitializeAsync()
    {
        await _t.InitializeAsync();
        _supplier = await _t.Wms.Master.CreatePartyAsync(new PartyDraft("Café del Valle SAC", "Company", false, true, "RUC", "20100000001", null, null, null));
        _t.Clock.Advance(TimeSpan.FromMinutes(1));
        await _t.Wms.Receipts.ReceiveDirectAsync(_t.Central, _supplier, [new DocumentLine(_t.Coffee, 100, 6m), new DocumentLine(_t.Sugar, 10, 2m)]);
    }

    public Task DisposeAsync() => _t.DisposeAsync();

    private async Task SellAsync(Guid item, decimal quantity)
    {
        _t.Clock.Advance(TimeSpan.FromMinutes(1));
        await _t.Wms.SyncHandlers.HandleSaleCompletedAsync(TestWorkspace.Event(SuiteEvents.SaleCompleted, new
        {
            saleId = Guid.NewGuid(), number = "B001-1", siteId = _t.Central,
            lines = new[] { new { itemId = item, quantity, unitPrice = 10m, discount = 0m, total = quantity * 10m } },
        }));
    }

    private async Task<SupplyRow> RowAsync(Guid item) => (await _t.Wms.Supply.GetCoverageAsync(_t.Central)).Single(r => r.ItemId == item);

    [Fact]
    public async Task Sugiere_completar_hasta_el_maximo_cuando_el_disponible_llega_al_minimo()
    {
        await _t.Wms.Inventory.UpdateStockSettingsAsync(_t.Central, _t.Coffee, null, reorderPoint: 20, maxStock: 100);
        await SellAsync(_t.Coffee, 60);
        var coffee = await RowAsync(_t.Coffee);
        Assert.Equal((2m, 20m, 0m), (coffee.DailyUsage, coffee.CoverageDays!.Value, coffee.Suggested));   // 60 en 30 días; 40 alcanzan 20 días
        Assert.Equal(_supplier, coffee.SupplierId);

        await SellAsync(_t.Coffee, 25);   // quedan 15, bajo el mínimo
        coffee = await RowAsync(_t.Coffee);
        Assert.Equal(85m, coffee.Suggested);

        // Sin mínimo ni máximo: punto de pedido = consumo del plazo (7 días) y se cubre plazo + 30 días.
        await SellAsync(_t.Sugar, 9);   // 0.3 por día; queda 1
        var sugar = await RowAsync(_t.Sugar);
        Assert.Equal((2.1m, 11m), (sugar.ReorderLevel!.Value, sugar.Suggested));   // ceil(0.3 × 37 − 1)

        var toOrder = await _t.Wms.Supply.GetCoverageAsync(_t.Central, onlyToOrder: true);
        Assert.Equal(2, toOrder.Count);
        var csv = await _t.Wms.Supply.ExportSuggestionCsvAsync(_t.Central);
        Assert.Contains("Café del Valle SAC", csv);
        Assert.Contains(",85,", csv);

        await Assert.ThrowsAsync<WmsValidationException>(() => _t.Wms.Inventory.UpdateStockSettingsAsync(_t.Central, _t.Coffee, null, 50, 10));
    }

    [Fact]
    public async Task La_solicitud_sale_al_erp_y_cuenta_como_pedida_hasta_que_llega_la_mercaderia()
    {
        await _t.Wms.Inventory.UpdateStockSettingsAsync(_t.Central, _t.Coffee, null, reorderPoint: 20, maxStock: 100);
        await SellAsync(_t.Coffee, 85);

        _t.Context.Role = WmsPermissions.Operator;
        await Assert.ThrowsAnyAsync<WmsValidationException>(() => _t.Wms.Supply.RequestPurchaseAsync(
            new PurchaseRequestDraft(_t.Central, _supplier, [new PurchaseRequestLineDraft(_t.Coffee, 85)])));
        _t.Context.Role = WmsPermissions.Supervisor;
        var number = await _t.Wms.Supply.RequestPurchaseAsync(new PurchaseRequestDraft(_t.Central, _supplier,
            [new PurchaseRequestLineDraft(_t.Coffee, 80), new PurchaseRequestLineDraft(_t.Coffee, 5)], new DateOnly(2026, 11, 10), "Urgente"));
        Assert.StartsWith("SC-AAAA-", number);

        var e = (await _t.OutboxAsync()).Last(x => x.EventType == SuiteEvents.PurchaseRequested).Payload!.Value;
        Assert.Equal((number, _supplier, "2026-11-10"), (e.GetProperty("number").GetString(), e.GetProperty("supplierId").GetGuid(), e.GetProperty("neededBy").GetString()));
        var line = e.GetProperty("lines").EnumerateArray().Single();
        Assert.Equal((85m, 15m, 6m), (line.GetProperty("quantity").GetDecimal(), line.GetProperty("onHand").GetDecimal(), line.GetProperty("unitCost").GetDecimal()));

        var coffee = await RowAsync(_t.Coffee);
        Assert.Equal((85m, 0m), (coffee.Requested, coffee.Suggested));   // ya pedido: no se vuelve a sugerir

        _t.Clock.Advance(TimeSpan.FromMinutes(1));
        await _t.Wms.Receipts.ReceiveDirectAsync(_t.Central, _supplier, [new DocumentLine(_t.Coffee, 85, 6m)]);
        coffee = await RowAsync(_t.Coffee);
        Assert.Equal((0m, 100m), (coffee.Requested, coffee.OnHand));

        var request = Assert.Single(await _t.Wms.Supply.GetRequestsAsync(_t.Central));
        Assert.Equal(("Café del Valle SAC", PurchaseRequestStatuses.Open), (request.SupplierName, request.Status));
        await _t.Wms.Supply.CloseRequestAsync(request.Id);
        Assert.Empty(await _t.Wms.Supply.GetRequestsAsync(_t.Central, openOnly: true));
    }

    [Fact]
    public async Task El_maximo_y_las_solicitudes_viajan_a_los_demas_equipos()
    {
        await _t.Wms.Inventory.UpdateStockSettingsAsync(_t.Central, _t.Coffee, "a-01", 20, 120);
        await _t.Wms.Supply.RequestPurchaseAsync(new PurchaseRequestDraft(_t.Central, null, [new PurchaseRequestLineDraft(_t.Sugar, 4)]));

        var other = new TestWorkspace("BBBB");
        await other.InitializeAsync();
        try
        {
            foreach (var e in (await _t.OutboxAsync()).Where(e => e.EventType == SuiteEvents.RecordUpserted))
                await other.Wms.SyncHandlers.HandleRecordUpsertedAsync(new VortexApi.Client.VortexEvent(e.Id, 1, e.EventType!, "WMS", 1, e.SubjectType,
                    e.SubjectId, e.CreatedAt, e.CreatedAt, null, e.Payload!.Value));
            Assert.Equal(120m, (await other.Wms.Inventory.GetStockRowAsync(other.Central, other.Coffee))!.MaxStock);
            var request = Assert.Single(await other.Wms.Supply.GetRequestsAsync());
            Assert.Equal(4m, request.Lines.Single().Quantity);
        }
        finally
        {
            await other.DisposeAsync();
        }
    }
}
