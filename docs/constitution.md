# Constitución del Proyecto SistemaCarrera

Este documento establece los principios técnicos y de desarrollo no negociables para la aplicación **SistemaCarrera**. Todo agente (Coordinator, Planner, Implementer, Reviewer) y colaborador humano debe apegarse a estas directrices.

---

## 1. Stack Tecnológico Base
- **Plataforma**: .NET 10.0 (`net10.0-windows`), C# con `Nullable` e `ImplicitUsings` habilitados.
- **Capa Visual**: Windows Presentation Foundation (WPF) con XAML.
- **Servidor Web Embebido**: ASP.NET Core (`Microsoft.AspNetCore.App`) en hilo secundario para servir vistas públicas/móviles y endpoints en `wwwroot`.
- **Persistencia**: SQLite local (`carrera.db`) gestionado mediante **Entity Framework Core**.
- **Generación QR**: `QRCoder`.

---

## 2. Principios y Reglas No Negociables

### A. Gestión de Hilos y Rendimiento de la UI (WPF)
1. **Nunca bloquear el hilo de interfaz (UI Thread)**: Cualquier operación de entrada/salida (I/O), acceso a base de datos, generación de QR o llamadas de red del servidor embebido debe ejecutarse de forma asíncrona o en tareas secundarias (`Task.Run` / `async-await`).
2. **Actualizaciones seguras de UI**: Toda modificación de controles visuales desde hilos secundarios debe despacharse mediante `Application.Current.Dispatcher` (`Invoke` o `BeginInvoke`).

### B. Integridad de Datos y Persistencia (SQLite)
1. **Operaciones atómicas y seguras**: La base de datos `carrera.db` contiene tiempos de competencia, hits y participantes en tiempo real. No se deben ejecutar queries destructivas sin validación.
2. **Evolución del Esquema**: Cualquier modificación en modelos (`Models/`) o tablas debe contemplar la compatibilidad hacia atrás o mecanismos de actualización de esquema en `ApplicationDbContext` / `App.xaml.cs`.

### C. Arquitectura y Separación de Responsabilidades
- `Models/`: Entidades de dominio puras (Carreras, Hits, Participantes, etc.).
- `Data/`: `ApplicationDbContext` y lógica de acceso a datos.
- `Helpers/`: Funciones utilitarias, formateo de tiempos y cálculos.
- `Server/`: Lógica del servidor web embebido y controladores/endpoints HTTP.
- `UI/` y ventanas (`MainWindow`, `LeaderboardWindow`): Lógica de presentación XAML y code-behind.
- `wwwroot/`: Archivos estáticos y vistas HTML/JS para tableros o consulta pública.

### D. Confiabilidad Crítica (Tiempos de Carrera)
- La precisión y fiabilidad de los tiempos cronometrados de los hits son la máxima prioridad funcional del sistema.
- Ninguna excepción en el servidor web secundario o en la interfaz debe tumbar el proceso principal de cronometraje.

---

## 3. Estándares del Flujo de Trabajo (SDD)
- **Cero código sin spec aprobada**: Salvo correcciones de bugs menores de una línea o tareas `/feature`, los cambios de funcionalidad se inician en una especificación en `specs/`.
- **Compilación Limpia**: No se aceptan entregas de tareas que dejen advertencias críticas o errores en `dotnet build`.
- **Pruebas Automatizadas**: Toda lógica de cálculo de tiempos, desempates, posiciones y persistencia debe estar cubierta por pruebas automatizadas con `dotnet test`.
- **Integración Continua con GitHub**: Todo cambio aprobado por el Reviewer y el usuario debe consolidarse con un commit semántico claro (`feat`, `fix`, `docs`, `refactor`) y sincronizarse a la rama remota correspondiente en GitHub sin romper el repositorio principal.

