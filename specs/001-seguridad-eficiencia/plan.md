# Plan de Implementación: 001 - Auditoría y Remediación de Seguridad de Datos y Eficiencia de Ejecución

**Spec de referencia:** `specs/001-seguridad-eficiencia/spec.md`

## 1. Arquitectura y Componentes Afectados

- **Modelos (`src/CarreraApp/Models/`)**:
  - `AppState.cs`: Incorporar método coordinado `CambiarCarreraActiva(Carrera? carrera)` con evento `CarreraActivaCambiada`, limpieza de `CategoriaPizarraActiva`, vaciado de `FotosEnEsperaGlobal` y disparo de limpieza en la cola en memoria del servidor; acotar `RegistrosExitososGlobal` (p. ej. máximo 200 entradas recientes) para evitar crecimiento indefinido en memoria.
  - `FotoPendiente` (en `AppState.cs`): Agregar propiedad `Miniatura` (`BitmapSource?`) pre-decodificada a escala reducida y congelada (`Freeze()`) para evitar bindings XAML directos a rutas en disco sin escalar.
  - `HitTimerItem.cs`: Mantener actualización de propiedades sin asignaciones innecesarias de cadenas o pinceles por cada tick.

- **Helpers (`src/CarreraApp/Helpers/`)**:
  - `StorageHelper.cs`:
    - Soporte thread-safe para rutas configurables (`ConfigurarRutasParaPruebas(string dbPath, string storageRoot)` y `RestablecerRutasPorDefecto()`), además de `ObtenerCarpetaRespaldos()`.
    - Sanitización segura de rutas (`SanitizarSegmentoRuta`, `ConstruirRutaFotoDefinitivaSegura`): neutralización de `.`, `..`, separadores y caracteres inválidos, acotado de longitud de segmento y ruta completa (`< 240` caracteres) y resolución de colisiones de nombres de archivo.
    - `LimpiarFotosTempHuerfanas(IEnumerable<string>? archivosActivos = null)`: eliminación de archivos huérfanos en `Fotos_Temp` con extensión `.jpg`, `.jpeg`, `.png` o `.webp`, preservando `.gitkeep` y sin tocar `Fotos_Definitivas`.
  - `ImageHelper.cs`:
    - Validación estricta de imágenes (`ValidarImagenAsync` / `ValidarImagen`): verificación de tamaño (`1` byte a `10 MB`), firma binaria (*magic bytes*) para **JPEG**, **PNG** y **WebP**, y decodificabilidad real mediante `BitmapDecoder` sin depender de infraestructura de ventana WPF (`Application.Current`).
    - Carga segura con parámetro opcional `int? decodePixelWidth = null`, `BitmapCacheOption.OnLoad`, `Freeze()` y versión asíncrona (`CargarSinBloqueoAsync`) para decodificar fuera del hilo UI.
  - `CsvExportHelper.cs` (nuevo):
    - Generación y escritura asíncrona de CSV con `sep=;` y UTF-8 BOM.
    - Sanitización `SanitizarCeldaCsv(string valor, bool esValorSistema = false)`: antepone `'` a campos editables por el usuario que inicien (directamente o tras espacios iniciales) con `=`, `+`, `-`, `@`, `\t` o `\r`, preservando intactos valores formateados del sistema como `--:--:--`.
  - `DatabaseMaintenanceHelper.cs` (nuevo):
    - `RespaldarBaseDatosConsistentementeAsync`: respaldo consistente compatible con WAL usando `SqliteConnection.BackupDatabase`, rotación automática conservando máximo **10 respaldos** en `Storage/Backups/`, y retorno estructurado de error sin bloquear el arranque si falla.
    - `InicializarYMigrarBaseDatosAsync`: `EnsureCreatedAsync()`, inspección previa vía `PRAGMA table_info` para migraciones idempotentes (`Hits.Hora_Fin`, `Carreras.RutaLogoIzquierdo`, `Carreras.RutaLogoDerecho`), creación de índices faltantes (`IX_Corredores_ID_Hit`, `IX_Corredores_ID_Categoria`, `IX_Hits_ID_Carrera`, `IX_Categorias_ID_Carrera`) y habilitación de `PRAGMA journal_mode=WAL`.

- **Persistencia (`src/CarreraApp/Data/`)**:
  - `ApplicationDbContext.cs`:
    - Constructor sobrecargado con `DbContextOptions<ApplicationDbContext>` para pruebas y uso de `StorageHelper.ObtenerRutaBaseDatos()` por defecto; eliminar `SqliteCacheMode.Shared` y configurar `DefaultTimeout = 5`.
    - Interceptor/apertura de conexión (`DbConnectionInterceptor`) que aplica explícitamente `PRAGMA foreign_keys=ON; PRAGMA busy_timeout=5000;` (y `PRAGMA journal_mode=WAL;` cuando corresponda a BD en archivo).
    - Definición de índices explícitos en `OnModelCreating` para `Corredor.ID_Hit`, `Corredor.ID_Categoria`, `Hit.ID_Carrera` y `Categoria.ID_Carrera`, además de llaves foráneas de `Categoria -> Carrera` y `Hit -> Carrera`.
  - `RaceDataService.cs`:
    - Desacoplamiento total de UI WPF (`Application.Current`, `Dispatcher`, `MessageBox`) y soporte opcional de fábrica de contexto (`Func<ApplicationDbContext>?`).
    - `AsignarTiempoAsync`: validación de `dorsal > 0`, hit iniciado y no finalizado, tiempo neto no negativo (`horaLlegada >= Hora_Inicio`), unicidad de tiempo por corredor, ruta definitiva segura, transacción explícita (`BeginTransactionAsync`), evaluación de cierre automático de hit (`Activo` sin tiempo, `Hora_Fin >= max(Hora_Llegada)`), copia segura a destino definitivo antes de `CommitAsync()`, borrado de temporal después de `CommitAsync()`, rollback completo (BD + archivo definitivo parcial) en caso de fallo preservando el temporal, y retorno con código semántico (`200`, `400`, `404`, `409`, `422`).
    - Métodos centralizados de catálogos y negocio: CRUD validado de `Carrera`, `Categoria`, `Hit` y `Corredor`; `CambiarEstadoCorredorAsync` (`Activo`/`DNS`/`DNF` permitido solo si no tiene tiempo registrado, prohibiendo cambio de hit o estado tras registrar tiempo, y re-evaluando cierre automático de hit al pasar a `DNS`/`DNF`); y bloqueo de eliminación de carreras, categorías, hits o corredores vinculados a tiempos registrados.

- **Servidor Web y Frontend (`src/CarreraApp/Server/` y `src/CarreraApp/wwwroot/`)**:
  - `WebServer.cs`:
    - Desacoplado de `Application.Current` y `MessageBox`, reportando fallos críticos por evento `ErrorCriticoServidor` y delegando la lógica de carrera activa y asignación a funciones configurables.
    - Parametrización con `WebServerOptions` (puertos HTTP `5000` y HTTPS `5001` por defecto; puertos efímeros/configurables y HTTP-only opcional para pruebas aisladas; exposición de puertos activos vinculados para lectura en pruebas).
    - Generación única por arranque de token CSPRNG URL-safe de 16 caracteres (`12` bytes en Base64Url) y validación en tiempo constante (`FixedTimeEquals`) para todas las rutas `/api/*` vía cabecera `X-Auth-Token` o query `?token=`, devolviendo HTTP `401 Unauthorized` si falta o es inválido mientras `wwwroot/` carga sin token.
    - Validación estricta en `/api/upload-photo` (<= 10 MB, no vacío, `tempId` válido, validación binaria + decodificabilidad JPEG/PNG/WebP en memoria antes de escribir en disco, retornando `400`, `413` o `415` JSON descriptivo).
    - Validación estricta de `IdFoto` contra *path traversal* (`400`) y máquina de estados atómica por `IdFoto` (`Pendiente` / `EnProceso`) compartida entre peticiones móviles y acciones locales de PC (`IntentarAdquirirFotoParaLocal`, `LiberarFotoLocal`, `LimpiarColaPendientes`).
    - `/api/assign-dorsal` asíncrono que espera validación y persistencia real antes de responder (`200`, `400`, `404`, `409`, `422`), rechazando explícitamente cuando no hay carrera activa en mesa.
  - `wwwroot/index.html` y `wwwroot/app.js`:
    - Extracción y envío de token en `X-Auth-Token`, banner/control visual ante `401` o falta de token para reescanear QR o ingresar credencial manualmente.
    - Generador de `tempId` único (`crypto.randomUUID()` + respaldo secuencial) y canvas independiente/cola secuencial en captura para evitar colisiones o corrupción de frame en ráfagas.
    - Bloqueo de controles anti doble clic durante envíos, manejo visual de mensajes reales de error (`400`/`404`/`409`/`422`) sin marcar verde ante fallo, y reemplazo de `setInterval` por sondeo secuencial encadenado (`setTimeout` tras completar `fetch`) evitando peticiones solapadas.

- **Vistas WPF (`src/CarreraApp/`)**:
  - `App.xaml` y `App.xaml.cs`: Arranque ordenado y no bloqueante (`OnStartup` sin `StartupUri` bloqueante, `Task` de inicialización que respalda BD -> migra/configura SQLite -> limpia `Fotos_Temp`, e inicio de `WebServer` con `CancellationTokenSource` cancelado en `OnExit`).
  - `MainWindow.xaml.cs`: Conexión de eventos/delegados de `WebServer` hacia `RaceDataService` usando `Dispatcher.InvokeAsync`, carga asíncrona de miniatura escalada (`decodePixelWidth: 160`), y gestión centralizada de ciclo de vida de `LeaderboardWindow` para evitar ventanas huérfanas al navegar.
  - `WelcomeView.xaml(.cs)`: Espera de inicialización de BD, generación asíncrona de QR usando el token único de sesión (`https://<IP>:5001/index.html?token=...`), visualización de URL completa con ajuste de línea y token manual de respaldo, validación de creación de carrera y uso de `AppState.CambiarCarreraActiva` al entrar a una carrera.
  - `TimingView.xaml(.cs)`: Eliminación de `GenerarDatosDePruebaBD()`, binding de miniaturas a `FotoPendiente.Miniatura`, decodificación asíncrona escalada (`decodePixelWidth: 1000`) en visor principal, exclusividad compartida con `WebServer` en asignar/descartar, consolidación a **1 sola recarga** por evento en cronometraje y pizarra (sin llamadas duplicadas), pinceles congelados y limpieza de suscripciones/timer/ventana pizarra en `Unloaded`.
  - `LeaderboardWindow.xaml.cs`: Control de versión y cancelación para descartar resultados obsoletos o fuera de orden en ráfagas, y caché en memoria de logotipos por ruta para evitar lecturas repetidas desde disco.
  - `CorredoresView.xaml(.cs)` y `HitsView.xaml.cs`: Selector de estado (`Activo`/`DNS`/`DNF`) bloqueado cuando el corredor ya tiene tiempo, bloqueo de cambio de hit con tiempo, validación de catálogos y protección contra borrados vía `RaceDataService`, y exportación CSV asíncrona vía `CsvExportHelper`.

- **Pruebas (`tests/CarreraApp.Tests/`)**:
  - Fixture aislado (`TestEnvironmentFixture`), suites unitarias (`DatabaseConfigurationTests`, `StorageAndBackupTests`, `CsvExportHelperTests`, `ImageHelperAndValidatorTests`), suites de servicio/concurrencia (`RaceDataServiceTimingTests`, `RaceDataServiceCatalogTests`), integración HTTP real (`WebServerIntegrationTests`, `FrontendContractTests`) y pruebas de carga/eficiencia (`PerformanceAndConcurrencyTests`, `AppLifecycleAndEfficiencyTests`).

---

## 2. Decisiones Técnicas y Alternativas Descartadas

- **Decisión 1 — Rutas configurables en `StorageHelper` y fábrica de `ApplicationDbContext` para aislamiento total en tests**:
  - *Elegida*: `StorageHelper` expone configuración explícita por sesión de prueba (`ConfigurarRutasParaPruebas`) y `ApplicationDbContext` acepta `DbContextOptions` y `Func<ApplicationDbContext>` en `RaceDataService`. Cada test usa un directorio temporal único eliminado al finalizar.
  - *Alternativa descartada*: Usar proveedor `EF Core InMemory`. Se descartó porque no ejecuta `PRAGMA journal_mode=WAL`, `PRAGMA foreign_keys=ON`, `busy_timeout`, índices únicos SQLite ni `SqliteConnection.BackupDatabase`, impidiendo reproducir fallos reales de contención y migración.
- **Decisión 2 — Desacoplamiento total de `WebServer` y `RaceDataService` respecto a `Application.Current` y `MessageBox`**:
  - *Elegida*: Eliminar cualquier referencia a `System.Windows.Application.Current`, `Dispatcher` o `MessageBox` en `Server/` y `Data/`. Los errores y notificaciones se propagan mediante resultados tipados (`AsignacionResultado`, códigos HTTP) y eventos (`ErrorCriticoServidor`), delegando la presentación exclusivamente a la capa WPF.
  - *Alternativa descartada*: Proteger llamadas WPF con `if (Application.Current != null)`. Se descartó porque perpetúa el acoplamiento arquitectónico, impide probar flujos reales de error en consola/CI y bloquea hilos secundarios innecesariamente.
- **Decisión 3 — Puertos configurables y efímeros en `WebServer` para pruebas de integración**:
  - *Elegida*: Introducir `WebServerOptions` permitiendo puertos por defecto (`5000` HTTP y `5001` HTTPS) en producción y asignación efímera (`puerto 0` con lectura de puertos reales asignados) + modo HTTP-only opcional en tests paralelos.
  - *Alternativa descartada*: Hardcodear `5000`/`5001` en `WebServer`. Se descartó porque generaría conflictos de puerto (`SocketException`) entre suites concurrentes o si la aplicación está abierta en la máquina.
- **Decisión 4 — Re-evaluación automática de cierre de Hit cuando un corredor pasa a `DNS` o `DNF`**:
  - *Elegida*: Evaluar el cierre del hit tanto al registrar un tiempo en `AsignarTiempoAsync` como al cambiar el estado de un corredor sin tiempo a `DNS` o `DNF` cuando su hit ya está iniciado y sin finalizar. Si todos los corredores `Activo` restantes ya registraron tiempo (con al menos 1 `Activo`), se fija `Hora_Fin = max(Hora_Llegada)` de los tiempos del hit.
  - *Alternativa descartada*: Evaluar el cierre del hit solo durante la llegada de meta. Se descartó porque marcar como `DNS`/`DNF` al último corredor faltante de un hit dejaría el hit bloqueado indefinidamente como `EN CURSO`.
- **Decisión 5 — Identificador único y canvas/cola segura en `wwwroot/app.js` ante ráfagas**:
  - *Elegida*: Sustituir `Date.now()` por `crypto.randomUUID()` (con respaldo incremental `timestamp-seq`) y capturar cada frame sobre un canvas temporal dedicado por clic (o cola serializada) antes de llamar a `toBlob()` asíncrono, evitando sobreescrituras de imagen y colisiones de ID en ráfagas rápidas.
  - *Alternativa descartada*: Reutilizar un `<canvas>` global mutado directamente por cada pulsación y usar `Date.now()`. Se descartó porque en clics consecutivos rápidos dos capturas dentro del mismo milisegundo colisionan en `item-${tempId}` o mezclan frames antes de que `toBlob` concluya.
- **Decisión 6 — Validación de imágenes en memoria y exclusividad compartida `IdFoto` entre móvil y PC**:
  - *Elegida*: Leer el multipart en un buffer acotado (<= 10 MB), validar *magic bytes* y decodificabilidad con `BitmapDecoder` antes de escribir en `Fotos_Temp`, y usar un estado atómico compartido (`Pendiente` -> `EnProceso`) para que ni dos móviles ni móvil+PC procesen la misma `IdFoto` a la vez.
  - *Alternativa descartada*: Guardar primero el archivo subido y validar después o confiar solo en la extensión/header MIME. Se descartó porque permitiría escribir payloads arbitrarios en disco y dejaría huérfanos en fallos de validación.
- **Decisión 7 — Respaldo en caliente `SqliteConnection.BackupDatabase` y orden transaccional en archivos**:
  - *Elegida*: Usar la API nativa de respaldo SQLite compatible con `WAL` antes de inicializar/migrar (rotando a máximo 10 archivos), y en `AsignarTiempoAsync` copiar a ruta definitiva dentro de la transacción y eliminar el temporal solo después de `CommitAsync()`, borrando la copia definitiva parcial en `RollbackAsync()`.
  - *Alternativa descartada*: `File.Copy` directo sobre `carrera.db` o borrar el temporal antes de `SaveChangesAsync()`. Se descartó porque `File.Copy` puede omitir transacciones pendientes en `-wal` y borrar antes del `Commit` provoca pérdida irreversible de evidencia ante fallos SQLite.

---

## 3. Estrategia de Pruebas

- **Pruebas unitarias y de integración automatizadas en `tests/CarreraApp.Tests/` (`dotnet test`)**:
  1. **Aislamiento y Persistencia SQLite**: `DatabaseConfigurationTests` valida rutas aisladas sin tocar producción, `WAL`, `foreign_keys=ON`, `busy_timeout=5000` y migración idempotente sobre una base heredada sin `Hora_Fin`, logos ni índices.
  2. **Almacenamiento, Rutas, Limpieza y Respaldos**: `StorageAndBackupTests` valida sanitización contra `..`/caracteres inválidos/colisiones/longitud, limpieza de `.jpg`, `.jpeg`, `.png` y `.webp` huérfanos preservando `.gitkeep` y `Fotos_Definitivas`, copia consistente de respaldo bajo WAL, retención máxima de 10 respaldos y arranque resiliente ante error de respaldo.
  3. **Exportación CSV**: `CsvExportHelperTests` valida neutralización con `'` para fórmulas (`=`, `+`, `-`, `@`, `\t`, `\r` incluso tras espacios iniciales) y preservación intacta de `--:--:--`.
  4. **Validación Binaria y Decodificación de Imágenes**: `ImageHelperAndValidatorTests` valida JPEG/PNG/WebP reales, rechazo de archivos vacíos (0 B), >10 MB, texto/ejecutable renombrado a `.jpg`, imágenes truncadas y carga escalada congelada sin bloqueo.
  5. **Lógica Transaccional y Reglas de Negocio**: `RaceDataServiceTimingTests` y `RaceDataServiceCatalogTests` validan tiempos no negativos, preservación de hora original, rollback ante error de BD conservando el temporal, exclusividad concurrente sobre misma foto, bloqueo de borrados con tiempos, reglas `Activo`/`DNS`/`DNF`, prohibición de cambio de hit con tiempo, re-evaluación automática de cierre de hit al pasar a `DNS`/`DNF`, y validación de dorsales/nombres duplicados.
  6. **Integración Web LAN y Contratos Frontend**: `WebServerIntegrationTests` y `FrontendContractTests` levantan Kestrel real en puerto efímero sin ventana WPF para validar token de 12–16 chars estable, `401` sin/con token inválido en `/api/*`, acceso libre a `wwwroot/`, carga multipart (`400`/`413`/`415`/`200`), bloqueo de traversal en `IdFoto`, rechazo cuando no hay carrera activa, limpieza al cambiar carrera, respuestas reales tras persistencia (`200`/`404`/`409`/`422`) y contratos en `app.js`/`index.html`.
  7. **Carga Objetivo y Eficiencia**: `PerformanceAndConcurrencyTests` y `AppLifecycleAndEfficiencyTests` simulan 40 corredores, 3 hits activos simultáneos y 2 clientes concurrentes enviando 10 llegadas/minuto (con ráfagas), verificando 100% de éxito, 0 `database is locked`, 1 única recarga consolidada por evento, versionado anti out-of-order, caché de logos, estabilización de memoria y ausencia de datos ficticios en carreras vacías.
- **Verificación manual / UI WPF**:
  - Ejecutar `dotnet run --project src/CarreraApp/CarreraApp.csproj`, verificar arranque instantáneo sin bloqueos, generación del código QR con `?token=` y respaldo manual visible, conexión de 2 dispositivos móviles por LAN en HTTPS/HTTP, pruebas de ráfaga de fotos y visualización fluida de la `LeaderboardWindow` y `CorredoresView` (`DNS`/`DNF` y exportación CSV).

---

## 4. Matriz de Cobertura de RF

| RF | Componentes / Métodos | Pruebas que lo cubren |
| :--- | :--- | :--- |
| **RF-01** | `StorageHelper.ConfigurarRutasParaPruebas`, `ApplicationDbContext(options)`, `RaceDataService(factory)` | `TestEnvironmentFixture`, `DatabaseConfigurationTests` |
| **RF-02** | `WebServer` (`ErrorCriticoServidor`, delegados), `RaceDataService`, `DatabaseMaintenanceHelper` | `WebServerIntegrationTests`, `RaceDataServiceTimingTests` |
| **RF-03** | `TimingView` (eliminación de `GenerarDatosDePruebaBD`) | `AppLifecycleAndEfficiencyTests` |
| **RF-04** | `ApplicationDbContext` (WAL/índices), `RaceDataService`, `WebServer` | `PerformanceAndConcurrencyTests` |
| **RF-05** | `WebServer.TokenSesionActual`, `WebServerOptions`, `WelcomeView.xaml(.cs)` | `WebServerIntegrationTests`, `AppLifecycleAndEfficiencyTests` |
| **RF-06** | `WebServer` (middleware auth `/api/*` + estáticos `wwwroot/`) | `WebServerIntegrationTests` |
| **RF-07** | `WebServer` (rechazo `401 Unauthorized`) | `WebServerIntegrationTests` |
| **RF-08** | `wwwroot/index.html`, `wwwroot/app.js` (manejo visual `401`) | `FrontendContractTests` |
| **RF-09** | `ImageHelper` (validación tamaño, *magic bytes* y `BitmapDecoder`), `WebServer` | `ImageHelperAndValidatorTests`, `WebServerIntegrationTests` |
| **RF-10** | `WebServer` (`/api/upload-photo` -> `400`, `413`, `415` JSON) | `WebServerIntegrationTests`, `ImageHelperAndValidatorTests` |
| **RF-11** | `WebServer` (validación `IdFoto` en `/api/assign-dorsal`, `/api/discard-photo`) | `WebServerIntegrationTests` |
| **RF-12** | `WebServer`, `MainWindow.xaml.cs` (rechazo si `CarreraActiva == null`) | `WebServerIntegrationTests` |
| **RF-13** | `AppState.CambiarCarreraActiva`, `WelcomeView`, `WebServer.LimpiarColaPendientes` | `WebServerIntegrationTests`, `AppLifecycleAndEfficiencyTests` |
| **RF-14** | `WebServer` (`/api/assign-dorsal`), `RaceDataService.AsignarTiempoAsync` | `WebServerIntegrationTests`, `RaceDataServiceTimingTests` |
| **RF-15** | `wwwroot/app.js` (bloqueo anti doble clic + manejo de error real) | `FrontendContractTests` |
| **RF-16** | `WebServer` (estados `Pendiente`/`EnProceso`), `TimingView`, `RaceDataService` | `WebServerIntegrationTests`, `RaceDataServiceTimingTests` |
| **RF-17** | `RaceDataService.AsignarTiempoAsync` (transacción, hora original, traslado, retiro cola) | `RaceDataServiceTimingTests` |
| **RF-18** | `RaceDataService.AsignarTiempoAsync` (rollback BD + limpieza parcial + conserva temporal) | `RaceDataServiceTimingTests` |
| **RF-19** | `RaceDataService.AsignarTiempoAsync` (rechazo `horaLlegada < Hora_Inicio`) | `RaceDataServiceTimingTests` |
| **RF-20** | `RaceDataService` (`EvaluarYCerrarHitAsync` en asignación y `DNS`/`DNF`) | `RaceDataServiceTimingTests`, `RaceDataServiceCatalogTests` |
| **RF-21** | `RaceDataService.CambiarEstadoCorredorAsync` / `CrearOActualizarCorredorAsync`, `CorredoresView` | `RaceDataServiceCatalogTests` |
| **RF-22** | `RaceDataService` (`EliminarCorredorAsync`, `EliminarHitAsync`, `EliminarCategoriaAsync`, `EliminarCarreraAsync`), vistas CRUD | `RaceDataServiceCatalogTests` |
| **RF-23** | `RaceDataService` (validación dorsal > 0 único, nombres vacíos/duplicados), vistas CRUD | `RaceDataServiceCatalogTests` |
| **RF-24** | `StorageHelper.SanitizarSegmentoRuta`, `ConstruirRutaFotoDefinitivaSegura` | `StorageAndBackupTests` |
| **RF-25** | `ApplicationDbContext`, `DatabaseMaintenanceHelper` (`WAL`, `foreign_keys`, `busy_timeout`, índices/migración) | `DatabaseConfigurationTests` |
| **RF-26** | `CsvExportHelper.SanitizarCeldaCsv`, `CorredoresView` | `CsvExportHelperTests` |
| **RF-27** | `StorageHelper.LimpiarFotosTempHuerfanas`, `App.xaml.cs` | `StorageAndBackupTests` |
| **RF-28** | `DatabaseMaintenanceHelper.RespaldarBaseDatosConsistentementeAsync`, `App.xaml.cs` | `StorageAndBackupTests` |
| **RF-29** | `App.xaml(.cs)`, `WelcomeView`, `ImageHelper`, `CsvExportHelper`, `CorredoresView` | `AppLifecycleAndEfficiencyTests`, `CsvExportHelperTests`, `ImageHelperAndValidatorTests` |
| **RF-30** | `TimingView`, `LeaderboardWindow` (1 recarga/evento, versionado, caché de logos) | `AppLifecycleAndEfficiencyTests` |
| **RF-31** | `TimingView.xaml(.cs)`, `MainWindow`, `LeaderboardWindow`, `ImageHelper`, `wwwroot/app.js` | `ImageHelperAndValidatorTests`, `FrontendContractTests`, `AppLifecycleAndEfficiencyTests` |

