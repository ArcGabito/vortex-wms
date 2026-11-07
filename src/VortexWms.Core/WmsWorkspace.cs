using VortexWms.Core.Data;
using VortexWms.Core.Domain;
using VortexWms.Core.Services;
using VortexWms.Core.Sync;

namespace VortexWms.Core;

/// <summary>
/// Todo el WMS de una organización: su base local y los servicios sobre ella. Cada organización tiene su propia
/// base, así un equipo compartido por dos empresas nunca mezcla datos.
/// </summary>
public sealed class WmsWorkspace
{
    private WmsWorkspace(WmsDatabase db, IWmsContext context, TimeProvider clock)
    {
        Database = db;
        Master = new MasterDataService(db, context, clock);
        Inventory = new InventoryService(db, context, clock);
        Orders = new OrderService(db, context, clock);
        Receipts = new ReceiptService(db, context, clock);
        Lots = new LotService(db, context, clock);
        Audit = new AuditService(db, context, clock);
        Conflicts = new SyncConflictService(db, context, clock);
        SyncStore = new SqliteSyncStore(db);
        SyncHandlers = new WmsSyncHandlers(db, Master, Orders, Receipts, clock);
    }

    public WmsDatabase Database { get; }
    public MasterDataService Master { get; }
    public InventoryService Inventory { get; }
    public OrderService Orders { get; }
    public ReceiptService Receipts { get; }
    public LotService Lots { get; }
    public AuditService Audit { get; }
    public SyncConflictService Conflicts { get; }
    public SqliteSyncStore SyncStore { get; }
    public WmsSyncHandlers SyncHandlers { get; }

    public static async Task<WmsWorkspace> OpenAsync(string databasePath, IWmsContext context, TimeProvider? clock = null,
        CancellationToken ct = default)
    {
        var db = new WmsDatabase(databasePath);
        await db.MigrateAsync(ct);
        var workspace = new WmsWorkspace(db, context, clock ?? TimeProvider.System);
        await workspace.Inventory.ValueExistingMovementsAsync(ct);
        await workspace.SyncHandlers.PruneProcessedEventsAsync(ct);
        return workspace;
    }
}
