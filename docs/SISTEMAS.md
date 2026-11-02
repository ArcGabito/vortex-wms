# Construir un sistema de la suite Vortex sobre el hub

Guía para quien desarrolla **Vortex ERP, CRM, WMS o POS** (personas o agentes de IA): cómo conectar el sistema
al hub `vortex-api`, qué datos le pertenecen al hub y cuáles al sistema, y qué reglas seguir para que los cuatro
funcionen solos, juntos o reemplazándose entre sí.

> Si eres un agente: lee esta guía completa antes de escribir código que toque usuarios, sesiones, clientes,
> artículos, sedes, datos de la empresa o comunicación con otro sistema de la suite. La referencia exacta de cada
> endpoint está en `http://localhost:5299/docs` (o `/openapi/v1.json`) con el hub levantado.

## 1. Levantar el hub en local

Requisitos: .NET SDK 10 y Docker. El hub vive en `~/vortex-api` (repo `ArcGabito/vortex-api`).

```bash
cd ~/vortex-api
docker compose up -d                                   # SQL Server en localhost:1433
dotnet run --project src/VortexApi --launch-profile http   # http://localhost:5299
```

Al arrancar en `Development` aplica las migraciones y crea todo lo necesario para empezar a programar:

| Qué | Valor |
|---|---|
| URL del hub | `http://localhost:5299` (desde un emulador Android: `http://10.0.2.2:5299`) |
| Issuer de los tokens (para validar sin conexión) | `https://hub.vortex.local` |
| Organización demo | **Vortex Demo**, con ERP, CRM, WMS y POS habilitados, sin límites y con acceso automático |
| Usuario OrgAdmin de la demo | `ana@vortex.local` / `Suite-Local-2026!` |
| Usuario normal de la demo | `luis@vortex.local` / `Suite-Local-2026!` |
| PlatformAdmin (opera el hub) | `admin@vortex.local` / `Vortex-Admin-2026!` |
| Claves de sistema | `vxp_erp_dev_key_change_me`, `vxp_crm_dev_key_change_me`, `vxp_wms_dev_key_change_me`, `vxp_pos_dev_key_change_me` |

Cualquiera de los dos usuarios demo puede iniciar sesión en los 4 sistemas sin configurar nada más.
Para probar roles por sistema (p. ej. `cajero` en POS), asígnalo con
`PUT /api/admin/organizations/{orgId}/products/POS/members/{userId}` usando el token de Ana
(`POST /api/admin/auth/login`). `src/VortexApi/VortexApi.http` tiene ejemplos de todas las llamadas.

Para empezar de cero: `docker compose down -v` y volver a arrancar.

## 2. Agregar el SDK al sistema

El SDK es el paquete **`VortexApi.Client`** (net10.0, sirve para MAUI, consola, ASP.NET o WPF). Hay tres formas
de referenciarlo; mientras el hub y el sistema evolucionan juntos, usa la A o la B.

**A. Referencia al proyecto** (los cambios del SDK se ven al instante). Con ambos repos clonados lado a lado
(`~/vortex-api` y `~/vortex-pos`, por ejemplo), en el `.csproj` del sistema:

```xml
<ItemGroup>
  <ProjectReference Include="..\..\vortex-api\src\VortexApi.Client\VortexApi.Client.csproj" />
</ItemGroup>
```

Ajusta la ruta relativa según dónde esté el `.csproj`. En CI del sistema esto exige clonar también `vortex-api`.

**B. Feed NuGet local** (el sistema queda con un `PackageReference` normal, como en producción):

```bash
~/vortex-api/scripts/pack-sdk.sh
# → Paquete VortexApi.Client 0.3.1-dev.20261101120000 en ~/.nuget/vortex-local
```

En el repo del sistema, un `nuget.config` junto a la solución:

```xml
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <packageSources>
    <add key="nuget.org" value="https://api.nuget.org/v3/index.json" />
    <add key="vortex-local" value="%HOME%/.nuget/vortex-local" />
  </packageSources>
</configuration>
```

y `dotnet add package VortexApi.Client --prerelease`. Cada ejecución de `pack-sdk.sh` genera una versión nueva;
para tomarla, vuelve a ejecutar `dotnet add package VortexApi.Client --prerelease`.

**C. Paquete publicado.** Al crear un tag `vX.Y.Z` en `vortex-api`, el workflow `Release` lo sube a GitHub
Packages (`https://nuget.pkg.github.com/ArcGabito/index.json`, requiere un token con `read:packages`) y, si está
configurado el secreto `NUGET_API_KEY`, a nuget.org. Cada build del CI también deja el `.nupkg` como artefacto
`sdk-nupkg`.

Namespaces:

```csharp
using VortexApi.Client;           // VortexApiClient, modelos, VortexApiException, VortexJson
using VortexApi.Client.Sessions;  // VortexSessionManager, ISessionStore, InMemorySessionStore
using VortexApi.Client.Sync;      // VortexSync, IVortexSyncStore, JsonFileSyncStore
```

## 3. Conexión y sesión

Cada sistema se identifica con su código (`ERP`, `CRM`, `WMS`, `POS`) y su clave de sistema. Ambos van en la
configuración del sistema, **nunca** fijos en el código (en producción la clave es otra).

```csharp
// MauiProgram.cs (o Program.cs)
var hubUrl = config["Vortex:HubUrl"];       // http://localhost:5299/ en desarrollo
builder.Services.AddSingleton(_ => new VortexApiClient(
    new HttpClient { BaseAddress = new Uri(hubUrl) },
    new VortexApiClientOptions { ProductCode = "POS", ProductKey = config["Vortex:ProductKey"]! }));

builder.Services.AddSingleton<ISessionStore, SecureStorageSessionStore>();   // ver abajo
builder.Services.AddSingleton(sp => new VortexSessionManager(
    sp.GetRequiredService<VortexApiClient>(), sp.GetRequiredService<ISessionStore>(), DeviceIdentity.Get()));

builder.Services.AddSingleton<IVortexSyncStore>(_ =>
    new JsonFileSyncStore(Path.Combine(FileSystem.AppDataDirectory, "vortex-sync.json")));
builder.Services.AddSingleton<VortexSync>();
```

- **`deviceId`**: un GUID generado la primera vez que se instala la app y guardado para siempre en ese equipo
  (mínimo 16 caracteres). No uses identificadores de hardware.
- **`ISessionStore`**: en MAUI, implementarlo sobre `SecureStorage`, serializando `VortexStoredSession` con
  `VortexJson.Options`. Nunca guardar tokens en `Preferences` ni en archivos. En pruebas, `InMemorySessionStore`.

```csharp
public sealed class SecureStorageSessionStore : ISessionStore
{
    private const string Key = "vortex.session";

    public async Task<VortexStoredSession?> LoadAsync(CancellationToken ct = default) =>
        await SecureStorage.GetAsync(Key) is { } json
            ? JsonSerializer.Deserialize<VortexStoredSession>(json, VortexJson.Options) : null;

    public Task SaveAsync(VortexStoredSession session, CancellationToken ct = default) =>
        SecureStorage.SetAsync(Key, JsonSerializer.Serialize(session, VortexJson.Options));

    public Task ClearAsync(CancellationToken ct = default)
    {
        SecureStorage.Remove(Key);
        return Task.CompletedTask;
    }
}
```

Flujo de la app:

```csharp
// Pantalla de login
var session = await sessions.LoginAsync(email, password, DeviceInfo.Name, DeviceInfo.Platform.ToString());
var org = session.Access.OrganizationName;
var rol = session.Access.Role;                      // rol propio del sistema (p. ej. "cajero"), puede ser null
var conFacturacion = session.Access.HasFeature("facturacion");

// Al abrir la app: ¿hay sesión guardada?
if (await sessions.GetSessionAsync() is null) { /* mostrar login */ }

// Cualquier llamada al hub: el token se renueva solo
var sistemas = await sessions.WithAccessTokenAsync(t => client.GetAvailableProductsAsync(t));

// Al abrir la app y cada pocos minutos
try { await sessions.HeartbeatAsync(); }
catch (HttpRequestException)
{
    var offline = await sessions.ValidateOfflineAsync("https://hub.vortex.local");   // Issuer del hub
    if (!offline.IsValid) { /* periodo sin conexión agotado (7 días por defecto): pedir conexión */ }
}

// Acceso retirado, usuario desactivado, sesión revocada…
sessions.SessionEnded += (_, ex) => MainThread.BeginInvokeOnMainThread(() => Shell.Current.GoToAsync("//login"));

// Cerrar sesión
await sessions.SignOutAsync();
```

`GetAvailableProductsAsync` devuelve los sistemas a los que el usuario puede entrar: úsalo para mostrar un
selector "Abrir Vortex ERP / CRM / …" solo con los que estén habilitados.

**El sistema no tiene tabla de usuarios ni contraseñas propias.** Identidad, contraseñas, bloqueo y roles de
administración viven en el hub. Si el sistema necesita datos propios por usuario (preferencias, caja asignada),
los guarda referenciando el `UserId` del hub.

## 4. Qué datos son del hub y cuáles del sistema

| Dato | Dueño | Cómo lo usa el sistema |
|---|---|---|
| Usuarios, roles, equipos, sistemas habilitados | Hub | Solo a través de la sesión. |
| Clientes y proveedores (`party`) | Hub | Copia local sincronizada; altas y cambios con `VortexSync`. |
| Artículos (`item`) | Hub | Ídem. El **stock no** es del hub. |
| Sedes, tiendas, almacenes (`site`) | Hub | Ídem. |
| Datos de la empresa (`company`) | Hub | Solo lectura (lo edita el OrgAdmin). |
| Ventas, facturas, stock, pedidos, oportunidades, asientos… | **El sistema** | En su propia base. Lo que otros sistemas necesitan saber se publica como **evento**. |

Reglas:

1. **Nunca crear un cliente, artículo o sede solo en la base local.** Se genera el Id en el equipo
   (`Guid.NewGuid()`), se guarda localmente y se encola con `EnqueueUpsertAsync`; se sube solo en la próxima
   sincronización, aunque ahora no haya red.
2. **Referenciar siempre por el Id del hub** (`customerId`, `itemId`, `siteId`), nunca por un Id local
   autoincremental. Así, si la empresa reemplaza el CRM por el ERP, las referencias siguen siendo válidas.
3. **La copia local se actualiza desde el feed** (`OnParties`, `OnItems`, `OnSites`, `OnCompany`), no al
   responder una escritura. Los handlers aplican por Id y versión y deben ser idempotentes.
4. **Validaciones del hub**: RUC de 11 dígitos, DNI de 8; documento, SKU y código de sede únicos por
   organización. Antes de crear un cliente, buscarlo: `GET /api/master/parties?documentNumber=…`.
5. **Datos propios del sistema sobre un dato maestro** (p. ej. "cliente frecuente" en POS, "etapa" en CRM) van en
   una tabla del sistema con la referencia al Id del hub, no como campos nuevos del hub.

Campos que acepta el hub (los demás se ignoran):

```jsonc
// party   PUT /api/master/parties/{id}
{ "name": "Bodega Don Pepe", "isCustomer": true, "isSupplier": false, "kind": "Company",   // Company | Person
  "tradeName": null, "documentType": "RUC", "documentNumber": "20123456789", "email": null, "phone": null,
  "address": { "line": "Av. Perú 123", "district": "Breña", "city": "Lima", "region": null, "postalCode": null, "countryCode": "PE" },
  "notes": null, "isActive": true }

// item    PUT /api/master/items/{id}
{ "sku": "CAFE-250", "name": "Café tostado 250 g", "kind": "Product",   // Product | Service
  "description": null, "unit": "unidad", "barcode": "7750000000017", "category": "Bebidas",
  "price": 18.50, "currencyCode": "PEN", "taxCode": "igv", "trackInventory": true, "isActive": true }

// site    PUT /api/master/sites/{id}
{ "code": "ALM-01", "name": "Almacén central", "kind": "Warehouse",   // Store | Warehouse | Office | Other
  "phone": null, "address": { "city": "Lima", "countryCode": "PE" }, "isActive": true }
```

## 5. Sincronización (`VortexSync`)

```csharp
sync
    .OnItems(async (c, ct) =>
    {
        if (c.Operation == VortexChangeOperation.Deleted) await db.MarkItemDeletedAsync(c.EntityId);
        else await db.UpsertItemAsync(c.Data, c.Version);
    })
    .OnParties((c, ct) => db.UpsertCustomerAsync(c.Data, c.Version))
    .OnSites((c, ct) => db.UpsertSiteAsync(c.Data, c.Version))
    .OnCompany((c, ct) => db.SaveCompanyAsync(c.Data))
    .OnEvent("stock.*", (e, ct) => db.ApplyStockAsync(e.DataAs<StockAdjusted>()))   // eventos de otros sistemas
    .OnConflict((c, ct) => Task.FromResult(VortexConflictResolution.DiscardLocal))  // por defecto gana el hub
    .OnPushFailed((f, ct) => { logger.LogWarning("{Code}: {Error}", f.ErrorCode, f.Error); return Task.CompletedTask; });

_ = sync.RunPeriodicAsync(TimeSpan.FromSeconds(30), onError: ex => logger.LogError(ex, "Sync"), ct: appStopping);
```

- Registrar **todos** los handlers antes de la primera sincronización.
- Sin red, `SyncAsync` devuelve `Completed = false` y conserva cola y cursores: no hace falta lógica de reintento.
- Un handler que lanza excepción detiene el feed en ese cambio; la próxima pasada lo reintenta. No tragarse
  errores dentro del handler.
- Los rechazos definitivos (RUC inválido, SKU duplicado) quedan en `store.GetFailedAsync()`: mostrarlos al
  usuario para que los corrija.
- Los eventos que publicó el propio sistema no se reciben (salvo `IncludeOwnEvents`).
- Para colas grandes, implementar `IVortexSyncStore` sobre la base SQLite de la app, en la misma transacción
  que los datos locales.

## 6. Eventos entre sistemas

Un sistema publica lo que pasó; los demás, **si están instalados**, reaccionan. El que publica no sabe ni debe
saber quién escucha.

```csharp
var ventaId = Guid.NewGuid();
await sync.EnqueueEventAsync("sale.completed", new SaleCompleted(...), subjectType: "sale", subjectId: "B001-000123", id: ventaId);
```

Catálogo actual (`GET /api/events/catalog`):

| Evento | Publica | Consumen |
|---|---|---|
| `sale.completed`, `sale.voided`, `sale.returned` | POS | ERP, WMS (y CRM `sale.completed`) |
| `quote.accepted`, `deal.won` | CRM | ERP |
| `order.created` | ERP, CRM | WMS |
| `invoice.issued` | ERP | CRM, POS |
| `payment.received` | ERP, POS | CRM, ERP |
| `purchase_order.created` | ERP | WMS |
| `goods.received` | WMS | ERP |
| `stock.adjusted`, `stock.transferred` | WMS | ERP, POS |
| `shipment.dispatched` | WMS | ERP, CRM |
| `master.{party,item,site,company}.*` | Hub | Todos (ya llegan por `OnParties`/`OnItems`/…) |

### Contenido de los eventos (versión 1)

El hub solo exige que `data` sea un objeto JSON de hasta 64 KB; **el formato lo acuerdan los sistemas**. Para que
un ERP y un POS desarrollados por separado se entiendan, usar estos campos (camelCase, montos como número decimal,
fechas ISO 8601, Ids del hub). Se pueden agregar campos; no quitar ni cambiar el significado de los existentes.

```jsonc
// sale.completed       subjectType "sale", subjectId = número de comprobante
{ "saleId": "guid", "number": "B001-000123", "siteId": "guid", "customerId": "guid | null",
  "currencyCode": "PEN", "subtotal": 50.76, "tax": 9.14, "total": 59.90,
  "lines": [ { "itemId": "guid", "sku": "CAFE-250", "quantity": 2, "unitPrice": 29.95, "discount": 0, "total": 59.90 } ],
  "payments": [ { "method": "cash", "amount": 59.90 } ] }        // cash | card | transfer | wallet | credit

// sale.voided          { "saleId": "guid", "number": "B001-000123", "reason": "..." }
// sale.returned        { "saleId": "guid", "returnId": "guid", "siteId": "guid", "lines": [ { "itemId", "quantity", "total" } ], "total": 0 }

// order.created        { "orderId": "guid", "number": "PED-0001", "customerId": "guid", "siteId": "guid | null",
//                        "lines": [ { "itemId", "quantity", "unitPrice" } ], "total": 0, "currencyCode": "PEN" }
// quote.accepted       { "quoteId": "guid", "customerId": "guid", "lines": [ ... ], "total": 0, "currencyCode": "PEN" }
// deal.won             { "dealId": "guid", "customerId": "guid", "amount": 0, "currencyCode": "PEN", "quoteId": "guid | null" }

// invoice.issued       { "invoiceId": "guid", "number": "F001-000045", "customerId": "guid", "saleId": "guid | null",
//                        "orderId": "guid | null", "issuedAt": "...", "total": 0, "currencyCode": "PEN" }
// payment.received     { "paymentId": "guid", "customerId": "guid | null", "invoiceId": "guid | null",
//                        "saleId": "guid | null", "amount": 0, "currencyCode": "PEN", "method": "transfer" }

// purchase_order.created { "purchaseOrderId": "guid", "supplierId": "guid", "siteId": "guid", "lines": [ { "itemId", "quantity", "unitCost" } ] }
// goods.received       { "receiptId": "guid", "purchaseOrderId": "guid | null", "siteId": "guid", "lines": [ { "itemId", "quantity" } ] }
// stock.adjusted       { "siteId": "guid", "itemId": "guid", "quantityDelta": -3, "quantityOnHand": 42, "reason": "count | damage | sale | ..." }
// stock.transferred    { "transferId": "guid", "fromSiteId": "guid", "toSiteId": "guid", "lines": [ { "itemId", "quantity" } ] }
// shipment.dispatched  { "shipmentId": "guid", "orderId": "guid", "customerId": "guid", "carrier": "...", "trackingNumber": "..." }
```

Cambios incompatibles en un evento → publicarlo con otro `schemaVersion` (campo de `POST /api/events`; el SDK
publica por ahora siempre la versión 1) y publicar ambas versiones un tiempo. Para un evento nuevo, agregarlo al catálogo del hub (`EventCatalog` en `src/VortexApi/Domain/Integration.cs`)
y a esta tabla; tipos con formato `categoria.accion`, en minúsculas. Las categorías `master.*` y `hub.*` están
reservadas al hub.

## 7. Reglas para que los sistemas se puedan usar solos, juntos o reemplazarse

1. **Funcionar sin los demás.** Ningún sistema puede requerir que otro esté instalado. Si el POS no recibe
   `invoice.issued` porque no hay ERP, igual vende.
2. **Ignorar lo desconocido.** Eventos sin handler, campos nuevos en `data` y versiones de esquema futuras no
   deben romper nada.
3. **Ids generados en el equipo** para todo lo que se crea (datos maestros y eventos). Reintentar nunca duplica.
4. **Sin llamadas directas entre sistemas.** Toda comunicación pasa por el hub (datos maestros o eventos).
5. **Si dos sistemas pueden hacer lo mismo** (pedido desde CRM o desde ERP), ambos publican el mismo evento con el
   mismo formato. Así, reemplazar uno por otro no cambia nada para los consumidores.
6. **Funciones según `features`.** Los módulos opcionales (`facturacion`, `nomina`…) se activan según
   `session.Access.HasFeature(...)`, no con configuración propia.
7. **Modo sin conexión.** Todo lo que el usuario hace se guarda primero localmente y se sube con `VortexSync`.

## 8. Errores

Todas las respuestas de error son `ProblemDetails` con un campo `code` estable; el SDK lanza
`VortexApiException` con `StatusCode` y `Code`. `ex.IsAccessDenied` indica que el usuario ya no puede usar el
sistema (no se arregla reintentando).

| `code` | Qué hacer |
|---|---|
| `invalid_credentials`, `account_locked` | Mensaje en el login. |
| `invalid_product` | Clave de sistema o `ProductCode` mal configurados. |
| `product_not_enabled`, `product_disabled`, `access_expired`, `no_access`, `organization_disabled`, `account_disabled` | La organización o el usuario no tienen acceso a este sistema: ir al login con el mensaje. |
| `user_limit_reached`, `device_limit_reached`, `device_revoked`, `device_mismatch` | Límite de la organización o equipo dado de baja: avisar al usuario que contacte a su administrador. |
| `refresh_token_reused`, `invalid_refresh_token` | Sesión cerrada por seguridad: login de nuevo (el SDK dispara `SessionEnded`). |
| `version_conflict` (412) | Otro sistema cambió el registro antes; `VortexSync` lo resuelve con `OnConflict`. |
| `duplicate_document`, `duplicate_sku`, `duplicate_code`, `invalid_document`, `validation_error` | Dato inválido: mostrar al usuario para corregir. |
| `invalid_event_type`, `invalid_event_data` | Error de programación en el evento. |

## 9. Probar el sistema contra el hub

- **Pruebas manuales / de extremo a extremo**: hub en local con la organización demo (sección 1).
- **Pruebas automáticas del sistema**: mockear `VortexApiClient` es frágil; mejor aislar el uso del SDK detrás de
  una interfaz propia del sistema (p. ej. `IHubGateway`) y probar la lógica con un falso. Para probar los handlers
  de sincronización, llamar directamente a las funciones que registras en `OnItems`/`OnEvent`.
- **Ver qué llegó al hub**: `GET /api/master/changes?since=0` y `GET /api/events?since=0` con un token del sistema,
  o la referencia interactiva en `/docs`.

## 10. Si el sistema necesita algo que el hub no tiene

No implementarlo como un parche en el sistema (tablas de usuarios propias, sincronización directa con otro
sistema, campos maestros duplicados). Abrir un issue en `ArcGabito/vortex-api` describiendo la necesidad, o
proponer el cambio en el hub: nuevo evento en el catálogo, campo en un dato maestro o endpoint.

## Anexo: `CLAUDE.md` para el repo de un sistema

Para que un agente que trabaja en el repo de un sistema use el hub correctamente, agregar al `CLAUDE.md` de ese
repo (ajustando el código del sistema):

```markdown
## Hub Vortex (identidad, datos maestros e integración)

Este sistema es **Vortex POS** (`ProductCode = "POS"`) y depende del hub `vortex-api`, clonado en `~/vortex-api`.
Antes de tocar login, sesiones, usuarios, clientes, artículos, sedes, datos de la empresa o comunicación con otro
sistema de la suite, lee `~/vortex-api/docs/SISTEMAS.md` y síguelo.

- Levantar el hub: `cd ~/vortex-api && docker compose up -d && dotnet run --project src/VortexApi --launch-profile http`
  (http://localhost:5299, referencia en /docs). Usuario demo: `luis@vortex.local` / `Suite-Local-2026!`,
  clave de sistema `vxp_pos_dev_key_change_me`.
- SDK: paquete `VortexApi.Client` (ver sección 2 de la guía). Si cambias el SDK, hazlo en `~/vortex-api` con sus pruebas.
- No crear tablas de usuarios ni contraseñas; no llamar directamente a otros sistemas; usar Ids del hub.
- Si falta algo en el hub, proponer el cambio en `~/vortex-api` en vez de un parche local.
```
