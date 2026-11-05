# Integración del WMS con el hub y la suite

Vortex WMS sigue la guía del hub ([`vortex-api/docs/SISTEMAS.md`](https://github.com/ArcGabito/vortex-api/blob/main/docs/SISTEMAS.md)).
Este documento resume qué hace el WMS con cada dato y evento, y cómo se mantiene el stock coherente entre varios
equipos que trabajan sin conexión.

## Identidad y sesión

- No hay tabla de usuarios: el login es contra el hub (`ProductCode = "WMS"`, clave de sistema en `appsettings.json`
  o en la pantalla de ingreso).
- Los tokens se guardan en `SecureStorage`. Con el offline token vigente la app abre y opera sin red; el chip de
  sincronización avisa cuando el periodo sin conexión se agota.
- El heartbeat (cada 5 min) renueva el offline token y detecta accesos retirados o equipos dados de baja: en ese
  caso se cierra la sesión y el login muestra el motivo.
- Cada organización tiene su propia base SQLite (`vortex-wms-{organizationId}.db` en `AppDataDirectory`).

## Permisos (base común de la suite)

Roles del WMS (`session.Access.Role`, los asigna el OrgAdmin): `operario` (sin rol asignado), `supervisor`, `consulta`
y `administrador` (también artículos y almacenes). OrgAdmin y PlatformAdmin del hub son administradores. Cada acción
valida su permiso en Core y los ajustes, anulaciones y cancelaciones quedan en la auditoría local.

### Sedes por usuario

Si el administrador asigna sedes al usuario en el hub (`siteIds` del miembro del WMS), la sesión las trae en
`Access.SiteIds`: el usuario solo ve el stock, los movimientos, los pedidos y las recepciones de esas sedes, y solo
opera en ellas (recibir, ajustar, despachar, preparar, cancelar, cambiar ubicación y mínimo, transferir **desde**
ellas; puede transferir hacia cualquier sede). Lo valida Core. Sin sedes asignadas, o si es administrador de la
organización, trabaja en todas.

## Datos maestros (dueño: el hub)

| Dato | En el WMS | Altas/cambios desde el WMS |
|---|---|---|
| Artículos | Catálogo, precio para valorizar stock, si controla inventario | Sí (`PUT /api/master/items/{id}`) |
| Sedes | Almacenes y tiendas donde hay stock | Alta (`PUT /api/master/sites/{id}`) |
| Clientes y proveedores | Pedidos y recepciones | Alta (`PUT /api/master/parties/{id}`) |
| Empresa | Moneda por defecto | No |

- La copia local solo se actualiza desde el feed, por `id` y `version` (nunca se pisa una versión más nueva).
- Un alta hecha en el WMS se guarda al instante con `pending = 1` (se ve como **POR ENVIAR**) y se encola con un Id
  generado en el equipo. Si el hub la rechaza (RUC duplicado, SKU repetido…), aparece en
  **Configuración → Rechazadas por el hub**; al descartarla se quita el alta local.

## Datos propios del WMS

Existencias por sede, reservas, ubicación (bin), stock mínimo, kardex (`movements`), recepciones, pedidos y
despachos. Viven en la base local de cada equipo; lo que otros necesitan saber sale como evento. La ubicación y el
stock mínimo por artículo y sede se comparten con los demás equipos del WMS por `wms.record.upserted` (gana el
cambio más reciente).

## Eventos (versión 1)

### Qué publica el WMS

| Evento | Cuándo | Id del evento |
|---|---|---|
| `stock.adjusted` | **Todo** cambio de stock (recepción, ajuste, transferencia, despacho, venta del POS) | Id del movimiento |
| `goods.received` | Al recibir mercadería (directa o de una orden de compra) | Id de la recepción |
| `stock.transferred` | Al transferir entre sedes | Id de la transferencia |
| `shipment.dispatched` | Al despachar un pedido | Id del despacho |
| `order.created` | Al crear un pedido en el WMS (`source = WMS`) | Id del pedido |
| `order.picking_started` | Al empezar a preparar un pedido | Determinista por pedido |
| `order.cancelled` | Al cancelar un pedido (libera la reserva) | Determinista por pedido |
| `receipt.cancelled` | Al anular una recepción esperada | Determinista por recepción |
| `wms.record.upserted` | Al cambiar la ubicación o el mínimo de un artículo en una sede | Id nuevo; `updatedAt` en los datos |

`stock.adjusted` lleva el delta y la existencia resultante (`quantityDelta`, `quantityOnHand`), el motivo y la
referencia del documento. Desde la fase 1 también el costo con que se valorizó el movimiento (`unitCost`, `totalCost`,
en la moneda de la empresa) y, en ventas y despachos, el importe de venta (`saleAmount`); las líneas de
`goods.received` llevan `unitCost` y `totalCost`. Son campos aditivos.

### Costo y kardex valorizado

Costo promedio ponderado móvil por artículo y sede. Las compras (costo de la orden de compra o del ingreso directo),
las transferencias recibidas (con el costo con que salieron del origen) y las anulaciones y devoluciones de ventas
(con el costo con que salieron) recalculan el promedio; las salidas salen al promedio vigente. Cada movimiento guarda
costo unitario, costo total, saldo valorizado y su tipo de operación de la Tabla 12 de SUNAT. El orden del kardex es
por fecha y luego por Id: un movimiento atrasado que llega de otro equipo recalcula desde su fecha, así todos los
equipos llegan al mismo kardex. Los montos se guardan como texto decimal exacto. Los costos solo los ven los roles con
permiso `cost.view` (supervisor y administradores).

### Qué consume el WMS

| Evento | Efecto |
|---|---|
| `sale.completed` (POS) | Descuenta stock en la sede de la venta (puede quedar negativo: la venta ya ocurrió) |
| `sale.voided` (POS) | Devuelve exactamente lo que descontó esa venta |
| `sale.returned` (POS) | Reingresa lo devuelto |
| `order.created` (ERP, CRM) | Crea el pedido y reserva stock en su sede |
| `purchase_order.created` (ERP) | Crea la recepción **Por recibir** |
| `order.cancelled` (ERP, CRM u otro equipo WMS) | Cancela el pedido y libera la reserva; si ya se despachó, prevalece el despacho |
| `stock.adjusted`, `goods.received`, `shipment.dispatched`, `order.picking_started`, `receipt.cancelled`, `wms.record.upserted` (otro equipo WMS) | Ver abajo |

- Cada evento se aplica una sola vez (`processed_events`), en la misma transacción que su efecto.
- `schemaVersion` distinto de 1 o `data` ilegible se registran como ignorados y no traban el feed.
- Los tipos desconocidos se ignoran.

## Varios equipos con el mismo WMS

El WMS se suscribe también a sus propios eventos (`IncludeOwnEvents = true`):

1. Cada movimiento de stock publica `stock.adjusted` con **el Id del movimiento como Id del evento**. El equipo que lo
   generó lo reconoce (ya tiene ese movimiento) y no lo aplica dos veces; los demás aplican el delta.
2. Los movimientos que vienen de otro sistema usan **Ids deterministas** (SHA-256 del tipo de documento, el documento,
   la sede y el artículo). Si dos equipos procesan la misma venta del POS, generan el mismo movimiento y el mismo
   evento, y el hub deduplica el segundo.
3. Las recepciones creadas desde una orden de compra también tienen Id determinista, así todos los equipos hablan de
   la misma recepción. Los movimientos de una recepción y de un despacho, y el despacho mismo, también son
   deterministas (documento y artículo; la sede no entra): si dos equipos reciben la misma orden de compra o despachan
   el mismo pedido sin conexión, el hub se queda con lo que le llegó primero y el stock se mueve una sola vez.
5. **Conflictos.** Si esos dos equipos registraron datos distintos (otra cantidad recibida, otra sede de despacho, uno
   canceló y el otro despachó), cada equipo adopta la versión del hub: deshace su movimiento y aplica el del hub. El
   caso queda en `sync_conflicts` y se ve en Configuración → Conflictos entre equipos. Un despacho prevalece sobre una
   cancelación: la mercadería ya salió.
6. Los estados viajan como eventos: empezar la preparación (`order.picking_started`), cancelar un pedido
   (`order.cancelled`) y anular una recepción (`receipt.cancelled`).
7. `processed_events` guarda los eventos aplicados durante 180 días; los más viejos se borran al abrir la base (el feed
   no los vuelve a entregar).
4. Los documentos se numeran por equipo para no chocar sin conexión: `REC-9AFE-00001`, `TRF-…`, `PED-…`, `DES-…`
   (el prefijo sale del Id del equipo y se ve en Configuración).

### Limitaciones conocidas

- El ERP y el CRM todavía no publican ni consumen `order.cancelled`; el WMS ya lo aplica venga de donde venga.
