using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using VortexApi.Client;
using VortexWms.Core.Data;
using VortexWms.Core.Domain;
using VortexWms.Core.Integration;
using VortexWms.Core.Sync;

using static VortexWms.Core.Texts.CoreText;

namespace VortexWms.Core.Services;

/// <summary>Cómo se calcula la sugerencia de compra.</summary>
/// <param name="HistoryDays">Días hacia atrás para el consumo promedio (ventas y despachos menos devoluciones).</param>
/// <param name="LeadTimeDays">Lo que tarda el proveedor: sin mínimo, se pide cuando el stock no alcanza para esos días.</param>
/// <param name="CoverageDays">Sin máximo, se pide para cubrir el plazo del proveedor más estos días.</param>
public sealed record SupplyParameters(int HistoryDays = 30, int LeadTimeDays = 7, int CoverageDays = 30)
{
    public SupplyParameters Clamped() => new(Math.Clamp(HistoryDays, 7, 365), Math.Clamp(LeadTimeDays, 0, 180), Math.Clamp(CoverageDays, 1, 365));
}

/// <summary>
/// Cobertura de un artículo en una sede y cuánto pedir. <c>Position</c> = disponible + por recibir + ya pedido; si no
/// llega al punto de pedido (el mínimo, o el consumo del plazo del proveedor) se sugiere completar hasta el máximo (o el
/// consumo del plazo más la cobertura).
/// </summary>
/// <param name="Incoming">Por recibir en recepciones esperadas.</param>
/// <param name="Requested">En solicitudes de compra abiertas que aún no llegaron.</param>
/// <param name="UnitCost">Costo promedio; null para quien no ve costos.</param>
public sealed record SupplyRow(Guid SiteId, Guid ItemId, string Sku, string ItemName, string Unit, string? UnitCode, string? Category,
    Guid? SupplierId, string? SupplierName, decimal OnHand, decimal Reserved, decimal Incoming, decimal Requested, decimal? Minimum, decimal? Maximum,
    decimal DailyUsage, decimal? CoverageDays, decimal? ReorderLevel, decimal Suggested, decimal? UnitCost)
{
    public decimal Available => OnHand - Reserved;
    public decimal Position => Available + Incoming + Requested;
}

public sealed record PurchaseRequestLineDraft(Guid ItemId, decimal Quantity);

public sealed record PurchaseRequestDraft(Guid SiteId, Guid? SupplierId, IReadOnlyList<PurchaseRequestLineDraft> Lines, DateOnly? NeededBy = null,
    string? Notes = null);

public static class PurchaseRequestStatuses
{
    /// <summary>Enviada al ERP; su cantidad cuenta como "ya pedido" hasta que se recibe mercadería del artículo.</summary>
    public const string Open = "open";

    /// <summary>Cerrada a mano (se compró de otra forma o ya no hace falta).</summary>
    public const string Closed = "closed";
}

public sealed record PurchaseRequestRow(Guid Id, string Number, Guid SiteId, string? SiteName, Guid? SupplierId, string? SupplierName, string Status,
    DateOnly? NeededBy, string? Notes, IReadOnlyList<PurchaseRequestedLine> Lines, DateTimeOffset CreatedAt, string? CreatedBy);

/// <summary>
/// Abastecimiento (fase 6a): mínimo y máximo por sede, consumo promedio, días de cobertura y sugerencia de compra por
/// proveedor. La sugerencia sale al ERP como <c>purchase.requested</c> (solicitud de compra por aprobar); sin ERP, se
/// exporta a CSV. El proveedor de cada artículo es el de su última recepción.
/// </summary>
public sealed class SupplyService(WmsDatabase db, IWmsContext context, TimeProvider clock) : WmsService(db, context, clock)
{
    public const string Prefix = "SC";

    /// <summary>Movimientos que cuentan como consumo (salen con signo negativo; las devoluciones de venta restan).</summary>
    private static readonly string[] UsageKinds =
        [MovementKinds.Shipment, MovementKinds.Sale, MovementKinds.SaleVoid, MovementKinds.SaleReturn, MovementKinds.CustomerReturn];

    /// <param name="onlyToOrder">Solo lo que hay que pedir.</param>
    public async Task<IReadOnlyList<SupplyRow>> GetCoverageAsync(Guid siteId, SupplyParameters? parameters = null, bool onlyToOrder = false,
        CancellationToken ct = default)
    {
        await using var c = await Db.OpenAsync(ct);
        await RequireSiteAccessAsync(c, null, siteId);
        var rows = await CoverageAsync(c, null, siteId, (parameters ?? new()).Clamped());
        if (!Context.Can(WmsPermissions.ViewCosts))
            rows = rows.Select(r => r with { UnitCost = null }).ToList();
        return onlyToOrder ? rows.Where(r => r.Suggested > 0).ToList() : rows;
    }

    private async Task<List<SupplyRow>> CoverageAsync(SqliteConnection c, SqliteTransaction? tx, Guid siteId, SupplyParameters p)
    {
        var since = SqliteExtensions.Time(Clock.GetUtcNow().AddDays(-p.HistoryDays));
        var kinds = string.Join(", ", UsageKinds.Select(k => $"'{k}'"));
        var rows = await c.QueryAsync($"""
            SELECT s.item_id, i.sku, i.name, i.unit, i.unit_code, i.category, s.on_hand, s.reserved, s.reorder_point, s.max_stock, s.avg_cost,
                   COALESCE((SELECT -SUM(m.quantity) FROM movements m
                             WHERE m.site_id = s.site_id AND m.item_id = s.item_id AND m.kind IN ({kinds}) AND m.occurred_at >= $since), 0) AS used,
                   COALESCE((SELECT SUM(MAX(l.expected_qty - l.received_qty, 0)) FROM receipt_lines l JOIN receipts r ON r.id = l.receipt_id
                             WHERE r.site_id = s.site_id AND l.item_id = s.item_id AND r.status = '{ReceiptStatuses.Expected}'), 0) AS incoming,
                   (SELECT r.supplier_id FROM receipts r JOIN receipt_lines l ON l.receipt_id = r.id
                    WHERE l.item_id = s.item_id AND r.supplier_id IS NOT NULL AND r.status = '{ReceiptStatuses.Received}'
                    ORDER BY CASE WHEN r.site_id = s.site_id THEN 0 ELSE 1 END, r.received_at DESC LIMIT 1) AS supplier_id
            FROM stock s JOIN items i ON i.id = s.item_id
            WHERE s.site_id = $site AND i.is_deleted = 0 AND i.is_active = 1 AND i.track_inventory = 1
            ORDER BY i.name COLLATE NOCASE
            """, r => new
        {
            ItemId = r.Guid("item_id"), Sku = r.Str("sku"), Name = r.Str("name"), Unit = r.Str("unit"), UnitCode = r.StrOrNull("unit_code"),
            Category = r.StrOrNull("category"), OnHand = r.Dec("on_hand"), Reserved = r.Dec("reserved"), Min = r.DecOrNull("reorder_point"),
            Max = r.DecOrNull("max_stock"), AvgCost = r.StrOrNull("avg_cost"), Used = r.Dec("used"), Incoming = r.Dec("incoming"),
            SupplierId = r.GuidOrNull("supplier_id"),
        }, tx, ("$site", siteId), ("$since", since));

        var requested = await RequestedAsync(c, tx, siteId);
        var suppliers = await c.QueryAsync("SELECT id, name FROM parties", r => (Id: r.Guid("id"), Name: r.Str("name")), tx);
        var names = suppliers.ToDictionary(x => x.Id, x => x.Name);
        return rows.Select(r =>
        {
            var daily = Math.Round(Math.Max(r.Used, 0) / p.HistoryDays, 4);
            var available = r.OnHand - r.Reserved;
            var pending = requested.GetValueOrDefault(r.ItemId);
            var position = available + r.Incoming + pending;
            decimal? coverage = daily > 0 ? Math.Round(Math.Max(available, 0) / daily, 1) : null;
            decimal? reorder = r.Min ?? (daily > 0 ? Math.Round(daily * p.LeadTimeDays, 2) : null);
            var target = r.Max ?? Math.Max(r.Min ?? 0, daily * (p.LeadTimeDays + p.CoverageDays));
            var suggested = reorder is { } level && position <= level ? Math.Max(0, Math.Ceiling(target - position)) : 0;
            decimal? cost = decimal.TryParse(r.AvgCost, NumberStyles.Number, CultureInfo.InvariantCulture, out var avg) ? avg : null;
            return new SupplyRow(siteId, r.ItemId, r.Sku, r.Name, r.Unit, r.UnitCode, r.Category, r.SupplierId,
                r.SupplierId is { } s ? names.GetValueOrDefault(s) : null, r.OnHand, r.Reserved, r.Incoming, pending, r.Min, r.Max, daily, coverage, reorder,
                suggested, cost);
        }).ToList();
    }

    /// <summary>
    /// Lo pedido en solicitudes abiertas de la sede, por artículo. Una línea deja de contar cuando llega mercadería del
    /// artículo a la sede después de la solicitud (la recepción de esa compra).
    /// </summary>
    private static async Task<Dictionary<Guid, decimal>> RequestedAsync(SqliteConnection c, SqliteTransaction? tx, Guid siteId)
    {
        var open = await c.QueryAsync("SELECT data FROM purchase_requests WHERE site_id = $site AND status = $open",
            r => JsonSerializer.Deserialize<RequestData>(r.Str("data"), VortexJson.Options)!, tx, ("$site", siteId), ("$open", PurchaseRequestStatuses.Open));
        var result = new Dictionary<Guid, decimal>();
        foreach (var request in open)
            foreach (var line in request.Lines)
            {
                var arrived = await c.ScalarAsync<long>($"""
                    SELECT COUNT(*) FROM movements WHERE site_id = $site AND item_id = $item AND kind = '{MovementKinds.Receipt}' AND occurred_at > $at
                    """, tx, ("$site", siteId), ("$item", line.ItemId), ("$at", SqliteExtensions.Time(request.CreatedAt)));
                if (arrived == 0)
                    result[line.ItemId] = result.GetValueOrDefault(line.ItemId) + line.Quantity;
            }
        return result;
    }

    /// <summary>La sugerencia (solo lo que hay que pedir) en CSV, agrupada por proveedor, para comprar sin ERP.</summary>
    public async Task<string> ExportSuggestionCsvAsync(Guid siteId, SupplyParameters? parameters = null, CancellationToken ct = default)
    {
        var rows = await GetCoverageAsync(siteId, parameters, onlyToOrder: true, ct);
        var costs = Context.Can(WmsPermissions.ViewCosts);
        var csv = new StringBuilder("proveedor,sku,articulo,unidad,disponible,por_recibir,pedido,minimo,maximo,consumo_diario,dias_cobertura,sugerido"
            + (costs ? ",costo_promedio\n" : "\n"));
        foreach (var r in rows.OrderBy(r => r.SupplierName ?? "￿", StringComparer.CurrentCultureIgnoreCase).ThenBy(r => r.ItemName))
        {
            csv.AppendJoin(',', Csv(r.SupplierName), Csv(r.Sku), Csv(r.ItemName), Csv(r.Unit), Num(r.Available), Num(r.Incoming), Num(r.Requested),
                Opt(r.Minimum), Opt(r.Maximum), Num(r.DailyUsage), Opt(r.CoverageDays), Num(r.Suggested));
            if (costs)
                csv.Append(',').Append(Opt(r.UnitCost));
            csv.Append('\n');
        }
        return csv.ToString();
    }

    /// <summary>
    /// Envía la sugerencia de un proveedor al ERP (<c>purchase.requested</c>) y la deja como solicitud abierta: su cantidad
    /// cuenta como ya pedida hasta que llegue la mercadería.
    /// </summary>
    public Task<string> RequestPurchaseAsync(PurchaseRequestDraft d, CancellationToken ct = default)
    {
        RequirePermission(WmsPermissions.RequestPurchase);
        var lines = d.Lines.Where(l => l.Quantity > 0).GroupBy(l => l.ItemId)
            .Select(g => new PurchaseRequestLineDraft(g.Key, g.Sum(l => l.Quantity))).ToList();
        Require(lines.Count > 0, T("error.solicitud_sin_lineas"));
        Require(d.Notes is null || d.Notes.Length <= 500, T("error.solicitud_notas"));
        return InTransactionAsync(async (c, tx, now) =>
        {
            await RequireSiteAccessAsync(c, tx, d.SiteId);
            if (d.SupplierId is { } supplier)
                Require(await c.ScalarAsync<long>("SELECT COUNT(*) FROM parties WHERE id = $id AND is_deleted = 0", tx, ("$id", supplier)) > 0,
                    T("error.proveedor_no_existe"));
            var coverage = (await CoverageAsync(c, tx, d.SiteId, new SupplyParameters())).ToDictionary(r => r.ItemId);
            var eventLines = new List<PurchaseRequestedLine>();
            foreach (var l in lines)
            {
                Require(coverage.TryGetValue(l.ItemId, out var row), T("error.articulo_sin_stock_en_sede"));
                eventLines.Add(new PurchaseRequestedLine(l.ItemId, l.Quantity, row!.UnitCode, row.UnitCost, row.OnHand, row.Minimum, row.Maximum,
                    row.DailyUsage, row.CoverageDays));
            }
            var id = Guid.NewGuid();
            var number = await DocumentNumbers.NextAsync(c, tx, Prefix, Context.DeviceTag);
            var notes = string.IsNullOrWhiteSpace(d.Notes) ? null : d.Notes.Trim();
            var data = new RequestData(id, number, d.SiteId, d.SupplierId, PurchaseRequestStatuses.Open, d.NeededBy, notes, eventLines, now, Context.UserName);
            await PublishAsync(c, tx, now, data);
            await Outbox.EnqueueEventAsync(c, tx, now, SuiteEvents.PurchaseRequested,
                new PurchaseRequested(id, number, d.SiteId, d.SupplierId, d.NeededBy, notes, Context.UserName, eventLines),
                subjectType: "purchase_request", subjectId: number, id: id);
            await AuditAsync(c, tx, now, "purchase.requested", "purchase_request", id, number,
                string.Join(", ", eventLines.Select(x => $"{x.Quantity:0.##}")));
            return number;
        }, ct);
    }

    /// <summary>Cierra una solicitud abierta: su cantidad deja de contar como ya pedida.</summary>
    public Task CloseRequestAsync(Guid requestId, CancellationToken ct = default)
    {
        RequirePermission(WmsPermissions.RequestPurchase);
        return InTransactionAsync(async (c, tx, now) =>
        {
            var json = await c.ScalarAsync<string?>("SELECT data FROM purchase_requests WHERE id = $id", tx, ("$id", requestId));
            Require(json is not null, T("error.solicitud_no_existe"));
            var data = JsonSerializer.Deserialize<RequestData>(json!, VortexJson.Options)!;
            await RequireSiteAccessAsync(c, tx, data.SiteId);
            if (data.Status == PurchaseRequestStatuses.Closed)
                return 0;
            await PublishAsync(c, tx, now, data with { Status = PurchaseRequestStatuses.Closed });
            await AuditAsync(c, tx, now, "purchase.request_closed", "purchase_request", requestId, data.Number, null);
            return 0;
        }, ct);
    }

    public async Task<IReadOnlyList<PurchaseRequestRow>> GetRequestsAsync(Guid? siteId = null, bool openOnly = false, int limit = 50,
        CancellationToken ct = default)
    {
        await using var c = await Db.OpenAsync(ct);
        var rows = await c.QueryAsync($"""
            SELECT r.data, s.name AS site, p.name AS supplier FROM purchase_requests r
            LEFT JOIN sites s ON s.id = r.site_id LEFT JOIN parties p ON p.id = r.supplier_id
            WHERE ($site IS NULL OR r.site_id = $site) AND ($open = 0 OR r.status = '{PurchaseRequestStatuses.Open}'){MySites("r.site_id")}
            ORDER BY r.created_at DESC LIMIT $limit
            """, r => (Data: JsonSerializer.Deserialize<RequestData>(r.Str("data"), VortexJson.Options)!, Site: r.StrOrNull("site"), Supplier: r.StrOrNull("supplier")),
            null, ("$site", siteId), ("$open", openOnly), ("$limit", limit));
        var costs = Context.Can(WmsPermissions.ViewCosts);
        return rows.Select(x => new PurchaseRequestRow(x.Data.Id, x.Data.Number, x.Data.SiteId, x.Site, x.Data.SupplierId, x.Supplier, x.Data.Status,
            x.Data.NeededBy, x.Data.Notes, costs ? x.Data.Lines : x.Data.Lines.Select(l => l with { UnitCost = null }).ToList(), x.Data.CreatedAt,
            x.Data.CreatedBy)).ToList();
    }

    // ---------- Registro replicado ----------

    internal sealed record RequestData(Guid Id, string Number, Guid SiteId, Guid? SupplierId, string Status, DateOnly? NeededBy, string? Notes,
        IReadOnlyList<PurchaseRequestedLine> Lines, DateTimeOffset CreatedAt, string? CreatedBy);

    private static async Task PublishAsync(SqliteConnection c, SqliteTransaction tx, DateTimeOffset now, RequestData data)
    {
        var json = JsonSerializer.SerializeToElement(data, VortexJson.Options);
        await ApplyRequestAsync(c, tx, json, now, force: true);
        await Outbox.EnqueueEventAsync(c, tx, now, SuiteEvents.RecordUpserted,
            new WmsRecord(WmsRecordKinds.PurchaseRequest, data.Id.ToString(), now, json), subjectType: "purchase_request", subjectId: data.Number);
    }

    internal static async Task<bool> ApplyRequestAsync(SqliteConnection c, SqliteTransaction tx, JsonElement json, DateTimeOffset at, bool force = false)
    {
        var d = json.Deserialize<RequestData>(VortexJson.Options)!;
        var current = await c.ScalarAsync<string?>("SELECT updated_at FROM purchase_requests WHERE id = $id", tx, ("$id", d.Id));
        if (!force && current is not null && string.CompareOrdinal(current, SqliteExtensions.Time(at)) >= 0)
            return false;
        await c.ExecAsync("""
            INSERT OR REPLACE INTO purchase_requests (id, number, site_id, supplier_id, status, data, created_at, updated_at)
            VALUES ($id, $number, $site, $supplier, $status, $data, $created, $at)
            """, tx, ("$id", d.Id), ("$number", d.Number), ("$site", d.SiteId), ("$supplier", d.SupplierId), ("$status", d.Status),
            ("$data", json.GetRawText()), ("$created", d.CreatedAt), ("$at", at));
        return true;
    }

    private static string Csv(string? value)
    {
        var v = value ?? "";
        if (v.Length > 0 && v[0] is '=' or '+' or '-' or '@')
            v = "'" + v;
        return v.IndexOfAny([',', '"', '\n', '\r']) >= 0 ? $"\"{v.Replace("\"", "\"\"")}\"" : v;
    }

    private static string Num(decimal value) => value.ToString("0.####", CultureInfo.InvariantCulture);

    private static string Opt(decimal? value) => value is { } v ? Num(v) : "";
}
