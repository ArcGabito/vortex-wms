# Changelog

Todos los cambios relevantes de este proyecto se documentan en este archivo.

El formato se basa en [Keep a Changelog](https://keepachangelog.com/es-ES/1.1.0/)
y el proyecto sigue [Versionado Semántico](https://semver.org/lang/es/).

## [Unreleased]

### Added
- **Reportes (fase 6b):** rotación y ABC, antigüedad, por vencer, sin movimiento, exactitud de conteos, productividad por
  operario, kardex valorizado del periodo y comparativo entre sedes, con exportación a CSV y PDF.
- **Abastecimiento (fase 6a):** máximo por artículo y sede, consumo diario, días de cobertura y sugerencia de compra por
  proveedor que se envía al ERP (`purchase.requested`) o se exporta a CSV; lo pedido no se vuelve a sugerir hasta que
  llega.
- **Devoluciones y mermas (fase 5b):** devoluciones de clientes con inspección (a stock, cuarentena o merma) y control
  contra lo despachado (`shipment.returned`), devoluciones a proveedores al costo promedio (`goods.returned`) y mermas con
  foto que se descuentan al aprobarlas el supervisor.
- **Conteos (fase 5a):** conteos de artículos, por ubicaciones, cíclicos por clase ABC, de artículos con diferencias o
  inventario general; ciegos, con recuento automático sobre un umbral y aprobación del supervisor, que ajusta por la
  diferencia con lo esperado al empezar. Ubicaciones congeladas mientras se cuentan y exactitud de inventario.
- **Picking, empaque y tareas (fase 4b2):** empaque en bultos con peso y contenido que salen con el despacho
  (`shipment.dispatched` con `packages`); reabastecimiento de ubicaciones de picking por mínimo y máximo; cola de tareas
  por operario (guardar, preparar, reabastecer, contar) asignada por el supervisor, con pedidos que solo prepara su
  responsable; picking por grupo de pedidos con un solo recorrido.
- **Picking guiado y despacho parcial (fase 4b1):** recorrido por ubicación para preparar cada pedido, confirmando con
  el escaneo de la ubicación y del artículo; si falta stock se despacha lo preparado y el resto queda pendiente y
  reservado (`shipment.dispatched` con `partial`). Los pedidos por aprobar del ERP no reservan ni se preparan hasta
  `order.approved`.
- **Ubicaciones y guardado guiado (fase 4a):** mapa por sede (tipo, zona, pasillo, rack, nivel, posición y artículo
  fijo) con alta por rango; stock por ubicación y traslados internos (`stock.relocated`); pantalla Guardar con la
  ubicación sugerida (la fija, la que ya tiene el artículo o una libre) que se confirma escaneando la etiqueta.
- **Celular (Android):** la app respeta las barras del sistema (estado y gestos) con íconos oscuros; el botón Atrás
  cierra el diálogo o el menú abiertos y vuelve a la página anterior en vez de cerrar la app; las tablas se ven como
  tarjetas y las pestañas que no caben se deslizan; en pantallas táctiles no se abre el teclado solo. El negro de la
  pantalla de inicio, el ícono, la barra de título y el logo deja el café `#1C120D` que se veía amarillento; los montos
  menores a 10 mil del panel se muestran con decimales. La paleta cálida (grises con tinte café) pasa a grises neutros.
- **Fase 3: lotes, vencimientos y series.** Saldo por lote con vencimiento; recepción con lote obligatoria para los
  artículos que controlan lote (con vida útil y días mínimos de vida al recibir). Despachos, transferencias y ventas de
  caja salen por FEFO (lo que vence antes), sin lotes vencidos, en cuarentena ni bloqueados; elegir otro lote requiere
  el permiso nuevo `lot.manage`. Series por unidad al recibir, despachar y transferir. Pantalla **Lotes**: saldos, por
  vencer, cambio de estado, vida útil y trazabilidad de un lote o serie hasta proveedores y clientes. El stock anterior
  a controlar lote queda "sin lote" y sale al final.
- `stock.adjusted` gana `lots` y `serials`; las líneas de `goods.received`, `stock.transferred` y
  `shipment.dispatched`, también (aditivos). Estado de lote y vida útil se comparten por `wms.record.upserted`.
- **Microsoft Store:** paquete MSIX generado por `.github/workflows/store.yml` (etiqueta `vX.Y.Z`), firmado por la Store
  para instalar sin la advertencia de SmartScreen; envío automático opcional. Guía en `docs/MICROSOFT-STORE.md`.
- **El idioma sigue al usuario:** el que elige en Configuración se guarda en el hub y se usa al entrar en cualquier
  sistema o equipo de la suite. Sin conexión queda en este equipo.
- **Fase 2: artículos de almacén.** Presentaciones con factor y código de barras propio (caja × 12, DUN-14), código
  de unidad UN/ECE, peso y control por lote o serie, editables en Artículos y compartidos por el hub. Escanear el
  código de una caja agrega la línea en cajas; recepciones, transferencias, pedidos y conteos se ingresan en
  cualquier presentación y el stock queda en la unidad base. Un código de barras no puede ser de dos artículos.
- **Fase 1: costo y kardex valorizado.** Costo promedio ponderado móvil por artículo y sede: las compras, las
  transferencias recibidas y las anulaciones y devoluciones de ventas entran con su costo, y las salidas salen al
  promedio. El kardex muestra costo unitario, costo total y saldo valorizado, con filtro por fechas; el inventario y
  el panel muestran el valor a costo, el costo de lo vendido y el margen de los últimos 30 días (solo para quien tiene
  permiso de ver costos). Un movimiento atrasado de otro equipo recalcula desde su fecha. El ingreso directo exige
  el costo unitario. Cada movimiento guarda su tipo de operación de la Tabla 12 de SUNAT para el PLE.
- `stock.adjusted` gana `unitCost`, `totalCost` y `saleAmount`; las líneas de `goods.received`, `unitCost` y
  `totalCost` (aditivos).

### Fixed
- El buscador de artículos y los campos de escaneo (Guardar, Preparar) perdían dígitos o tomaban el código anterior con
  lectores rápidos.
- La caja ilustrada del menú seguía en naranja: pasa a los morados de la marca. En el celular el título de la barra
  superior ya no se corta.
- Celular: los diálogos, el editor de líneas y los indicadores ya no se salen de la pantalla.
- Una orden de compra o un pedido con el mismo artículo en dos líneas movía el stock de una sola línea (los
  movimientos ahora se agrupan por artículo).
- **Sedes por usuario:** si el administrador asigna sedes al usuario en el hub, el WMS solo le muestra y le deja operar
  esas sedes (stock, movimientos, pedidos, recepciones, panel); puede transferir hacia cualquier sede. Sin sedes
  asignadas, o para los administradores de la organización, todas.
- **Fase 0: varios equipos sin conexión.** Si dos equipos reciben la misma orden de compra o despachan el mismo pedido
  sin conexión, el stock se mueve una sola vez (Ids deterministas); si registraron datos distintos, queda la versión
  que llegó primero al hub y el caso se ve en Configuración → Conflictos entre equipos. Empezar la preparación,
  cancelar pedidos (también desde el ERP o el CRM) y anular recepciones llega a los demás equipos
  (`order.picking_started`, `order.cancelled`, `receipt.cancelled`), y la ubicación y el mínimo por artículo se
  comparten (`wms.record.upserted`, gana el cambio más reciente). Los eventos aplicados se olvidan a los 180 días.
- **Idiomas**: la interfaz en español, inglés y portugués. Se elige en Configuración → Este equipo; por defecto sale
  del país de la empresa (portugués en Brasil y Portugal, inglés en EE. UU. y el Reino Unido, español en el resto).
  Una prueba verifica que cada texto exista en los tres idiomas con los mismos datos variables.
- Los **mensajes de validación y de permisos** salen en el idioma elegido (`CoreText` en Core,
  con el mismo catálogo que la interfaz).
- **Base común de la suite** (`VortexApi.Suite`): formatos de moneda, fecha y hora según el país y la zona horaria de
  la empresa; las categorías de impuesto de los artículos son las comunes de la suite.
- **Roles del WMS** asignados en el hub (fase 0 de la hoja de ruta): `operario` (por defecto: recibe, prepara, despacha y
  transfiere), `supervisor` (además ajusta stock, cambia ubicaciones y mínimos, crea y cancela pedidos, anula
  recepciones y ve la auditoría), `consulta` y `administrador`. Se validan en Core.
- **Auditoría** de ajustes de stock, recepciones anuladas y pedidos cancelados (pantalla Auditoría).

### Added
- `CONTRIBUTING.md`: entorno de desarrollo, flujo de PR con `main` protegida, convenciones y reglas del inventario.
- `SECURITY.md`: cómo reportar vulnerabilidades en privado y qué cubre.
- `docs/ROADMAP.md`: plan por fases para competir en el mercado (lo fiscal y tributario al final), con las
  propuestas al hub y las decisiones pendientes.

### Changed
- `docs/ROADMAP.md` pasa a ser la guía de requisitos empresariales: cada requisito (`WMS-xx`) con su estado y
  la fase que lo cubre, y cada fase con su criterio de terminado, propuestas al hub y decisiones pendientes.
- **Morado .NET como color de la suite:** el naranja de la marca (`#FF5B14`) pasa a `#512BD4`, el morado de .NET,
  en el acento de `app.css`, el logo, el ícono de la app, la pantalla de inicio, la barra de título y Android. La portada
  del inicio de sesión se muestra en tonos morados con `filter: hue-rotate`. Los colores de estado no cambian.
- Documentación reorganizada en `docs/`: el README queda como presentación y el detalle pasa a
  `docs/DESCRIPTION.md`; `SISTEMAS.md` y `THIRD-PARTY-NOTICES.md` se movieron a `docs/`.
- Barra de título de Windows en la tinta de la marca, con botones blancos y la caja del logo junto al nombre.
- Nuevo inicio de sesión mitad y mitad: portada a la izquierda y, a la derecha, un formulario simple con el logo,
  los controles del panel y la opción de mostrar la contraseña.
- La versión se muestra con tres cifras (`0.1.0`) en Configuración.
- Indicadores en dos líneas, del mismo alto que su ícono: la nota va junto a la cifra y solo se muestra si entra
  completa (si no, se ve al pasar el mouse).

## [0.1.0] - 2026-11-01

### Added
- App **.NET MAUI Blazor Hybrid** (Windows, Android, iOS, macOS) con la interfaz de Vortex WMS: panel, inventario,
  recepciones, pedidos y despachos, kardex, artículos, almacenes, contactos y configuración. Diseño claro a pantalla completa
  (sin marco, con barra de título integrada en Windows), tipografía Montserrat, acento naranja y vista adaptada a móvil.
- `VortexWms.Core`: dominio del almacén sobre SQLite (una base por organización), con existencias por sede,
  reservas, ubicaciones, stock mínimo, kardex inmutable, recepciones, transferencias, pedidos y despachos.
- Integración con el hub `vortex-api` siguiendo `SISTEMAS.md`: login y sesión con modo sin conexión, heartbeat,
  sincronización de datos maestros por versión, cola de salida transaccional, eventos v1 (`stock.adjusted`,
  `goods.received`, `stock.transferred`, `shipment.dispatched`, `order.created`) y consumo de ventas del POS, pedidos
  del ERP/CRM y órdenes de compra. Varios equipos del WMS comparten el stock. Ver `docs/INTEGRACION.md`.
- Pruebas de `VortexWms.Core` (inventario, sincronización, varios equipos) y prueba de integración contra un hub real.
- `scripts/pack-sdk.sh` y `scripts/pack-sdk.ps1` para empaquetar el SDK del hub en `packages/vortex-sdk`.
- CI en GitHub Actions: pruebas en Linux y compilación de la app para Windows.

### Changed
- Licencia: **AGPL-3.0** (antes SSPL).
- Repositorio: `ArcGabito/vortex-wms`.
