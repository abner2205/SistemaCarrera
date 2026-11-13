# Reglas y Guía del Proyecto SistemaCarrera

Bienvenido al repositorio de **SistemaCarrera**, una solución de cronometraje y gestión de competencias deportivas desarrollada en **.NET 10 (WPF + SQLite EF Core + ASP.NET Core embebido)**.

---

## 1. Estructura del Repositorio

- `src/CarreraApp/`: Proyecto principal de la aplicación.
  - `Models/`: Entidades de datos (Carreras, Participantes, Hits, Tiempos).
  - `Data/`: Contexto de base de datos (`ApplicationDbContext.cs`).
  - `Helpers/`: Utilidades y lógica de cálculo/formato.
  - `Server/`: Servidor web embebido que distribuye vistas en red local.
  - `UI/` y XAML (`MainWindow.xaml`, `LeaderboardWindow.xaml`): Interfaz de escritorio.
  - `wwwroot/`: Recursos web y frontend servidos en red local.
- `docs/`: Documentación del proyecto y [docs/constitution.md](file:///c:/Users/Armando/Desktop/Workflows/SistemaCarrera/docs/constitution.md).
- `specs/`: Especificaciones SDD (`specs/NNN-nombre/spec.md`, `plan.md`, `tasks.md`).
- `tests/`: Proyectos de pruebas unitarias e integración de la solución.
- `.agents/`:
  - `agents/`: Definición de subagentes especializados (`coordinator.md`, `planner.md`, `implementer.md`, `reviewer.md`).
  - `skills/`: Habilidades y procedimientos (`sdd`, `feature`).
  - `commands/`: Comandos ejecutables o flujos rápidos.
- `MEMORY.md`: Registro vivo de contexto, decisiones de arquitectura y estado del proyecto.

---

## 2. Comandos de Terminal

- **Compilar**:
  ```powershell
  dotnet build SistemaCarrera.sln
  ```
- **Ejecutar Pruebas**:
  ```powershell
  dotnet test
  ```
- **Ejecutar Aplicación**:
  ```powershell
  dotnet run --project src/CarreraApp/CarreraApp.csproj
  ```
- **Sincronizar con GitHub**:
  ```powershell
  git add .
  git commit -m "tipo(alcance): descripción concisa"
  git push origin <rama>
  ```

---

## 3. Convenciones de Desarrollo

1. **Lenguaje & Estilo C#**:
   - Respetar nullable reference types (`Nullable: enable`). Evitar warnings de tipo nulo usando comprobaciones explícitas o `null-coalescing`.
   - Convenciones PascalCase para métodos, propiedades y clases; camelCase para parámetros y variables locales; `_camelCase` para campos privados.
   - Usar `async`/`await` para operaciones I/O y base de datos.
2. **Interfaz WPF**:
   - Mantener desacoplada la lógica de cálculo de la vista.
   - Toda interacción con la UI desde tareas en segundo plano (como el servidor web o temporizadores) debe usar `Application.Current.Dispatcher.Invoke(...)`.
3. **Persistencia (EF Core & SQLite)**:
   - Evitar locks en `carrera.db`. Usar contextos con ciclos de vida cortos (`using var context = new ApplicationDbContext()`) para evitar retención prolongada de conexiones SQLite.

---

## 4. Flujo de Trabajo Multiagente (SDD)

El desarrollo de nuevas funcionalidades complejas sigue estrictamente el ciclo SDD:
1. **Coordinator**: Orquesta el flujo y actúa como interlocutor con el usuario.
2. **Planner**: Redacta `spec.md` (formato EARS), y tras aprobación, `plan.md` y `tasks.md`.
3. **Reviewer**: Valida la spec como QA en fase de clarificación; valida la implementación RF por RF al finalizar.
4. **Implementer**: Implementa una sola tarea a la vez, garantizando compilación y tests en verde antes de marcarla como completada.
5. **Cierre & Push a GitHub**: Con el `VEREDICTO: APROBADO` del Reviewer y la confirmación del usuario, el Coordinator actualiza `MEMORY.md`, genera el commit semántico y envía los cambios a GitHub mediante `git push`.
