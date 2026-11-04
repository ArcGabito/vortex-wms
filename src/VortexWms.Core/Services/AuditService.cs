using VortexWms.Core.Data;
using VortexWms.Core.Domain;

namespace VortexWms.Core.Services;

/// <summary>Consulta de la bitácora de acciones sensibles (ajustes de stock, anulaciones y cancelaciones).</summary>
public sealed class AuditService(WmsDatabase db, IWmsContext context, TimeProvider clock) : WmsService(db, context, clock)
{
    public async Task<PagedResult<AuditRow>> GetAsync(int page, int pageSize, CancellationToken ct = default)
    {
        RequirePermission(WmsPermissions.ViewAudit);
        await using var c = await Db.OpenAsync(ct);
        var total = await c.ScalarAsync<int>("SELECT COUNT(*) FROM audit_log");
        var rows = await c.QueryAsync("SELECT * FROM audit_log ORDER BY at DESC LIMIT $limit OFFSET $offset", r => new AuditRow(
            r.Guid("id"), r.Time("at"), r.StrOrNull("user_name"), r.StrOrNull("device"), r.Str("action"), r.Str("entity"),
            r.StrOrNull("reference"), r.StrOrNull("detail")), null, ("$limit", pageSize), ("$offset", (page - 1) * pageSize));
        return new PagedResult<AuditRow>(rows, total, page, pageSize);
    }
}
