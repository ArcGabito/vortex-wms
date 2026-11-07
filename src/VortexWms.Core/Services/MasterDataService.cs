using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;
using VortexApi.Client;
using VortexWms.Core.Data;
using VortexWms.Core.Domain;
using VortexWms.Core.Sync;

using static VortexWms.Core.Texts.CoreText;

namespace VortexWms.Core.Services;

/// <summary>
/// Artículos, sedes, clientes/proveedores y empresa: datos del hub. El WMS guarda una copia que se actualiza
/// <b>solo desde el feed</b> (por Id y versión). Las altas y cambios se guardan localmente como pendientes y se
/// encolan para el hub; nunca existen solo en la base local.
/// </summary>
public sealed partial class MasterDataService(WmsDatabase db, IWmsContext context, TimeProvider clock)
    : WmsService(db, context, clock)
{
    // ---------- Feed del hub ----------

    public async Task ApplyItemAsync(VortexMasterChange<VortexItem> change, CancellationToken ct = default)
    {
        var d = change.Data;
        await using var c = await Db.OpenAsync(ct);
        await c.ExecAsync("""
            INSERT INTO items (id, sku, name, kind, description, unit, barcode, category, price, currency_code, tax_code,
                               track_inventory, is_active, version, is_deleted, pending, updated_at, unit_code, weight_kg,
                               tracking, presentations)
            VALUES ($id, $sku, $name, $kind, $desc, $unit, $barcode, $category, $price, $currency, $tax, $track, $active,
                    $version, $deleted, 0, $updated, $unitCode, $weight, $tracking, $presentations)
            ON CONFLICT (id) DO UPDATE SET
                sku = excluded.sku, name = excluded.name, kind = excluded.kind, description = excluded.description,
                unit = excluded.unit, barcode = excluded.barcode, category = excluded.category, price = excluded.price,
                currency_code = excluded.currency_code, tax_code = excluded.tax_code,
                track_inventory = excluded.track_inventory, is_active = excluded.is_active, version = excluded.version,
                is_deleted = excluded.is_deleted, pending = 0, updated_at = excluded.updated_at,
                unit_code = excluded.unit_code, weight_kg = excluded.weight_kg, tracking = excluded.tracking,
                presentations = excluded.presentations
            WHERE excluded.version > items.version
            """, null, ("$id", change.EntityId), ("$sku", d.Sku), ("$name", d.Name), ("$kind", d.Kind), ("$desc", d.Description),
            ("$unit", d.Unit), ("$barcode", d.Barcode), ("$category", d.Category), ("$price", d.Price),
            ("$currency", d.CurrencyCode), ("$tax", d.TaxCode), ("$track", d.TrackInventory), ("$active", d.IsActive),
            ("$version", change.Version), ("$deleted", Deleted(change.Operation, d.Meta)), ("$updated", d.Meta.UpdatedAt),
            ("$unitCode", d.UnitCode), ("$weight", d.WeightKg?.ToString(CultureInfo.InvariantCulture)),
            ("$tracking", d.Tracking ?? ItemTrackings.None),
            ("$presentations", PresentationsJson(d.Presentations?.Select(p => new ItemPresentation(p.Code, p.Name, p.Factor, p.Barcode, p.UnitCode)))));
        await RebuildBarcodesAsync(c, null, change.EntityId);
    }

    public async Task ApplySiteAsync(VortexMasterChange<VortexSite> change, CancellationToken ct = default)
    {
        var d = change.Data;
        await using var c = await Db.OpenAsync(ct);
        await c.ExecAsync("""
            INSERT INTO sites (id, code, name, kind, phone, address_line, city, country_code, is_active, version, is_deleted, pending, updated_at)
            VALUES ($id, $code, $name, $kind, $phone, $line, $city, $country, $active, $version, $deleted, 0, $updated)
            ON CONFLICT (id) DO UPDATE SET
                code = excluded.code, name = excluded.name, kind = excluded.kind, phone = excluded.phone,
                address_line = excluded.address_line, city = excluded.city, country_code = excluded.country_code,
                is_active = excluded.is_active, version = excluded.version, is_deleted = excluded.is_deleted, pending = 0,
                updated_at = excluded.updated_at
            WHERE excluded.version > sites.version
            """, null, ("$id", change.EntityId), ("$code", d.Code), ("$name", d.Name), ("$kind", d.Kind), ("$phone", d.Phone),
            ("$line", d.Address?.Line), ("$city", d.Address?.City), ("$country", d.Address?.CountryCode),
            ("$active", d.IsActive), ("$version", change.Version), ("$deleted", Deleted(change.Operation, d.Meta)),
            ("$updated", d.Meta.UpdatedAt));
    }

    public async Task ApplyPartyAsync(VortexMasterChange<VortexParty> change, CancellationToken ct = default)
    {
        var d = change.Data;
        await using var c = await Db.OpenAsync(ct);
        await c.ExecAsync("""
            INSERT INTO parties (id, name, trade_name, kind, document_type, document_number, is_customer, is_supplier, email,
                                 phone, city, is_active, version, is_deleted, pending, updated_at)
            VALUES ($id, $name, $trade, $kind, $doctype, $docnum, $customer, $supplier, $email, $phone, $city, $active,
                    $version, $deleted, 0, $updated)
            ON CONFLICT (id) DO UPDATE SET
                name = excluded.name, trade_name = excluded.trade_name, kind = excluded.kind,
                document_type = excluded.document_type, document_number = excluded.document_number,
                is_customer = excluded.is_customer, is_supplier = excluded.is_supplier, email = excluded.email,
                phone = excluded.phone, city = excluded.city, is_active = excluded.is_active, version = excluded.version,
                is_deleted = excluded.is_deleted, pending = 0, updated_at = excluded.updated_at
            WHERE excluded.version > parties.version
            """, null, ("$id", change.EntityId), ("$name", d.Name), ("$trade", d.TradeName), ("$kind", d.Kind),
            ("$doctype", d.DocumentType), ("$docnum", d.DocumentNumber), ("$customer", d.IsCustomer),
            ("$supplier", d.IsSupplier), ("$email", d.Email), ("$phone", d.Phone), ("$city", d.Address?.City),
            ("$active", d.IsActive), ("$version", change.Version), ("$deleted", Deleted(change.Operation, d.Meta)),
            ("$updated", d.Meta.UpdatedAt));
    }

    public async Task ApplyCompanyAsync(VortexMasterChange<VortexCompany> change, CancellationToken ct = default)
    {
        var d = change.Data;
        await using var c = await Db.OpenAsync(ct);
        await c.ExecAsync("""
            INSERT INTO company (id, legal_name, trade_name, tax_id, country_code, currency_code, default_tax_rate, time_zone, version)
            VALUES (1, $legal, $trade, $tax, $country, $currency, $rate, $tz, $version)
            ON CONFLICT (id) DO UPDATE SET legal_name = excluded.legal_name, trade_name = excluded.trade_name,
                tax_id = excluded.tax_id, country_code = excluded.country_code, currency_code = excluded.currency_code,
                default_tax_rate = excluded.default_tax_rate, time_zone = excluded.time_zone, version = excluded.version
            WHERE excluded.version > company.version
            """, null, ("$legal", d.LegalName), ("$trade", d.TradeName), ("$tax", d.TaxId), ("$country", d.CountryCode),
            ("$currency", d.CurrencyCode), ("$rate", d.DefaultTaxRate), ("$tz", d.TimeZone), ("$version", change.Version));
    }

    private static bool Deleted(VortexChangeOperation operation, VortexMasterMeta meta) =>
        operation == VortexChangeOperation.Deleted || meta.IsDeleted;

    // ---------- Altas y cambios (se encolan para el hub) ----------

    public Task<Guid> CreateItemAsync(ItemDraft draft, CancellationToken ct = default) => SaveItemAsync(null, draft, ct);

    public Task<Guid> UpdateItemAsync(Guid id, ItemDraft draft, CancellationToken ct = default) => SaveItemAsync(id, draft, ct);

    private Task<Guid> SaveItemAsync(Guid? existingId, ItemDraft draft, CancellationToken ct)
    {
        RequirePermission(WmsPermissions.EditCatalog);
        var sku = draft.Sku.Trim().ToUpperInvariant();
        Require(sku.Length > 0, T("error.el_sku_es_obligatorio"));
        Require(!string.IsNullOrWhiteSpace(draft.Name), T("error.el_nombre_es_obligatorio"));
        Require(!string.IsNullOrWhiteSpace(draft.Unit), T("error.la_unidad_es_obligatoria"));
        Require(draft.Price >= 0, T("error.el_precio_no_puede_ser"));
        Require(draft.CurrencyCode.Trim().Length == 3, T("error.la_moneda_debe_ser_un"));
        var presentations = NormalizePresentations(draft);

        return InTransactionAsync(async (c, tx, now) =>
        {
            var duplicate = await c.ScalarAsync<long>(
                "SELECT COUNT(*) FROM items WHERE sku = $sku AND is_deleted = 0 AND id <> $id", tx,
                ("$sku", sku), ("$id", existingId ?? Guid.Empty));
            Require(duplicate == 0, T("error.ya_existe_un_articulo_con", sku));

            // Al escanear, un código debe llevar a un solo artículo.
            foreach (var code in presentations.Select(p => p.Barcode).Append(Clean(draft.Barcode)).OfType<string>())
            {
                var other = await c.ScalarAsync<string?>("""
                    SELECT i.sku FROM item_barcodes b JOIN items i ON i.id = b.item_id
                    WHERE b.barcode = $code AND i.id <> $id AND i.is_deleted = 0 LIMIT 1
                    """, tx, ("$code", code), ("$id", existingId ?? Guid.Empty));
                Require(other is null, T("error.codigo_de_barras_en_uso", code, other));
            }

            var id = existingId ?? Guid.NewGuid();
            var version = existingId is null ? 0 : await c.ScalarAsync<int>("SELECT version FROM items WHERE id = $id", tx, ("$id", id));
            var body = new
            {
                sku, name = draft.Name.Trim(), kind = draft.Kind, description = Clean(draft.Description), unit = draft.Unit.Trim().ToLowerInvariant(),
                barcode = Clean(draft.Barcode), category = Clean(draft.Category), price = draft.Price,
                currencyCode = draft.CurrencyCode.Trim().ToUpperInvariant(), taxCode = VortexApi.Suite.Taxes.TaxCategories.Normalize(draft.TaxCode),
                trackInventory = draft.Kind == "Product" && draft.TrackInventory, isActive = draft.IsActive,
                unitCode = Clean(draft.UnitCode)?.ToUpperInvariant(), weightKg = draft.WeightKg,
                tracking = draft.Kind == "Product" && draft.TrackInventory ? draft.Tracking : ItemTrackings.None,
                presentations,
            };

            await c.ExecAsync("""
                INSERT INTO items (id, sku, name, kind, description, unit, barcode, category, price, currency_code, tax_code,
                                   track_inventory, is_active, version, is_deleted, pending, updated_at, unit_code, weight_kg,
                                   tracking, presentations)
                VALUES ($id, $sku, $name, $kind, $desc, $unit, $barcode, $category, $price, $currency, $tax, $track, $active,
                        0, 0, 1, $now, $unitCode, $weight, $tracking, $presentations)
                ON CONFLICT (id) DO UPDATE SET sku = $sku, name = $name, kind = $kind, description = $desc, unit = $unit,
                    barcode = $barcode, category = $category, price = $price, currency_code = $currency, tax_code = $tax,
                    track_inventory = $track, is_active = $active, pending = 1, updated_at = $now, unit_code = $unitCode,
                    weight_kg = $weight, tracking = $tracking, presentations = $presentations
                """, tx, ("$id", id), ("$sku", body.sku), ("$name", body.name), ("$kind", body.kind), ("$desc", body.description),
                ("$unit", body.unit), ("$barcode", body.barcode), ("$category", body.category), ("$price", body.price),
                ("$currency", body.currencyCode), ("$tax", body.taxCode), ("$track", body.trackInventory),
                ("$active", body.isActive), ("$now", now), ("$unitCode", body.unitCode),
                ("$weight", body.weightKg?.ToString(CultureInfo.InvariantCulture)), ("$tracking", body.tracking),
                ("$presentations", PresentationsJson(presentations)));
            await RebuildBarcodesAsync(c, tx, id);

            await Outbox.EnqueueUpsertAsync(c, tx, now, VortexMasterResource.Items, id, body, version > 0 ? version : null);
            return id;
        }, ct);
    }

    public Task<Guid> CreateSiteAsync(SiteDraft draft, CancellationToken ct = default)
    {
        RequirePermission(WmsPermissions.EditCatalog);
        var code = draft.Code.Trim().ToUpperInvariant();
        Require(code.Length > 0, T("error.el_codigo_es_obligatorio"));
        Require(!string.IsNullOrWhiteSpace(draft.Name), T("error.el_nombre_es_obligatorio"));

        return InTransactionAsync(async (c, tx, now) =>
        {
            var duplicate = await c.ScalarAsync<long>("SELECT COUNT(*) FROM sites WHERE code = $code AND is_deleted = 0", tx, ("$code", code));
            Require(duplicate == 0, T("error.ya_existe_una_sede_con", code));

            var id = Guid.NewGuid();
            var body = new
            {
                code, name = draft.Name.Trim(), kind = draft.Kind, phone = Clean(draft.Phone),
                address = new { line = Clean(draft.AddressLine), city = Clean(draft.City), countryCode = draft.CountryCode },
                isActive = draft.IsActive,
            };
            await c.ExecAsync("""
                INSERT INTO sites (id, code, name, kind, phone, address_line, city, country_code, is_active, version, is_deleted, pending, updated_at)
                VALUES ($id, $code, $name, $kind, $phone, $line, $city, $country, $active, 0, 0, 1, $now)
                """, tx, ("$id", id), ("$code", code), ("$name", body.name), ("$kind", body.kind), ("$phone", body.phone),
                ("$line", body.address.line), ("$city", body.address.city), ("$country", body.address.countryCode),
                ("$active", body.isActive), ("$now", now));
            await Outbox.EnqueueUpsertAsync(c, tx, now, VortexMasterResource.Sites, id, body);
            return id;
        }, ct);
    }

    public Task<Guid> CreatePartyAsync(PartyDraft draft, CancellationToken ct = default)
    {
        RequirePermission(WmsPermissions.CreateParty);
        Require(!string.IsNullOrWhiteSpace(draft.Name), T("error.el_nombre_es_obligatorio"));
        Require(draft.IsCustomer || draft.IsSupplier, T("error.indica_si_es_cliente_proveedor"));
        var docType = Clean(draft.DocumentType)?.ToUpperInvariant();
        var docNumber = Clean(draft.DocumentNumber);
        Require((docType is null) == (docNumber is null), T("error.el_tipo_el_numero_de"));
        if (docType == "RUC")
            Require(Digits(11).IsMatch(docNumber!), T("error.el_ruc_debe_tener_11"));
        if (docType == "DNI")
            Require(Digits(8).IsMatch(docNumber!), T("error.el_dni_debe_tener_8"));

        return InTransactionAsync(async (c, tx, now) =>
        {
            if (docNumber is not null)
            {
                var existing = await c.ScalarAsync<string?>(
                    "SELECT name FROM parties WHERE document_number = $doc AND is_deleted = 0 LIMIT 1", tx, ("$doc", docNumber));
                Require(existing is null, T("error.el_documento_ya_esta_registrado", docNumber, existing));
            }

            var id = Guid.NewGuid();
            var body = new
            {
                name = draft.Name.Trim(), isCustomer = draft.IsCustomer, isSupplier = draft.IsSupplier, kind = draft.Kind,
                documentType = docType, documentNumber = docNumber, email = Clean(draft.Email), phone = Clean(draft.Phone),
                address = draft.City is null ? null : new { city = Clean(draft.City), countryCode = draft.CountryCode },
                isActive = true,
            };
            await c.ExecAsync("""
                INSERT INTO parties (id, name, trade_name, kind, document_type, document_number, is_customer, is_supplier,
                                     email, phone, city, is_active, version, is_deleted, pending, updated_at)
                VALUES ($id, $name, NULL, $kind, $doctype, $docnum, $customer, $supplier, $email, $phone, $city, 1, 0, 0, 1, $now)
                """, tx, ("$id", id), ("$name", body.name), ("$kind", body.kind), ("$doctype", docType), ("$docnum", docNumber),
                ("$customer", body.isCustomer), ("$supplier", body.isSupplier), ("$email", body.email), ("$phone", body.phone),
                ("$city", Clean(draft.City)), ("$now", now));
            await Outbox.EnqueueUpsertAsync(c, tx, now, VortexMasterResource.Parties, id, body);
            return id;
        }, ct);
    }

    // ---------- Consultas ----------

    public async Task<PagedResult<ItemRow>> GetItemsAsync(string? search, string? category, int page, int pageSize,
        bool includeInactive = true, CancellationToken ct = default)
    {
        await using var c = await Db.OpenAsync(ct);
        const string where = """
            WHERE i.is_deleted = 0 AND ($all = 1 OR i.is_active = 1)
              AND ($search IS NULL OR i.sku LIKE $like OR i.name LIKE $like
                   OR i.id IN (SELECT item_id FROM item_barcodes WHERE barcode = $search))
              AND ($category IS NULL OR i.category = $category)
            """;
        var args = new (string, object?)[]
        {
            ("$all", includeInactive), ("$search", Clean(search)), ("$like", Like(search)), ("$category", Clean(category)),
            ("$limit", pageSize), ("$offset", (page - 1) * pageSize),
        };
        var total = await c.ScalarAsync<int>($"SELECT COUNT(*) FROM items i {where}", null, args);
        var items = await c.QueryAsync($"""
            SELECT i.*, COALESCE(SUM(s.on_hand), 0) AS total_on_hand, COALESCE(SUM(s.reserved), 0) AS total_reserved
            FROM items i LEFT JOIN stock s ON s.item_id = i.id
            {where}
            GROUP BY i.id ORDER BY i.name COLLATE NOCASE LIMIT $limit OFFSET $offset
            """, ReadItem, null, args);
        return new PagedResult<ItemRow>(items, total, page, pageSize);
    }

    public async Task<IReadOnlyList<ItemRow>> SearchItemsAsync(string? term, int limit = 8, CancellationToken ct = default) =>
        (await GetItemsAsync(term, null, 1, limit, includeInactive: false, ct)).Items;

    public async Task<ItemRow?> GetItemAsync(Guid id, CancellationToken ct = default)
    {
        await using var c = await Db.OpenAsync(ct);
        return await c.FirstOrDefaultAsync("""
            SELECT i.*, COALESCE(SUM(s.on_hand), 0) AS total_on_hand, COALESCE(SUM(s.reserved), 0) AS total_reserved
            FROM items i LEFT JOIN stock s ON s.item_id = i.id WHERE i.id = $id GROUP BY i.id
            """, ReadItem, null, ("$id", id));
    }

    public async Task<IReadOnlyList<string>> GetCategoriesAsync(CancellationToken ct = default)
    {
        await using var c = await Db.OpenAsync(ct);
        return await c.QueryAsync("""
            SELECT DISTINCT category FROM items WHERE is_deleted = 0 AND category IS NOT NULL AND category <> ''
            ORDER BY category COLLATE NOCASE
            """, r => r.Str("category"));
    }

    /// <param name="mineOnly">Solo las sedes asignadas al usuario (para elegir dónde operar).</param>
    public async Task<IReadOnlyList<SiteRow>> GetSitesAsync(bool activeOnly = false, bool mineOnly = false, CancellationToken ct = default)
    {
        await using var c = await Db.OpenAsync(ct);
        return await c.QueryAsync($"""
            SELECT t.*, COUNT(s.item_id) AS sku_count, COALESCE(SUM(s.on_hand), 0) AS units
            FROM sites t LEFT JOIN stock s ON s.site_id = t.id AND s.on_hand <> 0
            WHERE t.is_deleted = 0 AND ($active = 0 OR t.is_active = 1){(mineOnly ? MySites("t.id") : "")}
            GROUP BY t.id ORDER BY CASE t.kind WHEN 'Warehouse' THEN 0 ELSE 1 END, t.code
            """, r => new SiteRow(r.Guid("id"), r.Str("code"), r.Str("name"), r.Str("kind"), r.StrOrNull("phone"),
                r.StrOrNull("address_line"), r.StrOrNull("city"), r.StrOrNull("country_code"), r.Bool("is_active"),
                r.Int("version"), r.Bool("pending"), r.Int("sku_count"), r.Dec("units")), null, ("$active", activeOnly));
    }

    public async Task<PagedResult<PartyRow>> GetPartiesAsync(string? search, bool? customers, bool? suppliers, int page,
        int pageSize, CancellationToken ct = default)
    {
        await using var c = await Db.OpenAsync(ct);
        const string where = """
            WHERE is_deleted = 0
              AND ($search IS NULL OR name LIKE $like OR trade_name LIKE $like OR document_number LIKE $like OR email LIKE $like)
              AND ($customers IS NULL OR is_customer = $customers)
              AND ($suppliers IS NULL OR is_supplier = $suppliers)
            """;
        var args = new (string, object?)[]
        {
            ("$search", Clean(search)), ("$like", Like(search)), ("$customers", customers), ("$suppliers", suppliers),
            ("$limit", pageSize), ("$offset", (page - 1) * pageSize),
        };
        var total = await c.ScalarAsync<int>($"SELECT COUNT(*) FROM parties {where}", null, args);
        var rows = await c.QueryAsync($"SELECT * FROM parties {where} ORDER BY name COLLATE NOCASE LIMIT $limit OFFSET $offset",
            ReadParty, null, args);
        return new PagedResult<PartyRow>(rows, total, page, pageSize);
    }

    public async Task<IReadOnlyList<Option>> GetPartyOptionsAsync(bool suppliers, CancellationToken ct = default)
    {
        await using var c = await Db.OpenAsync(ct);
        return await c.QueryAsync($"""
            SELECT id, name, document_number FROM parties
            WHERE is_deleted = 0 AND is_active = 1 AND {(suppliers ? "is_supplier" : "is_customer")} = 1
            ORDER BY name COLLATE NOCASE
            """, r => new Option(r.Guid("id"), r.Str("name"), r.StrOrNull("document_number")));
    }

    public async Task<CompanyInfo?> GetCompanyAsync(CancellationToken ct = default)
    {
        await using var c = await Db.OpenAsync(ct);
        return await c.FirstOrDefaultAsync("SELECT * FROM company WHERE id = 1", r => new CompanyInfo(
            r.Str("legal_name"), r.StrOrNull("trade_name"), r.StrOrNull("tax_id"), r.Str("country_code"), r.Str("currency_code"),
            r.DecOrNull("default_tax_rate"), r.StrOrNull("time_zone")));
    }

    internal static ItemRow ReadItem(SqliteDataReader r) => new(
        r.Guid("id"), r.Str("sku"), r.Str("name"), r.Str("kind"), r.StrOrNull("description"), r.Str("unit"),
        r.StrOrNull("barcode"), r.StrOrNull("category"), r.Dec("price"), r.Str("currency_code"), r.StrOrNull("tax_code"),
        r.Bool("track_inventory"), r.Bool("is_active"), r.Int("version"), r.Bool("pending"), r.Dec("total_on_hand"),
        r.Dec("total_reserved"))
    {
        UnitCode = r.StrOrNull("unit_code"),
        WeightKg = r.DecOrNull("weight_kg"),
        Tracking = r.StrOrNull("tracking") ?? ItemTrackings.None,
        Presentations = r.StrOrNull("presentations") is { } json
            ? JsonSerializer.Deserialize<List<ItemPresentation>>(json, VortexJson.Options) ?? []
            : [],
    };

    // ---------- Presentaciones y códigos de barras ----------

    /// <summary>
    /// Busca por código exacto (de la unidad o de una presentación) o por SKU. Si el código es el de una presentación,
    /// la devuelve para que la línea se registre en esa presentación (escanear la caja = 1 caja).
    /// </summary>
    public async Task<ItemScan?> ScanAsync(string code, CancellationToken ct = default)
    {
        var term = Clean(code);
        if (term is null)
            return null;
        await using var c = await Db.OpenAsync(ct);
        var hits = await c.QueryAsync("""
            SELECT b.item_id, b.presentation_code FROM item_barcodes b JOIN items i ON i.id = b.item_id
            WHERE b.barcode = $code AND i.is_deleted = 0
            UNION ALL
            SELECT id, NULL FROM items WHERE sku = $sku AND is_deleted = 0
            """, r => (Id: r.Guid("item_id"), Presentation: r.StrOrNull("presentation_code")), null,
            ("$code", term), ("$sku", term.ToUpperInvariant()));
        if (hits.Count == 0 || await GetItemAsync(hits[0].Id, ct) is not { } item)
            return null;
        return new ItemScan(item, item.PresentationByCode(hits[0].Presentation));
    }

    private static List<ItemPresentation> NormalizePresentations(ItemDraft draft)
    {
        var list = (draft.Presentations ?? []).Select(p => new ItemPresentation(
            p.Code.Trim().ToUpperInvariant(), p.Name.Trim(), p.Factor, Clean(p.Barcode), Clean(p.UnitCode)?.ToUpperInvariant())).ToList();
        foreach (var p in list)
        {
            Require(p.Code.Length > 0 && p.Name.Length > 0, T("error.la_presentacion_necesita_codigo"));
            Require(p.Factor > 0, T("error.el_factor_debe_ser_mayor"));
        }
        Require(list.Select(p => p.Code).Distinct().Count() == list.Count, T("error.presentacion_repetida"));
        var codes = list.Select(p => p.Barcode).Append(Clean(draft.Barcode)).OfType<string>().ToList();
        Require(codes.Distinct().Count() == codes.Count, T("error.codigo_de_barras_repetido"));
        return list;
    }

    private static string? PresentationsJson(IEnumerable<ItemPresentation>? presentations) =>
        presentations?.ToList() is { Count: > 0 } list ? JsonSerializer.Serialize(list, VortexJson.Options) : null;

    /// <summary>Reconstruye el índice de códigos del artículo (unidad y presentaciones) desde su fila.</summary>
    private static Task RebuildBarcodesAsync(SqliteConnection c, SqliteTransaction? tx, Guid itemId) => c.ExecAsync("""
        DELETE FROM item_barcodes WHERE item_id = $id;
        INSERT OR IGNORE INTO item_barcodes (barcode, item_id, presentation_code)
        SELECT barcode, id, NULL FROM items WHERE id = $id AND barcode IS NOT NULL AND barcode <> ''
        UNION ALL
        SELECT json_extract(p.value, '$.barcode'), i.id, json_extract(p.value, '$.code')
        FROM items i, json_each(COALESCE(i.presentations, '[]')) p
        WHERE i.id = $id AND json_extract(p.value, '$.barcode') IS NOT NULL;
        """, tx, ("$id", itemId));

    private static PartyRow ReadParty(SqliteDataReader r) => new(
        r.Guid("id"), r.Str("name"), r.StrOrNull("trade_name"), r.Str("kind"), r.StrOrNull("document_type"),
        r.StrOrNull("document_number"), r.Bool("is_customer"), r.Bool("is_supplier"), r.StrOrNull("email"),
        r.StrOrNull("phone"), r.StrOrNull("city"), r.Bool("is_active"), r.Int("version"), r.Bool("pending"));

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static Regex Digits(int count) => count == 11 ? Ruc() : Dni();

    [GeneratedRegex(@"^\d{11}$")]
    private static partial Regex Ruc();

    [GeneratedRegex(@"^\d{8}$")]
    private static partial Regex Dni();
}
