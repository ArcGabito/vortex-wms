# CLAUDE.md

Vortex WMS: sistema de almacenes de la suite Vortex (ERP, CRM, WMS, POS). App .NET MAUI Blazor Hybrid, AGPL-3.0,
textos de la interfaz y documentación en español (es-PE).

## Comandos

```bash
dotnet test tests/VortexWms.Core.Tests                                   # pruebas del dominio y la sincronización
VORTEX_HUB_URL=http://localhost:5299/ dotnet test tests/VortexWms.Core.Tests   # + prueba contra un hub real
dotnet build src/VortexWms -f net10.0-windows10.0.19041.0                # app en Windows (también -f net10.0-android)
scripts/pack-sdk.sh   |   ./scripts/pack-sdk.ps1                         # SDK del hub -> packages/vortex-sdk
```

## Estructura

- `src/VortexWms.Core`: todo lo que no es interfaz. `Data/` (SQLite con Microsoft.Data.Sqlite sin ORM, migraciones
  por `PRAGMA user_version` en `WmsDatabase`), `Services/` (Inventory, Receipt, Order, MasterData; `StockLedger` es el
  único que mueve stock), `Sync/` (store de la sincronización y handlers de eventos), `Integration/` (eventos v1).
- `src/VortexWms`: app. `Services/AppSession.cs` (sesión, heartbeat, bucle de sincronización), `Components/Pages`
  (pantallas; heredan `WmsPage`), `Components/Shared` (componentes), `wwwroot/css/app.css` (sistema de diseño).
- `tests/VortexWms.Core.Tests`: xUnit sobre bases SQLite temporales; `MultiDeviceTests` simula varios equipos.

## Reglas

- Todo cambio de stock pasa por `StockLedger.ApplyAsync` dentro de la transacción del documento: escribe el
  movimiento y encola `stock.adjusted` con el Id del movimiento. Lo que se encola va en la misma transacción.
- Movimientos derivados de documentos de otro sistema usan `DeterministicId` para que varios equipos coincidan.
- Los handlers de eventos son idempotentes (`processed_events`) y nunca lanzan por datos inválidos: los registran
  como ignorados.
- Errores para el usuario: `WmsValidationException` con el mensaje final en español.
- En Razor, dentro de atributos usa `@(...)` cuando haya comillas (`"..."`) y no nombres variables `code` ni `page`.
- UI: reutiliza las clases de `app.css` (`filters`, `table.data`, `two-line`, `pill`, `modal`…) y los componentes
  compartidos (`Modal`, `KebabMenu`, `StatTile`, `StatusPill`, `Pager`, `LineEditor`, `ItemPicker`).

## Hub Vortex (identidad, datos maestros e integración)

Este sistema es **Vortex WMS** (`ProductCode = "WMS"`) y depende del hub `vortex-api`, clonado en `~/vortex-api`
dentro de WSL (`wsl -d archlinux`). Antes de tocar login, sesiones, usuarios, clientes, artículos, sedes, datos de la
empresa o comunicación con otro sistema de la suite, lee `~/vortex-api/docs/SISTEMAS.md` y síguelo
(`docs/INTEGRACION.md` resume cómo lo aplica el WMS).

- Levantar el hub: `cd ~/vortex-api && docker compose up -d && dotnet run --project src/VortexApi --launch-profile http`
  (http://localhost:5299, referencia en /docs). Usuario demo: `luis@vortex.local` / `Suite-Local-2026!`,
  clave de sistema `vxp_wms_dev_key_change_me`.
- SDK: paquete `VortexApi.Client` desde el feed local `packages/vortex-sdk` (`scripts/pack-sdk.*`). Si cambias el
  SDK, hazlo en `~/vortex-api` con sus pruebas y vuelve a empaquetar.
- No crear tablas de usuarios ni contraseñas; no llamar directamente a otros sistemas; usar Ids del hub.
- Si falta algo en el hub, proponer el cambio en `~/vortex-api` en vez de un parche local.
