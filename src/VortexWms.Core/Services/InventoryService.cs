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
        var total = await c.ScalarAsync<int>($"SELECT COUNT(*) FROM stock s JOIN items i ON i.id = s.item_id JOIN sites t ON t.id = s.site_id {StockWhere}{MySites("s.site_id")}", null, args);
        var rows = await c.QueryAsync($"{StockSelect} {StockWhere}{MySites("s.site_id")} ORDER BY i.name COLLATE NOCASE, t.code LIMIT $limit OFFSET $offset",
            ReadStock, null, args);
        return new PagedResult<StockRow>(await WithCostsAsync(c, rows), total, q.Page, q.PageSize);
    }

    public async Task<StockRow?> GetStockRowAsync(Guid siteId, Guid itemId, CancellationToken ct = default)
    {
        await using var c = await Db.OpenAsync(ct);
        var row = await c.FirstOrDefaultAsync($"{StockSelect} WHERE s.site_id = $site AND s.item_id = $item", ReadStock, null,
            ("$site", siteId), ("$item", itemId));
        return row is null ? null : (await WithCostsAsync(c, [row]))[0];
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
            WHERE i.is_deleted = 0 AND t.is_deleted = 0{MySites("s.site_id")}
              AND (s.on_hand <= 0 OR (s.reorder_point IS NOT NULL AND s.on_hand - s.reserved <= s.reorder_point))
            ORDER BY s.on_hand - COALESCE(s.reorder_point, 0), i.name LIMIT $limit
            """, ReadStock, null, ("$limit", limit));
    }

    /// <summary>CSV (separado por comas, UTF-8) de las existencias que cumplen el filtro, sin paginar.</summary>
    public async Task<string> ExportStockCsvAsync(StockQuery q, CancellationToken ct = default)
    {
        await using var c = await Db.OpenAsync(ct);
        var rows = await c.QueryAsync($"{StockSelect} {StockWhere}{MySites("s.site_id")} ORDER BY i.name COLLATE NOCASE, t.code LIMIT $limit OFFSET $offset",
            ReadStock, null, StockArgs(q with { Page = 1, PageSize = int.MaxValue }));
        var costs = Context.Can(WmsPermissions.ViewCosts);
        var csv = new StringBuilder("sku,articulo,categoria,sede,ubicacion,stock,reservado,disponible,minimo,unidad,precio,moneda,valor"
            + (costs ? ",costo_promedio,valor_costo\n" : "\n"));
        foreach (var r in await WithCostsAsync(c, rows))
        {
            csv.AppendJoin(',', Csv(r.Sku), Csv(r.ItemName), Csv(r.Category), Csv(r.SiteCode), Csv(r.Bin), Num(r.OnHand),
                Num(r.Reserved), Num(r.Available), r.ReorderPoint is { } m ? Num(m) : "", Csv(r.Unit), Num(r.Price),
                Csv(r.CurrencyCode), Num(r.PriceValue));
            if (costs)
                csv.Append(',').Append(r.AvgCost is { } a ? Num(a) : "").Append(',').Append(r.CostValue is { } v ? Num(v) : "");
            csv.Append('\n');
        }
        return csv.ToString();
    }

    // ---------- Operaciones ----------

    /// <param name="countedQuantity">Conteo físico: el saldo pasa a ser este valor.</param>
    /// <param name="delta">Ajuste relativo (positivo o negativo). Se usa uno de los dos.</param>
    /// <param name="lotCode">Lote del ajuste. En artículos con lote es obligatorio para sumar y para contar (el conteo es del lote);
    /// al restar sin lote, sale por FEFO. Un ajuste puede dar de baja lotes vencidos o bloqueados.</param>
    /// <param name="expiresAt">Vencimiento de un lote nuevo.</param>
    /// <param name="serials">Series que entran o salen, en artículos con serie.</param>
    public Task<decimal> AdjustAsync(Guid siteId, Guid itemId, decimal? countedQuantity, decimal? delta, string reason, string? note,
        string? lotCode = null, DateOnly? expiresAt = null, IReadOnlyList<string>? serials = null, CancellationToken ct = default)
    {
        RequirePermission(WmsPermissions.Adjust);
        Require(countedQuantity is not null ^ delta is not null, T("error.indica_la_cantidad_contada_la"));
        Require(AdjustmentReasons.All.Contains(reason), T("error.motivo_de_ajuste_no_valido"));
        Require(countedQuantity is null or >= 0, T("error.el_conteo_no_puede_ser"));

        return InTransactionAsync(async (c, tx, now) =>
        {
            await RequireSiteAndItemAsync(c, tx, siteId, itemId);
            await RequireSiteAccessAsync(c, tx, siteId);
            var current = await StockLedger.OnHandAsync(c, tx, siteId, itemId);
            var tracking = await LotLedger.TrackingAsync(c, tx, itemId);
            var lot = Clean(lotCode)?.ToUpperInvariant();
            if (tracking == ItemTrackings.Lot && countedQuantity is not null)
            {
                // En artículos con lote se cuenta cada lote: el ajuste es la diferencia con el saldo de ese lote.
                Require(lot is not null, T("error.indica_el_lote_de", await ItemLabelAsync(c, tx, itemId)));
                var lotQty = await c.ScalarAsync<decimal>("SELECT COALESCE(SUM(quantity), 0) FROM stock_lots WHERE site_id = $s AND item_id = $i AND lot_code = $l",
                    tx, ("$s", siteId), ("$i", itemId), ("$l", lot));
                current = lotQty;
            }
            var change = countedQuantity is { } counted ? counted - current : delta!.Value;
            Require(change != 0, T("error.el_ajuste_no_cambia_el"));
            Require(current + change >= 0, T("error.el_stock_no_puede_quedar", current));
            var lots = tracking == ItemTrackings.Lot && lot is not null ? new List<LotQuantity> { new(lot, Math.Abs(change), expiresAt) } : null;
            var item = await InventoryService.ItemLabelAsync(c, tx, itemId);
            await AuditAsync(c, tx, now, VortexApi.Suite.Auditing.AuditActions.StockAdjusted, "item", itemId, item,
                $"{(change > 0 ? "+" : "")}{change:0.##} ({reason}){(Clean(note) is { } n ? $": {n}" : "")}");
            return await StockLedger.ApplyAsync(c, tx, now, Context.UserName,
                new StockLedger.Entry(siteId, itemId, change, MovementKinds.Adjustment, reason, Note: Clean(note), Lots: lots, Serials: serials,
                    AllowFefoOverride: true, AllowUnavailableLots: true));
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
            await RequireSiteAccessAsync(c, tx, fromSiteId);
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
            var sentLots = new Dictionary<Guid, (List<LotQuantity>? Lots, IReadOnlyList<string>? Serials)>();
            foreach (var line in merged)
            {
                var sent = await StockLedger.ApplyValuedAsync(c, tx, now, Context.UserName, new StockLedger.Entry(fromSiteId, line.ItemId,
                    -line.Quantity, MovementKinds.TransferOut, "transfer", transferId, number, Clean(note), Lots: line.Lots, Serials: line.Serials,
                    AllowFefoOverride: Context.Can(WmsPermissions.ManageLots)));
                // Llega al destino con el costo con que salió del origen, y con los mismos lotes y series.
                var lots = sent.Lots?.Select(l => l with { Quantity = -l.Quantity }).ToList();
                await StockLedger.ApplyAsync(c, tx, now, Context.UserName, new StockLedger.Entry(toSiteId, line.ItemId,
                    line.Quantity, MovementKinds.TransferIn, "transfer", transferId, number, Clean(note), UnitCost: sent.UnitCost,
                    Lots: lots is { Count: > 0 } ? lots : null, Serials: sent.Serials is { Count: > 0 } s ? s : null,
                    LotMode: StockLedger.LotMode.Lenient));
                sentLots[line.ItemId] = (lots, sent.Serials);
            }

            await Outbox.EnqueueEventAsync(c, tx, now, SuiteEvents.StockTransferred,
                new StockTransferred(transferId, number, fromSiteId, toSiteId, merged.Select(l => new QuantityLine(l.ItemId, l.Quantity, null, null,
                    sentLots.GetValueOrDefault(l.ItemId).Lots is { Count: > 0 } x ? x : null,
                    sentLots.GetValueOrDefault(l.ItemId).Serials is { Count: > 0 } s ? s : null)).ToList()),
                subjectType: "transfer", subjectId: number, id: transferId);
            return number;
        }, ct);
    }

    /// <summary>
    /// Ubicación en el almacén (pasillo/estante), stock mínimo y máximo (fase 6a): datos propios del WMS. Se comparten con los demás
    /// equipos por <c>wms.record.upserted</c>; si dos los cambian, gana el cambio más reciente.
    /// </summary>
    public Task UpdateStockSettingsAsync(Guid siteId, Guid itemId, string? bin, decimal? reorderPoint, decimal? maxStock = null,
        CancellationToken ct = default)
    {
        RequirePermission(WmsPermissions.StockSettings);
        Require(reorderPoint is null or >= 0, T("error.el_minimo_no_puede_ser"));
        Require(maxStock is null or > 0 && (reorderPoint is null || maxStock is null || maxStock >= reorderPoint), T("error.maximo_menor_que_minimo"));
        return InTransactionAsync(async (c, tx, now) =>
        {
            await RequireSiteAccessAsync(c, tx, siteId);
            var settings = new StockSettingsData(siteId, itemId, Clean(bin)?.ToUpperInvariant(), reorderPoint, maxStock);
            await WriteSettingsAsync(c, tx, settings, now);
            await Outbox.EnqueueEventAsync(c, tx, now, SuiteEvents.RecordUpserted,
                new WmsRecord(WmsRecordKinds.StockSettings, $"{siteId}:{itemId}", now,
                    System.Text.Json.JsonSerializer.SerializeToElement(settings, VortexApi.Client.VortexJson.Options)),
                subjectType: WmsRecordKinds.StockSettings, subjectId: itemId.ToString());
            return 0;
        }, ct);
    }

    /// <summary>Ajustes de otro equipo: se aplican solo si son más nuevos que los guardados.</summary>
    internal static async Task ApplySettingsAsync(SqliteConnection c, SqliteTransaction tx, StockSettingsData s, DateTimeOffset updatedAt)
    {
        var current = await c.ScalarAsync<string?>("SELECT settings_updated_at FROM stock WHERE site_id = $site AND item_id = $item", tx,
            ("$site", s.SiteId), ("$item", s.ItemId));
        if (current is not null && string.CompareOrdinal(current, SqliteExtensions.Time(updatedAt)) >= 0)
            return;
        await WriteSettingsAsync(c, tx, s, updatedAt);
    }

    private static Task WriteSettingsAsync(SqliteConnection c, SqliteTransaction tx, StockSettingsData s, DateTimeOffset at) =>
        c.ExecAsync("""
            INSERT INTO stock (site_id, item_id, on_hand, reserved, bin, reorder_point, max_stock, updated_at, settings_updated_at)
            VALUES ($site, $item, 0, 0, $bin, $min, $max, $at, $at)
            ON CONFLICT (site_id, item_id) DO UPDATE SET bin = $bin, reorder_point = $min, max_stock = $max, settings_updated_at = $at
            """, tx, ("$site", s.SiteId), ("$item", s.ItemId), ("$bin", s.Bin), ("$min", s.ReorderPoint), ("$max", s.MaxStock), ("$at", at));

    /// <summary>Valoriza una vez los movimientos anteriores a la fase 1 (bases migradas).</summary>
    internal async Task ValueExistingMovementsAsync(CancellationToken ct = default)
    {
        await using var c = await Db.OpenAsync(ct);
        if (await c.ScalarAsync<long>("SELECT COUNT(*) FROM movements WHERE unit_cost IS NULL") == 0)
            return;
        await using var tx = c.BeginTransaction();
        await Valuation.RevalueAllAsync(c, tx);
        await tx.CommitAsync(ct);
    }

    // ---------- Kardex ----------

    public async Task<PagedResult<MovementRow>> GetMovementsAsync(MovementQuery q, CancellationToken ct = default)
    {
        await using var c = await Db.OpenAsync(ct);
        const string where = """
            WHERE ($search IS NULL OR i.sku LIKE $like OR i.name LIKE $like OR m.reference LIKE $like)
              AND ($site IS NULL OR m.site_id = $site)
              AND ($kind IS NULL OR m.kind = $kind)
              AND ($item IS NULL OR m.item_id = $item)
              AND ($from IS NULL OR m.occurred_at >= $from)
              AND ($to IS NULL OR m.occurred_at < $to)
            """;
        var args = new (string, object?)[]
        {
            ("$search", Clean(q.Search)), ("$like", Like(q.Search)), ("$site", q.SiteId), ("$kind", Clean(q.Kind)),
            ("$item", q.ItemId), ("$from", q.From), ("$to", q.To),
            ("$limit", q.PageSize), ("$offset", (q.Page - 1) * q.PageSize),
        };
        const string from = "FROM movements m LEFT JOIN items i ON i.id = m.item_id LEFT JOIN sites t ON t.id = m.site_id";
        var total = await c.ScalarAsync<int>($"SELECT COUNT(*) {from} {where}{MySites("m.site_id")}", null, args);
        var rows = await c.QueryAsync($"""
            SELECT m.*, COALESCE(i.sku, '?') AS sku, COALESCE(i.name, 'Artículo desconocido') AS item_name,
                   COALESCE(t.name, 'Sede desconocida') AS site_name
            {from} {where}{MySites("m.site_id")} ORDER BY m.occurred_at DESC, m.rowid DESC LIMIT $limit OFFSET $offset
            """, r => new MovementRow(r.Guid("id"), r.Time("occurred_at"), r.Str("kind"), r.Guid("site_id"), r.Str("site_name"),
                r.Guid("item_id"), r.Str("sku"), r.Str("item_name"), r.Dec("quantity"), r.Dec("on_hand_after"),
                r.StrOrNull("reason"), r.StrOrNull("reference"), r.StrOrNull("user_name"), r.StrOrNull("note"),
                r.DecOrNull("unit_cost"), r.DecOrNull("total_cost"), r.DecOrNull("value_after"), r.StrOrNull("operation_type")), null, args);
        if (!Context.Can(WmsPermissions.ViewCosts))
            rows = rows.Select(m => m with { UnitCost = null, TotalCost = null, ValueAfter = null }).ToList();
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
        var units = await c.ScalarAsync<decimal>($"SELECT COALESCE(SUM(on_hand), 0) FROM stock WHERE 1 = 1{MySites("site_id")}");
        var units30 = await c.ScalarAsync<decimal>($"SELECT COALESCE(SUM(quantity), 0) FROM movements WHERE occurred_at >= $d{MySites("site_id")}", null, ("$d", d30));
        var ordersOpen = await c.ScalarAsync<int>($"SELECT COUNT(*) FROM orders WHERE status IN ('pending', 'picking'){MySites("site_id")}");
        var ordersPicking = await c.ScalarAsync<int>($"SELECT COUNT(*) FROM orders WHERE status = 'picking'{MySites("site_id")}");
        var dispatched = await c.ScalarAsync<int>($"SELECT COUNT(*) FROM orders WHERE status = 'dispatched' AND dispatched_at >= $d{MySites("site_id")}", null, ("$d", d30));
        var dispatchedPrev = await c.ScalarAsync<int>($"SELECT COUNT(*) FROM orders WHERE status = 'dispatched' AND dispatched_at >= $a AND dispatched_at < $b{MySites("site_id")}",
            null, ("$a", d60), ("$b", d30));
        var receipts = await c.ScalarAsync<int>($"SELECT COUNT(*) FROM receipts WHERE status = 'expected'{MySites("site_id")}");
        var low = await c.ScalarAsync<int>($"SELECT COUNT(*) FROM stock WHERE on_hand > 0 AND reorder_point IS NOT NULL AND on_hand - reserved <= reorder_point{MySites("site_id")}");
        var outOfStock = await c.ScalarAsync<int>($"SELECT COUNT(*) FROM stock s JOIN items i ON i.id = s.item_id WHERE s.on_hand <= 0 AND i.is_deleted = 0{MySites("s.site_id")}");
        var currency = await Valuation.CurrencyAsync(c, null);

        // Costos (fase 1): valor del inventario a costo, costo de lo vendido y margen de los últimos 30 días. Los montos
        // se suman en decimal, no en SQL, porque se guardan como texto exacto.
        decimal? value = null, costOfSales = null, sales = null, margin = null;
        if (Context.Can(WmsPermissions.ViewCosts))
        {
            var stock = await c.QueryAsync($"SELECT on_hand, avg_cost FROM stock WHERE on_hand > 0{MySites("site_id")}",
                r => r.Dec("on_hand") * r.Dec("avg_cost"));
            value = currency.Round(stock.Sum());
            var sold = await c.QueryAsync($"""
                SELECT total_cost, sale_amount FROM movements
                WHERE occurred_at >= $d AND kind IN ('sale', 'shipment', 'sale_void', 'sale_return'){MySites("site_id")}
                """, r => (Cost: r.Dec("total_cost"), Amount: r.Dec("sale_amount")), null, ("$d", d30));
            costOfSales = currency.Round(-sold.Sum(m => m.Cost));
            sales = currency.Round(sold.Sum(m => m.Amount));
            margin = sales - costOfSales;
        }

        return new DashboardSummary(activeItems, categories, units, Change(units, units - units30), ordersOpen, ordersPicking,
            dispatched, Change(dispatched, dispatchedPrev), receipts, low, outOfStock, value, currency.Code, costOfSales, sales, margin);
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
        r.DecOrNull("reorder_point"), r.StrOrNull("bin"), r.Dec("price"), r.Str("currency_code"), r.Time("updated_at"),
        r.DecOrNull("avg_cost"), MaxStock: r.DecOrNull("max_stock"));

    /// <summary>Costo promedio y valor a costo, solo para quien puede ver costos.</summary>
    private async Task<List<StockRow>> WithCostsAsync(SqliteConnection c, IEnumerable<StockRow> rows)
    {
        if (!Context.Can(WmsPermissions.ViewCosts))
            return rows.Select(r => r with { AvgCost = null, CostValue = null }).ToList();
        var currency = await Valuation.CurrencyAsync(c, null);
        return rows.Select(r => r with { CostValue = r.AvgCost is { } avg ? currency.Round(r.OnHand * avg) : null }).ToList();
    }

    internal static List<DocumentLine> Merge(IEnumerable<DocumentLine> lines) =>
        lines.GroupBy(l => l.ItemId).Select(g => new DocumentLine(g.Key, g.Sum(l => l.Quantity), g.First().UnitPrice,
            g.Any(l => l.Lots is { Count: > 0 }) ? g.SelectMany(l => l.Lots ?? []).ToList() : null,
            g.Any(l => l.Serials is { Count: > 0 }) ? g.SelectMany(l => l.Serials ?? []).ToList() : null)).ToList();

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
