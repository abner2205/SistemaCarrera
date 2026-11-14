# Tareas de Implementación: 001 - Auditoría y Remediación de Seguridad de Datos y Eficiencia de Ejecución

**Plan:** `specs/001-seguridad-eficiencia/plan.md`

- [x] **T1: Aislamiento de rutas, configuración SQLite (WAL/FK/busy_timeout), migración segura e infraestructura de pruebas**
  - **RF Asociados:** RF-01, RF-25
  - **Archivos:** `src/CarreraApp/Helpers/StorageHelper.cs`, `src/CarreraApp/Data/ApplicationDbContext.cs`, `src/CarreraApp/Helpers/DatabaseMaintenanceHelper.cs`, `tests/CarreraApp.Tests/TestEnvironmentFixture.cs`, `tests/CarreraApp.Tests/DatabaseConfigurationTests.cs`, `tests/CarreraApp.Tests/UnitTest1.cs` (reemplazar)
  - **Hecho cuando:** `dotnet build SistemaCarrera.slnx` compila sin errores y `dotnet test` pasa en verde `DatabaseConfigurationTests` comprobando aislamiento total fuera de producción, `PRAGMA journal_mode=WAL`, `PRAGMA foreign_keys=ON`, `PRAGMA busy_timeout`, índices e idempotencia de migración sobre una BD previa sin columnas ni índices nuevos.

- [x] **T2: Respaldo consistente de `carrera.db` (máx. 10), limpieza de temporales huérfanos y sanitización de rutas**
  - **RF Asociados:** RF-24, RF-27, RF-28
  - **Archivos:** `src/CarreraApp/Helpers/StorageHelper.cs`, `src/CarreraApp/Helpers/DatabaseMaintenanceHelper.cs`, `tests/CarreraApp.Tests/StorageAndBackupTests.cs`
  - **Hecho cuando:** `dotnet build SistemaCarrera.slnx` compila y `dotnet test` pasa en verde `StorageAndBackupTests` validando sanitización de rutas (`..`, colisiones, longitud máxima), depuración en `Fotos_Temp` de `.jpg`/`.jpeg`/`.png`/`.webp` huérfanos preservando `.gitkeep` y `Fotos_Definitivas`, rotación a máximo 10 respaldos consistentes bajo WAL y tolerancia sin bloqueo ante fallo de respaldo.

- [x] **T3: Sanitización CSV contra inyección de fórmulas y exportación asíncrona**
  - **RF Asociados:** RF-26, RF-29
  - **Archivos:** `src/CarreraApp/Helpers/CsvExportHelper.cs`, `src/CarreraApp/views/CorredoresView.xaml.cs`, `tests/CarreraApp.Tests/CsvExportHelperTests.cs`
  - **Hecho cuando:** `dotnet build SistemaCarrera.slnx` compila y `dotnet test` pasa en verde `CsvExportHelperTests` verificando prefijo `'` en campos editables con `=`, `+`, `-`, `@`, `\t`, `\r` (directos o tras espacios iniciales), preservación de `--:--:--` y escritura asíncrona UTF-8 BOM.

- [x] **T4: Validación binaria de imágenes (JPEG/PNG/WebP <= 10 MB) y carga/decodificación escalada no bloqueante**
  - **RF Asociados:** RF-09, RF-10, RF-29, RF-31
  - **Archivos:** `src/CarreraApp/Helpers/ImageHelper.cs`, `tests/CarreraApp.Tests/ImageHelperAndValidatorTests.cs`
  - **Hecho cuando:** `dotnet build SistemaCarrera.slnx` compila y `dotnet test` pasa en verde `ImageHelperAndValidatorTests` validando JPEG/PNG/WebP auténticos, rechazo de 0 bytes, >10 MB, archivos no imagen renombrados e imágenes corruptas sin usar infraestructura de UI, además de decodificación escalada y congelada (`Freeze()`).

- [x] **T5: Atomicidad transaccional, tiempos no negativos, exclusividad y cierre automático de hits en `RaceDataService`**
  - **RF Asociados:** RF-02, RF-14, RF-16, RF-17, RF-18, RF-19, RF-20
  - **Archivos:** `src/CarreraApp/Data/RaceDataService.cs`, `tests/CarreraApp.Tests/RaceDataServiceTimingTests.cs`
  - **Hecho cuando:** `dotnet build SistemaCarrera.slnx` compila y `dotnet test` pasa en verde `RaceDataServiceTimingTests` comprobando ejecución sin WPF, transacción explícita, preservación de `horaLlegada`, rechazo de tiempos netos negativos, cierre automático de hit (`Hora_Fin >= max(Hora_Llegada)`), rollback de BD y archivo definitivo ante fallo preservando el temporal, y exclusividad en asignaciones concurrentes.

- [x] **T6: Reglas `DNS`/`DNF`, protección de borrados destructivos y validación de catálogos**
  - **RF Asociados:** RF-02, RF-20, RF-21, RF-22, RF-23
  - **Archivos:** `src/CarreraApp/Data/RaceDataService.cs`, `src/CarreraApp/views/CorredoresView.xaml`, `src/CarreraApp/views/CorredoresView.xaml.cs`, `src/CarreraApp/views/HitsView.xaml.cs`, `src/CarreraApp/views/WelcomeView.xaml.cs`, `tests/CarreraApp.Tests/RaceDataServiceCatalogTests.cs`
  - **Hecho cuando:** `dotnet build SistemaCarrera.slnx` compila y `dotnet test` pasa en verde `RaceDataServiceCatalogTests` validando dorsal > 0 único, nombres no vacíos/no duplicados, prohibición de eliminar carreras/categorías/hits/corredores con tiempos, cambio `Activo`/`DNS`/`DNF` solo sin tiempo, prohibición de cambiar hit tras tener tiempo y re-evaluación automática de cierre de hit al pasar a `DNS`/`DNF`.

- [x] **T7: Seguridad del servidor web LAN (`WebServer`), token por sesión, validación de endpoints y pruebas de integración**
  - **RF Asociados:** RF-02, RF-05, RF-06, RF-07, RF-09, RF-10, RF-11, RF-12, RF-13, RF-14, RF-16
  - **Archivos:** `src/CarreraApp/Server/WebServer.cs`, `src/CarreraApp/Models/AppState.cs`, `tests/CarreraApp.Tests/WebServerIntegrationTests.cs`
  - **Hecho cuando:** `dotnet build SistemaCarrera.slnx` compila y `dotnet test` pasa en verde `WebServerIntegrationTests` ejecutando Kestrel aislado sin UI WPF y validando token URL-safe de 12–16 caracteres, `401` en `/api/*` sin/con token inválido, carga libre de `wwwroot/`, rechazo `400`/`413`/`415`, bloqueo de *path traversal* en `IdFoto`, rechazo si no hay carrera activa, limpieza de cola al cambiar carrera y exclusividad móvil-móvil / móvil-PC con respuesta HTTP real tras persistencia.

- [x] **T8: Endurecimiento de seguridad, control de ráfagas y sincronización en el cliente web móvil**
  - **RF Asociados:** RF-08, RF-14, RF-15, RF-31
  - **Archivos:** `src/CarreraApp/wwwroot/index.html`, `src/CarreraApp/wwwroot/app.js`, `tests/CarreraApp.Tests/FrontendContractTests.cs`
  - **Hecho cuando:** `dotnet build SistemaCarrera.slnx` compila y `dotnet test` pasa en verde `FrontendContractTests` validando extracción/envío de `X-Auth-Token`, manejo visual de `401`, bloqueo anti doble clic, feedback ante errores reales sin marcar verde, generación de `tempId` único, aislamiento de canvas en ráfaga y sondeo secuencial sin peticiones solapadas.

- [x] **T9: Arranque asíncrono, QR estable con token, eliminación de datos ficticios y optimización de UI WPF**
  - **RF Asociados:** RF-03, RF-05, RF-13, RF-27, RF-28, RF-29, RF-30, RF-31
  - **Archivos:** `src/CarreraApp/App.xaml`, `src/CarreraApp/App.xaml.cs`, `src/CarreraApp/MainWindow.xaml.cs`, `src/CarreraApp/LeaderboardWindow.xaml.cs`, `src/CarreraApp/Models/AppState.cs`, `src/CarreraApp/views/WelcomeView.xaml`, `src/CarreraApp/views/WelcomeView.xaml.cs`, `src/CarreraApp/views/TimingView.xaml`, `src/CarreraApp/views/TimingView.xaml.cs`
  - **Hecho cuando:** `dotnet build SistemaCarrera.slnx` compila limpio sin advertencias críticas y `dotnet test` pasa en verde toda la suite acumulada tras integrar inicio asíncrono, QR con token estable y respaldo manual, eliminación de seed ficticio en `TimingView`, miniaturas escaladas, 1 recarga consolidada por evento, versionado y caché de logos en `LeaderboardWindow`, limpieza al cambiar de carrera y liberación de recursos/ventanas al navegar o cerrar.

- [x] **T10: Suite de pruebas de carga objetivo y validación integral de eficiencia y ciclo de vida**
  - **RF Asociados:** RF-03, RF-04, RF-29, RF-30, RF-31
  - **Archivos:** `tests/CarreraApp.Tests/PerformanceAndConcurrencyTests.cs`, `tests/CarreraApp.Tests/AppLifecycleAndEfficiencyTests.cs`
  - **Hecho cuando:** `dotnet build SistemaCarrera.slnx` compila limpio y `dotnet test` ejecuta al 100% en verde toda la suite (incluyendo simulación de 40 corredores, 3 hits activos simultáneos, 2 clientes concurrentes y 10 llegadas/minuto con 0 errores `database is locked`, 1 recarga por evento, caché de logos, estabilidad de memoria y ausencia de datos ficticios en carreras vacías).
