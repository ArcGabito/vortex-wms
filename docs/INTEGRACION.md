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
despachos. Viven solo en la base local de cada equipo; lo que otros necesitan saber sale como evento.

## Eventos (versión 1)

### Qué publica el WMS

| Evento | Cuándo | Id del evento |
|---|---|---|
| `stock.adjusted` | **Todo** cambio de stock (recepción, ajuste, transferencia, despacho, venta del POS) | Id del movimiento |
| `goods.received` | Al recibir mercadería (directa o de una orden de compra) | Id de la recepción |
| `stock.transferred` | Al transferir entre sedes | Id de la transferencia |
| `shipment.dispatched` | Al despachar un pedido | Id del despacho |
| `order.created` | Al crear un pedido en el WMS (`source = WMS`) | Id del pedido |

`stock.adjusted` lleva el delta y la existencia resultante (`quantityDelta`, `quantityOnHand`), el motivo y la
referencia del documento.

### Qué consume el WMS

| Evento | Efecto |
|---|---|
| `sale.completed` (POS) | Descuenta stock en la sede de la venta (puede quedar negativo: la venta ya ocurrió) |
| `sale.voided` (POS) | Devuelve exactamente lo que descontó esa venta |
| `sale.returned` (POS) | Reingresa lo devuelto |
| `order.created` (ERP, CRM) | Crea el pedido y reserva stock en su sede |
| `purchase_order.created` (ERP) | Crea la recepción **Por recibir** |
| `stock.adjusted`, `goods.received`, `shipment.dispatched` (otro equipo WMS) | Ver abajo |

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
   la misma recepción.
4. Los documentos se numeran por equipo para no chocar sin conexión: `REC-9AFE-00001`, `TRF-…`, `PED-…`, `DES-…`
   (el prefijo sale del Id del equipo y se ve en Configuración).

### Limitaciones conocidas

- Empezar la preparación (picking) y cancelar pedidos o anular recepciones no se replica a otros equipos: el catálogo
  de eventos v1 no tiene `order.cancelled` ni `receipt.cancelled`. Según la sección 10 de la guía, se propondrá en el
  hub en vez de resolverlo con un parche local.
- Los movimientos de despacho y de recepción usan Ids aleatorios: si dos equipos despachan el mismo pedido (o reciben
  la misma orden de compra) sin conexión, el stock se mueve dos veces. Se corrige en la fase 0 de
  [ROADMAP.md](ROADMAP.md).
- `processed_events` crece sin límite; falta una política de limpieza por antigüedad.
