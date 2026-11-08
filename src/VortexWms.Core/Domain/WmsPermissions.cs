using VortexApi.Suite.Security;

using static VortexWms.Core.Texts.CoreText;

namespace VortexWms.Core.Domain;

/// <summary>
/// Qué puede hacer cada rol del WMS (fase 0 de la hoja de ruta). El OrgAdmin asigna el rol en el hub (Sistemas → WMS →
/// miembros); quien no tiene rol entra como <c>operario</c>. Se valida en los servicios de Core, no solo en la interfaz.
/// </summary>
public static class WmsPermissions
{
    public const string Receive = "receipt.receive";
    public const string CancelReceipt = "receipt.cancel";
    public const string CreateOrder = "order.create";
    public const string Pick = "order.pick";
    public const string Dispatch = "order.dispatch";
    public const string CancelOrder = "order.cancel";
    public const string Transfer = "stock.transfer";

    /// <summary>Ajustes de stock y conteos.</summary>
    public const string Adjust = "stock.adjust";

    /// <summary>Ubicación y stock mínimo por artículo y sede.</summary>
    public const string StockSettings = "stock.settings";

    public const string CreateParty = "party.create";

    /// <summary>Crear o editar artículos y sedes (datos maestros de la suite).</summary>
    public const string EditCatalog = "catalog.edit";

    public const string ViewAudit = "audit.view";

    /// <summary>Ver costos, valor del inventario a costo, costo de lo vendido y margen.</summary>
    public const string ViewCosts = "cost.view";

    /// <summary>Elegir un lote distinto al que sugiere FEFO, cambiar el estado de un lote y aceptar lotes con poca vida.</summary>
    public const string ManageLots = "lot.manage";

    /// <summary>Crear y editar el mapa de ubicaciones.</summary>
    public const string ManageLocations = "location.manage";

    /// <summary>Guardar lo recibido y mover stock entre ubicaciones de una sede.</summary>
    public const string Relocate = "stock.relocate";

    public const string Operator = "operario";
    public const string Supervisor = "supervisor";
    public const string ReadOnly = "consulta";

    public static readonly PermissionMatrix Matrix = PermissionMatrix.Create(Operator,
        (ReadOnly, []),
        (Operator, [Receive, Pick, Dispatch, Transfer, Relocate]),
        (Supervisor, [Receive, Pick, Dispatch, Transfer, CancelReceipt, CreateOrder, CancelOrder, Adjust, StockSettings, CreateParty, ViewAudit, ViewCosts, ManageLots, ManageLocations, Relocate]));

    public static string Describe(string permission) => permission switch
    {
        Receive => T("permiso.receive"),
        CancelReceipt => T("permiso.cancelreceipt"),
        CreateOrder => T("permiso.createorder"),
        Pick => T("permiso.pick"),
        Dispatch => T("permiso.dispatch"),
        CancelOrder => T("permiso.cancelorder"),
        Transfer => T("permiso.transfer"),
        Adjust => T("permiso.adjust"),
        StockSettings => T("permiso.stocksettings"),
        CreateParty => T("permiso.createparty"),
        EditCatalog => T("permiso.editcatalog"),
        ViewAudit => T("permiso.viewaudit"),
        ViewCosts => T("permiso.viewcosts"),
        ManageLots => T("permiso.managelots"),
        ManageLocations => T("permiso.managelocations"),
        Relocate => T("permiso.relocate"),
        _ => permission,
    };
}
