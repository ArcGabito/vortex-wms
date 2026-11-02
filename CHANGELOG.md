# Changelog

Todos los cambios relevantes de este proyecto se documentan en este archivo.

El formato se basa en [Keep a Changelog](https://keepachangelog.com/es-ES/1.1.0/)
y el proyecto sigue [Versionado Semántico](https://semver.org/lang/es/).

## [Unreleased]

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
