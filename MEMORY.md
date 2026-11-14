# Memoria del Proyecto SistemaCarrera

Este documento almacena el estado persistente, decisiones técnicas y el historial de progreso de **SistemaCarrera**.

---

## 1. Estado Actual del Sistema

- **Versión / Target**: .NET 10.0 (WPF / SQLite EF Core / ASP.NET Core embebido)
- **Estado de Compilación y Pruebas**: `dotnet build SistemaCarrera.slnx` limpio (`0 Advertencia(s), 0 Errores`); suite `CarreraApp.Tests` pasando al 100% (`202/202` pruebas en verde, `0` omitidas).
- **Base de Datos**: `carrera.db` (SQLite en modo `WAL`, `foreign_keys = ON`, `busy_timeout = 5000`) con tablas principales `Carreras`, `Categorias`, `Hits`, `Corredores`, `Tiempos` y respaldos consistentes vía `DatabaseMaintenanceHelper`.
- **Servidor Web**: `WebServer` (Kestrel embebido HTTP/HTTPS) con autenticación por token de sesión (`X-Auth-Token` / `token` para QR), validación estricta de imágenes/dorsales, exclusividad mutua de fotos pendientes y vista móvil/pública en `wwwroot/`.

---

## 2. Decisiones Arquitectónicas Registradas

| Fecha | Decisión | Razón / Impacto |
| :--- | :--- | :--- |
| 2026-11-13 | Adopción de arquitectura Multiagente SDD | Separación de roles (Coordinator, Planner, Implementer, Reviewer) para garantizar especificaciones claras antes de modificar código. |
| 2026-11-13 | Unificación a .NET 10 / WPF | Descarte de herramientas Node.js y alineación de runners a `dotnet test` y `dotnet build`. |
| 2026-11-13 | Endurecimiento SQLite WAL, respaldo consistente e índices | `ApplicationDbContext` configura `WAL`, `FK`, `busy_timeout=5000`; `DatabaseMaintenanceHelper` ejecuta respaldos en caliente vía SQLite Online Backup API. |
| 2026-11-13 | Token de sesión LAN, validación binaria de imágenes y sanitización | `WebServer` exige token por sesión en rutas operativas/API; `ImageHelper`, `StorageHelper` y `CsvExportHelper` validan magic bytes/dimensiones, rutas seguras y fórmulas CSV. |
| 2026-11-13 | Transacciones atómicas, consolidación UI y desacople de vistas | `RaceDataService` encapsula asignación de tiempos y auto-cierre de hits con reintento ante `SQLITE_BUSY=5`; `ReloadVersionCoordinator`, `LogoRouteCache` y `TimingView` (colecciones locales desuscritas en `Unloaded`) evitan recargas duplicadas y retención de hilos STA. |
| Inicial | Servidor web Kestrel embebido | Permite que dispositivos móviles y pantallas externas en la misma LAN consulten el leaderboard y tiempos sin requerir infraestructura cloud. |

---

## 3. Registro de Especificaciones (Specs)

| ID | Nombre | Estado | Resumen |
| :--- | :--- | :--- | :--- |
| `001` | `001-seguridad-eficiencia` | Completada | Seguridad LAN/archivo/CSV, atomicidad de tiempos/hits, reglas `DNS`/`DNF`, optimización de arranque/UI/memoria y suite completa de pruebas unitarias, integración y carga (`202/202` en verde). Validada RF-01 a RF-31 (`VEREDICTO: APROBADO`). |

---

## 4. Deuda Técnica y Pendientes Inmediatos

1. **Verificación manual en LAN (opcional)**: Ejecutar `dotnet run --project src/CarreraApp/CarreraApp.csproj` con dos teléfonos reales en la misma red local previo a evento en vivo.
2. **Documentación de endpoints y limpieza menor**: Mantener actualizado el mapa de rutas HTTP/HTTPS de `WebServer` y evaluar el retiro posterior del evento legado `DorsalAsignadoRemoto` en `WebServer.cs`.
