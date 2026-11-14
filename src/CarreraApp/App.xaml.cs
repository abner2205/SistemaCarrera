using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using CarreraApp.Data;
using CarreraApp.Helpers;
using CarreraApp.Models;
using CarreraApp.Server;

namespace CarreraApp
{
    public partial class App : Application
    {
        private CancellationTokenSource? _appCts;
        private Task? _startupTask;

        /// <summary>
        /// Tarea compartida que representa la inicialización asíncrona de base de datos,
        /// respaldo previo y limpieza de archivos temporales al arranque.
        /// </summary>
        public static Task InicializacionBaseDatosTask { get; private set; } = Task.CompletedTask;

        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            _appCts = new CancellationTokenSource();
            CancellationToken appToken = _appCts.Token;

            // Generar una única vez por sesión de arranque el token estable de WebServer
            _ = WebServer.TokenSesionActual;

            // Asignar la tarea antes de crear MainWindow/WelcomeView para que WelcomeView espere la tarea real
            Task initTask = Task.Run(
                () => EjecutarSecuenciaInicializacionAsync(cancellationToken: appToken),
                CancellationToken.None);
            InicializacionBaseDatosTask = initTask;

            _startupTask = Task.Run(async () =>
            {
                try
                {
                    await initTask.ConfigureAwait(false);
                    if (appToken.IsCancellationRequested)
                    {
                        return;
                    }

                    await WebServer.IniciarServidorAsync(new WebServerOptions
                    {
                        ObtenerCarreraActiva = () => AppState.CarreraActiva
                    }, appToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (appToken.IsCancellationRequested)
                {
                    // Apagado ordenado en curso
                }
                catch (Exception ex)
                {
                    Trace.TraceError($"[App] Error durante la secuencia de arranque: {ex}");
                }
            }, CancellationToken.None);

            // Mostrar la ventana principal inmediatamente sin bloquear el hilo UI
            var mainWindow = new UI.MainWindow();
            MainWindow = mainWindow;
            mainWindow.Show();
        }

        /// <summary>
        /// Ejecuta en orden seguro la secuencia de arranque:
        /// 1) respaldo consistente de carrera.db (tolerante a fallos sin bloquear),
        /// 2) inicialización/migración/WAL/índices en SQLite,
        /// 3) limpieza de imágenes huérfanas en Fotos_Temp.
        /// </summary>
        public static async Task<RespaldoResultado> EjecutarSecuenciaInicializacionAsync(
            Func<ApplicationDbContext>? contextFactory = null,
            string? carpetaRespaldos = null,
            string? dbPathParaRespaldo = null,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

            RespaldoResultado respaldo = await DatabaseMaintenanceHelper
                .RespaldarBaseDatosConsistentementeAsync(
                    dbPath: dbPathParaRespaldo,
                    carpetaRespaldos: carpetaRespaldos,
                    cancellationToken: cancellationToken)
                .ConfigureAwait(false);

            if (!respaldo.Exito)
            {
                Trace.TraceWarning($"[App] Respaldo omitido por error no bloqueante: {respaldo.Mensaje}");
                var dispatcher = Current?.Dispatcher;
                if (dispatcher != null && !dispatcher.HasShutdownStarted && !dispatcher.HasShutdownFinished)
                {
                    _ = dispatcher.InvokeAsync(() =>
                        AppState.AgregarRegistroExitoso($"[AVISO RESPALDO] {respaldo.Mensaje}"));
                }
            }

            await DatabaseMaintenanceHelper
                .InicializarYMigrarBaseDatosAsync(contextFactory, cancellationToken)
                .ConfigureAwait(false);

            cancellationToken.ThrowIfCancellationRequested();

            List<string>? activos = ObtenerNombresTempActivosEnServidor();
            StorageHelper.LimpiarFotosTempHuerfanas(activos);

            return respaldo;
        }

        protected override void OnExit(ExitEventArgs e)
        {
            try
            {
                _appCts?.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // Ya liberado previamente
            }

            try
            {
                Task detenerServidor = WebServer.DetenerServidorAsync();
                Task.WhenAny(detenerServidor, Task.Delay(TimeSpan.FromSeconds(3))).GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                Trace.TraceWarning($"[App] Error al detener el servidor web en OnExit: {ex.Message}");
            }

            if (_startupTask != null)
            {
                try
                {
                    _startupTask.Wait(TimeSpan.FromSeconds(2));
                }
                catch
                {
                    // Ignorar cancelación o errores ya reportados durante el cierre
                }
            }

            _appCts?.Dispose();
            _appCts = null;

            base.OnExit(e);
        }

        private static List<string>? ObtenerNombresTempActivosEnServidor()
        {
            IReadOnlyCollection<string> idsPendientes = WebServer.ObtenerIdsFotosPendientes();
            if (idsPendientes.Count == 0)
            {
                return null;
            }

            var nombres = new List<string>(idsPendientes.Count * 4);
            foreach (string id in idsPendientes)
            {
                nombres.Add($"{id}.jpg");
                nombres.Add($"{id}.jpeg");
                nombres.Add($"{id}.png");
                nombres.Add($"{id}.webp");
            }

            return nombres;
        }
    }
}
