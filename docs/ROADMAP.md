# Hoja de ruta

Guía para que Vortex WMS pase de gestor de stock multisede a un WMS empresarial que compita con Manhattan, SAP EWM,
Odoo Inventory y Fishbowl (pymes y medianas empresas de Perú, LATAM, EE. UU. y Europa). Va por fases en el orden en que conviene construirlas: cada una se apoya en la
anterior. **Lo fiscal y tributario (GRE, PLE) queda para el final**, pero desde la fase 1 se guardan los datos que
necesitará, para que entonces sea una exportación y no un rediseño.

Los tamaños (S, M, L, XL) son relativos entre fases y se vuelven a estimar al empezar cada una.

> **Alcance internacional (2026-11).** La suite apunta a **Perú, el resto de LATAM, EE. UU. y Europa** en español,
> inglés y portugués ([ROADMAP-SUITE](https://github.com/ArcGabito/vortex-api/blob/main/docs/ROADMAP-SUITE.md)).
> Antes de la fase 0 va la **base común de la suite**: impuestos y monedas de `VortexApi.Suite` (el costo y el valor
> del stock se redondean según la moneda de la empresa), formatos regionales, idiomas y auditoría. Los roles de la
> fase 0 usan la matriz de permisos común. La fase 8 deja de ser solo Perú: cada país es un paquete de localización.
> La interfaz ya está en español, inglés y portugués, con selector en Configuración → Este equipo, y los mensajes de
> validación también salen en ese idioma.

Cómo usarla:

1. **Requisitos empresariales** es la lista de lo que debe tener el WMS. Cada requisito tiene un código (`WMS-xx`),
   su estado y la fase que lo cubre.
2. **Fases** dice en qué orden se construyen, qué datos y eventos necesitan y cuándo se dan por terminadas.
3. Al cerrar una fase se actualiza su **Estado** y el de sus requisitos, y se anota en el `CHANGELOG`.

## Requisitos empresariales

Estados: **Hecho**, **Parcial** (existe una parte; la fase indica qué falta) y **Pendiente**.

### Stock y costo

| # | Requisito | Estado | Fase |
|---|---|---|---|
| WMS-01 | Stock por sede con reservas, mínimo y kardex inmutable | Hecho | — |
| WMS-02 | Ajustes con motivo y transferencias entre sedes | Hecho | — |
| WMS-03 | Costo promedio ponderado y kardex valorizado | Hecho | 1 |
| WMS-04 | Valor del inventario a costo, costo de lo vendido y margen | Hecho | 1 |
| WMS-05 | Tipo de operación SUNAT (Tabla 12) en cada movimiento | Hecho | 1 |
| WMS-06 | Transferencias en tránsito con diferencias al llegar | Pendiente | 7 |

### Artículos

| # | Requisito | Estado | Fase |
|---|---|---|---|
| WMS-10 | Presentaciones con factor y varios códigos de barras (EAN, DUN-14) | Hecho | 2 |
| WMS-11 | Peso, unidad UN/ECE y control por lote o serie | Hecho | 2 |
| WMS-12 | Escaneo con la cámara en Android e iOS | Pendiente | 2 |
| WMS-13 | Volumen, vida útil y días mínimos de vida al recibir | Parcial: vida útil y días mínimos; falta el volumen | 3 |
| WMS-14 | Kits y ensamblado (armar y desarmar con su costo) | Pendiente | 9 |

### Lotes y series

| # | Requisito | Estado | Fase |
|---|---|---|---|
| WMS-20 | Stock por lote con vencimiento | Hecho | 3 |
| WMS-21 | FEFO en picking y despacho | Hecho | 3 |
| WMS-22 | Estados de lote (disponible, cuarentena, bloqueado) y alertas de vencimiento | Hecho | 3 |
| WMS-23 | Series por unidad al recibir y despachar | Hecho | 3 |
| WMS-24 | Trazabilidad hacia proveedores y clientes para retiros del mercado | Hecho | 3 |

### Entradas

| # | Requisito | Estado | Fase |
|---|---|---|---|
| WMS-30 | Recepciones de OC del ERP y directas, con diferencias | Hecho | — |
| WMS-31 | Recepción en cualquier presentación | Hecho | 2 |
| WMS-32 | Guardado guiado con ubicación sugerida | Pendiente | 4a |
| WMS-33 | Aviso anticipado de envío (ASN) del proveedor | Pendiente | 9 |
| WMS-34 | Citas de muelle para recepción y despacho | Pendiente | 9 |
| WMS-35 | Cross-docking (de la recepción al despacho sin almacenar) | Pendiente | 9 |

### Ubicaciones

| # | Requisito | Estado | Fase |
|---|---|---|---|
| WMS-40 | Ubicación por artículo en texto libre | Hecho | — |
| WMS-41 | Mapa de ubicaciones (zona, pasillo, rack, nivel, posición) con código escaneable | Pendiente | 4a |
| WMS-42 | Stock por ubicación y traslados internos | Pendiente | 4a |
| WMS-43 | Reabastecimiento de ubicaciones de picking | Pendiente | 4b |
| WMS-44 | Ubicación óptima según rotación (slotting) | Pendiente | 9 |

### Salidas

| # | Requisito | Estado | Fase |
|---|---|---|---|
| WMS-50 | Pedidos del ERP, CRM y WMS; preparación y despacho; ventas del POS | Hecho | — |
| WMS-51 | Picking guiado por pedido y por grupo, con recorrido ordenado | Pendiente | 4b |
| WMS-52 | Olas de picking planificadas por transportista u hora de corte | Pendiente | 9 |
| WMS-53 | Despacho parcial con saldo pendiente | Pendiente | 4b |
| WMS-54 | Empaque en bultos con peso y contenido | Pendiente | 4b |
| WMS-55 | Integración con transportistas (tarifa, etiqueta y seguimiento) | Pendiente | 9 |

### Control de inventario

| # | Requisito | Estado | Fase |
|---|---|---|---|
| WMS-60 | Ajuste por conteo de un artículo | Hecho | — |
| WMS-61 | Conteo cíclico programado por ubicación o clase ABC | Pendiente | 5 |
| WMS-62 | Conteo ciego, recuento automático e inventario general | Pendiente | 5 |
| WMS-63 | Aprobación de diferencias y exactitud de inventario | Pendiente | 5 |
| WMS-64 | Devoluciones de clientes y a proveedores; mermas con foto y aprobación | Pendiente | 5 |

### Operación con varias personas

| # | Requisito | Estado | Fase |
|---|---|---|---|
| WMS-70 | Varios equipos sin duplicar stock (Ids deterministas, conflictos visibles) | Hecho | 0 |
| WMS-71 | Roles (`operario`, `supervisor`, `consulta`, `administrador`) validados en Core | Hecho | 0 |
| WMS-72 | Sedes por usuario | Hecho | 0 |
| WMS-73 | Auditoría de ajustes, recepciones anuladas y pedidos cancelados | Hecho | 0 |
| WMS-74 | Cola de tareas por operario asignada por el supervisor | Pendiente | 4b |
| WMS-75 | Productividad por operario | Pendiente | 6 |

### Abastecimiento, reportes y etiquetas

| # | Requisito | Estado | Fase |
|---|---|---|---|
| WMS-80 | Mínimo y máximo por sede, consumo promedio y cobertura | Parcial: mínimo por artículo | 6 |
| WMS-81 | Sugerencia de compra que llega al ERP | Pendiente | 6 |
| WMS-82 | Rotación, ABC, antigüedad, por vencer, sin movimiento y comparativo entre sedes | Pendiente | 6 |
| WMS-83 | Exportación a Excel, CSV y PDF | Parcial: CSV | 6 |
| WMS-84 | Etiquetas de artículo, ubicación y bulto (ZPL y PDF) | Pendiente | 6 |

### Canales, 3PL y fiscal

| # | Requisito | Estado | Fase |
|---|---|---|---|
| WMS-90 | Tiendas en línea vía conectores del hub | Pendiente | 7 |
| WMS-91 | Varios propietarios del stock (3PL) y cobro de almacenaje | Pendiente | 9 |
| WMS-92 | Guía de remisión electrónica (Perú) y equivalentes por país | Pendiente | 8 |
| WMS-93 | PLE 13.1 y 12.1 | Pendiente | 8 |

## Dónde estamos (2026-11)

Fases 0 a 3 hechas (salvo el escaneo con cámara de la fase 2 y el volumen del artículo). Lo siguiente es la fase 4a
(ubicaciones y guardado guiado).

## Reglas que el plan respeta

- **El hub es dueño** de usuarios, roles, artículos, sedes y terceros ([SISTEMAS.md](SISTEMAS.md)). Si una fase necesita
  un campo o evento que el hub no tiene, primero se propone en `vortex-api` (sección 10 de la guía).
- **Los eventos v1 solo crecen**: se agregan campos, nunca se quitan ni cambian de significado.
- **Todo cambio de stock pasa por `StockLedger.ApplyAsync`** en la transacción del documento.
- **Sin conexión primero**: cada equipo tiene su base y comparte con los demás por eventos. Toda función nueva
  define cómo se replica y qué pasa si dos equipos hacen lo mismo sin red.
- **Módulos opcionales por `features`** del hub (`session.Access.HasFeature`), no por configuración propia.

## Fase 0: cimientos multiequipo y permisos · M

**Objetivo:** que lo que viene no multiplique los problemas entre equipos, y que cada usuario haga solo lo que le
corresponde.

- **Documentos compartidos sin duplicar stock.** Hoy los movimientos de despacho y de recepción usan Ids aleatorios
  (solo las ventas del POS usan `DeterministicId`). Si dos equipos despachan el mismo pedido sin conexión, el stock
  baja dos veces. Pasar a Ids deterministas (documento, línea, sede, artículo) y, si los dos equipos registraron
  cantidades distintas, dejar el conflicto visible en Configuración.
- **Estados de documentos replicados.** Cancelar un pedido, anular una recepción o empezar la preparación hoy no
  llega a los demás equipos (limitación ya anotada en [INTEGRACION.md](INTEGRACION.md)). Proponer `order.cancelled`,
  `receipt.cancelled` y `order.picking_started`; el ERP y el CRM también podrán cancelar pedidos.
- **Datos propios del WMS compartidos entre equipos** (ubicaciones, ajustes por artículo, tareas). Un mecanismo común
  en `VortexWms.Core`: cada registro viaja completo en un evento con su versión y cada equipo aplica la más alta.
  Ver decisión 1.
- **Roles del WMS** asignados en el hub (`session.Access.Role`): `administrador`, `supervisor`, `operario` y
  `consulta`, con una matriz de acciones (ajustar stock, aprobar diferencias, ver costos, configurar ubicaciones…).
  Se validan en los servicios de Core, no solo ocultando botones; funcionan sin conexión porque vienen en la sesión.
- **Sedes por usuario**: proponer en el hub las sedes asignadas a cada miembro del sistema. Mientras no exista, cada
  usuario ve todas.
- **Mantenimiento**: limpieza de `processed_events` por antigüedad.

**Terminado cuando:** en `MultiDeviceTests`, dos equipos sin conexión despachan el mismo pedido y el stock baja una
sola vez; cancelar un pedido en un equipo libera la reserva en los demás; un `operario` que intenta ajustar stock
recibe `WmsValidationException`.

**Estado (2026-11):** hecha. Sedes por usuario con `Access.SiteIds` del hub (vacío = todas; los administradores de la
organización ven todas). Ids deterministas en
recepciones y despachos con conflictos visibles en Configuración, `order.picking_started`, `order.cancelled`,
`receipt.cancelled` y `wms.record.upserted` (ubicación y mínimo), roles, y limpieza de `processed_events` a los 180
días. Pruebas en `OfflineConflictTests`.

## Fase 1: costo y kardex valorizado · M-L

**Objetivo:** saber cuánto vale el inventario a costo y cuánto costó lo que salió.

- **Costo promedio ponderado móvil por artículo y sede** (el método más usado en Perú; PEPS puede venir después).
- **De dónde sale el costo:** de la OC del ERP (`purchase_order.created` ya trae `unitCost`), del ingreso directo
  (obligatorio), de la sede de origen en las transferencias y del costo vigente en ajustes positivos. Las salidas
  (ventas del POS, despachos, mermas) salen al costo vigente.
- **Kardex valorizado:** por movimiento, costo unitario, costo total y saldo valorizado, con filtro por periodo.
- **Valor del inventario a costo** en el Panel y en Inventario, costo de lo vendido y margen estimado. Los costos solo
  los ven los roles con permiso.
- **Preparar lo fiscal sin hacerlo:** cada movimiento guarda su tipo de operación según la Tabla 12 de SUNAT y el
  documento de origen.

**Datos y eventos:** costo en `movements` y en los saldos; `stock.adjusted` y las líneas de `goods.received` ganan
`unitCost` y `totalCost` (aditivos). Los montos de costo se guardan con precisión decimal, no como `REAL` (decisión 3).

**Multiequipo:** el promedio depende del orden de los movimientos. Si llega tarde un movimiento con fecha anterior
(un equipo que estuvo sin red), se recalcula desde ese punto, ordenando por fecha e Id para que todos los equipos
lleguen al mismo resultado.

**Terminado cuando:** una secuencia conocida de entradas y salidas da el promedio esperado; un movimiento atrasado
deja el mismo kardex valorizado en dos equipos; el Panel muestra el valor a costo.

**Estado (2026-11):** hecha. Decisiones 2 y 3 tomadas como se recomendaba (promedio ponderado móvil por artículo y
sede; costos en texto decimal exacto). Pruebas en `CostingTests`.

## Fase 2: artículos de almacén · M

**Objetivo:** recibir por caja, despachar por unidad y encontrar cualquier artículo escaneando.

- **Presentaciones** con factor (caja × 12, paquete × 6) y su código de la Tabla 6 de SUNAT. Se recibe, despacha y
  cuenta en cualquier presentación; el stock siempre queda en la unidad base.
- **Varios códigos de barras** por artículo (EAN de la unidad, DUN-14 de la caja).
- **Escaneo con cámara** en Android e iOS (hoy solo lector en modo teclado), con aviso de acierto y error.
- **Datos de almacén por artículo:** peso y volumen, control por lote o serie, vida útil y días mínimos de vida al
  recibir.

**Hub:** presentaciones, códigos, control lote/serie y peso también los usan el POS (vende por caja) y el ERP (compra
por caja; la GRE pide peso), así que se proponen como campos de `item`. Vida útil y días mínimos son solo del WMS.

**Terminado cuando:** recibir 2 cajas × 12 suma 24 unidades; escanear el DUN-14 elige la presentación caja; todo
funciona sin conexión.

**Estado (2026-11):** hecha en su parte de artículos y escaneo con lector (hub #25). Presentaciones, códigos de
unidad UN/ECE, peso y control lote/serie viven en el `item` del hub (decisión 4); el WMS indexa todos los códigos en
`item_barcodes` para escanear sin conexión, y recepciones, transferencias, pedidos y conteos se ingresan en
cualquier presentación. Pruebas en `PresentationTests`. **Pendiente:** escaneo con cámara (Android/iOS), volumen,
vida útil y días mínimos al recibir (estos dos se hacen con la fase 3, donde se usan).

## Fase 3: lotes, vencimientos y series · L

**Objetivo:** abrir rubros que hoy no pueden usar el WMS: farmacia, alimentos, cosmética, electrónica.

- **Stock por lote con vencimiento**, obligatorio al recibir si el artículo controla lote.
- **FEFO:** picking y despacho sugieren el lote que vence primero; cambiarlo requiere permiso.
- **Alertas** de lotes por vencer (plazos configurables). Los vencidos no se pueden despachar.
- **Estados de lote:** disponible, cuarentena, bloqueado.
- **Series:** una por unidad, al recibir y al despachar; no se despacha una serie que no está en stock.
- **Trazabilidad:** de un lote o serie a sus recepciones, proveedores, pedidos y clientes, para retiros del mercado.
- **Ventas del POS:** si la venta no trae lote, se descuenta por FEFO; si trae serie (campo aditivo en
  `sale.completed`), se usa esa.

**Datos y eventos:** una sola tabla de saldos detallados por (sede, artículo, ubicación, lote), diseñada ya para la
fase 4 aunque la ubicación quede vacía hasta entonces; `stock` sigue como total por sede. `lotCode`, `expiresAt` y
`serials` se agregan a `stock.adjusted`, `goods.received` y `sale.completed`.

**Terminado cuando:** el despacho sugiere FEFO; un lote vencido no sale; la trazabilidad de un lote lista sus clientes;
los lotes se replican entre equipos.

**Estado (2026-11):** hecha. Saldo por lote con vencimiento y estado (disponible, cuarentena, bloqueado), FEFO en
despachos, transferencias y ventas de caja, series por unidad, vida útil y días mínimos al recibir, alertas por vencer y
trazabilidad. El stock anterior a controlar lote queda "sin lote" y sale al final. Decisión 5 (planes por `features`)
sigue pendiente: los lotes están disponibles para todos. Pruebas en `LotsTests`. **Pendiente:** volumen del artículo.

## Fase 4a: ubicaciones y guardado · L

**Objetivo:** que el stock tenga un lugar exacto y el operario sepa dónde guardar.

- **Mapa de ubicaciones** por sede (zona › pasillo › rack › nivel › posición), con tipo (recepción, almacenamiento,
  picking, despacho, cuarentena) y código escaneable. Alta masiva por rango (`A-01-01` a `A-10-05`). Reemplaza el
  texto libre `bin`.
- **Stock por ubicación** y traslados internos, que no cambian el stock de la sede.
- **Guardado guiado:** al recibir se sugiere la ubicación (la fija del artículo, una con el mismo lote o una libre de
  la zona) y se confirma escaneándola.

**Hub:** evento `stock.relocated` para los traslados internos; solo lo consumen otros equipos del WMS.

## Fase 4b: picking, empaque y tareas · XL

**Objetivo:** operar el almacén con varias personas, no solo registrar el stock.

- **Picking** por pedido y por grupo de pedidos, con recorrido ordenado por ubicación y confirmación escaneando
  ubicación y artículo.
- **Faltantes:** despacho parcial con saldo pendiente.
- **Empaque:** bultos con peso y contenido.
- **Reabastecimiento** de ubicaciones de picking (mínimo y máximo por ubicación).
- **Tareas:** cola de trabajo por operario (guardar, preparar, reabastecer, contar), asignada por el supervisor; vista
  móvil pensada para Android con lector.

**Datos y eventos:** `shipment.dispatched` gana `lines` con lo despachado (aditivo), para que el ERP facture
despachos parciales. Las tareas se replican con el mecanismo de la fase 0; un operario solo ejecuta sus tareas, así
dos equipos no preparan lo mismo.

**Terminado cuando:** una prueba de punta a punta (recibir › guardar › pedido › picking guiado › empaque › despacho
parcial) pasa con dos equipos.

## Fase 5: conteos, devoluciones y mermas · M

- **Conteo cíclico programado:** por ubicación, por clase ABC (A cada mes, B cada trimestre, C cada semestre) o por
  artículos con diferencias.
- **Modalidades de conteo:** ciego (el operario no ve el saldo); recuento automático si la diferencia supera un
  umbral; inventario general por sede.
- **Ajustes:** la diferencia se aplica solo con aprobación del supervisor; durante el conteo se congela solo la
  ubicación contada.
- **Exactitud de inventario** como indicador.
- **Devoluciones de clientes** con inspección: vuelve a stock, a cuarentena o a merma, vinculada al despacho.
- **Devoluciones a proveedor**, vinculadas a la recepción.
- **Mermas** con motivo, foto y aprobación.

**Hub:** `shipment.returned` y `goods.returned`, para que el ERP emita notas de crédito y débito.

## Fase 6: abastecimiento, reportes y etiquetas · M-L

- **Mínimo y máximo por sede**, consumo promedio y días de cobertura.
- **Sugerencia de compra por proveedor**, que sale al ERP como `purchase.requested`; sin ERP, se exporta.
- **Reportes:** rotación, ABC, antigüedad, por vencer, sin movimiento, exactitud, productividad por operario,
  kardex valorizado por periodo y comparativo entre sedes. Exportación a Excel, CSV y PDF.
- **Etiquetas** de artículo, ubicación y bulto, en ZPL para impresoras Zebra (red o Bluetooth) y en PDF para
  impresoras comunes.

## Fase 7: canales y alcance · variable

- **Tiendas en línea** (WooCommerce, Shopify, Mercado Libre): conectores como integraciones del hub (credenciales de
  cliente OAuth) que publican `order.created` y leen el stock disponible de los eventos. El WMS no se conecta
  directo con la tienda. `stock.adjusted` gana `quantityAvailable`.
- **Transferencias en tránsito:** salida y llegada separadas, con diferencias al recibir.
- **Versión web:** hoy cada equipo tiene su propia base; una versión web necesita un servidor del WMS. Evaluar
  después de 1.0, empezando por un panel web de consulta alimentado por eventos.

## Fase 8: fiscal y tributario (Perú primero) · L

- **Guía de Remisión Electrónica** (remitente y, si la empresa traslada con flota propia, transportista). Los datos
  salen del despacho o la transferencia: motivo, peso bruto (fase 2), partida y llegada con ubigeo, transportista,
  vehículo y conductor. Quién la emite es la decisión 7. Hasta esta fase, la GRE la emite el sistema de facturación
  de la empresa.
- **PLE**: Formato 13.1 (inventario permanente valorizado) y 12.1 (unidades físicas), con los datos guardados desde
  la fase 1 (Tabla 12) y la fase 2 (Tabla 6).
- **Ubigeo** en las direcciones de sedes y terceros: propuesta al hub.
- Se activa con un `feature` del hub (por ejemplo `facturacion`).
- **Otros países**, con el mismo modelo de paquetes: carta porte (complemento del CFDI) en México, remisión
  electrónica en Colombia, guía de despacho electrónica (DTE 52) en Chile; en EE. UU. y la UE, documentos de envío
  y declaraciones de inventario según el país (Intrastat en la UE).

## Fase 9: operación avanzada y 3PL · L

**Objetivo:** atender centros de distribución con mucho volumen y operadores logísticos que guardan stock de
terceros.

- **ASN y citas de muelle** (WMS-33, WMS-34): el proveedor avisa qué envía y cuándo; la recepción se agenda por muelle
  y se recibe contra el aviso.
- **Cross-docking** (WMS-35): lo recibido para un pedido pendiente va directo a despacho.
- **Olas de picking** (WMS-52) por transportista u hora de corte, y **slotting** (WMS-44): sugerir la ubicación de
  cada artículo según su rotación.
- **Kits y ensamblado** (WMS-14): armar y desarmar con el costo de sus componentes.
- **Transportistas** (WMS-55): tarifa, etiqueta y número de seguimiento al despachar, vía integraciones del hub.
- **3PL** (WMS-91): el stock tiene propietario; cada cliente del operador ve solo lo suyo y el almacenaje se cobra por
  posición, volumen o movimiento (el ERP factura con los datos del WMS).

**Hub:** `asn.created`; `trackingNumber` y `carrier` en `shipment.dispatched`; propietario del stock en los eventos de
stock (aditivos).

**Terminado cuando:** una recepción contra ASN marca las diferencias; un pedido de un cliente 3PL no puede tomar stock
de otro propietario.

## Propuestas al hub

Conviene enviar las de cada fase juntas, antes de empezarla.

| Propuesta | Tipo | Fase |
|---|---|---|
| `order.cancelled`, `receipt.cancelled`, `order.picking_started` | Eventos | 0 |
| Eventos para replicar datos propios del WMS (o registros genéricos por sistema, decisión 1) | Eventos o endpoint | 0 |
| Sedes asignadas por miembro del sistema | Campo en el acceso del miembro | 0 |
| `unitCost`, `totalCost` en `stock.adjusted` y `goods.received` | Campos aditivos (documentar en el catálogo) | 1 |
| Presentaciones con factor, código de barras y código SUNAT; control lote/serie; peso | Campos de `item` | 2 |
| `lots` (`lotCode`, `quantity`, `expiresAt`) y `serials` en eventos de stock y de venta (hecho) | Campos aditivos | 3 |
| `stock.relocated` | Evento | 4a |
| `lines` en `shipment.dispatched` | Campo aditivo | 4b |
| `shipment.returned`, `goods.returned` | Eventos | 5 |
| `purchase.requested` | Evento | 6 |
| `quantityAvailable` en `stock.adjusted` | Campo aditivo | 7 |
| Ubigeo en direcciones | Campo de `site` y `party` | 8 |
| `asn.created` | Evento | 9 |
| `trackingNumber` y `carrier` en `shipment.dispatched` | Campos aditivos | 9 |
| Propietario del stock (3PL) en los eventos de stock | Campo aditivo | 9 |

## Decisiones pendientes

| # | Decisión | Recomendación | Antes de |
|---|---|---|---|
| 1 | Cómo se replican los datos propios del WMS entre equipos | Eventos del WMS con versión, como ya se hace con el stock. Si el hub incorpora registros genéricos por sistema, migrar a eso | Fase 0 |
| 2 | Método de costo | Promedio ponderado móvil por artículo y sede | Fase 1 |
| 3 | Precisión de montos | Costos en decimal exacto (texto o entero escalado), no `REAL` | Fase 1 |
| 4 | Dónde viven presentaciones, lote/serie y peso | En el `item` del hub; vida útil y datos de ubicación en el WMS | Fase 2 |
| 5 | Planes comerciales | `lotes` y `ubicaciones` como `features` del hub, para vender un plan básico y uno avanzado | Fase 3 |
| 6 | Versión web | Después de 1.0 | Fase 7 |
| 7 | Quién emite la GRE | Se decide al empezar la fase 8 | Fase 8 |
| 8 | 3PL en la misma app o como edición aparte | La misma app con el `feature` `3pl` | Fase 9 |

## Versiones

| Versión | Contenido |
|---|---|
| 0.2.0 | Fase 0 |
| 0.3.0 | Fase 1 |
| 0.4.0 | Fase 2 |
| 0.5.0 | Fase 3 |
| 0.6.0 | Fase 4a |
| 0.7.0 | Fase 4b |
| 0.8.0 | Fase 5 |
| 0.9.0 | Fase 6 |
| 1.0.0 | Estabilización y piloto con una empresa real |
| 1.x | Fases 7, 8 y 9 |

## Cómo se trabaja cada fase

1. **Hub primero:** se envían sus propuestas a `vortex-api` con pruebas y se vuelve a empaquetar el SDK
   (`scripts/pack-sdk.*`).
2. **Base de datos:** una migración nueva al final de `Schema.Migrations`; nunca se edita una publicada.
3. **Stock:** todo cambio pasa por `StockLedger`, y los documentos que pueden procesar varios equipos usan
   `DeterministicId`.
4. **Pruebas:** unitarias y un escenario en `MultiDeviceTests` por cada documento o dato replicado.
5. **Documentación:** los eventos nuevos o ampliados van en [INTEGRACION.md](INTEGRACION.md); los cambios, en el
   `CHANGELOG`; los requisitos cumplidos se marcan en esta guía.
6. **Pantallas:** se arman con los componentes compartidos, en los tres idiomas, y se prueban en Windows y en la
   vista móvil.
