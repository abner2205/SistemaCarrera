# Plan de Implementación: 002 - Identidad Visual y Feedback Interactivo del Frontend

**Spec de referencia:** `specs/002-feedback-visual-frontend/spec.md`

## 1. Sistema de Diseño Visual (`frontend-design` — Offline & Alto Contraste)

### A. Paleta de Tokens (4–6 Hex Base + Semánticos WCAG AA)
| Token | Valor Hex | Uso Principal | Contraste Verificado |
| :--- | :--- | :--- | :--- |
| `TrackInk` | `#0F172A` | Texto principal, barra superior, fondo oscuro de cabecera | `> 15:1` sobre `#F8FAFC` / `#FFFFFF` |
| `ChalkSurface` | `#F8FAFC` | Fondo base de vistas claras (móvil y escritorio) | Base clara de alto contraste |
| `SlateMuted` | `#475569` | Texto secundario, subtítulos, etiquetas de campo | `~7.0:1` sobre `#F8FAFC` (WCAG AA >= 4.5:1) |
| `SignalCobalt` | `#0B57D0` | Acción primaria, foco de teclado, elemento seleccionado | `~6.4:1` con texto `#FFFFFF`; `> 3:1` borde foco |
| `FinishEmerald` | `#047857` | Confirmación exitosa, asignación válida, hit en curso | `~5.4:1` con texto `#FFFFFF` |
| `PenaltyCrimson` | `#B91C1C` | Errores, descarte, rechazo `401` | `~6.4:1` con texto `#FFFFFF` |
| `CautionAmber` | `#B45309` | Advertencias, reintentos, bloqueo por tiempo | `~5.0:1` con `#FFFFFF`; `#78350F` sobre `#FEF3C7` (`> 9:1`) |

- **Pizarra de Clasificación (`LeaderboardWindow`)**: Fondo `#0B0E14` con texto principal `#F8FAFC` (`> 17:1`), oro `#FACC15` (`> 12:1`), plata `#E2E8F0` y bronce `#FDBA74` (`> 10:1`).

### B. Tipografía Local/Offline y Tabular
- **Web Móvil (`wwwroot/`)**: `system-ui, -apple-system, "Segoe UI", Roboto, Arial, sans-serif` para interfaz; `font-variant-numeric: tabular-nums` + respaldo monoespaciado local (`"Cascadia Mono", "Consolas", ui-monospace, monospace`) en dorsales, contadores y tiempos.
- **Escritorio WPF**: `"Segoe UI"` con `Typography.NumeralAlignment="Tabular"` en controles numéricos, y respaldo explícito `"Consolas"` en cronómetros de hit, tiempos netos, posiciones y dorsales críticos.

### C. Concepto de Layout y Principios de Diseño
- **Identidad "Mesa de Control y Pista"**: Jerarquía visual enfocada en la lectura instantánea de dorsales, estados de hit y tiempos; cero decoración SaaS genérica, cero etiquetas innecesarias en mayúsculas sostenidas y copywriting en voz activa (*"Capturar foto"*, *"Asignar dorsal"*, *"Guardar corredor"*).
- **Web Móvil (320–440 px)**: Flujo de una sola mano con barra de sesión compacta, visor + botón de disparo siempre disponible, cola pendiente con tarjetas resistentes a nombres largos y teclado virtual.
- **Movimiento con moderación**: Transiciones de `150–250 ms` solo en respuesta a acciones del usuario; cero animaciones continuas decorativas en reposo; respeto a `prefers-reduced-motion` (web) y `SystemParameters.ClientAreaAnimation` (Windows/WPF).

---

## 2. Arquitectura y Componentes Afectados

- **Cliente Web Móvil (`src/CarreraApp/wwwroot/`)**:
  - `src/CarreraApp/wwwroot/index.html` (y estilos embebidos o `styles.css` local si se extrae): Tokens CSS, estados `:hover`, `:focus-visible`, `:active`, `:disabled`, layout responsive `320–360 px`, manejo de teclado virtual, toast/banners no bloqueantes, estado vacío de cola y `@media (prefers-reduced-motion: reduce)`.
  - `src/CarreraApp/wwwroot/app.js`: Feedback visual instantáneo de disparo en `capturePhotoFrame()`, estados por tarjeta (`subiendo`, `lista`, `asignando`, `error`, `procesada`), indicador de desconexión LAN y recuperación en `pollPendingPhotos()`, aviso no intrusivo cuando `syncPendingQueueWithServer()` retira una tarjeta procesada concurrentemente o por cambio de carrera, y toast auto-ocultable (`3–4 s`).
- **Diccionario de Estilos y Helpers WPF (`src/CarreraApp/`)**:
  - `src/CarreraApp/App.xaml`: Recursos globales con paleta de tokens, estilos base para `Button` (primario, secundario, peligro, éxito), `TextBox`, `ComboBox`, `ListViewItem` (con `FocusVisualStyle`, `IsMouseOver`, `IsPressed`, `IsSelected`, `IsEnabled`) y estilos tipográficos tabulares.
  - `src/CarreraApp/Helpers/UiFeedbackHelper.cs` (o helper equivalente en `Helpers/`): Helper desacoplado y testeable para consulta de preferencia de animación en Windows (`ClientAreaAnimation`), duración efectiva de transiciones (`0 ms` vs `150–250 ms`) y lógica de formato/estado de notificaciones no bloqueantes.
- **Ventanas y Vistas WPF (`src/CarreraApp/` y `src/CarreraApp/views/`)**:
  - `src/CarreraApp/MainWindow.xaml` y `MainWindow.xaml.cs`: Barra superior renovada, indicador de sección activa en menú lateral, banner/toast global no bloqueante con auto-ocultamiento (`3–4 s`) y limpieza de temporizador de toast al cerrar.
  - `src/CarreraApp/views/WelcomeView.xaml` y `WelcomeView.xaml.cs`: Selección visual de carrera activa, estado vacío accionable cuando no hay carreras, validación en línea y feedback de creación/respaldo.
  - `src/CarreraApp/views/CorredoresView.xaml` y `CorredoresView.xaml.cs`: Resaltado de campos con error en línea, reemplazo de `MessageBox` de validación/confirmación rutinaria por feedback en línea/toast (manteniendo diálogo únicamente en confirmación destructiva de eliminación), estado vacío accionable y etiquetas claras para `Activo`/`DNS`/`DNF`.
  - `src/CarreraApp/views/HitsView.xaml` y `HitsView.xaml.cs`: Feedback en línea para validación de hits/categorías, estados vacíos accionables y confirmación no bloqueante al guardar.
  - `src/CarreraApp/views/TimingView.xaml` y `TimingView.xaml.cs`: Diferenciación visual clara de hits `En Curso` vs `En Espera`/`Finalizado`, cronómetro tabular, resaltado de foto/corredor seleccionado, feedback en línea ante errores de asignación y preservación estricta de `TimingView_Unloaded` (`_cronometroTimer`, `_hitsReloadCoordinator`).
  - `src/CarreraApp/LeaderboardWindow.xaml` y `LeaderboardWindow.xaml.cs`: Jerarquía de podio (`1°`, `2°`, `3°`) y tabla `4°+` con dígitos tabulares, contraste `#0B0E14`, truncado seguro de nombres largos y preservación de `ReloadVersionCoordinator` y `LogoRouteCache`.

---

## 3. Decisiones Técnicas y Alternativas Descartadas

1. **Tokens y estilos nativos en `App.xaml` + CSS local sin librerías externas**:
   - *Decisión*: Implementar tokens en `App.xaml` (WPF) y CSS puro en `wwwroot/` sin Tailwind CDN, Google Fonts ni paquetes externos de UI (MaterialDesign/FluentWPF).
   - *Descartado*: CDNs externos (violan operación 100% offline en LAN) y librerías pesadas de terceros en WPF que agregan riesgo de regresión visual y complejidad innecesaria.
2. **Feedback no bloqueante por banner/toast integrado y validación en línea**:
   - *Decisión*: Usar un componente/región de notificación no bloqueante con temporizador seguro (`3–4 s`) y resaltado de borde/mensaje en línea junto a los campos.
   - *Descartado*: Mantener `MessageBox.Show(...)` para éxitos o validaciones rutinarias (interrumpe el flujo del operador en competencia). Se conserva diálogo modal únicamente para confirmar eliminaciones destructivas.
3. **Respeto a movimiento reducido mediante helper testeable + `@media (prefers-reduced-motion)`**:
   - *Decisión*: Encapsular el cálculo de duración de animación (`0 ms` si movimiento reducido, `180 ms` estándar) en un helper puro en `Helpers/` y usar `@media (prefers-reduced-motion: reduce)` en `wwwroot/`.
   - *Descartado*: Animaciones fijas en XAML/JS sin verificar preferencias del sistema operativo.

---

## 4. Estrategia de Pruebas (`dotnet test`)

- **Suite existente (`CarreraApp.Tests`)**: Ejecutar `dotnet test` tras cada tarea para garantizar que las **202 pruebas** de `001-seguridad-eficiencia` permanecen al 100% en verde.
- **Nuevas pruebas unitarias en `tests/CarreraApp.Tests/`**:
  - Verificación de contraste WCAG AA sobre la paleta de tokens definida (cálculo de luminancia relativa y ratio `>= 4.5:1` / `>= 3:1`).
  - Pruebas del helper de feedback/movimiento reducido (`UiFeedbackHelper`: duraciones `0 ms` cuando `animacionesHabilitadas == false`, `150–250 ms` cuando `true`, y manejo de mensajes/niveles de toast).
  - Pruebas de contrato sobre recursos estáticos (`wwwroot/index.html` y `wwwroot/app.js`): ausencia de URLs externas (`http://`, `https://` a CDNs), presencia de `@media (prefers-reduced-motion: reduce)`, `tabular-nums`, y conservación de manejo de `401` y cola pendiente.
- **Verificación manual de UI (WPF + móvil)**: Revisión visual de `320–360 px` con teclado virtual, navegación por teclado (`Tab`/`Enter`) y escalado DPI en `MainWindow` y `LeaderboardWindow`.

---

## 5. Matriz de Cobertura de RF

| RF | Componentes / Archivos | Cobertura de Pruebas |
| :--- | :--- | :--- |
| **RF-01** | `App.xaml`, `index.html`, `LeaderboardWindow.xaml` | Test unitario de ratios WCAG AA de paleta |
| **RF-02** | `App.xaml`, `index.html`, `app.js` | Test de ausencia de CDNs/URLs externas en `wwwroot/` |
| **RF-03** | `App.xaml`, `TimingView.xaml`, `LeaderboardWindow.xaml`, `index.html` | Test de presencia de `tabular-nums` / estilos tabulares |
| **RF-04** | `index.html`, `app.js`, `WelcomeView`, `CorredoresView`, `HitsView`, `TimingView` | Revisión de copywriting y estados vacíos en vistas |
| **RF-05** | `App.xaml` (ControlTemplates/FocusVisualStyle), `index.html` | Inspección XAML/CSS de estados interactivos |
| **RF-06** | `App.xaml`, `WelcomeView`, `TimingView`, `CorredoresView`, `HitsView`, `index.html` | Inspección de estilos `IsSelected` / tarjeta activa |
| **RF-07** | `App.xaml`, `MainWindow`, `index.html`, `UiFeedbackHelper` | Test `UiFeedbackHelper` (`150–250 ms`, sin loops en reposo) |
| **RF-08** | `app.js` (`capturePhotoFrame`), `TimingView.xaml.cs` | Verificación de flujo sin `vibrate` ni retardos bloqueantes |
| **RF-09** | `app.js` (`setItemBusy`, `assignRunner`, `discardPhoto`), `CorredoresView`, `HitsView` | Verificación de bloqueo por tarjeta sin afectar captura |
| **RF-10** | `MainWindow` (toast global), vistas WPF, `app.js` (`showToast`/`showStatusBanner`) | Test de estado/auto-ocultamiento de toast y revisión UI |
| **RF-11** | `CorredoresView.xaml(.cs)`, `HitsView.xaml(.cs)`, `TimingView.xaml(.cs)`, `app.js` | Revisión de eliminación de `MessageBox` rutinarios + feedback en línea |
| **RF-12** | `index.html`, `app.js` (`capturePhotoFrame`, `addPendingItemToUI`) | Verificación de disparo instantáneo no bloqueante |
| **RF-13** | `app.js` (`syncPendingQueueWithServer`, `setItemStatus`) | Test/verificación de retiro limpio por concurrencia/cambio carrera |
| **RF-14** | `index.html` (`prefers-reduced-motion`), `UiFeedbackHelper` (`ClientAreaAnimation`) | Test unitario `UiFeedbackHelper` + test contrato CSS |
| **RF-15** | `MainWindow.xaml(.cs)`, `WelcomeView.xaml(.cs)`, `TimingView.xaml.cs` | Verificación de navegación activa y `Unloaded` limpio |
| **RF-16** | `TimingView.xaml(.cs)` | Verificación visual de estados de Hit (`En Curso`/`En Espera`/`Finalizado`) |
| **RF-17** | `LeaderboardWindow.xaml(.cs)` | Verificación de podio/tabla `#0B0E14`, `ReloadVersionCoordinator` y caché logos |
