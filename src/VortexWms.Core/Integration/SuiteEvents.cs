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
    public const string ReceiptCancelled = "receipt.cancelled";
    public const string OrderPickingStarted = "order.picking_started";

    /// <summary>Replicación interna: datos propios del WMS (ubicación y mínimo por artículo y sede) entre sus equipos.</summary>
    public const string RecordUpserted = "wms.record.upserted";
    public const string StockRelocated = "stock.relocated";

    /// <summary>Devolución de un cliente recibida e inspeccionada (fase 5b), para la nota de crédito del ERP.</summary>
    public const string ShipmentReturned = "shipment.returned";

    /// <summary>Devolución al proveedor (fase 5b).</summary>
    public const string GoodsReturned = "goods.returned";

    // Publican el WMS, el ERP y el CRM
    public const string OrderCancelled = "order.cancelled";

    // Consume el WMS (order.created también lo publica el WMS cuando funciona sin ERP ni CRM)
    public const string SaleCompleted = "sale.completed";
    public const string SaleVoided = "sale.voided";
    public const string SaleReturned = "sale.returned";
    public const string OrderCreated = "order.created";

    /// <summary>El ERP aprobó un pedido que superaba el crédito del cliente: ya se reserva y se prepara.</summary>
    public const string OrderApproved = "order.approved";
    public const string PurchaseOrderCreated = "purchase_order.created";
}

/// <summary>Línea con cantidad; en <c>goods.received</c> también su costo (fase 1, aditivo).</summary>
public sealed record QuantityLine(Guid ItemId, decimal Quantity, decimal? UnitCost = null, decimal? TotalCost = null,
    IReadOnlyList<Domain.LotQuantity>? Lots = null, IReadOnlyList<string>? Serials = null);

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
/// Fase 1 agrega (aditivos) el costo con que se valorizó el movimiento (<c>unitCost</c>, <c>totalCost</c>, en la moneda
/// de la empresa) y, en ventas y despachos, el importe de venta (<c>saleAmount</c>).
/// Fase 3 agrega (aditivos) <c>lots</c> (lote, cantidad con signo y vencimiento) y <c>serials</c> del movimiento.
public sealed record StockAdjusted(
    Guid SiteId, Guid ItemId, decimal QuantityDelta, decimal QuantityOnHand, string Reason, string? Reference,
    decimal? UnitCost = null, decimal? TotalCost = null, decimal? SaleAmount = null, IReadOnlyList<Domain.LotQuantity>? Lots = null,
    IReadOnlyList<string>? Serials = null);

/// <summary><c>stock.transferred</c>. Extiende v1 con <c>number</c>.</summary>
public sealed record StockTransferred(Guid TransferId, string Number, Guid FromSiteId, Guid ToSiteId, IReadOnlyList<QuantityLine> Lines);

/// <summary><c>shipment.dispatched</c>. Extiende v1 con <c>number</c>, <c>siteId</c> y <c>lines</c>.</summary>
public sealed record ShipmentDispatched(
    Guid ShipmentId, Guid OrderId, Guid CustomerId, string? Carrier, string? TrackingNumber, string Number, Guid SiteId,
    IReadOnlyList<QuantityLine> Lines, bool? Partial = null, IReadOnlyList<ShipmentPackage>? Packages = null);

/// <summary>Bulto de un despacho (fase 4b2, aditivo): número dentro del pedido, peso y contenido.</summary>
public sealed record ShipmentPackage(int Number, decimal? WeightKg, IReadOnlyList<QuantityLine> Lines);

// ---------- Consumidos por el WMS ----------

/// <summary>Línea de venta del POS. <c>serials</c> (aditivo, fase 3): las series vendidas, si el POS las registró.</summary>
public sealed record SaleLine(Guid ItemId, string? Sku, decimal Quantity, decimal UnitPrice, decimal Discount, decimal Total,
    IReadOnlyList<string>? Serials = null);

public sealed record SaleCompleted(
    Guid SaleId, string? Number, Guid SiteId, Guid? CustomerId, string? CurrencyCode, decimal Total, IReadOnlyList<SaleLine>? Lines);

public sealed record SaleVoided(Guid SaleId, string? Number, string? Reason);

public sealed record SaleReturnLine(Guid ItemId, decimal Quantity, decimal Total);

public sealed record SaleReturned(Guid SaleId, Guid ReturnId, Guid SiteId, IReadOnlyList<SaleReturnLine>? Lines, decimal Total);

public sealed record OrderLine(Guid ItemId, decimal Quantity, decimal UnitPrice);

public sealed record OrderCreated(
    Guid OrderId, string? Number, Guid CustomerId, Guid? SiteId, IReadOnlyList<OrderLine>? Lines, decimal Total, string? CurrencyCode,
    string? ApprovalStatus = null);

/// <summary><c>order.approved</c> del ERP.</summary>
public sealed record OrderApproved(Guid OrderId, string? Number, string? ApprovedBy);

public sealed record PurchaseOrderLine(Guid ItemId, decimal Quantity, decimal UnitCost);

/// <summary><c>purchase_order.created</c>. <c>number</c> no está en v1: se usa si el ERP lo envía.</summary>
public sealed record PurchaseOrderCreated(
    Guid PurchaseOrderId, string? Number, Guid SupplierId, Guid SiteId, IReadOnlyList<PurchaseOrderLine>? Lines);

// ---------- Estados de documentos (fase 0) ----------

/// <summary><c>order.picking_started</c>: empezó la preparación del pedido.</summary>
public sealed record OrderPickingStarted(Guid OrderId, string? Number);

/// <summary><c>order.cancelled</c>: pedido cancelado antes de despacharse (libera la reserva).</summary>
public sealed record OrderCancelled(Guid OrderId, string? Number, string? Reason);

/// <summary><c>receipt.cancelled</c>: recepción esperada anulada.</summary>
public sealed record ReceiptCancelled(Guid ReceiptId, string? Number, Guid? PurchaseOrderId);

// ---------- Replicación interna del WMS ----------

public static class WmsRecordKinds
{
    /// <summary>Ubicación y stock mínimo de un artículo en una sede. Id: <c>{siteId}:{itemId}</c>.</summary>
    public const string StockSettings = "stock_settings";

    /// <summary>Estado de un lote en una sede (disponible, cuarentena, bloqueado). Id: <c>{siteId}:{itemId}:{lotCode}</c>.</summary>
    public const string LotStatus = "lot_status";

    /// <summary>Vida útil y días mínimos al recibir de un artículo. Id: <c>{itemId}</c>.</summary>
    public const string ShelfLife = "shelf_life";

    /// <summary>Ubicación del mapa de una sede. Id: el de la ubicación.</summary>
    public const string Location = "location";

    /// <summary>Tarea de un operario (guardar, preparar, reabastecer, contar). Id: el de la tarea.</summary>
    public const string Task = "task";

    /// <summary>Usuario que trabaja en el WMS (para asignarle tareas). Id: el del usuario en el hub.</summary>
    public const string Operator = "operator";

    /// <summary>Conteo de inventario (cabecera y líneas). Id: el del conteo.</summary>
    public const string Count = "count";

    /// <summary>Lo contado en una línea de un conteo. Id: <c>{conteo}:{línea}:{intento}</c> (1 conteo, 2 recuento).</summary>
    public const string CountResult = "count_result";

    /// <summary>Devolución de un cliente o a un proveedor (con sus líneas). Id: el de la devolución.</summary>
    public const string Return = "return";

    /// <summary>Merma reportada (con foto) y su aprobación. Id: el de la merma.</summary>
    public const string Shrinkage = "shrinkage";
}

/// <summary>
/// <c>wms.record.upserted</c>: la versión completa de un registro propio del WMS. Los demás equipos la aplican si es más
/// nueva que la suya (<c>updatedAt</c>), así el último cambio gana y repetir el evento no cambia nada.
/// </summary>
public sealed record WmsRecord(string Kind, string Id, DateTimeOffset UpdatedAt, System.Text.Json.JsonElement Data);

public sealed record StockSettingsData(Guid SiteId, Guid ItemId, string? Bin, decimal? ReorderPoint);

public sealed record LotStatusData(Guid SiteId, Guid ItemId, string LotCode, string Status, string? Note = null);

/// <summary>Ubicación del mapa de una sede (registro propio del WMS; gana el cambio más reciente).</summary>
public sealed record LocationData(
    Guid Id, Guid SiteId, string Code, string Kind, string? Zone, string? Aisle, string? Rack, string? Level, string? Position,
    Guid? FixedItemId, bool IsActive, decimal? MinQuantity = null, decimal? MaxQuantity = null);

/// <summary>Tarea de un operario (registro propio del WMS; gana el cambio más reciente).</summary>
public sealed record TaskData(
    Guid Id, Guid SiteId, string Kind, string Title, Guid? OrderId, Guid? LocationId, Guid? ItemId, decimal? Quantity, Guid? AssigneeId,
    string? AssigneeName, string Status, string? Notes, string? CreatedBy, DateTimeOffset CreatedAt, DateTimeOffset? DoneAt, string? DoneBy);

/// <summary>Usuario que usa el WMS en algún equipo, con su rol.</summary>
public sealed record OperatorData(Guid Id, string Name, string? Role);

/// <summary>
/// <c>stock.relocated</c>: traslado interno de stock entre ubicaciones de una sede (no cambia el stock de la sede). Sin
/// <c>fromLocationId</c> sale de lo que no tiene ubicación (guardar lo recibido); sin <c>toLocationId</c>, vuelve a quedar
/// sin ubicación. Solo lo consumen los demás equipos del WMS.
/// </summary>
public sealed record StockRelocated(Guid SiteId, Guid ItemId, Guid? FromLocationId, Guid? ToLocationId, decimal Quantity,
    string? Reference = null);

/// <summary><c>shipment.returned</c>: devolución de un cliente; <c>disposition</c>: restock, quarantine o scrap.</summary>
public sealed record ShipmentReturned(Guid ReturnId, string Number, Guid? OrderId, Guid? ShipmentId, Guid? CustomerId, Guid SiteId, string Reason,
    IReadOnlyList<ShipmentReturnedLine> Lines);

public sealed record ShipmentReturnedLine(Guid ItemId, decimal Quantity, string Disposition, string? LotCode = null);

/// <summary><c>goods.returned</c>: devolución al proveedor, al costo promedio.</summary>
public sealed record GoodsReturned(Guid ReturnId, string Number, Guid? PurchaseOrderId, Guid SupplierId, Guid SiteId, string Reason,
    IReadOnlyList<GoodsReturnedLine> Lines);

public sealed record GoodsReturnedLine(Guid ItemId, decimal Quantity, decimal UnitCost);
