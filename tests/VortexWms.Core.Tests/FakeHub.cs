using VortexApi.Client;
using VortexApi.Client.Sync;
using VortexWms.Core.Integration;

namespace VortexWms.Core.Tests;

/// <summary>
/// El hub visto por los equipos del WMS: guarda los eventos en orden de llegada, se queda con el primero cuando dos
/// equipos publican el mismo Id (igual que <c>POST /api/events</c>) y entrega a cada equipo todo lo publicado, también
/// lo suyo (<c>IncludeOwnEvents</c>).
/// </summary>
public sealed class FakeHub
{
    private readonly List<VortexEvent> _events = [];
    private readonly Dictionary<TestWorkspace, HashSet<Guid>> _pushed = [];
    private readonly Dictionary<TestWorkspace, int> _cursor = [];

    public IReadOnlyList<VortexEvent> Events => _events;

    /// <summary>Sube la cola del equipo (lo ya subido no se repite).</summary>
    public async Task PushAsync(TestWorkspace device)
    {
        var pushed = _pushed.TryGetValue(device, out var set) ? set : _pushed[device] = [];
        foreach (var entry in (await device.OutboxAsync()).Where(e => e.Kind == VortexOutboxKind.Event))
        {
            if (!pushed.Add(entry.Id) || _events.Any(e => e.Id == entry.Id))
                continue;
            _events.Add(new VortexEvent(entry.Id, _events.Count + 1, entry.EventType!, "WMS", 1, entry.SubjectType, entry.SubjectId,
                entry.CreatedAt, entry.CreatedAt, null, entry.Payload!.Value));
        }
    }

    /// <summary>Entrega al equipo lo que todavía no recibió.</summary>
    public async Task PullAsync(TestWorkspace device)
    {
        var from = _cursor.GetValueOrDefault(device);
        foreach (var e in _events.Skip(from).ToList())
            await HandleAsync(device, e);
        _cursor[device] = _events.Count;
    }

    /// <summary>Cada equipo sube su cola, en el orden dado, y después todos bajan lo publicado.</summary>
    public async Task SyncAsync(params TestWorkspace[] devices)
    {
        foreach (var device in devices)
            await PushAsync(device);
        foreach (var device in devices)
            await PullAsync(device);
    }

    public static Task HandleAsync(TestWorkspace device, VortexEvent e)
    {
        var handlers = device.Wms.SyncHandlers;
        return e.Type switch
        {
            SuiteEvents.StockAdjusted => handlers.HandleStockAdjustedAsync(e),
            SuiteEvents.GoodsReceived => handlers.HandleGoodsReceivedAsync(e),
            SuiteEvents.ShipmentDispatched => handlers.HandleShipmentDispatchedAsync(e),
            SuiteEvents.OrderCreated => handlers.HandleOrderCreatedAsync(e),
            SuiteEvents.OrderPickingStarted => handlers.HandleOrderPickingStartedAsync(e),
            SuiteEvents.OrderCancelled => handlers.HandleOrderCancelledAsync(e),
            SuiteEvents.ReceiptCancelled => handlers.HandleReceiptCancelledAsync(e),
            SuiteEvents.RecordUpserted => handlers.HandleRecordUpsertedAsync(e),
            _ => Task.CompletedTask,
        };
    }
}
