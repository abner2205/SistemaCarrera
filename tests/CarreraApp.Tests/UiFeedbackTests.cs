using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;
using CarreraApp.Data;
using CarreraApp.Helpers;
using CarreraApp.Models;
using CarreraApp.UI;
using CarreraApp.UI.Views;

namespace CarreraApp.Tests
{
    public class UiFeedbackTests
    {
        public static IEnumerable<object[]> ParesContrasteTextoNormalWcagAA()
        {
            // Tokens base de plan.md (texto normal >= 4.5:1)
            yield return new object[] { UiFeedbackHelper.TokensPaleta.TrackInk, UiFeedbackHelper.TokensPaleta.ChalkSurface };
            yield return new object[] { UiFeedbackHelper.TokensPaleta.TrackInk, UiFeedbackHelper.TokensPaleta.PureWhite };
            yield return new object[] { UiFeedbackHelper.TokensPaleta.SlateMuted, UiFeedbackHelper.TokensPaleta.ChalkSurface };
            yield return new object[] { UiFeedbackHelper.TokensPaleta.SlateMuted, UiFeedbackHelper.TokensPaleta.PureWhite };
            yield return new object[] { UiFeedbackHelper.TokensPaleta.PureWhite, UiFeedbackHelper.TokensPaleta.SignalCobalt };
            yield return new object[] { UiFeedbackHelper.TokensPaleta.PureWhite, UiFeedbackHelper.TokensPaleta.FinishEmerald };
            yield return new object[] { UiFeedbackHelper.TokensPaleta.PureWhite, UiFeedbackHelper.TokensPaleta.PenaltyCrimson };
            yield return new object[] { UiFeedbackHelper.TokensPaleta.PureWhite, UiFeedbackHelper.TokensPaleta.CautionAmber };
            yield return new object[] { UiFeedbackHelper.TokensPaleta.CautionAmberDarkText, UiFeedbackHelper.TokensPaleta.CautionAmberSurface };

            // Pizarra de clasificación (LeaderboardWindow)
            yield return new object[] { UiFeedbackHelper.TokensPaleta.LeaderboardText, UiFeedbackHelper.TokensPaleta.LeaderboardBg };
            yield return new object[] { UiFeedbackHelper.TokensPaleta.LeaderboardGold, UiFeedbackHelper.TokensPaleta.LeaderboardBg };
            yield return new object[] { UiFeedbackHelper.TokensPaleta.LeaderboardSilver, UiFeedbackHelper.TokensPaleta.LeaderboardBg };
            yield return new object[] { UiFeedbackHelper.TokensPaleta.LeaderboardBronze, UiFeedbackHelper.TokensPaleta.LeaderboardBg };
        }

        public static IEnumerable<object[]> ParesContrasteControlesYFocoWcagAA()
        {
            // Bordes de foco, íconos e indicadores de estado (>= 3.0:1)
            yield return new object[] { UiFeedbackHelper.TokensPaleta.SignalCobalt, UiFeedbackHelper.TokensPaleta.ChalkSurface };
            yield return new object[] { UiFeedbackHelper.TokensPaleta.SignalCobalt, UiFeedbackHelper.TokensPaleta.PureWhite };
            yield return new object[] { UiFeedbackHelper.TokensPaleta.FinishEmerald, UiFeedbackHelper.TokensPaleta.ChalkSurface };
            yield return new object[] { UiFeedbackHelper.TokensPaleta.PenaltyCrimson, UiFeedbackHelper.TokensPaleta.ChalkSurface };
            yield return new object[] { UiFeedbackHelper.TokensPaleta.CautionAmber, UiFeedbackHelper.TokensPaleta.ChalkSurface };
        }

        [Theory]
        [MemberData(nameof(ParesContrasteTextoNormalWcagAA))]
        public void CumpleContrasteWcagAA_ParesTextoNormalDePaleta_SuperanCuatroPuntoCinco(string primerPlano, string fondo)
        {
            double ratio = UiFeedbackHelper.CalcularRelacionContraste(primerPlano, fondo);

            Assert.True(ratio >= 4.5, $"El par {primerPlano} sobre {fondo} obtuvo contraste {ratio:F2}:1 (< 4.5:1).");
            Assert.True(UiFeedbackHelper.CumpleContrasteWcagAA(primerPlano, fondo, esTextoGrandeOElementoGrafico: false));
        }

        [Theory]
        [MemberData(nameof(ParesContrasteControlesYFocoWcagAA))]
        public void CumpleContrasteWcagAA_BordesFocoEIconosDePaleta_SuperanTresPuntoCero(string primerPlano, string fondo)
        {
            double ratio = UiFeedbackHelper.CalcularRelacionContraste(primerPlano, fondo);

            Assert.True(ratio >= 3.0, $"El par {primerPlano} sobre {fondo} obtuvo contraste {ratio:F2}:1 (< 3.0:1).");
            Assert.True(UiFeedbackHelper.CumpleContrasteWcagAA(primerPlano, fondo, esTextoGrandeOElementoGrafico: true));
        }

        [Fact]
        public void CalcularRelacionContraste_BlancoYNegro_EsVeintiunoAUno()
        {
            double ratio = UiFeedbackHelper.CalcularRelacionContraste("#000000", "#FFFFFF");

            Assert.InRange(ratio, 20.99, 21.01);
        }

        [Fact]
        public void CumpleContrasteWcagAA_ParInsuficiente_RetornaFalse()
        {
            bool cumple = UiFeedbackHelper.CumpleContrasteWcagAA("#94A3B8", "#F8FAFC", esTextoGrandeOElementoGrafico: false);

            Assert.False(cumple);
        }

        [Theory]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData("GGGGGG")]
        [InlineData("#12345")]
        public void CalcularRelacionContraste_HexInvalido_LanzaArgumentException(string hexInvalido)
        {
            Assert.Throws<ArgumentException>(() => UiFeedbackHelper.CalcularRelacionContraste(hexInvalido, "#FFFFFF"));
        }

        [Fact]
        public void ObtenerDuracionTransicionMs_ConAnimacionesHabilitadas_RetornaEntre150Y250Ms()
        {
            int duracion = UiFeedbackHelper.ObtenerDuracionTransicionMs(animacionesHabilitadas: true);

            Assert.InRange(duracion, 150, 250);
        }

        [Theory]
        [InlineData(50, 150)]
        [InlineData(180, 180)]
        [InlineData(600, 250)]
        public void ObtenerDuracionTransicionMs_ConAnimacionesHabilitadasYValorPersonalizado_AcotaEntre150Y250Ms(
            int solicitadoMs,
            int esperadoMs)
        {
            int duracion = UiFeedbackHelper.ObtenerDuracionTransicionMs(animacionesHabilitadas: true, duracionBaseMs: solicitadoMs);

            Assert.Equal(esperadoMs, duracion);
        }

        [Fact]
        public void ObtenerDuracionTransicionMs_ConMovimientoReducidoActivo_RetornaCeroMs()
        {
            int duracion = UiFeedbackHelper.ObtenerDuracionTransicionMs(animacionesHabilitadas: false, duracionBaseMs: 200);

            Assert.Equal(0, duracion);
            Assert.Equal(TimeSpan.Zero, UiFeedbackHelper.ObtenerDuracionTransicion(animacionesHabilitadas: false));
        }

        [Theory]
        [InlineData(NivelFeedback.Exito)]
        [InlineData(NivelFeedback.Informacion)]
        [InlineData(NivelFeedback.Advertencia)]
        public void CrearConfiguracionAviso_ConfirmacionOInformativo_ConfiguraAutoOcultamientoEntre3Y4Segundos(NivelFeedback nivel)
        {
            ConfiguracionAvisoFeedback aviso = UiFeedbackHelper.CrearConfiguracionAviso(nivel, "Operación completada.");

            Assert.Equal(nivel, aviso.Nivel);
            Assert.Equal("Operación completada.", aviso.Mensaje);
            Assert.False(aviso.RequiereCierreManual);
            Assert.InRange(aviso.DuracionAutoOcultamientoMs, 3000, 4000);
            Assert.True(UiFeedbackHelper.CumpleContrasteWcagAA(aviso.ColorTextoHex, aviso.ColorFondoHex));
        }

        [Fact]
        public void CrearConfiguracionAviso_NivelErrorSinDuracionExplicita_EsPersistenteConCierreManual()
        {
            ConfiguracionAvisoFeedback aviso = UiFeedbackHelper.CrearConfiguracionAviso(
                NivelFeedback.Error,
                "El dorsal 105 ya está asignado a otro corredor.");

            Assert.Equal(NivelFeedback.Error, aviso.Nivel);
            Assert.True(aviso.RequiereCierreManual);
            Assert.Equal(0, aviso.DuracionAutoOcultamientoMs);
            Assert.True(UiFeedbackHelper.CumpleContrasteWcagAA(aviso.ColorTextoHex, aviso.ColorFondoHex));
        }

        [Theory]
        [InlineData("")]
        [InlineData("   ")]
        public void CrearConfiguracionAviso_MensajeVacio_LanzaArgumentException(string mensajeVacio)
        {
            Assert.Throws<ArgumentException>(() =>
                UiFeedbackHelper.CrearConfiguracionAviso(NivelFeedback.Exito, mensajeVacio));
        }
    }

    [Collection("IsolatedStorage")]
    public class MainWindowUiFeedbackTests
    {
        [Fact]
        public async Task MainWindow_NavegacionActiva_ResaltaBotonActivoYActualizaSeccion()
        {
            using var env = new TestEnvironmentFixture();
            await DatabaseMaintenanceHelper.InicializarYMigrarBaseDatosAsync(env.CrearFabricaContexto());

            Carrera carrera;
            await using (var db = env.CrearContexto())
            {
                carrera = new Carrera { Nombre = "Carrera Nav", Fecha = DateTime.Today };
                db.Carreras.Add(carrera);
                await db.SaveChangesAsync();
            }

            try
            {
                AppState.CambiarCarreraActiva(carrera);

                string? tagInicioInicial = null;
                string? tagCronoInicial = null;
                string? seccionInicial = null;

                string? tagInicioEnTiming = null;
                string? tagCronoEnTiming = null;
                string? seccionEnTiming = null;

                string? tagCorredores = null;
                string? seccionCorredores = null;

                string? tagHits = null;
                string? seccionHits = null;

                await RunOnStaThreadAsync(async dispatcher =>
                {
                    MainWindow mainWindow = CrearMainWindowOculta();
                    mainWindow.Show();
                    await EsperarDespachoAsync(dispatcher);

                    var btnInicio = (Button)mainWindow.FindName("BtnNavInicio");
                    var btnCrono = (Button)mainWindow.FindName("BtnNavCronometraje");
                    var btnCorredores = (Button)mainWindow.FindName("BtnNavCorredores");
                    var btnHits = (Button)mainWindow.FindName("BtnNavHits");
                    var txtSeccion = (TextBlock)mainWindow.FindName("TxtSeccionActiva");

                    tagInicioInicial = btnInicio.Tag?.ToString();
                    tagCronoInicial = btnCrono.Tag?.ToString();
                    seccionInicial = txtSeccion.Text;

                    mainWindow.NavegarAVista(new TimingView());
                    await EsperarDespachoAsync(dispatcher);
                    tagInicioEnTiming = btnInicio.Tag?.ToString();
                    tagCronoEnTiming = btnCrono.Tag?.ToString();
                    seccionEnTiming = txtSeccion.Text;

                    mainWindow.NavegarAVista(new CorredoresView());
                    await EsperarDespachoAsync(dispatcher);
                    tagCorredores = btnCorredores.Tag?.ToString();
                    seccionCorredores = txtSeccion.Text;

                    mainWindow.NavegarAVista(new HitsView());
                    await EsperarDespachoAsync(dispatcher);
                    tagHits = btnHits.Tag?.ToString();
                    seccionHits = txtSeccion.Text;

                    mainWindow.Close();
                    await EsperarDespachoAsync(dispatcher);
                });

                Assert.Equal("Active", tagInicioInicial);
                Assert.Null(tagCronoInicial);
                Assert.Equal("Inicio", seccionInicial);

                Assert.Null(tagInicioEnTiming);
                Assert.Equal("Active", tagCronoEnTiming);
                Assert.Equal("Panel de Cronometraje", seccionEnTiming);

                Assert.Equal("Active", tagCorredores);
                Assert.Equal("Gestión de Corredores", seccionCorredores);

                Assert.Equal("Active", tagHits);
                Assert.Equal("Configuración de Hits", seccionHits);
            }
            finally
            {
                AppState.CambiarCarreraActiva(null);
            }
        }

        [Fact]
        public async Task MainWindow_ToastGlobal_AutoOcultaEnExitoRequiereCierreEnErrorYLimpiaTimerAlCerrar()
        {
            using var env = new TestEnvironmentFixture();
            await DatabaseMaintenanceHelper.InicializarYMigrarBaseDatosAsync(env.CrearFabricaContexto());

            Visibility visExito = Visibility.Collapsed;
            string? msgExito = null;
            bool timerActivoEnExito = false;
            double intervaloExitoMs = 0;

            Visibility visError = Visibility.Collapsed;
            string? msgError = null;
            bool timerActivoEnError = true;

            Visibility visTrasCerrarToast = Visibility.Visible;
            DispatcherTimer? timerTrasCerrarVentana = null;

            await RunOnStaThreadAsync(async dispatcher =>
            {
                MainWindow mainWindow = CrearMainWindowOculta();
                mainWindow.Show();
                await EsperarDespachoAsync(dispatcher);

                var toastBorder = (Border)mainWindow.FindName("ToastGlobalBorder");
                var txtMensaje = (TextBlock)mainWindow.FindName("TxtToastMensaje");
                var btnCerrar = (Button)mainWindow.FindName("BtnCerrarToast");

                mainWindow.MostrarNotificacionGlobal(NivelFeedback.Exito, "Carrera guardada correctamente.");
                await EsperarDespachoAsync(dispatcher);

                DispatcherTimer? timerExito = ObtenerToastTimer(mainWindow);
                visExito = toastBorder.Visibility;
                msgExito = txtMensaje.Text;
                timerActivoEnExito = timerExito?.IsEnabled ?? false;
                intervaloExitoMs = timerExito?.Interval.TotalMilliseconds ?? 0;

                mainWindow.MostrarNotificacionGlobal(NivelFeedback.Error, "Error de conexión con el servidor.");
                await EsperarDespachoAsync(dispatcher);

                DispatcherTimer? timerError = ObtenerToastTimer(mainWindow);
                visError = toastBorder.Visibility;
                msgError = txtMensaje.Text;
                timerActivoEnError = timerError?.IsEnabled ?? false;

                btnCerrar.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                await EsperarCondicionAsync(
                    dispatcher,
                    () => toastBorder.Visibility == Visibility.Collapsed,
                    TimeSpan.FromSeconds(1));
                visTrasCerrarToast = toastBorder.Visibility;

                mainWindow.MostrarNotificacionGlobal(NivelFeedback.Informacion, "Aviso temporal.");
                await EsperarDespachoAsync(dispatcher);

                mainWindow.Close();
                await EsperarDespachoAsync(dispatcher);
                timerTrasCerrarVentana = ObtenerToastTimer(mainWindow);
            });

            Assert.Equal(Visibility.Visible, visExito);
            Assert.Equal("Carrera guardada correctamente.", msgExito);
            Assert.True(timerActivoEnExito);
            Assert.InRange(intervaloExitoMs, UiFeedbackHelper.DuracionToastMinMs, UiFeedbackHelper.DuracionToastMaxMs);

            Assert.Equal(Visibility.Visible, visError);
            Assert.Equal("Error de conexión con el servidor.", msgError);
            Assert.False(timerActivoEnError);

            Assert.Equal(Visibility.Collapsed, visTrasCerrarToast);
            Assert.Null(timerTrasCerrarVentana);
        }

        [Fact]
        public async Task MainWindow_NavegarSinCarreraActiva_MuestraToastNoBloqueanteSinCambiarVista()
        {
            using var env = new TestEnvironmentFixture();
            await DatabaseMaintenanceHelper.InicializarYMigrarBaseDatosAsync(env.CrearFabricaContexto());

            AppState.CambiarCarreraActiva(null);

            bool sigueEnWelcomeTrasCorredores = false;
            Visibility visToastCorredores = Visibility.Collapsed;
            string? msgCorredores = null;

            bool sigueEnWelcomeTrasHits = false;
            Visibility visToastHits = Visibility.Collapsed;
            string? msgHits = null;

            await RunOnStaThreadAsync(async dispatcher =>
            {
                MainWindow mainWindow = CrearMainWindowOculta();
                mainWindow.Show();
                await EsperarDespachoAsync(dispatcher);

                var contenedor = (ContentControl)mainWindow.FindName("ContenedorPrincipal");
                var btnCorredores = (Button)mainWindow.FindName("BtnNavCorredores");
                var btnHits = (Button)mainWindow.FindName("BtnNavHits");
                var toastBorder = (Border)mainWindow.FindName("ToastGlobalBorder");
                var txtMensaje = (TextBlock)mainWindow.FindName("TxtToastMensaje");

                btnCorredores.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                await EsperarDespachoAsync(dispatcher);
                sigueEnWelcomeTrasCorredores = contenedor.Content is WelcomeView;
                visToastCorredores = toastBorder.Visibility;
                msgCorredores = txtMensaje.Text;

                btnHits.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                await EsperarDespachoAsync(dispatcher);
                sigueEnWelcomeTrasHits = contenedor.Content is WelcomeView;
                visToastHits = toastBorder.Visibility;
                msgHits = txtMensaje.Text;

                mainWindow.Close();
                await EsperarDespachoAsync(dispatcher);
            });

            Assert.True(sigueEnWelcomeTrasCorredores);
            Assert.Equal(Visibility.Visible, visToastCorredores);
            Assert.Contains("carrera", msgCorredores!, StringComparison.OrdinalIgnoreCase);

            Assert.True(sigueEnWelcomeTrasHits);
            Assert.Equal(Visibility.Visible, visToastHits);
            Assert.Contains("carrera", msgHits!, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public async Task WelcomeView_ValidacionEnLinea_SeleccionYBannerSinMessageBox()
        {
            Visibility visErrorInicial = Visibility.Visible;
            Visibility visErrorTrasSubmitVacio = Visibility.Collapsed;
            string? msgErrorNombre = null;
            Visibility visErrorTrasEscribir = Visibility.Visible;
            bool abrirHabilitadoSinSeleccion = true;
            bool abrirHabilitadoConSeleccion = false;
            string? resumenConSeleccion = null;
            Visibility visBannerTrasCopiar = Visibility.Collapsed;
            bool timerLimpiadoAlDescargar = false;

            await RunOnStaThreadAsync(async dispatcher =>
            {
                var welcome = new WelcomeView();
                var host = new Window
                {
                    Content = welcome,
                    WindowState = WindowState.Normal,
                    Width = 800,
                    Height = 600,
                    Left = -32000,
                    Top = -32000,
                    ShowActivated = false,
                    ShowInTaskbar = false,
                    Opacity = 0
                };
                host.Show();
                await EsperarDespachoAsync(dispatcher);

                var inputNombre = (TextBox)welcome.FindName("InputNombreCarrera");
                var txtErrorNombre = (TextBlock)welcome.FindName("TxtErrorNombreCarrera");
                var btnCrear = (Button)welcome.FindName("BtnCrearCarrera");
                var listaCarreras = (ListView)welcome.FindName("ListaCarreras");
                var btnAbrir = (Button)welcome.FindName("BtnAbrirCarrera");
                var txtResumen = (TextBlock)welcome.FindName("TxtResumenSeleccion");
                var txtToken = (TextBox)welcome.FindName("TxtTokenSesion");
                var btnCopiarToken = (Button)welcome.FindName("BtnCopiarToken");
                var banner = (Border)welcome.FindName("BannerFeedbackWelcome");

                visErrorInicial = txtErrorNombre.Visibility;
                abrirHabilitadoSinSeleccion = btnAbrir.IsEnabled;

                // 1. Validación en línea al enviar nombre vacío
                inputNombre.Text = string.Empty;
                btnCrear.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                await EsperarDespachoAsync(dispatcher);
                visErrorTrasSubmitVacio = txtErrorNombre.Visibility;
                msgErrorNombre = txtErrorNombre.Text;

                // 2. Limpieza automática al editar
                inputNombre.Text = "Maratón Urbano";
                await EsperarDespachoAsync(dispatcher);
                visErrorTrasEscribir = txtErrorNombre.Visibility;

                // 3. Selección de carrera habilita BtnAbrirCarrera y actualiza resumen
                var carrera = new Carrera { ID_Carrera = 42, Nombre = "Carrera Nocturna", Fecha = DateTime.Today };
                listaCarreras.ItemsSource = new List<Carrera> { carrera };
                listaCarreras.SelectedItem = carrera;
                await EsperarDespachoAsync(dispatcher);
                abrirHabilitadoConSeleccion = btnAbrir.IsEnabled;
                resumenConSeleccion = txtResumen.Text;

                // 4. Copiar token muestra banner no bloqueante y Unloaded limpia el timer
                txtToken.Text = "TOKEN1234567";
                btnCopiarToken.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                await EsperarDespachoAsync(dispatcher);
                visBannerTrasCopiar = banner.Visibility;

                host.Close();
                await EsperarDespachoAsync(dispatcher);
                timerLimpiadoAlDescargar = ObtenerWelcomeFeedbackTimer(welcome) == null;
            });

            Assert.Equal(Visibility.Collapsed, visErrorInicial);
            Assert.Equal(Visibility.Visible, visErrorTrasSubmitVacio);
            Assert.False(string.IsNullOrWhiteSpace(msgErrorNombre));
            Assert.Equal(Visibility.Collapsed, visErrorTrasEscribir);
            Assert.False(abrirHabilitadoSinSeleccion);
            Assert.True(abrirHabilitadoConSeleccion);
            Assert.Contains("Carrera Nocturna", resumenConSeleccion!, StringComparison.Ordinal);
            Assert.Equal(Visibility.Visible, visBannerTrasCopiar);
            Assert.True(timerLimpiadoAlDescargar);
        }

        [Fact]
        public async Task CorredoresView_ValidacionEnLineaEstadoVacioYUnloaded_FuncionanSinMessageBox()
        {
            using var env = new TestEnvironmentFixture();
            await DatabaseMaintenanceHelper.InicializarYMigrarBaseDatosAsync(env.CrearFabricaContexto());

            Carrera? carrera = null;
            await using (var db = env.CrearContexto())
            {
                carrera = new Carrera { Nombre = "Carrera T6 Corredores", Fecha = DateTime.Today };
                db.Carreras.Add(carrera);
                await db.SaveChangesAsync();
            }

            Visibility visErrorDorsalInicial = Visibility.Visible;
            Visibility visErrorDorsalTrasSubmitInvalido = Visibility.Collapsed;
            Visibility visErrorDorsalTrasEscribir = Visibility.Visible;
            Visibility visEstadoVacioSinCatalogos = Visibility.Collapsed;
            string? textoBotonEstadoVacio = null;
            string? etiquetaDns = null;
            string? etiquetaDnf = null;
            bool timerLimpiadoAlDescargar = false;

            try
            {
                AppState.CambiarCarreraActiva(carrera);

                await RunOnStaThreadAsync(async dispatcher =>
                {
                    var vista = new CorredoresView();
                    var host = new Window
                    {
                        Content = vista,
                        Width = 900,
                        Height = 600,
                        Left = -32000,
                        Top = -32000,
                        ShowActivated = false,
                        ShowInTaskbar = false,
                        Opacity = 0
                    };
                    host.Show();
                    await EsperarDespachoAsync(dispatcher);

                    var inputDorsal = (TextBox)vista.FindName("InputDorsal");
                    var txtErrorDorsal = (TextBlock)vista.FindName("TxtErrorDorsal");
                    var btnGuardar = (Button)vista.FindName("BtnAccionCorredor");
                    var panelVacio = (Border)vista.FindName("PanelEstadoVacioCorredores");
                    var btnEstadoVacio = (Button)vista.FindName("BtnAccionEstadoVacio");

                    await EsperarCondicionAsync(
                        dispatcher,
                        () => panelVacio.Visibility == Visibility.Visible,
                        TimeSpan.FromSeconds(3));

                    visErrorDorsalInicial = txtErrorDorsal.Visibility;
                    visEstadoVacioSinCatalogos = panelVacio.Visibility;
                    textoBotonEstadoVacio = btnEstadoVacio.Content?.ToString();

                    inputDorsal.Text = "abc";
                    btnGuardar.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                    await EsperarDespachoAsync(dispatcher);
                    visErrorDorsalTrasSubmitInvalido = txtErrorDorsal.Visibility;

                    inputDorsal.Text = "101";
                    await EsperarDespachoAsync(dispatcher);
                    visErrorDorsalTrasEscribir = txtErrorDorsal.Visibility;

                    var converter = new EstadoCorredorDescripcionConverter();
                    etiquetaDns = converter.Convert(EstadoCorredor.DNS, typeof(string), null!, System.Globalization.CultureInfo.InvariantCulture)?.ToString();
                    etiquetaDnf = converter.Convert(EstadoCorredor.DNF, typeof(string), null!, System.Globalization.CultureInfo.InvariantCulture)?.ToString();

                    host.Close();
                    await EsperarDespachoAsync(dispatcher);
                    timerLimpiadoAlDescargar = ObtenerFeedbackTimer(vista) == null;
                });
            }
            finally
            {
                AppState.CambiarCarreraActiva(null);
            }

            Assert.Equal(Visibility.Collapsed, visErrorDorsalInicial);
            Assert.Equal(Visibility.Visible, visErrorDorsalTrasSubmitInvalido);
            Assert.Equal(Visibility.Collapsed, visErrorDorsalTrasEscribir);
            Assert.Equal(Visibility.Visible, visEstadoVacioSinCatalogos);
            Assert.Equal("Ir a Hits y Categorías", textoBotonEstadoVacio);
            Assert.Equal("DNS (No salió)", etiquetaDns);
            Assert.Equal("DNF (No finalizó)", etiquetaDnf);
            Assert.True(timerLimpiadoAlDescargar);
        }

        [Fact]
        public async Task HitsView_ValidacionEnLineaEstadoVacioYUnloaded_FuncionanSinMessageBox()
        {
            using var env = new TestEnvironmentFixture();
            await DatabaseMaintenanceHelper.InicializarYMigrarBaseDatosAsync(env.CrearFabricaContexto());

            Carrera? carrera = null;
            await using (var db = env.CrearContexto())
            {
                carrera = new Carrera { Nombre = "Carrera T6 Hits", Fecha = DateTime.Today };
                db.Carreras.Add(carrera);
                await db.SaveChangesAsync();
            }

            Visibility visErrorHitInicial = Visibility.Visible;
            Visibility visErrorHitTrasSubmitVacio = Visibility.Collapsed;
            Visibility visErrorHitTrasEscribir = Visibility.Visible;
            Visibility visErrorCatTrasSubmitVacio = Visibility.Collapsed;
            Visibility visEstadoVacioHits = Visibility.Collapsed;
            Visibility visEstadoVacioCategorias = Visibility.Collapsed;
            bool timerLimpiadoAlDescargar = false;

            try
            {
                AppState.CambiarCarreraActiva(carrera);

                await RunOnStaThreadAsync(async dispatcher =>
                {
                    var vista = new HitsView();
                    var host = new Window
                    {
                        Content = vista,
                        Width = 900,
                        Height = 600,
                        Left = -32000,
                        Top = -32000,
                        ShowActivated = false,
                        ShowInTaskbar = false,
                        Opacity = 0
                    };
                    host.Show();
                    await EsperarDespachoAsync(dispatcher);

                    var inputHit = (TextBox)vista.FindName("InputNombreHit");
                    var txtErrorHit = (TextBlock)vista.FindName("TxtErrorNombreHit");
                    var btnGuardarHit = (Button)vista.FindName("BtnAccionHit");
                    var txtErrorCat = (TextBlock)vista.FindName("TxtErrorNombreCategoria");
                    var btnGuardarCat = (Button)vista.FindName("BtnAccionCategoria");
                    var panelVacioHits = (Border)vista.FindName("PanelEstadoVacioHits");
                    var panelVacioCat = (Border)vista.FindName("PanelEstadoVacioCategorias");

                    await EsperarCondicionAsync(
                        dispatcher,
                        () => panelVacioHits.Visibility == Visibility.Visible && panelVacioCat.Visibility == Visibility.Visible,
                        TimeSpan.FromSeconds(3));

                    visErrorHitInicial = txtErrorHit.Visibility;
                    visEstadoVacioHits = panelVacioHits.Visibility;
                    visEstadoVacioCategorias = panelVacioCat.Visibility;

                    btnGuardarHit.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                    await EsperarDespachoAsync(dispatcher);
                    visErrorHitTrasSubmitVacio = txtErrorHit.Visibility;

                    inputHit.Text = "Hit 1";
                    await EsperarDespachoAsync(dispatcher);
                    visErrorHitTrasEscribir = txtErrorHit.Visibility;

                    btnGuardarCat.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                    await EsperarDespachoAsync(dispatcher);
                    visErrorCatTrasSubmitVacio = txtErrorCat.Visibility;

                    host.Close();
                    await EsperarDespachoAsync(dispatcher);
                    timerLimpiadoAlDescargar = ObtenerFeedbackTimer(vista) == null;
                });
            }
            finally
            {
                AppState.CambiarCarreraActiva(null);
            }

            Assert.Equal(Visibility.Collapsed, visErrorHitInicial);
            Assert.Equal(Visibility.Visible, visErrorHitTrasSubmitVacio);
            Assert.Equal(Visibility.Collapsed, visErrorHitTrasEscribir);
            Assert.Equal(Visibility.Visible, visErrorCatTrasSubmitVacio);
            Assert.Equal(Visibility.Visible, visEstadoVacioHits);
            Assert.Equal(Visibility.Visible, visEstadoVacioCategorias);
            Assert.True(timerLimpiadoAlDescargar);
        }

        private static DispatcherTimer? ObtenerFeedbackTimer(object vista)
        {
            FieldInfo? campo = vista.GetType().GetField("_feedbackTimer", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(campo);
            return campo!.GetValue(vista) as DispatcherTimer;
        }

        private static DispatcherTimer? ObtenerWelcomeFeedbackTimer(WelcomeView welcome)
        {
            FieldInfo? campo = typeof(WelcomeView).GetField("_feedbackTimer", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(campo);
            return campo!.GetValue(welcome) as DispatcherTimer;
        }

        [Fact]
        public async Task TimingView_SinApplicationCurrent_MuestraEstadosVaciosYValidacionEnLineaSinModal()
        {
            using var env = new TestEnvironmentFixture();
            await DatabaseMaintenanceHelper.InicializarYMigrarBaseDatosAsync(env.CrearFabricaContexto());

            AppState.CambiarCarreraActiva(null);
            AppState.FotosEnEsperaGlobal.Clear();
            AppState.RegistrosExitososGlobal.Clear();

            Visibility hitsVacioVisibilidad = Visibility.Collapsed;
            Visibility fotosVacioVisibilidad = Visibility.Collapsed;
            Visibility visorVacioVisibilidad = Visibility.Collapsed;
            Visibility errorDorsalVisibilidad = Visibility.Collapsed;
            string? textoErrorDorsal = null;
            bool feedbackTimerNuloTrasUnload = false;

            await RunOnStaThreadAsync(async dispatcher =>
            {
                var view = new TimingView();
                var host = new Window
                {
                    Content = view,
                    Width = 1000,
                    Height = 700,
                    Left = -32000,
                    Top = -32000,
                    ShowActivated = false,
                    ShowInTaskbar = false,
                    Opacity = 0
                };

                host.Show();
                await EsperarDespachoAsync(dispatcher);

                var panelHitsVacio = (Border)view.FindName("PanelEstadoVacioHits");
                var panelFotosVacio = (Border)view.FindName("PanelEstadoVacioFotos");
                var panelVisorVacio = (StackPanel)view.FindName("PanelVisorVacio");
                var inputDorsal = (TextBox)view.FindName("InputDorsal");
                var btnAsignar = (Button)view.FindName("BtnAsignar");
                var txtErrorDorsal = (TextBlock)view.FindName("TxtErrorDorsalTiming");

                hitsVacioVisibilidad = panelHitsVacio.Visibility;
                fotosVacioVisibilidad = panelFotosVacio.Visibility;
                visorVacioVisibilidad = panelVisorVacio.Visibility;

                inputDorsal.Text = "abc";
                btnAsignar.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                await EsperarDespachoAsync(dispatcher);

                errorDorsalVisibilidad = txtErrorDorsal.Visibility;
                textoErrorDorsal = txtErrorDorsal.Text;

                host.Close();
                await EsperarDespachoAsync(dispatcher);

                FieldInfo? campoFeedbackTimer = typeof(TimingView).GetField("_feedbackTimer", BindingFlags.Instance | BindingFlags.NonPublic);
                feedbackTimerNuloTrasUnload = campoFeedbackTimer?.GetValue(view) == null;
            });

            Assert.Equal(Visibility.Visible, hitsVacioVisibilidad);
            Assert.Equal(Visibility.Visible, fotosVacioVisibilidad);
            Assert.Equal(Visibility.Visible, visorVacioVisibilidad);
            Assert.Equal(Visibility.Visible, errorDorsalVisibilidad);
            Assert.False(string.IsNullOrWhiteSpace(textoErrorDorsal));
            Assert.True(feedbackTimerNuloTrasUnload);
        }

        private static MainWindow CrearMainWindowOculta() => new()
        {
            WindowState = WindowState.Normal,
            Width = 320,
            Height = 200,
            Left = -32000,
            Top = -32000,
            ShowActivated = false,
            ShowInTaskbar = false,
            Opacity = 0
        };

        private static DispatcherTimer? ObtenerToastTimer(MainWindow mainWindow)
        {
            FieldInfo? campo = typeof(MainWindow).GetField("_toastTimer", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(campo);
            return campo!.GetValue(mainWindow) as DispatcherTimer;
        }

        private static Task RunOnStaThreadAsync(Func<Dispatcher, Task> action)
        {
            var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

            var thread = new Thread(() =>
            {
                SynchronizationContext.SetSynchronizationContext(
                    new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));

                _ = ExecuteAsync();
                Dispatcher.Run();

                async Task ExecuteAsync()
                {
                    try
                    {
                        await action(Dispatcher.CurrentDispatcher);
                        tcs.TrySetResult();
                    }
                    catch (Exception ex)
                    {
                        tcs.TrySetException(ex);
                    }
                    finally
                    {
                        Dispatcher.CurrentDispatcher.InvokeShutdown();
                    }
                }
            });

            thread.SetApartmentState(ApartmentState.STA);
            thread.IsBackground = true;
            thread.Start();

            return tcs.Task;
        }

        private static Task EsperarDespachoAsync(Dispatcher dispatcher)
        {
            var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => tcs.TrySetResult()));
            return tcs.Task;
        }

        private static async Task EsperarCondicionAsync(
            Dispatcher dispatcher,
            Func<bool> condicion,
            TimeSpan timeout)
        {
            DateTime limite = DateTime.UtcNow + timeout;
            while (!condicion())
            {
                if (DateTime.UtcNow >= limite)
                {
                    break;
                }

                await Task.Delay(25);
                await EsperarDespachoAsync(dispatcher);
            }
        }
    }
}
