using System.Text.Json;
using Microsoft.Data.Sqlite;
using VortexApi.Client;
using VortexWms.Core.Data;
using VortexWms.Core.Domain;
using VortexWms.Core.Integration;
using VortexWms.Core.Sync;

using static VortexWms.Core.Texts.CoreText;

namespace VortexWms.Core.Services;

/// <summary>
/// Cola de tareas por operario (fase 4b2): el supervisor asigna guardar, preparar, reabastecer o contar; cada operario ve
/// las suyas. Las tareas y los operarios son registros del WMS (<c>wms.record.upserted</c>) que se replican a todos los
/// equipos. Un pedido con una tarea de preparar asignada solo lo prepara su responsable (o un supervisor), así dos equipos
/// no preparan lo mismo. Preparar se da por hecha al despachar el pedido y reabastecer al reabastecer la ubicación.
/// </summary>
public sealed class TaskService(WmsDatabase db, IWmsContext context, TimeProvider clock) : WmsService(db, context, clock)
{
    /// <summary>Tareas de las sedes del usuario: las suyas (<paramref name="mine"/>) o las de todo el equipo.</summary>
    public async Task<IReadOnlyList<TaskRow>> GetTasksAsync(bool mine, bool includeClosed = false, Guid? siteId = null,
        CancellationToken ct = default)
    {
        await using var c = await Db.OpenAsync(ct);
        return await c.QueryAsync($"""
            SELECT t.*, s.name AS site_name, o.number AS order_number, l.code AS location_code, i.name AS item_name
            FROM tasks t LEFT JOIN sites s ON s.id = t.site_id LEFT JOIN orders o ON o.id = t.order_id
                 LEFT JOIN locations l ON l.id = t.location_id LEFT JOIN items i ON i.id = t.item_id
            WHERE ($mine = 0 OR t.assignee_id = $me) AND ($closed = 1 OR t.status = 'pending') AND ($site IS NULL OR t.site_id = $site)
                  {MySites("t.site_id")}
            ORDER BY CASE t.status WHEN 'pending' THEN 0 ELSE 1 END, t.created_at DESC
            LIMIT 300
            """, Read, null, ("$mine", mine), ("$me", Context.UserId), ("$closed", includeClosed), ("$site", siteId));
    }

    /// <summary>Cuántas tareas pendientes tiene el usuario (para el menú).</summary>
    public async Task<int> CountMineAsync(CancellationToken ct = default)
    {
        await using var c = await Db.OpenAsync(ct);
        return (int)await c.ScalarAsync<long>("SELECT COUNT(*) FROM tasks WHERE assignee_id = $me AND status = 'pending'", null,
            ("$me", Context.UserId));
    }

    /// <summary>Quienes usan el WMS en algún equipo (se registran al abrir la app), con sus tareas pendientes.</summary>
    public async Task<IReadOnlyList<OperatorRow>> GetOperatorsAsync(CancellationToken ct = default)
    {
        await using var c = await Db.OpenAsync(ct);
        return await c.QueryAsync("""
            SELECT o.id, o.name, o.role,
                   (SELECT COUNT(*) FROM tasks t WHERE t.assignee_id = o.id AND t.status = 'pending') AS pending
            FROM operators o ORDER BY o.name COLLATE NOCASE
            """, r => new OperatorRow(r.Guid("id"), r.Str("name"), r.StrOrNull("role"), r.Int("pending")));
    }

    /// <summary>
    /// Deja al usuario actual en la lista de operarios de todos los equipos (al abrir la app). Solo publica si es nuevo o
    /// cambió su nombre o rol.
    /// </summary>
    public Task RegisterCurrentUserAsync(CancellationToken ct = default)
    {
        if (Context.UserId is not { } me)
            return Task.CompletedTask;
        return InTransactionAsync(async (c, tx, now) =>
        {
            var known = await c.FirstOrDefaultAsync("SELECT name, role FROM operators WHERE id = $id",
                r => new { Name = r.Str("name"), Role = r.StrOrNull("role") }, tx, ("$id", me));
            if (known is not null && known.Name == Context.UserName && known.Role == Context.Role)
                return 0;
            var data = new OperatorData(me, Context.UserName, Context.Role);
            await ApplyOperatorAsync(c, tx, data, now, force: true);
            await Outbox.EnqueueEventAsync(c, tx, now, SuiteEvents.RecordUpserted,
                new WmsRecord(WmsRecordKinds.Operator, me.ToString(), now, JsonSerializer.SerializeToElement(data, VortexJson.Options)),
                subjectType: "operator", subjectId: me.ToString());
            return 1;
        }, ct);
    }

    /// <summary>Asigna una tarea a un operario (supervisor).</summary>
    public Task<Guid> CreateAsync(TaskDraft d, CancellationToken ct = default)
    {
        RequirePermission(WmsPermissions.ManageTasks);
        Require(TaskKinds.All.Contains(d.Kind), T("error.tipo_de_tarea_no_valido"));
        Require(d.AssigneeId != Guid.Empty, T("error.elige_a_quien"));
        Require(d.Kind != TaskKinds.Pick || d.OrderId is not null, T("error.elige_el_pedido"));
        Require(d.Kind != TaskKinds.Replenish || d.LocationId is not null, T("error.elige_la_ubicacion"));
        Require(d.Quantity is null or > 0, T("error.la_cantidad_debe_ser_mayor"));
        return InTransactionAsync(async (c, tx, now) =>
        {
            await RequireSiteAccessAsync(c, tx, d.SiteId);
            if (d.OrderId is { } orderId)
            {
                var order = await c.FirstOrDefaultAsync("SELECT number, status, site_id FROM orders WHERE id = $id",
                    r => new { Number = r.Str("number"), Status = r.Str("status"), Site = r.Guid("site_id") }, tx, ("$id", orderId));
                Require(order is not null, T("error.el_pedido_no_existe"));
                Require(OrderStatuses.IsOpen(order!.Status), T("error.el_pedido_ya_fue_despachado"));
                // Un pedido se prepara por una sola persona: la tarea anterior pasa al nuevo responsable.
                await CloseOpenAsync(c, tx, now, Context.UserName, TaskKinds.Pick, orderId: orderId, status: TaskStatuses.Cancelled);
            }
            var title = Clean(d.Title) ?? await DefaultTitleAsync(c, tx, d);
            var data = new TaskData(Guid.NewGuid(), d.SiteId, d.Kind, title, d.OrderId, d.LocationId, d.ItemId, d.Quantity, d.AssigneeId,
                Clean(d.AssigneeName), TaskStatuses.Pending, Clean(d.Notes), Context.UserName, now, null, null);
            await PublishAsync(c, tx, now, data);
            await AuditAsync(c, tx, now, "task.created", "task", data.Id, title, data.AssigneeName);
            return data.Id;
        }, ct);
    }

    /// <summary>Pasa la tarea a otro operario (supervisor).</summary>
    public Task ReassignAsync(Guid taskId, Guid assigneeId, string assigneeName, CancellationToken ct = default)
    {
        RequirePermission(WmsPermissions.ManageTasks);
        return ChangeAsync(taskId, t =>
        {
            Require(t.Status == TaskStatuses.Pending, T("error.la_tarea_ya_esta_cerrada"));
            return t with { AssigneeId = assigneeId, AssigneeName = Clean(assigneeName) };
        }, "task.reassigned", ct);
    }

    /// <summary>La da por hecha su responsable (o un supervisor).</summary>
    public Task CompleteAsync(Guid taskId, CancellationToken ct = default) =>
        ChangeAsync(taskId, t =>
        {
            Require(t.Status == TaskStatuses.Pending, T("error.la_tarea_ya_esta_cerrada"));
            Require(t.AssigneeId == Context.UserId || Context.Can(WmsPermissions.ManageTasks), T("error.la_tarea_es_de", t.AssigneeName ?? "?"));
            return t with { Status = TaskStatuses.Done, DoneAt = Clock.GetUtcNow(), DoneBy = Context.UserName };
        }, "task.done", ct);

    /// <summary>La anula el supervisor.</summary>
    public Task CancelAsync(Guid taskId, CancellationToken ct = default)
    {
        RequirePermission(WmsPermissions.ManageTasks);
        return ChangeAsync(taskId, t =>
        {
            Require(t.Status == TaskStatuses.Pending, T("error.la_tarea_ya_esta_cerrada"));
            return t with { Status = TaskStatuses.Cancelled, DoneAt = Clock.GetUtcNow(), DoneBy = Context.UserName };
        }, "task.cancelled", ct);
    }

    private Task ChangeAsync(Guid taskId, Func<TaskData, TaskData> change, string action, CancellationToken ct) =>
        InTransactionAsync(async (c, tx, now) =>
        {
            var current = await c.FirstOrDefaultAsync("SELECT * FROM tasks WHERE id = $id", ReadData, tx, ("$id", taskId));
            Require(current is not null, T("error.la_tarea_no_existe"));
            await RequireSiteAccessAsync(c, tx, current!.SiteId);
            var updated = change(current);
            await PublishAsync(c, tx, now, updated);
            await AuditAsync(c, tx, now, action, "task", taskId, updated.Title, updated.AssigneeName);
            return 0;
        }, ct);

    // ---------- Usado por los demás servicios ----------

    /// <summary>
    /// Corta la preparación si el pedido tiene una tarea de preparar pendiente de otra persona (un supervisor sí puede).
    /// </summary>
    internal static async Task RequireMyPickAsync(SqliteConnection c, SqliteTransaction? tx, IWmsContext context, Guid orderId)
    {
        if (context.Can(WmsPermissions.ManageTasks))
            return;
        var owner = await c.FirstOrDefaultAsync("""
            SELECT assignee_id, assignee_name FROM tasks WHERE order_id = $order AND kind = 'pick' AND status = 'pending'
            ORDER BY created_at DESC LIMIT 1
            """, r => new { Id = r.GuidOrNull("assignee_id"), Name = r.StrOrNull("assignee_name") }, tx, ("$order", orderId));
        Require(owner is null || owner.Id == context.UserId, T("error.lo_prepara", owner?.Name ?? "?"));
    }

    /// <summary>Cierra (por defecto, como hechas) las tareas pendientes de un tipo para un pedido o una ubicación.</summary>
    internal static async Task CloseOpenAsync(SqliteConnection c, SqliteTransaction tx, DateTimeOffset now, string? user, string kind,
        Guid? orderId = null, Guid? locationId = null, string status = TaskStatuses.Done)
    {
        var open = await c.QueryAsync("""
            SELECT * FROM tasks WHERE kind = $kind AND status = 'pending'
              AND (($order IS NOT NULL AND order_id = $order) OR ($loc IS NOT NULL AND location_id = $loc))
            """, ReadData, tx, ("$kind", kind), ("$order", orderId), ("$loc", locationId));
        foreach (var t in open)
            await PublishAsync(c, tx, now, t with { Status = status, DoneAt = now, DoneBy = user });
    }

    private static async Task PublishAsync(SqliteConnection c, SqliteTransaction tx, DateTimeOffset now, TaskData data)
    {
        await ApplyTaskAsync(c, tx, data, now, force: true);
        await Outbox.EnqueueEventAsync(c, tx, now, SuiteEvents.RecordUpserted,
            new WmsRecord(WmsRecordKinds.Task, data.Id.ToString(), now, JsonSerializer.SerializeToElement(data, VortexJson.Options)),
            subjectType: "task", subjectId: data.Id.ToString());
    }

    /// <summary>Aplica una tarea; la de otro equipo solo si es más nueva que la que hay (los cambios propios siempre).</summary>
    internal static async Task<bool> ApplyTaskAsync(SqliteConnection c, SqliteTransaction tx, TaskData d, DateTimeOffset updatedAt,
        bool force = false)
    {
        var current = await c.ScalarAsync<string?>("SELECT updated_at FROM tasks WHERE id = $id", tx, ("$id", d.Id));
        if (!force && current is not null && string.CompareOrdinal(current, SqliteExtensions.Time(updatedAt)) >= 0)
            return false;
        await c.ExecAsync("""
            INSERT INTO tasks (id, site_id, kind, title, order_id, location_id, item_id, quantity, assignee_id, assignee_name, status, notes,
                               created_by, created_at, done_at, done_by, updated_at)
            VALUES ($id, $site, $kind, $title, $order, $loc, $item, $qty, $who, $whoName, $status, $notes, $by, $created, $done, $doneBy, $at)
            ON CONFLICT (id) DO UPDATE SET site_id = $site, kind = $kind, title = $title, order_id = $order, location_id = $loc, item_id = $item,
                quantity = $qty, assignee_id = $who, assignee_name = $whoName, status = $status, notes = $notes, created_by = $by,
                created_at = $created, done_at = $done, done_by = $doneBy, updated_at = $at
            """, tx, ("$id", d.Id), ("$site", d.SiteId), ("$kind", d.Kind), ("$title", d.Title), ("$order", d.OrderId), ("$loc", d.LocationId),
            ("$item", d.ItemId), ("$qty", d.Quantity), ("$who", d.AssigneeId), ("$whoName", d.AssigneeName), ("$status", d.Status),
            ("$notes", d.Notes), ("$by", d.CreatedBy), ("$created", d.CreatedAt), ("$done", d.DoneAt), ("$doneBy", d.DoneBy), ("$at", updatedAt));
        return true;
    }

    internal static async Task<bool> ApplyOperatorAsync(SqliteConnection c, SqliteTransaction tx, OperatorData d, DateTimeOffset updatedAt,
        bool force = false)
    {
        var current = await c.ScalarAsync<string?>("SELECT updated_at FROM operators WHERE id = $id", tx, ("$id", d.Id));
        if (!force && current is not null && string.CompareOrdinal(current, SqliteExtensions.Time(updatedAt)) >= 0)
            return false;
        await c.ExecAsync("""
            INSERT INTO operators (id, name, role, updated_at) VALUES ($id, $name, $role, $at)
            ON CONFLICT (id) DO UPDATE SET name = $name, role = $role, updated_at = $at
            """, tx, ("$id", d.Id), ("$name", d.Name), ("$role", d.Role), ("$at", updatedAt));
        return true;
    }

    private static async Task<string> DefaultTitleAsync(SqliteConnection c, SqliteTransaction tx, TaskDraft d)
    {
        var order = d.OrderId is { } o ? await c.ScalarAsync<string?>("SELECT number FROM orders WHERE id = $id", tx, ("$id", o)) : null;
        var location = d.LocationId is { } l ? await c.ScalarAsync<string?>("SELECT code FROM locations WHERE id = $id", tx, ("$id", l)) : null;
        var item = d.ItemId is { } i ? await c.ScalarAsync<string?>("SELECT name FROM items WHERE id = $id", tx, ("$id", i)) : null;
        return d.Kind switch
        {
            TaskKinds.Pick => T("tarea.titulo_preparar", order ?? "?"),
            TaskKinds.Replenish => T("tarea.titulo_reabastecer", location ?? "?"),
            TaskKinds.Count => T("tarea.titulo_contar", location ?? item ?? "?"),
            _ => item is null ? T("tarea.titulo_guardar_todo") : T("tarea.titulo_guardar", item),
        };
    }

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static TaskData ReadData(SqliteDataReader r) => new(
        r.Guid("id"), r.Guid("site_id"), r.Str("kind"), r.Str("title"), r.GuidOrNull("order_id"), r.GuidOrNull("location_id"),
        r.GuidOrNull("item_id"), r.DecOrNull("quantity"), r.GuidOrNull("assignee_id"), r.StrOrNull("assignee_name"), r.Str("status"),
        r.StrOrNull("notes"), r.StrOrNull("created_by"), r.Time("created_at"), r.TimeOrNull("done_at"), r.StrOrNull("done_by"));

    private static TaskRow Read(SqliteDataReader r) => new(
        r.Guid("id"), r.Guid("site_id"), r.StrOrNull("site_name"), r.Str("kind"), r.Str("title"), r.GuidOrNull("order_id"),
        r.StrOrNull("order_number"), r.GuidOrNull("location_id"), r.StrOrNull("location_code"), r.GuidOrNull("item_id"), r.StrOrNull("item_name"),
        r.DecOrNull("quantity"), r.GuidOrNull("assignee_id"), r.StrOrNull("assignee_name"), r.Str("status"), r.StrOrNull("notes"),
        r.StrOrNull("created_by"), r.Time("created_at"), r.TimeOrNull("done_at"), r.StrOrNull("done_by"));
}
