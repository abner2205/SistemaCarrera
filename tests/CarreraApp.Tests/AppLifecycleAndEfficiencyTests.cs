using System;
using System.Collections.Generic;
using System.Data.Common;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using CarreraApp.Data;
using CarreraApp.Helpers;
using CarreraApp.Models;
using CarreraApp.Server;
using CarreraApp.UI;
using CarreraApp.UI.Views;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace CarreraApp.Tests
{
    [Collection("IsolatedStorage")]
    public class AppLifecycleAndEfficiencyTests
    {
        [Fact]
        public async Task Rf03_AusenciaGenerarDatosDePruebaBD_YCarreraVaciaPermaneceSinDatosFicticios()
        {
            // 1. Verificar ausencia total de GenerarDatosDePruebaBD en TimingView y en todo el ensamblado
            MethodInfo[] metodosTiming = typeof(TimingView).GetMethods(
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static);
            Assert.DoesNotContain(metodosTiming, m => m.Name.Contains("GenerarDatosDePruebaBD", StringComparison.OrdinalIgnoreCase));

            foreach (Type tipo in typeof(App).Assembly.GetTypes())
            {
                MethodInfo[] metodos = tipo.GetMethods(
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly);
                Assert.DoesNotContain(metodos, m => string.Equals(m.Name, "GenerarDatosDePruebaBD", StringComparison.Ordinal));
            }

            // 2. Crear y abrir una carrera vacía en entorno aislado y cargar TimingView sin Application.Current
            using var env = new TestEnvironmentFixture();
            await DatabaseMaintenanceHelper.InicializarYMigrarBaseDatosAsync(env.CrearFabricaContexto());

            var service = new RaceDataService(env.CrearFabricaContexto());
            OperacionResultado<Carrera> creada = await service.CrearOActualizarCarreraAsync(
                null,
                "Carrera Vacia Sin Seed",
                DateTime.Today);
            Assert.True(creada.Exito);
            Assert.NotNull(creada.Entidad);

            try
            {
                Assert.Null(Application.Current);
                AppState.CambiarCarreraActiva(creada.Entidad);

                int cantidadHitsVista = -1;
                int cantidadCategoriasVista = -1;

                await RunOnStaThreadAsync(async dispatcher =>
                {
                    var timingView = new TimingView();
                    var hostWindow = CrearVentanaHostOculta(timingView);
                    hostWindow.Show();

                    await EsperarDespachoAsync(dispatcher);

                    var comboCategorias = (ComboBox)timingView.FindName("ComboCategoriasPizarra");
                    cantidadHitsVista = timingView.ListaDeHits.Count;
                    cantidadCategoriasVista = comboCategorias.Items.Count;

                    hostWindow.Close();
                    await EsperarDespachoAsync(dispatcher);
                });

                Assert.Equal(0, cantidadHitsVista);
                Assert.Equal(0, cantidadCategoriasVista);

                await using var db = env.CrearContexto();
                Assert.Equal(1, await db.Carreras.CountAsync());
                Assert.Equal(0, await db.Categorias.CountAsync());
                Assert.Equal(0, await db.Hits.CountAsync());
                Assert.Equal(0, await db.Corredores.CountAsync());
                Assert.Equal(0, await db.Tiempos.CountAsync());
                Assert.Null(Application.Current);
            }
            finally
            {
                WebServer.LimpiarColaPendientes();
                AppState.CambiarCarreraActiva(null);
            }
        }

        [Fact]
        public async Task Rf05Y13_TokenEstableConQr_YCambiarCarreraActivaLimpiaColasPizarraYTemporales()
        {
            using var env = new TestEnvironmentFixture();
            await DatabaseMaintenanceHelper.InicializarYMigrarBaseDatosAsync(env.CrearFabricaContexto());

            Carrera carrera1;
            Carrera carrera2;
            Categoria cat1;
            await using (var db = env.CrearContexto())
            {
                carrera1 = new Carrera { Nombre = "Carrera Alfa", Fecha = DateTime.Today };
                carrera2 = new Carrera { Nombre = "Carrera Beta", Fecha = DateTime.Today.AddDays(1) };
                db.Carreras.AddRange(carrera1, carrera2);
                await db.SaveChangesAsync();

                cat1 = new Categoria { Nombre = "Categoria Alfa", ID_Carrera = carrera1.ID_Carrera };
                db.Categorias.Add(cat1);
                await db.SaveChangesAsync();
            }

            await using var server = await WebServer.IniciarEnSegundoPlanoAsync(new WebServerOptions
            {
                DireccionEscucha = IPAddress.Loopback,
                PuertoHttp = 0,
                HabilitarHttps = false,
                RegenerarTokenAlIniciar = true,
                DbContextFactory = env.CrearFabricaContexto()
            });

            try
            {
                string tokenSesion = WebServer.TokenSesionActual;
                Assert.InRange(tokenSesion.Length, 12, 16);
                Assert.Matches("^[A-Za-z0-9_-]{12,16}$", tokenSesion);

                // Verificar WelcomeView genera QR y muestra URL con token estable entre cargas de vista
                string? url1 = null;
                string? tokenUi1 = null;
                string? tokenUi2 = null;
                bool qrCongelado = false;

                await RunOnStaThreadAsync(async dispatcher =>
                {
                    var welcome1 = new WelcomeView();
                    var host = CrearVentanaHostOculta(welcome1);
                    host.Show();

                    var txtUrl = (TextBlock)welcome1.FindName("TxtUrlServer");
                    var txtToken = (TextBox)welcome1.FindName("TxtTokenSesion");
                    var qrImage = (Image)welcome1.FindName("QrImage");

                    await EsperarCondicionAsync(
                        dispatcher,
                        () => qrImage.Source != null && !string.IsNullOrWhiteSpace(txtUrl.Text),
                        TimeSpan.FromSeconds(5));

                    url1 = txtUrl.Text;
                    tokenUi1 = txtToken.Text;
                    qrCongelado = qrImage.Source is BitmapSource bmp && bmp.IsFrozen;

                    // Navegar a otra vista y volver a WelcomeView: el token no cambia
                    host.Content = new TimingView();
                    await EsperarDespachoAsync(dispatcher);

                    var welcome2 = new WelcomeView();
                    host.Content = welcome2;
                    var txtToken2 = (TextBox)welcome2.FindName("TxtTokenSesion");
                    await EsperarCondicionAsync(
                        dispatcher,
                        () => !string.IsNullOrWhiteSpace(txtToken2.Text),
                        TimeSpan.FromSeconds(5));
                    tokenUi2 = txtToken2.Text;

                    host.Close();
                    await EsperarDespachoAsync(dispatcher);
                });

                Assert.Equal(tokenSesion, tokenUi1);
                Assert.Equal(tokenSesion, tokenUi2);
                Assert.True(qrCongelado);
                Assert.NotNull(url1);
                Assert.StartsWith("https://", url1!, StringComparison.OrdinalIgnoreCase);
                Assert.Contains($":5001/index.html?token={Uri.EscapeDataString(tokenSesion)}", url1!);

                // Poblar cola de servidor, AppState, Fotos_Temp (.gitkeep + huérfano) y Fotos_Definitivas
                Directory.CreateDirectory(env.FotosTempDir);
                Directory.CreateDirectory(env.FotosDefinitivasDir);
                string gitkeep = Path.Combine(env.FotosTempDir, ".gitkeep");
                string huerfanaWebp = Path.Combine(env.FotosTempDir, "huerfana_previa.webp");
                string definitivaIntacta = Path.Combine(env.FotosDefinitivasDir, "final_intacta.jpg");
                await File.WriteAllTextAsync(gitkeep, string.Empty);
                await File.WriteAllBytesAsync(huerfanaWebp, new byte[] { 1, 2, 3, 4 });
                await File.WriteAllBytesAsync(definitivaIntacta, new byte[] { 0xFF, 0xD8, 0xFF, 0xD9 });

                AppState.CambiarCarreraActiva(carrera1);
                AppState.CategoriaPizarraActiva = cat1;

                using var client = new HttpClient { BaseAddress = new Uri(WebServer.UrlBaseHttpActivo!) };
                client.DefaultRequestHeaders.Add("X-Auth-Token", tokenSesion);
                string idSubida = await SubirFotoAsync(client, CrearJpegValido(64, 64), "carrera1.jpg", "rf13-1");

                AppState.FotosEnEsperaGlobal.Add(new FotoPendiente
                {
                    IdFoto = idSubida,
                    Hora_Llegada = DateTime.Now,
                    Ruta_Foto = Path.Combine(env.FotosTempDir, $"{idSubida}.jpg")
                });

                // Cambiar de carrera activa debe purgar todo el estado previo sin cambiar el token de sesión
                AppState.CambiarCarreraActiva(carrera2);

                Assert.Same(carrera2, AppState.CarreraActiva);
                Assert.Null(AppState.CategoriaPizarraActiva);
                Assert.Empty(AppState.FotosEnEsperaGlobal);
                Assert.Empty(WebServer.ObtenerIdsFotosPendientes());
                Assert.False(File.Exists(Path.Combine(env.FotosTempDir, $"{idSubida}.jpg")));
                Assert.False(File.Exists(huerfanaWebp));
                Assert.True(File.Exists(gitkeep));
                Assert.True(File.Exists(definitivaIntacta));
                Assert.Equal(tokenSesion, WebServer.TokenSesionActual);
            }
            finally
            {
                WebServer.LimpiarColaPendientes();
                AppState.CambiarCarreraActiva(null);
            }
        }

        [Fact]
        public async Task Rf27Y28_SecuenciaInicializacion_CreaRespaldosMax10YLimpiaHuerfanosSinBloquearAnteFallo()
        {
            using var env = new TestEnvironmentFixture();
            await DatabaseMaintenanceHelper.InicializarYMigrarBaseDatosAsync(env.CrearFabricaContexto());

            Directory.CreateDirectory(env.BackupsDir);
            Directory.CreateDirectory(env.FotosTempDir);
            Directory.CreateDirectory(env.FotosDefinitivasDir);

            // Sembrar 11 respaldos previos para verificar rotación a máximo 10 tras el nuevo respaldo
            for (int i = 1; i <= 11; i++)
            {
                string rutaVieja = Path.Combine(env.BackupsDir, $"carrera_backup_20261113_0000{i:00}.db");
                await File.WriteAllBytesAsync(rutaVieja, new byte[] { (byte)i });
                File.SetCreationTimeUtc(rutaVieja, DateTime.UtcNow.AddMinutes(-20 + i));
                File.SetLastWriteTimeUtc(rutaVieja, DateTime.UtcNow.AddMinutes(-20 + i));
            }

            string gitkeep = Path.Combine(env.FotosTempDir, ".gitkeep");
            string huerfana = Path.Combine(env.FotosTempDir, "huérfana.png");
            string definitiva = Path.Combine(env.FotosDefinitivasDir, "definitiva.jpg");
            await File.WriteAllTextAsync(gitkeep, string.Empty);
            await File.WriteAllBytesAsync(huerfana, new byte[] { 9, 8, 7 });
            await File.WriteAllBytesAsync(definitiva, new byte[] { 0xFF, 0xD8, 0xFF, 0xD9 });

            RespaldoResultado respaldo = await App.EjecutarSecuenciaInicializacionAsync(env.CrearFabricaContexto());

            Assert.True(respaldo.Exito, respaldo.Mensaje);
            Assert.NotNull(respaldo.RutaRespaldo);
            Assert.True(File.Exists(respaldo.RutaRespaldo));
            Assert.Equal(10, Directory.GetFiles(env.BackupsDir, "*.db").Length);
            Assert.False(File.Exists(huerfana));
            Assert.True(File.Exists(gitkeep));
            Assert.True(File.Exists(definitiva));

            // Verificar fallo controlado de respaldo sin bloquear migración ni limpieza
            string rutaInvalida = Path.Combine(env.RootDirectory, "archivo_como_carpeta.txt");
            await File.WriteAllTextAsync(rutaInvalida, "bloqueo");
            await File.WriteAllBytesAsync(huerfana, new byte[] { 5, 4, 3 });

            RespaldoResultado fallo = await App.EjecutarSecuenciaInicializacionAsync(
                env.CrearFabricaContexto(),
                carpetaRespaldos: rutaInvalida);

            Assert.False(fallo.Exito);
            Assert.False(File.Exists(huerfana));
            Assert.Null(Application.Current);
        }

        [Fact]
        public async Task Rf29Y31_DecodificacionEscalada_OperacionesAsincronasFueraDeUiYEstabilidadDeMemoria()
        {
            using var env = new TestEnvironmentFixture();
            await DatabaseMaintenanceHelper.InicializarYMigrarBaseDatosAsync(env.CrearFabricaContexto());

            try
            {
                // 1. Verificar E/S pesada (ImageHelper, CSV, Respaldo) fuera del hilo STA de interfaz sin bloquear
                string rutaImagenGrande = Path.Combine(env.RootDirectory, "grande_1600x1200.jpg");
                await File.WriteAllBytesAsync(rutaImagenGrande, CrearJpegValido(1600, 1200));
                string rutaCsv = Path.Combine(env.RootDirectory, "exportacion_async.csv");

                int anchoThumb = 0;
                int altoThumb = 0;
                int anchoVisor = 0;
                int altoVisor = 0;
                bool ambosCongelados = false;
                bool csvExiste = false;
                bool respaldoOk = false;

                await RunOnStaThreadAsync(async _ =>
                {
                    BitmapImage? miniatura = await ImageHelper.CargarSinBloqueoAsync(rutaImagenGrande, decodePixelWidth: 160);
                    BitmapImage? visor = await ImageHelper.CargarSinBloqueoAsync(rutaImagenGrande, decodePixelWidth: 1000);

                    Assert.NotNull(miniatura);
                    Assert.NotNull(visor);
                    anchoThumb = miniatura!.PixelWidth;
                    altoThumb = miniatura.PixelHeight;
                    anchoVisor = visor!.PixelWidth;
                    altoVisor = visor.PixelHeight;
                    ambosCongelados = miniatura.IsFrozen && visor.IsFrozen;

                    var filas = Enumerable.Range(1, 40)
                        .Select(i => new CorredorCsvFila(i, $"Corredor {i}", "Elite", "Hit 1", "Activo", "00:10:00", "08:10:00"))
                        .ToList();
                    await CsvExportHelper.ExportarCorredoresCsvAsync(rutaCsv, filas);
                    csvExiste = File.Exists(rutaCsv);

                    RespaldoResultado resp = await DatabaseMaintenanceHelper.RespaldarBaseDatosConsistentementeAsync(
                        dbPath: env.DbPath,
                        carpetaRespaldos: env.BackupsDir);
                    respaldoOk = resp.Exito;
                });

                Assert.Equal(160, anchoThumb);
                Assert.Equal(120, altoThumb);
                Assert.Equal(1000, anchoVisor);
                Assert.Equal(750, altoVisor);
                Assert.True(ambosCongelados);
                Assert.True(csvExiste);
                Assert.True(respaldoOk);

                // 2. Procesar un lote representativo de 35 imágenes limpiando referencias y verificar recolección de memoria
                //    y acotamiento de AppState.RegistrosExitososGlobal <= 200
                List<WeakReference> referencias = await ProcesarLoteImagenesYRegistrosAsync(rutaImagenGrande, cantidad: 35);

                Assert.Equal(AppState.MaxRegistrosExitosos, AppState.RegistrosExitososGlobal.Count);
                Assert.Equal("Entrada #350", AppState.RegistrosExitososGlobal[0]);
                Assert.Equal("Entrada #151", AppState.RegistrosExitososGlobal[^1]);

                AppState.RegistrosExitososGlobal.Clear();
                AppState.CambiarCarreraActiva(null);

                // Desenrollar el stack async previo antes de forzar GC en compilación Debug
                await Task.Yield();

                for (int i = 0; i < 3; i++)
                {
                    GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
                    GC.WaitForPendingFinalizers();
                }

                Assert.Empty(AppState.FotosEnEsperaGlobal);
                Assert.All(referencias, wr => Assert.False(wr.IsAlive, "Quedó una referencia retenida de BitmapImage o FotoPendiente tras limpiar."));
            }
            finally
            {
                AppState.RegistrosExitososGlobal.Clear();
                WebServer.LimpiarColaPendientes();
                AppState.CambiarCarreraActiva(null);
            }
        }

        [Fact]
        public async Task Rf30_TimingViewYLeaderboardWindow_Consolida1RecargaPorEventoSinDobleClickCacheLogosYVersionado()
        {
            using var env = new TestEnvironmentFixture();
            await DatabaseMaintenanceHelper.InicializarYMigrarBaseDatosAsync(env.CrearFabricaContexto());

            string rutaLogoIzq = Path.Combine(env.LogosDir, "logo_izq.png");
            string rutaLogoDer = Path.Combine(env.LogosDir, "logo_der.png");
            Directory.CreateDirectory(env.LogosDir);
            await File.WriteAllBytesAsync(rutaLogoIzq, CrearPngValido(320, 120));
            await File.WriteAllBytesAsync(rutaLogoDer, CrearPngValido(320, 120));

            Carrera carrera;
            Categoria cat1;
            Categoria cat2;
            Hit hit;
            Corredor corredor1;
            Corredor corredor2;

            await using (var db = env.CrearContexto())
            {
                carrera = new Carrera
                {
                    Nombre = "Carrera Consolidacion RF30",
                    Fecha = DateTime.Today,
                    RutaLogoIzquierdo = rutaLogoIzq,
                    RutaLogoDerecho = rutaLogoDer
                };
                db.Carreras.Add(carrera);
                await db.SaveChangesAsync();

                cat1 = new Categoria { Nombre = "Categoria A", ID_Carrera = carrera.ID_Carrera };
                cat2 = new Categoria { Nombre = "Categoria B", ID_Carrera = carrera.ID_Carrera };
                hit = new Hit { Nombre_Hit = "Hit Consolidado", ID_Carrera = carrera.ID_Carrera, Hora_Inicio = DateTime.Now.AddMinutes(-5) };
                db.Categorias.AddRange(cat1, cat2);
                db.Hits.Add(hit);
                await db.SaveChangesAsync();

                corredor1 = new Corredor
                {
                    Dorsal = 10,
                    Nombre = "Corredor Cat A",
                    ID_Carrera = carrera.ID_Carrera,
                    ID_Categoria = cat1.ID_Categoria,
                    ID_Hit = hit.ID_Hit
                };
                corredor2 = new Corredor
                {
                    Dorsal = 20,
                    Nombre = "Corredor Cat B",
                    ID_Carrera = carrera.ID_Carrera,
                    ID_Categoria = cat2.ID_Categoria,
                    ID_Hit = hit.ID_Hit
                };
                db.Corredores.AddRange(corredor1, corredor2);
                await db.SaveChangesAsync();

                db.Tiempos.Add(new Tiempo
                {
                    ID_Corredor = corredor2.ID_Corredor,
                    Hora_Llegada = DateTime.Now.AddMinutes(-1),
                    Tiempo_Neto = TimeSpan.FromMinutes(4),
                    Ruta_Foto = "catb.jpg"
                });
                await db.SaveChangesAsync();
            }

            await using var server = await WebServer.IniciarEnSegundoPlanoAsync(new WebServerOptions
            {
                DireccionEscucha = IPAddress.Loopback,
                PuertoHttp = 0,
                HabilitarHttps = false,
                RegenerarTokenAlIniciar = true,
                DbContextFactory = env.CrearFabricaContexto()
            });

            int notificacionesNuevoTiempo = 0;
            void ContarNuevoTiempo() => Interlocked.Increment(ref notificacionesNuevoTiempo);
            AppState.NuevoTiempoRegistrado += ContarNuevoTiempo;

            try
            {
                // Activar carrera antes de recibir fotos (CambiarCarreraActiva limpia Fotos_Temp por RF-13)
                AppState.CambiarCarreraActiva(carrera);
                AppState.CategoriaPizarraActiva = cat1;

                // Subir una foto real de 1400x1050 para probar miniatura 160px + visor 1000px + BtnAsignar.Click único
                using var client = new HttpClient { BaseAddress = new Uri(WebServer.UrlBaseHttpActivo!) };
                client.DefaultRequestHeaders.Add("X-Auth-Token", WebServer.TokenSesionActual);
                string idFoto = await SubirFotoAsync(client, CrearJpegValido(1400, 1050), "llegada10.jpg", "rf30-foto");
                string rutaFotoTemp = Path.Combine(env.FotosTempDir, $"{idFoto}.jpg");

                int recargasLeaderboard = 0;
                bool gatePrimeraConsulta = false;
                var consulta1EnVuelo = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var liberarConsulta1 = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

                Func<ApplicationDbContext> fabricaPizarra = () =>
                {
                    Interlocked.Increment(ref recargasLeaderboard);
                    if (Volatile.Read(ref gatePrimeraConsulta))
                    {
                        Volatile.Write(ref gatePrimeraConsulta, false);
                        var opts = new DbContextOptionsBuilder<ApplicationDbContext>();
                        ApplicationDbContext.ConfigurarOpcionesSqlite(opts, env.DbPath);
                        opts.AddInterceptors(new RetrasarPrimeraConsultaInterceptor(consulta1EnVuelo, liberarConsulta1));
                        return new ApplicationDbContext(opts.Options);
                    }

                    return env.CrearContexto();
                };

                long deltaVersionTiming = 0;
                int clickHandlersEnBtnAsignar = 0;
                int recargasPizarraTrasTiempo = 0;
                int recargasPizarraTrasCategoria = 0;
                bool logosReutilizadosPorCache = false;
                string? tituloCategoriaFinal = null;
                List<ResultadoView>? podioFinal = null;
                int anchoMiniatura = 0;
                int anchoVisor = 0;

                await RunOnStaThreadAsync(async dispatcher =>
                {
                    BitmapImage? mini = await ImageHelper.CargarSinBloqueoAsync(rutaFotoTemp, decodePixelWidth: 160);
                    var fotoPendiente = new FotoPendiente
                    {
                        IdFoto = idFoto,
                        Hora_Llegada = DateTime.Now.AddSeconds(-2),
                        Ruta_Foto = rutaFotoTemp,
                        Miniatura = mini
                    };
                    AppState.FotosEnEsperaGlobal.Add(fotoPendiente);
                    anchoMiniatura = mini?.PixelWidth ?? 0;

                    var timingView = new TimingView();
                    var pizarra = new LeaderboardWindow(fabricaPizarra)
                    {
                        WindowState = WindowState.Normal,
                        Width = 200,
                        Height = 150,
                        Left = -32000,
                        Top = -32000,
                        ShowActivated = false,
                        ShowInTaskbar = false,
                        Opacity = 0
                    };

                    var hostTiming = CrearVentanaHostOculta(timingView);
                    hostTiming.Show();
                    pizarra.Show();

                    var btnAsignar = (Button)timingView.FindName("BtnAsignar");
                    var inputDorsal = (TextBox)timingView.FindName("InputDorsal");
                    var listaPendientes = (ListView)timingView.FindName("ListaFotosPendientes");
                    var visor = (Image)timingView.FindName("VisorFotoPrincipal");
                    var imgIzq = (Image)pizarra.FindName("ImgLogoIzquierdo");
                    var imgDer = (Image)pizarra.FindName("ImgLogoDerecho");
                    var txtCategoriaPizarra = (TextBlock)pizarra.FindName("TxtNombreCategoria");
                    var listaPodio = (ItemsControl)pizarra.FindName("ListaPodio");

                    await EsperarCondicionAsync(
                        dispatcher,
                        () => timingView.ListaDeHits.Count == 1 &&
                              imgIzq.Source != null &&
                              imgDer.Source != null,
                        TimeSpan.FromSeconds(5));

                    // Verificar única suscripción Click en BtnAsignar
                    clickHandlersEnBtnAsignar = ObtenerCantidadHandlersRoutedEvent(btnAsignar, ButtonBase.ClickEvent);

                    listaPendientes.SelectedIndex = 0;
                    await EsperarCondicionAsync(dispatcher, () => visor.Source != null, TimeSpan.FromSeconds(5));
                    anchoVisor = (visor.Source as BitmapSource)?.PixelWidth ?? 0;

                    ImageSource logoIzqInicial = imgIzq.Source;
                    ImageSource logoDerInicial = imgDer.Source;

                    // Reiniciar contadores antes de asignar dorsal 10 vía BtnAsignar
                    long versionAntes = ObtenerVersionReloadTimingView(timingView);
                    Volatile.Write(ref notificacionesNuevoTiempo, 0);
                    Interlocked.Exchange(ref recargasLeaderboard, 0);

                    inputDorsal.Text = "10";
                    btnAsignar.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));

                    await EsperarCondicionAsync(
                        dispatcher,
                        () => AppState.FotosEnEsperaGlobal.Count == 0 &&
                              listaPodio.Items.Count == 1 &&
                              timingView.ListaDeHits.Count == 1 &&
                              timingView.ListaDeHits[0].CorredoresLlegaron == 2,
                        TimeSpan.FromSeconds(5));

                    await EsperarDespachoAsync(dispatcher);

                    long versionDespues = ObtenerVersionReloadTimingView(timingView);
                    deltaVersionTiming = versionDespues - versionAntes;
                    recargasPizarraTrasTiempo = Volatile.Read(ref recargasLeaderboard);
                    logosReutilizadosPorCache =
                        ReferenceEquals(logoIzqInicial, imgIzq.Source) &&
                        ReferenceEquals(logoDerInicial, imgDer.Source);

                    // Probar cambio de categoría en pizarra: 1 recarga consolidada y caché de logos intacta
                    Interlocked.Exchange(ref recargasLeaderboard, 0);
                    AppState.CategoriaPizarraActiva = cat2;
                    AppState.NotificarCambioCategoriaPizarra();

                    await EsperarCondicionAsync(
                        dispatcher,
                        () => txtCategoriaPizarra.Text.Contains("Categoria B", StringComparison.Ordinal),
                        TimeSpan.FromSeconds(5));
                    await EsperarDespachoAsync(dispatcher);

                    recargasPizarraTrasCategoria = Volatile.Read(ref recargasLeaderboard);
                    logosReutilizadosPorCache = logosReutilizadosPorCache &&
                        ReferenceEquals(logoIzqInicial, imgIzq.Source) &&
                        ReferenceEquals(logoDerInicial, imgDer.Source);

                    // Probar descarte de recargas obsoletas en LeaderboardWindow ante ráfaga fuera de orden:
                    // Consulta lenta (cat1) inicia primero, luego consulta rápida (cat2) la supera.
                    AppState.CategoriaPizarraActiva = cat1;
                    Volatile.Write(ref gatePrimeraConsulta, true);
                    AppState.NotificarCambioCategoriaPizarra();

                    await consulta1EnVuelo.Task.WaitAsync(TimeSpan.FromSeconds(5));

                    AppState.CategoriaPizarraActiva = cat2;
                    AppState.NotificarCambioCategoriaPizarra();

                    await EsperarCondicionAsync(
                        dispatcher,
                        () => txtCategoriaPizarra.Text.Contains("Categoria B", StringComparison.Ordinal) &&
                              Volatile.Read(ref recargasLeaderboard) >= 2,
                        TimeSpan.FromSeconds(5));

                    // Liberar la consulta lenta obsoleta (cat1) y confirmar que NO sobrescribe a cat2
                    liberarConsulta1.TrySetResult();
                    await Task.Delay(80);
                    await EsperarDespachoAsync(dispatcher);

                    tituloCategoriaFinal = txtCategoriaPizarra.Text;
                    podioFinal = (listaPodio.ItemsSource as IEnumerable<ResultadoView>)?.ToList();

                    pizarra.Close();
                    hostTiming.Close();
                    await EsperarDespachoAsync(dispatcher);
                });

                Assert.Equal(160, anchoMiniatura);
                Assert.Equal(1000, anchoVisor);
                Assert.Equal(1, clickHandlersEnBtnAsignar);
                Assert.Equal(1, Volatile.Read(ref notificacionesNuevoTiempo));
                Assert.Equal(1, deltaVersionTiming);
                Assert.Equal(1, recargasPizarraTrasTiempo);
                Assert.Equal(1, recargasPizarraTrasCategoria);
                Assert.True(logosReutilizadosPorCache, "LogoRouteCache debe reutilizar las mismas instancias BitmapImage sin releer disco.");
                Assert.Equal("Resultados: Categoria B", tituloCategoriaFinal);
                Assert.NotNull(podioFinal);
                Assert.Single(podioFinal!);
                Assert.Equal(20, podioFinal[0].Dorsal);
            }
            finally
            {
                AppState.NuevoTiempoRegistrado -= ContarNuevoTiempo;
                WebServer.LimpiarColaPendientes();
                AppState.CambiarCarreraActiva(null);
            }
        }

        [Fact]
        public async Task Rf31_CicloVidaVentanasYVistas_DetieneTimersLimpiaSuscripcionesYCierraPizarraHuerfana()
        {
            using var env = new TestEnvironmentFixture();
            await DatabaseMaintenanceHelper.InicializarYMigrarBaseDatosAsync(env.CrearFabricaContexto());

            Carrera carrera;
            await using (var db = env.CrearContexto())
            {
                carrera = new Carrera { Nombre = "Carrera Ciclo Vida", Fecha = DateTime.Today };
                db.Carreras.Add(carrera);
                await db.SaveChangesAsync();

                db.Categorias.Add(new Categoria { Nombre = "General", ID_Carrera = carrera.ID_Carrera });
                db.Hits.Add(new Hit { Nombre_Hit = "Hit 1", ID_Carrera = carrera.ID_Carrera, Hora_Inicio = DateTime.Now.AddMinutes(-1) });
                await db.SaveChangesAsync();
            }

            await using var server = await WebServer.IniciarEnSegundoPlanoAsync(new WebServerOptions
            {
                DireccionEscucha = IPAddress.Loopback,
                PuertoHttp = 0,
                HabilitarHttps = false,
                RegenerarTokenAlIniciar = true,
                DbContextFactory = env.CrearFabricaContexto()
            });

            try
            {
                bool pizarraAbiertaDuranteTiming = false;
                bool pizarraAbiertaTrasNavegar = true;
                bool timerActivoDuranteTiming = false;
                bool timerActivoTrasDescargar = true;
                bool timingDescargada = false;
                bool visorNuloTrasDescargar = false;
                bool viewCtsNuloTrasDescargar = false;
                bool visorCtsNuloTrasDescargar = false;
                long versionTimingAntesEventoPostDescarga = 0;
                long versionTimingDespuesEventoPostDescarga = 0;
                int fotosAgregadasMientrasAbierta = 0;
                int fotosAgregadasTrasCerrarMainWindow = 0;

                using var client = new HttpClient { BaseAddress = new Uri(WebServer.UrlBaseHttpActivo!) };
                client.DefaultRequestHeaders.Add("X-Auth-Token", WebServer.TokenSesionActual);
                byte[] jpeg = CrearJpegValido(320, 240);

                await RunOnStaThreadAsync(async dispatcher =>
                {
                    AppState.CambiarCarreraActiva(carrera);

                    var mainWindow = new MainWindow
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
                    mainWindow.Show();

                    var timingView = new TimingView();
                    mainWindow.NavegarAVista(timingView);

                    await EsperarCondicionAsync(
                        dispatcher,
                        () => timingView.ListaDeHits.Count == 1,
                        TimeSpan.FromSeconds(5));

                    DispatcherTimer cronometro = ObtenerCampoPrivado<DispatcherTimer>(timingView, "_cronometroTimer");
                    timerActivoDuranteTiming = cronometro.IsEnabled;

                    // Mientras MainWindow está abierta, una foto subida al WebServer entra a AppState.FotosEnEsperaGlobal
                    _ = await SubirFotoAsync(client, jpeg, "abierta.jpg", "life-open");
                    await EsperarCondicionAsync(dispatcher, () => AppState.FotosEnEsperaGlobal.Count == 1, TimeSpan.FromSeconds(5));
                    fotosAgregadasMientrasAbierta = AppState.FotosEnEsperaGlobal.Count;

                    // Abrir pizarra desde MainWindow y luego navegar fuera de TimingView -> debe cerrar la pizarra huérfana y limpiar TimingView
                    mainWindow.AlternarPizarra();
                    await EsperarDespachoAsync(dispatcher);
                    pizarraAbiertaDuranteTiming = mainWindow.PizarraEstaAbierta;

                    mainWindow.NavegarAVista(new WelcomeView());
                    await EsperarDespachoAsync(dispatcher);

                    pizarraAbiertaTrasNavegar = mainWindow.PizarraEstaAbierta;
                    timerActivoTrasDescargar = cronometro.IsEnabled;
                    timingDescargada = ObtenerCampoPrivado<bool>(timingView, "_estaDescargado");
                    var visor = (Image)timingView.FindName("VisorFotoPrincipal");
                    visorNuloTrasDescargar = visor.Source == null;
                    viewCtsNuloTrasDescargar = ObtenerCampoPrivadoOpcional<CancellationTokenSource>(timingView, "_viewCts") == null;
                    visorCtsNuloTrasDescargar = ObtenerCampoPrivadoOpcional<CancellationTokenSource>(timingView, "_visorCts") == null;

                    // Verificar que TimingView descargado ya no reacciona a AppState.NuevoTiempoRegistrado
                    versionTimingAntesEventoPostDescarga = ObtenerVersionReloadTimingView(timingView);
                    AppState.NotificarNuevoTiempo();
                    await Task.Delay(50);
                    await EsperarDespachoAsync(dispatcher);
                    versionTimingDespuesEventoPostDescarga = ObtenerVersionReloadTimingView(timingView);

                    // Cerrar MainWindow -> debe desuscribir eventos de WebServer
                    mainWindow.Close();
                    await EsperarDespachoAsync(dispatcher);
                });

                AppState.FotosEnEsperaGlobal.Clear();
                _ = await SubirFotoAsync(client, jpeg, "cerrada.jpg", "life-closed");
                await Task.Delay(60);
                fotosAgregadasTrasCerrarMainWindow = AppState.FotosEnEsperaGlobal.Count;

                Assert.True(timerActivoDuranteTiming);
                Assert.False(timerActivoTrasDescargar);
                Assert.True(timingDescargada);
                Assert.True(visorNuloTrasDescargar);
                Assert.True(viewCtsNuloTrasDescargar);
                Assert.True(visorCtsNuloTrasDescargar);
                Assert.True(pizarraAbiertaDuranteTiming);
                Assert.False(pizarraAbiertaTrasNavegar);
                Assert.Equal(versionTimingAntesEventoPostDescarga, versionTimingDespuesEventoPostDescarga);
                Assert.Equal(1, fotosAgregadasMientrasAbierta);
                Assert.Equal(0, fotosAgregadasTrasCerrarMainWindow);
                Assert.Null(Application.Current);
            }
            finally
            {
                WebServer.LimpiarColaPendientes();
                AppState.CambiarCarreraActiva(null);
            }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static async Task<List<WeakReference>> ProcesarLoteImagenesYRegistrosAsync(string rutaImagenGrande, int cantidad)
        {
            var referencias = new List<WeakReference>(cantidad * 3);

            for (int i = 1; i <= cantidad; i++)
            {
                BitmapImage? thumb = await ImageHelper.CargarSinBloqueoAsync(rutaImagenGrande, decodePixelWidth: 160);
                BitmapImage? full = await ImageHelper.CargarSinBloqueoAsync(rutaImagenGrande, decodePixelWidth: 1000);

                Assert.NotNull(thumb);
                Assert.NotNull(full);
                Assert.Equal(160, thumb!.PixelWidth);
                Assert.Equal(1000, full!.PixelWidth);

                var pendiente = new FotoPendiente
                {
                    IdFoto = $"batch-{i}",
                    Hora_Llegada = DateTime.Now,
                    Ruta_Foto = rutaImagenGrande,
                    Miniatura = thumb
                };

                AppState.FotosEnEsperaGlobal.Add(pendiente);
                referencias.Add(new WeakReference(thumb));
                referencias.Add(new WeakReference(full));
                referencias.Add(new WeakReference(pendiente));

                pendiente.Miniatura = null;
                AppState.FotosEnEsperaGlobal.Remove(pendiente);
                thumb = null;
                full = null;
                pendiente = null;
            }

            for (int i = 1; i <= 350; i++)
            {
                AppState.AgregarRegistroExitoso($"Entrada #{i}");
            }

            return referencias;
        }

        private static Window CrearVentanaHostOculta(object contenido)
        {
            return new Window
            {
                Content = contenido,
                WindowStyle = WindowStyle.None,
                WindowState = WindowState.Normal,
                Width = 320,
                Height = 200,
                Left = -32000,
                Top = -32000,
                ShowActivated = false,
                ShowInTaskbar = false,
                Opacity = 0
            };
        }

        private static async Task RunOnStaThreadAsync(Func<Dispatcher, Task> action)
        {
            var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var thread = new Thread(() =>
            {
                Dispatcher dispatcher = Dispatcher.CurrentDispatcher;
                SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
                Exception? capturedException = null;

                dispatcher.UnhandledException += (_, args) =>
                {
                    capturedException ??= args.Exception;
                    args.Handled = true;
                    dispatcher.BeginInvokeShutdown(DispatcherPriority.Send);
                };

                async void Execute()
                {
                    try
                    {
                        await action(dispatcher);
                    }
                    catch (Exception ex)
                    {
                        capturedException = ex;
                    }
                    finally
                    {
                        dispatcher.BeginInvokeShutdown(DispatcherPriority.Background);
                    }
                }

                dispatcher.BeginInvoke((Action)Execute);
                Dispatcher.Run();

                if (capturedException != null)
                {
                    tcs.TrySetException(capturedException);
                }
                else
                {
                    tcs.TrySetResult();
                }
            });

            thread.SetApartmentState(ApartmentState.STA);
            thread.IsBackground = true;
            thread.Start();

            try
            {
                await tcs.Task.ConfigureAwait(false);
            }
            finally
            {
                await Task.Run(() => thread.Join(TimeSpan.FromSeconds(5))).ConfigureAwait(false);
                GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
                GC.WaitForPendingFinalizers();
                GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
            }
        }

        private static async Task EsperarDespachoAsync(Dispatcher dispatcher)
        {
            await dispatcher.InvokeAsync(() => { }, DispatcherPriority.Background);
            await dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
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
                    throw new TimeoutException($"La condición esperada no se cumplió dentro de {timeout.TotalSeconds:F1} segundos.");
                }

                await Task.Delay(20);
                await dispatcher.InvokeAsync(() => { }, DispatcherPriority.Background);
            }
        }

        private static int ObtenerCantidadHandlersRoutedEvent(UIElement elemento, RoutedEvent routedEvent)
        {
            PropertyInfo? propStore = typeof(UIElement).GetProperty(
                "EventHandlersStore",
                BindingFlags.Instance | BindingFlags.NonPublic);
            object? store = propStore?.GetValue(elemento);
            if (store == null)
            {
                return 0;
            }

            MethodInfo? metodo = store.GetType().GetMethod(
                "GetRoutedEventHandlers",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (metodo?.Invoke(store, new object[] { routedEvent }) is RoutedEventHandlerInfo[] handlers)
            {
                return handlers.Length;
            }

            return 0;
        }

        private static long ObtenerVersionReloadTimingView(TimingView view)
        {
            var coordinator = ObtenerCampoPrivado<ReloadVersionCoordinator>(view, "_hitsReloadCoordinator");
            return ObtenerCampoPrivado<long>(coordinator, "_versionActual");
        }

        private static T ObtenerCampoPrivado<T>(object instancia, string nombreCampo)
        {
            FieldInfo? campo = instancia.GetType().GetField(nombreCampo, BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(campo);
            return (T)campo!.GetValue(instancia)!;
        }

        private static T? ObtenerCampoPrivadoOpcional<T>(object instancia, string nombreCampo) where T : class
        {
            FieldInfo? campo = instancia.GetType().GetField(nombreCampo, BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(campo);
            return campo!.GetValue(instancia) as T;
        }

        private static async Task<string> SubirFotoAsync(
            HttpClient client,
            byte[] bytes,
            string nombreArchivo,
            string tempId)
        {
            using var form = new MultipartFormDataContent();
            var fileContent = new ByteArrayContent(bytes);
            fileContent.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            form.Add(fileContent, "foto", nombreArchivo);
            form.Add(new StringContent(tempId), "tempId");

            HttpResponseMessage resp = await client.PostAsync("/api/upload-photo", form);
            string json = await resp.Content.ReadAsStringAsync();
            Assert.True(resp.StatusCode == HttpStatusCode.OK, $"Fallo upload-photo ({(int)resp.StatusCode}): {json}");

            using JsonDocument doc = JsonDocument.Parse(json);
            return doc.RootElement.GetProperty("idFoto").GetString()!;
        }

        private static byte[] CrearJpegValido(int ancho, int alto)
        {
            BitmapSource fuente = CrearBitmapSintetico(ancho, alto);
            var encoder = new JpegBitmapEncoder { QualityLevel = 80 };
            encoder.Frames.Add(BitmapFrame.Create(fuente));
            using var ms = new MemoryStream();
            encoder.Save(ms);
            return ms.ToArray();
        }

        private static byte[] CrearPngValido(int ancho, int alto)
        {
            BitmapSource fuente = CrearBitmapSintetico(ancho, alto);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(fuente));
            using var ms = new MemoryStream();
            encoder.Save(ms);
            return ms.ToArray();
        }

        private static BitmapSource CrearBitmapSintetico(int ancho, int alto)
        {
            int stride = ancho * 4;
            byte[] pixels = new byte[alto * stride];
            for (int y = 0; y < alto; y++)
            {
                for (int x = 0; x < ancho; x++)
                {
                    int idx = y * stride + x * 4;
                    pixels[idx + 0] = (byte)((x * 3 + y) % 256);
                    pixels[idx + 1] = (byte)((x + y * 5) % 256);
                    pixels[idx + 2] = (byte)((x * 7 + y * 2) % 256);
                    pixels[idx + 3] = 255;
                }
            }

            var bmp = BitmapSource.Create(ancho, alto, 96, 96, PixelFormats.Bgra32, null, pixels, stride);
            bmp.Freeze();
            return bmp;
        }

        private sealed class RetrasarPrimeraConsultaInterceptor : DbCommandInterceptor
        {
            private readonly TaskCompletionSource _enVuelo;
            private readonly TaskCompletionSource _liberar;
            private int _disparado;

            public RetrasarPrimeraConsultaInterceptor(
                TaskCompletionSource enVuelo,
                TaskCompletionSource liberar)
            {
                _enVuelo = enVuelo;
                _liberar = liberar;
            }

            public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
                DbCommand command,
                CommandEventData eventData,
                InterceptionResult<DbDataReader> result,
                CancellationToken cancellationToken = default)
            {
                if (Interlocked.Exchange(ref _disparado, 1) == 0)
                {
                    _enVuelo.TrySetResult();
                    await _liberar.Task.ConfigureAwait(false);
                }

                return await base.ReaderExecutingAsync(command, eventData, result, cancellationToken).ConfigureAwait(false);
            }
        }
    }
}
