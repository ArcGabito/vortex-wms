using System.Globalization;
using Microsoft.Data.Sqlite;
using VortexWms.Core.Data;
using VortexWms.Core.Domain;

using static VortexWms.Core.Texts.CoreText;

namespace VortexWms.Core.Services;

/// <summary>
/// Saldos por lote y series, en la misma transacción que el movimiento de <see cref="StockLedger"/>. El total de la sede
/// sigue en <c>stock</c>; lo que no está en ningún lote (stock anterior a la fase 3, o ventas que no se pudieron asignar)
/// es el saldo <b>sin lote</b> (<c>lot_code = ''</c>): total menos la suma de los lotes.
/// </summary>
internal static class LotLedger
{
    /// <summary>Lote que representa el saldo sin lote en los movimientos.</summary>
    public const string NoLot = "";

    public sealed record Result(IReadOnlyList<LotQuantity> Lots, IReadOnlyList<string> Serials);

    private sealed record Balance(string LotCode, DateOnly? ExpiresAt, decimal Quantity, string Status);

    public static async Task<string> TrackingAsync(SqliteConnection c, SqliteTransaction? tx, Guid itemId) =>
        await c.ScalarAsync<string?>("SELECT tracking FROM items WHERE id = $id", tx, ("$id", itemId)) ?? ItemTrackings.None;

    /// <summary>
    /// Asigna el movimiento a lotes y series y actualiza sus saldos. Debe llamarse antes de cambiar el total de la sede
    /// (<paramref name="onHandBefore"/> es el total previo, para conocer el saldo sin lote).
    /// </summary>
    public static async Task<Result> ApplyAsync(SqliteConnection c, SqliteTransaction tx, DateTimeOffset now, StockLedger.Entry e,
        decimal onHandBefore)
    {
        var tracking = await TrackingAsync(c, tx, e.ItemId);
        if (tracking == ItemTrackings.None || e.Quantity == 0)
            return new([], []);
        var item = await InventoryService.ItemLabelAsync(c, tx, e.ItemId);
        var today = await TodayAsync(c, tx, now);

        if (tracking == ItemTrackings.Serial)
            return new([], await SerialsAsync(c, tx, now, e, item));

        return e.Quantity > 0
            ? new(await InAsync(c, tx, now, e, item), [])
            : new(await OutAsync(c, tx, now, e, item, onHandBefore, today), []);
    }

    // ---------- Entradas ----------

    private static async Task<List<LotQuantity>> InAsync(SqliteConnection c, SqliteTransaction tx, DateTimeOffset now, StockLedger.Entry e,
        string item)
    {
        var lots = (e.Lots ?? []).Where(l => l.Quantity != 0).ToList();
        if (lots.Count == 0)
        {
            Require(e.LotMode == StockLedger.LotMode.Lenient, T("error.indica_el_lote_de", item));
            return [new LotQuantity(NoLot, e.Quantity)];
        }
        Require(lots.All(l => l.Quantity > 0) && (e.LotMode == StockLedger.LotMode.Lenient || lots.All(l => !string.IsNullOrWhiteSpace(l.LotCode))),
            T("error.lotes_invalidos", item));
        Require(lots.Sum(l => l.Quantity) == e.Quantity, T("error.los_lotes_no_suman", item, e.Quantity));

        var result = new List<LotQuantity>();
        foreach (var lot in lots.GroupBy(l => l.LotCode.Trim().ToUpperInvariant()))
        {
            if (lot.Key == NoLot)
            {
                result.Add(new LotQuantity(NoLot, lot.Sum(l => l.Quantity)));
                continue;
            }
            var expires = lot.Select(l => l.ExpiresAt).FirstOrDefault(x => x is not null);
            var current = await BalanceAsync(c, tx, e.SiteId, e.ItemId, lot.Key);
            if (current?.ExpiresAt is { } existing && expires is { } given && existing != given && e.LotMode == StockLedger.LotMode.Strict)
                Require(false, T("error.el_lote_ya_vence", lot.Key, existing));
            var quantity = lot.Sum(l => l.Quantity);
            await AddAsync(c, tx, now, e.SiteId, e.ItemId, lot.Key, current?.ExpiresAt ?? expires, quantity);
            result.Add(new LotQuantity(lot.Key, quantity, current?.ExpiresAt ?? expires));
        }
        return result;
    }

    // ---------- Salidas ----------

    private static async Task<List<LotQuantity>> OutAsync(SqliteConnection c, SqliteTransaction tx, DateTimeOffset now, StockLedger.Entry e,
        string item, decimal onHandBefore, DateOnly today)
    {
        var needed = -e.Quantity;
        var balances = await BalancesAsync(c, tx, e.SiteId, e.ItemId);
        var unlotted = Math.Max(0, onHandBefore - balances.Sum(b => b.Quantity));
        var fefo = Fefo(balances, today, needed, unlotted);
        var result = new List<LotQuantity>();

        var explicitLots = (e.Lots ?? []).Where(l => l.Quantity != 0).ToList();
        if (explicitLots.Count > 0)
        {
            Require(explicitLots.Sum(l => Math.Abs(l.Quantity)) == needed, T("error.los_lotes_no_suman", item, needed));
            var chosen = explicitLots.GroupBy(l => l.LotCode.Trim().ToUpperInvariant())
                .Select(g => new LotQuantity(g.Key, g.Sum(l => Math.Abs(l.Quantity)))).ToList();
            var isFefo = chosen.OrderBy(l => l.LotCode).SequenceEqual(fefo.OrderBy(l => l.LotCode).Select(l => l with { ExpiresAt = null }));
            Require(isFefo || e.AllowFefoOverride, T("error.fefo_sugiere", item, string.Join(", ", fefo.Select(l => Label(l)))));
            foreach (var lot in chosen)
            {
                if (lot.LotCode == NoLot)
                {
                    Require(lot.Quantity <= unlotted, T("error.no_hay_tanto_sin_lote", item, unlotted));
                    result.Add(new LotQuantity(NoLot, -lot.Quantity));
                    continue;
                }
                var balance = balances.FirstOrDefault(b => b.LotCode == lot.LotCode);
                Require(balance is not null && balance.Quantity >= lot.Quantity,
                    T("error.no_hay_tanto_del_lote", lot.LotCode, item, balance?.Quantity ?? 0));
                if (!e.AllowUnavailableLots)
                {
                    Require(balance!.Status == LotStatuses.Available, T("error.el_lote_no_esta_disponible", lot.LotCode, balance.Status));
                    Require(balance.ExpiresAt is not { } exp || exp >= today, T("error.el_lote_esta_vencido", lot.LotCode, balance.ExpiresAt!));
                }
                await AddAsync(c, tx, now, e.SiteId, e.ItemId, lot.LotCode, null, -lot.Quantity);
                result.Add(new LotQuantity(lot.LotCode, -lot.Quantity, balance!.ExpiresAt));
            }
            return result;
        }

        var assigned = fefo.Sum(l => l.Quantity);
        if (assigned < needed)
        {
            Require(e.LotMode == StockLedger.LotMode.Lenient,
                T("error.no_hay_lotes_vigentes", item, assigned, needed));
            // Venta de caja o movimiento de otro equipo: ya ocurrió. Lo que no alcanza queda sin lote.
            var rest = needed - assigned;
            var noLot = fefo.FirstOrDefault(l => l.LotCode == NoLot);
            fefo = [.. fefo.Where(l => l.LotCode != NoLot), new LotQuantity(NoLot, (noLot?.Quantity ?? 0) + rest)];
        }
        foreach (var lot in fefo)
        {
            if (lot.LotCode != NoLot)
                await AddAsync(c, tx, now, e.SiteId, e.ItemId, lot.LotCode, null, -lot.Quantity);
            result.Add(lot with { Quantity = -lot.Quantity });
        }
        return result;
    }

    /// <summary>
    /// FEFO: primero el lote que vence antes (los que no vencen, al final), solo disponibles y no vencidos; después el saldo
    /// sin lote. Devuelve lo que alcanza a cubrir.
    /// </summary>
    private static List<LotQuantity> Fefo(IReadOnlyList<Balance> balances, DateOnly today, decimal needed, decimal unlotted)
    {
        var result = new List<LotQuantity>();
        var left = needed;
        foreach (var b in balances
                     .Where(b => b.Quantity > 0 && b.Status == LotStatuses.Available && (b.ExpiresAt is null || b.ExpiresAt >= today))
                     .OrderBy(b => b.ExpiresAt is null).ThenBy(b => b.ExpiresAt).ThenBy(b => b.LotCode, StringComparer.Ordinal))
        {
            if (left <= 0)
                break;
            var take = Math.Min(left, b.Quantity);
            result.Add(new LotQuantity(b.LotCode, take, b.ExpiresAt));
            left -= take;
        }
        if (left > 0 && unlotted > 0)
            result.Add(new LotQuantity(NoLot, Math.Min(left, unlotted)));
        return result;
    }

    /// <summary>Lo que FEFO sacaría de la sede (para sugerirlo en pantalla antes de despachar).</summary>
    public static async Task<IReadOnlyList<LotQuantity>> SuggestAsync(SqliteConnection c, SqliteTransaction? tx, Guid siteId, Guid itemId,
        decimal quantity, DateOnly today)
    {
        var balances = await BalancesAsync(c, tx, siteId, itemId);
        var onHand = await StockLedger.OnHandAsync(c, tx, siteId, itemId);
        return Fefo(balances, today, quantity, Math.Max(0, onHand - balances.Sum(b => b.Quantity)));
    }

    // ---------- Series ----------

    private static async Task<List<string>> SerialsAsync(SqliteConnection c, SqliteTransaction tx, DateTimeOffset now, StockLedger.Entry e,
        string item)
    {
        var serials = (e.Serials ?? []).Select(s => s.Trim().ToUpperInvariant()).Where(s => s.Length > 0).ToList();
        var units = Math.Abs(e.Quantity);
        if (serials.Count == 0)
        {
            Require(e.LotMode == StockLedger.LotMode.Lenient, T("error.indica_las_series_de", item, units));
            return [];
        }
        Require(serials.Distinct().Count() == serials.Count, T("error.series_repetidas", item));
        Require(units == Math.Floor(units) && serials.Count == units, T("error.una_serie_por_unidad", item, units, serials.Count));

        foreach (var serial in serials)
        {
            var current = await c.FirstOrDefaultAsync("SELECT site_id, status FROM serials WHERE item_id = $item AND serial = $serial",
                r => new { Site = r.GuidOrNull("site_id"), Status = r.Str("status") }, tx, ("$item", e.ItemId), ("$serial", serial));
            if (e.Quantity > 0)
            {
                if (e.LotMode == StockLedger.LotMode.Strict)
                    Require(current is null || current.Status != SerialStatuses.InStock, T("error.la_serie_ya_esta_en_stock", serial));
                await SetSerialAsync(c, tx, now, e.ItemId, serial, e.SiteId, SerialStatuses.InStock);
            }
            else
            {
                if (e.LotMode == StockLedger.LotMode.Strict)
                    Require(current is { Status: SerialStatuses.InStock } && current.Site == e.SiteId, T("error.la_serie_no_esta_en_la_sede", serial));
                await SetSerialAsync(c, tx, now, e.ItemId, serial, e.SiteId, SerialStatuses.Out);
            }
        }
        return serials;
    }

    private static Task SetSerialAsync(SqliteConnection c, SqliteTransaction tx, DateTimeOffset now, Guid itemId, string serial, Guid siteId,
        string status) =>
        c.ExecAsync("""
            INSERT INTO serials (item_id, serial, site_id, status, updated_at) VALUES ($item, $serial, $site, $status, $now)
            ON CONFLICT (item_id, serial) DO UPDATE SET site_id = $site, status = $status, updated_at = $now
            """, tx, ("$item", itemId), ("$serial", serial), ("$site", siteId), ("$status", status), ("$now", now));

    // ---------- Registro y réplica ----------

    /// <summary>Deja en el movimiento qué lotes y series movió (para la trazabilidad y para replicarlo).</summary>
    public static async Task RecordAsync(SqliteConnection c, SqliteTransaction tx, Guid movementId, Result r)
    {
        foreach (var lot in r.Lots.GroupBy(l => l.LotCode))
        {
            await c.ExecAsync("""
                INSERT INTO movement_lots (movement_id, lot_code, expires_at, quantity) VALUES ($id, $lot, $exp, $qty)
                ON CONFLICT (movement_id, lot_code) DO UPDATE SET quantity = quantity + $qty
                """, tx, ("$id", movementId), ("$lot", lot.Key), ("$exp", Day(lot.First().ExpiresAt)), ("$qty", lot.Sum(l => l.Quantity)));
        }
        foreach (var serial in r.Serials.Distinct())
            await c.ExecAsync("INSERT OR IGNORE INTO movement_serials (movement_id, serial) VALUES ($id, $s)", tx, ("$id", movementId), ("$s", serial));
    }

    /// <summary>
    /// Aplica los lotes y series de un movimiento de otro equipo (tal como los asignó ese equipo). Si el movimiento ya
    /// existía aquí con otra asignación (FEFO dio distinto, o fue un conflicto), primero se deshace la local: así todos
    /// los equipos quedan con la asignación que se quedó el hub.
    /// </summary>
    public static async Task ReplicateAsync(SqliteConnection c, SqliteTransaction tx, DateTimeOffset now, Guid movementId, Guid siteId,
        Guid itemId, decimal delta, IReadOnlyList<LotQuantity>? lots, IReadOnlyList<string>? serials, Guid? previousSiteId,
        decimal? localQuantity)
    {
        var remoteLots = (lots ?? []).ToList();
        var remoteSerials = (serials ?? []).Select(s => s.Trim().ToUpperInvariant()).ToList();
        var localLots = await c.QueryAsync("SELECT lot_code, expires_at, quantity FROM movement_lots WHERE movement_id = $id",
            r => new LotQuantity(r.Str("lot_code"), r.Dec("quantity"), DayOrNull(r.StrOrNull("expires_at"))), tx, ("$id", movementId));
        var localSerials = await c.QueryAsync("SELECT serial FROM movement_serials WHERE movement_id = $id", r => r.Str("serial"), tx,
            ("$id", movementId));
        var sameLots = localLots.Select(l => (l.LotCode, l.Quantity)).OrderBy(x => x.LotCode)
            .SequenceEqual(remoteLots.GroupBy(l => l.LotCode).Select(g => (g.Key, g.Sum(l => l.Quantity))).OrderBy(x => x.Key));
        if (sameLots && localSerials.Order().SequenceEqual(remoteSerials.Order()) && previousSiteId is null)
            return;

        // Deshace la asignación local (si la había).
        var oldSite = previousSiteId ?? siteId;
        foreach (var lot in localLots.Where(l => l.LotCode != NoLot))
            await AddAsync(c, tx, now, oldSite, itemId, lot.LotCode, null, -lot.Quantity);
        // Una serie que entró aquí vuelve a "fuera"; una que salió vuelve a la sede.
        foreach (var serial in localSerials)
            await SetSerialAsync(c, tx, now, itemId, serial, oldSite, (localQuantity ?? delta) > 0 ? SerialStatuses.Out : SerialStatuses.InStock);
        await c.ExecAsync("DELETE FROM movement_lots WHERE movement_id = $id", tx, ("$id", movementId));
        await c.ExecAsync("DELETE FROM movement_serials WHERE movement_id = $id", tx, ("$id", movementId));

        // Aplica la del otro equipo.
        foreach (var lot in remoteLots.Where(l => l.LotCode != NoLot))
            await AddAsync(c, tx, now, siteId, itemId, lot.LotCode, lot.ExpiresAt, lot.Quantity);
        foreach (var serial in remoteSerials)
            await SetSerialAsync(c, tx, now, itemId, serial, siteId, delta > 0 ? SerialStatuses.InStock : SerialStatuses.Out);
        await RecordAsync(c, tx, movementId, new Result(remoteLots, remoteSerials));
    }

    // ---------- Saldos ----------

    /// <summary>La fecha de hoy en la zona horaria de la empresa (los vencimientos son por día).</summary>
    public static async Task<DateOnly> TodayAsync(SqliteConnection c, SqliteTransaction? tx, DateTimeOffset now) =>
        DateOnly.FromDateTime(VortexApi.Suite.Regional.RegionalSettings.ToCompanyTime(now,
            await c.ScalarAsync<string?>("SELECT time_zone FROM company WHERE id = 1", tx) ?? "America/Lima").DateTime);

    private static Task AddAsync(SqliteConnection c, SqliteTransaction tx, DateTimeOffset now, Guid siteId, Guid itemId, string lotCode,
        DateOnly? expiresAt, decimal quantity) =>
        c.ExecAsync("""
            INSERT INTO stock_lots (site_id, item_id, location, lot_code, expires_at, quantity, status)
            VALUES ($site, $item, '', $lot, $exp, $qty, 'available')
            ON CONFLICT (site_id, item_id, location, lot_code) DO UPDATE SET quantity = quantity + $qty,
                expires_at = COALESCE(expires_at, $exp)
            """, tx, ("$site", siteId), ("$item", itemId), ("$lot", lotCode), ("$exp", Day(expiresAt)), ("$qty", quantity));

    private static Task<List<Balance>> BalancesAsync(SqliteConnection c, SqliteTransaction? tx, Guid siteId, Guid itemId) =>
        c.QueryAsync("""
            SELECT lot_code, MIN(expires_at) AS expires_at, SUM(quantity) AS quantity, MAX(status) AS status FROM stock_lots
            WHERE site_id = $site AND item_id = $item AND lot_code <> '' GROUP BY lot_code
            """, r => new Balance(r.Str("lot_code"), DayOrNull(r.StrOrNull("expires_at")), r.Dec("quantity"), r.Str("status")), tx,
            ("$site", siteId), ("$item", itemId));

    private static async Task<Balance?> BalanceAsync(SqliteConnection c, SqliteTransaction tx, Guid siteId, Guid itemId, string lotCode) =>
        (await BalancesAsync(c, tx, siteId, itemId)).FirstOrDefault(b => b.LotCode == lotCode);

    public static string? Day(DateOnly? day) => day?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    public static DateOnly? DayOrNull(string? value) =>
        value is null ? null : DateOnly.ParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static string Label(LotQuantity l) => l.LotCode == NoLot ? $"{T("info.sin_lote")} × {l.Quantity:0.##}" : $"{l.LotCode} × {l.Quantity:0.##}";

    private static void Require(bool condition, string message)
    {
        if (!condition)
            throw new WmsValidationException(message);
    }
}
