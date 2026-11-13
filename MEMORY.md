# Memoria del Proyecto SistemaCarrera

Este documento almacena el estado persistente, decisiones técnicas y el historial de progreso de **SistemaCarrera**.

---

## 1. Estado Actual del Sistema

- **Versión / Target**: .NET 10.0 (WPF / SQLite / ASP.NET Core)
- **Estado de Compilación**: Proyecto base funcional.
- **Base de Datos**: `carrera.db` (SQLite) con tablas principales: `Carreras`, `Hits`, `Participantes` (con soporte para logos de carrera y hora de finalización de hits).
- **Servidor Web**: `WebServer.IniciarServidorAsync()` montado en `OnStartup` para compartir vistas y tablas en red local vía `wwwroot/`.

---

## 2. Decisiones Arquitectónicas Registradas

| Fecha | Decisión | Razón / Impacto |
| :--- | :--- | :--- |
| 2026-11-13 | Adopción de arquitectura Multiagente SDD | Separación de roles (Coordinator, Planner, Implementer, Reviewer) para garantizar especificaciones claras antes de modificar código. |
| 2026-11-13 | Unificación a .NET 10 / WPF | Descarte de herramientas Node.js y alineación de runners a `dotnet test` y `dotnet build`. |
| Inicial | Servidor web Kestrel embebido | Permite que dispositivos móviles y pantallas externas en la misma LAN consulten el leaderboard y tiempos sin requerir infraestructura cloud. |

---

## 3. Registro de Especificaciones (Specs)

| ID | Nombre | Estado | Resumen |
| :--- | :--- | :--- | :--- |
| - | *(Sin specs activas aún)* | - | El proyecto está listo para iniciar su primera spec con `/sdd` o `@coordinator`. |

---

## 4. Deuda Técnica y Pendientes Inmediatos

1. **Suite de pruebas**: Configurar proyecto de pruebas en `tests/` (`SistemaCarrera.Tests.csproj`) vinculado a `SistemaCarrera.sln`.
2. **Documentación de endpoints**: Mapear las rutas HTTP que expone `WebServer` hacia `wwwroot/` y vistas frontend.
