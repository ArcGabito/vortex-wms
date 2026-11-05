using Microsoft.Data.Sqlite;
using VortexWms.Core.Data;
using VortexWms.Core.Domain;
using VortexWms.Core.Integration;
using VortexWms.Core.Sync;

using static VortexWms.Core.Texts.CoreText;

namespace VortexWms.Core.Services;

/// <summary>
/// Recepciones de mercadería: esperadas (desde <c>purchase_order.created</c> del ERP) o directas (sin orden de
/// compra). Al recibir suben las existencias y se publica <c>goods.received</c>.
/// </summary>
public sealed class ReceiptService(WmsDatabase db, IWmsContext context, TimeProvider clock) : WmsService(db, context, clock)
{
    public async Task<PagedResult<ReceiptRow>> GetReceiptsAsync(string? search, string? status, int page, int pageSize,
        CancellationToken ct = default)
    {
        await using var c = await Db.OpenAsync(ct);
        const string from = "FROM receipts r LEFT JOIN parties p ON p.id = r.supplier_id LEFT JOIN sites t ON t.id = r.site_id";
        const string where = """
            WHERE ($status IS NULL OR r.status = $status)
              AND ($search IS NULL OR r.number LIKE $like OR p.name LIKE $like)
            """;
        var args = new (string, object?)[]
        {
            ("$status", string.IsNullOrWhiteSpace(status) ? null : status), ("$search", string.IsNullOrWhiteSpace(search) ? null : search.Trim()),
            ("$like", Like(search)), ("$limit", pageSize), ("$offset", (page - 1) * pageSize),
        };
        var total = await c.ScalarAsync<int>($"SELECT COUNT(*) {from} {where}{MySites("r.site_id")}", null, args);
        var rows = await c.QueryAsync($"""
            SELECT r.*, p.name AS supplier_name, t.name AS site_name,
                   (SELECT COUNT(*) FROM receipt_lines l WHERE l.receipt_id = r.id) AS line_count,
                   (SELECT COALESCE(SUM(expected_qty), 0) FROM receipt_lines l WHERE l.receipt_id = r.id) AS expected_units,
                   (SELECT COALESCE(SUM(received_qty), 0) FROM receipt_lines l WHERE l.receipt_id = r.id) AS received_units
            {from} {where}{MySites("r.site_id")}
            ORDER BY CASE r.status WHEN 'expected' THEN 0 ELSE 1 END, r.created_at DESC
            LIMIT $limit OFFSET $offset
            """, r => new ReceiptRow(r.Guid("id"), r.Str("number"), r.GuidOrNull("purchase_order_id"), r.GuidOrNull("supplier_id"),
                r.StrOrNull("supplier_name"), r.Guid("site_id"), r.StrOrNull("site_name"), r.Str("status"), r.Time("created_at"),
                r.TimeOrNull("received_at"), r.Int("line_count"), r.Dec("expected_units"), r.Dec("received_units")), null, args);
        return new PagedResult<ReceiptRow>(rows, total, page, pageSize);
    }

    public async Task<IReadOnlyList<ReceiptLineRow>> GetLinesAsync(Guid receiptId, CancellationToken ct = default)
    {
        await using var c = await Db.OpenAsync(ct);
        return await c.QueryAsync("""
            SELECT l.*, COALESCE(i.sku, '?') AS sku, COALESCE(i.name, 'Artículo desconocido') AS item_name
            FROM receipt_lines l LEFT JOIN items i ON i.id = l.item_id WHERE l.receipt_id = $id ORDER BY l.line_no
            """, r => new ReceiptLineRow(r.Int("line_no"), r.Guid("item_id"), r.Str("sku"), r.Str("item_name"),
                r.Dec("expected_qty"), r.Dec("received_qty"), r.DecOrNull("unit_cost")), null, ("$id", receiptId));
    }

    /// <summary>Recepción directa: la mercadería llegó sin orden de compra previa.</summary>
    public Task<string> ReceiveDirectAsync(Guid siteId, Guid? supplierId, IReadOnlyList<DocumentLine> lines, CancellationToken ct = default)
    {
        RequirePermission(WmsPermissions.Receive);
        Require(lines.Count > 0, T("error.agrega_al_menos_un_articulo"));
        Require(lines.All(l => l.Quantity > 0), T("error.las_cantidades_deben_ser_mayores"));
        var merged = InventoryService.Merge(lines);

        return InTransactionAsync(async (c, tx, now) =>
        {
            await RequireSiteAccessAsync(c, tx, siteId);
            foreach (var line in merged)
                await InventoryService.RequireSiteAndItemAsync(c, tx, siteId, line.ItemId);

            var receiptId = Guid.NewGuid();
            var number = await DocumentNumbers.NextAsync(c, tx, DocumentNumbers.Receipt, Context.DeviceTag);
            await c.ExecAsync("""
                INSERT INTO receipts (id, number, purchase_order_id, supplier_id, site_id, status, created_at, received_at)
                VALUES ($id, $number, NULL, $supplier, $site, 'received', $now, $now)
                """, tx, ("$id", receiptId), ("$number", number), ("$supplier", supplierId), ("$site", siteId), ("$now", now));

            var lineNo = 0;
            foreach (var line in merged)
            {
                await c.ExecAsync("""
                    INSERT INTO receipt_lines (receipt_id, line_no, item_id, expected_qty, received_qty, unit_cost)
                    VALUES ($id, $no, $item, $qty, $qty, $cost)
                    """, tx, ("$id", receiptId), ("$no", ++lineNo), ("$item", line.ItemId), ("$qty", line.Quantity), ("$cost", line.UnitPrice));
            }

            await PostAsync(c, tx, now, receiptId, number, null, siteId, supplierId, merged.Select(l => new QuantityLine(l.ItemId, l.Quantity)).ToList());
            return number;
        }, ct);
    }

    /// <summary>Recibe una recepción esperada. <paramref name="received"/>: cantidad recibida por número de línea.</summary>
    public Task<string> ReceiveExpectedAsync(Guid receiptId, IReadOnlyDictionary<int, decimal> received, CancellationToken ct = default)
    {
        RequirePermission(WmsPermissions.Receive);
        Require(received.Values.All(q => q >= 0), T("error.las_cantidades_no_pueden_ser"));
        Require(received.Values.Any(q => q > 0), T("error.indica_al_menos_una_cantidad"));

        return InTransactionAsync(async (c, tx, now) =>
        {
            var receipt = await c.FirstOrDefaultAsync("SELECT * FROM receipts WHERE id = $id", r => new
            {
                Number = r.Str("number"), Status = r.Str("status"), SiteId = r.Guid("site_id"),
                PurchaseOrderId = r.GuidOrNull("purchase_order_id"), SupplierId = r.GuidOrNull("supplier_id"),
            }, tx, ("$id", receiptId));
            Require(receipt is not null, T("error.la_recepcion_no_existe"));
            Require(receipt!.Status == ReceiptStatuses.Expected, T("error.esta_recepcion_ya_fue_procesada"));
            await InventoryService.RequireSiteAsync(c, tx, receipt.SiteId);
            await RequireSiteAccessAsync(c, tx, receipt.SiteId);

            var lines = await c.QueryAsync("SELECT line_no, item_id FROM receipt_lines WHERE receipt_id = $id ORDER BY line_no",
                r => (LineNo: r.Int("line_no"), ItemId: r.Guid("item_id")), tx, ("$id", receiptId));
            var posted = new List<QuantityLine>();
            foreach (var (lineNo, itemId) in lines)
            {
                var quantity = received.GetValueOrDefault(lineNo);
                await c.ExecAsync("UPDATE receipt_lines SET received_qty = $qty WHERE receipt_id = $id AND line_no = $no", tx,
                    ("$qty", quantity), ("$id", receiptId), ("$no", lineNo));
                if (quantity > 0)
                    posted.Add(new QuantityLine(itemId, quantity));
            }

            await c.ExecAsync("UPDATE receipts SET status = 'received', received_at = $now WHERE id = $id", tx, ("$id", receiptId), ("$now", now));
            await PostAsync(c, tx, now, receiptId, receipt.Number, receipt.PurchaseOrderId, receipt.SiteId, receipt.SupplierId, posted);
            return receipt.Number;
        }, ct);
    }

    public Task CancelAsync(Guid receiptId, CancellationToken ct = default)
    {
        RequirePermission(WmsPermissions.CancelReceipt);
        return InTransactionAsync(async (c, tx, now) =>
        {
            var status = await c.ScalarAsync<string?>("SELECT status FROM receipts WHERE id = $id", tx, ("$id", receiptId));
            Require(status == ReceiptStatuses.Expected, T("error.solo_se_anula_una_recepcion"));
            if (await c.ScalarAsync<string?>("SELECT site_id FROM receipts WHERE id = $id", tx, ("$id", receiptId)) is { } site)
                await RequireSiteAccessAsync(c, tx, Guid.Parse(site));
            var receipt = await c.FirstOrDefaultAsync("SELECT number, purchase_order_id FROM receipts WHERE id = $id",
                r => new { Number = r.Str("number"), PurchaseOrderId = r.GuidOrNull("purchase_order_id") }, tx, ("$id", receiptId));
            await AuditAsync(c, tx, now, VortexApi.Suite.Auditing.AuditActions.Voided, "receipt", receiptId, receipt!.Number, null);
            await Outbox.EnqueueEventAsync(c, tx, now, SuiteEvents.ReceiptCancelled,
                new ReceiptCancelled(receiptId, receipt.Number, receipt.PurchaseOrderId),
                subjectType: "receipt", subjectId: receipt.Number, id: DeterministicId.From("receipt_cancel", receiptId));
            return await c.ExecAsync("UPDATE receipts SET status = 'cancelled' WHERE id = $id", tx, ("$id", receiptId));
        }, ct);
    }

    /// <summary>Aplica <c>purchase_order.created</c>: crea la recepción esperada. Idempotente por orden de compra.</summary>
    internal async Task ApplyPurchaseOrderAsync(SqliteConnection c, SqliteTransaction tx, DateTimeOffset now, PurchaseOrderCreated e)
    {
        if (e.PurchaseOrderId == Guid.Empty ||
            await c.ScalarAsync<long>("SELECT COUNT(*) FROM receipts WHERE purchase_order_id = $po", tx, ("$po", e.PurchaseOrderId)) > 0)
            return;

        var siteId = e.SiteId != Guid.Empty ? e.SiteId : await DefaultSiteAsync(c, tx) ?? Guid.Empty;
        // Mismo Id en todos los equipos: así la recepción hecha en uno cierra la esperada en los demás.
        var receiptId = DeterministicId.From("receipt", e.PurchaseOrderId);
        var number = string.IsNullOrWhiteSpace(e.Number) ? $"OC-{e.PurchaseOrderId.ToString()[..8].ToUpperInvariant()}" : e.Number;
        await c.ExecAsync("""
            INSERT INTO receipts (id, number, purchase_order_id, supplier_id, site_id, status, created_at)
            VALUES ($id, $number, $po, $supplier, $site, 'expected', $now)
            """, tx, ("$id", receiptId), ("$number", number), ("$po", e.PurchaseOrderId),
            ("$supplier", e.SupplierId == Guid.Empty ? null : e.SupplierId), ("$site", siteId), ("$now", now));

        var lineNo = 0;
        foreach (var line in (e.Lines ?? []).Where(l => l.ItemId != Guid.Empty && l.Quantity > 0))
        {
            await c.ExecAsync("""
                INSERT INTO receipt_lines (receipt_id, line_no, item_id, expected_qty, received_qty, unit_cost)
                VALUES ($id, $no, $item, $qty, 0, $cost)
                """, tx, ("$id", receiptId), ("$no", ++lineNo), ("$item", line.ItemId), ("$qty", line.Quantity), ("$cost", line.UnitCost));
        }
    }

    /// <summary>
    /// <c>goods.received</c> de otro equipo del WMS: cierra la recepción esperada o registra la directa. El stock no se
    /// toca aquí: llega por los <c>stock.adjusted</c> de ese equipo.
    /// </summary>
    internal async Task ApplyReceivedElsewhereAsync(SqliteConnection c, SqliteTransaction tx, DateTimeOffset occurredAt, GoodsReceived g)
    {
        var lines = (g.Lines ?? []).Where(l => l.ItemId != Guid.Empty).GroupBy(l => l.ItemId)
            .Select(x => new QuantityLine(x.Key, x.Sum(l => l.Quantity))).ToList();
        var existing = await c.FirstOrDefaultAsync("""
            SELECT id, status FROM receipts WHERE id = $id OR ($po IS NOT NULL AND purchase_order_id = $po) LIMIT 1
            """, r => new { Id = r.Guid("id"), Status = r.Str("status") }, tx, ("$id", g.ReceiptId), ("$po", g.PurchaseOrderId));

        if (existing is not null)
        {
            if (existing.Status == ReceiptStatuses.Received)
            {
                // También se recibió aquí (o es el eco de la propia recepción): quedan las cantidades del hub.
                var local = await c.QueryAsync("SELECT item_id, SUM(received_qty) AS qty FROM receipt_lines WHERE receipt_id = $id GROUP BY item_id",
                    r => (Item: r.Guid("item_id"), Qty: r.Dec("qty")), tx, ("$id", existing.Id));
                var same = lines.All(l => local.Any(x => x.Item == l.ItemId && x.Qty == l.Quantity))
                    && local.Where(x => x.Qty != 0).All(x => lines.Any(l => l.ItemId == x.Item));
                if (same)
                    return;
                await c.ExecAsync("UPDATE receipt_lines SET received_qty = 0 WHERE receipt_id = $id", tx, ("$id", existing.Id));
                await SetReceivedAsync(c, tx, existing.Id, lines);
                await SyncConflicts.RecordAsync(c, tx, occurredAt, SyncConflictKinds.ReceiptQuantities, existing.Id, g.Number, null);
                return;
            }
            if (existing.Status == ReceiptStatuses.Cancelled)
                await SyncConflicts.RecordAsync(c, tx, occurredAt, SyncConflictKinds.ReceivedAfterCancel, existing.Id, g.Number, null);
            else if (existing.Status != ReceiptStatuses.Expected)
                return;
            await SetReceivedAsync(c, tx, existing.Id, lines);
            await c.ExecAsync("UPDATE receipts SET status = 'received', received_at = $at WHERE id = $id", tx,
                ("$id", existing.Id), ("$at", occurredAt));
            return;
        }

        await c.ExecAsync("""
            INSERT INTO receipts (id, number, purchase_order_id, supplier_id, site_id, status, created_at, received_at)
            VALUES ($id, $number, $po, $supplier, $site, 'received', $at, $at)
            """, tx, ("$id", g.ReceiptId), ("$number", g.Number ?? $"REC-{g.ReceiptId.ToString()[..8].ToUpperInvariant()}"),
            ("$po", g.PurchaseOrderId), ("$supplier", g.SupplierId), ("$site", g.SiteId), ("$at", occurredAt));
        var lineNo = 0;
        foreach (var line in lines)
        {
            await c.ExecAsync("""
                INSERT INTO receipt_lines (receipt_id, line_no, item_id, expected_qty, received_qty) VALUES ($id, $no, $item, $qty, $qty)
                """, tx, ("$id", g.ReceiptId), ("$no", ++lineNo), ("$item", line.ItemId), ("$qty", line.Quantity));
        }
    }

    /// <summary><c>receipt.cancelled</c> de otro equipo: la recepción esperada ya no se espera.</summary>
    internal static Task ApplyCancelledAsync(SqliteConnection c, SqliteTransaction tx, ReceiptCancelled e) =>
        c.ExecAsync("UPDATE receipts SET status = 'cancelled' WHERE id = $id AND status = 'expected'", tx, ("$id", e.ReceiptId));

    private static async Task SetReceivedAsync(SqliteConnection c, SqliteTransaction tx, Guid receiptId, IReadOnlyList<QuantityLine> lines)
    {
        foreach (var line in lines)
        {
            await c.ExecAsync("""
                UPDATE receipt_lines SET received_qty = $qty
                WHERE receipt_id = $id AND line_no = (SELECT MIN(line_no) FROM receipt_lines WHERE receipt_id = $id AND item_id = $item)
                """, tx, ("$qty", line.Quantity), ("$id", receiptId), ("$item", line.ItemId));
        }
    }

    private async Task PostAsync(SqliteConnection c, SqliteTransaction tx, DateTimeOffset now, Guid receiptId, string number,
        Guid? purchaseOrderId, Guid siteId, Guid? supplierId, IReadOnlyList<QuantityLine> lines)
    {
        foreach (var line in lines.Where(l => l.Quantity > 0))
        {
            if (await StockLedger.TracksInventoryAsync(c, tx, line.ItemId))
                await StockLedger.ApplyAsync(c, tx, now, Context.UserName,
                    new StockLedger.Entry(siteId, line.ItemId, line.Quantity, MovementKinds.Receipt, "receipt", receiptId, number,
                        // Mismo Id en todos los equipos: si dos reciben la misma orden de compra sin conexión, el stock sube
                        // una sola vez (gana la versión que el hub recibió primero).
                        MovementId: DeterministicId.From("receipt_line", receiptId, line.ItemId)));
        }
        await Outbox.EnqueueEventAsync(c, tx, now, SuiteEvents.GoodsReceived,
            new GoodsReceived(receiptId, number, purchaseOrderId, siteId, supplierId, lines),
            subjectType: "receipt", subjectId: number, id: receiptId);
    }
}
