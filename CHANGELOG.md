# Changelog

Todos los cambios relevantes de este proyecto se documentan en este archivo.

El formato se basa en [Keep a Changelog](https://keepachangelog.com/es-ES/1.1.0/)
y el proyecto sigue [Versionado Semántico](https://semver.org/lang/es/).

## [Unreleased]

### Added
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
