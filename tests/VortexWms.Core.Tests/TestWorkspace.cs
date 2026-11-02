using System.Text.Json;
using Microsoft.Extensions.Time.Testing;
using VortexApi.Client;
using VortexApi.Client.Sync;
using VortexWms.Core.Domain;

namespace VortexWms.Core.Tests;

/// <summary>Workspace sobre una base SQLite temporal, con un almacén y artículos ya sincronizados desde el "hub".</summary>
public sealed class TestWorkspace(string deviceTag = "TEST") : IAsyncLifetime
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"vortex-wms-test-{Guid.NewGuid():N}.db");

    public FakeTimeProvider Clock { get; } = new(new DateTimeOffset(2026, 11, 1, 15, 0, 0, TimeSpan.Zero));
    public WmsWorkspace Wms { get; private set; } = null!;

    public Guid Central { get; } = Guid.Parse("11111111-0000-0000-0000-000000000001");
    public Guid Store { get; } = Guid.Parse("11111111-0000-0000-0000-000000000002");
    public Guid Coffee { get; } = Guid.Parse("22222222-0000-0000-0000-000000000001");
    public Guid Sugar { get; } = Guid.Parse("22222222-0000-0000-0000-000000000002");
    public Guid Delivery { get; } = Guid.Parse("22222222-0000-0000-0000-000000000003");

    public async Task InitializeAsync()
    {
        Wms = await WmsWorkspace.OpenAsync(_path, new Context(deviceTag), Clock);
        await Wms.Master.ApplySiteAsync(Site(Central, "ALM-01", "Almacén central", "Warehouse"));
        await Wms.Master.ApplySiteAsync(Site(Store, "TDA-01", "Tienda Miraflores", "Store"));
        await Wms.Master.ApplyItemAsync(Item(Coffee, "CAFE-250", "Café tostado 250 g"));
        await Wms.Master.ApplyItemAsync(Item(Sugar, "AZU-1K", "Azúcar rubia 1 kg"));
        await Wms.Master.ApplyItemAsync(Item(Delivery, "SRV-DELIVERY", "Envío a domicilio", trackInventory: false));
    }

    public Task DisposeAsync()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        foreach (var file in new[] { _path, _path + "-wal", _path + "-shm" })
            File.Delete(file);
        return Task.CompletedTask;
    }

    public async Task<decimal> OnHandAsync(Guid site, Guid item) =>
        (await Wms.Inventory.GetStockRowAsync(site, item))?.OnHand ?? 0;

    public async Task<List<VortexOutboxEntry>> OutboxAsync() => (await Wms.SyncStore.PeekAsync(1000)).ToList();

    public static VortexMasterChange<VortexItem> Item(Guid id, string sku, string name, int version = 1, bool trackInventory = true,
        VortexChangeOperation op = VortexChangeOperation.Created) =>
        new(version, id, op, version, DateTimeOffset.UtcNow, "HUB",
            new VortexItem(id, sku, name, "Product", null, "unidad", null, "Abarrotes", 18.50m, "PEN", "igv", trackInventory, true,
                new VortexMasterMeta(version, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, null)));

    public static VortexMasterChange<VortexSite> Site(Guid id, string code, string name, string kind) =>
        new(1, id, VortexChangeOperation.Created, 1, DateTimeOffset.UtcNow, "HUB",
            new VortexSite(id, code, name, kind, null, new VortexAddress(null, null, "Lima", null, null, "PE"), true,
                new VortexMasterMeta(1, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, null)));

    public static VortexEvent Event(string type, object data, string source = "POS", int schemaVersion = 1, Guid? id = null) =>
        new(id ?? Guid.NewGuid(), 1, type, source, schemaVersion, null, null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, null,
            JsonSerializer.SerializeToElement(data, VortexJson.Options));

    private sealed class Context(string deviceTag) : IWmsContext
    {
        public Guid? UserId => Guid.Parse("00000000-0000-0000-0000-000000000001");
        public string UserName => "Luis";
        public string DeviceTag => deviceTag;
        public Guid? DefaultSiteId => null;
    }
}
