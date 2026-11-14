using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using VortexApi.Client;
using VortexWms.Core.Data;
using VortexWms.Core.Domain;

using static VortexWms.Core.Texts.CoreText;

namespace VortexWms.Core.Services;

public static class ReportKinds
{
    /// <summary>Rotación por artículo (salidas, stock promedio, vueltas, días de inventario) con su clase ABC por valor.</summary>
    public const string Rotation = "rotation";

    /// <summary>Antigüedad del stock: días desde la última entrada, por tramos.</summary>
    public const string Aging = "aging";

    /// <summary>Lotes vencidos o que vencen dentro de los días pedidos.</summary>
    public const string Expiring = "expiring";

    /// <summary>Artículos con stock y sin movimiento en los días pedidos.</summary>
    public const string Idle = "idle";

    /// <summary>Exactitud de los conteos aprobados (líneas sin diferencia).</summary>
    public const string Accuracy = "accuracy";

    /// <summary>Productividad por operario: recepciones, picking, traslados, conteos y tareas.</summary>
    public const string Productivity = "productivity";

    /// <summary>Kardex valorizado del periodo: inicial, entradas, salidas y final por artículo.</summary>
    public const string Kardex = "kardex";

    /// <summary>Comparativo entre sedes.</summary>
    public const string Sites = "sites";

    public static readonly string[] All = [Rotation, Aging, Expiring, Idle, Accuracy, Productivity, Kardex, Sites];

    /// <summary>Los que muestran costos (piden el permiso <c>cost.view</c> para ver los valores).</summary>
    public static bool HasValues(string kind) => kind is Rotation or Aging or Idle or Accuracy or Kardex or Sites;
}

/// <param name="To">Último día incluido.</param>
/// <param name="Days">Para "por vencer" (vencen en esos días) y "sin movimiento" (sin movimiento en esos días).</param>
public sealed record ReportQuery(string Kind, Guid? SiteId, DateOnly From, DateOnly To, int Days = 60);

public enum ReportColumnType { Text, Number, Money, Percent, Date }

public sealed record ReportColumn(string Header, ReportColumnType Type = ReportColumnType.Text);

/// <summary>
/// Un reporte como tabla (fase 6b): la pantalla, el CSV y el PDF usan lo mismo. Las celdas son <c>string</c>,
/// <c>decimal</c>, <c>int</c> o <c>DateOnly</c> (o null); <c>Totals</c> es la fila de totales si aplica.
/// </summary>
public sealed record WmsReport(string Kind, string Title, string? Subtitle, IReadOnlyList<ReportColumn> Columns, IReadOnlyList<object?[]> Rows,
    object?[]? Totals = null, string? CurrencyCode = null);

/// <summary>
/// Reportes del almacén (fase 6b, WMS-75, WMS-82): rotación y ABC, antigüedad, por vencer, sin movimiento, exactitud de
/// conteos, productividad por operario, kardex valorizado del periodo y comparativo entre sedes. Solo de las sedes del
/// usuario; los valores, solo para quien ve costos.
/// </summary>
public sealed class ReportService(WmsDatabase db, IWmsContext context, TimeProvider clock) : WmsService(db, context, clock)
{
    private static readonly string[] Outbound = [MovementKinds.Shipment, MovementKinds.Sale, MovementKinds.SaleVoid, MovementKinds.SaleReturn, MovementKinds.CustomerReturn];

    public async Task<WmsReport> GetAsync(ReportQuery q, CancellationToken ct = default)
    {
        Require(ReportKinds.All.Contains(q.Kind), T("error.reporte_desconocido"));
        Require(q.To >= q.From && q.To.DayNumber - q.From.DayNumber <= 366, T("error.reporte_periodo"));
        await using var c = await Db.OpenAsync(ct);
        if (q.SiteId is { } siteId)
            await RequireSiteAccessAsync(c, null, siteId);
        var tz = await c.ScalarAsync<string?>("SELECT time_zone FROM company WHERE id = 1");
        var ctx = new Ctx(c, q, Instant(q.From, tz), Instant(q.To.AddDays(1), tz), await LotLedger.TodayAsync(c, null, Clock.GetUtcNow()),
            Context.Can(WmsPermissions.ViewCosts), (await Valuation.CurrencyAsync(c, null)).Code);
        var report = q.Kind switch
        {
            ReportKinds.Rotation => await RotationAsync(ctx),
            ReportKinds.Aging => await AgingAsync(ctx),
            ReportKinds.Expiring => await ExpiringAsync(ctx),
            ReportKinds.Idle => await IdleAsync(ctx),
            ReportKinds.Accuracy => await AccuracyAsync(ctx),
            ReportKinds.Productivity => await ProductivityAsync(ctx),
            ReportKinds.Kardex => await KardexAsync(ctx),
            _ => await SitesAsync(ctx),
        };
        var siteName = q.SiteId is { } id ? await c.ScalarAsync<string?>("SELECT name FROM sites WHERE id = $id", null, ("$id", id)) : T("reportes.todas_las_sedes");
        return report with { Subtitle = $"{siteName} · {q.From:yyyy-MM-dd} – {q.To:yyyy-MM-dd}", CurrencyCode = ctx.Currency };
    }

    private sealed record Ctx(SqliteConnection C, ReportQuery Q, string From, string To, DateOnly Today, bool Costs, string Currency);

    private static string Instant(DateOnly day, string? timeZone)
    {
        var local = day.ToDateTime(TimeOnly.MinValue);
        TimeSpan offset;
        try
        {
            offset = string.IsNullOrWhiteSpace(timeZone) ? TimeSpan.Zero : TimeZoneInfo.FindSystemTimeZoneById(timeZone).GetUtcOffset(local);
        }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            offset = TimeSpan.Zero;
        }
        return SqliteExtensions.Time(new DateTimeOffset(local, offset));
    }

    private string SiteFilter(string column) => $" AND ($site IS NULL OR {column} = $site){MySites(column)}";

    private static decimal? Dec(string? text) =>
        decimal.TryParse(text, NumberStyles.Number, CultureInfo.InvariantCulture, out var v) ? v : null;

    private static decimal? Pct(decimal part, decimal whole) => whole > 0 ? Math.Round(100 * part / whole, 1) : null;

    // ---------- Rotación y ABC ----------

    private async Task<WmsReport> RotationAsync(Ctx x)
    {
        var kinds = string.Join(", ", Outbound.Select(k => $"'{k}'"));
        var rows = await x.C.QueryAsync($"""
            SELECT i.id, i.sku, i.name, i.unit,
                   COALESCE((SELECT -SUM(m.quantity) FROM movements m WHERE m.item_id = i.id AND m.kind IN ({kinds})
                             AND m.occurred_at >= $from AND m.occurred_at < $to{SiteFilter("m.site_id")}), 0) AS out_qty,
                   (SELECT SUM(-CAST(m.total_cost AS REAL)) FROM movements m WHERE m.item_id = i.id AND m.kind IN ({kinds})
                             AND m.occurred_at >= $from AND m.occurred_at < $to{SiteFilter("m.site_id")}) AS out_value,
                   COALESCE((SELECT SUM(s.on_hand) FROM stock s WHERE s.item_id = i.id{SiteFilter("s.site_id")}), 0) AS on_hand,
                   COALESCE((SELECT SUM(m.quantity) FROM movements m WHERE m.item_id = i.id AND m.occurred_at >= $from{SiteFilter("m.site_id")}), 0) AS since_from
            FROM items i WHERE i.is_deleted = 0 AND i.track_inventory = 1
            """, r => new
        {
            Sku = r.Str("sku"), Name = r.Str("name"), Unit = r.Str("unit"), Out = r.Dec("out_qty"), OutValue = r.DecOrNull("out_value"),
            OnHand = r.Dec("on_hand"), SinceFrom = r.Dec("since_from"),
        }, null, ("$from", x.From), ("$to", x.To), ("$site", x.Q.SiteId));
        var days = x.Q.To.DayNumber - x.Q.From.DayNumber + 1;
        var active = rows.Where(r => r.Out != 0 || r.OnHand != 0).Select(r =>
        {
            // Stock promedio aproximado: el promedio entre el inicial (el actual menos lo que se movió desde el inicio) y el actual.
            var opening = r.OnHand - r.SinceFrom;
            var average = Math.Max(0, (opening + r.OnHand) / 2);
            var turns = average > 0 ? Math.Round(r.Out / average, 2) : (decimal?)null;
            return (r.Sku, r.Name, r.Unit, r.Out, Value: r.OutValue ?? 0, Average: Math.Round(average, 2), Turns: turns,
                DaysOfStock: turns is > 0 ? Math.Round(days / turns.Value, 1) : (decimal?)null);
        }).OrderByDescending(r => r.Value).ThenByDescending(r => r.Out).ToList();

        // ABC por valor de las salidas (Pareto): es A el artículo que empieza dentro del primer 80 % acumulado, B dentro del
        // 95 % y C el resto (y lo que no tuvo salidas).
        var total = active.Sum(r => r.Value);
        var acc = 0m;
        var result = new List<object?[]>();
        foreach (var r in active)
        {
            var before = total > 0 ? acc / total : 1;
            acc += r.Value;
            var cls = r.Out <= 0 || r.Value <= 0 ? "C" : before < 0.8m ? "A" : before < 0.95m ? "B" : "C";
            result.Add([r.Sku, r.Name, r.Unit, r.Out, x.Costs ? Math.Round(r.Value, 2) : null, r.Average, r.Turns, r.DaysOfStock, cls]);
        }
        return new WmsReport(ReportKinds.Rotation, T("reportes.rotacion"), null,
        [
            new(T("reportes.sku")), new(T("reportes.articulo")), new(T("reportes.unidad")), new(T("reportes.salidas"), ReportColumnType.Number),
            new(T("reportes.valor_salidas"), ReportColumnType.Money), new(T("reportes.stock_promedio"), ReportColumnType.Number),
            new(T("reportes.rotacion_veces"), ReportColumnType.Number), new(T("reportes.dias_inventario"), ReportColumnType.Number), new(T("reportes.clase_abc")),
        ], result, [T("reportes.total"), null, null, active.Sum(r => r.Out), x.Costs ? Math.Round(total, 2) : null, null, null, null, null]);
    }

    // ---------- Antigüedad ----------

    private async Task<WmsReport> AgingAsync(Ctx x)
    {
        var rows = await x.C.QueryAsync($"""
            SELECT i.sku, i.name, t.name AS site, s.on_hand, s.avg_cost,
                   (SELECT MAX(m.occurred_at) FROM movements m WHERE m.item_id = s.item_id AND m.site_id = s.site_id AND m.quantity > 0
                    AND m.kind IN ('{MovementKinds.Receipt}', '{MovementKinds.TransferIn}')) AS last_in
            FROM stock s JOIN items i ON i.id = s.item_id JOIN sites t ON t.id = s.site_id
            WHERE s.on_hand > 0 AND i.is_deleted = 0{SiteFilter("s.site_id")}
            """, r => new { Sku = r.Str("sku"), Name = r.Str("name"), Site = r.Str("site"), OnHand = r.Dec("on_hand"), Cost = Dec(r.StrOrNull("avg_cost")),
                LastIn = r.TimeOrNull("last_in") }, null, ("$site", x.Q.SiteId));
        var result = rows.Select(r =>
        {
            int? age = r.LastIn is { } d ? x.Today.DayNumber - DateOnly.FromDateTime(d.UtcDateTime).DayNumber : null;
            var bucket = age switch { null => "—", <= 30 => "0-30", <= 60 => "31-60", <= 90 => "61-90", <= 180 => "91-180", _ => "180+" };
            return new object?[] { r.Sku, r.Name, r.Site, r.OnHand, r.LastIn is { } li ? DateOnly.FromDateTime(li.UtcDateTime) : null, age, bucket,
                x.Costs && r.Cost is { } cost ? Math.Round(cost * r.OnHand, 2) : null };
        }).OrderByDescending(r => (int?)r[5] ?? int.MaxValue).ToList();
        return new WmsReport(ReportKinds.Aging, T("reportes.antiguedad"), null,
        [
            new(T("reportes.sku")), new(T("reportes.articulo")), new(T("reportes.sede")), new(T("reportes.stock"), ReportColumnType.Number),
            new(T("reportes.ultima_entrada"), ReportColumnType.Date), new(T("reportes.dias"), ReportColumnType.Number), new(T("reportes.tramo")),
            new(T("reportes.valor"), ReportColumnType.Money),
        ], result, [T("reportes.total"), null, null, rows.Sum(r => r.OnHand), null, null, null,
            x.Costs ? result.Sum(r => (decimal?)r[7] ?? 0) : null]);
    }

    // ---------- Por vencer ----------

    private async Task<WmsReport> ExpiringAsync(Ctx x)
    {
        var limit = x.Today.AddDays(Math.Clamp(x.Q.Days, 1, 730)).ToString("yyyy-MM-dd");
        var rows = await x.C.QueryAsync($"""
            SELECT i.sku, i.name, t.name AS site, l.lot_code, l.expires_at, SUM(l.quantity) AS qty, MAX(l.status) AS status
            FROM stock_lots l JOIN items i ON i.id = l.item_id JOIN sites t ON t.id = l.site_id
            WHERE l.quantity > 0 AND l.expires_at IS NOT NULL AND l.expires_at <= $limit{SiteFilter("l.site_id")}
            GROUP BY l.site_id, l.item_id, l.lot_code ORDER BY l.expires_at
            """, r => new object?[]
        {
            r.Str("sku"), r.Str("name"), r.Str("site"), r.Str("lot_code"), DateOnly.Parse(r.Str("expires_at")[..10], CultureInfo.InvariantCulture),
            DateOnly.Parse(r.Str("expires_at")[..10], CultureInfo.InvariantCulture).DayNumber - x.Today.DayNumber, r.Dec("qty"), r.Str("status"),
        }, null, ("$limit", limit), ("$site", x.Q.SiteId));
        return new WmsReport(ReportKinds.Expiring, T("reportes.por_vencer"), null,
        [
            new(T("reportes.sku")), new(T("reportes.articulo")), new(T("reportes.sede")), new(T("reportes.lote")), new(T("reportes.vence"), ReportColumnType.Date),
            new(T("reportes.dias"), ReportColumnType.Number), new(T("reportes.cantidad"), ReportColumnType.Number), new(T("reportes.estado")),
        ], rows);
    }

    // ---------- Sin movimiento ----------

    private async Task<WmsReport> IdleAsync(Ctx x)
    {
        var since = SqliteExtensions.Time(Clock.GetUtcNow().AddDays(-Math.Clamp(x.Q.Days, 1, 730)));
        var rows = await x.C.QueryAsync($"""
            SELECT i.sku, i.name, t.name AS site, s.on_hand, s.avg_cost,
                   (SELECT MAX(m.occurred_at) FROM movements m WHERE m.item_id = s.item_id AND m.site_id = s.site_id) AS last_move
            FROM stock s JOIN items i ON i.id = s.item_id JOIN sites t ON t.id = s.site_id
            WHERE s.on_hand > 0 AND i.is_deleted = 0{SiteFilter("s.site_id")}
              AND NOT EXISTS (SELECT 1 FROM movements m WHERE m.item_id = s.item_id AND m.site_id = s.site_id AND m.occurred_at >= $since)
            """, r => new { Sku = r.Str("sku"), Name = r.Str("name"), Site = r.Str("site"), OnHand = r.Dec("on_hand"), Cost = Dec(r.StrOrNull("avg_cost")),
                Last = r.TimeOrNull("last_move") }, null, ("$since", since), ("$site", x.Q.SiteId));
        var result = rows.Select(r => new object?[]
        {
            r.Sku, r.Name, r.Site, r.OnHand, r.Last is { } l ? DateOnly.FromDateTime(l.UtcDateTime) : null,
            r.Last is { } d ? x.Today.DayNumber - DateOnly.FromDateTime(d.UtcDateTime).DayNumber : null,
            x.Costs && r.Cost is { } cost ? Math.Round(cost * r.OnHand, 2) : null,
        }).OrderByDescending(r => (decimal?)r[6] ?? 0).ToList();
        return new WmsReport(ReportKinds.Idle, T("reportes.sin_movimiento"), null,
        [
            new(T("reportes.sku")), new(T("reportes.articulo")), new(T("reportes.sede")), new(T("reportes.stock"), ReportColumnType.Number),
            new(T("reportes.ultimo_movimiento"), ReportColumnType.Date), new(T("reportes.dias"), ReportColumnType.Number), new(T("reportes.valor"), ReportColumnType.Money),
        ], result, [T("reportes.total"), null, null, rows.Sum(r => r.OnHand), null, null, x.Costs ? result.Sum(r => (decimal?)r[6] ?? 0) : null]);
    }

    // ---------- Exactitud ----------

    private async Task<WmsReport> AccuracyAsync(Ctx x)
    {
        var counts = await x.C.QueryAsync($"""
            SELECT c.data, t.name AS site FROM counts c JOIN sites t ON t.id = c.site_id
            WHERE c.status = '{CountStatuses.Approved}'{SiteFilter("c.site_id")}
            """, r => (Data: JsonSerializer.Deserialize<CountService.CountData>(r.Str("data"), VortexJson.Options)!, Site: r.Str("site")),
            null, ("$site", x.Q.SiteId));
        var fromT = DateTimeOffset.Parse(x.From, CultureInfo.InvariantCulture);
        var toT = DateTimeOffset.Parse(x.To, CultureInfo.InvariantCulture);
        var result = new List<object?[]>();
        int lines = 0, exact = 0;
        decimal diffValue = 0;
        foreach (var (d, site) in counts.Where(k => k.Data.ApprovedAt is { } a && a >= fromT && a < toT).OrderBy(k => k.Data.ApprovedAt))
        {
            var results = await x.C.QueryAsync("SELECT data FROM count_results WHERE count_id = $id",
                r => JsonSerializer.Deserialize<CountService.CountResultData>(r.Str("data"), VortexJson.Options)!, null, ("$id", d.Id));
            int n = 0, ok = 0;
            decimal value = 0;
            foreach (var line in d.Lines)
            {
                var final = results.Where(r => r.LineNo == line.LineNo).OrderByDescending(r => r.Attempt).FirstOrDefault()?.Quantity;
                if (final is not { } counted)
                    continue;
                n++;
                if (counted == line.Expected)
                    ok++;
                value += Math.Abs(counted - line.Expected) * line.UnitCost;
            }
            lines += n;
            exact += ok;
            diffValue += value;
            result.Add([d.Number, site, DateOnly.FromDateTime(d.ApprovedAt!.Value.UtcDateTime), n, ok, Pct(ok, n), x.Costs ? Math.Round(value, 2) : null]);
        }
        return new WmsReport(ReportKinds.Accuracy, T("reportes.exactitud"), null,
        [
            new(T("reportes.conteo")), new(T("reportes.sede")), new(T("reportes.aprobado"), ReportColumnType.Date), new(T("reportes.lineas"), ReportColumnType.Number),
            new(T("reportes.exactas"), ReportColumnType.Number), new(T("reportes.exactitud_pct"), ReportColumnType.Percent),
            new(T("reportes.diferencia_valor"), ReportColumnType.Money),
        ], result, [T("reportes.total"), null, null, lines, exact, Pct(exact, lines), x.Costs ? Math.Round(diffValue, 2) : null]);
    }

    // ---------- Productividad por operario ----------

    private async Task<WmsReport> ProductivityAsync(Ctx x)
    {
        var args = new (string, object?)[] { ("$from", x.From), ("$to", x.To), ("$site", x.Q.SiteId) };
        var byKind = await x.C.QueryAsync($"""
            SELECT COALESCE(m.user_name, '—') AS who, m.kind, COUNT(*) AS n, SUM(ABS(m.quantity)) AS qty FROM movements m
            WHERE m.occurred_at >= $from AND m.occurred_at < $to{SiteFilter("m.site_id")} GROUP BY 1, 2
            """, r => (Who: r.Str("who"), Kind: r.Str("kind"), N: r.Int("n"), Qty: r.Dec("qty")), null, args);
        var picks = await x.C.QueryAsync($"""
            SELECT COALESCE(p.user_name, '—') AS who, COUNT(*) AS n, SUM(p.quantity) AS qty FROM order_picks p JOIN orders o ON o.id = p.order_id
            WHERE p.picked_at >= $from AND p.picked_at < $to{SiteFilter("o.site_id")} GROUP BY 1
            """, r => (Who: r.Str("who"), N: r.Int("n"), Qty: r.Dec("qty")), null, args);
        var moves = await x.C.QueryAsync($"""
            SELECT COALESCE(r.user_name, '—') AS who, COUNT(*) AS n FROM relocations r
            WHERE r.occurred_at >= $from AND r.occurred_at < $to{SiteFilter("r.site_id")} GROUP BY 1
            """, r => (Who: r.Str("who"), N: r.Int("n")), null, args);
        var tasks = await x.C.QueryAsync($"""
            SELECT COALESCE(t.done_by, t.assignee_name, '—') AS who, COUNT(*) AS n FROM tasks t
            WHERE t.done_at >= $from AND t.done_at < $to{SiteFilter("t.site_id")} GROUP BY 1
            """, r => (Who: r.Str("who"), N: r.Int("n")), null, args);
        var countLines = (await x.C.QueryAsync("SELECT r.data, c.site_id FROM count_results r JOIN counts c ON c.id = r.count_id",
                r => (Data: JsonSerializer.Deserialize<CountService.CountResultData>(r.Str("data"), VortexJson.Options)!, Site: r.Guid("site_id"))))
            .Where(r => (x.Q.SiteId is null || r.Site == x.Q.SiteId) && string.CompareOrdinal(SqliteExtensions.Time(r.Data.CountedAt), x.From) >= 0
                && string.CompareOrdinal(SqliteExtensions.Time(r.Data.CountedAt), x.To) < 0)
            .GroupBy(r => r.Data.CountedBy ?? "—").ToDictionary(g => g.Key, g => g.Count());

        var people = byKind.Select(b => b.Who).Concat(picks.Select(p => p.Who)).Concat(moves.Select(m => m.Who)).Concat(tasks.Select(t => t.Who))
            .Concat(countLines.Keys).Distinct().Where(w => w != "—" || byKind.Any(b => b.Who == w)).ToList();
        var result = people.Select(w =>
        {
            int Lines(params string[] kinds) => byKind.Where(b => b.Who == w && kinds.Contains(b.Kind)).Sum(b => b.N);
            var pick = picks.FirstOrDefault(p => p.Who == w);
            return new object?[]
            {
                w, Lines(MovementKinds.Receipt), pick.N, pick.Qty, Lines(MovementKinds.Shipment), moves.FirstOrDefault(m => m.Who == w).N,
                countLines.GetValueOrDefault(w), Lines(MovementKinds.Adjustment), tasks.FirstOrDefault(t => t.Who == w).N,
            };
        }).OrderByDescending(r => (int)r[2]! + (int)r[1]! + (int)r[4]!).ToList();
        return new WmsReport(ReportKinds.Productivity, T("reportes.productividad"), null,
        [
            new(T("reportes.operario")), new(T("reportes.lineas_recibidas"), ReportColumnType.Number), new(T("reportes.lineas_tomadas"), ReportColumnType.Number),
            new(T("reportes.unidades_tomadas"), ReportColumnType.Number), new(T("reportes.lineas_despachadas"), ReportColumnType.Number),
            new(T("reportes.traslados"), ReportColumnType.Number), new(T("reportes.lineas_contadas"), ReportColumnType.Number),
            new(T("reportes.ajustes"), ReportColumnType.Number), new(T("reportes.tareas"), ReportColumnType.Number),
        ], result);
    }

    // ---------- Kardex valorizado ----------

    private async Task<WmsReport> KardexAsync(Ctx x)
    {
        var moves = await x.C.QueryAsync($"""
            SELECT m.site_id, m.item_id, i.sku, i.name, m.occurred_at, m.quantity, m.on_hand_after, m.total_cost, m.value_after
            FROM movements m JOIN items i ON i.id = m.item_id
            WHERE m.occurred_at < $to{SiteFilter("m.site_id")} ORDER BY m.occurred_at, m.id
            """, r => new
        {
            Key = (r.Guid("site_id"), r.Guid("item_id")), Sku = r.Str("sku"), Name = r.Str("name"), At = r.Str("occurred_at"), Qty = r.Dec("quantity"),
            After = r.Dec("on_hand_after"), Cost = Dec(r.StrOrNull("total_cost")), ValueAfter = Dec(r.StrOrNull("value_after")),
        }, null, ("$to", x.To), ("$site", x.Q.SiteId));
        var rows = new Dictionary<Guid, (string Sku, string Name, decimal OpenQ, decimal OpenV, decimal InQ, decimal InV, decimal OutQ, decimal OutV,
            decimal CloseQ, decimal CloseV)>();
        foreach (var g in moves.GroupBy(m => m.Key))
        {
            var before = g.LastOrDefault(m => string.CompareOrdinal(m.At, x.From) < 0);
            var inPeriod = g.Where(m => string.CompareOrdinal(m.At, x.From) >= 0).ToList();
            if (inPeriod.Count == 0 && (before is null || before.After == 0))
                continue;
            var last = g.Last();
            var item = g.Key.Item2;
            var cur = rows.GetValueOrDefault(item, (g.First().Sku, g.First().Name, 0, 0, 0, 0, 0, 0, 0, 0));
            cur.OpenQ += before?.After ?? 0;
            cur.OpenV += before?.ValueAfter ?? 0;
            cur.InQ += inPeriod.Where(m => m.Qty > 0).Sum(m => m.Qty);
            cur.InV += inPeriod.Where(m => m.Qty > 0).Sum(m => m.Cost ?? 0);
            cur.OutQ += inPeriod.Where(m => m.Qty < 0).Sum(m => -m.Qty);
            cur.OutV += inPeriod.Where(m => m.Qty < 0).Sum(m => -(m.Cost ?? 0));
            cur.CloseQ += last.After;
            cur.CloseV += last.ValueAfter ?? 0;
            rows[item] = cur;
        }
        decimal? V(decimal v) => x.Costs ? Math.Round(v, 2) : null;
        var result = rows.Values.OrderBy(r => r.Name, StringComparer.CurrentCultureIgnoreCase)
            .Select(r => new object?[] { r.Sku, r.Name, r.OpenQ, V(r.OpenV), r.InQ, V(r.InV), r.OutQ, V(r.OutV), r.CloseQ, V(r.CloseV) }).ToList();
        var all = rows.Values.ToList();
        return new WmsReport(ReportKinds.Kardex, T("reportes.kardex"), null,
        [
            new(T("reportes.sku")), new(T("reportes.articulo")), new(T("reportes.inicial"), ReportColumnType.Number), new(T("reportes.valor_inicial"), ReportColumnType.Money),
            new(T("reportes.entradas"), ReportColumnType.Number), new(T("reportes.valor_entradas"), ReportColumnType.Money),
            new(T("reportes.salidas"), ReportColumnType.Number), new(T("reportes.valor_salidas"), ReportColumnType.Money),
            new(T("reportes.final"), ReportColumnType.Number), new(T("reportes.valor_final"), ReportColumnType.Money),
        ], result, [T("reportes.total"), null, all.Sum(r => r.OpenQ), V(all.Sum(r => r.OpenV)), all.Sum(r => r.InQ), V(all.Sum(r => r.InV)),
            all.Sum(r => r.OutQ), V(all.Sum(r => r.OutV)), all.Sum(r => r.CloseQ), V(all.Sum(r => r.CloseV))]);
    }

    // ---------- Comparativo entre sedes ----------

    private async Task<WmsReport> SitesAsync(Ctx x)
    {
        var kinds = string.Join(", ", Outbound.Select(k => $"'{k}'"));
        var rows = await x.C.QueryAsync($"""
            SELECT t.id, t.name,
                   (SELECT COUNT(*) FROM stock s WHERE s.site_id = t.id AND s.on_hand > 0) AS items,
                   COALESCE((SELECT SUM(s.on_hand) FROM stock s WHERE s.site_id = t.id), 0) AS units,
                   (SELECT SUM(s.on_hand * CAST(s.avg_cost AS REAL)) FROM stock s WHERE s.site_id = t.id AND s.avg_cost IS NOT NULL) AS value,
                   COALESCE((SELECT -SUM(m.quantity) FROM movements m WHERE m.site_id = t.id AND m.kind IN ({kinds})
                             AND m.occurred_at >= $from AND m.occurred_at < $to), 0) AS out_qty,
                   (SELECT SUM(-CAST(m.total_cost AS REAL)) FROM movements m WHERE m.site_id = t.id AND m.kind IN ({kinds})
                             AND m.occurred_at >= $from AND m.occurred_at < $to) AS out_value,
                   (SELECT COUNT(*) FROM movements m WHERE m.site_id = t.id AND m.kind = '{MovementKinds.Receipt}'
                             AND m.occurred_at >= $from AND m.occurred_at < $to) AS receipts
            FROM sites t WHERE t.is_deleted = 0{SiteFilter("t.id")} ORDER BY t.name COLLATE NOCASE
            """, r => new
        {
            Name = r.Str("name"), Items = r.Int("items"), Units = r.Dec("units"), Value = r.DecOrNull("value"), Out = r.Dec("out_qty"),
            OutValue = r.DecOrNull("out_value"), Receipts = r.Int("receipts"),
        }, null, ("$from", x.From), ("$to", x.To), ("$site", x.Q.SiteId));
        var result = rows.Select(r => new object?[]
        {
            r.Name, r.Items, r.Units, x.Costs ? Math.Round(r.Value ?? 0, 2) : null, r.Out, x.Costs ? Math.Round(r.OutValue ?? 0, 2) : null, r.Receipts,
            r.Value is > 0 && r.OutValue is { } ov ? Math.Round(ov / r.Value.Value, 2) : null,
        }).ToList();
        return new WmsReport(ReportKinds.Sites, T("reportes.comparativo"), null,
        [
            new(T("reportes.sede")), new(T("reportes.articulos_con_stock"), ReportColumnType.Number), new(T("reportes.unidades"), ReportColumnType.Number),
            new(T("reportes.valor"), ReportColumnType.Money), new(T("reportes.salidas"), ReportColumnType.Number), new(T("reportes.valor_salidas"), ReportColumnType.Money),
            new(T("reportes.lineas_recibidas"), ReportColumnType.Number), new(T("reportes.rotacion_veces"), ReportColumnType.Number),
        ], result, [T("reportes.total"), rows.Sum(r => r.Items), rows.Sum(r => r.Units), x.Costs ? Math.Round(rows.Sum(r => r.Value ?? 0), 2) : null,
            rows.Sum(r => r.Out), x.Costs ? Math.Round(rows.Sum(r => r.OutValue ?? 0), 2) : null, rows.Sum(r => r.Receipts), null]);
    }

    // ---------- Exportación ----------

    /// <summary>El reporte en CSV (UTF-8, separado por comas, números con punto) para Excel.</summary>
    public static string ToCsv(WmsReport r)
    {
        var sb = new StringBuilder();
        sb.AppendLine(Csv(r.Title) + "," + Csv(r.Subtitle));
        sb.AppendLine(string.Join(',', r.Columns.Select(c => Csv(c.Header))));
        foreach (var row in r.Totals is null ? r.Rows : [.. r.Rows, r.Totals])
            sb.AppendLine(string.Join(',', row.Select(Cell)));
        return sb.ToString();

        static string Cell(object? v) => v switch
        {
            null => "",
            decimal d => d.ToString("0.####", CultureInfo.InvariantCulture),
            int i => i.ToString(CultureInfo.InvariantCulture),
            DateOnly d => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            _ => Csv(v.ToString()),
        };
    }

    /// <summary>
    /// El reporte en Excel (.xlsx): empresa y periodo arriba, encabezados con filtro, números como números, fechas como
    /// fechas y la fila de totales.
    /// </summary>
    public static byte[] ToXlsx(WmsReport r, string company) =>
        VortexApi.Suite.Export.XlsxWriter.Write(new VortexApi.Suite.Export.XlsxSheet(r.Title,
            r.Columns.Select(c => new VortexApi.Suite.Export.XlsxColumn(c.Header, c.Type switch
            {
                ReportColumnType.Number => VortexApi.Suite.Export.XlsxColumnType.Number,
                ReportColumnType.Money => VortexApi.Suite.Export.XlsxColumnType.Money,
                ReportColumnType.Percent => VortexApi.Suite.Export.XlsxColumnType.Percent,
                ReportColumnType.Date => VortexApi.Suite.Export.XlsxColumnType.Date,
                _ => VortexApi.Suite.Export.XlsxColumnType.Text,
            })).ToList(),
            r.Rows, r.Totals, $"{company} · {r.Title}", r.CurrencyCode is { } cur ? $"{r.Subtitle} · {cur}" : r.Subtitle));

    /// <summary>Una celda para mostrar o imprimir con la cultura dada.</summary>
    public static string Format(object? value, ReportColumnType type, CultureInfo culture) => value switch
    {
        null => "—",
        decimal d when type == ReportColumnType.Percent => $"{d.ToString("0.#", culture)} %",
        decimal d when type == ReportColumnType.Money => d.ToString("#,0.00", culture),
        decimal d => d.ToString(d % 1 == 0 ? "#,0" : "#,0.##", culture),
        int i => i.ToString("#,0", culture),
        DateOnly d => d.ToString("d", culture),
        _ => value.ToString() ?? "",
    };

    private static string Csv(string? value)
    {
        var v = value ?? "";
        if (v.Length > 0 && v[0] is '=' or '+' or '-' or '@')
            v = "'" + v;
        return v.IndexOfAny([',', '"', '\n', '\r']) >= 0 ? $"\"{v.Replace("\"", "\"\"")}\"" : v;
    }
}
