# Tareas de Implementación: 002 - Identidad Visual y Feedback Interactivo del Frontend

**Plan:** `specs/002-feedback-visual-frontend/plan.md`

- [x] **T1: Helper de feedback visual, paleta WCAG AA y pruebas unitarias base**
  - **RF Asociados:** RF-01, RF-02, RF-07, RF-10, RF-14
  - **Archivos:** `src/CarreraApp/Helpers/UiFeedbackHelper.cs`, `tests/CarreraApp.Tests/UiFeedbackTests.cs`
  - **Hecho cuando:** `dotnet build SistemaCarrera.slnx` compila sin errores ni advertencias y `dotnet test` pasa al 100% incluyendo pruebas unitarias de contraste WCAG AA (`>= 4.5:1` / `>= 3:1`), duraciones de transición (`150–250 ms` vs `0 ms` con movimiento reducido) y configuración de avisos no bloqueantes.

- [x] **T2: Tokens de diseño y estilos globales interactivos en WPF**
  - **RF Asociados:** RF-01, RF-02, RF-03, RF-05, RF-06, RF-07
  - **Archivos:** `src/CarreraApp/App.xaml`
  - **Hecho cuando:** `App.xaml` define la paleta de tokens, estilos de botones (primario, secundario, éxito, peligro), `TextBox`, `ComboBox`, `ListViewItem` (con estados reposo, *hover*, foco visible, presionado, seleccionado y deshabilitado) y estilos de dígitos tabulares locales, compilando limpio con `dotnet build`.

- [x] **T3: Ventana principal (`MainWindow`) con navegación activa y toast global no bloqueante**
  - **RF Asociados:** RF-04, RF-05, RF-10, RF-14, RF-15
  - **Archivos:** `src/CarreraApp/MainWindow.xaml`, `src/CarreraApp/MainWindow.xaml.cs`
  - **Hecho cuando:** `MainWindow` resalta la sección activa en el menú lateral, muestra un toast/banner global no bloqueante con auto-ocultamiento de `3–4 s` respetando movimiento reducido, reemplaza avisos informativos bloqueantes al navegar sin carrera activa y libera su temporizador al cerrar.

- [x] **T4: Rediseño e interacciones del cliente web móvil (`index.html` y `app.js`)**
  - **RF Asociados:** RF-01, RF-02, RF-03, RF-04, RF-05, RF-06, RF-07, RF-08, RF-09, RF-10, RF-11, RF-12, RF-13, RF-14
  - **Archivos:** `src/CarreraApp/wwwroot/index.html`, `src/CarreraApp/wwwroot/app.js`
  - **Hecho cuando:** El cliente móvil aplica la nueva paleta WCAG AA 100% offline, `tabular-nums`, soporte `320–360 px` y teclado virtual, respuesta instantánea de disparo en `capturePhotoFrame()` sin bloquear el botón de cámara, bloqueo solo por tarjeta en curso, aviso no intrusivo ante retiro por concurrencia/cambio de carrera, indicador de desconexión LAN, toast auto-ocultable y `@media (prefers-reduced-motion: reduce)`.

- [x] **T5: Vista de bienvenida (`WelcomeView`) con selección clara y estado vacío accionable**
  - **RF Asociados:** RF-01, RF-04, RF-05, RF-06, RF-10, RF-11, RF-15
  - **Archivos:** `src/CarreraApp/views/WelcomeView.xaml`, `src/CarreraApp/views/WelcomeView.xaml.cs`
  - **Hecho cuando:** `WelcomeView` resalta la carrera seleccionada, muestra estado vacío accionable cuando no hay carreras, presenta validación en línea y confirmaciones no bloqueantes al crear carrera o copiar enlace/token, manteniendo intacta la generación QR.

- [x] **T6: Vistas de gestión (`CorredoresView` y `HitsView`) con validación en línea y sin `MessageBox` rutinarios**
  - **RF Asociados:** RF-03, RF-04, RF-05, RF-06, RF-09, RF-10, RF-11
  - **Archivos:** `src/CarreraApp/views/CorredoresView.xaml`, `src/CarreraApp/views/CorredoresView.xaml.cs`, `src/CarreraApp/views/HitsView.xaml`, `src/CarreraApp/views/HitsView.xaml.cs`
  - **Hecho cuando:** Ambas vistas aplican los nuevos estilos, dígitos tabulares en dorsales, estados vacíos accionables, etiquetas claras para `Activo`/`DNS`/`DNF`, validación visual en línea de campos y confirmación no bloqueante al guardar/exportar (conservando diálogo modal solo en eliminaciones destructivas).

- [x] **T7: Panel de cronometraje (`TimingView`) con estados de Hit y feedback de asignación**
  - **RF Asociados:** RF-03, RF-04, RF-05, RF-06, RF-08, RF-09, RF-10, RF-11, RF-15, RF-16
  - **Archivos:** `src/CarreraApp/views/TimingView.xaml`, `src/CarreraApp/views/TimingView.xaml.cs`
  - **Hecho cuando:** `TimingView` diferencia visualmente hits `En Curso`, `En Espera` y `Finalizado`, usa cronómetro tabular, resalta la captura/corredor seleccionado, muestra errores y confirmaciones de asignación sin `MessageBox` bloqueantes y preserva íntegra la limpieza de `_cronometroTimer` y `_hitsReloadCoordinator` en `TimingView_Unloaded`.

- [x] **T8: Pizarra de clasificación (`LeaderboardWindow`) y pruebas finales de contrato/regresión**
  - **RF Asociados:** RF-01, RF-02, RF-03, RF-04, RF-17
  - **Archivos:** `src/CarreraApp/LeaderboardWindow.xaml`, `src/CarreraApp/LeaderboardWindow.xaml.cs`, `tests/CarreraApp.Tests/FrontendContractTests.cs`
  - **Hecho cuando:** `LeaderboardWindow` aplica fondo `#0B0E14`, podio y tabla con dígitos tabulares, truncado seguro de nombres largos y escalado DPI sin alterar `ReloadVersionCoordinator` ni `LogoRouteCache`; las pruebas de contrato verifican ausencia de CDNs externos y presencia de reglas de accesibilidad/movimiento reducido en `wwwroot/`, y `dotnet test` pasa al 100% (`202` existentes + nuevas en verde).
