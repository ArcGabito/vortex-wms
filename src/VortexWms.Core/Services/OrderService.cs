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
            WHERE ($status IS NULL OR o.status = $status OR ($status = 'open' AND o.status IN ('pending', 'picking')))
              AND ($search IS NULL OR o.number LIKE $like OR p.name LIKE $like OR o.tracking_number LIKE $like)
            """;
        const string from = "FROM orders o LEFT JOIN parties p ON p.id = o.customer_id LEFT JOIN sites t ON t.id = o.site_id";
        var args = new (string, object?)[]
        {
            ("$status", string.IsNullOrWhiteSpace(status) ? null : status), ("$search", string.IsNullOrWhiteSpace(search) ? null : search.Trim()),
            ("$like", Like(search)), ("$limit", pageSize), ("$offset", (page - 1) * pageSize),
        };
        var total = await c.ScalarAsync<int>($"SELECT COUNT(*) {from} {where}", null, args);
        var rows = await c.QueryAsync($"""
            SELECT o.*, p.name AS customer_name, t.name AS site_name,
                   (SELECT COUNT(*) FROM order_lines l WHERE l.order_id = o.id) AS line_count,
                   (SELECT COALESCE(SUM(quantity), 0) FROM order_lines l WHERE l.order_id = o.id) AS units
            {from} {where}
            ORDER BY CASE o.status WHEN 'picking' THEN 0 WHEN 'pending' THEN 1 ELSE 2 END, o.created_at DESC
            LIMIT $limit OFFSET $offset
            """, r => new OrderRow(r.Guid("id"), r.Str("number"), r.Str("source"), r.GuidOrNull("customer_id"),
                r.StrOrNull("customer_name"), r.Guid("site_id"), r.StrOrNull("site_name"), r.Str("status"), r.Dec("total"),
                r.Str("currency_code"), r.Time("created_at"), r.TimeOrNull("dispatched_at"), r.StrOrNull("carrier"),
                r.StrOrNull("tracking_number"), r.Int("line_count"), r.Dec("units")), null, args);
        return new PagedResult<OrderRow>(rows, total, page, pageSize);
    }

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
                r.Dec("unit_price"), r.Dec("on_hand")), null, ("$id", orderId));
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
        await InsertAsync(c, tx, now, e.OrderId, number, source, e.CustomerId, siteId, e.Lines ?? [], e.CurrencyCode ?? "PEN");
    }

    /// <summary>
    /// <c>shipment.dispatched</c> de otro equipo del WMS: cierra el pedido y libera su reserva. El stock no se toca
    /// aquí: llega por los <c>stock.adjusted</c> de ese equipo.
    /// </summary>
    internal async Task ApplyDispatchedElsewhereAsync(SqliteConnection c, SqliteTransaction tx, DateTimeOffset now,
        DateTimeOffset occurredAt, ShipmentDispatched d)
    {
        var order = await c.FirstOrDefaultAsync("SELECT status, site_id FROM orders WHERE id = $id",
            r => new { Status = r.Str("status"), SiteId = r.Guid("site_id") }, tx, ("$id", d.OrderId));
        if (order is null || order.Status is not (OrderStatuses.Pending or OrderStatuses.Picking))
            return;
        if (order.SiteId != Guid.Empty)
        {
            foreach (var line in (await LinesAsync(c, tx, d.OrderId)).Where(l => l.Tracked))
                await StockLedger.ReserveAsync(c, tx, now, order.SiteId, line.ItemId, -line.Quantity);
        }
        await c.ExecAsync("""
            UPDATE orders SET status = 'dispatched', dispatched_at = $at, shipment_id = $shipment, carrier = $carrier,
                              tracking_number = $tracking, site_id = $site
            WHERE id = $id
            """, tx, ("$id", d.OrderId), ("$at", occurredAt), ("$shipment", d.ShipmentId), ("$carrier", d.Carrier),
            ("$tracking", d.TrackingNumber), ("$site", d.SiteId == Guid.Empty ? order.SiteId : d.SiteId));
    }

    public Task StartPickingAsync(Guid orderId, CancellationToken ct = default)
    {
        RequirePermission(WmsPermissions.Pick);
        return InTransactionAsync(async (c, tx, _) =>
        {
            var status = await StatusAsync(c, tx, orderId);
            Require(status == OrderStatuses.Pending, T("error.solo_se_puede_preparar_un"));
            return await c.ExecAsync("UPDATE orders SET status = 'picking' WHERE id = $id", tx, ("$id", orderId));
        }, ct);
    }

    /// <summary>
    /// Despacha el pedido desde <paramref name="siteId"/> (por defecto, la sede del pedido): libera la reserva,
    /// descuenta el stock y publica <c>shipment.dispatched</c>.
    /// </summary>
    public Task<string> DispatchAsync(Guid orderId, string? carrier, string? trackingNumber, Guid? siteId = null, CancellationToken ct = default)
    {
        RequirePermission(WmsPermissions.Dispatch);
        return InTransactionAsync(async (c, tx, now) =>
        {
            var order = await c.FirstOrDefaultAsync("SELECT * FROM orders WHERE id = $id", r => new
            {
                Status = r.Str("status"), SiteId = r.Guid("site_id"), CustomerId = r.GuidOrNull("customer_id"), Number = r.Str("number"),
            }, tx, ("$id", orderId));
            Require(order is not null, T("error.el_pedido_no_existe"));
            Require(order!.Status is OrderStatuses.Pending or OrderStatuses.Picking, T("error.el_pedido_ya_fue_despachado"));
            var from = siteId ?? order.SiteId;
            Require(from != Guid.Empty, T("error.elige_el_almacen_desde_el"));
            await InventoryService.RequireSiteAsync(c, tx, from);

            var lines = await LinesAsync(c, tx, orderId);
            foreach (var line in lines.Where(l => l.Tracked))
            {
                var onHand = await StockLedger.OnHandAsync(c, tx, from, line.ItemId);
                Require(onHand >= line.Quantity,
                    T("error.stock_insuficiente_de_de", (await InventoryService.ItemLabelAsync(c, tx, line.ItemId)), onHand, line.Quantity));
            }

            var shipmentId = Guid.NewGuid();
            var shipmentNumber = await DocumentNumbers.NextAsync(c, tx, DocumentNumbers.Shipment, Context.DeviceTag);
            foreach (var line in lines.Where(l => l.Tracked))
            {
                if (order.SiteId != Guid.Empty)
                    await StockLedger.ReserveAsync(c, tx, now, order.SiteId, line.ItemId, -line.Quantity);
                await StockLedger.ApplyAsync(c, tx, now, Context.UserName, new StockLedger.Entry(from, line.ItemId, -line.Quantity,
                        MovementKinds.Shipment, "shipment", orderId, order.Number));
            }

            await c.ExecAsync("""
                UPDATE orders SET status = 'dispatched', dispatched_at = $now, shipment_id = $shipment, carrier = $carrier,
                                  tracking_number = $tracking, site_id = $site
                WHERE id = $id
                """, tx, ("$id", orderId), ("$now", now), ("$shipment", shipmentId), ("$carrier", Clean(carrier)),
                ("$tracking", Clean(trackingNumber)), ("$site", from));
            await Outbox.EnqueueEventAsync(c, tx, now, SuiteEvents.ShipmentDispatched,
                new ShipmentDispatched(shipmentId, orderId, order.CustomerId ?? Guid.Empty, Clean(carrier), Clean(trackingNumber),
                    shipmentNumber, from, lines.Select(l => new QuantityLine(l.ItemId, l.Quantity)).ToList()),
                subjectType: "shipment", subjectId: shipmentNumber, id: shipmentId);
            return shipmentNumber;
        }, ct);
    }

    public Task CancelAsync(Guid orderId, CancellationToken ct = default)
    {
        RequirePermission(WmsPermissions.CancelOrder);
        return InTransactionAsync(async (c, tx, now) =>
        {
            var status = await StatusAsync(c, tx, orderId);
            Require(status is OrderStatuses.Pending or OrderStatuses.Picking, T("error.solo_se_cancela_un_pedido"));
            await AuditAsync(c, tx, now, VortexApi.Suite.Auditing.AuditActions.Cancelled, "order", orderId,
                await c.ScalarAsync<string?>("SELECT number FROM orders WHERE id = $id", tx, ("$id", orderId)), null);
            var siteId = await c.ScalarAsync<string>("SELECT site_id FROM orders WHERE id = $id", tx, ("$id", orderId));
            if (Guid.Parse(siteId) is var site && site != Guid.Empty)
            {
                foreach (var line in (await LinesAsync(c, tx, orderId)).Where(l => l.Tracked))
                    await StockLedger.ReserveAsync(c, tx, now, site, line.ItemId, -line.Quantity);
            }
            return await c.ExecAsync("UPDATE orders SET status = 'cancelled' WHERE id = $id", tx, ("$id", orderId));
        }, ct);
    }

    private static async Task InsertAsync(SqliteConnection c, SqliteTransaction tx, DateTimeOffset now, Guid orderId, string number,
        string source, Guid customerId, Guid siteId, IReadOnlyList<OrderLine> lines, string currencyCode)
    {
        await c.ExecAsync("""
            INSERT INTO orders (id, number, source, customer_id, site_id, status, total, currency_code, created_at)
            VALUES ($id, $number, $source, $customer, $site, 'pending', $total, $currency, $now)
            """, tx, ("$id", orderId), ("$number", number), ("$source", source),
            ("$customer", customerId == Guid.Empty ? null : customerId), ("$site", siteId),
            ("$total", lines.Sum(l => l.Quantity * l.UnitPrice)), ("$currency", currencyCode), ("$now", now));

        var lineNo = 0;
        foreach (var line in lines.Where(l => l.ItemId != Guid.Empty && l.Quantity > 0))
        {
            await c.ExecAsync("""
                INSERT INTO order_lines (order_id, line_no, item_id, quantity, unit_price) VALUES ($id, $no, $item, $qty, $price)
                """, tx, ("$id", orderId), ("$no", ++lineNo), ("$item", line.ItemId), ("$qty", line.Quantity), ("$price", line.UnitPrice));
            if (siteId != Guid.Empty && await StockLedger.TracksInventoryAsync(c, tx, line.ItemId))
                await StockLedger.ReserveAsync(c, tx, now, siteId, line.ItemId, line.Quantity);
        }
    }

    private sealed record Line(Guid ItemId, decimal Quantity, bool Tracked);

    private static async Task<List<Line>> LinesAsync(SqliteConnection c, SqliteTransaction tx, Guid orderId) =>
        await c.QueryAsync("""
            SELECT l.item_id, l.quantity, COALESCE(i.track_inventory, 1) AS tracked
            FROM order_lines l LEFT JOIN items i ON i.id = l.item_id WHERE l.order_id = $id ORDER BY l.line_no
            """, r => new Line(r.Guid("item_id"), r.Dec("quantity"), r.Bool("tracked")), tx, ("$id", orderId));

    private static Task<string?> StatusAsync(SqliteConnection c, SqliteTransaction tx, Guid orderId) =>
        c.ScalarAsync<string?>("SELECT status FROM orders WHERE id = $id", tx, ("$id", orderId));

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
