using VortexApi.Client;
using VortexApi.Client.Sessions;
using VortexApi.Client.Sync;
using VortexWms.Core.Domain;

namespace VortexWms.Core.Tests;

/// <summary>Solo corre con un hub real: <c>VORTEX_HUB_URL=http://localhost:5299/ dotnet test</c> (organización demo).</summary>
public sealed class HubFactAttribute : FactAttribute
{
    public HubFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("VORTEX_HUB_URL")))
            Skip = "Define VORTEX_HUB_URL para probar contra un hub real.";
    }
}

public sealed class HubIntegrationTests : IAsyncLifetime
{
    private readonly TestWorkspace _t = new();

    public Task InitializeAsync() => _t.InitializeAsync();
    public Task DisposeAsync() => _t.DisposeAsync();

    [HubFact]
    public async Task Altas_y_recepcion_offline_llegan_al_hub_y_vuelven_por_el_feed()
    {
        var client = new VortexApiClient(
            new HttpClient { BaseAddress = new Uri(Environment.GetEnvironmentVariable("VORTEX_HUB_URL")!) },
            new VortexApiClientOptions
            {
                ProductCode = "WMS",
                ProductKey = Environment.GetEnvironmentVariable("VORTEX_WMS_KEY") ?? "vxp_wms_dev_key_change_me",
            });
        var sessions = new VortexSessionManager(client, new InMemorySessionStore(), "vortex-wms-integration-tests");
        await sessions.LoginAsync("luis@vortex.local", "Suite-Local-2026!", "Pruebas Vortex WMS", "tests");
        var sync = _t.Wms.SyncHandlers.Register(new VortexSync(client, sessions, _t.Wms.SyncStore));

        var first = await sync.SyncAsync();
        Assert.True(first.Completed, first.InterruptedBy);

        // Todo se crea sin tocar la red: sede y artículo nuevos (datos del hub) y una recepción (dato del WMS).
        var tag = Guid.NewGuid().ToString("N")[..6].ToUpperInvariant();
        var site = await _t.Wms.Master.CreateSiteAsync(new SiteDraft($"T{tag}", $"Almacén de pruebas {tag}", "Warehouse", null, null, "Lima", "PE"));
        var item = await _t.Wms.Master.CreateItemAsync(new ItemDraft($"TEST-{tag}", "Artículo de prueba", "unidad", "Pruebas", null, 1, "PEN"));
        await _t.Wms.Receipts.ReceiveDirectAsync(site, null, [new DocumentLine(item, 5)]);

        var second = await sync.SyncAsync();
        Assert.True(second.Completed, second.InterruptedBy);
        Assert.Equal(0, second.Failed);
        Assert.Equal(0, second.PendingOutbox);
        Assert.Equal(2, second.PushedEvents);        // stock.adjusted + goods.received
        Assert.Equal(2, second.PushedMasterWrites);  // sede + artículo

        // La copia local se confirma desde el feed del hub, no al responder la escritura.
        Assert.False((await _t.Wms.Master.GetItemAsync(item))!.Pending);
        Assert.Contains(await _t.Wms.Master.GetSitesAsync(), s => s.Id == site && !s.Pending && s.Version >= 1);
    }
}
