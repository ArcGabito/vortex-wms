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
    decimal OnHand, decimal Reserved);

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
    string? Description = null, bool TrackInventory = true, string Kind = "Product", string? TaxCode = "igv", bool IsActive = true);

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
    DateTimeOffset UpdatedAt)
{
    public decimal Available => OnHand - Reserved;
    public decimal Value => OnHand * Price;

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
    decimal Quantity, decimal OnHandAfter, string? Reason, string? Reference, string? UserName, string? Note);

public sealed record MovementQuery(string? Search = null, Guid? SiteId = null, string? Kind = null, int Page = 1, int PageSize = 15);

public sealed record DocumentLine(Guid ItemId, decimal Quantity, decimal? UnitPrice = null);

// ---------- Pedidos y despachos ----------

public static class OrderStatuses
{
    public const string Pending = "pending";
    public const string Picking = "picking";
    public const string Dispatched = "dispatched";
    public const string Cancelled = "cancelled";

    public static readonly string[] All = [Pending, Picking, Dispatched, Cancelled];
}

public sealed record OrderRow(
    Guid Id, string Number, string Source, Guid? CustomerId, string? CustomerName, Guid SiteId, string? SiteName,
    string Status, decimal Total, string CurrencyCode, DateTimeOffset CreatedAt, DateTimeOffset? DispatchedAt, string? Carrier,
    string? TrackingNumber, int LineCount, decimal Units);

public sealed record OrderLineRow(int LineNo, Guid ItemId, string Sku, string ItemName, decimal Quantity, decimal UnitPrice, decimal OnHand);

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
    int Dispatched30d, decimal? DispatchedChangePct, int ReceiptsExpected, int LowStock, int OutOfStock, decimal StockValue,
    string CurrencyCode);
