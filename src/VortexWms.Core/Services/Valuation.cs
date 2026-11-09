using System.Globalization;
using Microsoft.Data.Sqlite;
using VortexWms.Core.Data;
using VortexWms.Core.Domain;

namespace VortexWms.Core.Services;

/// <summary>
/// Kardex valorizado con costo promedio ponderado móvil por artículo y sede (fase 1 de la hoja de ruta):
/// <list type="bullet">
/// <item>Una entrada con costo conocido (compra, transferencia recibida, anulación de venta) recalcula el promedio:
/// <c>(saldo × promedio + cantidad × costo) / (saldo + cantidad)</c>. Si el saldo era cero o negativo, el promedio
/// pasa a ser el costo de la entrada.</item>
/// <item>Una entrada sin costo (ajuste positivo, devolución de cliente) entra al promedio vigente.</item>
/// <item>Toda salida sale al promedio vigente y no lo cambia.</item>
/// </list>
/// Cada movimiento guarda su costo unitario, su costo total, el promedio y el saldo valorizado después de él. El orden
/// es por fecha y luego por Id: si un equipo que estuvo sin conexión entrega un movimiento con fecha anterior, se
/// recalcula desde ese punto y todos los equipos llegan al mismo kardex. Los montos se guardan como texto decimal exacto,
/// no como <c>REAL</c>.
/// </summary>
internal static class Valuation
{
    /// <summary>Decimales del costo unitario y del promedio (los totales se redondean según la moneda).</summary>
    public const int UnitDecimals = 6;

    public static string Store(decimal value) => value.ToString(CultureInfo.InvariantCulture);

    public static string? Store(decimal? value) => value is { } v ? Store(v) : null;

    public static decimal Unit(decimal value) => Math.Round(value, UnitDecimals, MidpointRounding.AwayFromZero);

    /// <summary>Moneda de la empresa (los costos del inventario están en ella).</summary>
    public static async Task<VortexApi.Suite.Money.Currency> CurrencyAsync(SqliteConnection c, SqliteTransaction? tx) =>
        VortexApi.Suite.Money.Currencies.Get(await c.ScalarAsync<string?>("SELECT currency_code FROM company WHERE id = 1", tx) ?? "PEN");

    /// <summary>
    /// Recalcula la valorización del artículo en la sede desde el movimiento <paramref name="fromId"/> (inclusive) en
    /// adelante, partiendo del saldo del movimiento anterior. Actualiza también el saldo en unidades y el promedio vigente.
    /// </summary>
    public static async Task RevalueAsync(SqliteConnection c, SqliteTransaction tx, Guid siteId, Guid itemId, DateTimeOffset fromAt,
        Guid fromId)
    {
        var currency = await CurrencyAsync(c, tx);
        var at = SqliteExtensions.Time(fromAt);
        var id = fromId.ToString();
        var previous = await c.FirstOrDefaultAsync("""
            SELECT on_hand_after, avg_cost_after FROM movements
            WHERE site_id = $site AND item_id = $item AND (occurred_at < $at OR (occurred_at = $at AND id < $id))
            ORDER BY occurred_at DESC, id DESC LIMIT 1
            """, r => new Balance(r.Dec("on_hand_after"), r.Dec("avg_cost_after")), tx,
            ("$site", siteId), ("$item", itemId), ("$at", at), ("$id", id));
        var qty = previous?.Qty ?? 0;
        var avg = previous?.Avg ?? 0;

        var rows = await c.QueryAsync("""
            SELECT id, quantity, input_cost FROM movements
            WHERE site_id = $site AND item_id = $item AND (occurred_at > $at OR (occurred_at = $at AND id >= $id))
            ORDER BY occurred_at, id
            """, r => (Id: r.Str("id"), Qty: r.Dec("quantity"), Input: r.DecOrNull("input_cost")), tx,
            ("$site", siteId), ("$item", itemId), ("$at", at), ("$id", id));

        foreach (var m in rows)
        {
            decimal unit;
            if (m.Qty > 0 && m.Input is { } input)
            {
                unit = Unit(input);
                avg = qty <= 0 ? unit : Unit((qty * avg + m.Qty * unit) / (qty + m.Qty));
            }
            else
            {
                unit = avg;
            }
            qty += m.Qty;
            var total = currency.Round(m.Qty * unit);
            var value = currency.Round(qty * avg);
            await c.ExecAsync("""
                UPDATE movements SET on_hand_after = $after, unit_cost = $unit, total_cost = $total, avg_cost_after = $avg,
                                     value_after = $value
                WHERE id = $id
                """, tx, ("$id", m.Id), ("$after", qty), ("$unit", Store(unit)), ("$total", Store(total)), ("$avg", Store(avg)),
                ("$value", Store(value)));
        }

        await c.ExecAsync("UPDATE stock SET avg_cost = $avg WHERE site_id = $site AND item_id = $item", tx,
            ("$avg", Store(avg)), ("$site", siteId), ("$item", itemId));
    }

    private sealed record Balance(decimal Qty, decimal Avg);

    /// <summary>Recalcula todo el kardex (al migrar una base que no tenía costos).</summary>
    public static async Task RevalueAllAsync(SqliteConnection c, SqliteTransaction tx)
    {
        var pairs = await c.QueryAsync("SELECT DISTINCT site_id, item_id FROM movements", r => (Site: r.Guid("site_id"), Item: r.Guid("item_id")), tx);
        foreach (var (site, item) in pairs)
            await RevalueAsync(c, tx, site, item, DateTimeOffset.MinValue, Guid.Empty);
    }

    /// <summary>
    /// Tipo de operación de la Tabla 12 de SUNAT (para el PLE 13.1 de la fase 8): 01 venta, 02 compra, 05 devolución
    /// recibida, 06 devolución entregada, 11 transferencia entre almacenes, 13 mermas, 28 ajuste por diferencia de inventario, 99 otros.
    /// </summary>
    public static string OperationType(string kind, string? reason) => kind switch
    {
        MovementKinds.Sale or MovementKinds.Shipment => "01",
        MovementKinds.Receipt => "02",
        MovementKinds.SaleVoid or MovementKinds.SaleReturn or MovementKinds.CustomerReturn => "05",
        MovementKinds.SupplierReturn => "06",
        MovementKinds.TransferIn or MovementKinds.TransferOut => "11",
        MovementKinds.Adjustment when reason is AdjustmentReasons.Damage or AdjustmentReasons.Loss or AdjustmentReasons.Expired => "13",
        MovementKinds.Adjustment => "28",
        _ => "99",
    };
}
