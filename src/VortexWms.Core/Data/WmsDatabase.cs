using Microsoft.Data.Sqlite;

namespace VortexWms.Core.Data;

/// <summary>
/// Base local del WMS (SQLite, una por organización). Guarda la copia de los datos maestros del hub, las
/// existencias y documentos propios del WMS, y la cola de sincronización, para que todo funcione sin conexión y
/// cada operación quede en la misma transacción que su evento.
/// </summary>
public sealed class WmsDatabase
{
    public WmsDatabase(string path)
    {
        Path = path;
        ConnectionString = new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = true,
            DefaultTimeout = 30,
        }.ToString();
    }

    public string Path { get; }
    public string ConnectionString { get; }

    public async Task<SqliteConnection> OpenAsync(CancellationToken ct = default)
    {
        var connection = new SqliteConnection(ConnectionString);
        await connection.OpenAsync(ct);
        await connection.ExecAsync("PRAGMA foreign_keys = OFF; PRAGMA synchronous = NORMAL;");
        return connection;
    }

    /// <summary>Crea o actualiza el esquema. Cada versión se aplica una sola vez (<c>PRAGMA user_version</c>).</summary>
    public async Task MigrateAsync(CancellationToken ct = default)
    {
        var directory = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(Path));
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        await using var connection = await OpenAsync(ct);
        await connection.ExecAsync("PRAGMA journal_mode = WAL;");
        var current = await connection.ScalarAsync<long>("PRAGMA user_version;");
        for (var version = (int)current; version < Schema.Migrations.Length; version++)
        {
            await using var tx = connection.BeginTransaction();
            await connection.ExecAsync(Schema.Migrations[version], tx);
            await connection.ExecAsync($"PRAGMA user_version = {version + 1};", tx);
            await tx.CommitAsync(ct);
        }
    }
}

internal static class Schema
{
    /// <summary>Migraciones en orden. Nunca modificar una ya publicada: agregar una nueva al final.</summary>
    public static readonly string[] Migrations =
    [
        """
        -- Copia local de datos maestros del hub (dueño: hub). pending = cambio local aún no confirmado por el hub.
        CREATE TABLE items (
            id TEXT PRIMARY KEY, sku TEXT NOT NULL, name TEXT NOT NULL, kind TEXT NOT NULL, description TEXT,
            unit TEXT NOT NULL, barcode TEXT, category TEXT, price REAL NOT NULL, currency_code TEXT NOT NULL,
            tax_code TEXT, track_inventory INTEGER NOT NULL, is_active INTEGER NOT NULL, version INTEGER NOT NULL,
            is_deleted INTEGER NOT NULL DEFAULT 0, pending INTEGER NOT NULL DEFAULT 0, updated_at TEXT NOT NULL);
        CREATE INDEX ix_items_sku ON items (sku);
        CREATE INDEX ix_items_barcode ON items (barcode);
        CREATE INDEX ix_items_category ON items (category);

        CREATE TABLE sites (
            id TEXT PRIMARY KEY, code TEXT NOT NULL, name TEXT NOT NULL, kind TEXT NOT NULL, phone TEXT,
            address_line TEXT, city TEXT, country_code TEXT, is_active INTEGER NOT NULL, version INTEGER NOT NULL,
            is_deleted INTEGER NOT NULL DEFAULT 0, pending INTEGER NOT NULL DEFAULT 0, updated_at TEXT NOT NULL);

        CREATE TABLE parties (
            id TEXT PRIMARY KEY, name TEXT NOT NULL, trade_name TEXT, kind TEXT NOT NULL, document_type TEXT,
            document_number TEXT, is_customer INTEGER NOT NULL, is_supplier INTEGER NOT NULL, email TEXT, phone TEXT,
            city TEXT, is_active INTEGER NOT NULL, version INTEGER NOT NULL, is_deleted INTEGER NOT NULL DEFAULT 0,
            pending INTEGER NOT NULL DEFAULT 0, updated_at TEXT NOT NULL);
        CREATE INDEX ix_parties_document ON parties (document_number);

        CREATE TABLE company (
            id INTEGER PRIMARY KEY CHECK (id = 1), legal_name TEXT NOT NULL, trade_name TEXT, tax_id TEXT,
            country_code TEXT NOT NULL, currency_code TEXT NOT NULL, version INTEGER NOT NULL);

        -- Datos propios del WMS (dueño: WMS). Se referencian siempre por el Id del hub.
        CREATE TABLE stock (
            site_id TEXT NOT NULL, item_id TEXT NOT NULL, on_hand REAL NOT NULL DEFAULT 0,
            reserved REAL NOT NULL DEFAULT 0, bin TEXT, reorder_point REAL, updated_at TEXT NOT NULL,
            PRIMARY KEY (site_id, item_id));
        CREATE INDEX ix_stock_item ON stock (item_id);

        CREATE TABLE movements (
            id TEXT PRIMARY KEY, occurred_at TEXT NOT NULL, kind TEXT NOT NULL, site_id TEXT NOT NULL,
            item_id TEXT NOT NULL, quantity REAL NOT NULL, on_hand_after REAL NOT NULL, reason TEXT,
            document_id TEXT, reference TEXT, user_name TEXT, note TEXT);
        CREATE INDEX ix_movements_time ON movements (occurred_at);
        CREATE INDEX ix_movements_item ON movements (item_id, site_id);
        CREATE INDEX ix_movements_document ON movements (document_id);

        CREATE TABLE orders (
            id TEXT PRIMARY KEY, number TEXT NOT NULL, source TEXT NOT NULL, customer_id TEXT, site_id TEXT NOT NULL,
            status TEXT NOT NULL, total REAL NOT NULL, currency_code TEXT NOT NULL, created_at TEXT NOT NULL,
            dispatched_at TEXT, shipment_id TEXT, carrier TEXT, tracking_number TEXT);
        CREATE INDEX ix_orders_status ON orders (status, created_at);
        CREATE TABLE order_lines (
            order_id TEXT NOT NULL, line_no INTEGER NOT NULL, item_id TEXT NOT NULL, quantity REAL NOT NULL,
            unit_price REAL NOT NULL, PRIMARY KEY (order_id, line_no));

        CREATE TABLE receipts (
            id TEXT PRIMARY KEY, number TEXT NOT NULL, purchase_order_id TEXT, supplier_id TEXT, site_id TEXT NOT NULL,
            status TEXT NOT NULL, created_at TEXT NOT NULL, received_at TEXT);
        CREATE INDEX ix_receipts_status ON receipts (status, created_at);
        CREATE UNIQUE INDEX ux_receipts_po ON receipts (purchase_order_id) WHERE purchase_order_id IS NOT NULL;
        CREATE TABLE receipt_lines (
            receipt_id TEXT NOT NULL, line_no INTEGER NOT NULL, item_id TEXT NOT NULL, expected_qty REAL NOT NULL,
            received_qty REAL NOT NULL DEFAULT 0, unit_cost REAL, PRIMARY KEY (receipt_id, line_no));

        CREATE TABLE document_sequences (prefix TEXT PRIMARY KEY, value INTEGER NOT NULL);

        -- Integración: eventos ya aplicados (idempotencia) y estado de VortexSync (cursores, cola, rechazos).
        CREATE TABLE processed_events (
            event_id TEXT PRIMARY KEY, type TEXT NOT NULL, processed_at TEXT NOT NULL, note TEXT);
        CREATE TABLE sync_cursors (stream TEXT PRIMARY KEY, cursor INTEGER NOT NULL);
        CREATE TABLE sync_outbox (seq INTEGER PRIMARY KEY AUTOINCREMENT, id TEXT NOT NULL UNIQUE, entry TEXT NOT NULL);
        CREATE TABLE sync_failed (
            id TEXT PRIMARY KEY, entry TEXT NOT NULL, error_code TEXT NOT NULL, error TEXT NOT NULL, failed_at TEXT NOT NULL);
        """,
        // 2: base común de la suite. Impuesto y zona horaria de la empresa (del hub) y bitácora de auditoría.
        """
        ALTER TABLE company ADD COLUMN default_tax_rate REAL;
        ALTER TABLE company ADD COLUMN time_zone TEXT;
        """ + VortexApi.Suite.Auditing.AuditSchema.Sql,
        // 3: fase 0. Conflictos entre equipos (dos equipos hicieron lo mismo sin conexión con datos distintos; gana la
        // versión que el hub recibió primero) y versión de los ajustes por artículo y sede (ubicación y mínimo).
        """
        CREATE TABLE sync_conflicts (
            id TEXT PRIMARY KEY, occurred_at TEXT NOT NULL, kind TEXT NOT NULL, document_id TEXT, reference TEXT,
            detail TEXT, dismissed INTEGER NOT NULL DEFAULT 0);
        CREATE INDEX ix_sync_conflicts_open ON sync_conflicts (dismissed, occurred_at);
        ALTER TABLE stock ADD COLUMN settings_updated_at TEXT;
        CREATE INDEX ix_processed_events_at ON processed_events (processed_at);
        """,
        // 4: fase 1, kardex valorizado (costo promedio ponderado móvil por artículo y sede). Los montos van como texto
        // decimal exacto. input_cost: costo de una entrada con costo conocido (NULL = al promedio vigente).
        // operation_type: Tabla 12 de SUNAT, para el PLE de la fase 8. sale_amount: importe de venta, para el margen.
        """
        ALTER TABLE stock ADD COLUMN avg_cost TEXT;
        ALTER TABLE movements ADD COLUMN input_cost TEXT;
        ALTER TABLE movements ADD COLUMN unit_cost TEXT;
        ALTER TABLE movements ADD COLUMN total_cost TEXT;
        ALTER TABLE movements ADD COLUMN avg_cost_after TEXT;
        ALTER TABLE movements ADD COLUMN value_after TEXT;
        ALTER TABLE movements ADD COLUMN sale_amount TEXT;
        ALTER TABLE movements ADD COLUMN operation_type TEXT;
        CREATE INDEX ix_movements_valuation ON movements (site_id, item_id, occurred_at, id);
        UPDATE movements SET input_cost = (
            SELECT CAST(l.unit_cost AS TEXT) FROM receipt_lines l
            WHERE l.receipt_id = movements.document_id AND l.item_id = movements.item_id AND l.unit_cost IS NOT NULL LIMIT 1)
        WHERE kind = 'receipt';
        UPDATE movements SET operation_type = CASE
            WHEN kind IN ('sale', 'shipment') THEN '01' WHEN kind = 'receipt' THEN '02'
            WHEN kind IN ('sale_void', 'sale_return') THEN '05'
            WHEN kind IN ('transfer_in', 'transfer_out') THEN '11'
            WHEN kind = 'adjustment' AND reason IN ('damage', 'loss') THEN '13'
            WHEN kind = 'adjustment' THEN '28' ELSE '99' END;
        """,
        // 5: fase 2, artículos de almacén (campos del hub). presentations: JSON [{code,name,factor,barcode,unitCode}].
        // item_barcodes: índice de todos los códigos de un artículo (unidad y presentaciones) para el escaneo; se
        // reconstruye desde items cada vez que el artículo cambia.
        """
        ALTER TABLE items ADD COLUMN unit_code TEXT;
        ALTER TABLE items ADD COLUMN weight_kg TEXT;
        ALTER TABLE items ADD COLUMN tracking TEXT NOT NULL DEFAULT 'None';
        ALTER TABLE items ADD COLUMN presentations TEXT;
        CREATE TABLE item_barcodes (
            barcode TEXT NOT NULL, item_id TEXT NOT NULL, presentation_code TEXT, PRIMARY KEY (barcode, item_id));
        CREATE INDEX ix_item_barcodes_item ON item_barcodes (item_id);
        INSERT INTO item_barcodes (barcode, item_id) SELECT barcode, id FROM items WHERE barcode IS NOT NULL AND barcode <> '';
        """,
        // 6: fase 3, lotes, vencimientos y series. stock_lots: saldo detallado por sede, artículo, ubicación (vacía hasta la
        // fase 4) y lote (lot_code '' = sin lote: lo que salió sin poder asignarse a un lote). stock sigue siendo el total.
        // movement_lots / movement_serials: qué lotes y series movió cada movimiento (trazabilidad). item_shelf_life: vida
        // útil y días mínimos al recibir, propios del WMS.
        """
        CREATE TABLE stock_lots (
            site_id TEXT NOT NULL, item_id TEXT NOT NULL, location TEXT NOT NULL DEFAULT '', lot_code TEXT NOT NULL,
            expires_at TEXT, quantity REAL NOT NULL DEFAULT 0, status TEXT NOT NULL DEFAULT 'available', status_updated_at TEXT,
            PRIMARY KEY (site_id, item_id, location, lot_code));
        CREATE INDEX ix_stock_lots_item ON stock_lots (item_id, site_id);
        CREATE INDEX ix_stock_lots_expiry ON stock_lots (expires_at);
        CREATE INDEX ix_stock_lots_code ON stock_lots (lot_code);

        CREATE TABLE serials (
            item_id TEXT NOT NULL, serial TEXT NOT NULL, site_id TEXT, lot_code TEXT, status TEXT NOT NULL, updated_at TEXT NOT NULL,
            PRIMARY KEY (item_id, serial));
        CREATE INDEX ix_serials_serial ON serials (serial);

        CREATE TABLE movement_lots (
            movement_id TEXT NOT NULL, lot_code TEXT NOT NULL, expires_at TEXT, quantity REAL NOT NULL,
            PRIMARY KEY (movement_id, lot_code));
        CREATE INDEX ix_movement_lots_code ON movement_lots (lot_code);
        CREATE TABLE movement_serials (
            movement_id TEXT NOT NULL, serial TEXT NOT NULL, PRIMARY KEY (movement_id, serial));
        CREATE INDEX ix_movement_serials_serial ON movement_serials (serial);

        CREATE TABLE item_shelf_life (
            item_id TEXT PRIMARY KEY, shelf_life_days INTEGER, min_days_on_receipt INTEGER, updated_at TEXT NOT NULL);
        """,
        // 7: fase 4a, ubicaciones. locations: mapa por sede (registro del WMS, gana el más nuevo). location_stock: cuánto
        // hay de cada artículo en cada ubicación; lo que no está en ninguna es stock.on_hand menos la suma (por guardar).
        // relocations: historial de traslados internos (stock.relocated), con Id para aplicarlos una sola vez.
        """
        CREATE TABLE locations (
            id TEXT PRIMARY KEY, site_id TEXT NOT NULL, code TEXT NOT NULL, kind TEXT NOT NULL, zone TEXT, aisle TEXT, rack TEXT,
            level TEXT, position TEXT, fixed_item_id TEXT, is_active INTEGER NOT NULL DEFAULT 1, updated_at TEXT NOT NULL);
        CREATE UNIQUE INDEX ux_locations_code ON locations (site_id, code);
        CREATE INDEX ix_locations_fixed ON locations (fixed_item_id);

        CREATE TABLE location_stock (
            site_id TEXT NOT NULL, location_id TEXT NOT NULL, item_id TEXT NOT NULL, quantity REAL NOT NULL DEFAULT 0,
            PRIMARY KEY (site_id, location_id, item_id));
        CREATE INDEX ix_location_stock_item ON location_stock (item_id, site_id);

        CREATE TABLE relocations (
            id TEXT PRIMARY KEY, occurred_at TEXT NOT NULL, site_id TEXT NOT NULL, item_id TEXT NOT NULL, from_location_id TEXT,
            to_location_id TEXT, quantity REAL NOT NULL, user_name TEXT, reference TEXT);
        CREATE INDEX ix_relocations_item ON relocations (item_id, site_id, occurred_at);
        """,
        // 8: fase 4b1, picking y despachos parciales. Lo tomado de cada ubicación por pedido (de este equipo, hasta despachar),
        // lo ya despachado por línea y los despachos de cada pedido (un pedido puede salir en partes). Un pedido que llega
        // por aprobar (supera el crédito del cliente en el ERP) no reserva ni se prepara hasta order.approved.
        """
        ALTER TABLE orders ADD COLUMN approval TEXT;
        ALTER TABLE order_lines ADD COLUMN shipped REAL NOT NULL DEFAULT 0;
        CREATE TABLE order_picks (
            id TEXT PRIMARY KEY, order_id TEXT NOT NULL, item_id TEXT NOT NULL, location_id TEXT, quantity REAL NOT NULL,
            picked_at TEXT NOT NULL, user_name TEXT, shipment_id TEXT);
        CREATE INDEX ix_order_picks_order ON order_picks (order_id);
        CREATE TABLE order_shipments (
            id TEXT PRIMARY KEY, order_id TEXT NOT NULL, number TEXT NOT NULL, dispatched_at TEXT NOT NULL, partial INTEGER NOT NULL);
        CREATE INDEX ix_order_shipments_order ON order_shipments (order_id);
        """,
        // 9: fase 4b2. Mínimo y máximo de las ubicaciones de picking (reabastecimiento); tareas por operario y operarios
        // conocidos (registros del WMS, gana el más nuevo); bultos de cada pedido con su peso y contenido (de este equipo,
        // como lo tomado, hasta despacharse).
        """
        ALTER TABLE locations ADD COLUMN min_qty REAL;
        ALTER TABLE locations ADD COLUMN max_qty REAL;
        CREATE TABLE tasks (
            id TEXT PRIMARY KEY, site_id TEXT NOT NULL, kind TEXT NOT NULL, title TEXT NOT NULL, order_id TEXT, location_id TEXT,
            item_id TEXT, quantity REAL, assignee_id TEXT, assignee_name TEXT, status TEXT NOT NULL, notes TEXT, created_by TEXT,
            created_at TEXT NOT NULL, done_at TEXT, done_by TEXT, updated_at TEXT NOT NULL);
        CREATE INDEX ix_tasks_assignee ON tasks (assignee_id, status);
        CREATE INDEX ix_tasks_order ON tasks (order_id);
        CREATE TABLE operators (id TEXT PRIMARY KEY, name TEXT NOT NULL, role TEXT, updated_at TEXT NOT NULL);
        CREATE TABLE order_packages (
            id TEXT PRIMARY KEY, order_id TEXT NOT NULL, number INTEGER NOT NULL, weight_kg REAL, notes TEXT, shipment_id TEXT,
            created_at TEXT NOT NULL, user_name TEXT);
        CREATE INDEX ix_order_packages_order ON order_packages (order_id);
        CREATE TABLE order_package_lines (
            package_id TEXT NOT NULL, item_id TEXT NOT NULL, quantity REAL NOT NULL, PRIMARY KEY (package_id, item_id));
        """,
    ];
}
