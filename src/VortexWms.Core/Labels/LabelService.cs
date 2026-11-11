using VortexApi.Suite.Money;
using VortexWms.Core.Data;
using VortexWms.Core.Domain;
using VortexWms.Core.Services;

using static VortexWms.Core.Texts.CoreText;

namespace VortexWms.Core.Labels;

/// <summary>
/// Etiquetas de artículo, ubicación y bulto (fase 6c, WMS-84). El código de barras es el que el WMS lee al escanear: el
/// del artículo (o su SKU), el código de la ubicación y, en un bulto, <c>{pedido}-{número}</c>.
/// </summary>
public sealed class LabelService(WmsDatabase db, IWmsContext context, TimeProvider clock) : WmsService(db, context, clock)
{
    /// <param name="withPrice">Con el precio de venta (etiqueta de góndola).</param>
    public async Task<IReadOnlyList<LabelData>> ForItemsAsync(IReadOnlyList<Guid> itemIds, bool withPrice = false, CancellationToken ct = default)
    {
        await using var c = await Db.OpenAsync(ct);
        var result = new List<LabelData>();
        foreach (var id in itemIds.Distinct())
        {
            var item = await c.FirstOrDefaultAsync("SELECT sku, name, barcode, unit, price, currency_code FROM items WHERE id = $id AND is_deleted = 0",
                r => new { Sku = r.Str("sku"), Name = r.Str("name"), Barcode = r.StrOrNull("barcode"), Unit = r.Str("unit"), Price = r.Dec("price"),
                    Currency = r.Str("currency_code") }, null, ("$id", id));
            Require(item is not null, T("error.uno_de_los_articulos_ya"));
            result.Add(new LabelData(LabelKinds.Item, item!.Name, $"{item.Sku} · {item.Unit}", Barcodeable(item.Barcode) ?? item.Sku,
                Footer: withPrice ? Currencies.Get(item.Currency).Format(item.Price) : null));
        }
        return result;
    }

    /// <summary>Las ubicaciones elegidas de la sede (todas las activas si no se eligen).</summary>
    public async Task<IReadOnlyList<LabelData>> ForLocationsAsync(Guid siteId, IReadOnlyList<Guid>? locationIds = null, CancellationToken ct = default)
    {
        await using var c = await Db.OpenAsync(ct);
        await RequireSiteAccessAsync(c, null, siteId);
        var rows = await c.QueryAsync("""
            SELECT l.id, l.code, l.kind, l.zone, i.name AS fixed FROM locations l LEFT JOIN items i ON i.id = l.fixed_item_id
            WHERE l.site_id = $site AND l.is_active = 1 ORDER BY l.code
            """, r => new { Id = r.Guid("id"), Code = r.Str("code"), Kind = r.Str("kind"), Zone = r.StrOrNull("zone"), Fixed = r.StrOrNull("fixed") },
            null, ("$site", siteId));
        var site = await c.ScalarAsync<string?>("SELECT name FROM sites WHERE id = $id", null, ("$id", siteId)) ?? "";
        return rows.Where(r => locationIds is not { Count: > 0 } || locationIds.Contains(r.Id))
            .Select(r => new LabelData(LabelKinds.Location, site, string.Join(" · ", new[] { r.Zone, r.Fixed }.Where(x => !string.IsNullOrWhiteSpace(x))),
                r.Code, Big: r.Code)).ToList();
    }

    /// <summary>Los bultos de un pedido: "2/3", cliente, peso y contenido resumido.</summary>
    public async Task<IReadOnlyList<LabelData>> ForPackagesAsync(Guid orderId, CancellationToken ct = default)
    {
        await using var c = await Db.OpenAsync(ct);
        var order = await c.FirstOrDefaultAsync("""
            SELECT o.number, o.site_id, p.name AS customer FROM orders o LEFT JOIN parties p ON p.id = o.customer_id WHERE o.id = $id
            """, r => new { Number = r.Str("number"), Site = r.Guid("site_id"), Customer = r.StrOrNull("customer") }, null, ("$id", orderId));
        Require(order is not null, T("error.el_pedido_no_existe"));
        await RequireSiteAccessAsync(c, null, order!.Site);
        var packages = await c.QueryAsync("""
            SELECT p.number, p.weight_kg, (SELECT SUM(l.quantity) FROM order_package_lines l WHERE l.package_id = p.id) AS units
            FROM order_packages p WHERE p.order_id = $id ORDER BY p.number
            """, r => new { Number = r.Int("number"), Weight = r.DecOrNull("weight_kg"), Units = r.DecOrNull("units") ?? 0 }, null, ("$id", orderId));
        Require(packages.Count > 0, T("error.pedido_sin_bultos"));
        return packages.Select(p => new LabelData(LabelKinds.Package, order.Customer ?? order.Number, T("etiquetas.pedido_unidades", order.Number, p.Units.ToString("0.##")),
            $"{order.Number}-{p.Number}", Big: $"{p.Number}/{packages.Count}", Footer: p.Weight is { } kg ? $"{kg:0.##} kg" : null)).ToList();
    }

    /// <summary>El código de barras del artículo si se puede imprimir en Code 128 (ASCII); si no, null.</summary>
    private static string? Barcodeable(string? barcode) =>
        string.IsNullOrWhiteSpace(barcode) || barcode.Any(ch => ch < 32 || ch > 126) ? null : barcode.Trim();
}
