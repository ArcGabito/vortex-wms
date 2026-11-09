using System.Text.Json;
using Microsoft.Data.Sqlite;
using VortexApi.Client;
using VortexWms.Core.Data;
using VortexWms.Core.Domain;
using VortexWms.Core.Integration;
using VortexWms.Core.Sync;

using static VortexWms.Core.Texts.CoreText;

namespace VortexWms.Core.Services;

/// <summary>
/// Lotes, vencimientos y series: saldos por lote, lotes por vencer, estado de cada lote (disponible, cuarentena,
/// bloqueado), vida útil por artículo y trazabilidad de un lote o serie hasta sus proveedores y clientes.
/// </summary>
public sealed class LotService(WmsDatabase db, IWmsContext context, TimeProvider clock) : WmsService(db, context, clock)
{
    /// <summary>Días antes del vencimiento en que un lote pasa a "por vencer" por defecto.</summary>
    public const int DefaultExpiringDays = 30;

    // ---------- Consultas ----------

    public async Task<PagedResult<LotRow>> GetLotsAsync(LotQuery q, CancellationToken ct = default)
    {
        await using var c = await Db.OpenAsync(ct);
        var today = await LotLedger.TodayAsync(c, null, Clock.GetUtcNow());
        var where = $"""
            WHERE l.lot_code <> '' AND ($site IS NULL OR l.site_id = $site) AND ($item IS NULL OR l.item_id = $item)
              AND ($empty = 1 OR l.quantity > 0)
              AND ($days IS NULL OR (l.expires_at IS NOT NULL AND l.expires_at <= $limit))
              AND ($search IS NULL OR l.lot_code LIKE $like OR i.sku LIKE $like OR i.name LIKE $like){MySites("l.site_id")}
            """;
        var args = new (string, object?)[]
        {
            ("$site", q.SiteId), ("$item", q.ItemId), ("$empty", q.IncludeEmpty), ("$days", q.ExpiringWithinDays),
            ("$limit", LotLedger.Day(today.AddDays(q.ExpiringWithinDays ?? 0))), ("$search", Clean(q.Search)), ("$like", Like(q.Search)),
            ("$take", q.PageSize), ("$skip", (q.Page - 1) * q.PageSize),
        };
        const string from = "FROM stock_lots l JOIN items i ON i.id = l.item_id JOIN sites s ON s.id = l.site_id";
        var total = await c.ScalarAsync<int>($"SELECT COUNT(*) {from} {where}", null, args);
        var rows = await c.QueryAsync($"""
            SELECT l.*, s.name AS site_name, i.sku, i.name AS item_name {from} {where}
            ORDER BY l.expires_at IS NULL, l.expires_at, i.name, l.lot_code LIMIT $take OFFSET $skip
            """, r =>
            {
                var expires = LotLedger.DayOrNull(r.StrOrNull("expires_at"));
                return new LotRow(r.Guid("site_id"), r.Str("site_name"), r.Guid("item_id"), r.Str("sku"), r.Str("item_name"), r.Str("lot_code"),
                    expires, r.Dec("quantity"), r.Str("status"), expires is { } e ? e.DayNumber - today.DayNumber : null);
            }, null, args);
        return new PagedResult<LotRow>(rows, total, q.Page, q.PageSize);
    }

    /// <summary>Lotes con saldo que vencen dentro de <paramref name="days"/> días (o ya vencieron), para el panel.</summary>
    public async Task<(int Count, int Expired)> GetExpiringCountAsync(int days = DefaultExpiringDays, CancellationToken ct = default)
    {
        await using var c = await Db.OpenAsync(ct);
        var today = await LotLedger.TodayAsync(c, null, Clock.GetUtcNow());
        var rows = await c.QueryAsync($"""
            SELECT COUNT(*) AS n, SUM(CASE WHEN expires_at < $today THEN 1 ELSE 0 END) AS expired FROM stock_lots
            WHERE quantity > 0 AND lot_code <> '' AND expires_at IS NOT NULL AND expires_at <= $limit{MySites("site_id")}
            """, r => (r.Int("n"), r.IsDBNull(r.GetOrdinal("expired")) ? 0 : r.Int("expired")), null,
            ("$today", LotLedger.Day(today)), ("$limit", LotLedger.Day(today.AddDays(days))));
        return rows[0];
    }

    public async Task<IReadOnlyList<SerialRow>> GetSerialsAsync(Guid itemId, Guid? siteId = null, bool inStockOnly = true, CancellationToken ct = default)
    {
        await using var c = await Db.OpenAsync(ct);
        return await c.QueryAsync($"""
            SELECT x.*, s.name AS site_name FROM serials x LEFT JOIN sites s ON s.id = x.site_id
            WHERE x.item_id = $item AND ($site IS NULL OR x.site_id = $site) AND ($only = 0 OR x.status = 'in_stock')
            ORDER BY x.serial
            """, r => new SerialRow(r.Guid("item_id"), r.Str("serial"), r.GuidOrNull("site_id"), r.StrOrNull("site_name"), r.Str("status"),
                r.StrOrNull("lot_code"), r.Time("updated_at")), null, ("$item", itemId), ("$site", siteId), ("$only", inStockOnly));
    }

    /// <summary>Lo que FEFO sacaría para despachar o transferir <paramref name="quantity"/> (para mostrarlo antes de confirmar).</summary>
    public async Task<IReadOnlyList<LotQuantity>> SuggestAsync(Guid siteId, Guid itemId, decimal quantity, CancellationToken ct = default)
    {
        await using var c = await Db.OpenAsync(ct);
        return await LotLedger.SuggestAsync(c, null, siteId, itemId, quantity, await LotLedger.TodayAsync(c, null, Clock.GetUtcNow()));
    }

    /// <summary>
    /// Trazabilidad de un lote o una serie (de un artículo o de todos): cada movimiento con su documento y a quién se le
    /// compró (recepciones) o se le vendió (despachos), para un retiro del mercado.
    /// </summary>
    public async Task<IReadOnlyList<TraceRow>> TraceAsync(string code, Guid? itemId = null, CancellationToken ct = default)
    {
        var key = (code ?? "").Trim().ToUpperInvariant();
        Require(key.Length > 0, T("error.escribe_el_lote_o_serie"));
        await using var c = await Db.OpenAsync(ct);
        return await c.QueryAsync("""
            SELECT m.occurred_at, m.kind, m.site_id, s.name AS site_name, m.item_id, i.name AS item_name, x.lot_code, x.quantity,
                   m.reference, COALESCE(r.supplier_id, o.customer_id) AS party_id, p.name AS party_name
            FROM (SELECT movement_id, lot_code, quantity FROM movement_lots WHERE lot_code = $code
                  UNION ALL
                  SELECT ms.movement_id, ms.serial, CASE WHEN mv.quantity < 0 THEN -1 ELSE 1 END
                  FROM movement_serials ms JOIN movements mv ON mv.id = ms.movement_id WHERE ms.serial = $code) x
            JOIN movements m ON m.id = x.movement_id
            LEFT JOIN sites s ON s.id = m.site_id LEFT JOIN items i ON i.id = m.item_id
            LEFT JOIN receipts r ON r.id = m.document_id LEFT JOIN orders o ON o.id = m.document_id
            LEFT JOIN parties p ON p.id = COALESCE(r.supplier_id, o.customer_id)
            WHERE $item IS NULL OR m.item_id = $item
            ORDER BY m.occurred_at, m.rowid
            """, r => new TraceRow(r.Time("occurred_at"), r.Str("kind"), r.Guid("site_id"), r.StrOrNull("site_name"), r.Guid("item_id"),
                r.StrOrNull("item_name") ?? "", r.Str("lot_code"), r.Dec("quantity"), r.StrOrNull("reference"), r.GuidOrNull("party_id"),
                r.StrOrNull("party_name")), null, ("$code", key), ("$item", itemId));
    }

    // ---------- Estado de un lote ----------

    /// <summary>Pone un lote en cuarentena, lo bloquea o lo libera. Se comparte con los demás equipos (gana el último cambio).</summary>
    public Task SetStatusAsync(Guid siteId, Guid itemId, string lotCode, string status, string? note, CancellationToken ct = default)
    {
        RequirePermission(WmsPermissions.ManageLots);
        Require(LotStatuses.All.Contains(status), T("error.estado_de_lote_no_valido"));
        var code = (lotCode ?? "").Trim().ToUpperInvariant();
        return InTransactionAsync(async (c, tx, now) =>
        {
            await RequireSiteAccessAsync(c, tx, siteId);
            Require(await c.ScalarAsync<long>("SELECT COUNT(*) FROM stock_lots WHERE site_id = $site AND item_id = $item AND lot_code = $lot", tx,
                ("$site", siteId), ("$item", itemId), ("$lot", code)) > 0, T("error.el_lote_no_existe", code));
            await PublishStatusAsync(c, tx, now, new LotStatusData(siteId, itemId, code, status, Clean(note)));
            await AuditAsync(c, tx, now, "lot.status", "item", itemId, code, $"{status}{(Clean(note) is { } n ? $": {n}" : "")}");
            return 0;
        }, ct);
    }

    /// <summary>Cambia el estado del lote y lo replica (en la transacción de quien llama).</summary>
    internal static async Task PublishStatusAsync(SqliteConnection c, SqliteTransaction tx, DateTimeOffset now, LotStatusData data)
    {
        await ApplyStatusAsync(c, tx, data, now);
        await Outbox.EnqueueEventAsync(c, tx, now, SuiteEvents.RecordUpserted,
            new WmsRecord(WmsRecordKinds.LotStatus, $"{data.SiteId}:{data.ItemId}:{data.LotCode}", now,
                JsonSerializer.SerializeToElement(data, VortexJson.Options)),
            subjectType: "lot", subjectId: data.LotCode);
    }

    /// <summary>Aplica el estado si es más nuevo que el que hay (el último cambio gana, en este y en los demás equipos).</summary>
    internal static async Task<bool> ApplyStatusAsync(SqliteConnection c, SqliteTransaction tx, LotStatusData s, DateTimeOffset updatedAt)
    {
        var current = await c.ScalarAsync<string?>("""
            SELECT MAX(status_updated_at) FROM stock_lots WHERE site_id = $site AND item_id = $item AND lot_code = $lot
            """, tx, ("$site", s.SiteId), ("$item", s.ItemId), ("$lot", s.LotCode));
        if (current is not null && string.CompareOrdinal(current, SqliteExtensions.Time(updatedAt)) >= 0)
            return false;
        // Si el lote aún no llegó a este equipo, se crea vacío: cuando llegue su entrada ya tendrá el estado.
        await c.ExecAsync("""
            INSERT INTO stock_lots (site_id, item_id, location, lot_code, quantity, status, status_updated_at)
            VALUES ($site, $item, '', $lot, 0, $status, $at)
            ON CONFLICT (site_id, item_id, location, lot_code) DO NOTHING
            """, tx, ("$site", s.SiteId), ("$item", s.ItemId), ("$lot", s.LotCode), ("$status", s.Status), ("$at", updatedAt));
        await c.ExecAsync("""
            UPDATE stock_lots SET status = $status, status_updated_at = $at WHERE site_id = $site AND item_id = $item AND lot_code = $lot
            """, tx, ("$site", s.SiteId), ("$item", s.ItemId), ("$lot", s.LotCode), ("$status", s.Status), ("$at", updatedAt));
        return true;
    }

    // ---------- Vida útil ----------

    public async Task<ItemShelfLife> GetShelfLifeAsync(Guid itemId, CancellationToken ct = default)
    {
        await using var c = await Db.OpenAsync(ct);
        return await ShelfLifeAsync(c, null, itemId);
    }

    /// <summary>Vida útil del artículo y días mínimos que debe tener un lote al recibirlo. Se comparte con los demás equipos.</summary>
    public Task SetShelfLifeAsync(Guid itemId, int? shelfLifeDays, int? minDaysOnReceipt, CancellationToken ct = default)
    {
        RequirePermission(WmsPermissions.StockSettings);
        Require(shelfLifeDays is null or > 0 && minDaysOnReceipt is null or >= 0, T("error.dias_no_validos"));
        Require(shelfLifeDays is null || minDaysOnReceipt is null || minDaysOnReceipt <= shelfLifeDays, T("error.dias_minimos_mayores_que_vida"));
        return InTransactionAsync(async (c, tx, now) =>
        {
            var data = new ItemShelfLife(itemId, shelfLifeDays, minDaysOnReceipt);
            await ApplyShelfLifeAsync(c, tx, data, now);
            await Outbox.EnqueueEventAsync(c, tx, now, SuiteEvents.RecordUpserted,
                new WmsRecord(WmsRecordKinds.ShelfLife, itemId.ToString(), now, JsonSerializer.SerializeToElement(data, VortexJson.Options)),
                subjectType: "item", subjectId: itemId.ToString());
            return 0;
        }, ct);
    }

    internal static async Task<bool> ApplyShelfLifeAsync(SqliteConnection c, SqliteTransaction tx, ItemShelfLife s, DateTimeOffset updatedAt)
    {
        var current = await c.ScalarAsync<string?>("SELECT updated_at FROM item_shelf_life WHERE item_id = $id", tx, ("$id", s.ItemId));
        if (current is not null && string.CompareOrdinal(current, SqliteExtensions.Time(updatedAt)) >= 0)
            return false;
        await c.ExecAsync("""
            INSERT INTO item_shelf_life (item_id, shelf_life_days, min_days_on_receipt, updated_at) VALUES ($id, $life, $min, $at)
            ON CONFLICT (item_id) DO UPDATE SET shelf_life_days = $life, min_days_on_receipt = $min, updated_at = $at
            """, tx, ("$id", s.ItemId), ("$life", s.ShelfLifeDays), ("$min", s.MinDaysOnReceipt), ("$at", updatedAt));
        return true;
    }

    private static async Task<ItemShelfLife> ShelfLifeAsync(SqliteConnection c, SqliteTransaction? tx, Guid itemId) =>
        await c.FirstOrDefaultAsync("SELECT * FROM item_shelf_life WHERE item_id = $id", r => new ItemShelfLife(itemId,
            r.IsDBNull(r.GetOrdinal("shelf_life_days")) ? null : r.Int("shelf_life_days"),
            r.IsDBNull(r.GetOrdinal("min_days_on_receipt")) ? null : r.Int("min_days_on_receipt")), tx, ("$id", itemId))
        ?? new ItemShelfLife(itemId, null, null);

    /// <summary>
    /// Al recibir: un lote sin vencimiento toma hoy + la vida útil del artículo; un lote con menos días de vida que el mínimo
    /// solo se acepta con permiso. Devuelve los lotes con su vencimiento.
    /// </summary>
    internal static async Task<IReadOnlyList<LotQuantity>?> CheckShelfLifeAsync(SqliteConnection c, SqliteTransaction tx, Guid itemId,
        IReadOnlyList<LotQuantity>? lots, DateOnly today, bool canOverride)
    {
        if (lots is not { Count: > 0 })
            return lots;
        var life = await ShelfLifeAsync(c, tx, itemId);
        var result = new List<LotQuantity>();
        foreach (var lot in lots)
        {
            var expires = lot.ExpiresAt ?? (life.ShelfLifeDays is { } days ? today.AddDays(days) : null);
            if (life.MinDaysOnReceipt is { } min && expires is { } exp && exp.DayNumber - today.DayNumber < min && !canOverride)
                throw new WmsValidationException(T("error.lote_con_poca_vida", lot.LotCode, exp.DayNumber - today.DayNumber, min));
            result.Add(lot with { ExpiresAt = expires });
        }
        return result;
    }

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
