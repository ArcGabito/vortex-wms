using Microsoft.Data.Sqlite;
using VortexWms.Core.Data;
using VortexWms.Core.Domain;
using VortexWms.Core.Integration;
using VortexWms.Core.Sync;

namespace VortexWms.Core.Services;

/// <summary>
/// Único punto donde cambian las existencias: actualiza el saldo, deja el movimiento en el kardex y encola
/// <c>stock.adjusted</c> con el saldo absoluto, todo en la transacción del documento que lo origina.
/// </summary>
internal static class StockLedger
{
    /// <param name="MovementId">Id fijo (derivado de un evento externo); por defecto uno nuevo.</param>
    public sealed record Entry(
        Guid SiteId, Guid ItemId, decimal Quantity, string Kind, string Reason, Guid? DocumentId = null, string? Reference = null,
        string? Note = null, Guid? MovementId = null);

    public static async Task<decimal> ApplyAsync(SqliteConnection c, SqliteTransaction tx, DateTimeOffset now, string? userName, Entry e)
    {
        if (e.MovementId is { } fixedId && await MovementExistsAsync(c, tx, fixedId))
            return await OnHandAsync(c, tx, e.SiteId, e.ItemId);

        await c.ExecAsync("""
            INSERT INTO stock (site_id, item_id, on_hand, reserved, updated_at) VALUES ($site, $item, $qty, 0, $now)
            ON CONFLICT (site_id, item_id) DO UPDATE SET on_hand = on_hand + $qty, updated_at = $now
            """, tx, ("$site", e.SiteId), ("$item", e.ItemId), ("$qty", e.Quantity), ("$now", now));
        var onHand = await OnHandAsync(c, tx, e.SiteId, e.ItemId);

        var movementId = e.MovementId ?? Guid.NewGuid();
        await c.ExecAsync("""
            INSERT INTO movements (id, occurred_at, kind, site_id, item_id, quantity, on_hand_after, reason, document_id, reference, user_name, note)
            VALUES ($id, $now, $kind, $site, $item, $qty, $after, $reason, $doc, $ref, $user, $note)
            """, tx, ("$id", movementId), ("$now", now), ("$kind", e.Kind), ("$site", e.SiteId), ("$item", e.ItemId),
            ("$qty", e.Quantity), ("$after", onHand), ("$reason", e.Reason), ("$doc", e.DocumentId), ("$ref", e.Reference),
            ("$user", userName), ("$note", e.Note));

        await Outbox.EnqueueEventAsync(c, tx, now, SuiteEvents.StockAdjusted,
            new StockAdjusted(e.SiteId, e.ItemId, e.Quantity, onHand, e.Reason, e.Reference),
            subjectType: "item", subjectId: e.ItemId.ToString(), id: movementId);
        return onHand;
    }

    /// <summary>
    /// Aplica un cambio hecho en otro equipo del WMS (llegó como <c>stock.adjusted</c>): suma el delta y deja el
    /// movimiento con el mismo Id, sin volver a publicarlo. Si el movimiento ya existe (propio o repetido), no hace nada.
    /// </summary>
    public static async Task ReplicateAsync(SqliteConnection c, SqliteTransaction tx, Guid movementId, DateTimeOffset occurredAt,
        StockAdjusted s)
    {
        if (await MovementExistsAsync(c, tx, movementId))
            return;

        await c.ExecAsync("""
            INSERT INTO stock (site_id, item_id, on_hand, reserved, updated_at) VALUES ($site, $item, $qty, 0, $at)
            ON CONFLICT (site_id, item_id) DO UPDATE SET on_hand = on_hand + $qty, updated_at = MAX(updated_at, $at)
            """, tx, ("$site", s.SiteId), ("$item", s.ItemId), ("$qty", s.QuantityDelta), ("$at", occurredAt));
        await c.ExecAsync("""
            INSERT INTO movements (id, occurred_at, kind, site_id, item_id, quantity, on_hand_after, reason, document_id, reference, user_name, note)
            VALUES ($id, $at, $kind, $site, $item, $qty, $after, $reason, NULL, $ref, NULL, 'Otro equipo')
            """, tx, ("$id", movementId), ("$at", occurredAt), ("$kind", KindFor(s.Reason, s.QuantityDelta)), ("$site", s.SiteId),
            ("$item", s.ItemId), ("$qty", s.QuantityDelta), ("$after", await OnHandAsync(c, tx, s.SiteId, s.ItemId)),
            ("$reason", s.Reason), ("$ref", s.Reference));
    }

    private static string KindFor(string? reason, decimal delta) => reason switch
    {
        "receipt" => MovementKinds.Receipt,
        "transfer" => delta < 0 ? MovementKinds.TransferOut : MovementKinds.TransferIn,
        "shipment" => MovementKinds.Shipment,
        "sale" => MovementKinds.Sale,
        "sale_void" => MovementKinds.SaleVoid,
        "return" => MovementKinds.SaleReturn,
        _ => MovementKinds.Adjustment,
    };

    private static async Task<bool> MovementExistsAsync(SqliteConnection c, SqliteTransaction tx, Guid id) =>
        await c.ScalarAsync<long>("SELECT COUNT(*) FROM movements WHERE id = $id", tx, ("$id", id)) > 0;

    public static Task ReserveAsync(SqliteConnection c, SqliteTransaction tx, DateTimeOffset now, Guid siteId, Guid itemId, decimal quantity) =>
        c.ExecAsync("""
            INSERT INTO stock (site_id, item_id, on_hand, reserved, updated_at) VALUES ($site, $item, 0, MAX($qty, 0), $now)
            ON CONFLICT (site_id, item_id) DO UPDATE SET reserved = MAX(reserved + $qty, 0), updated_at = $now
            """, tx, ("$site", siteId), ("$item", itemId), ("$qty", quantity), ("$now", now));

    public static Task<decimal> OnHandAsync(SqliteConnection c, SqliteTransaction? tx, Guid siteId, Guid itemId) =>
        c.ScalarAsync<decimal>("SELECT on_hand FROM stock WHERE site_id = $site AND item_id = $item", tx,
            ("$site", siteId), ("$item", itemId));

    public static Task<decimal> AvailableAsync(SqliteConnection c, SqliteTransaction? tx, Guid siteId, Guid itemId) =>
        c.ScalarAsync<decimal>("SELECT on_hand - reserved FROM stock WHERE site_id = $site AND item_id = $item", tx,
            ("$site", siteId), ("$item", itemId));

    /// <summary>Solo los artículos con control de inventario (o aún desconocidos localmente) mueven stock.</summary>
    public static async Task<bool> TracksInventoryAsync(SqliteConnection c, SqliteTransaction tx, Guid itemId) =>
        await c.ScalarAsync<long?>("SELECT track_inventory FROM items WHERE id = $id", tx, ("$id", itemId)) is not 0;
}

internal static class DocumentNumbers
{
    public const string Receipt = "REC";
    public const string Transfer = "TRF";
    public const string Order = "PED";
    public const string Shipment = "DES";

    /// <summary>Numeración por equipo (<c>REC-3F0C-00012</c>): dos equipos sin conexión nunca generan el mismo número.</summary>
    public static async Task<string> NextAsync(SqliteConnection c, SqliteTransaction tx, string prefix, string deviceTag)
    {
        var value = await c.ScalarAsync<long>("""
            INSERT INTO document_sequences (prefix, value) VALUES ($p, 1)
            ON CONFLICT (prefix) DO UPDATE SET value = value + 1 RETURNING value
            """, tx, ("$p", prefix));
        return $"{prefix}-{deviceTag}-{value:D5}";
    }
}

/// <summary>Base de los servicios: base local, contexto del usuario y reloj.</summary>
public abstract class WmsService(WmsDatabase db, IWmsContext context, TimeProvider clock)
{
    protected WmsDatabase Db { get; } = db;
    protected IWmsContext Context { get; } = context;
    protected TimeProvider Clock { get; } = clock;

    protected async Task<T> InTransactionAsync<T>(Func<SqliteConnection, SqliteTransaction, DateTimeOffset, Task<T>> work,
        CancellationToken ct = default)
    {
        await using var c = await Db.OpenAsync(ct);
        await using var tx = c.BeginTransaction();
        var result = await work(c, tx, Clock.GetUtcNow());
        await tx.CommitAsync(ct);
        return result;
    }

    /// <summary>Almacén por defecto: el elegido por el usuario o el primer almacén activo.</summary>
    protected async Task<Guid?> DefaultSiteAsync(SqliteConnection c, SqliteTransaction? tx)
    {
        if (Context.DefaultSiteId is { } chosen)
            return chosen;
        return await c.ScalarAsync<string?>("""
            SELECT id FROM sites WHERE is_deleted = 0 AND is_active = 1
            ORDER BY CASE kind WHEN 'Warehouse' THEN 0 ELSE 1 END, code LIMIT 1
            """, tx) is { } id ? Guid.Parse(id) : null;
    }

    protected static string Like(string? term) => $"%{term?.Trim().Replace("%", "").Replace("_", "")}%";

    protected static void Require(bool condition, string message)
    {
        if (!condition)
            throw new WmsValidationException(message);
    }
}
