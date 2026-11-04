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
    ];
}
