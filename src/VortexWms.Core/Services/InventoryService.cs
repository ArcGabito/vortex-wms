using System.Globalization;
using System.Text;
using Microsoft.Data.Sqlite;
using VortexWms.Core.Data;
using VortexWms.Core.Domain;
using VortexWms.Core.Integration;
using VortexWms.Core.Sync;

using static VortexWms.Core.Texts.CoreText;

namespace VortexWms.Core.Services;

/// <summary>Existencias por sede, kardex, ajustes, transferencias y el resumen del panel.</summary>
public sealed class InventoryService(WmsDatabase db, IWmsContext context, TimeProvider clock) : WmsService(db, context, clock)
{
    private const string StockSelect = """
        SELECT s.*, t.code AS site_code, t.name AS site_name, i.sku, i.name AS item_name, i.category, i.unit, i.barcode,
               i.price, i.currency_code
        FROM stock s
        JOIN items i ON i.id = s.item_id
        JOIN sites t ON t.id = s.site_id
        """;

    private const string StockWhere = """
        WHERE i.is_deleted = 0 AND t.is_deleted = 0
          AND ($search IS NULL OR i.sku LIKE $like OR i.name LIKE $like OR i.barcode = $search OR s.bin LIKE $like)
          AND ($site IS NULL OR s.site_id = $site)
          AND ($category IS NULL OR i.category = $category)
          AND ($status IS NULL
               OR ($status = 'OutOfStock' AND s.on_hand <= 0)
               OR ($status = 'Low' AND s.on_hand > 0 AND s.reorder_point IS NOT NULL AND s.on_hand - s.reserved <= s.reorder_point)
               OR ($status = 'InStock' AND s.on_hand > 0 AND (s.reorder_point IS NULL OR s.on_hand - s.reserved > s.reorder_point)))
        """;

    public async Task<PagedResult<StockRow>> GetStockAsync(StockQuery q, CancellationToken ct = default)
    {
        await using var c = await Db.OpenAsync(ct);
        var args = StockArgs(q);
        var total = await c.ScalarAsync<int>($"SELECT COUNT(*) FROM stock s JOIN items i ON i.id = s.item_id JOIN sites t ON t.id = s.site_id {StockWhere}", null, args);
        var rows = await c.QueryAsync($"{StockSelect} {StockWhere} ORDER BY i.name COLLATE NOCASE, t.code LIMIT $limit OFFSET $offset",
            ReadStock, null, args);
        return new PagedResult<StockRow>(rows, total, q.Page, q.PageSize);
    }

    public async Task<StockRow?> GetStockRowAsync(Guid siteId, Guid itemId, CancellationToken ct = default)
    {
        await using var c = await Db.OpenAsync(ct);
        return await c.FirstOrDefaultAsync($"{StockSelect} WHERE s.site_id = $site AND s.item_id = $item", ReadStock, null,
            ("$site", siteId), ("$item", itemId));
    }

    public async Task<decimal> GetAvailableAsync(Guid siteId, Guid itemId, CancellationToken ct = default)
    {
        await using var c = await Db.OpenAsync(ct);
        return await StockLedger.AvailableAsync(c, null, siteId, itemId);
    }

    public async Task<IReadOnlyList<StockRow>> GetLowStockAsync(int limit, CancellationToken ct = default)
    {
        await using var c = await Db.OpenAsync(ct);
        return await c.QueryAsync($"""
            {StockSelect}
            WHERE i.is_deleted = 0 AND t.is_deleted = 0
              AND (s.on_hand <= 0 OR (s.reorder_point IS NOT NULL AND s.on_hand - s.reserved <= s.reorder_point))
            ORDER BY s.on_hand - COALESCE(s.reorder_point, 0), i.name LIMIT $limit
            """, ReadStock, null, ("$limit", limit));
    }

    /// <summary>CSV (separado por comas, UTF-8) de las existencias que cumplen el filtro, sin paginar.</summary>
    public async Task<string> ExportStockCsvAsync(StockQuery q, CancellationToken ct = default)
    {
        await using var c = await Db.OpenAsync(ct);
        var rows = await c.QueryAsync($"{StockSelect} {StockWhere} ORDER BY i.name COLLATE NOCASE, t.code LIMIT $limit OFFSET $offset",
            ReadStock, null, StockArgs(q with { Page = 1, PageSize = int.MaxValue }));
        var csv = new StringBuilder("sku,articulo,categoria,sede,ubicacion,stock,reservado,disponible,minimo,unidad,precio,moneda,valor\n");
        foreach (var r in rows)
        {
            csv.AppendJoin(',', Csv(r.Sku), Csv(r.ItemName), Csv(r.Category), Csv(r.SiteCode), Csv(r.Bin), Num(r.OnHand),
                Num(r.Reserved), Num(r.Available), r.ReorderPoint is { } m ? Num(m) : "", Csv(r.Unit), Num(r.Price),
                Csv(r.CurrencyCode), Num(r.Value)).Append('\n');
        }
        return csv.ToString();
    }

    // ---------- Operaciones ----------

    /// <param name="countedQuantity">Conteo físico: el saldo pasa a ser este valor.</param>
    /// <param name="delta">Ajuste relativo (positivo o negativo). Se usa uno de los dos.</param>
    public Task<decimal> AdjustAsync(Guid siteId, Guid itemId, decimal? countedQuantity, decimal? delta, string reason, string? note,
        CancellationToken ct = default)
    {
        RequirePermission(WmsPermissions.Adjust);
        Require(countedQuantity is not null ^ delta is not null, T("error.indica_la_cantidad_contada_la"));
        Require(AdjustmentReasons.All.Contains(reason), T("error.motivo_de_ajuste_no_valido"));
        Require(countedQuantity is null or >= 0, T("error.el_conteo_no_puede_ser"));

        return InTransactionAsync(async (c, tx, now) =>
        {
            await RequireSiteAndItemAsync(c, tx, siteId, itemId);
            var current = await StockLedger.OnHandAsync(c, tx, siteId, itemId);
            var change = countedQuantity is { } counted ? counted - current : delta!.Value;
            Require(change != 0, T("error.el_ajuste_no_cambia_el"));
            Require(current + change >= 0, T("error.el_stock_no_puede_quedar", current));
            var item = await InventoryService.ItemLabelAsync(c, tx, itemId);
            await AuditAsync(c, tx, now, VortexApi.Suite.Auditing.AuditActions.StockAdjusted, "item", itemId, item,
                $"{(change > 0 ? "+" : "")}{change:0.##} ({reason}){(Clean(note) is { } n ? $": {n}" : "")}");
            return await StockLedger.ApplyAsync(c, tx, now, Context.UserName,
                new StockLedger.Entry(siteId, itemId, change, MovementKinds.Adjustment, reason, Note: Clean(note)));
        }, ct);
    }

    public Task<string> TransferAsync(Guid fromSiteId, Guid toSiteId, IReadOnlyList<DocumentLine> lines, string? note,
        CancellationToken ct = default)
    {
        RequirePermission(WmsPermissions.Transfer);
        Require(fromSiteId != toSiteId, T("error.el_origen_el_destino_deben"));
        Require(lines.Count > 0, T("error.agrega_al_menos_un_articulo"));
        Require(lines.All(l => l.Quantity > 0), T("error.las_cantidades_deben_ser_mayores"));
        var merged = Merge(lines);

        return InTransactionAsync(async (c, tx, now) =>
        {
            foreach (var line in merged)
            {
                await RequireSiteAndItemAsync(c, tx, fromSiteId, line.ItemId);
                var available = await StockLedger.AvailableAsync(c, tx, fromSiteId, line.ItemId);
                Require(available >= line.Quantity,
                    T("error.stock_disponible_insuficiente_de_en", (await ItemLabelAsync(c, tx, line.ItemId)), available));
            }
            await RequireSiteAsync(c, tx, toSiteId);

            var transferId = Guid.NewGuid();
            var number = await DocumentNumbers.NextAsync(c, tx, DocumentNumbers.Transfer, Context.DeviceTag);
            foreach (var line in merged)
            {
                await StockLedger.ApplyAsync(c, tx, now, Context.UserName, new StockLedger.Entry(fromSiteId, line.ItemId,
                    -line.Quantity, MovementKinds.TransferOut, "transfer", transferId, number, Clean(note)));
                await StockLedger.ApplyAsync(c, tx, now, Context.UserName, new StockLedger.Entry(toSiteId, line.ItemId,
                    line.Quantity, MovementKinds.TransferIn, "transfer", transferId, number, Clean(note)));
            }

            await Outbox.EnqueueEventAsync(c, tx, now, SuiteEvents.StockTransferred,
                new StockTransferred(transferId, number, fromSiteId, toSiteId, merged.Select(l => new QuantityLine(l.ItemId, l.Quantity)).ToList()),
                subjectType: "transfer", subjectId: number, id: transferId);
            return number;
        }, ct);
    }

    /// <summary>Ubicación en el almacén (pasillo/estante) y stock mínimo: datos propios del WMS, no viajan al hub.</summary>
    public Task UpdateStockSettingsAsync(Guid siteId, Guid itemId, string? bin, decimal? reorderPoint, CancellationToken ct = default)
    {
        RequirePermission(WmsPermissions.StockSettings);
        Require(reorderPoint is null or >= 0, T("error.el_minimo_no_puede_ser"));
        return InTransactionAsync(async (c, tx, now) =>
        {
            await c.ExecAsync("""
                INSERT INTO stock (site_id, item_id, on_hand, reserved, bin, reorder_point, updated_at)
                VALUES ($site, $item, 0, 0, $bin, $min, $now)
                ON CONFLICT (site_id, item_id) DO UPDATE SET bin = $bin, reorder_point = $min, updated_at = $now
                """, tx, ("$site", siteId), ("$item", itemId), ("$bin", Clean(bin)?.ToUpperInvariant()), ("$min", reorderPoint),
                ("$now", now));
            return 0;
        }, ct);
    }

    // ---------- Kardex ----------

    public async Task<PagedResult<MovementRow>> GetMovementsAsync(MovementQuery q, CancellationToken ct = default)
    {
        await using var c = await Db.OpenAsync(ct);
        const string where = """
            WHERE ($search IS NULL OR i.sku LIKE $like OR i.name LIKE $like OR m.reference LIKE $like)
              AND ($site IS NULL OR m.site_id = $site)
              AND ($kind IS NULL OR m.kind = $kind)
            """;
        var args = new (string, object?)[]
        {
            ("$search", Clean(q.Search)), ("$like", Like(q.Search)), ("$site", q.SiteId), ("$kind", Clean(q.Kind)),
            ("$limit", q.PageSize), ("$offset", (q.Page - 1) * q.PageSize),
        };
        const string from = "FROM movements m LEFT JOIN items i ON i.id = m.item_id LEFT JOIN sites t ON t.id = m.site_id";
        var total = await c.ScalarAsync<int>($"SELECT COUNT(*) {from} {where}", null, args);
        var rows = await c.QueryAsync($"""
            SELECT m.*, COALESCE(i.sku, '?') AS sku, COALESCE(i.name, 'Artículo desconocido') AS item_name,
                   COALESCE(t.name, 'Sede desconocida') AS site_name
            {from} {where} ORDER BY m.occurred_at DESC, m.rowid DESC LIMIT $limit OFFSET $offset
            """, r => new MovementRow(r.Guid("id"), r.Time("occurred_at"), r.Str("kind"), r.Guid("site_id"), r.Str("site_name"),
                r.Guid("item_id"), r.Str("sku"), r.Str("item_name"), r.Dec("quantity"), r.Dec("on_hand_after"),
                r.StrOrNull("reason"), r.StrOrNull("reference"), r.StrOrNull("user_name"), r.StrOrNull("note")), null, args);
        return new PagedResult<MovementRow>(rows, total, q.Page, q.PageSize);
    }

    // ---------- Panel ----------

    public async Task<DashboardSummary> GetDashboardAsync(CancellationToken ct = default)
    {
        await using var c = await Db.OpenAsync(ct);
        var now = Clock.GetUtcNow();
        var d30 = now.AddDays(-30);
        var d60 = now.AddDays(-60);

        var activeItems = await c.ScalarAsync<int>("SELECT COUNT(*) FROM items WHERE is_deleted = 0 AND is_active = 1 AND track_inventory = 1");
        var categories = await c.ScalarAsync<int>("SELECT COUNT(DISTINCT category) FROM items WHERE is_deleted = 0 AND category IS NOT NULL");
        var units = await c.ScalarAsync<decimal>("SELECT COALESCE(SUM(on_hand), 0) FROM stock");
        var units30 = await c.ScalarAsync<decimal>("SELECT COALESCE(SUM(quantity), 0) FROM movements WHERE occurred_at >= $d", null, ("$d", d30));
        var ordersOpen = await c.ScalarAsync<int>("SELECT COUNT(*) FROM orders WHERE status IN ('pending', 'picking')");
        var ordersPicking = await c.ScalarAsync<int>("SELECT COUNT(*) FROM orders WHERE status = 'picking'");
        var dispatched = await c.ScalarAsync<int>("SELECT COUNT(*) FROM orders WHERE status = 'dispatched' AND dispatched_at >= $d", null, ("$d", d30));
        var dispatchedPrev = await c.ScalarAsync<int>("SELECT COUNT(*) FROM orders WHERE status = 'dispatched' AND dispatched_at >= $a AND dispatched_at < $b",
            null, ("$a", d60), ("$b", d30));
        var receipts = await c.ScalarAsync<int>("SELECT COUNT(*) FROM receipts WHERE status = 'expected'");
        var low = await c.ScalarAsync<int>("SELECT COUNT(*) FROM stock WHERE on_hand > 0 AND reorder_point IS NOT NULL AND on_hand - reserved <= reorder_point");
        var outOfStock = await c.ScalarAsync<int>("SELECT COUNT(*) FROM stock s JOIN items i ON i.id = s.item_id WHERE s.on_hand <= 0 AND i.is_deleted = 0");
        var value = await c.ScalarAsync<decimal>("SELECT COALESCE(SUM(s.on_hand * i.price), 0) FROM stock s JOIN items i ON i.id = s.item_id WHERE s.on_hand > 0");
        var currency = await c.ScalarAsync<string?>("SELECT currency_code FROM company WHERE id = 1") ?? "PEN";

        return new DashboardSummary(activeItems, categories, units, Change(units, units - units30), ordersOpen, ordersPicking,
            dispatched, Change(dispatched, dispatchedPrev), receipts, low, outOfStock, value, currency);
    }

    /// <summary>Variación porcentual; null si no hay base de comparación.</summary>
    private static decimal? Change(decimal current, decimal previous) =>
        previous > 0 ? Math.Round((current - previous) / previous * 100, 1) : null;

    // ---------- Internos ----------

    private static (string, object?)[] StockArgs(StockQuery q) =>
    [
        ("$search", Clean(q.Search)), ("$like", Like(q.Search)), ("$site", q.SiteId), ("$category", Clean(q.Category)),
        ("$status", q.Status?.ToString()), ("$limit", q.PageSize), ("$offset", (long)(q.Page - 1) * q.PageSize),
    ];

    private static StockRow ReadStock(SqliteDataReader r) => new(
        r.Guid("site_id"), r.Str("site_code"), r.Str("site_name"), r.Guid("item_id"), r.Str("sku"), r.Str("item_name"),
        r.StrOrNull("category"), r.Str("unit"), r.StrOrNull("barcode"), r.Dec("on_hand"), r.Dec("reserved"),
        r.DecOrNull("reorder_point"), r.StrOrNull("bin"), r.Dec("price"), r.Str("currency_code"), r.Time("updated_at"));

    internal static List<DocumentLine> Merge(IEnumerable<DocumentLine> lines) =>
        lines.GroupBy(l => l.ItemId).Select(g => new DocumentLine(g.Key, g.Sum(l => l.Quantity), g.First().UnitPrice)).ToList();

    internal static async Task RequireSiteAsync(SqliteConnection c, SqliteTransaction tx, Guid siteId) =>
        Require(await c.ScalarAsync<long>("SELECT COUNT(*) FROM sites WHERE id = $id AND is_deleted = 0", tx, ("$id", siteId)) > 0,
            T("error.la_sede_no_existe"));

    internal static async Task RequireSiteAndItemAsync(SqliteConnection c, SqliteTransaction tx, Guid siteId, Guid itemId)
    {
        await RequireSiteAsync(c, tx, siteId);
        var tracks = await c.ScalarAsync<long?>("SELECT track_inventory FROM items WHERE id = $id AND is_deleted = 0", tx, ("$id", itemId));
        Require(tracks is not null, T("error.el_articulo_no_existe"));
        Require(tracks == 1, T("error.el_articulo_no_controla_inventario"));
    }

    internal static async Task<string> ItemLabelAsync(SqliteConnection c, SqliteTransaction? tx, Guid itemId) =>
        await c.ScalarAsync<string?>("SELECT sku || ' · ' || name FROM items WHERE id = $id", tx, ("$id", itemId)) ?? itemId.ToString();

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string Csv(string? value) =>
        value is null ? "" : value.IndexOfAny([',', '"', '\n', '\r']) >= 0 ? $"\"{value.Replace("\"", "\"\"")}\"" : value;

    private static string Num(decimal value) => value.ToString("0.####", CultureInfo.InvariantCulture);
}
