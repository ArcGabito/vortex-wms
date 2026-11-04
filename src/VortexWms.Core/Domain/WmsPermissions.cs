using VortexApi.Suite.Security;

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

    public const string Operator = "operario";
    public const string Supervisor = "supervisor";
    public const string ReadOnly = "consulta";

    public static readonly PermissionMatrix Matrix = PermissionMatrix.Create(Operator,
        (ReadOnly, []),
        (Operator, [Receive, Pick, Dispatch, Transfer]),
        (Supervisor, [Receive, Pick, Dispatch, Transfer, CancelReceipt, CreateOrder, CancelOrder, Adjust, StockSettings, CreateParty, ViewAudit]));

    public static string Describe(string permission) => permission switch
    {
        Receive => "recibir mercadería",
        CancelReceipt => "anular recepciones",
        CreateOrder => "crear pedidos",
        Pick => "preparar pedidos",
        Dispatch => "despachar pedidos",
        CancelOrder => "cancelar pedidos",
        Transfer => "transferir stock",
        Adjust => "ajustar el stock",
        StockSettings => "cambiar ubicaciones y mínimos",
        CreateParty => "registrar clientes y proveedores",
        EditCatalog => "editar artículos y almacenes",
        ViewAudit => "ver la auditoría",
        _ => permission,
    };
}
