using VortexWms.Core.Domain;

namespace VortexWms.Core.Tests;

/// <summary>Base común de la suite en el WMS: roles y auditoría.</summary>
public sealed class SuiteBaseTests
{
    [Fact]
    public async Task El_operario_recibe_y_transfiere_pero_no_ajusta_ni_cancela()
    {
        var t = new TestWorkspace(role: WmsPermissions.Operator);
        await t.InitializeAsync();
        try
        {
            await t.Wms.Receipts.ReceiveDirectAsync(t.Central, null, [new DocumentLine(t.Coffee, 10)]);
            await t.Wms.Inventory.TransferAsync(t.Central, t.Store, [new DocumentLine(t.Coffee, 2)], null);

            var ex = await Assert.ThrowsAsync<WmsValidationException>(() =>
                t.Wms.Inventory.AdjustAsync(t.Central, t.Coffee, 7, null, AdjustmentReasons.Count, null));
            Assert.Contains("Tu rol (operario) no permite ajustar el stock", ex.Message);
            await Assert.ThrowsAsync<WmsValidationException>(() => t.Wms.Audit.GetAsync(1, 10));
            await Assert.ThrowsAsync<WmsValidationException>(() => t.Wms.Master.CreateItemAsync(
                new ItemDraft("NUEVO-1", "Nuevo", "unidad", null, null, 1, "PEN")));

            t.Context.Role = WmsPermissions.Supervisor;
            await t.Wms.Inventory.AdjustAsync(t.Central, t.Coffee, 7, null, AdjustmentReasons.Count, "Conteo de fin de mes");

            t.Context.Role = WmsPermissions.ReadOnly;
            await Assert.ThrowsAsync<WmsValidationException>(() => t.Wms.Receipts.ReceiveDirectAsync(t.Central, null, [new DocumentLine(t.Coffee, 1)]));
        }
        finally
        {
            await t.DisposeAsync();
        }
    }

    [Fact]
    public async Task Ajustes_y_cancelaciones_quedan_en_la_auditoria()
    {
        var t = new TestWorkspace();
        await t.InitializeAsync();
        try
        {
            await t.Wms.Receipts.ReceiveDirectAsync(t.Central, null, [new DocumentLine(t.Coffee, 10)]);
            await t.Wms.Inventory.AdjustAsync(t.Central, t.Coffee, null, -2, AdjustmentReasons.Damage, "Bolsas rotas");
            var orderNumber = await t.Wms.Orders.CreateAsync(Guid.NewGuid(), t.Central, [new DocumentLine(t.Coffee, 1)], "PEN");
            var order = (await t.Wms.Orders.GetOrdersAsync(orderNumber, null, 1, 10)).Items.Single();
            await t.Wms.Orders.CancelAsync(order.Id);

            var log = (await t.Wms.Audit.GetAsync(1, 10)).Items;
            Assert.Equal(["stock.adjusted", "document.cancelled"], log.Select(e => e.Action).Reverse());
            Assert.Contains(log, e => e.Detail == "-2 (damage): Bolsas rotas");
            Assert.Contains(log, e => e.Reference == orderNumber);
        }
        finally
        {
            await t.DisposeAsync();
        }
    }
}
