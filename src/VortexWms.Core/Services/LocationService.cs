using System.Globalization;
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
/// Mapa de ubicaciones por sede, stock por ubicación y traslados internos (fase 4a). El stock de la sede no cambia al
/// moverlo de ubicación; lo que no está en ninguna queda "por guardar". El mapa se replica como <c>wms.record.upserted</c>
/// y cada traslado como <c>stock.relocated</c>, que solo consumen los demás equipos del WMS.
/// </summary>
public sealed class LocationService(WmsDatabase db, IWmsContext context, TimeProvider clock) : WmsService(db, context, clock)
{
    // ---------- Mapa ----------

    public async Task<IReadOnlyList<LocationRow>> GetLocationsAsync(Guid siteId, bool includeInactive = false, string? search = null,
        CancellationToken ct = default)
    {
        await using var c = await Db.OpenAsync(ct);
        return await c.QueryAsync($"""
            SELECT l.*, i.name AS fixed_item_name,
                   COALESCE((SELECT SUM(quantity) FROM location_stock s WHERE s.location_id = l.id AND s.quantity > 0), 0) AS units,
                   (SELECT COUNT(*) FROM location_stock s WHERE s.location_id = l.id AND s.quantity > 0) AS items
            FROM locations l LEFT JOIN items i ON i.id = l.fixed_item_id
            WHERE l.site_id = $site AND ($all = 1 OR l.is_active = 1) AND ($q IS NULL OR l.code LIKE $like OR l.zone LIKE $like)
            ORDER BY l.code
            """, ReadLocation, null, ("$site", siteId), ("$all", includeInactive), ("$q", Clean(search)), ("$like", Like(search)));
    }

    /// <summary>La ubicación activa con ese código en la sede (para confirmar escaneando la etiqueta).</summary>
    public async Task<LocationRow?> FindByCodeAsync(Guid siteId, string code, CancellationToken ct = default)
    {
        await using var c = await Db.OpenAsync(ct);
        return await c.FirstOrDefaultAsync("""
            SELECT l.*, NULL AS fixed_item_name, 0 AS units, 0 AS items FROM locations l
            WHERE l.site_id = $site AND l.code = $code AND l.is_active = 1
            """, ReadLocation, null, ("$site", siteId), ("$code", NormalizeCode(code)));
    }

    /// <summary>Crea o edita una ubicación. El código es único por sede; sin código se arma con zona, pasillo, rack, nivel y posición.</summary>
    public Task<Guid> SaveAsync(Guid? id, LocationDraft d, CancellationToken ct = default)
    {
        RequirePermission(WmsPermissions.ManageLocations);
        Require(LocationKinds.All.Contains(d.Kind), T("error.tipo_de_ubicacion_no_valido"));
        var code = NormalizeCode(Clean(d.Code) ?? string.Join("-", new[] { d.Zone, d.Aisle, d.Rack, d.Level, d.Position }
            .Select(Clean).Where(p => p is not null)));
        Require(code.Length > 0, T("error.la_ubicacion_necesita_un_codigo"));
        Require(d.MinQuantity is null or >= 0 && d.MaxQuantity is null or > 0, T("error.la_cantidad_debe_ser_mayor"));
        Require(d.MinQuantity is null || d.MaxQuantity is null || d.MaxQuantity >= d.MinQuantity, T("error.maximo_menor_que_minimo"));
        return InTransactionAsync(async (c, tx, now) =>
        {
            await RequireSiteAccessAsync(c, tx, d.SiteId);
            var locationId = id ?? Guid.NewGuid();
            Require(await c.ScalarAsync<long>("SELECT COUNT(*) FROM locations WHERE site_id = $site AND code = $code AND id <> $id", tx,
                ("$site", d.SiteId), ("$code", code), ("$id", locationId)) == 0, T("error.ya_existe_la_ubicacion", code));
            if (id is { } existing)
            {
                var site = await c.ScalarAsync<string?>("SELECT site_id FROM locations WHERE id = $id", tx, ("$id", existing));
                Require(site is not null, T("error.la_ubicacion_no_existe"));
                Require(Guid.Parse(site!) == d.SiteId, T("error.la_ubicacion_es_de_otra_sede"));
                if (!d.IsActive)
                    Require(await UnitsAsync(c, tx, existing) == 0, T("error.la_ubicacion_tiene_stock"));
            }
            var data = new LocationData(locationId, d.SiteId, code, d.Kind, Clean(d.Zone)?.ToUpperInvariant(), Clean(d.Aisle), Clean(d.Rack),
                Clean(d.Level), Clean(d.Position), d.FixedItemId, d.IsActive, d.MinQuantity, d.MaxQuantity);
            await PublishAsync(c, tx, now, data);
            await AuditAsync(c, tx, now, id is null ? "location.created" : "location.updated", "location", locationId, code, d.Kind);
            return locationId;
        }, ct);
    }

    /// <summary>Alta por rango: <c>{zona}-{pasillo:00}-{nivel:00}</c>. Las que ya existen se saltan. Devuelve cuántas creó.</summary>
    public Task<int> CreateRangeAsync(LocationRange r, CancellationToken ct = default)
    {
        RequirePermission(WmsPermissions.ManageLocations);
        Require(LocationKinds.All.Contains(r.Kind), T("error.tipo_de_ubicacion_no_valido"));
        var zone = NormalizeCode(r.Zone);
        Require(zone.Length > 0, T("error.la_ubicacion_necesita_un_codigo"));
        Require(r.AisleFrom >= 0 && r.AisleTo >= r.AisleFrom && r.LevelFrom >= 0 && r.LevelTo >= r.LevelFrom, T("error.rango_no_valido"));
        var total = (r.AisleTo - r.AisleFrom + 1) * (r.LevelTo - r.LevelFrom + 1);
        Require(total <= 2000, T("error.rango_demasiado_grande", 2000));
        return InTransactionAsync(async (c, tx, now) =>
        {
            await RequireSiteAccessAsync(c, tx, r.SiteId);
            var created = 0;
            for (var aisle = r.AisleFrom; aisle <= r.AisleTo; aisle++)
            for (var level = r.LevelFrom; level <= r.LevelTo; level++)
            {
                var (a, l) = (aisle.ToString("00", CultureInfo.InvariantCulture), level.ToString("00", CultureInfo.InvariantCulture));
                var code = $"{zone}-{a}-{l}";
                if (await c.ScalarAsync<long>("SELECT COUNT(*) FROM locations WHERE site_id = $site AND code = $code", tx,
                        ("$site", r.SiteId), ("$code", code)) > 0)
                    continue;
                await PublishAsync(c, tx, now, new LocationData(Guid.NewGuid(), r.SiteId, code, r.Kind, zone, a, null, l, null, null, true));
                created++;
            }
            await AuditAsync(c, tx, now, "location.range", "site", r.SiteId, $"{zone}-{r.AisleFrom:00}-{r.LevelFrom:00}…{zone}-{r.AisleTo:00}-{r.LevelTo:00}",
                created.ToString(CultureInfo.InvariantCulture));
            return created;
        }, ct);
    }

    // ---------- Stock por ubicación ----------

    public async Task<IReadOnlyList<LocationStockRow>> GetLocationStockAsync(Guid locationId, CancellationToken ct = default)
    {
        await using var c = await Db.OpenAsync(ct);
        return await c.QueryAsync("""
            SELECT s.location_id, l.code, s.item_id, i.sku, i.name, i.unit, s.quantity
            FROM location_stock s JOIN locations l ON l.id = s.location_id JOIN items i ON i.id = s.item_id
            WHERE s.location_id = $loc AND s.quantity > 0 ORDER BY i.name
            """, ReadStock, null, ("$loc", locationId));
    }

    /// <summary>Dónde está un artículo en la sede (para mover o preparar).</summary>
    public async Task<IReadOnlyList<LocationStockRow>> GetItemLocationsAsync(Guid siteId, Guid itemId, CancellationToken ct = default)
    {
        await using var c = await Db.OpenAsync(ct);
        return await c.QueryAsync("""
            SELECT s.location_id, l.code, s.item_id, i.sku, i.name, i.unit, s.quantity
            FROM location_stock s JOIN locations l ON l.id = s.location_id JOIN items i ON i.id = s.item_id
            WHERE s.site_id = $site AND s.item_id = $item AND s.quantity > 0 ORDER BY l.code
            """, ReadStock, null, ("$site", siteId), ("$item", itemId));
    }

    /// <summary>Lo que está en la sede sin ubicación (lo recibido por guardar), con la ubicación que se sugiere.</summary>
    public async Task<IReadOnlyList<PutawayRow>> GetPendingPutawayAsync(Guid? siteId = null, CancellationToken ct = default)
    {
        await using var c = await Db.OpenAsync(ct);
        var pending = await c.QueryAsync($"""
            SELECT st.site_id, s.name AS site_name, st.item_id, i.sku, i.name, i.unit,
                   st.on_hand - COALESCE((SELECT SUM(quantity) FROM location_stock ls WHERE ls.site_id = st.site_id AND ls.item_id = st.item_id), 0) AS pending
            FROM stock st JOIN items i ON i.id = st.item_id LEFT JOIN sites s ON s.id = st.site_id
            WHERE ($site IS NULL OR st.site_id = $site){MySites("st.site_id")}
              AND EXISTS (SELECT 1 FROM locations l WHERE l.site_id = st.site_id AND l.is_active = 1)
            """, r => (Site: r.Guid("site_id"), SiteName: r.StrOrNull("site_name"), Item: r.Guid("item_id"), Sku: r.Str("sku"),
                Name: r.Str("name"), Unit: r.Str("unit"), Pending: r.Dec("pending")), null, ("$site", siteId));
        var rows = new List<PutawayRow>();
        foreach (var p in pending.Where(p => p.Pending > 0).OrderBy(p => p.Name))
        {
            var s = await SuggestAsync(c, null, p.Site, p.Item);
            rows.Add(new PutawayRow(p.Site, p.SiteName, p.Item, p.Sku, p.Name, p.Unit, p.Pending, s?.Id, s?.Code, s?.Reason));
        }
        return rows;
    }

    public async Task<IReadOnlyList<RelocationRow>> GetRelocationsAsync(Guid siteId, Guid? itemId = null, int limit = 50, CancellationToken ct = default)
    {
        await using var c = await Db.OpenAsync(ct);
        return await c.QueryAsync("""
            SELECT r.*, i.name AS item_name, f.code AS from_code, t.code AS to_code
            FROM relocations r JOIN items i ON i.id = r.item_id
            LEFT JOIN locations f ON f.id = r.from_location_id LEFT JOIN locations t ON t.id = r.to_location_id
            WHERE r.site_id = $site AND ($item IS NULL OR r.item_id = $item)
            ORDER BY r.occurred_at DESC, r.rowid DESC LIMIT $limit
            """, r => new RelocationRow(r.Guid("id"), r.Time("occurred_at"), r.Guid("item_id"), r.Str("item_name"), r.StrOrNull("from_code"),
                r.StrOrNull("to_code"), r.Dec("quantity"), r.StrOrNull("user_name"), r.StrOrNull("reference")), null,
            ("$site", siteId), ("$item", itemId), ("$limit", limit));
    }

    // ---------- Guardar y trasladar ----------

    /// <summary>
    /// Mueve stock de una ubicación a otra de la misma sede (sin <paramref name="fromLocationId"/>: guarda lo que estaba sin
    /// ubicación; sin <paramref name="toLocationId"/>: lo deja sin ubicación). No cambia el stock de la sede.
    /// </summary>
    public Task RelocateAsync(Guid siteId, Guid itemId, Guid? fromLocationId, Guid? toLocationId, decimal quantity, string? reference = null,
        CancellationToken ct = default)
    {
        RequirePermission(WmsPermissions.Relocate);
        Require(quantity > 0, T("error.la_cantidad_debe_ser_mayor"));
        Require(fromLocationId != toLocationId, T("error.elige_otra_ubicacion"));
        return InTransactionAsync(async (c, tx, now) =>
        {
            await RelocateCoreAsync(c, tx, now, siteId, itemId, fromLocationId, toLocationId, quantity, reference);
            return 0;
        }, ct);
    }

    private async Task RelocateCoreAsync(SqliteConnection c, SqliteTransaction tx, DateTimeOffset now, Guid siteId, Guid itemId,
        Guid? fromLocationId, Guid? toLocationId, decimal quantity, string? reference)
    {
        {
            await RequireSiteAccessAsync(c, tx, siteId);
            foreach (var loc in new[] { fromLocationId, toLocationId })
            {
                if (loc is not { } l)
                    continue;
                var found = await First(c.QueryAsync("SELECT site_id, is_active, code FROM locations WHERE id = $id",
                    r => (Site: r.Guid("site_id"), Active: r.Bool("is_active"), Code: r.Str("code")), tx, ("$id", l)));
                Require(found is not null, T("error.la_ubicacion_no_existe"));
                var info = found!.Value;
                Require(info.Site == siteId, T("error.la_ubicacion_es_de_otra_sede"));
                Require(loc != toLocationId || info.Active, T("error.la_ubicacion_esta_inactiva", info.Code));
            }
            var available = fromLocationId is { } from
                ? await QuantityAsync(c, tx, siteId, from, itemId)
                : await UnassignedAsync(c, tx, siteId, itemId);
            Require(quantity <= available, T("error.solo_hay_en_la_ubicacion", available));

            var e = new StockRelocated(siteId, itemId, fromLocationId, toLocationId, quantity, Clean(reference));
            var id = Guid.NewGuid();
            await ApplyAsync(c, tx, id, now, e, Context.UserName);
            await Outbox.EnqueueEventAsync(c, tx, now, SuiteEvents.StockRelocated, e, subjectType: "item", subjectId: itemId.ToString(), id: id);
        }
    }

    // ---------- Reabastecimiento (fase 4b2) ----------

    /// <summary>
    /// Ubicaciones de picking con artículo fijo que están en su mínimo o por debajo: cuánto traer para llegar al máximo
    /// (o al mínimo si no tiene máximo) y desde dónde (la primera de almacenamiento con stock libre por código, si no lo
    /// que no tiene ubicación). Lo ya tomado para pedidos no cuenta como libre.
    /// </summary>
    public async Task<IReadOnlyList<ReplenishmentRow>> GetReplenishmentsAsync(Guid? siteId = null, CancellationToken ct = default)
    {
        await using var c = await Db.OpenAsync(ct);
        var targets = await c.QueryAsync($"""
            SELECT l.id, l.site_id, l.code, l.fixed_item_id, l.min_qty, l.max_qty, i.sku, i.name, i.unit,
                   COALESCE((SELECT SUM(s.quantity) FROM location_stock s WHERE s.location_id = l.id AND s.item_id = l.fixed_item_id), 0) AS current
            FROM locations l JOIN items i ON i.id = l.fixed_item_id
            WHERE l.kind = 'picking' AND l.is_active = 1 AND l.min_qty IS NOT NULL AND ($site IS NULL OR l.site_id = $site) {MySites("l.site_id")}
            ORDER BY l.code
            """, r => (Id: r.Guid("id"), Site: r.Guid("site_id"), Code: r.Str("code"), Item: r.Guid("fixed_item_id"), Min: r.Dec("min_qty"),
                Max: r.DecOrNull("max_qty"), Sku: r.Str("sku"), Name: r.Str("name"), Unit: r.Str("unit"), Current: r.Dec("current")),
            null, ("$site", siteId));
        var rows = new List<ReplenishmentRow>();
        foreach (var t in targets.Where(t => t.Current <= t.Min))
        {
            var target = Math.Max(t.Max ?? t.Min, t.Min);
            var need = target - t.Current;
            if (need <= 0)
                continue;
            var source = await c.FirstOrDefaultAsync("""
                SELECT l.id, l.code, s.quantity - COALESCE((SELECT SUM(p.quantity) FROM order_picks p
                                                          WHERE p.location_id = l.id AND p.item_id = s.item_id AND p.shipment_id IS NULL), 0) AS free
                FROM location_stock s JOIN locations l ON l.id = s.location_id
                WHERE s.site_id = $site AND s.item_id = $item AND l.kind = 'storage' AND l.is_active = 1
                  AND s.quantity - COALESCE((SELECT SUM(p.quantity) FROM order_picks p
                                             WHERE p.location_id = l.id AND p.item_id = s.item_id AND p.shipment_id IS NULL), 0) > 0
                ORDER BY l.code LIMIT 1
                """, r => new { Id = (Guid?)r.Guid("id"), Code = (string?)r.Str("code"), Free = r.Dec("free") }, null, ("$site", t.Site), ("$item", t.Item));
            if (source is null)
            {
                var loose = await UnassignedAsync(c, null, t.Site, t.Item) - await c.ScalarAsync<decimal>("""
                    SELECT COALESCE(SUM(p.quantity), 0) FROM order_picks p JOIN orders o ON o.id = p.order_id
                    WHERE p.location_id IS NULL AND p.item_id = $item AND p.shipment_id IS NULL AND o.site_id = $site
                    """, null, ("$site", t.Site), ("$item", t.Item));
                source = new { Id = (Guid?)null, Code = (string?)null, Free = Math.Max(0, loose) };
            }
            rows.Add(new ReplenishmentRow(t.Site, t.Id, t.Code, t.Item, t.Sku, t.Name, t.Unit, t.Current, t.Min, target,
                Math.Min(need, source.Free), source.Id, source.Code, source.Free));
        }
        return rows;
    }

    /// <summary>
    /// Trae a la ubicación de picking su artículo fijo desde <paramref name="fromLocationId"/> (o desde lo que no tiene
    /// ubicación) y da por hechas las tareas de reabastecer pendientes de esa ubicación.
    /// </summary>
    public Task ReplenishAsync(Guid locationId, Guid? fromLocationId, decimal quantity, CancellationToken ct = default)
    {
        RequirePermission(WmsPermissions.Relocate);
        Require(quantity > 0, T("error.la_cantidad_debe_ser_mayor"));
        return InTransactionAsync(async (c, tx, now) =>
        {
            var target = await c.FirstOrDefaultAsync("SELECT site_id, code, fixed_item_id FROM locations WHERE id = $id",
                r => new { Site = r.Guid("site_id"), Code = r.Str("code"), Item = r.GuidOrNull("fixed_item_id") }, tx, ("$id", locationId));
            Require(target is not null, T("error.la_ubicacion_no_existe"));
            Require(target!.Item is not null, T("error.la_ubicacion_no_tiene_articulo_fijo", target.Code));
            Require(fromLocationId != locationId, T("error.elige_otra_ubicacion"));
            await RelocateCoreAsync(c, tx, now, target.Site, target.Item!.Value, fromLocationId, locationId, quantity, T("tarea.reabastecer_ref"));
            await TaskService.CloseOpenAsync(c, tx, now, Context.UserName, TaskKinds.Replenish, locationId: locationId);
            return 0;
        }, ct);
    }

    /// <summary>
    /// Ubicación sugerida para guardar un artículo: la fija del artículo; si no, donde ya hay de ese artículo (la de más
    /// stock); si no, la primera de almacenamiento vacía de la sede.
    /// </summary>
    public async Task<(Guid Id, string Code, string Reason)?> SuggestAsync(Guid siteId, Guid itemId, CancellationToken ct = default)
    {
        await using var c = await Db.OpenAsync(ct);
        return await SuggestAsync(c, null, siteId, itemId);
    }

    // ---------- Internos y réplica ----------

    internal static async Task<(Guid Id, string Code, string Reason)?> SuggestAsync(SqliteConnection c, SqliteTransaction? tx, Guid siteId, Guid itemId)
    {
        static (Guid, string) Read(SqliteDataReader r) => (r.Guid("id"), r.Str("code"));
        var fixedLoc = await First(c.QueryAsync("""
            SELECT id, code FROM locations WHERE site_id = $site AND fixed_item_id = $item AND is_active = 1 ORDER BY code LIMIT 1
            """, Read, tx, ("$site", siteId), ("$item", itemId)));
        if (fixedLoc is { } f)
            return (f.Item1, f.Item2, PutawayReasons.Fixed);
        var same = await First(c.QueryAsync("""
            SELECT l.id, l.code FROM location_stock s JOIN locations l ON l.id = s.location_id
            WHERE s.site_id = $site AND s.item_id = $item AND s.quantity > 0 AND l.is_active = 1 AND l.kind IN ('storage', 'picking')
            ORDER BY s.quantity DESC, l.code LIMIT 1
            """, Read, tx, ("$site", siteId), ("$item", itemId)));
        if (same is { } s)
            return (s.Item1, s.Item2, PutawayReasons.SameItem);
        var empty = await First(c.QueryAsync("""
            SELECT l.id, l.code FROM locations l
            WHERE l.site_id = $site AND l.is_active = 1 AND l.kind = 'storage' AND l.fixed_item_id IS NULL
              AND NOT EXISTS (SELECT 1 FROM location_stock s WHERE s.location_id = l.id AND s.quantity > 0)
            ORDER BY l.code LIMIT 1
            """, Read, tx, ("$site", siteId)));
        return empty is { } e ? (e.Item1, e.Item2, PutawayReasons.Empty) : null;
    }

    /// <summary>Aplica un traslado (propio o de otro equipo) una sola vez, sin pasar del stock que hay.</summary>
    internal static async Task<bool> ApplyAsync(SqliteConnection c, SqliteTransaction tx, Guid id, DateTimeOffset at, StockRelocated e, string? userName)
    {
        if (await c.ScalarAsync<long>("SELECT COUNT(*) FROM relocations WHERE id = $id", tx, ("$id", id)) > 0)
            return false;
        // En otro equipo el saldo puede haber cambiado: se mueve lo que haya, como mucho lo pedido.
        var available = e.FromLocationId is { } from
            ? await QuantityAsync(c, tx, e.SiteId, from, e.ItemId)
            : await UnassignedAsync(c, tx, e.SiteId, e.ItemId);
        var qty = Math.Max(0, Math.Min(e.Quantity, available));
        if (e.FromLocationId is { } source)
            await AddAsync(c, tx, e.SiteId, source, e.ItemId, -qty);
        if (e.ToLocationId is { } target)
            await AddAsync(c, tx, e.SiteId, target, e.ItemId, qty);
        await c.ExecAsync("""
            INSERT INTO relocations (id, occurred_at, site_id, item_id, from_location_id, to_location_id, quantity, user_name, reference)
            VALUES ($id, $at, $site, $item, $from, $to, $qty, $user, $ref)
            """, tx, ("$id", id), ("$at", at), ("$site", e.SiteId), ("$item", e.ItemId), ("$from", e.FromLocationId), ("$to", e.ToLocationId),
            ("$qty", qty), ("$user", userName), ("$ref", e.Reference));
        return true;
    }

    /// <summary>
    /// Después de que baja el stock de la sede (despacho, venta, ajuste): si las ubicaciones suman más de lo que hay, se
    /// descuenta lo que sobra en un orden fijo (despacho, picking, almacenamiento, recepción, cuarentena; por código).
    /// </summary>
    internal static async Task TrimAsync(SqliteConnection c, SqliteTransaction tx, Guid siteId, Guid itemId)
    {
        var onHand = await StockLedger.OnHandAsync(c, tx, siteId, itemId);
        var held = await c.QueryAsync("""
            SELECT s.location_id, s.quantity, l.kind, l.code FROM location_stock s JOIN locations l ON l.id = s.location_id
            WHERE s.site_id = $site AND s.item_id = $item AND s.quantity > 0
            """, r => (Id: r.Guid("location_id"), Qty: r.Dec("quantity"), Kind: r.Str("kind"), Code: r.Str("code")), tx,
            ("$site", siteId), ("$item", itemId));
        var excess = held.Sum(h => h.Qty) - Math.Max(0, onHand);
        foreach (var h in held.OrderBy(h => LocationKinds.ConsumeOrder(h.Kind)).ThenBy(h => h.Code, StringComparer.Ordinal))
        {
            if (excess <= 0)
                break;
            var take = Math.Min(h.Qty, excess);
            await AddAsync(c, tx, siteId, h.Id, itemId, -take);
            excess -= take;
        }
    }

    internal static async Task<bool> ApplyLocationAsync(SqliteConnection c, SqliteTransaction tx, LocationData d, DateTimeOffset updatedAt)
    {
        var current = await c.ScalarAsync<string?>("SELECT updated_at FROM locations WHERE id = $id", tx, ("$id", d.Id));
        if (current is not null && string.CompareOrdinal(current, SqliteExtensions.Time(updatedAt)) >= 0)
            return false;
        // Si otro equipo creó el mismo código en la sede, gana el más nuevo y el viejo queda inactivo con su código marcado.
        await c.ExecAsync("""
            UPDATE locations SET is_active = 0, code = code || '~' || substr(id, 1, 4)
            WHERE site_id = $site AND code = $code AND id <> $id
            """, tx, ("$site", d.SiteId), ("$code", d.Code), ("$id", d.Id));
        await c.ExecAsync("""
            INSERT INTO locations (id, site_id, code, kind, zone, aisle, rack, level, position, fixed_item_id, is_active, updated_at, min_qty, max_qty)
            VALUES ($id, $site, $code, $kind, $zone, $aisle, $rack, $level, $pos, $fixed, $active, $at, $min, $max)
            ON CONFLICT (id) DO UPDATE SET site_id = $site, code = $code, kind = $kind, zone = $zone, aisle = $aisle, rack = $rack,
                level = $level, position = $pos, fixed_item_id = $fixed, is_active = $active, updated_at = $at, min_qty = $min, max_qty = $max
            """, tx, ("$id", d.Id), ("$site", d.SiteId), ("$code", d.Code), ("$kind", d.Kind), ("$zone", d.Zone), ("$aisle", d.Aisle),
            ("$rack", d.Rack), ("$level", d.Level), ("$pos", d.Position), ("$fixed", d.FixedItemId), ("$active", d.IsActive), ("$at", updatedAt),
            ("$min", d.MinQuantity), ("$max", d.MaxQuantity));
        return true;
    }

    private static async Task PublishAsync(SqliteConnection c, SqliteTransaction tx, DateTimeOffset now, LocationData data)
    {
        await ApplyLocationAsync(c, tx, data, now);
        await Outbox.EnqueueEventAsync(c, tx, now, SuiteEvents.RecordUpserted,
            new WmsRecord(WmsRecordKinds.Location, data.Id.ToString(), now, JsonSerializer.SerializeToElement(data, VortexJson.Options)),
            subjectType: "location", subjectId: data.Code);
    }

    private static Task AddAsync(SqliteConnection c, SqliteTransaction tx, Guid siteId, Guid locationId, Guid itemId, decimal delta) =>
        c.ExecAsync("""
            INSERT INTO location_stock (site_id, location_id, item_id, quantity) VALUES ($site, $loc, $item, $qty)
            ON CONFLICT (site_id, location_id, item_id) DO UPDATE SET quantity = quantity + $qty
            """, tx, ("$site", siteId), ("$loc", locationId), ("$item", itemId), ("$qty", delta));

    private static Task<decimal> QuantityAsync(SqliteConnection c, SqliteTransaction? tx, Guid siteId, Guid locationId, Guid itemId) =>
        c.ScalarAsync<decimal>("SELECT COALESCE(SUM(quantity), 0) FROM location_stock WHERE site_id = $site AND location_id = $loc AND item_id = $item",
            tx, ("$site", siteId), ("$loc", locationId), ("$item", itemId));

    /// <summary>Lo que hay en la sede sin ubicación: el stock menos lo que está en ubicaciones.</summary>
    internal static async Task<decimal> UnassignedAsync(SqliteConnection c, SqliteTransaction? tx, Guid siteId, Guid itemId) =>
        Math.Max(0, await StockLedger.OnHandAsync(c, tx, siteId, itemId) - await c.ScalarAsync<decimal>(
            "SELECT COALESCE(SUM(quantity), 0) FROM location_stock WHERE site_id = $site AND item_id = $item", tx, ("$site", siteId), ("$item", itemId)));

    private static Task<decimal> UnitsAsync(SqliteConnection c, SqliteTransaction tx, Guid locationId) =>
        c.ScalarAsync<decimal>("SELECT COALESCE(SUM(quantity), 0) FROM location_stock WHERE location_id = $loc AND quantity > 0", tx, ("$loc", locationId));

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static async Task<T?> First<T>(Task<List<T>> query) where T : struct => await query is { Count: > 0 } list ? list[0] : null;

    private static string NormalizeCode(string? code) => (code ?? "").Trim().ToUpperInvariant().Replace(' ', '-');

    private static LocationRow ReadLocation(SqliteDataReader r) => new(
        r.Guid("id"), r.Guid("site_id"), r.Str("code"), r.Str("kind"), r.StrOrNull("zone"), r.StrOrNull("aisle"), r.StrOrNull("rack"),
        r.StrOrNull("level"), r.StrOrNull("position"), r.GuidOrNull("fixed_item_id"), r.StrOrNull("fixed_item_name"), r.Bool("is_active"),
        r.Dec("units"), r.Int("items"), r.DecOrNull("min_qty"), r.DecOrNull("max_qty"));

    private static LocationStockRow ReadStock(SqliteDataReader r) => new(
        r.Guid("location_id"), r.Str("code"), r.Guid("item_id"), r.Str("sku"), r.Str("name"), r.Str("unit"), r.Dec("quantity"));
}
