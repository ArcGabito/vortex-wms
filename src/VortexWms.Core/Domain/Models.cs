namespace VortexWms.Core.Domain;

/// <summary>Quién opera y en qué equipo. Lo provee la app a partir de la sesión del hub.</summary>
public interface IWmsContext
{
    Guid? UserId { get; }
    string UserName { get; }

    /// <summary>Prefijo corto del equipo para numerar documentos sin choques entre equipos sin conexión.</summary>
    string DeviceTag { get; }

    /// <summary>Almacén elegido por el usuario para operar por defecto (puede ser null).</summary>
    Guid? DefaultSiteId { get; }

    /// <summary>Rol efectivo del usuario en el WMS (<see cref="WmsPermissions"/>).</summary>
    string Role { get; }

    /// <summary>Si el usuario puede hacer la acción (<see cref="WmsPermissions"/>).</summary>
    bool Can(string permission);

    /// <summary>Sedes asignadas al usuario en el hub (<c>Access.SiteIds</c>). Vacío: trabaja en todas.</summary>
    IReadOnlyList<Guid> SiteIds { get; }
}

/// <summary>Dato inválido o regla de negocio incumplida: se muestra tal cual al usuario.</summary>
public sealed class WmsValidationException(string message) : Exception(message);

public sealed record PagedResult<T>(IReadOnlyList<T> Items, int Total, int Page, int PageSize)
{
    public int Pages => Math.Max(1, (Total + PageSize - 1) / PageSize);
    public int From => Total == 0 ? 0 : (Page - 1) * PageSize + 1;
    public int To => Math.Min(Total, Page * PageSize);
}

// ---------- Datos maestros (copia local del hub) ----------

public sealed record ItemRow(
    Guid Id, string Sku, string Name, string Kind, string? Description, string Unit, string? Barcode, string? Category,
    decimal Price, string CurrencyCode, string? TaxCode, bool TrackInventory, bool IsActive, int Version, bool Pending,
    decimal OnHand, decimal Reserved)
{
    /// <summary>Unidad base con código UN/ECE (Tabla 6 de SUNAT): NIU, KGM, BX…</summary>
    public string? UnitCode { get; init; }
    public decimal? WeightKg { get; init; }

    /// <summary><c>None</c>, <c>Lot</c> o <c>Serial</c> (fase 3).</summary>
    public string Tracking { get; init; } = ItemTrackings.None;

    /// <summary>Presentaciones (caja × 12). El stock siempre se lleva en la unidad base.</summary>
    public IReadOnlyList<ItemPresentation> Presentations { get; init; } = [];

    public ItemPresentation? PresentationByCode(string? code) => code is null ? null
        : Presentations.FirstOrDefault(p => string.Equals(p.Code, code, StringComparison.OrdinalIgnoreCase));

    /// <summary>Pasa una cantidad y un precio o costo de la presentación a la unidad base (2 cajas × 12 = 24).</summary>
    public (decimal Quantity, decimal? UnitPrice) ToBase(string? presentationCode, decimal quantity, decimal? unitPrice = null) =>
        PresentationByCode(presentationCode) is { } p
            ? (quantity * p.Factor, unitPrice is { } price ? decimal.Round(price / p.Factor, 6) : null)
            : (quantity, unitPrice);
}

/// <summary>Presentación de un artículo: cuántas unidades base trae y su código de barras (DUN-14, EAN).</summary>
public sealed record ItemPresentation(string Code, string Name, decimal Factor, string? Barcode = null, string? UnitCode = null);

/// <summary>Resultado de escanear: el artículo y, si el código era el de una presentación, cuál.</summary>
public sealed record ItemScan(ItemRow Item, ItemPresentation? Presentation);

public static class ItemTrackings
{
    public const string None = "None";
    public const string Lot = "Lot";
    public const string Serial = "Serial";
}

public sealed record SiteRow(
    Guid Id, string Code, string Name, string Kind, string? Phone, string? AddressLine, string? City, string? CountryCode,
    bool IsActive, int Version, bool Pending, int SkuCount, decimal Units);

public sealed record PartyRow(
    Guid Id, string Name, string? TradeName, string Kind, string? DocumentType, string? DocumentNumber, bool IsCustomer,
    bool IsSupplier, string? Email, string? Phone, string? City, bool IsActive, int Version, bool Pending);

/// <summary>Empresa del hub. <c>TimeZone</c> IANA (las fechas se muestran en la hora de la empresa).</summary>
public sealed record CompanyInfo(
    string LegalName, string? TradeName, string? TaxId, string CountryCode, string CurrencyCode, decimal? DefaultTaxRate = null,
    string? TimeZone = null);

public sealed record AuditRow(
    Guid Id, DateTimeOffset At, string? UserName, string? Device, string Action, string Entity, string? Reference, string? Detail);

public sealed record Option(Guid Id, string Label, string? Detail = null);

public sealed record ItemDraft(
    string Sku, string Name, string Unit, string? Category, string? Barcode, decimal Price, string CurrencyCode,
    string? Description = null, bool TrackInventory = true, string Kind = "Product", string? TaxCode = "igv", bool IsActive = true,
    string? UnitCode = null, decimal? WeightKg = null, string Tracking = ItemTrackings.None,
    IReadOnlyList<ItemPresentation>? Presentations = null);

public sealed record SiteDraft(
    string Code, string Name, string Kind, string? Phone, string? AddressLine, string? City, string CountryCode, bool IsActive = true);

public sealed record PartyDraft(
    string Name, string Kind, bool IsCustomer, bool IsSupplier, string? DocumentType, string? DocumentNumber,
    string? Email, string? Phone, string? City, string CountryCode = "PE");

// ---------- Existencias ----------

public enum StockStatus
{
    InStock,
    Low,
    OutOfStock,
}

public sealed record StockRow(
    Guid SiteId, string SiteCode, string SiteName, Guid ItemId, string Sku, string ItemName, string? Category, string Unit,
    string? Barcode, decimal OnHand, decimal Reserved, decimal? ReorderPoint, string? Bin, decimal Price, string CurrencyCode,
    DateTimeOffset UpdatedAt, decimal? AvgCost = null, decimal? CostValue = null)
{
    public decimal Available => OnHand - Reserved;

    /// <summary>Valor a precio de venta.</summary>
    public decimal PriceValue => OnHand * Price;

    public StockStatus Status => OnHand <= 0 ? StockStatus.OutOfStock
        : ReorderPoint is { } min && Available <= min ? StockStatus.Low
        : StockStatus.InStock;
}

public sealed record StockQuery(
    string? Search = null, Guid? SiteId = null, string? Category = null, StockStatus? Status = null, int Page = 1,
    int PageSize = 10);

public static class MovementKinds
{
    public const string Receipt = "receipt";
    public const string Adjustment = "adjustment";
    public const string TransferOut = "transfer_out";
    public const string TransferIn = "transfer_in";
    public const string Shipment = "shipment";
    public const string Sale = "sale";
    public const string SaleVoid = "sale_void";
    public const string SaleReturn = "sale_return";

    public static readonly string[] All = [Receipt, Adjustment, TransferOut, TransferIn, Shipment, Sale, SaleVoid, SaleReturn];
}

/// <summary>Motivos de ajuste manual; viajan en <c>stock.adjusted.reason</c>.</summary>
public static class AdjustmentReasons
{
    public const string Count = "count";
    public const string Damage = "damage";
    public const string Loss = "loss";
    public const string Found = "found";
    public const string Correction = "correction";

    public static readonly string[] All = [Count, Damage, Loss, Found, Correction];
}

public sealed record MovementRow(
    Guid Id, DateTimeOffset OccurredAt, string Kind, Guid SiteId, string SiteName, Guid ItemId, string Sku, string ItemName,
    decimal Quantity, decimal OnHandAfter, string? Reason, string? Reference, string? UserName, string? Note,
    decimal? UnitCost = null, decimal? TotalCost = null, decimal? ValueAfter = null, string? OperationType = null);

/// <param name="From">Desde (inclusive) y <paramref name="To"/> hasta (exclusive): el periodo del kardex valorizado.</param>
public sealed record MovementQuery(string? Search = null, Guid? SiteId = null, string? Kind = null, int Page = 1, int PageSize = 15,
    Guid? ItemId = null, DateTimeOffset? From = null, DateTimeOffset? To = null);

/// <summary>
/// Línea de un documento. Para artículos con lote, <c>Lots</c> dice de qué lotes (al recibir es obligatorio; al sacar, si
/// falta, se toma por FEFO). Para artículos con serie, <c>Serials</c> lleva una serie por unidad.
/// </summary>
public sealed record DocumentLine(Guid ItemId, decimal Quantity, decimal? UnitPrice = null, IReadOnlyList<LotQuantity>? Lots = null,
    IReadOnlyList<string>? Serials = null);

/// <summary>Lo recibido de una línea de una recepción esperada, con sus lotes o series.</summary>
public sealed record ReceivedLine(int LineNo, decimal Quantity, IReadOnlyList<LotQuantity>? Lots = null, IReadOnlyList<string>? Serials = null);

// ---------- Lotes y series ----------

/// <summary>Cantidad de un lote. El vencimiento solo se pide al recibir (al sacar se toma el del lote).</summary>
public sealed record LotQuantity(string LotCode, decimal Quantity, DateOnly? ExpiresAt = null);

public static class LotStatuses
{
    /// <summary>Se puede despachar y vender.</summary>
    public const string Available = "available";

    /// <summary>En revisión (control de calidad, devolución): no sale hasta liberarse.</summary>
    public const string Quarantine = "quarantine";

    /// <summary>No sale (retiro del mercado, defecto).</summary>
    public const string Blocked = "blocked";

    public static readonly string[] All = [Available, Quarantine, Blocked];
}

/// <summary>Saldo de un lote en una sede. <c>DaysLeft</c>: días para vencer (negativo si ya venció).</summary>
public sealed record LotRow(
    Guid SiteId, string SiteName, Guid ItemId, string Sku, string ItemName, string LotCode, DateOnly? ExpiresAt, decimal Quantity,
    string Status, int? DaysLeft)
{
    public bool IsExpired => DaysLeft < 0;
}

public sealed record LotQuery(
    Guid? SiteId = null, Guid? ItemId = null, string? Search = null, int? ExpiringWithinDays = null, bool IncludeEmpty = false,
    int Page = 1, int PageSize = 25);

/// <summary>Una unidad con serie: dónde está (o si ya salió) y de qué lote vino.</summary>
public sealed record SerialRow(Guid ItemId, string Serial, Guid? SiteId, string? SiteName, string Status, string? LotCode, DateTimeOffset UpdatedAt);

public static class SerialStatuses
{
    public const string InStock = "in_stock";
    public const string Out = "out";
}

/// <summary>Un paso en la trazabilidad de un lote o serie: el movimiento, su documento y a quién se compró o vendió.</summary>
public sealed record TraceRow(
    DateTimeOffset At, string Kind, Guid SiteId, string? SiteName, Guid ItemId, string ItemName, string LotCode, decimal Quantity,
    string? Reference, Guid? PartyId, string? PartyName);

/// <summary>
/// Datos de almacén de un artículo que son solo del WMS: vida útil (para sugerir el vencimiento al recibir) y días
/// mínimos de vida que debe tener un lote para aceptarlo.
/// </summary>
public sealed record ItemShelfLife(Guid ItemId, int? ShelfLifeDays, int? MinDaysOnReceipt);

// ---------- Pedidos y despachos ----------

public static class OrderStatuses
{
    public const string Pending = "pending";
    public const string Picking = "picking";

    /// <summary>Salió una parte; el resto queda pendiente (faltante) y sigue reservado.</summary>
    public const string Partial = "partial";
    public const string Dispatched = "dispatched";
    public const string Cancelled = "cancelled";

    public static readonly string[] All = [Pending, Picking, Partial, Dispatched, Cancelled];

    /// <summary>Se puede preparar, despachar o cancelar.</summary>
    public static bool IsOpen(string status) => status is Pending or Picking or Partial;
}

public static class OrderApprovals
{
    /// <summary>Supera el crédito del cliente en el ERP: no reserva ni se prepara hasta <c>order.approved</c>.</summary>
    public const string Pending = "pending";
    public const string Approved = "approved";
}

/// <summary>Una parada del recorrido de picking: de qué ubicación tomar cuánto de un artículo (sin ubicación, al final).</summary>
public sealed record PickStop(Guid ItemId, string Sku, string ItemName, string Unit, string? Barcode, Guid? LocationId, string? LocationCode,
    decimal ToPick);

/// <summary>Lo ya tomado (aún sin despachar).</summary>
public sealed record PickRow(Guid Id, Guid ItemId, string ItemName, Guid? LocationId, string? LocationCode, decimal Quantity,
    DateTimeOffset PickedAt, string? UserName);

/// <summary>Cuánto falta preparar de cada artículo y cuánto no hay en la sede.</summary>
public sealed record PickNeed(Guid ItemId, string Sku, string ItemName, decimal Remaining, decimal Picked, decimal Missing);

public sealed record PickPlan(OrderRow Order, IReadOnlyList<PickStop> Stops, IReadOnlyList<PickRow> Picked, IReadOnlyList<PickNeed> Needs)
{
    public bool Complete => Needs.All(n => n.Picked >= n.Remaining);
}

public sealed record OrderRow(
    Guid Id, string Number, string Source, Guid? CustomerId, string? CustomerName, Guid SiteId, string? SiteName,
    string Status, decimal Total, string CurrencyCode, DateTimeOffset CreatedAt, DateTimeOffset? DispatchedAt, string? Carrier,
    string? TrackingNumber, int LineCount, decimal Units, string? Approval = null, decimal Shipped = 0)
{
    public bool PendingApproval => Approval == OrderApprovals.Pending;
}

public sealed record OrderLineRow(int LineNo, Guid ItemId, string Sku, string ItemName, decimal Quantity, decimal UnitPrice, decimal OnHand,
    decimal Shipped = 0);

// ---------- Recepciones ----------

public static class ReceiptStatuses
{
    public const string Expected = "expected";
    public const string Received = "received";
    public const string Cancelled = "cancelled";

    public static readonly string[] All = [Expected, Received, Cancelled];
}

public sealed record ReceiptRow(
    Guid Id, string Number, Guid? PurchaseOrderId, Guid? SupplierId, string? SupplierName, Guid SiteId, string? SiteName,
    string Status, DateTimeOffset CreatedAt, DateTimeOffset? ReceivedAt, int LineCount, decimal ExpectedUnits, decimal ReceivedUnits);

public sealed record ReceiptLineRow(int LineNo, Guid ItemId, string Sku, string ItemName, decimal ExpectedQty, decimal ReceivedQty, decimal? UnitCost);

// ---------- Panel ----------

public sealed record DashboardSummary(
    int ActiveItems, int Categories, decimal UnitsOnHand, decimal? UnitsChangePct, int OrdersOpen, int OrdersPicking,
    int Dispatched30d, decimal? DispatchedChangePct, int ReceiptsExpected, int LowStock, int OutOfStock, decimal? StockValue,
    string CurrencyCode, decimal? CostOfSales30d = null, decimal? Sales30d = null, decimal? Margin30d = null);

// ---------- Ubicaciones (fase 4a) ----------

/// <summary>Tipo de ubicación: dónde se recibe, se almacena, se prepara, se despacha o se aparta.</summary>
public static class LocationKinds
{
    public const string Receiving = "receiving";
    public const string Storage = "storage";
    public const string Picking = "picking";
    public const string Shipping = "shipping";
    public const string Quarantine = "quarantine";

    public static readonly IReadOnlyList<string> All = [Receiving, Storage, Picking, Shipping, Quarantine];

    /// <summary>
    /// Orden en que se descuenta el stock de las ubicaciones cuando baja sin indicar de dónde (despacho, venta, ajuste):
    /// primero lo que está por salir, al final la cuarentena. Es fijo para que todos los equipos lleguen al mismo saldo.
    /// </summary>
    public static int ConsumeOrder(string kind) => kind switch
    {
        Shipping => 0,
        Picking => 1,
        Storage => 2,
        Receiving => 3,
        _ => 4,
    };
}

public sealed record LocationRow(
    Guid Id, Guid SiteId, string Code, string Kind, string? Zone, string? Aisle, string? Rack, string? Level, string? Position,
    Guid? FixedItemId, string? FixedItemName, bool IsActive, decimal Units, int Items);

/// <summary>Alta o edición de una ubicación. El código se arma con las partes si no se escribe.</summary>
public sealed record LocationDraft(
    Guid SiteId, string? Code, string Kind, string? Zone = null, string? Aisle = null, string? Rack = null, string? Level = null,
    string? Position = null, Guid? FixedItemId = null, bool IsActive = true);

/// <summary>Alta por rango: zona y pasillos y niveles de-a (A-01-01 … A-10-05).</summary>
public sealed record LocationRange(Guid SiteId, string Zone, int AisleFrom, int AisleTo, int LevelFrom, int LevelTo, string Kind);

public sealed record LocationStockRow(Guid LocationId, string LocationCode, Guid ItemId, string Sku, string ItemName, string Unit,
    decimal Quantity);

/// <summary>Lo recibido que todavía no tiene ubicación, con la que se sugiere.</summary>
public sealed record PutawayRow(Guid SiteId, string? SiteName, Guid ItemId, string Sku, string ItemName, string Unit, decimal Pending,
    Guid? SuggestedLocationId, string? SuggestedCode, string? SuggestionReason);

public sealed record RelocationRow(Guid Id, DateTimeOffset OccurredAt, Guid ItemId, string ItemName, string? FromCode, string? ToCode,
    decimal Quantity, string? UserName, string? Reference);

/// <summary>Por qué se sugiere una ubicación al guardar.</summary>
public static class PutawayReasons
{
    public const string Fixed = "fixed";
    public const string SameItem = "same_item";
    public const string Empty = "empty";
}
