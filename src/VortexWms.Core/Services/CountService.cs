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

public static class CountStatuses
{
    /// <summary>Se está contando.</summary>
    public const string Open = "open";

    /// <summary>Todo contado (y recontado): espera la aprobación del supervisor.</summary>
    public const string Review = "review";
    public const string Approved = "approved";
    public const string Cancelled = "cancelled";

    public static bool IsActive(string status) => status is Open or Review;
}

/// <summary>Qué artículos entran a un conteo.</summary>
public static class CountScopes
{
    /// <summary>Los artículos elegidos.</summary>
    public const string Items = "items";

    /// <summary>Lo que está en unas ubicaciones (se congelan mientras se cuenta).</summary>
    public const string Locations = "locations";

    /// <summary>Los artículos de una clase ABC que ya toca contar.</summary>
    public const string Abc = "abc";

    /// <summary>Los artículos con ajustes (diferencias) en los últimos días.</summary>
    public const string Differences = "differences";

    /// <summary>Inventario general de la sede: todo, con todas las ubicaciones congeladas.</summary>
    public const string Full = "full";

    public static readonly string[] All = [Items, Locations, Abc, Differences, Full];
}

/// <param name="Abc">Clase a contar (A, B o C) con <see cref="CountScopes.Abc"/>.</param>
/// <param name="Days">Días hacia atrás con <see cref="CountScopes.Differences"/>.</param>
/// <param name="RecountPercent">Diferencia (en % de lo esperado) desde la que se pide recontar; null: no se pide.</param>
/// <param name="RecountUnits">O diferencia en unidades desde la que se pide recontar.</param>
public sealed record CountDraft(Guid SiteId, string Scope, bool Blind = true, IReadOnlyList<Guid>? ItemIds = null,
    IReadOnlyList<Guid>? LocationIds = null, string? Abc = null, int Days = 30, decimal? RecountPercent = 5, decimal? RecountUnits = null,
    Guid? AssigneeId = null, string? AssigneeName = null, string? Note = null, int MaxLines = 200);

/// <summary>Una línea: un artículo (y un lote, si se controla por lote) con lo que el sistema esperaba al empezar.</summary>
public sealed record CountLine(int LineNo, Guid ItemId, string? LotCode, decimal Expected, decimal UnitCost, IReadOnlyList<string> Locations);

public sealed record CountLineView(CountLine Line, string Sku, string ItemName, string Unit, decimal? Expected, decimal? First, decimal? Recount,
    string Status, decimal? Final, decimal? Difference);

public static class CountLineStatuses
{
    public const string Pending = "pending";
    public const string Recount = "recount";
    public const string Done = "done";
}

public sealed record CountRow(Guid Id, string Number, Guid SiteId, string SiteName, string Scope, bool Blind, string Status, int Lines,
    int Counted, int Differences, DateTimeOffset CreatedAt, string? CreatedBy, DateTimeOffset? ApprovedAt, string? ApprovedBy);

public sealed record CountView(CountRow Count, string? Note, decimal? RecountPercent, decimal? RecountUnits, IReadOnlyList<Guid> FrozenLocations,
    IReadOnlyList<CountLineView> Lines);

/// <summary>Exactitud de inventario: líneas contadas sin diferencia sobre el total, y cuánto valor cambió al ajustar.</summary>
public sealed record CountAccuracy(int Lines, int Exact, decimal Percent, decimal AdjustedValue, int Counts, string CurrencyCode = "PEN");

/// <summary>Clase ABC de un artículo en una sede por el valor que salió en los últimos 90 días.</summary>
public sealed record AbcRow(Guid ItemId, string Sku, string Name, decimal Value, string Class, DateTimeOffset? LastCounted, bool Due);

/// <summary>
/// Conteos cíclicos e inventario general (fase 5a): por artículos, por ubicaciones (que se congelan), por clase ABC (A cada
/// mes, B cada trimestre, C cada semestre), por artículos con diferencias o de toda la sede. El conteo puede ser ciego (el
/// operario no ve lo esperado); una diferencia sobre el umbral pide recontar; el ajuste solo se aplica cuando el supervisor
/// aprueba, por la diferencia con lo esperado al empezar (lo que se movió mientras tanto no se pierde).
/// <para>
/// Varios equipos: el conteo (cabecera y líneas) es un registro del WMS; cada conteo de una línea es un registro aparte
/// (<c>{conteo}:{línea}:{intento}</c>), así dos operarios que cuentan líneas distintas sin conexión suman los dos.
/// </para>
/// </summary>
public sealed class CountService(WmsDatabase db, IWmsContext context, TimeProvider clock) : WmsService(db, context, clock)
{
    public const string NumberPrefix = "CNT";

    /// <summary>Cada cuántos días toca contar cada clase.</summary>
    public static readonly IReadOnlyDictionary<string, int> AbcFrequencyDays = new Dictionary<string, int> { ["A"] = 30, ["B"] = 90, ["C"] = 180 };

    // ---------- Crear ----------

    public Task<Guid> CreateAsync(CountDraft d, CancellationToken ct = default)
    {
        RequirePermission(WmsPermissions.Adjust);
        Require(CountScopes.All.Contains(d.Scope), T("error.conteo_alcance"));
        Require(d.RecountPercent is null or >= 0 && d.RecountUnits is null or >= 0, T("error.conteo_umbral"));
        return InTransactionAsync(async (c, tx, now) =>
        {
            await RequireSiteAccessAsync(c, tx, d.SiteId);
            var items = await ScopeItemsAsync(c, tx, d, now);
            Require(items.Count > 0, T("error.conteo_sin_articulos"));
            items = items.Take(Math.Clamp(d.MaxLines, 1, 5000)).ToList();
            foreach (var item in items)
                if (await ActiveCountForAsync(c, tx, d.SiteId, item) is { } open)
                    throw new WmsValidationException(T("error.conteo_ya_abierto", await InventoryService.ItemLabelAsync(c, tx, item), open));

            var lines = await LinesAsync(c, tx, d.SiteId, items);
            var frozen = d.Scope switch
            {
                CountScopes.Full => await c.QueryAsync("SELECT id FROM locations WHERE site_id = $s AND is_active = 1", r => r.Guid("id"), tx, ("$s", d.SiteId)),
                CountScopes.Locations => d.LocationIds?.Distinct().ToList() ?? [],
                _ => [],
            };
            var id = Guid.NewGuid();
            var number = await DocumentNumbers.NextAsync(c, tx, NumberPrefix, Context.DeviceTag);
            var data = new CountData(id, number, d.SiteId, d.Scope, d.Blind, d.RecountPercent, d.RecountUnits, CountStatuses.Open, Clean(d.Note),
                frozen, lines, now, Context.UserName, null, null);
            await PublishAsync(c, tx, now, data);
            if (d.AssigneeId is { } assignee)
                await TaskService.CreateForDocumentAsync(c, tx, now, Context.UserName, d.SiteId, TaskKinds.Count, id, assignee, Clean(d.AssigneeName),
                    T("conteo.tarea", number, lines.Count), Clean(d.Note));
            await AuditAsync(c, tx, now, "count.created", "count", id, number, $"{d.Scope}: {lines.Count} líneas{(d.Blind ? ", ciego" : "")}");
            return id;
        }, ct);
    }

    /// <summary>Los artículos que entran según el alcance (sin los que se controlan por serie: esos se cuentan escaneando).</summary>
    private async Task<List<Guid>> ScopeItemsAsync(SqliteConnection c, SqliteTransaction tx, CountDraft d, DateTimeOffset now)
    {
        const string noSerial = "AND COALESCE(i.tracking, 'None') <> 'Serial'";
        switch (d.Scope)
        {
            case CountScopes.Items:
                return d.ItemIds?.Distinct().ToList() ?? [];
            case CountScopes.Locations:
                Require(d.LocationIds is { Count: > 0 }, T("error.conteo_sin_ubicaciones"));
                return (await c.QueryAsync($"""
                    SELECT DISTINCT s.item_id FROM location_stock s JOIN items i ON i.id = s.item_id
                    WHERE s.site_id = $site AND s.quantity > 0 AND s.location_id IN (SELECT value FROM json_each($locs)) {noSerial}
                    UNION SELECT l.fixed_item_id FROM locations l JOIN items i ON i.id = l.fixed_item_id
                    WHERE l.id IN (SELECT value FROM json_each($locs)) {noSerial}
                    """, r => r.Guid("item_id"), tx, ("$site", d.SiteId),
                    ("$locs", JsonSerializer.Serialize(d.LocationIds!.Select(x => x.ToString()))))).Distinct().ToList();
            case CountScopes.Abc:
                Require(d.Abc is "A" or "B" or "C", T("error.conteo_clase"));
                return (await AbcAsync(c, tx, d.SiteId, now)).Where(a => a.Class == d.Abc && a.Due).Select(a => a.ItemId).ToList();
            case CountScopes.Differences:
                return await c.QueryAsync($"""
                    SELECT DISTINCT m.item_id FROM movements m JOIN items i ON i.id = m.item_id
                    WHERE m.site_id = $site AND m.kind = 'adjustment' AND m.occurred_at >= $since {noSerial}
                    """, r => r.Guid("item_id"), tx, ("$site", d.SiteId), ("$since", now.AddDays(-Math.Max(1, d.Days))));
            default:
                return await c.QueryAsync($"""
                    SELECT s.item_id FROM stock s JOIN items i ON i.id = s.item_id
                    WHERE s.site_id = $site AND (s.on_hand <> 0 OR i.is_active = 1) {noSerial} ORDER BY i.sku
                    """, r => r.Guid("item_id"), tx, ("$site", d.SiteId));
        }
    }

    /// <summary>Una línea por artículo; los que tienen lote, una por lote con saldo (más una sin lote si no tiene ninguno).</summary>
    private static async Task<List<CountLine>> LinesAsync(SqliteConnection c, SqliteTransaction tx, Guid siteId, IReadOnlyList<Guid> items)
    {
        var lines = new List<CountLine>();
        foreach (var item in items)
        {
            var info = (await c.QueryAsync("SELECT i.tracking, s.on_hand, s.avg_cost FROM items i LEFT JOIN stock s ON s.item_id = i.id AND s.site_id = $site WHERE i.id = $id",
                r => (Tracking: r.StrOrNull("tracking") ?? ItemTrackings.None, OnHand: r.DecOrNull("on_hand") ?? 0,
                    Cost: decimal.TryParse(r.StrOrNull("avg_cost"), NumberStyles.Number, CultureInfo.InvariantCulture, out var cost) ? cost : 0),
                tx, ("$site", siteId), ("$id", item))).FirstOrDefault();
            if (info == default)
                continue;
            var locations = await c.QueryAsync("""
                SELECT l.code FROM location_stock s JOIN locations l ON l.id = s.location_id
                WHERE s.site_id = $site AND s.item_id = $item AND s.quantity > 0 ORDER BY l.code
                """, r => r.Str("code"), tx, ("$site", siteId), ("$item", item));
            if (info.Tracking == ItemTrackings.Lot)
            {
                var lots = await c.QueryAsync("SELECT lot_code, quantity FROM stock_lots WHERE site_id = $s AND item_id = $i AND quantity > 0 ORDER BY lot_code",
                    r => (Lot: r.Str("lot_code"), Qty: r.Dec("quantity")), tx, ("$s", siteId), ("$i", item));
                foreach (var lot in lots)
                    lines.Add(new CountLine(lines.Count + 1, item, lot.Lot, lot.Qty, info.Cost, locations));
                if (lots.Count > 0)
                    continue;
            }
            lines.Add(new CountLine(lines.Count + 1, item, null, info.OnHand, info.Cost, locations));
        }
        return lines;
    }

    // ---------- Contar ----------

    /// <summary>
    /// Registra lo contado en una línea. La primera vez es el conteo; si la diferencia con lo esperado pasa el umbral, la
    /// línea pide un recuento y el siguiente registro lo es. Un conteo en revisión vuelve a abrirse si se corrige una línea.
    /// </summary>
    public Task RecordAsync(Guid countId, int lineNo, decimal quantity, CancellationToken ct = default)
    {
        RequirePermission(WmsPermissions.Count);
        Require(quantity >= 0, T("error.el_conteo_no_puede_ser"));
        return InTransactionAsync(async (c, tx, now) =>
        {
            var count = await CountAsync(c, tx, countId);
            Require(CountStatuses.IsActive(count.Status), T("error.conteo_cerrado"));
            var line = count.Lines.FirstOrDefault(l => l.LineNo == lineNo);
            Require(line is not null, T("error.conteo_linea"));
            var results = await ResultsAsync(c, tx, countId);
            var (first, recount) = Attempts(results, lineNo);
            // El primer registro es el conteo; si su diferencia pide recontar, los siguientes son el recuento (el último vale).
            var attempt = first is not null && NeedsRecount(count, line!, first.Value) ? 2 : 1;
            var id = $"{countId}:{lineNo}:{attempt}";
            await PublishResultAsync(c, tx, now, new CountResultData(id, countId, lineNo, attempt, quantity, Context.UserName, now));
            if (count.Status == CountStatuses.Review)
                await PublishAsync(c, tx, now, count with { Status = CountStatuses.Open });
            return 0;
        }, ct);
    }

    /// <summary>Todo contado y recontado: queda para que el supervisor lo apruebe.</summary>
    public Task SubmitAsync(Guid countId, CancellationToken ct = default)
    {
        RequirePermission(WmsPermissions.Count);
        return InTransactionAsync(async (c, tx, now) =>
        {
            var count = await CountAsync(c, tx, countId);
            Require(count.Status == CountStatuses.Open, T("error.conteo_cerrado"));
            var lines = Evaluate(count, await ResultsAsync(c, tx, countId));
            Require(lines.All(l => l.Status == CountLineStatuses.Done), T("error.conteo_incompleto", lines.Count(l => l.Status != CountLineStatuses.Done)));
            await PublishAsync(c, tx, now, count with { Status = CountStatuses.Review });
            return 0;
        }, ct);
    }

    /// <summary>
    /// El supervisor aprueba: cada diferencia se ajusta (motivo conteo) por lo contado menos lo que se esperaba al empezar, así lo
    /// que entró o salió durante el conteo no se pierde. Las ubicaciones quedan libres.
    /// </summary>
    public Task<int> ApproveAsync(Guid countId, CancellationToken ct = default)
    {
        RequirePermission(WmsPermissions.ApproveCounts);
        return InTransactionAsync(async (c, tx, now) =>
        {
            var count = await CountAsync(c, tx, countId);
            Require(count.Status == CountStatuses.Review, T("error.conteo_no_en_revision"));
            var lines = Evaluate(count, await ResultsAsync(c, tx, countId));
            var adjusted = 0;
            foreach (var l in lines.Where(l => l.Difference is { } diff && diff != 0))
            {
                var diff = l.Difference!.Value;
                var onHand = await StockLedger.OnHandAsync(c, tx, count.SiteId, l.Line.ItemId);
                // Si mientras tanto salió más de lo que había, el ajuste no deja el stock en negativo.
                var change = Math.Max(diff, -onHand);
                if (change == 0)
                    continue;
                await StockLedger.ApplyAsync(c, tx, now, Context.UserName, new StockLedger.Entry(count.SiteId, l.Line.ItemId, change,
                    MovementKinds.Adjustment, AdjustmentReasons.Count, DocumentId: countId, Reference: count.Number,
                    Note: T("conteo.ajuste", count.Number, l.Final, l.Line.Expected),
                    MovementId: DeterministicId.From("count_adjustment", countId, new Guid(l.Line.LineNo, 0, 0, new byte[8])),
                    Lots: l.Line.LotCode is { } lot ? [new LotQuantity(lot, Math.Abs(change))] : null, LotMode: StockLedger.LotMode.Lenient,
                    AllowFefoOverride: true, AllowUnavailableLots: true));
                adjusted++;
            }
            await PublishAsync(c, tx, now, count with { Status = CountStatuses.Approved, ApprovedAt = now, ApprovedBy = Context.UserName });
            await TaskService.CloseOpenAsync(c, tx, now, Context.UserName, TaskKinds.Count, orderId: countId);
            await AuditAsync(c, tx, now, "count.approved", "count", countId, count.Number,
                $"{lines.Count} líneas, {lines.Count(l => l.Difference is not 0)} con diferencia, {adjusted} ajustes");
            return adjusted;
        }, ct);
    }

    public Task CancelAsync(Guid countId, CancellationToken ct = default)
    {
        RequirePermission(WmsPermissions.Adjust);
        return InTransactionAsync(async (c, tx, now) =>
        {
            var count = await CountAsync(c, tx, countId);
            Require(CountStatuses.IsActive(count.Status), T("error.conteo_cerrado"));
            await PublishAsync(c, tx, now, count with { Status = CountStatuses.Cancelled });
            await TaskService.CloseOpenAsync(c, tx, now, Context.UserName, TaskKinds.Count, orderId: countId, status: TaskStatuses.Cancelled);
            await AuditAsync(c, tx, now, "count.cancelled", "count", countId, count.Number, null);
            return 0;
        }, ct);
    }

    // ---------- Consultar ----------

    public async Task<IReadOnlyList<CountRow>> GetCountsAsync(Guid? siteId = null, bool activeOnly = false, CancellationToken ct = default)
    {
        await using var c = await Db.OpenAsync(ct);
        var counts = await c.QueryAsync($"""
            SELECT data FROM counts WHERE ($site IS NULL OR site_id = $site) AND ($active = 0 OR status IN ('open', 'review'))
             {MySites("site_id")} ORDER BY created_at DESC LIMIT 200
            """, r => JsonSerializer.Deserialize<CountData>(r.Str("data"), VortexJson.Options)!, null, ("$site", siteId), ("$active", activeOnly));
        var rows = new List<CountRow>();
        foreach (var count in counts)
            rows.Add(Row(count, Evaluate(count, await ResultsAsync(c, null, count.Id)), await SiteNameAsync(c, count.SiteId)));
        return rows;
    }

    /// <summary>El conteo con sus líneas. En un conteo ciego abierto, quien no aprueba no ve lo esperado ni la diferencia.</summary>
    public async Task<CountView?> GetAsync(Guid countId, CancellationToken ct = default)
    {
        await using var c = await Db.OpenAsync(ct);
        var count = await c.FirstOrDefaultAsync("SELECT data FROM counts WHERE id = $id",
            r => JsonSerializer.Deserialize<CountData>(r.Str("data"), VortexJson.Options)!, null, ("$id", countId));
        if (count is null)
            return null;
        var lines = Evaluate(count, await ResultsAsync(c, null, countId));
        var hide = count.Blind && CountStatuses.IsActive(count.Status) && !Context.Can(WmsPermissions.ApproveCounts);
        var items = (await c.QueryAsync("SELECT id, sku, name, unit FROM items WHERE id IN (SELECT value FROM json_each($ids))",
                r => (Id: r.Guid("id"), Sku: r.Str("sku"), Name: r.Str("name"), Unit: r.Str("unit")), null,
                ("$ids", JsonSerializer.Serialize(count.Lines.Select(l => l.ItemId.ToString()).Distinct()))))
            .ToDictionary(x => x.Id);
        var views = lines.Select(l =>
        {
            var item = items.GetValueOrDefault(l.Line.ItemId);
            return new CountLineView(l.Line, item.Sku ?? "", item.Name ?? "?", item.Unit ?? "", hide ? null : l.Line.Expected, l.First, l.Recount,
                l.Status, l.Final, hide ? null : l.Difference);
        }).ToList();
        return new CountView(Row(count, lines, await SiteNameAsync(c, count.SiteId)), count.Note, count.RecountPercent, count.RecountUnits,
            count.Frozen, views);
    }

    /// <summary>Exactitud de inventario de los conteos aprobados en los últimos días (de una sede o de todas).</summary>
    public async Task<CountAccuracy> GetAccuracyAsync(Guid? siteId = null, int days = 90, CancellationToken ct = default)
    {
        await using var c = await Db.OpenAsync(ct);
        var since = SqliteExtensions.Time(Clock.GetUtcNow().AddDays(-days));
        var counts = await c.QueryAsync($"""
            SELECT data FROM counts WHERE status = 'approved' AND ($site IS NULL OR site_id = $site) AND created_at >= $since{MySites("site_id")}
            """, r => JsonSerializer.Deserialize<CountData>(r.Str("data"), VortexJson.Options)!, null, ("$site", siteId), ("$since", since));
        int lines = 0, exact = 0;
        decimal value = 0;
        foreach (var count in counts)
        {
            foreach (var l in Evaluate(count, await ResultsAsync(c, null, count.Id)))
            {
                lines++;
                if (l.Difference is 0 or null)
                    exact++;
                value += (l.Difference ?? 0) * l.Line.UnitCost;
            }
        }
        return new CountAccuracy(lines, exact, lines == 0 ? 100 : Math.Round(exact * 100m / lines, 1), Math.Round(value, 2), counts.Count,
            (await Valuation.CurrencyAsync(c, null)).Code);
    }

    /// <summary>
    /// Clase ABC de los artículos de la sede por el valor (costo promedio) de lo que salió en 90 días: A los que entran antes de
    /// juntar el 80 % del valor, B hasta el 95 %, C el resto (y lo que no salió). <c>Due</c>: toca contarlo (nunca se contó o pasó su frecuencia).
    /// </summary>
    public async Task<IReadOnlyList<AbcRow>> GetAbcAsync(Guid siteId, CancellationToken ct = default)
    {
        await using var c = await Db.OpenAsync(ct);
        return await AbcAsync(c, null, siteId, Clock.GetUtcNow());
    }

    private static async Task<List<AbcRow>> AbcAsync(SqliteConnection c, SqliteTransaction? tx, Guid siteId, DateTimeOffset now)
    {
        var rows = await c.QueryAsync("""
            SELECT i.id, i.sku, i.name, s.avg_cost,
                   COALESCE((SELECT -SUM(m.quantity) FROM movements m WHERE m.site_id = $site AND m.item_id = i.id AND m.quantity < 0
                             AND m.kind IN ('shipment', 'sale', 'transfer_out') AND m.occurred_at >= $since), 0) AS out_qty
            FROM stock s JOIN items i ON i.id = s.item_id
            WHERE s.site_id = $site AND COALESCE(i.tracking, 'None') <> 'Serial' AND (s.on_hand <> 0 OR i.is_active = 1)
            """, r => (Id: r.Guid("id"), Sku: r.Str("sku"), Name: r.Str("name"),
                Value: r.Dec("out_qty") * (decimal.TryParse(r.StrOrNull("avg_cost"), NumberStyles.Number, CultureInfo.InvariantCulture, out var cost) ? cost : 0)),
            tx, ("$site", siteId), ("$since", now.AddDays(-90)));
        var lastCounted = new Dictionary<Guid, DateTimeOffset>();
        foreach (var count in await c.QueryAsync("SELECT data FROM counts WHERE site_id = $site AND status = 'approved'",
                     r => JsonSerializer.Deserialize<CountData>(r.Str("data"), VortexJson.Options)!, tx, ("$site", siteId)))
            foreach (var l in count.Lines)
                if (!lastCounted.TryGetValue(l.ItemId, out var at) || at < count.ApprovedAt)
                    lastCounted[l.ItemId] = count.ApprovedAt ?? count.CreatedAt;

        var total = rows.Sum(r => r.Value);
        decimal running = 0;
        var result = new List<AbcRow>();
        foreach (var r in rows.OrderByDescending(r => r.Value).ThenBy(r => r.Sku, StringComparer.Ordinal))
        {
            // La clase sale de lo acumulado antes del artículo: el primero siempre es A.
            var before = total == 0 ? 1 : running / total;
            running += r.Value;
            var cls = r.Value <= 0 ? "C" : before < 0.80m ? "A" : before < 0.95m ? "B" : "C";
            var last = lastCounted.TryGetValue(r.Id, out var lc) ? lc : (DateTimeOffset?)null;
            result.Add(new AbcRow(r.Id, r.Sku, r.Name, Math.Round(r.Value, 2), cls, last,
                last is null || last.Value.AddDays(AbcFrequencyDays[cls]) <= now));
        }
        return result;
    }

    // ---------- Congelamiento ----------

    /// <summary>La ubicación está en un conteo abierto: no se traslada ni reabastece desde o hacia ella hasta que se cierre.</summary>
    internal static async Task<string?> FrozenByAsync(SqliteConnection c, SqliteTransaction? tx, Guid locationId) =>
        await c.ScalarAsync<string?>("""
            SELECT number FROM counts WHERE status IN ('open', 'review') AND EXISTS (SELECT 1 FROM json_each(frozen) WHERE value = $loc)
            LIMIT 1
            """, tx, ("$loc", locationId.ToString()));

    private static Task<string?> ActiveCountForAsync(SqliteConnection c, SqliteTransaction tx, Guid siteId, Guid itemId) =>
        c.ScalarAsync<string?>("""
            SELECT number FROM counts WHERE site_id = $site AND status IN ('open', 'review')
              AND EXISTS (SELECT 1 FROM json_each(data, '$.lines') WHERE json_extract(value, '$.itemId') = $item) LIMIT 1
            """, tx, ("$site", siteId), ("$item", itemId.ToString()));

    // ---------- Evaluación de las líneas ----------

    private sealed record Evaluated(CountLine Line, decimal? First, decimal? Recount, string Status, decimal? Final, decimal? Difference);

    private static List<Evaluated> Evaluate(CountData count, IReadOnlyList<CountResultData> results) =>
        count.Lines.Select(line =>
        {
            var (first, recount) = Attempts(results, line.LineNo);
            if (first is null)
                return new Evaluated(line, null, null, CountLineStatuses.Pending, null, null);
            if (recount is null && NeedsRecount(count, line, first.Value))
                return new Evaluated(line, first, null, CountLineStatuses.Recount, null, null);
            var final = recount ?? first.Value;
            return new Evaluated(line, first, recount, CountLineStatuses.Done, final, final - line.Expected);
        }).ToList();

    private static (decimal? First, decimal? Recount) Attempts(IReadOnlyList<CountResultData> results, int lineNo) =>
        (results.Where(r => r.LineNo == lineNo && r.Attempt == 1).MaxBy(r => r.CountedAt)?.Quantity,
         results.Where(r => r.LineNo == lineNo && r.Attempt == 2).MaxBy(r => r.CountedAt)?.Quantity);

    private static bool NeedsRecount(CountData count, CountLine line, decimal counted)
    {
        var diff = Math.Abs(counted - line.Expected);
        if (diff == 0)
            return false;
        var byPercent = count.RecountPercent is { } pct && (line.Expected == 0 || diff * 100 / Math.Abs(line.Expected) > pct);
        var byUnits = count.RecountUnits is { } units && diff > units;
        return byPercent || byUnits;
    }

    private static CountRow Row(CountData d, IReadOnlyList<Evaluated> lines, string site) => new(d.Id, d.Number, d.SiteId, site, d.Scope, d.Blind,
        d.Status, lines.Count, lines.Count(l => l.Status == CountLineStatuses.Done), lines.Count(l => l.Difference is not null and not 0), d.CreatedAt,
        d.CreatedBy, d.ApprovedAt, d.ApprovedBy);

    private static async Task<string> SiteNameAsync(SqliteConnection c, Guid siteId) =>
        await c.ScalarAsync<string?>("SELECT name FROM sites WHERE id = $id", null, ("$id", siteId)) ?? "?";

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    // ---------- Registros replicados ----------

    public sealed record CountData(Guid Id, string Number, Guid SiteId, string Scope, bool Blind, decimal? RecountPercent, decimal? RecountUnits,
        string Status, string? Note, IReadOnlyList<Guid> Frozen, IReadOnlyList<CountLine> Lines, DateTimeOffset CreatedAt, string? CreatedBy,
        DateTimeOffset? ApprovedAt, string? ApprovedBy);

    public sealed record CountResultData(string Id, Guid CountId, int LineNo, int Attempt, decimal Quantity, string? CountedBy, DateTimeOffset CountedAt);

    private static async Task<CountData> CountAsync(SqliteConnection c, SqliteTransaction tx, Guid id) =>
        await c.FirstOrDefaultAsync("SELECT data FROM counts WHERE id = $id",
            r => JsonSerializer.Deserialize<CountData>(r.Str("data"), VortexJson.Options)!, tx, ("$id", id))
        ?? throw new WmsValidationException(T("error.conteo_no_existe"));

    private static Task<List<CountResultData>> ResultsAsync(SqliteConnection c, SqliteTransaction? tx, Guid countId) =>
        c.QueryAsync("SELECT data FROM count_results WHERE count_id = $id",
            r => JsonSerializer.Deserialize<CountResultData>(r.Str("data"), VortexJson.Options)!, tx, ("$id", countId));

    private static async Task PublishAsync(SqliteConnection c, SqliteTransaction tx, DateTimeOffset now, CountData data)
    {
        var json = JsonSerializer.SerializeToElement(data, VortexJson.Options);
        await ApplyCountAsync(c, tx, json, now, force: true);
        await Outbox.EnqueueEventAsync(c, tx, now, SuiteEvents.RecordUpserted, new WmsRecord(WmsRecordKinds.Count, data.Id.ToString(), now, json),
            subjectType: "count", subjectId: data.Number);
    }

    private static async Task PublishResultAsync(SqliteConnection c, SqliteTransaction tx, DateTimeOffset now, CountResultData data)
    {
        var json = JsonSerializer.SerializeToElement(data, VortexJson.Options);
        await ApplyResultAsync(c, tx, json, now, force: true);
        await Outbox.EnqueueEventAsync(c, tx, now, SuiteEvents.RecordUpserted, new WmsRecord(WmsRecordKinds.CountResult, data.Id, now, json),
            subjectType: "count_result", subjectId: data.Id);
    }

    internal static async Task<bool> ApplyCountAsync(SqliteConnection c, SqliteTransaction tx, JsonElement json, DateTimeOffset updatedAt,
        bool force = false)
    {
        var d = json.Deserialize<CountData>(VortexJson.Options)!;
        var current = await c.ScalarAsync<string?>("SELECT updated_at FROM counts WHERE id = $id", tx, ("$id", d.Id));
        if (!force && current is not null && string.CompareOrdinal(current, SqliteExtensions.Time(updatedAt)) >= 0)
            return false;
        await c.ExecAsync("""
            INSERT OR REPLACE INTO counts (id, number, site_id, status, frozen, data, created_at, updated_at)
            VALUES ($id, $number, $site, $status, $frozen, $data, $created, $at)
            """, tx, ("$id", d.Id), ("$number", d.Number), ("$site", d.SiteId), ("$status", d.Status),
            ("$frozen", JsonSerializer.Serialize(d.Frozen.Select(x => x.ToString()))), ("$data", json.GetRawText()), ("$created", d.CreatedAt),
            ("$at", updatedAt));
        return true;
    }

    internal static async Task<bool> ApplyResultAsync(SqliteConnection c, SqliteTransaction tx, JsonElement json, DateTimeOffset updatedAt,
        bool force = false)
    {
        var d = json.Deserialize<CountResultData>(VortexJson.Options)!;
        var current = await c.ScalarAsync<string?>("SELECT updated_at FROM count_results WHERE id = $id", tx, ("$id", d.Id));
        if (!force && current is not null && string.CompareOrdinal(current, SqliteExtensions.Time(updatedAt)) >= 0)
            return false;
        await c.ExecAsync("""
            INSERT OR REPLACE INTO count_results (id, count_id, data, updated_at) VALUES ($id, $count, $data, $at)
            """, tx, ("$id", d.Id), ("$count", d.CountId), ("$data", json.GetRawText()), ("$at", updatedAt));
        return true;
    }
}
