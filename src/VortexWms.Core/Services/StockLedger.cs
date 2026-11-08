using Microsoft.Data.Sqlite;
using VortexWms.Core.Data;
using VortexWms.Core.Domain;
using VortexWms.Core.Integration;
using VortexWms.Core.Sync;

using static VortexWms.Core.Texts.CoreText;

namespace VortexWms.Core.Services;

/// <summary>
/// Único punto donde cambian las existencias: actualiza el saldo, deja el movimiento en el kardex y encola
/// <c>stock.adjusted</c> con el saldo absoluto, todo en la transacción del documento que lo origina.
/// </summary>
internal static class StockLedger
{
    /// <param name="MovementId">Id fijo (derivado de un evento externo); por defecto uno nuevo.</param>
    /// <param name="UnitCost">Costo unitario de una entrada con costo conocido (compra, transferencia); sin él, al promedio.</param>
    /// <param name="SaleAmount">Importe de venta de una salida (ventas y despachos), para el margen.</param>
    /// <param name="Lots">Lotes del movimiento (al recibir, obligatorio si el artículo controla lote; al sacar, si falta, FEFO).</param>
    /// <param name="Serials">Series del movimiento (una por unidad) si el artículo controla serie.</param>
    /// <param name="LotMode">Estricto: faltan lotes o series, se rechaza. Tolerante (ventas de caja): lo que falta queda sin lote.</param>
    /// <param name="AllowFefoOverride">Si se puede sacar de otro lote que el que sugiere FEFO.</param>
    /// <param name="AllowUnavailableLots">Si se puede sacar de lotes vencidos, en cuarentena o bloqueados (mermas).</param>
    public sealed record Entry(
        Guid SiteId, Guid ItemId, decimal Quantity, string Kind, string Reason, Guid? DocumentId = null, string? Reference = null,
        string? Note = null, Guid? MovementId = null, decimal? UnitCost = null, decimal? SaleAmount = null,
        IReadOnlyList<LotQuantity>? Lots = null, IReadOnlyList<string>? Serials = null, LotMode LotMode = LotMode.Strict,
        bool AllowFefoOverride = false, bool AllowUnavailableLots = false);

    public enum LotMode { Strict, Lenient }

    /// <summary>Costo con que se valorizó un movimiento.</summary>
    public sealed record Valued(decimal OnHand, decimal UnitCost, decimal TotalCost, IReadOnlyList<LotQuantity>? Lots = null,
        IReadOnlyList<string>? Serials = null);

    public static async Task<decimal> ApplyAsync(SqliteConnection c, SqliteTransaction tx, DateTimeOffset now, string? userName, Entry e) =>
        (await ApplyValuedAsync(c, tx, now, userName, e)).OnHand;

    /// <summary>Como <see cref="ApplyAsync"/>, y devuelve el costo con que quedó valorizado el movimiento.</summary>
    public static async Task<Valued> ApplyValuedAsync(SqliteConnection c, SqliteTransaction tx, DateTimeOffset now, string? userName, Entry e)
    {
        if (e.MovementId is { } fixedId && await MovementExistsAsync(c, tx, fixedId))
            return await ValuedAsync(c, tx, fixedId, await OnHandAsync(c, tx, e.SiteId, e.ItemId));

        var lots = await LotLedger.ApplyAsync(c, tx, now, e, await OnHandAsync(c, tx, e.SiteId, e.ItemId));
        await c.ExecAsync("""
            INSERT INTO stock (site_id, item_id, on_hand, reserved, updated_at) VALUES ($site, $item, $qty, 0, $now)
            ON CONFLICT (site_id, item_id) DO UPDATE SET on_hand = on_hand + $qty, updated_at = $now
            """, tx, ("$site", e.SiteId), ("$item", e.ItemId), ("$qty", e.Quantity), ("$now", now));
        var onHand = await OnHandAsync(c, tx, e.SiteId, e.ItemId);
        if (e.Quantity < 0)
            await LocationService.TrimAsync(c, tx, e.SiteId, e.ItemId);

        var movementId = e.MovementId ?? Guid.NewGuid();
        await c.ExecAsync("""
            INSERT INTO movements (id, occurred_at, kind, site_id, item_id, quantity, on_hand_after, reason, document_id, reference,
                                   user_name, note, input_cost, sale_amount, operation_type)
            VALUES ($id, $now, $kind, $site, $item, $qty, $after, $reason, $doc, $ref, $user, $note, $input, $sale, $op)
            """, tx, ("$id", movementId), ("$now", now), ("$kind", e.Kind), ("$site", e.SiteId), ("$item", e.ItemId),
            ("$qty", e.Quantity), ("$after", onHand), ("$reason", e.Reason), ("$doc", e.DocumentId), ("$ref", e.Reference),
            ("$user", userName), ("$note", e.Note), ("$input", e.Quantity > 0 ? Valuation.Store(e.UnitCost) : null),
            ("$sale", Valuation.Store(e.SaleAmount)), ("$op", Valuation.OperationType(e.Kind, e.Reason)));
        await Valuation.RevalueAsync(c, tx, e.SiteId, e.ItemId, now, movementId);
        await LotLedger.RecordAsync(c, tx, movementId, lots);
        var valued = await ValuedAsync(c, tx, movementId, onHand);

        await Outbox.EnqueueEventAsync(c, tx, now, SuiteEvents.StockAdjusted,
            new StockAdjusted(e.SiteId, e.ItemId, e.Quantity, onHand, e.Reason, e.Reference, valued.UnitCost, valued.TotalCost,
                e.SaleAmount, lots.Lots.Count > 0 ? lots.Lots : null, lots.Serials.Count > 0 ? lots.Serials : null),
            subjectType: "item", subjectId: e.ItemId.ToString(), id: movementId);
        return valued with { Lots = lots.Lots, Serials = lots.Serials };
    }

    private static async Task<Valued> ValuedAsync(SqliteConnection c, SqliteTransaction tx, Guid movementId, decimal onHand) =>
        await c.FirstOrDefaultAsync("SELECT unit_cost, total_cost FROM movements WHERE id = $id",
            r => new Valued(onHand, r.Dec("unit_cost"), r.Dec("total_cost")), tx, ("$id", movementId)) ?? new Valued(onHand, 0, 0);

    /// <summary>
    /// Costo de entrada de un movimiento replicado: las compras y las transferencias recibidas traen el suyo; lo demás
    /// entra al promedio de este equipo.
    /// </summary>
    private static decimal? ReplicatedInputCost(StockAdjusted s) =>
        s.QuantityDelta > 0 && s.Reason is "receipt" or "transfer" or "sale_void" or "return" ? s.UnitCost : null;

    /// <summary>
    /// Aplica un cambio hecho en otro equipo del WMS (llegó como <c>stock.adjusted</c>): suma el delta y deja el
    /// movimiento con el mismo Id, sin volver a publicarlo. Si el movimiento ya existe igual (propio o repetido), no hace
    /// nada. Si existe con otra sede o cantidad, los dos equipos registraron el mismo documento sin conexión y el hub se
    /// quedó con la versión del otro: se deshace la local, se aplica la del hub y queda el conflicto a la vista.
    /// </summary>
    public static async Task ReplicateAsync(SqliteConnection c, SqliteTransaction tx, Guid movementId, DateTimeOffset occurredAt,
        StockAdjusted s, DateTimeOffset now)
    {
        var local = await c.FirstOrDefaultAsync("SELECT site_id, item_id, quantity, document_id, reference, occurred_at FROM movements WHERE id = $id",
            r => new { Site = r.Guid("site_id"), Item = r.Guid("item_id"), Qty = r.Dec("quantity"), Doc = r.GuidOrNull("document_id"),
                Ref = r.StrOrNull("reference"), At = r.Time("occurred_at") }, tx, ("$id", movementId));
        if (local is not null)
        {
            if (local.Site == s.SiteId && local.Item == s.ItemId && local.Qty == s.QuantityDelta)
            {
                // Mismo movimiento: si FEFO asignó otros lotes aquí, quedan los del hub.
                await LotLedger.ReplicateAsync(c, tx, now, movementId, s.SiteId, s.ItemId, s.QuantityDelta, s.Lots, s.Serials, null, local.Qty);
                return;
            }
            await c.ExecAsync("UPDATE stock SET on_hand = on_hand - $qty, updated_at = $now WHERE site_id = $site AND item_id = $item", tx,
                ("$qty", local.Qty), ("$now", now), ("$site", local.Site), ("$item", local.Item));
            await c.ExecAsync("""
                INSERT INTO stock (site_id, item_id, on_hand, reserved, updated_at) VALUES ($site, $item, $qty, 0, $now)
                ON CONFLICT (site_id, item_id) DO UPDATE SET on_hand = on_hand + $qty, updated_at = $now
                """, tx, ("$site", s.SiteId), ("$item", s.ItemId), ("$qty", s.QuantityDelta), ("$now", now));
            // Queda la versión del hub, con su fecha: así el orden del kardex es el mismo en todos los equipos.
            await c.ExecAsync("""
                UPDATE movements SET site_id = $site, item_id = $item, quantity = $qty, on_hand_after = $after, note = $note,
                                     occurred_at = $at, input_cost = $input, sale_amount = COALESCE($sale, sale_amount)
                WHERE id = $id
                """, tx, ("$id", movementId), ("$site", s.SiteId), ("$item", s.ItemId), ("$qty", s.QuantityDelta),
                ("$after", await OnHandAsync(c, tx, s.SiteId, s.ItemId)), ("$note", "conflict"), ("$at", occurredAt),
                ("$input", Valuation.Store(ReplicatedInputCost(s))), ("$sale", Valuation.Store(s.SaleAmount)));
            await Valuation.RevalueAsync(c, tx, local.Site, local.Item, local.At < occurredAt ? local.At : occurredAt, Guid.Empty);
            if (local.Site != s.SiteId || local.Item != s.ItemId)
                await Valuation.RevalueAsync(c, tx, s.SiteId, s.ItemId, occurredAt, Guid.Empty);
            await LotLedger.ReplicateAsync(c, tx, now, movementId, s.SiteId, s.ItemId, s.QuantityDelta, s.Lots, s.Serials, local.Site, local.Qty);
            await LocationService.TrimAsync(c, tx, local.Site, local.Item);
            await LocationService.TrimAsync(c, tx, s.SiteId, s.ItemId);
            await SyncConflicts.RecordAsync(c, tx, now, SyncConflictKinds.Movement, movementId, local.Ref ?? s.Reference,
                $"{local.Qty:0.##}→{s.QuantityDelta:0.##}");
            return;
        }

        await c.ExecAsync("""
            INSERT INTO stock (site_id, item_id, on_hand, reserved, updated_at) VALUES ($site, $item, $qty, 0, $at)
            ON CONFLICT (site_id, item_id) DO UPDATE SET on_hand = on_hand + $qty, updated_at = MAX(updated_at, $at)
            """, tx, ("$site", s.SiteId), ("$item", s.ItemId), ("$qty", s.QuantityDelta), ("$at", occurredAt));
        await c.ExecAsync("""
            INSERT INTO movements (id, occurred_at, kind, site_id, item_id, quantity, on_hand_after, reason, document_id, reference,
                                   user_name, note, input_cost, sale_amount, operation_type)
            VALUES ($id, $at, $kind, $site, $item, $qty, $after, $reason, NULL, $ref, NULL, 'Otro equipo', $input, $sale, $op)
            """, tx, ("$id", movementId), ("$at", occurredAt), ("$kind", KindFor(s.Reason, s.QuantityDelta)), ("$site", s.SiteId),
            ("$item", s.ItemId), ("$qty", s.QuantityDelta), ("$after", await OnHandAsync(c, tx, s.SiteId, s.ItemId)),
            ("$reason", s.Reason), ("$ref", s.Reference), ("$input", Valuation.Store(ReplicatedInputCost(s))),
            ("$sale", Valuation.Store(s.SaleAmount)), ("$op", Valuation.OperationType(KindFor(s.Reason, s.QuantityDelta), s.Reason)));
        await LotLedger.ReplicateAsync(c, tx, now, movementId, s.SiteId, s.ItemId, s.QuantityDelta, s.Lots, s.Serials, null, null);
        if (s.QuantityDelta < 0)
            await LocationService.TrimAsync(c, tx, s.SiteId, s.ItemId);
        // Si llegó atrasado (un equipo que estuvo sin conexión), se recalcula desde su fecha.
        await Valuation.RevalueAsync(c, tx, s.SiteId, s.ItemId, occurredAt, movementId);
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

    /// <summary>Si el usuario trabaja en la sede (sedes asignadas en el hub; sin asignar, todas).</summary>
    protected bool CanUseSite(Guid siteId) => Context.SiteIds.Count == 0 || Context.SiteIds.Contains(siteId);

    /// <summary>Corta la operación si la sede no está entre las del usuario.</summary>
    protected async Task RequireSiteAccessAsync(SqliteConnection c, SqliteTransaction? tx, Guid siteId)
    {
        if (CanUseSite(siteId))
            return;
        var name = await c.ScalarAsync<string?>("SELECT name FROM sites WHERE id = $id", tx, ("$id", siteId));
        Require(false, T("error.sede_no_asignada", name ?? siteId.ToString()));
    }

    /// <summary>Filtro SQL de las sedes del usuario sobre <paramref name="column"/> (vacío si trabaja en todas).</summary>
    protected string MySites(string column) =>
        Context.SiteIds.Count == 0 ? "" : $" AND {column} IN ({string.Join(", ", Context.SiteIds.Select(s => $"'{s}'"))})";

    /// <summary>Almacén por defecto: el elegido por el usuario o el primer almacén activo de sus sedes.</summary>
    protected async Task<Guid?> DefaultSiteAsync(SqliteConnection c, SqliteTransaction? tx)
    {
        if (Context.DefaultSiteId is { } chosen && CanUseSite(chosen))
            return chosen;
        return await c.ScalarAsync<string?>($"""
            SELECT id FROM sites WHERE is_deleted = 0 AND is_active = 1 {MySites("id")}
            ORDER BY CASE kind WHEN 'Warehouse' THEN 0 ELSE 1 END, code LIMIT 1
            """, tx) is { } id ? Guid.Parse(id) : null;
    }

    protected static string Like(string? term) => $"%{term?.Trim().Replace("%", "").Replace("_", "")}%";

    /// <summary>Corta la operación si el rol del usuario no la permite (el mensaje se muestra tal cual).</summary>
    protected void RequirePermission(string permission)
    {
        if (!Context.Can(permission))
            throw new WmsValidationException(T("error.permiso_supervisor", Context.Role, WmsPermissions.Describe(permission)));
    }

    /// <summary>Deja la acción en la bitácora, en la misma transacción que la acción.</summary>
    protected Task AuditAsync(SqliteConnection c, SqliteTransaction tx, DateTimeOffset now, string action, string entity, Guid? entityId,
        string? reference, string? detail) =>
        c.ExecAsync("""
            INSERT INTO audit_log (id, at, user_id, user_name, device, action, entity, entity_id, reference, detail)
            VALUES ($id, $at, $user, $name, $device, $action, $entity, $entityId, $ref, $detail)
            """, tx, ("$id", Guid.NewGuid()), ("$at", now), ("$user", Context.UserId), ("$name", Context.UserName),
            ("$device", Context.DeviceTag), ("$action", action), ("$entity", entity), ("$entityId", entityId?.ToString()),
            ("$ref", reference), ("$detail", detail));

    protected static void Require(bool condition, string message)
    {
        if (!condition)
            throw new WmsValidationException(message);
    }
}
