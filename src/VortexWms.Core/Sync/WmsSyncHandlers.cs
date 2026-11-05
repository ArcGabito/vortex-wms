using System.Text.Json;
using Microsoft.Data.Sqlite;
using VortexApi.Client;
using VortexApi.Client.Sync;
using VortexWms.Core.Data;
using VortexWms.Core.Domain;
using VortexWms.Core.Integration;
using VortexWms.Core.Services;

namespace VortexWms.Core.Sync;

/// <summary>
/// Qué hace el WMS con lo que llega del hub:
/// <list type="bullet">
/// <item>Actualiza la copia de datos maestros (artículos, sedes, clientes/proveedores, empresa).</item>
/// <item>Reacciona a los otros sistemas: ventas del POS, pedidos del ERP/CRM, órdenes de compra del ERP.</item>
/// <item>Replica lo que hicieron <b>otros equipos del mismo WMS</b> (varios operarios en un almacén): cada cambio de
/// stock viaja como <c>stock.adjusted</c> y se aplica como delta, así todos los equipos convergen y uno nuevo
/// reconstruye el stock desde el historial del hub. Requiere <see cref="VortexSyncOptions.IncludeOwnEvents"/>.</item>
/// </list>
/// Cada evento se aplica una sola vez (tabla <c>processed_events</c>) y en una transacción junto con su efecto.
/// </summary>
public sealed class WmsSyncHandlers(
    WmsDatabase db, MasterDataService master, OrderService orders, ReceiptService receipts, TimeProvider clock)
{
    public const string ProductCode = "WMS";

    /// <summary>Opciones de <see cref="VortexSync"/> que necesita el WMS (recibir los eventos de sus otros equipos).</summary>
    public static VortexSyncOptions SyncOptions => new() { IncludeOwnEvents = true };

    /// <summary>Registra todos los handlers. Llamar antes de la primera sincronización.</summary>
    public VortexSync Register(VortexSync sync) => sync
        .OnItems((c, ct) => master.ApplyItemAsync(c, ct))
        .OnSites((c, ct) => master.ApplySiteAsync(c, ct))
        .OnParties((c, ct) => master.ApplyPartyAsync(c, ct))
        .OnCompany((c, ct) => master.ApplyCompanyAsync(c, ct))
        .OnEvent(SuiteEvents.SaleCompleted, HandleSaleCompletedAsync)
        .OnEvent(SuiteEvents.SaleVoided, HandleSaleVoidedAsync)
        .OnEvent(SuiteEvents.SaleReturned, HandleSaleReturnedAsync)
        .OnEvent(SuiteEvents.OrderCreated, HandleOrderCreatedAsync)
        .OnEvent(SuiteEvents.PurchaseOrderCreated, HandlePurchaseOrderCreatedAsync)
        .OnEvent(SuiteEvents.StockAdjusted, HandleStockAdjustedAsync)
        .OnEvent(SuiteEvents.GoodsReceived, HandleGoodsReceivedAsync)
        .OnEvent(SuiteEvents.ShipmentDispatched, HandleShipmentDispatchedAsync)
        .OnEvent(SuiteEvents.OrderPickingStarted, HandleOrderPickingStartedAsync)
        .OnEvent(SuiteEvents.OrderCancelled, HandleOrderCancelledAsync)
        .OnEvent(SuiteEvents.ReceiptCancelled, HandleReceiptCancelledAsync)
        .OnEvent(SuiteEvents.RecordUpserted, HandleRecordUpsertedAsync);

    /// <summary>Cuánto se recuerdan los eventos ya aplicados. El feed no vuelve a entregar eventos tan viejos.</summary>
    public static readonly TimeSpan ProcessedEventsRetention = TimeSpan.FromDays(180);

    /// <summary>Borra los registros de eventos aplicados hace más de <see cref="ProcessedEventsRetention"/>.</summary>
    public async Task<int> PruneProcessedEventsAsync(CancellationToken ct = default)
    {
        await using var c = await db.OpenAsync(ct);
        return await c.ExecAsync("DELETE FROM processed_events WHERE processed_at < $limit", null,
            ("$limit", clock.GetUtcNow() - ProcessedEventsRetention));
    }

    // ---------- Otros sistemas ----------

    /// <summary>
    /// Venta en caja: descuenta stock aunque quede negativo (la venta ya ocurrió). Los movimientos usan Ids
    /// deterministas: si varios equipos procesan la misma venta, el hub deduplica su <c>stock.adjusted</c>.
    /// </summary>
    public Task HandleSaleCompletedAsync(VortexEvent e, CancellationToken ct = default) =>
        ApplyOnceAsync<SaleCompleted>(e, async (sale, c, tx, now) =>
        {
            if (sale.SiteId == Guid.Empty)
                return "sin siteId";
            var amounts = (sale.Lines ?? []).GroupBy(l => l.ItemId).ToDictionary(g => g.Key, g => g.Sum(l => l.Total));
            foreach (var (itemId, quantity) in PerItem((sale.Lines ?? []).Select(l => (l.ItemId, l.Quantity))))
            {
                if (!await StockLedger.TracksInventoryAsync(c, tx, itemId))
                    continue;
                await StockLedger.ApplyAsync(c, tx, now, e.Source, new StockLedger.Entry(sale.SiteId, itemId, -quantity,
                    MovementKinds.Sale, "sale", sale.SaleId, sale.Number,
                    MovementId: DeterministicId.From("sale", sale.SaleId, sale.SiteId, itemId), SaleAmount: amounts.GetValueOrDefault(itemId)));
            }
            return null;
        }, ct);

    /// <summary>Anulación: devuelve al stock exactamente lo que descontó esa venta (si no se anuló antes).</summary>
    public Task HandleSaleVoidedAsync(VortexEvent e, CancellationToken ct = default) =>
        ApplyOnceAsync<SaleVoided>(e, async (voided, c, tx, now) =>
        {
            var alreadyVoided = await c.ScalarAsync<long>(
                "SELECT COUNT(*) FROM movements WHERE document_id = $id AND kind = 'sale_void'", tx, ("$id", voided.SaleId));
            if (alreadyVoided > 0)
                return "venta ya anulada";
            var sold = await c.QueryAsync("""
                SELECT site_id, item_id, SUM(quantity) AS qty, MAX(reference) AS reference, MAX(unit_cost) AS unit_cost,
                       MAX(sale_amount) AS sale_amount
                FROM movements WHERE document_id = $id AND kind = 'sale' GROUP BY site_id, item_id
                """, r => (Site: r.Guid("site_id"), Item: r.Guid("item_id"), Qty: r.Dec("qty"), Ref: r.StrOrNull("reference"),
                    Cost: r.DecOrNull("unit_cost"), Amount: r.DecOrNull("sale_amount")),
                tx, ("$id", voided.SaleId));
            // Vuelve al stock con el costo con que salió y anula su importe de venta.
            foreach (var m in sold.Where(m => m.Qty != 0))
                await StockLedger.ApplyAsync(c, tx, now, e.Source, new StockLedger.Entry(m.Site, m.Item, -m.Qty,
                    MovementKinds.SaleVoid, "sale_void", voided.SaleId, voided.Number ?? m.Ref, voided.Reason,
                    DeterministicId.From("sale_void", voided.SaleId, m.Site, m.Item), UnitCost: m.Cost, SaleAmount: -m.Amount));
            return sold.Count == 0 ? "venta desconocida" : null;
        }, ct);

    public Task HandleSaleReturnedAsync(VortexEvent e, CancellationToken ct = default) =>
        ApplyOnceAsync<SaleReturned>(e, async (ret, c, tx, now) =>
        {
            if (ret.SiteId == Guid.Empty)
                return "sin siteId";
            var amounts = (ret.Lines ?? []).GroupBy(l => l.ItemId).ToDictionary(g => g.Key, g => g.Sum(l => Math.Abs(l.Total)));
            foreach (var (itemId, quantity) in PerItem((ret.Lines ?? []).Select(l => (l.ItemId, Math.Abs(l.Quantity)))))
            {
                if (!await StockLedger.TracksInventoryAsync(c, tx, itemId))
                    continue;
                // Reingresa con el costo con que salió en la venta (si este equipo la tiene); si no, al promedio.
                var soldAt = await c.ScalarAsync<string?>("SELECT unit_cost FROM movements WHERE id = $id", tx,
                    ("$id", DeterministicId.From("sale", ret.SaleId, ret.SiteId, itemId)));
                await StockLedger.ApplyAsync(c, tx, now, e.Source, new StockLedger.Entry(ret.SiteId, itemId, quantity,
                    MovementKinds.SaleReturn, "return", ret.ReturnId, e.SubjectId,
                    MovementId: DeterministicId.From("sale_return", ret.ReturnId, ret.SiteId, itemId),
                    UnitCost: soldAt is null ? null : decimal.Parse(soldAt, System.Globalization.CultureInfo.InvariantCulture),
                    SaleAmount: -amounts.GetValueOrDefault(itemId)));
            }
            return null;
        }, ct);

    public Task HandleOrderCreatedAsync(VortexEvent e, CancellationToken ct = default) =>
        ApplyOnceAsync<OrderCreated>(e, async (order, c, tx, now) =>
        {
            await orders.ApplyOrderCreatedAsync(c, tx, now, e.Source, order);
            return null;
        }, ct);

    public Task HandlePurchaseOrderCreatedAsync(VortexEvent e, CancellationToken ct = default) =>
        ApplyOnceAsync<PurchaseOrderCreated>(e, async (po, c, tx, now) =>
        {
            await receipts.ApplyPurchaseOrderAsync(c, tx, now, po);
            return null;
        }, ct);

    // ---------- Otros equipos del mismo WMS ----------

    /// <summary>
    /// Cambio de stock hecho en otro equipo: se aplica su delta sin volver a publicarlo. Los propios (y los derivados
    /// de una venta que este equipo ya procesó) se reconocen porque el movimiento con ese Id ya existe.
    /// </summary>
    public Task HandleStockAdjustedAsync(VortexEvent e, CancellationToken ct = default) =>
        ApplyOnceAsync<StockAdjusted>(e, async (s, c, tx, now) =>
        {
            if (e.Source != ProductCode)
                return "ignorado: el stock solo lo publica el WMS";
            if (s.SiteId == Guid.Empty || s.ItemId == Guid.Empty)
                return "sin siteId o itemId";
            await StockLedger.ReplicateAsync(c, tx, e.Id, e.OccurredAt, s, now);
            return null;
        }, ct);

    /// <summary>Recepción hecha en otro equipo: se refleja el documento (el stock llega por <c>stock.adjusted</c>).</summary>
    public Task HandleGoodsReceivedAsync(VortexEvent e, CancellationToken ct = default) =>
        ApplyOnceAsync<GoodsReceived>(e, async (g, c, tx, now) =>
        {
            if (e.Source != ProductCode || g.ReceiptId == Guid.Empty)
                return null;
            await receipts.ApplyReceivedElsewhereAsync(c, tx, e.OccurredAt, g);
            return null;
        }, ct);

    /// <summary>Despacho hecho en otro equipo: se cierra el pedido y se libera su reserva (el stock llega aparte).</summary>
    public Task HandleShipmentDispatchedAsync(VortexEvent e, CancellationToken ct = default) =>
        ApplyOnceAsync<ShipmentDispatched>(e, async (d, c, tx, now) =>
        {
            if (e.Source != ProductCode || d.OrderId == Guid.Empty)
                return null;
            await orders.ApplyDispatchedElsewhereAsync(c, tx, now, e.OccurredAt, d);
            return null;
        }, ct);

    /// <summary>Preparación empezada en otro equipo del WMS.</summary>
    public Task HandleOrderPickingStartedAsync(VortexEvent e, CancellationToken ct = default) =>
        ApplyOnceAsync<OrderPickingStarted>(e, async (p, c, tx, now) =>
        {
            if (e.Source != ProductCode)
                return "ignorado: la preparación solo la publica el WMS";
            await OrderService.ApplyPickingStartedAsync(c, tx, p);
            return null;
        }, ct);

    /// <summary>Pedido cancelado en otro equipo del WMS, en el ERP o en el CRM.</summary>
    public Task HandleOrderCancelledAsync(VortexEvent e, CancellationToken ct = default) =>
        ApplyOnceAsync<OrderCancelled>(e, async (o, c, tx, now) =>
        {
            await orders.ApplyCancelledAsync(c, tx, now, o);
            return null;
        }, ct);

    /// <summary>Recepción esperada anulada en otro equipo del WMS.</summary>
    public Task HandleReceiptCancelledAsync(VortexEvent e, CancellationToken ct = default) =>
        ApplyOnceAsync<ReceiptCancelled>(e, async (r, c, tx, now) =>
        {
            if (e.Source != ProductCode)
                return "ignorado: las recepciones solo las anula el WMS";
            await ReceiptService.ApplyCancelledAsync(c, tx, r);
            return null;
        }, ct);

    /// <summary>Datos propios del WMS editados en otro equipo (gana el cambio más reciente).</summary>
    public Task HandleRecordUpsertedAsync(VortexEvent e, CancellationToken ct = default) =>
        ApplyOnceAsync<WmsRecord>(e, async (r, c, tx, now) =>
        {
            if (e.Source != ProductCode)
                return "ignorado: solo lo publica el WMS";
            if (r.Kind != WmsRecordKinds.StockSettings)
                return $"ignorado: tipo {r.Kind} desconocido";
            var s = r.Data.Deserialize<StockSettingsData>(VortexJson.Options);
            if (s is null || s.SiteId == Guid.Empty || s.ItemId == Guid.Empty)
                return "sin siteId o itemId";
            await InventoryService.ApplySettingsAsync(c, tx, s, r.UpdatedAt);
            return null;
        }, ct);

    // ---------- Internos ----------

    private static IEnumerable<(Guid ItemId, decimal Quantity)> PerItem(IEnumerable<(Guid ItemId, decimal Quantity)> lines) =>
        lines.Where(l => l.ItemId != Guid.Empty && l.Quantity != 0)
            .GroupBy(l => l.ItemId)
            .Select(g => (g.Key, g.Sum(l => l.Quantity)));

    /// <summary>
    /// Aplica un evento una sola vez. Un esquema futuro o un <c>data</c> ilegible se registran como ignorados (nunca
    /// se van a poder aplicar y no deben trabar el feed); un error de la base sí se propaga para reintentar.
    /// </summary>
    private async Task ApplyOnceAsync<T>(VortexEvent e, Func<T, SqliteConnection, SqliteTransaction, DateTimeOffset, Task<string?>> apply,
        CancellationToken ct) where T : class
    {
        await using var c = await db.OpenAsync(ct);
        await using var tx = c.BeginTransaction();
        if (await c.ScalarAsync<long>("SELECT COUNT(*) FROM processed_events WHERE event_id = $id", tx, ("$id", e.Id)) > 0)
            return;

        string? note;
        if (e.SchemaVersion != 1)
        {
            note = $"ignorado: schemaVersion {e.SchemaVersion} no soportada";
        }
        else
        {
            T? data = null;
            try
            {
                data = e.DataAs<T>();
                note = data is null ? "ignorado: data vacía" : null;
            }
            catch (JsonException ex)
            {
                note = $"ignorado: data inválida ({ex.Message})";
            }
            if (data is not null)
                note = await apply(data, c, tx, clock.GetUtcNow());
        }

        await c.ExecAsync("INSERT INTO processed_events (event_id, type, processed_at, note) VALUES ($id, $type, $now, $note)", tx,
            ("$id", e.Id), ("$type", e.Type), ("$now", clock.GetUtcNow()), ("$note", note));
        await tx.CommitAsync(ct);
    }
}
