namespace VortexWms.Core.Integration;

// Contratos de los eventos de la suite Vortex, versión 1 (docs/SISTEMAS.md del hub, sección 6).
// Se serializan con VortexJson.Options (camelCase). Se pueden agregar campos, nunca quitar ni cambiar su significado;
// al leer, los campos desconocidos se ignoran.

public static class SuiteEvents
{
    // Publica el WMS
    public const string GoodsReceived = "goods.received";
    public const string StockAdjusted = "stock.adjusted";
    public const string StockTransferred = "stock.transferred";
    public const string ShipmentDispatched = "shipment.dispatched";

    // Consume el WMS (order.created también lo publica el WMS cuando funciona sin ERP ni CRM)
    public const string SaleCompleted = "sale.completed";
    public const string SaleVoided = "sale.voided";
    public const string SaleReturned = "sale.returned";
    public const string OrderCreated = "order.created";
    public const string PurchaseOrderCreated = "purchase_order.created";
}

public sealed record QuantityLine(Guid ItemId, decimal Quantity);

// ---------- Publicados por el WMS ----------

/// <summary><c>goods.received</c>. Extiende v1 con <c>number</c> y <c>supplierId</c>.</summary>
public sealed record GoodsReceived(
    Guid ReceiptId, string Number, Guid? PurchaseOrderId, Guid SiteId, Guid? SupplierId, IReadOnlyList<QuantityLine> Lines);

/// <summary>
/// <c>stock.adjusted</c>: se publica en <b>cada</b> cambio de existencias de un artículo en una sede (ajuste, recepción,
/// transferencia, despacho, venta). <c>quantityOnHand</c> es el saldo absoluto: quien lleve un espejo del stock debe
/// usarlo en vez de sumar <c>quantityDelta</c>, así no cuenta dos veces lo que también llega en
/// <c>goods.received</c> o <c>stock.transferred</c>. Extiende v1 con <c>reference</c> (número de documento).
/// </summary>
public sealed record StockAdjusted(
    Guid SiteId, Guid ItemId, decimal QuantityDelta, decimal QuantityOnHand, string Reason, string? Reference);

/// <summary><c>stock.transferred</c>. Extiende v1 con <c>number</c>.</summary>
public sealed record StockTransferred(Guid TransferId, string Number, Guid FromSiteId, Guid ToSiteId, IReadOnlyList<QuantityLine> Lines);

/// <summary><c>shipment.dispatched</c>. Extiende v1 con <c>number</c>, <c>siteId</c> y <c>lines</c>.</summary>
public sealed record ShipmentDispatched(
    Guid ShipmentId, Guid OrderId, Guid CustomerId, string? Carrier, string? TrackingNumber, string Number, Guid SiteId,
    IReadOnlyList<QuantityLine> Lines);

// ---------- Consumidos por el WMS ----------

public sealed record SaleLine(Guid ItemId, string? Sku, decimal Quantity, decimal UnitPrice, decimal Discount, decimal Total);

public sealed record SaleCompleted(
    Guid SaleId, string? Number, Guid SiteId, Guid? CustomerId, string? CurrencyCode, decimal Total, IReadOnlyList<SaleLine>? Lines);

public sealed record SaleVoided(Guid SaleId, string? Number, string? Reason);

public sealed record SaleReturnLine(Guid ItemId, decimal Quantity, decimal Total);

public sealed record SaleReturned(Guid SaleId, Guid ReturnId, Guid SiteId, IReadOnlyList<SaleReturnLine>? Lines, decimal Total);

public sealed record OrderLine(Guid ItemId, decimal Quantity, decimal UnitPrice);

public sealed record OrderCreated(
    Guid OrderId, string? Number, Guid CustomerId, Guid? SiteId, IReadOnlyList<OrderLine>? Lines, decimal Total, string? CurrencyCode);

public sealed record PurchaseOrderLine(Guid ItemId, decimal Quantity, decimal UnitCost);

/// <summary><c>purchase_order.created</c>. <c>number</c> no está en v1: se usa si el ERP lo envía.</summary>
public sealed record PurchaseOrderCreated(
    Guid PurchaseOrderId, string? Number, Guid SupplierId, Guid SiteId, IReadOnlyList<PurchaseOrderLine>? Lines);
