## Qué hace

- **Inventario por sede**: existencias, reservado, disponible, ubicación (pasillo-estante-nivel), stock mínimo,
  alertas de stock bajo y agotado, valorización y exportación a CSV.
- **Recepciones**: las órdenes de compra del ERP llegan como recepciones *por recibir*; también entradas directas con
  costo unitario. Se registran diferencias entre lo esperado y lo recibido.
- **Pedidos y despachos**: los pedidos del ERP y del CRM reservan stock; se preparan y se despachan con transportista
  y número de guía. También se pueden crear pedidos en el WMS.
- **Ajustes y transferencias**: conteo físico, mermas, pérdidas, sobrantes y transferencias entre almacenes y tiendas.
- **Kardex**: cada entrada y salida queda registrada con su saldo, usuario y documento; no se edita ni se borra.
- **Ventas del POS**: descuentan stock en la tienda; las anulaciones y devoluciones lo reponen.
- **Sin conexión**: todo se guarda en SQLite en el equipo y se sube al hub al reconectar. Varios equipos de la misma
  empresa comparten el stock.
- **Lector de código de barras**: escanear y Enter agrega el artículo en recepciones, pedidos y transferencias.

Cómo se conecta con el hub y con los demás sistemas: [INTEGRACION.md](INTEGRACION.md).

## Estructura

```
src/VortexWms.Core/     Dominio, base SQLite, sincronización y eventos (sin dependencias de MAUI)
src/VortexWms/          App MAUI Blazor Hybrid: Components/ (pantallas Razor) y wwwroot/ (estilos)
tests/VortexWms.Core.Tests/
scripts/pack-sdk.*      Empaquetan el SDK del hub en packages/vortex-sdk
```

## Requisitos

- [.NET SDK 10](https://dotnet.microsoft.com/download) con el workload de MAUI: `dotnet workload install maui`
- El hub `vortex-api` en local o en la infraestructura de tu empresa (Azure, Docker o servidor propio).

## Empezar

1. **Levantar el hub** (en Linux, macOS o WSL):

   ```bash
   git clone https://github.com/ArcGabito/vortex-api ~/vortex-api
   cd ~/vortex-api && docker compose up -d && dotnet run --project src/VortexApi --launch-profile http
   ```

   Queda en `http://localhost:5299` con la organización demo **Vortex Demo**.

2. **Empaquetar el SDK** del hub en el feed local del repo (`packages/vortex-sdk`):

   ```bash
   scripts/pack-sdk.sh                       # hub en ~/vortex-api
   ```

   ```powershell
   ./scripts/pack-sdk.ps1                    # Windows con el hub dentro de WSL (distro archlinux por defecto)
   ./scripts/pack-sdk.ps1 -Distro Ubuntu
   ```

3. **Ejecutar la app**:

   ```bash
   dotnet build src/VortexWms -t:Run -f net10.0-windows10.0.19041.0   # Windows
   dotnet build src/VortexWms -t:Run -f net10.0-android               # Emulador Android (usa http://10.0.2.2:5299)
   dotnet build src/VortexWms -t:Run -f net10.0-maccatalyst           # macOS
   ```

4. **Ingresar** con el usuario demo `luis@vortex.local` / `Suite-Local-2026!` (en compilaciones Debug el login tiene
   el atajo *Usar cuenta demo*).

La dirección del hub y la clave de sistema del WMS están en [`src/VortexWms/appsettings.json`](../src/VortexWms/appsettings.json)
y se pueden cambiar desde el login (**Servidor**). La clave `vxp_wms_dev_key_change_me` es solo para desarrollo.

## Pruebas

```bash
dotnet test tests/VortexWms.Core.Tests
VORTEX_HUB_URL=http://localhost:5299/ dotnet test tests/VortexWms.Core.Tests   # incluye la prueba contra un hub real
```

## Contribuir

Lee [CONTRIBUTING.md](../CONTRIBUTING.md): entorno de desarrollo, flujo de PR, convenciones y reglas del inventario.

## Licencia

[AGPL-3.0-only](../LICENSE.md). Los componentes de terceros y sus licencias están en
[THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).
