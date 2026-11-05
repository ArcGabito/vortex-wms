using VortexWms.Core.Domain;

namespace VortexWms.Core.Tests;

/// <summary>Sedes por usuario: quien tiene sedes asignadas en el hub solo ve y opera esas.</summary>
public sealed class SiteAccessTests
{
    [Fact]
    public async Task Un_operario_del_almacen_central_solo_ve_y_opera_su_sede()
    {
        var t = new TestWorkspace(role: WmsPermissions.Supervisor);
        await t.InitializeAsync();
        try
        {
            await t.Wms.Receipts.ReceiveDirectAsync(t.Central, null, [new DocumentLine(t.Coffee, 10)]);
            await t.Wms.Receipts.ReceiveDirectAsync(t.Store, null, [new DocumentLine(t.Coffee, 5)]);

            t.Context.SiteIds = [t.Central];
            var mine = await t.Wms.Master.GetSitesAsync(activeOnly: true, mineOnly: true);
            Assert.Equal([t.Central], mine.Select(s => s.Id));
            Assert.Equal(2, (await t.Wms.Master.GetSitesAsync()).Count);   // el catálogo de sedes sigue completo

            var stock = await t.Wms.Inventory.GetStockAsync(new StockQuery());
            Assert.All(stock.Items, s => Assert.Equal(t.Central, s.SiteId));
            Assert.Equal(10, (await t.Wms.Inventory.GetDashboardAsync()).UnitsOnHand);
            Assert.All((await t.Wms.Inventory.GetMovementsAsync(new MovementQuery())).Items, m => Assert.Equal(t.Central, m.SiteId));
            Assert.Equal(1, (await t.Wms.Receipts.GetReceiptsAsync(null, null, 1, 10)).Total);

            var ex = await Assert.ThrowsAsync<WmsValidationException>(() =>
                t.Wms.Receipts.ReceiveDirectAsync(t.Store, null, [new DocumentLine(t.Coffee, 1)]));
            Assert.Contains("No trabajas en la sede Tienda Miraflores", ex.Message);
            await Assert.ThrowsAsync<WmsValidationException>(() => t.Wms.Inventory.AdjustAsync(t.Store, t.Coffee, 0, null, AdjustmentReasons.Count, null));
            await Assert.ThrowsAsync<WmsValidationException>(() => t.Wms.Inventory.TransferAsync(t.Store, t.Central, [new DocumentLine(t.Coffee, 1)], null));

            // Puede enviar mercadería de su sede a otra.
            await t.Wms.Inventory.TransferAsync(t.Central, t.Store, [new DocumentLine(t.Coffee, 2)], null);

            t.Context.SiteIds = [];
            Assert.Equal(7, await t.OnHandAsync(t.Store, t.Coffee));
        }
        finally
        {
            await t.DisposeAsync();
        }
    }
}
