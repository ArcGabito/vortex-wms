using Microsoft.Data.Sqlite;
using VortexWms.Core.Data;
using VortexWms.Core.Domain;
using VortexWms.Core.Integration;
using VortexWms.Core.Sync;

using static VortexWms.Core.Texts.CoreText;

namespace VortexWms.Core.Services;

/// <summary>
/// Pedidos a despachar: llegan como <c>order.created</c> desde el ERP o el CRM, o se crean en el WMS cuando funciona
/// solo. Al crearse reservan stock; al despacharse lo descuentan y publican <c>shipment.dispatched</c>.
/// </summary>
public sealed class OrderService(WmsDatabase db, IWmsContext context, TimeProvider clock) : WmsService(db, context, clock)
{
    public async Task<PagedResult<OrderRow>> GetOrdersAsync(string? search, string? status, int page, int pageSize,
        CancellationToken ct = default)
    {
        await using var c = await Db.OpenAsync(ct);
        const string where = """
            WHERE ($status IS NULL OR o.status = $status OR ($status = 'open' AND o.status IN ('pending', 'picking', 'partial')))
              AND ($search IS NULL OR o.number LIKE $like OR p.name LIKE $like OR o.tracking_number LIKE $like)
            """;
        const string from = "FROM orders o LEFT JOIN parties p ON p.id = o.customer_id LEFT JOIN sites t ON t.id = o.site_id";
        var args = new (string, object?)[]
        {
            ("$status", string.IsNullOrWhiteSpace(status) ? null : status), ("$search", string.IsNullOrWhiteSpace(search) ? null : search.Trim()),
            ("$like", Like(search)), ("$limit", pageSize), ("$offset", (page - 1) * pageSize),
        };
        var total = await c.ScalarAsync<int>($"SELECT COUNT(*) {from} {where}{MySites("o.site_id")}", null, args);
        var rows = await c.QueryAsync($"""
            SELECT o.*, p.name AS customer_name, t.name AS site_name,
                   (SELECT COUNT(*) FROM order_lines l WHERE l.order_id = o.id) AS line_count,
                   (SELECT COALESCE(SUM(quantity), 0) FROM order_lines l WHERE l.order_id = o.id) AS units,
                   (SELECT COALESCE(SUM(shipped), 0) FROM order_lines l WHERE l.order_id = o.id) AS shipped
            {from} {where}{MySites("o.site_id")}
            ORDER BY CASE o.status WHEN 'picking' THEN 0 WHEN 'partial' THEN 1 WHEN 'pending' THEN 2 ELSE 3 END, o.created_at DESC
            LIMIT $limit OFFSET $offset
            """, r => new OrderRow(r.Guid("id"), r.Str("number"), r.Str("source"), r.GuidOrNull("customer_id"),
                r.StrOrNull("customer_name"), r.Guid("site_id"), r.StrOrNull("site_name"), r.Str("status"), r.Dec("total"),
                r.Str("currency_code"), r.Time("created_at"), r.TimeOrNull("dispatched_at"), r.StrOrNull("carrier"),
                r.StrOrNull("tracking_number"), r.Int("line_count"), r.Dec("units"), r.StrOrNull("approval"), r.Dec("shipped")), null, args);
        return new PagedResult<OrderRow>(rows, total, page, pageSize);
    }

    public async Task<OrderRow?> GetAsync(Guid id, CancellationToken ct = default)
    {
        await using var c = await Db.OpenAsync(ct);
        return await GetAsync(c, null, id);
    }

    private static Task<OrderRow?> GetAsync(SqliteConnection c, SqliteTransaction? tx, Guid id) =>
        c.FirstOrDefaultAsync("""
            SELECT o.*, p.name AS customer_name, t.name AS site_name,
                   (SELECT COUNT(*) FROM order_lines l WHERE l.order_id = o.id) AS line_count,
                   (SELECT COALESCE(SUM(quantity), 0) FROM order_lines l WHERE l.order_id = o.id) AS units,
                   (SELECT COALESCE(SUM(shipped), 0) FROM order_lines l WHERE l.order_id = o.id) AS shipped
            FROM orders o LEFT JOIN parties p ON p.id = o.customer_id LEFT JOIN sites t ON t.id = o.site_id
            WHERE o.id = $id
            """, r => new OrderRow(r.Guid("id"), r.Str("number"), r.Str("source"), r.GuidOrNull("customer_id"),
                r.StrOrNull("customer_name"), r.Guid("site_id"), r.StrOrNull("site_name"), r.Str("status"), r.Dec("total"),
                r.Str("currency_code"), r.Time("created_at"), r.TimeOrNull("dispatched_at"), r.StrOrNull("carrier"),
                r.StrOrNull("tracking_number"), r.Int("line_count"), r.Dec("units"), r.StrOrNull("approval"), r.Dec("shipped")), tx, ("$id", id));

    public async Task<IReadOnlyList<OrderLineRow>> GetLinesAsync(Guid orderId, CancellationToken ct = default)
    {
        await using var c = await Db.OpenAsync(ct);
        return await c.QueryAsync("""
            SELECT l.*, COALESCE(i.sku, '?') AS sku, COALESCE(i.name, 'Artículo desconocido') AS item_name,
                   COALESCE(s.on_hand, 0) AS on_hand
            FROM order_lines l
            JOIN orders o ON o.id = l.order_id
            LEFT JOIN items i ON i.id = l.item_id
            LEFT JOIN stock s ON s.item_id = l.item_id AND s.site_id = o.site_id
            WHERE l.order_id = $id ORDER BY l.line_no
            """, r => new OrderLineRow(r.Int("line_no"), r.Guid("item_id"), r.Str("sku"), r.Str("item_name"), r.Dec("quantity"),
                r.Dec("unit_price"), r.Dec("on_hand"), r.Dec("shipped")), null, ("$id", orderId));
    }

    /// <summary>Pedido creado en el WMS (sin ERP ni CRM). Publica <c>order.created</c> igual que lo harían ellos.</summary>
    public Task<string> CreateAsync(Guid customerId, Guid siteId, IReadOnlyList<DocumentLine> lines, string currencyCode,
        CancellationToken ct = default)
    {
        RequirePermission(WmsPermissions.CreateOrder);
        Require(customerId != Guid.Empty, T("error.elige_el_cliente"));
        Require(lines.Count > 0, T("error.agrega_al_menos_un_articulo"));
        Require(lines.All(l => l.Quantity > 0), T("error.las_cantidades_deben_ser_mayores"));
        var merged = InventoryService.Merge(lines);

        return InTransactionAsync(async (c, tx, now) =>
        {
            await InventoryService.RequireSiteAsync(c, tx, siteId);
            await RequireSiteAccessAsync(c, tx, siteId);
            var orderId = Guid.NewGuid();
            var number = await DocumentNumbers.NextAsync(c, tx, DocumentNumbers.Order, Context.DeviceTag);
            var orderLines = merged.Select(l => new OrderLine(l.ItemId, l.Quantity, l.UnitPrice ?? 0)).ToList();
            await InsertAsync(c, tx, now, orderId, number, "WMS", customerId, siteId, orderLines, currencyCode);
            await Outbox.EnqueueEventAsync(c, tx, now, SuiteEvents.OrderCreated,
                new OrderCreated(orderId, number, customerId, siteId, orderLines, orderLines.Sum(l => l.Quantity * l.UnitPrice), currencyCode),
                subjectType: "order", subjectId: number, id: orderId);
            return number;
        }, ct);
    }

    /// <summary>Aplica <c>order.created</c> de otro sistema. Idempotente por <c>orderId</c>.</summary>
    internal async Task ApplyOrderCreatedAsync(SqliteConnection c, SqliteTransaction tx, DateTimeOffset now, string source, OrderCreated e)
    {
        if (e.OrderId == Guid.Empty || await c.ScalarAsync<long>("SELECT COUNT(*) FROM orders WHERE id = $id", tx, ("$id", e.OrderId)) > 0)
            return;
        var siteId = e.SiteId ?? await DefaultSiteAsync(c, tx) ?? Guid.Empty;
        var number = string.IsNullOrWhiteSpace(e.Number) ? $"{source}-{e.OrderId.ToString()[..8].ToUpperInvariant()}" : e.Number;
        await InsertAsync(c, tx, now, e.OrderId, number, source, e.CustomerId, siteId, e.Lines ?? [], e.CurrencyCode ?? "PEN",
            e.ApprovalStatus == OrderApprovals.Pending ? OrderApprovals.Pending : null);
    }

    /// <summary><c>order.approved</c>: el pedido que esperaba aprobación de crédito reserva su stock y ya se puede preparar.</summary>
    internal static async Task ApplyApprovedAsync(SqliteConnection c, SqliteTransaction tx, DateTimeOffset now, OrderApproved e)
    {
        var order = await c.FirstOrDefaultAsync("SELECT site_id, approval, status FROM orders WHERE id = $id",
            r => new { Site = r.Guid("site_id"), Approval = r.StrOrNull("approval"), Status = r.Str("status") }, tx, ("$id", e.OrderId));
        if (order is null || order.Approval != OrderApprovals.Pending)
            return;
        await c.ExecAsync("UPDATE orders SET approval = 'approved' WHERE id = $id", tx, ("$id", e.OrderId));
        if (order.Site != Guid.Empty && OrderStatuses.IsOpen(order.Status))
            foreach (var line in (await LinesAsync(c, tx, e.OrderId)).Where(l => l.Tracked))
                await StockLedger.ReserveAsync(c, tx, now, order.Site, line.ItemId, line.Quantity - line.Shipped);
    }

    /// <summary>
    /// <c>shipment.dispatched</c> de otro equipo del WMS: cierra el pedido y libera su reserva. El stock no se toca
    /// aquí: llega por los <c>stock.adjusted</c> de ese equipo.
    /// </summary>
    internal async Task ApplyDispatchedElsewhereAsync(SqliteConnection c, SqliteTransaction tx, DateTimeOffset now,
        DateTimeOffset occurredAt, ShipmentDispatched d)
    {
        var order = await c.FirstOrDefaultAsync("SELECT status, site_id, number, shipment_id, carrier, tracking_number, approval FROM orders WHERE id = $id",
            r => new { Status = r.Str("status"), SiteId = r.Guid("site_id"), Number = r.Str("number"), ShipmentSite = r.Guid("site_id"),
                Carrier = r.StrOrNull("carrier"), Tracking = r.StrOrNull("tracking_number"), Approval = r.StrOrNull("approval") }, tx, ("$id", d.OrderId));
        if (order is null)
            return;
        if (order.Status == OrderStatuses.Dispatched)
        {
            // También se despachó aquí: queda la versión del hub.
            if (order.ShipmentSite != d.SiteId || order.Carrier != d.Carrier || order.Tracking != d.TrackingNumber)
            {
                await c.ExecAsync("UPDATE orders SET site_id = $site, carrier = $carrier, tracking_number = $tracking WHERE id = $id", tx,
                    ("$id", d.OrderId), ("$site", d.SiteId == Guid.Empty ? order.SiteId : d.SiteId), ("$carrier", d.Carrier),
                    ("$tracking", d.TrackingNumber));
                await SyncConflicts.RecordAsync(c, tx, now, SyncConflictKinds.DispatchedTwice, d.OrderId, order.Number, d.Number);
            }
            return;
        }
        // El eco de un despacho parcial propio (o uno ya aplicado) no cambia nada.
        if (await c.ScalarAsync<long>("SELECT COUNT(*) FROM order_shipments WHERE id = $id", tx, ("$id", d.ShipmentId)) > 0)
            return;
        if (order.Status == OrderStatuses.Cancelled)
        {
            // Se canceló aquí pero otro equipo ya lo despachó: la mercadería salió. La reserva ya se liberó al cancelar.
            await SyncConflicts.RecordAsync(c, tx, now, SyncConflictKinds.DispatchedAfterCancel, d.OrderId, order.Number, d.Number);
        }

        // Lo despachado allá: por línea si viene (despacho parcial), si no, todo lo que faltaba.
        var lines = await LinesAsync(c, tx, d.OrderId);
        var shipped = d.Partial == true && d.Lines is { Count: > 0 }
            ? d.Lines.GroupBy(l => l.ItemId).ToDictionary(g => g.Key, g => g.Sum(l => l.Quantity))
            : lines.GroupBy(l => l.ItemId).ToDictionary(g => g.Key, g => g.Sum(l => l.Quantity - l.Shipped));
        foreach (var (itemId, quantity) in shipped)
        {
            if (order.Status != OrderStatuses.Cancelled && order.SiteId != Guid.Empty && order.Approval != OrderApprovals.Pending &&
                lines.Any(l => l.ItemId == itemId && l.Tracked))
                await StockLedger.ReserveAsync(c, tx, now, order.SiteId, itemId, -quantity);
            await MarkShippedAsync(c, tx, d.OrderId, itemId, quantity);
        }
        await c.ExecAsync("""
            INSERT OR IGNORE INTO order_shipments (id, order_id, number, dispatched_at, partial) VALUES ($id, $order, $number, $at, $partial)
            """, tx, ("$id", d.ShipmentId), ("$order", d.OrderId), ("$number", d.Number), ("$at", occurredAt), ("$partial", d.Partial == true));
        var status = order.Status == OrderStatuses.Cancelled ? OrderStatuses.Dispatched
            : d.Partial == true ? OrderStatuses.Partial : OrderStatuses.Dispatched;
        await c.ExecAsync("""
            UPDATE orders SET status = $status, dispatched_at = $at, shipment_id = $shipment, carrier = $carrier,
                              tracking_number = $tracking, site_id = $site
            WHERE id = $id
            """, tx, ("$id", d.OrderId), ("$status", status), ("$at", occurredAt), ("$shipment", d.ShipmentId), ("$carrier", d.Carrier),
            ("$tracking", d.TrackingNumber), ("$site", d.SiteId == Guid.Empty ? order.SiteId : d.SiteId));
    }

    public Task StartPickingAsync(Guid orderId, CancellationToken ct = default)
    {
        RequirePermission(WmsPermissions.Pick);
        return InTransactionAsync(async (c, tx, now) =>
        {
            var status = await StatusAsync(c, tx, orderId);
            Require(status == OrderStatuses.Pending, T("error.solo_se_puede_preparar_un"));
            await RequireApprovedAsync(c, tx, orderId);
            await RequireOrderSiteAsync(c, tx, orderId);
            await TaskService.RequireMyPickAsync(c, tx, Context, orderId);
            var number = await c.ScalarAsync<string?>("SELECT number FROM orders WHERE id = $id", tx, ("$id", orderId));
            await Outbox.EnqueueEventAsync(c, tx, now, SuiteEvents.OrderPickingStarted, new OrderPickingStarted(orderId, number),
                subjectType: "order", subjectId: number, id: DeterministicId.From("order_picking", orderId));
            return await c.ExecAsync("UPDATE orders SET status = 'picking' WHERE id = $id", tx, ("$id", orderId));
        }, ct);
    }

    // ---------- Picking guiado ----------

    /// <summary>
    /// Recorrido para preparar el pedido: de qué ubicación tomar cuánto, en el orden del código de ubicación (pasillo por
    /// pasillo) y lo que no tiene ubicación al final. Primero las de picking, después almacenamiento, despacho y
    /// recepción; la cuarentena no se usa. También lo ya tomado y lo que falta en la sede.
    /// </summary>
    public async Task<PickPlan?> GetPickListAsync(Guid orderId, CancellationToken ct = default)
    {
        await using var c = await Db.OpenAsync(ct);
        if (await GetAsync(c, null, orderId) is not { } order)
            return null;
        var picked = await PicksAsync(c, null, orderId);
        var stops = new List<PickStop>();
        var needs = new List<PickNeed>();
        foreach (var item in (await LinesAsync(c, null, orderId)).Where(l => l.Tracked).GroupBy(l => l.ItemId))
        {
            var info = await c.FirstOrDefaultAsync("SELECT sku, name, unit, barcode FROM items WHERE id = $id",
                r => new { Sku = r.Str("sku"), Name = r.Str("name"), Unit = r.Str("unit"), Barcode = r.StrOrNull("barcode") }, null, ("$id", item.Key))
                ?? new { Sku = "?", Name = "?", Unit = "", Barcode = (string?)null };
            var remaining = item.Sum(l => l.Quantity - l.Shipped);
            var already = picked.Where(p => p.ItemId == item.Key).Sum(p => p.Quantity);
            var left = Math.Max(0, remaining - already);
            foreach (var spot in await AvailableSpotsAsync(c, null, order.SiteId, item.Key, orderId))
            {
                if (left <= 0)
                    break;
                var take = Math.Min(left, spot.Quantity);
                stops.Add(new PickStop(item.Key, info.Sku, info.Name, info.Unit, info.Barcode, spot.LocationId, spot.Code, take));
                left -= take;
            }
            needs.Add(new PickNeed(item.Key, info.Sku, info.Name, remaining, already, left));
        }
        var ordered = stops.OrderBy(s => s.LocationCode is null).ThenBy(s => s.LocationCode, StringComparer.Ordinal).ThenBy(s => s.ItemName).ToList();
        return new PickPlan(order, ordered, picked, needs);
    }

    /// <summary>
    /// Confirma lo tomado de una ubicación (escaneada) o de lo que no tiene ubicación. Si el pedido estaba pendiente pasa a
    /// preparación. No se toma más de lo que falta ni más de lo que hay en la ubicación.
    /// </summary>
    public Task ConfirmPickAsync(Guid orderId, Guid itemId, Guid? locationId, decimal quantity, CancellationToken ct = default)
    {
        RequirePermission(WmsPermissions.Pick);
        Require(quantity > 0, T("error.la_cantidad_debe_ser_mayor"));
        return InTransactionAsync(async (c, tx, now) =>
        {
            await ConfirmPickCoreAsync(c, tx, now, orderId, itemId, locationId, quantity);
            return 0;
        }, ct);
    }

    private async Task ConfirmPickCoreAsync(SqliteConnection c, SqliteTransaction tx, DateTimeOffset now, Guid orderId, Guid itemId,
        Guid? locationId, decimal quantity)
    {
        {
            var order = await GetAsync(c, tx, orderId);
            Require(order is not null, T("error.el_pedido_no_existe"));
            Require(OrderStatuses.IsOpen(order!.Status), T("error.el_pedido_ya_fue_despachado"));
            await RequireApprovedAsync(c, tx, orderId);
            await RequireSiteAccessAsync(c, tx, order.SiteId);
            await TaskService.RequireMyPickAsync(c, tx, Context, orderId);

            var lines = (await LinesAsync(c, tx, orderId)).Where(l => l.ItemId == itemId).ToList();
            Require(lines.Count > 0, T("error.el_articulo_no_esta_en_el_pedido"));
            var already = (await PicksAsync(c, tx, orderId)).Where(p => p.ItemId == itemId).Sum(p => p.Quantity);
            var left = lines.Sum(l => l.Quantity - l.Shipped) - already;
            Require(quantity <= left, T("error.solo_faltan", left));
            var spot = (await AvailableSpotsAsync(c, tx, order.SiteId, itemId, orderId)).FirstOrDefault(s => s.LocationId == locationId);
            Require(spot is not null && spot.Quantity >= quantity, T("error.solo_hay_en_la_ubicacion", spot?.Quantity ?? 0));

            await c.ExecAsync("""
                INSERT INTO order_picks (id, order_id, item_id, location_id, quantity, picked_at, user_name)
                VALUES ($id, $order, $item, $loc, $qty, $at, $user)
                """, tx, ("$id", Guid.NewGuid()), ("$order", orderId), ("$item", itemId), ("$loc", locationId), ("$qty", quantity),
                ("$at", now), ("$user", Context.UserName));
            if (order.Status == OrderStatuses.Pending)
            {
                await Outbox.EnqueueEventAsync(c, tx, now, SuiteEvents.OrderPickingStarted, new OrderPickingStarted(orderId, order.Number),
                    subjectType: "order", subjectId: order.Number, id: DeterministicId.From("order_picking", orderId));
                await c.ExecAsync("UPDATE orders SET status = 'picking' WHERE id = $id", tx, ("$id", orderId));
            }
        }
    }

    /// <summary>Deshace una toma que aún no se despachó (si lo que queda tomado alcanza para lo ya empacado).</summary>
    public Task UndoPickAsync(Guid pickId, CancellationToken ct = default)
    {
        RequirePermission(WmsPermissions.Pick);
        return InTransactionAsync(async (c, tx, now) =>
        {
            var pick = await c.FirstOrDefaultAsync("SELECT order_id, item_id, quantity FROM order_picks WHERE id = $id AND shipment_id IS NULL",
                r => new { Order = r.Guid("order_id"), Item = r.Guid("item_id"), Qty = r.Dec("quantity") }, tx, ("$id", pickId));
            if (pick is null)
                return 0;
            var picked = (await PicksAsync(c, tx, pick.Order)).Where(p => p.ItemId == pick.Item).Sum(p => p.Quantity);
            var packed = (await PackagesAsync(c, tx, pick.Order)).SelectMany(p => p.Lines).Where(l => l.ItemId == pick.Item).Sum(l => l.Quantity);
            Require(picked - pick.Qty >= packed, T("error.ya_esta_empacado"));
            return await c.ExecAsync("DELETE FROM order_picks WHERE id = $id AND shipment_id IS NULL", tx, ("$id", pickId));
        }, ct);
    }

    // ---------- Picking por grupo (fase 4b2) ----------

    /// <summary>
    /// Un solo recorrido para varios pedidos: cada parada dice cuánto tomar de esa ubicación y para qué pedido. Los pedidos
    /// se atienden en el orden dado (si no alcanza el stock, se completa primero el primero).
    /// </summary>
    public async Task<BatchPickPlan> GetBatchPickAsync(IReadOnlyList<Guid> orderIds, CancellationToken ct = default)
    {
        await using var c = await Db.OpenAsync(ct);
        var orders = new List<OrderRow>();
        foreach (var id in orderIds.Distinct())
            if (await GetAsync(c, null, id) is { } o && OrderStatuses.IsOpen(o.Status))
                orders.Add(o);
        var used = new Dictionary<(Guid Item, Guid? Location), decimal>();
        var stops = new Dictionary<(Guid Item, Guid? Location), (PickStop Stop, List<BatchSplit> Splits)>();
        var needs = new List<PickNeed>();
        var items = new Dictionary<Guid, (string Sku, string Name, string Unit, string? Barcode)>();
        foreach (var order in orders.Where(o => !o.PendingApproval))
        {
            var picked = await PicksAsync(c, null, order.Id);
            foreach (var item in (await LinesAsync(c, null, order.Id)).Where(l => l.Tracked).GroupBy(l => l.ItemId))
            {
                if (!items.TryGetValue(item.Key, out var info))
                    items[item.Key] = info = (await c.QueryAsync("SELECT sku, name, unit, barcode FROM items WHERE id = $id",
                        r => (r.Str("sku"), r.Str("name"), r.Str("unit"), r.StrOrNull("barcode")), null, ("$id", item.Key)))
                        .DefaultIfEmpty(("?", "?", "", null)).First();
                var remaining = item.Sum(l => l.Quantity - l.Shipped);
                var already = picked.Where(p => p.ItemId == item.Key).Sum(p => p.Quantity);
                var left = Math.Max(0, remaining - already);
                foreach (var spot in await AvailableSpotsAsync(c, null, order.SiteId, item.Key, order.Id))
                {
                    if (left <= 0)
                        break;
                    var key = (item.Key, spot.LocationId);
                    var free = spot.Quantity - used.GetValueOrDefault(key);
                    if (free <= 0)
                        continue;
                    var take = Math.Min(left, free);
                    used[key] = used.GetValueOrDefault(key) + take;
                    if (!stops.TryGetValue(key, out var stop))
                        stop = (new PickStop(item.Key, info.Sku, info.Name, info.Unit, info.Barcode, spot.LocationId, spot.Code, 0), []);
                    stop.Splits.Add(new BatchSplit(order.Id, order.Number, take));
                    stops[key] = (stop.Stop with { ToPick = stop.Stop.ToPick + take }, stop.Splits);
                    left -= take;
                }
                needs.Add(new PickNeed(item.Key, info.Sku, info.Name, remaining, already, left));
            }
        }
        var merged = needs.GroupBy(n => n.ItemId).Select(g => new PickNeed(g.Key, g.First().Sku, g.First().ItemName, g.Sum(n => n.Remaining),
            g.Sum(n => n.Picked), g.Sum(n => n.Missing))).ToList();
        var ordered = stops.Values.Select(s => new BatchStop(s.Stop.ItemId, s.Stop.Sku, s.Stop.ItemName, s.Stop.Unit, s.Stop.Barcode,
                s.Stop.LocationId, s.Stop.LocationCode, s.Stop.ToPick, s.Splits))
            .OrderBy(s => s.LocationCode is null).ThenBy(s => s.LocationCode, StringComparer.Ordinal).ThenBy(s => s.ItemName).ToList();
        return new BatchPickPlan(orders, ordered, merged);
    }

    /// <summary>
    /// Confirma lo tomado de una ubicación para el grupo: se reparte entre los pedidos en su orden (a cada uno, lo que le
    /// falta), en una sola operación.
    /// </summary>
    public Task ConfirmBatchPickAsync(IReadOnlyList<Guid> orderIds, Guid itemId, Guid? locationId, decimal quantity, CancellationToken ct = default)
    {
        RequirePermission(WmsPermissions.Pick);
        Require(quantity > 0, T("error.la_cantidad_debe_ser_mayor"));
        return InTransactionAsync(async (c, tx, now) =>
        {
            var left = quantity;
            foreach (var orderId in orderIds.Distinct())
            {
                if (left <= 0)
                    break;
                var order = await GetAsync(c, tx, orderId);
                if (order is null || !OrderStatuses.IsOpen(order.Status) || order.PendingApproval)
                    continue;
                var lines = (await LinesAsync(c, tx, orderId)).Where(l => l.ItemId == itemId).ToList();
                var already = (await PicksAsync(c, tx, orderId)).Where(p => p.ItemId == itemId).Sum(p => p.Quantity);
                var missing = lines.Sum(l => l.Quantity - l.Shipped) - already;
                if (missing <= 0)
                    continue;
                var take = Math.Min(left, missing);
                await ConfirmPickCoreAsync(c, tx, now, orderId, itemId, locationId, take);
                left -= take;
            }
            Require(left < quantity, T("error.el_articulo_no_esta_en_el_pedido"));
            Require(left <= 0, T("error.solo_faltan", quantity - left));
            return 0;
        }, ct);
    }

    // ---------- Empaque (fase 4b2) ----------

    /// <summary>
    /// Lo que se puede empacar de cada artículo (lo tomado; si no hubo picking, lo que falta despachar), lo ya empacado y
    /// los bultos del pedido (los aún sin despachar y los de despachos anteriores).
    /// </summary>
    public async Task<PackingPlan> GetPackingAsync(Guid orderId, CancellationToken ct = default)
    {
        await using var c = await Db.OpenAsync(ct);
        return await PackingAsync(c, null, orderId);
    }

    private static async Task<PackingPlan> PackingAsync(SqliteConnection c, SqliteTransaction? tx, Guid orderId)
    {
        var picks = await PicksAsync(c, tx, orderId);
        var packages = await PackagesAsync(c, tx, orderId, includeShipped: true);
        var open = packages.Where(p => p.ShipmentId is null).ToList();
        var items = new List<PackItemRow>();
        foreach (var item in (await LinesAsync(c, tx, orderId)).Where(l => l.Tracked).GroupBy(l => l.ItemId))
        {
            var remaining = item.Sum(l => l.Quantity - l.Shipped);
            var packable = picks.Count > 0 ? picks.Where(p => p.ItemId == item.Key).Sum(p => p.Quantity) : remaining;
            if (packable <= 0)
                continue;
            var info = (await c.QueryAsync("SELECT sku, name, unit FROM items WHERE id = $id",
                r => (Sku: r.Str("sku"), Name: r.Str("name"), Unit: r.Str("unit")), tx, ("$id", item.Key))).DefaultIfEmpty((Sku: "?", Name: "?", Unit: "")).First();
            items.Add(new PackItemRow(item.Key, info.Sku, info.Name, info.Unit, packable,
                open.SelectMany(p => p.Lines).Where(l => l.ItemId == item.Key).Sum(l => l.Quantity)));
        }
        return new PackingPlan(items, packages);
    }

    /// <summary>Arma un bulto con su peso y contenido (no más de lo que queda por empacar de cada artículo). Devuelve su número.</summary>
    public Task<int> AddPackageAsync(Guid orderId, decimal? weightKg, IReadOnlyList<DocumentLine> lines, string? notes = null,
        CancellationToken ct = default)
    {
        RequirePermission(WmsPermissions.Pick);
        Require(weightKg is null or > 0, T("error.el_peso_debe_ser_mayor"));
        var content = lines.Where(l => l.Quantity > 0).GroupBy(l => l.ItemId).Select(g => (ItemId: g.Key, Quantity: g.Sum(l => l.Quantity))).ToList();
        Require(content.Count > 0, T("error.el_bulto_esta_vacio"));
        return InTransactionAsync(async (c, tx, now) =>
        {
            var order = await GetAsync(c, tx, orderId);
            Require(order is not null, T("error.el_pedido_no_existe"));
            Require(OrderStatuses.IsOpen(order!.Status), T("error.el_pedido_ya_fue_despachado"));
            await RequireSiteAccessAsync(c, tx, order.SiteId);
            await TaskService.RequireMyPickAsync(c, tx, Context, orderId);
            var plan = await PackingAsync(c, tx, orderId);
            foreach (var line in content)
            {
                var item = plan.Items.FirstOrDefault(i => i.ItemId == line.ItemId);
                Require(item is not null, T("error.el_articulo_no_esta_en_el_pedido"));
                Require(line.Quantity <= item!.Left, T("error.solo_faltan_empacar", item.Left, item.ItemName));
            }
            var number = await c.ScalarAsync<long>("SELECT COALESCE(MAX(number), 0) + 1 FROM order_packages WHERE order_id = $id", tx, ("$id", orderId));
            var id = Guid.NewGuid();
            await c.ExecAsync("""
                INSERT INTO order_packages (id, order_id, number, weight_kg, notes, created_at, user_name)
                VALUES ($id, $order, $number, $weight, $notes, $at, $user)
                """, tx, ("$id", id), ("$order", orderId), ("$number", number), ("$weight", weightKg), ("$notes", Clean(notes)), ("$at", now),
                ("$user", Context.UserName));
            foreach (var line in content)
                await c.ExecAsync("INSERT INTO order_package_lines (package_id, item_id, quantity) VALUES ($id, $item, $qty)", tx,
                    ("$id", id), ("$item", line.ItemId), ("$qty", line.Quantity));
            return (int)number;
        }, ct);
    }

    /// <summary>Deshace un bulto que aún no se despachó.</summary>
    public Task RemovePackageAsync(Guid packageId, CancellationToken ct = default)
    {
        RequirePermission(WmsPermissions.Pick);
        return InTransactionAsync(async (c, tx, now) =>
        {
            var removed = await c.ExecAsync("DELETE FROM order_packages WHERE id = $id AND shipment_id IS NULL", tx, ("$id", packageId));
            if (removed > 0)
                await c.ExecAsync("DELETE FROM order_package_lines WHERE package_id = $id", tx, ("$id", packageId));
            return removed;
        }, ct);
    }

    private static async Task<List<PackageRow>> PackagesAsync(SqliteConnection c, SqliteTransaction? tx, Guid orderId, bool includeShipped = false)
    {
        var packages = await c.QueryAsync("""
            SELECT * FROM order_packages WHERE order_id = $id AND ($all = 1 OR shipment_id IS NULL) ORDER BY number
            """, r => new PackageRow(r.Guid("id"), r.Int("number"), r.DecOrNull("weight_kg"), r.StrOrNull("notes"), r.GuidOrNull("shipment_id"),
                r.Time("created_at"), r.StrOrNull("user_name"), []), tx, ("$id", orderId), ("$all", includeShipped));
        for (var i = 0; i < packages.Count; i++)
        {
            var lines = await c.QueryAsync("""
                SELECT pl.item_id, COALESCE(i.sku, '?') AS sku, COALESCE(i.name, '?') AS name, pl.quantity
                FROM order_package_lines pl LEFT JOIN items i ON i.id = pl.item_id WHERE pl.package_id = $id ORDER BY i.name
                """, r => new PackageLineRow(r.Guid("item_id"), r.Str("sku"), r.Str("name"), r.Dec("quantity")), tx, ("$id", packages[i].Id));
            packages[i] = packages[i] with { Lines = lines };
        }
        return packages;
    }

    private sealed record Spot(Guid? LocationId, string? Code, decimal Quantity);

    /// <summary>
    /// Dónde hay del artículo en la sede, descontado lo ya tomado (por cualquier pedido, sin despachar): primero las
    /// ubicaciones en orden de tipo y código, al final lo que no tiene ubicación.
    /// </summary>
    private static async Task<List<Spot>> AvailableSpotsAsync(SqliteConnection c, SqliteTransaction? tx, Guid siteId, Guid itemId, Guid orderId)
    {
        var spots = await c.QueryAsync("""
            SELECT l.id, l.code, s.quantity - COALESCE((SELECT SUM(p.quantity) FROM order_picks p
                                                      WHERE p.location_id = l.id AND p.item_id = s.item_id AND p.shipment_id IS NULL), 0) AS free
            FROM location_stock s JOIN locations l ON l.id = s.location_id
            WHERE s.site_id = $site AND s.item_id = $item AND l.kind <> 'quarantine'
            ORDER BY CASE l.kind WHEN 'picking' THEN 0 WHEN 'storage' THEN 1 WHEN 'shipping' THEN 2 ELSE 3 END, l.code
            """, r => new Spot(r.Guid("id"), r.Str("code"), r.Dec("free")), tx, ("$site", siteId), ("$item", itemId));
        var located = await c.ScalarAsync<decimal>("SELECT COALESCE(SUM(quantity), 0) FROM location_stock WHERE site_id = $site AND item_id = $item",
            tx, ("$site", siteId), ("$item", itemId));
        var unlocatedPicked = await c.ScalarAsync<decimal>("""
            SELECT COALESCE(SUM(quantity), 0) FROM order_picks p JOIN orders o ON o.id = p.order_id
            WHERE p.location_id IS NULL AND p.item_id = $item AND p.shipment_id IS NULL AND o.site_id = $site
            """, tx, ("$site", siteId), ("$item", itemId));
        var unlocated = await StockLedger.OnHandAsync(c, tx, siteId, itemId) - located - unlocatedPicked;
        spots.Add(new Spot(null, null, unlocated));
        return spots.Where(s => s.Quantity > 0).ToList();
    }

    private static Task<List<PickRow>> PicksAsync(SqliteConnection c, SqliteTransaction? tx, Guid orderId) =>
        c.QueryAsync("""
            SELECT p.*, COALESCE(i.name, '?') AS item_name, l.code AS location_code
            FROM order_picks p LEFT JOIN items i ON i.id = p.item_id LEFT JOIN locations l ON l.id = p.location_id
            WHERE p.order_id = $id AND p.shipment_id IS NULL ORDER BY p.picked_at
            """, r => new PickRow(r.Guid("id"), r.Guid("item_id"), r.Str("item_name"), r.GuidOrNull("location_id"), r.StrOrNull("location_code"),
                r.Dec("quantity"), r.Time("picked_at"), r.StrOrNull("user_name")), tx, ("$id", orderId));

    // ---------- Despacho ----------

    /// <summary>
    /// Despacha el pedido desde <paramref name="siteId"/> (por defecto, la sede del pedido): libera la reserva,
    /// descuenta el stock y publica <c>shipment.dispatched</c>. Si se preparó con picking guiado sale lo tomado (de esas
    /// ubicaciones); si no, sale todo lo que falta. Si sale menos de lo que falta, es un despacho parcial: necesita
    /// <paramref name="allowPartial"/>, el resto queda pendiente y reservado, y el pedido queda "parcial".
    /// </summary>
    /// <param name="picks">Lotes o series que se despachan por artículo. Sin ellos, los lotes salen por FEFO; elegir otro lote
    /// requiere permiso. Los artículos con serie necesitan sus series.</param>
    public Task<string> DispatchAsync(Guid orderId, string? carrier, string? trackingNumber, Guid? siteId = null,
        IReadOnlyList<DocumentLine>? picks = null, bool allowPartial = false, CancellationToken ct = default)
    {
        RequirePermission(WmsPermissions.Dispatch);
        return InTransactionAsync(async (c, tx, now) =>
        {
            var order = await c.FirstOrDefaultAsync("SELECT * FROM orders WHERE id = $id", r => new
            {
                Status = r.Str("status"), SiteId = r.Guid("site_id"), CustomerId = r.GuidOrNull("customer_id"), Number = r.Str("number"),
            }, tx, ("$id", orderId));
            Require(order is not null, T("error.el_pedido_no_existe"));
            Require(OrderStatuses.IsOpen(order!.Status), T("error.el_pedido_ya_fue_despachado"));
            await RequireApprovedAsync(c, tx, orderId);
            var from = siteId ?? order.SiteId;
            Require(from != Guid.Empty, T("error.elige_el_almacen_desde_el"));
            await InventoryService.RequireSiteAsync(c, tx, from);
            await RequireSiteAccessAsync(c, tx, from);

            var lines = await LinesAsync(c, tx, orderId);
            var taken = await PicksAsync(c, tx, orderId);
            Require(taken.Count == 0 || from == order.SiteId, T("error.lo_preparado_es_de_otra_sede"));
            // Por artículo: lo que falta despachar y lo que sale ahora (lo tomado, o todo lo que falta si no hubo picking).
            var perItem = lines.Where(l => l.Tracked).GroupBy(l => l.ItemId).Select(g =>
            {
                var remaining = g.Sum(l => l.Quantity - l.Shipped);
                var amount = g.Sum(l => l.Quantity * l.UnitPrice);
                var units = g.Sum(l => l.Quantity);
                var ship = taken.Count > 0 ? Math.Min(remaining, taken.Where(p => p.ItemId == g.Key).Sum(p => p.Quantity)) : remaining;
                return (ItemId: g.Key, Remaining: remaining, Ship: ship, Amount: units == 0 ? 0 : amount * ship / units);
            }).Where(x => x.Remaining > 0).ToList();
            var partial = perItem.Any(x => x.Ship < x.Remaining);
            Require(perItem.Any(x => x.Ship > 0), T("error.no_hay_nada_preparado"));
            Require(!partial || allowPartial, T("error.faltan_para_completar", order.Number));
            foreach (var line in perItem.Where(x => x.Ship > 0))
            {
                var onHand = await StockLedger.OnHandAsync(c, tx, from, line.ItemId);
                Require(onHand >= line.Ship,
                    T("error.stock_insuficiente_de_de", (await InventoryService.ItemLabelAsync(c, tx, line.ItemId)), onHand, line.Ship));
            }

            // Ids derivados del pedido (y del número de despacho, si sale en partes): si dos equipos despachan lo mismo
            // sin conexión, el hub se queda con uno y el stock baja una sola vez.
            var previous = await c.ScalarAsync<long>("SELECT COUNT(*) FROM order_shipments WHERE order_id = $id", tx, ("$id", orderId));
            var shipmentId = previous == 0 ? DeterministicId.From("shipment", orderId)
                : DeterministicId.From("shipment", orderId, DeterministicId.From("part", new Guid((int)previous, 0, 0, new byte[8])));
            var shipmentNumber = await DocumentNumbers.NextAsync(c, tx, DocumentNumbers.Shipment, Context.DeviceTag);

            // Lo tomado de cada ubicación sale de esa ubicación: pasa a "sin ubicación" justo antes de descontarse (y el
            // stock que baja se descuenta primero de lo que no tiene ubicación).
            foreach (var pick in taken.Where(p => p.LocationId is not null))
            {
                var move = new StockRelocated(from, pick.ItemId, pick.LocationId, null, pick.Quantity, order.Number);
                var moveId = DeterministicId.From("pick_out", pick.Id);
                if (await LocationService.ApplyAsync(c, tx, moveId, now, move, Context.UserName))
                    await Outbox.EnqueueEventAsync(c, tx, now, SuiteEvents.StockRelocated, move, subjectType: "item",
                        subjectId: pick.ItemId.ToString(), id: moveId);
            }

            var dispatched = new Dictionary<Guid, StockLedger.Valued>();
            foreach (var line in perItem.Where(x => x.Ship > 0))
            {
                if (order.SiteId != Guid.Empty)
                    await StockLedger.ReserveAsync(c, tx, now, order.SiteId, line.ItemId, -line.Ship);
                var pick = InventoryService.Merge((picks ?? []).Where(p => p.ItemId == line.ItemId)).FirstOrDefault();
                dispatched[line.ItemId] = await StockLedger.ApplyValuedAsync(c, tx, now, Context.UserName, new StockLedger.Entry(from, line.ItemId,
                        -line.Ship, MovementKinds.Shipment, "shipment", orderId, order.Number,
                        MovementId: previous == 0 ? DeterministicId.From("shipment_line", orderId, line.ItemId)
                            : DeterministicId.From("shipment_line", shipmentId, line.ItemId),
                        SaleAmount: line.Amount, Lots: pick?.Lots, Serials: pick?.Serials,
                        AllowFefoOverride: Context.Can(WmsPermissions.ManageLots)));
                await MarkShippedAsync(c, tx, orderId, line.ItemId, line.Ship);
            }

            var packages = await PackagesAsync(c, tx, orderId);
            await c.ExecAsync("UPDATE order_picks SET shipment_id = $shipment WHERE order_id = $id AND shipment_id IS NULL", tx,
                ("$id", orderId), ("$shipment", shipmentId));
            await c.ExecAsync("UPDATE order_packages SET shipment_id = $shipment WHERE order_id = $id AND shipment_id IS NULL", tx,
                ("$id", orderId), ("$shipment", shipmentId));
            await TaskService.CloseOpenAsync(c, tx, now, Context.UserName, TaskKinds.Pick, orderId: orderId);
            await c.ExecAsync("""
                INSERT INTO order_shipments (id, order_id, number, dispatched_at, partial) VALUES ($id, $order, $number, $at, $partial)
                """, tx, ("$id", shipmentId), ("$order", orderId), ("$number", shipmentNumber), ("$at", now), ("$partial", partial));
            await c.ExecAsync("""
                UPDATE orders SET status = $status, dispatched_at = $now, shipment_id = $shipment, carrier = $carrier,
                                  tracking_number = $tracking, site_id = $site
                WHERE id = $id
                """, tx, ("$id", orderId), ("$status", partial ? OrderStatuses.Partial : OrderStatuses.Dispatched), ("$now", now),
                ("$shipment", shipmentId), ("$carrier", Clean(carrier)), ("$tracking", Clean(trackingNumber)), ("$site", from));
            await Outbox.EnqueueEventAsync(c, tx, now, SuiteEvents.ShipmentDispatched,
                new ShipmentDispatched(shipmentId, orderId, order.CustomerId ?? Guid.Empty, Clean(carrier), Clean(trackingNumber),
                    shipmentNumber, from, perItem.Where(x => x.Ship > 0).Select(x => new QuantityLine(x.ItemId, x.Ship, null, null,
                        dispatched.GetValueOrDefault(x.ItemId)?.Lots is { Count: > 0 } lots ? lots.Select(l => l with { Quantity = -l.Quantity }).ToList() : null,
                        dispatched.GetValueOrDefault(x.ItemId)?.Serials is { Count: > 0 } s ? s : null)).ToList(),
                    partial ? true : null,
                    packages.Count == 0 ? null : packages.Select(p => new ShipmentPackage(p.Number, p.WeightKg,
                        p.Lines.Select(l => new QuantityLine(l.ItemId, l.Quantity)).ToList())).ToList()),
                subjectType: "shipment", subjectId: shipmentNumber, id: shipmentId);
            return shipmentNumber;
        }, ct);
    }

    /// <summary>Reparte lo despachado de un artículo entre sus líneas, en orden.</summary>
    private static async Task MarkShippedAsync(SqliteConnection c, SqliteTransaction tx, Guid orderId, Guid itemId, decimal quantity)
    {
        var lines = await c.QueryAsync("SELECT line_no, quantity, shipped FROM order_lines WHERE order_id = $id AND item_id = $item ORDER BY line_no",
            r => (No: r.Int("line_no"), Qty: r.Dec("quantity"), Shipped: r.Dec("shipped")), tx, ("$id", orderId), ("$item", itemId));
        foreach (var line in lines)
        {
            if (quantity <= 0)
                break;
            var add = Math.Min(quantity, line.Qty - line.Shipped);
            if (add <= 0)
                continue;
            await c.ExecAsync("UPDATE order_lines SET shipped = shipped + $add WHERE order_id = $id AND line_no = $no", tx,
                ("$id", orderId), ("$no", line.No), ("$add", add));
            quantity -= add;
        }
    }

    private static async Task RequireApprovedAsync(SqliteConnection c, SqliteTransaction tx, Guid orderId) =>
        Require(await c.ScalarAsync<string?>("SELECT approval FROM orders WHERE id = $id", tx, ("$id", orderId)) != OrderApprovals.Pending,
            T("error.pedido_por_aprobar"));

    public Task CancelAsync(Guid orderId, string? reason = null, CancellationToken ct = default)
    {
        RequirePermission(WmsPermissions.CancelOrder);
        return InTransactionAsync(async (c, tx, now) =>
        {
            var status = await StatusAsync(c, tx, orderId);
            Require(status is not null && OrderStatuses.IsOpen(status), T("error.solo_se_cancela_un_pedido"));
            await RequireOrderSiteAsync(c, tx, orderId);
            var number = await c.ScalarAsync<string?>("SELECT number FROM orders WHERE id = $id", tx, ("$id", orderId));
            await AuditAsync(c, tx, now, VortexApi.Suite.Auditing.AuditActions.Cancelled, "order", orderId, number, Clean(reason));
            await ReleaseAndCancelAsync(c, tx, now, orderId);
            await Outbox.EnqueueEventAsync(c, tx, now, SuiteEvents.OrderCancelled, new OrderCancelled(orderId, number, Clean(reason)),
                subjectType: "order", subjectId: number, id: DeterministicId.From("order_cancel", orderId));
            return 1;
        }, ct);
    }

    /// <summary><c>order.picking_started</c> de otro equipo: el pedido pasa a preparación.</summary>
    internal static Task ApplyPickingStartedAsync(SqliteConnection c, SqliteTransaction tx, OrderPickingStarted e) =>
        c.ExecAsync("UPDATE orders SET status = 'picking' WHERE id = $id AND status = 'pending'", tx, ("$id", e.OrderId));

    /// <summary>
    /// <c>order.cancelled</c> del WMS (otro equipo), el ERP o el CRM: libera la reserva. Si aquí ya se despachó, la
    /// mercadería salió: prevalece el despacho y queda el conflicto a la vista.
    /// </summary>
    internal async Task ApplyCancelledAsync(SqliteConnection c, SqliteTransaction tx, DateTimeOffset now, OrderCancelled e)
    {
        var status = await StatusAsync(c, tx, e.OrderId);
        if (status is not null && OrderStatuses.IsOpen(status))
            await ReleaseAndCancelAsync(c, tx, now, e.OrderId);
        else if (status == OrderStatuses.Dispatched)
            await SyncConflicts.RecordAsync(c, tx, now, SyncConflictKinds.CancelAfterDispatch, e.OrderId, e.Number, e.Reason);
    }

    /// <summary>El pedido es de una de las sedes del usuario (sin sede asignada todavía, cualquiera puede tomarlo).</summary>
    private async Task RequireOrderSiteAsync(SqliteConnection c, SqliteTransaction tx, Guid orderId)
    {
        var site = await c.ScalarAsync<string?>("SELECT site_id FROM orders WHERE id = $id", tx, ("$id", orderId));
        if (site is not null && Guid.Parse(site) is var siteId && siteId != Guid.Empty)
            await RequireSiteAccessAsync(c, tx, siteId);
    }

    private static async Task ReleaseAndCancelAsync(SqliteConnection c, SqliteTransaction tx, DateTimeOffset now, Guid orderId)
    {
        var siteId = await c.ScalarAsync<string>("SELECT site_id FROM orders WHERE id = $id", tx, ("$id", orderId));
        var pending = await c.ScalarAsync<string?>("SELECT approval FROM orders WHERE id = $id", tx, ("$id", orderId)) == OrderApprovals.Pending;
        if (!pending && Guid.Parse(siteId) is var site && site != Guid.Empty)
        {
            // Solo se libera lo que seguía reservado: lo que faltaba despachar.
            foreach (var line in (await LinesAsync(c, tx, orderId)).Where(l => l.Tracked && l.Quantity > l.Shipped))
                await StockLedger.ReserveAsync(c, tx, now, site, line.ItemId, -(line.Quantity - line.Shipped));
        }
        await c.ExecAsync("DELETE FROM order_picks WHERE order_id = $id AND shipment_id IS NULL", tx, ("$id", orderId));
        await c.ExecAsync("""
            DELETE FROM order_package_lines WHERE package_id IN (SELECT id FROM order_packages WHERE order_id = $id AND shipment_id IS NULL);
            DELETE FROM order_packages WHERE order_id = $id AND shipment_id IS NULL;
            """, tx, ("$id", orderId));
        await TaskService.CloseOpenAsync(c, tx, now, null, TaskKinds.Pick, orderId: orderId, status: TaskStatuses.Cancelled);
        await c.ExecAsync("UPDATE orders SET status = 'cancelled' WHERE id = $id", tx, ("$id", orderId));
    }

    private static async Task InsertAsync(SqliteConnection c, SqliteTransaction tx, DateTimeOffset now, Guid orderId, string number,
        string source, Guid customerId, Guid siteId, IReadOnlyList<OrderLine> lines, string currencyCode, string? approval = null)
    {
        await c.ExecAsync("""
            INSERT INTO orders (id, number, source, customer_id, site_id, status, total, currency_code, created_at, approval)
            VALUES ($id, $number, $source, $customer, $site, 'pending', $total, $currency, $now, $approval)
            """, tx, ("$id", orderId), ("$number", number), ("$source", source),
            ("$customer", customerId == Guid.Empty ? null : customerId), ("$site", siteId),
            ("$total", lines.Sum(l => l.Quantity * l.UnitPrice)), ("$currency", currencyCode), ("$now", now), ("$approval", approval));

        var lineNo = 0;
        foreach (var line in lines.Where(l => l.ItemId != Guid.Empty && l.Quantity > 0))
        {
            await c.ExecAsync("""
                INSERT INTO order_lines (order_id, line_no, item_id, quantity, unit_price) VALUES ($id, $no, $item, $qty, $price)
                """, tx, ("$id", orderId), ("$no", ++lineNo), ("$item", line.ItemId), ("$qty", line.Quantity), ("$price", line.UnitPrice));
            // Un pedido por aprobar no reserva: aún no es un compromiso (reserva al llegar order.approved).
            if (approval != OrderApprovals.Pending && siteId != Guid.Empty && await StockLedger.TracksInventoryAsync(c, tx, line.ItemId))
                await StockLedger.ReserveAsync(c, tx, now, siteId, line.ItemId, line.Quantity);
        }
    }

    private sealed record Line(Guid ItemId, decimal Quantity, bool Tracked, decimal UnitPrice, decimal Shipped);

    private static async Task<List<Line>> LinesAsync(SqliteConnection c, SqliteTransaction? tx, Guid orderId) =>
        await c.QueryAsync("""
            SELECT l.item_id, l.quantity, COALESCE(i.track_inventory, 1) AS tracked, l.unit_price, l.shipped
            FROM order_lines l LEFT JOIN items i ON i.id = l.item_id WHERE l.order_id = $id ORDER BY l.line_no
            """, r => new Line(r.Guid("item_id"), r.Dec("quantity"), r.Bool("tracked"), r.Dec("unit_price"), r.Dec("shipped")), tx, ("$id", orderId));

    private static Task<string?> StatusAsync(SqliteConnection c, SqliteTransaction tx, Guid orderId) =>
        c.ScalarAsync<string?>("SELECT status FROM orders WHERE id = $id", tx, ("$id", orderId));

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
