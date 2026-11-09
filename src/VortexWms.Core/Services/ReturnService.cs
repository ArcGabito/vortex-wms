using System.Text.Json;
using Microsoft.Data.Sqlite;
using VortexApi.Client;
using VortexWms.Core.Data;
using VortexWms.Core.Domain;
using VortexWms.Core.Integration;
using VortexWms.Core.Sync;

using static VortexWms.Core.Texts.CoreText;

namespace VortexWms.Core.Services;

public static class ReturnDispositions
{
    /// <summary>Está bien: vuelve al stock disponible.</summary>
    public const string Restock = "restock";

    /// <summary>Hay que revisarlo: entra al stock pero no sale (lote en cuarentena o ubicación de cuarentena).</summary>
    public const string Quarantine = "quarantine";

    /// <summary>No sirve: no vuelve al stock (merma).</summary>
    public const string Scrap = "scrap";

    public static readonly string[] All = [Restock, Quarantine, Scrap];
}

public static class ReturnKinds
{
    public const string Customer = "customer";
    public const string Supplier = "supplier";
}

/// <param name="Disposition">En una devolución de cliente, qué se hace con lo que llegó (<see cref="ReturnDispositions"/>).</param>
public sealed record ReturnLineDraft(Guid ItemId, decimal Quantity, string? LotCode = null, string Disposition = ReturnDispositions.Restock,
    DateOnly? ExpiresAt = null);

/// <summary>Devolución de un cliente: con el pedido, se controla que no se devuelva más de lo despachado.</summary>
public sealed record CustomerReturnDraft(Guid SiteId, Guid? CustomerId, Guid? OrderId, IReadOnlyList<ReturnLineDraft> Lines, string Reason);

/// <summary>Devolución al proveedor, de lo que se le compró.</summary>
public sealed record SupplierReturnDraft(Guid SiteId, Guid SupplierId, Guid? PurchaseOrderId, IReadOnlyList<ReturnLineDraft> Lines, string Reason);

public sealed record ReturnRow(Guid Id, string Number, string Kind, Guid SiteId, string? SiteName, Guid? PartyId, string? PartyName, Guid? OrderId,
    string? OrderNumber, string Reason, IReadOnlyList<ReturnLineDraft> Lines, DateTimeOffset CreatedAt, string? CreatedBy);

public static class ShrinkageStatuses
{
    public const string Pending = "pending";
    public const string Approved = "approved";
    public const string Rejected = "rejected";
}

/// <summary>Merma reportada (rotura, vencimiento, pérdida) con su foto; se descuenta del stock cuando un supervisor la aprueba.</summary>
public sealed record ShrinkageRow(Guid Id, Guid SiteId, string? SiteName, Guid ItemId, string ItemName, string? LotCode, decimal Quantity, string Reason,
    string? Note, string? Photo, string Status, DateTimeOffset ReportedAt, string? ReportedBy, DateTimeOffset? DecidedAt, string? DecidedBy,
    string? DecisionNote, decimal? UnitCost);

/// <summary>
/// Devoluciones y mermas (fase 5b). La devolución de un cliente entra al stock (o a cuarentena, o se da por merma) y se
/// publica como <c>shipment.returned</c> para que el ERP emita la nota de crédito; la devolución al proveedor sale del stock
/// al costo promedio y se publica como <c>goods.returned</c>. Las mermas se reportan con foto y solo bajan el stock cuando
/// un supervisor las aprueba. Las devoluciones y las mermas son registros del WMS (gana el más nuevo); los movimientos de
/// stock viajan como siempre.
/// </summary>
public sealed class ReturnService(WmsDatabase db, IWmsContext context, TimeProvider clock) : WmsService(db, context, clock)
{
    public const string CustomerPrefix = "DEV";
    public const string SupplierPrefix = "DVP";

    /// <summary>Tamaño máximo de la foto de una merma (data URL).</summary>
    public const int MaxPhotoChars = 700_000;

    // ---------- Devolución de cliente ----------

    public Task<string> ReceiveCustomerReturnAsync(CustomerReturnDraft d, CancellationToken ct = default)
    {
        RequirePermission(WmsPermissions.ReturnStock);
        Require(!string.IsNullOrWhiteSpace(d.Reason), T("error.devolucion_motivo"));
        var lines = Validate(d.Lines);
        Require(lines.All(l => ReturnDispositions.All.Contains(l.Disposition)), T("error.devolucion_destino"));
        return InTransactionAsync(async (c, tx, now) =>
        {
            await RequireSiteAccessAsync(c, tx, d.SiteId);
            var customer = d.CustomerId;
            string? orderNumber = null;
            if (d.OrderId is { } orderId)
            {
                var order = await c.FirstOrDefaultAsync("SELECT number, customer_id FROM orders WHERE id = $id",
                    r => new { Number = r.Str("number"), Customer = r.GuidOrNull("customer_id") }, tx, ("$id", orderId));
                Require(order is not null, T("error.el_pedido_no_existe"));
                orderNumber = order!.Number;
                customer ??= order.Customer;
                // No se devuelve más de lo que salió de ese pedido (contando lo ya devuelto).
                foreach (var g in lines.GroupBy(l => l.ItemId))
                {
                    var shipped = await c.ScalarAsync<decimal>("SELECT COALESCE(SUM(shipped), 0) FROM order_lines WHERE order_id = $o AND item_id = $i", tx,
                        ("$o", orderId), ("$i", g.Key));
                    var returned = await ReturnedAsync(c, tx, orderId, g.Key);
                    Require(g.Sum(l => l.Quantity) <= shipped - returned,
                        T("error.devolucion_mas_de_lo_despachado", await InventoryService.ItemLabelAsync(c, tx, g.Key), shipped - returned));
                }
            }

            var id = Guid.NewGuid();
            var number = await DocumentNumbers.NextAsync(c, tx, CustomerPrefix, Context.DeviceTag);
            var quarantineLocation = await c.ScalarAsync<string?>(
                "SELECT id FROM locations WHERE site_id = $s AND kind = 'quarantine' AND is_active = 1 ORDER BY code LIMIT 1", tx, ("$s", d.SiteId));
            var no = 0;
            foreach (var l in lines)
            {
                no++;
                if (l.Disposition == ReturnDispositions.Scrap)
                    continue;   // no vuelve al stock: queda solo en la devolución (la nota de crédito igual corresponde)
                var tracking = await LotLedger.TrackingAsync(c, tx, l.ItemId);
                Require(tracking != ItemTrackings.Lot || l.LotCode is not null, T("error.indica_el_lote_de", await InventoryService.ItemLabelAsync(c, tx, l.ItemId)));
                await StockLedger.ApplyAsync(c, tx, now, Context.UserName, new StockLedger.Entry(d.SiteId, l.ItemId, l.Quantity, MovementKinds.CustomerReturn,
                    d.Reason.Trim(), DocumentId: id, Reference: number, MovementId: DeterministicId.From("customer_return", id, new Guid(no, 0, 0, new byte[8])),
                    Lots: l.LotCode is { } lot ? [new LotQuantity(lot, l.Quantity, l.ExpiresAt)] : null, LotMode: StockLedger.LotMode.Lenient));
                if (l.Disposition != ReturnDispositions.Quarantine)
                    continue;
                if (l.LotCode is { } code)
                    await LotService.PublishStatusAsync(c, tx, now, new LotStatusData(d.SiteId, l.ItemId, code, LotStatuses.Quarantine, number));
                else if (Guid.TryParse(quarantineLocation, out var q))
                    await new LocationService(Db, Context, Clock).RelocateCoreAsync(c, tx, now, d.SiteId, l.ItemId, null, q, l.Quantity, number);
            }

            await PublishAsync(c, tx, now, new ReturnData(id, number, ReturnKinds.Customer, d.SiteId, customer, d.OrderId, orderNumber, d.Reason.Trim(),
                lines, now, Context.UserName));
            await Outbox.EnqueueEventAsync(c, tx, now, SuiteEvents.ShipmentReturned,
                new ShipmentReturned(id, number, d.OrderId, null, customer, d.SiteId, d.Reason.Trim(),
                    lines.Select(l => new ShipmentReturnedLine(l.ItemId, l.Quantity, l.Disposition, l.LotCode)).ToList()),
                subjectType: "return", subjectId: number, id: id);
            await AuditAsync(c, tx, now, "return.customer", "return", id, number,
                $"{orderNumber ?? "sin pedido"}: {string.Join(", ", lines.Select(l => $"{l.Quantity:0.##} {l.Disposition}"))}");
            return number;
        }, ct);
    }

    // ---------- Devolución al proveedor ----------

    public Task<string> ReturnToSupplierAsync(SupplierReturnDraft d, CancellationToken ct = default)
    {
        RequirePermission(WmsPermissions.Adjust);
        Require(!string.IsNullOrWhiteSpace(d.Reason), T("error.devolucion_motivo"));
        var lines = Validate(d.Lines);
        return InTransactionAsync(async (c, tx, now) =>
        {
            await RequireSiteAccessAsync(c, tx, d.SiteId);
            var id = Guid.NewGuid();
            var number = await DocumentNumbers.NextAsync(c, tx, SupplierPrefix, Context.DeviceTag);
            var costs = new List<GoodsReturnedLine>();
            var no = 0;
            foreach (var l in lines)
            {
                no++;
                var onHand = await StockLedger.OnHandAsync(c, tx, d.SiteId, l.ItemId);
                Require(l.Quantity <= onHand, T("error.el_stock_no_puede_quedar", onHand));
                var cost = await AverageCostAsync(c, tx, d.SiteId, l.ItemId);
                await StockLedger.ApplyAsync(c, tx, now, Context.UserName, new StockLedger.Entry(d.SiteId, l.ItemId, -l.Quantity, MovementKinds.SupplierReturn,
                    d.Reason.Trim(), DocumentId: id, Reference: number, MovementId: DeterministicId.From("supplier_return", id, new Guid(no, 0, 0, new byte[8])),
                    Lots: l.LotCode is { } lot ? [new LotQuantity(lot, l.Quantity)] : null, LotMode: StockLedger.LotMode.Lenient,
                    AllowFefoOverride: true, AllowUnavailableLots: true));
                costs.Add(new GoodsReturnedLine(l.ItemId, l.Quantity, cost));
            }
            var poNumber = d.PurchaseOrderId is { } po ? await c.ScalarAsync<string?>("SELECT number FROM receipts WHERE purchase_order_id = $id", tx, ("$id", po)) : null;
            await PublishAsync(c, tx, now, new ReturnData(id, number, ReturnKinds.Supplier, d.SiteId, d.SupplierId, d.PurchaseOrderId, poNumber,
                d.Reason.Trim(), lines, now, Context.UserName));
            await Outbox.EnqueueEventAsync(c, tx, now, SuiteEvents.GoodsReturned,
                new GoodsReturned(id, number, d.PurchaseOrderId, d.SupplierId, d.SiteId, d.Reason.Trim(), costs),
                subjectType: "return", subjectId: number, id: id);
            await AuditAsync(c, tx, now, "return.supplier", "return", id, number, string.Join(", ", costs.Select(x => $"{x.Quantity:0.##} a {x.UnitCost:0.####}")));
            return number;
        }, ct);
    }

    public async Task<IReadOnlyList<ReturnRow>> GetReturnsAsync(string? kind = null, int limit = 100, CancellationToken ct = default)
    {
        await using var c = await Db.OpenAsync(ct);
        var rows = await c.QueryAsync($"""
            SELECT r.data, s.name AS site, p.name AS party FROM stock_returns r
            LEFT JOIN sites s ON s.id = r.site_id LEFT JOIN parties p ON p.id = r.party_id
            WHERE ($kind IS NULL OR r.kind = $kind){MySites("r.site_id")} ORDER BY r.created_at DESC LIMIT $limit
            """, r => (Data: JsonSerializer.Deserialize<ReturnData>(r.Str("data"), VortexJson.Options)!, Site: r.StrOrNull("site"), Party: r.StrOrNull("party")),
            null, ("$kind", kind), ("$limit", limit));
        return rows.Select(x => new ReturnRow(x.Data.Id, x.Data.Number, x.Data.Kind, x.Data.SiteId, x.Site, x.Data.PartyId, x.Party, x.Data.OrderId,
            x.Data.OrderNumber, x.Data.Reason, x.Data.Lines, x.Data.CreatedAt, x.Data.CreatedBy)).ToList();
    }

    // ---------- Mermas ----------

    /// <summary>Reporta una merma con su foto (PNG o JPEG en data URL). No cambia el stock hasta que se apruebe.</summary>
    public Task<Guid> ReportShrinkageAsync(Guid siteId, Guid itemId, decimal quantity, string reason, string? note, string? photo,
        string? lotCode = null, CancellationToken ct = default)
    {
        RequirePermission(WmsPermissions.ReportShrinkage);
        Require(quantity > 0, T("error.la_cantidad_debe_ser_mayor"));
        Require(reason is AdjustmentReasons.Damage or AdjustmentReasons.Loss or AdjustmentReasons.Expired, T("error.merma_motivo"));
        Require(photo is null || (photo.Length <= MaxPhotoChars
                                  && (photo.StartsWith("data:image/png;base64,", StringComparison.Ordinal) || photo.StartsWith("data:image/jpeg;base64,", StringComparison.Ordinal))),
            T("error.merma_foto"));
        return InTransactionAsync(async (c, tx, now) =>
        {
            await RequireSiteAccessAsync(c, tx, siteId);
            var onHand = await StockLedger.OnHandAsync(c, tx, siteId, itemId);
            Require(quantity <= onHand, T("error.el_stock_no_puede_quedar", onHand));
            var id = Guid.NewGuid();
            await PublishShrinkageAsync(c, tx, now, new ShrinkageData(id, siteId, itemId, Clean(lotCode)?.ToUpperInvariant(), quantity, reason, Clean(note),
                photo, ShrinkageStatuses.Pending, now, Context.UserName, null, null, null, null));
            await AuditAsync(c, tx, now, "shrinkage.reported", "item", itemId, await InventoryService.ItemLabelAsync(c, tx, itemId), $"{quantity:0.##} ({reason})");
            return id;
        }, ct);
    }

    /// <summary>El supervisor aprueba (baja el stock como ajuste con su motivo) o rechaza la merma.</summary>
    public Task DecideShrinkageAsync(Guid id, bool approve, string? note = null, CancellationToken ct = default)
    {
        RequirePermission(WmsPermissions.Adjust);
        return InTransactionAsync(async (c, tx, now) =>
        {
            var s = await c.FirstOrDefaultAsync("SELECT data FROM shrinkage WHERE id = $id",
                r => JsonSerializer.Deserialize<ShrinkageData>(r.Str("data"), VortexJson.Options)!, tx, ("$id", id));
            Require(s is not null, T("error.merma_no_existe"));
            Require(s!.Status == ShrinkageStatuses.Pending, T("error.merma_ya_decidida"));
            await RequireSiteAccessAsync(c, tx, s.SiteId);
            decimal? cost = null;
            if (approve)
            {
                var onHand = await StockLedger.OnHandAsync(c, tx, s.SiteId, s.ItemId);
                var quantity = Math.Min(s.Quantity, onHand);
                Require(quantity > 0, T("error.el_stock_no_puede_quedar", onHand));
                cost = await AverageCostAsync(c, tx, s.SiteId, s.ItemId);
                await StockLedger.ApplyAsync(c, tx, now, Context.UserName, new StockLedger.Entry(s.SiteId, s.ItemId, -quantity, MovementKinds.Adjustment,
                    s.Reason, DocumentId: id, Note: T("merma.ajuste", s.Note ?? "", s.ReportedBy ?? "").Trim(), MovementId: DeterministicId.From("shrinkage", id),
                    Lots: s.LotCode is { } lot ? [new LotQuantity(lot, quantity)] : null, LotMode: StockLedger.LotMode.Lenient,
                    AllowFefoOverride: true, AllowUnavailableLots: true));
            }
            await PublishShrinkageAsync(c, tx, now, s with
            {
                Status = approve ? ShrinkageStatuses.Approved : ShrinkageStatuses.Rejected, DecidedAt = now, DecidedBy = Context.UserName,
                DecisionNote = Clean(note), UnitCost = cost,
            });
            await AuditAsync(c, tx, now, approve ? "shrinkage.approved" : "shrinkage.rejected", "item", s.ItemId,
                await InventoryService.ItemLabelAsync(c, tx, s.ItemId), $"{s.Quantity:0.##} ({s.Reason}){(Clean(note) is { } n ? $": {n}" : "")}");
            return 0;
        }, ct);
    }

    public async Task<IReadOnlyList<ShrinkageRow>> GetShrinkageAsync(bool pendingOnly = false, CancellationToken ct = default)
    {
        await using var c = await Db.OpenAsync(ct);
        var rows = await c.QueryAsync($"""
            SELECT m.data, s.name AS site, i.name AS item FROM shrinkage m
            LEFT JOIN sites s ON s.id = m.site_id LEFT JOIN items i ON i.id = m.item_id
            WHERE ($pending = 0 OR m.status = 'pending'){MySites("m.site_id")} ORDER BY m.status = 'pending' DESC, m.created_at DESC LIMIT 200
            """, r => (Data: JsonSerializer.Deserialize<ShrinkageData>(r.Str("data"), VortexJson.Options)!, Site: r.StrOrNull("site"), Item: r.StrOrNull("item")),
            null, ("$pending", pendingOnly));
        return rows.Select(x => new ShrinkageRow(x.Data.Id, x.Data.SiteId, x.Site, x.Data.ItemId, x.Item ?? "?", x.Data.LotCode, x.Data.Quantity, x.Data.Reason,
            x.Data.Note, x.Data.Photo, x.Data.Status, x.Data.ReportedAt, x.Data.ReportedBy, x.Data.DecidedAt, x.Data.DecidedBy, x.Data.DecisionNote,
            x.Data.UnitCost)).ToList();
    }

    // ---------- Internos ----------

    private static List<ReturnLineDraft> Validate(IReadOnlyList<ReturnLineDraft> lines)
    {
        var list = lines.Where(l => l.Quantity > 0)
            .Select(l => l with { LotCode = string.IsNullOrWhiteSpace(l.LotCode) ? null : l.LotCode.Trim().ToUpperInvariant() }).ToList();
        if (list.Count == 0)
            throw new WmsValidationException(T("error.agrega_al_menos_un_articulo"));
        return list;
    }

    private static async Task<decimal> ReturnedAsync(SqliteConnection c, SqliteTransaction tx, Guid orderId, Guid itemId)
    {
        var total = 0m;
        foreach (var data in await c.QueryAsync("SELECT data FROM stock_returns WHERE order_id = $o AND kind = 'customer'",
                     r => JsonSerializer.Deserialize<ReturnData>(r.Str("data"), VortexJson.Options)!, tx, ("$o", orderId)))
            total += data.Lines.Where(l => l.ItemId == itemId).Sum(l => l.Quantity);
        return total;
    }

    private static async Task<decimal> AverageCostAsync(SqliteConnection c, SqliteTransaction tx, Guid siteId, Guid itemId) =>
        decimal.TryParse(await c.ScalarAsync<string?>("SELECT avg_cost FROM stock WHERE site_id = $s AND item_id = $i", tx, ("$s", siteId), ("$i", itemId)),
            System.Globalization.NumberStyles.Number, System.Globalization.CultureInfo.InvariantCulture, out var cost) ? cost : 0;

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    // ---------- Registros replicados ----------

    public sealed record ReturnData(Guid Id, string Number, string Kind, Guid SiteId, Guid? PartyId, Guid? OrderId, string? OrderNumber, string Reason,
        IReadOnlyList<ReturnLineDraft> Lines, DateTimeOffset CreatedAt, string? CreatedBy);

    public sealed record ShrinkageData(Guid Id, Guid SiteId, Guid ItemId, string? LotCode, decimal Quantity, string Reason, string? Note, string? Photo,
        string Status, DateTimeOffset ReportedAt, string? ReportedBy, DateTimeOffset? DecidedAt, string? DecidedBy, string? DecisionNote, decimal? UnitCost);

    private static async Task PublishAsync(SqliteConnection c, SqliteTransaction tx, DateTimeOffset now, ReturnData data)
    {
        var json = JsonSerializer.SerializeToElement(data, VortexJson.Options);
        await ApplyReturnAsync(c, tx, json, now, force: true);
        await Outbox.EnqueueEventAsync(c, tx, now, SuiteEvents.RecordUpserted, new WmsRecord(WmsRecordKinds.Return, data.Id.ToString(), now, json),
            subjectType: "return", subjectId: data.Number);
    }

    private static async Task PublishShrinkageAsync(SqliteConnection c, SqliteTransaction tx, DateTimeOffset now, ShrinkageData data)
    {
        var json = JsonSerializer.SerializeToElement(data, VortexJson.Options);
        await ApplyShrinkageAsync(c, tx, json, now, force: true);
        await Outbox.EnqueueEventAsync(c, tx, now, SuiteEvents.RecordUpserted, new WmsRecord(WmsRecordKinds.Shrinkage, data.Id.ToString(), now, json),
            subjectType: "shrinkage", subjectId: data.Id.ToString());
    }

    internal static async Task<bool> ApplyReturnAsync(SqliteConnection c, SqliteTransaction tx, JsonElement json, DateTimeOffset at, bool force = false)
    {
        var d = json.Deserialize<ReturnData>(VortexJson.Options)!;
        var current = await c.ScalarAsync<string?>("SELECT updated_at FROM stock_returns WHERE id = $id", tx, ("$id", d.Id));
        if (!force && current is not null && string.CompareOrdinal(current, SqliteExtensions.Time(at)) >= 0)
            return false;
        await c.ExecAsync("""
            INSERT OR REPLACE INTO stock_returns (id, number, kind, site_id, party_id, order_id, data, created_at, updated_at)
            VALUES ($id, $number, $kind, $site, $party, $order, $data, $created, $at)
            """, tx, ("$id", d.Id), ("$number", d.Number), ("$kind", d.Kind), ("$site", d.SiteId), ("$party", d.PartyId), ("$order", d.OrderId),
            ("$data", json.GetRawText()), ("$created", d.CreatedAt), ("$at", at));
        return true;
    }

    internal static async Task<bool> ApplyShrinkageAsync(SqliteConnection c, SqliteTransaction tx, JsonElement json, DateTimeOffset at, bool force = false)
    {
        var d = json.Deserialize<ShrinkageData>(VortexJson.Options)!;
        var current = await c.ScalarAsync<string?>("SELECT updated_at FROM shrinkage WHERE id = $id", tx, ("$id", d.Id));
        if (!force && current is not null && string.CompareOrdinal(current, SqliteExtensions.Time(at)) >= 0)
            return false;
        await c.ExecAsync("""
            INSERT OR REPLACE INTO shrinkage (id, site_id, item_id, status, data, created_at, updated_at)
            VALUES ($id, $site, $item, $status, $data, $created, $at)
            """, tx, ("$id", d.Id), ("$site", d.SiteId), ("$item", d.ItemId), ("$status", d.Status), ("$data", json.GetRawText()),
            ("$created", d.ReportedAt), ("$at", at));
        return true;
    }
}
