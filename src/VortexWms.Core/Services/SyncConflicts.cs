using Microsoft.Data.Sqlite;
using VortexWms.Core.Data;
using VortexWms.Core.Domain;

namespace VortexWms.Core.Services;

/// <summary>
/// Conflictos entre equipos: dos equipos hicieron lo mismo sin conexión con datos distintos (recibieron otra cantidad,
/// despacharon desde otra sede, uno canceló y el otro despachó). Gana la versión que el hub recibió primero; este
/// equipo la adopta y deja el aviso para que el supervisor lo revise.
/// </summary>
public static class SyncConflictKinds
{
    /// <summary>El mismo movimiento de stock con otra sede o cantidad.</summary>
    public const string Movement = "movement";

    /// <summary>La misma recepción registrada con otras cantidades.</summary>
    public const string ReceiptQuantities = "receipt_quantities";

    /// <summary>Recepción anulada aquí y recibida en otro equipo.</summary>
    public const string ReceivedAfterCancel = "received_after_cancel";

    /// <summary>Pedido cancelado aquí y despachado en otro equipo.</summary>
    public const string DispatchedAfterCancel = "dispatched_after_cancel";

    /// <summary>Pedido despachado aquí y cancelado en otro equipo o sistema (prevalece el despacho).</summary>
    public const string CancelAfterDispatch = "cancel_after_dispatch";

    /// <summary>El mismo pedido despachado en dos equipos (otra sede, transportista o guía).</summary>
    public const string DispatchedTwice = "dispatched_twice";
}

public sealed record SyncConflictRow(Guid Id, DateTimeOffset OccurredAt, string Kind, Guid? DocumentId, string? Reference, string? Detail);

internal static class SyncConflicts
{
    public static Task RecordAsync(SqliteConnection c, SqliteTransaction tx, DateTimeOffset now, string kind, Guid? documentId,
        string? reference, string? detail) => SyncConflictService.RecordAsync(c, tx, now, kind, documentId, reference, detail);
}

public sealed class SyncConflictService(WmsDatabase db, IWmsContext context, TimeProvider clock) : WmsService(db, context, clock)
{
    public async Task<IReadOnlyList<SyncConflictRow>> GetOpenAsync(CancellationToken ct = default)
    {
        await using var c = await Db.OpenAsync(ct);
        return await c.QueryAsync("""
            SELECT * FROM sync_conflicts WHERE dismissed = 0 ORDER BY occurred_at DESC LIMIT 100
            """, r => new SyncConflictRow(r.Guid("id"), r.Time("occurred_at"), r.Str("kind"), r.GuidOrNull("document_id"),
                r.StrOrNull("reference"), r.StrOrNull("detail")));
    }

    public async Task DismissAsync(Guid id, CancellationToken ct = default)
    {
        await using var c = await Db.OpenAsync(ct);
        await c.ExecAsync("UPDATE sync_conflicts SET dismissed = 1 WHERE id = $id", null, ("$id", id));
    }

    /// <summary>Deja el aviso (una vez por documento y tipo: repetir el evento no lo duplica).</summary>
    internal static async Task RecordAsync(SqliteConnection c, SqliteTransaction tx, DateTimeOffset now, string kind,
        Guid? documentId, string? reference, string? detail)
    {
        var id = documentId is { } doc ? Integration.DeterministicId.From("conflict_" + kind, doc) : Guid.NewGuid();
        await c.ExecAsync("""
            INSERT INTO sync_conflicts (id, occurred_at, kind, document_id, reference, detail)
            VALUES ($id, $now, $kind, $doc, $ref, $detail)
            ON CONFLICT (id) DO UPDATE SET detail = $detail, occurred_at = $now, dismissed = 0
            """, tx, ("$id", id), ("$now", now), ("$kind", kind), ("$doc", documentId), ("$ref", reference), ("$detail", detail));
    }
}
