using System;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using CarreraApp.Data;
using CarreraApp.Helpers;
using CarreraApp.Models;
using CarreraApp.Server;
using CarreraApp.UI.Views;

namespace CarreraApp.UI
{
    public partial class MainWindow : Window
    {
        private readonly CancellationTokenSource _cerrarCts = new();
        private LeaderboardWindow? _ventanaPizarra;
        private DispatcherTimer? _toastTimer;
        private int _toastVersion;
        private bool _estaCerrada;

        public event Action<bool>? EstadoPizarraCambiado;

        public bool PizarraEstaAbierta => _ventanaPizarra != null && _ventanaPizarra.IsLoaded;

        public bool PizarraEstaEnPantallaCompleta =>
            PizarraEstaAbierta && _ventanaPizarra!.WindowStyle == WindowStyle.None;

        public MainWindow()
        {
            InitializeComponent();

            // Escuchar eventos del servidor web y del estado global de carrera
            WebServer.NuevaFotoRecibida += WebServer_NuevaFotoRecibida;
            WebServer.FotoAsignadaRemota += WebServer_FotoAsignadaRemota;
            WebServer.FotoDescartadaRemota += WebServer_FotoDescartadaRemota;
            WebServer.ErrorCriticoServidor += WebServer_ErrorCriticoServidor;
            AppState.CarreraActivaCambiada += AppState_CarreraActivaCambiada;

            NavegarAVista(new WelcomeView());
        }

        public void NavegarAVista(UserControl nuevaVista)
        {
            ArgumentNullException.ThrowIfNull(nuevaVista);

            // Evitar ventanas de pizarra huérfanas al salir de cronometraje o cambiar de pantalla
            if (nuevaVista is not TimingView || !ReferenceEquals(ContenedorPrincipal.Content, nuevaVista))
            {
                CerrarPizarra();
            }

            ContenedorPrincipal.Content = nuevaVista;
            ActualizarIndicadorNavegacionActiva(nuevaVista);
            PanelLateral.Visibility = Visibility.Collapsed;
        }

        public void MostrarNotificacionGlobal(
            NivelFeedback nivel,
            string mensaje,
            int? duracionPersonalizadaMs = null)
        {
            if (_estaCerrada || Dispatcher.HasShutdownStarted)
            {
                return;
            }

            if (!Dispatcher.CheckAccess())
            {
                _ = Dispatcher.InvokeAsync(() => MostrarNotificacionGlobal(nivel, mensaje, duracionPersonalizadaMs));
                return;
            }

            ConfiguracionAvisoFeedback config = UiFeedbackHelper.CrearConfiguracionAviso(
                nivel,
                mensaje,
                duracionPersonalizadaMs);

            _toastVersion++;
            _toastTimer?.Stop();

            Brush fondo = CrearBrushCongelado(config.ColorFondoHex);
            Brush borde = CrearBrushCongelado(config.ColorBordeHex);
            Brush texto = CrearBrushCongelado(config.ColorTextoHex);

            ToastGlobalBorder.Background = fondo;
            ToastGlobalBorder.BorderBrush = borde;
            TxtToastIcono.Foreground = texto;
            TxtToastMensaje.Foreground = texto;
            BtnCerrarToast.Foreground = texto;

            TxtToastIcono.Text = config.Nivel switch
            {
                NivelFeedback.Exito => "✓",
                NivelFeedback.Informacion => "i",
                NivelFeedback.Advertencia => "!",
                NivelFeedback.Error => "✕",
                _ => "i"
            };
            TxtToastMensaje.Text = config.Mensaje;
            ToastGlobalBorder.Visibility = Visibility.Visible;

            int duracionAnimMs = UiFeedbackHelper.ObtenerDuracionTransicionMs();
            if (duracionAnimMs > 0)
            {
                var animacionEntrada = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(duracionAnimMs))
                {
                    EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
                };
                ToastGlobalBorder.BeginAnimation(OpacityProperty, animacionEntrada);
            }
            else
            {
                ToastGlobalBorder.BeginAnimation(OpacityProperty, null);
                ToastGlobalBorder.Opacity = 1;
            }

            if (!config.RequiereCierreManual && config.DuracionAutoOcultamientoMs > 0)
            {
                _toastTimer ??= CrearToastTimer();
                _toastTimer.Interval = TimeSpan.FromMilliseconds(config.DuracionAutoOcultamientoMs);
                _toastTimer.Start();
            }
        }

        public void OcultarNotificacionGlobal()
        {
            _toastTimer?.Stop();
            int versionActual = ++_toastVersion;

            if (ToastGlobalBorder.Visibility != Visibility.Visible)
            {
                return;
            }

            int duracionAnimMs = UiFeedbackHelper.ObtenerDuracionTransicionMs();
            if (duracionAnimMs <= 0 || _estaCerrada)
            {
                ToastGlobalBorder.BeginAnimation(OpacityProperty, null);
                ToastGlobalBorder.Visibility = Visibility.Collapsed;
                return;
            }

            var animacionSalida = new DoubleAnimation(ToastGlobalBorder.Opacity, 0, TimeSpan.FromMilliseconds(duracionAnimMs))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn }
            };
            animacionSalida.Completed += (_, _) =>
            {
                if (_estaCerrada || versionActual != _toastVersion)
                {
                    return;
                }

                ToastGlobalBorder.BeginAnimation(OpacityProperty, null);
                ToastGlobalBorder.Visibility = Visibility.Collapsed;
            };
            ToastGlobalBorder.BeginAnimation(OpacityProperty, animacionSalida);
        }

        public void AlternarPizarra()
        {
            if (_estaCerrada)
            {
                return;
            }

            if (PizarraEstaAbierta)
            {
                CerrarPizarra();
                return;
            }

            var pizarra = new LeaderboardWindow();
            _ventanaPizarra = pizarra;
            pizarra.Closed += VentanaPizarra_Closed;
            pizarra.Show();
            EstadoPizarraCambiado?.Invoke(true);
        }

        public bool AlternarPantallaCompletaPizarra()
        {
            if (!PizarraEstaAbierta || _ventanaPizarra == null)
            {
                return false;
            }

            if (_ventanaPizarra.WindowStyle == WindowStyle.None)
            {
                _ventanaPizarra.WindowStyle = WindowStyle.SingleBorderWindow;
                _ventanaPizarra.WindowState = WindowState.Normal;
                return false;
            }

            _ventanaPizarra.WindowStyle = WindowStyle.None;
            _ventanaPizarra.WindowState = WindowState.Maximized;
            return true;
        }

        public void CerrarPizarra()
        {
            LeaderboardWindow? pizarra = _ventanaPizarra;
            if (pizarra == null)
            {
                return;
            }

            _ventanaPizarra = null;
            pizarra.Closed -= VentanaPizarra_Closed;

            if (pizarra.IsLoaded)
            {
                pizarra.Close();
            }

            EstadoPizarraCambiado?.Invoke(false);
        }

        public void ActualizarTituloCarrera()
        {
            TxtNombreCarrera.Text = AppState.CarreraActiva != null
                ? $"CarreraApp: {AppState.CarreraActiva.Nombre}"
                : "CarreraApp";
        }

        private void ActualizarIndicadorNavegacionActiva(UserControl vistaActiva)
        {
            BtnNavInicio.Tag = vistaActiva is WelcomeView ? "Active" : null;
            BtnNavCronometraje.Tag = vistaActiva is TimingView ? "Active" : null;
            BtnNavCorredores.Tag = vistaActiva is CorredoresView ? "Active" : null;
            BtnNavHits.Tag = vistaActiva is HitsView ? "Active" : null;

            TxtSeccionActiva.Text = vistaActiva switch
            {
                WelcomeView => "Inicio",
                TimingView => "Panel de Cronometraje",
                CorredoresView => "Gestión de Corredores",
                HitsView => "Configuración de Hits",
                _ => "CarreraApp"
            };
        }

        private DispatcherTimer CrearToastTimer()
        {
            var timer = new DispatcherTimer(DispatcherPriority.Normal, Dispatcher);
            timer.Tick += ToastTimer_Tick;
            return timer;
        }

        private void ToastTimer_Tick(object? sender, EventArgs e)
        {
            OcultarNotificacionGlobal();
        }

        private void DetenerYLiberarToastTimer()
        {
            if (_toastTimer != null)
            {
                _toastTimer.Stop();
                _toastTimer.Tick -= ToastTimer_Tick;
                _toastTimer = null;
            }
        }

        private static SolidColorBrush CrearBrushCongelado(string hexColor)
        {
            var color = (Color)ColorConverter.ConvertFromString(hexColor);
            var brush = new SolidColorBrush(color);
            brush.Freeze();
            return brush;
        }

        private void VentanaPizarra_Closed(object? sender, EventArgs e)
        {
            if (sender is LeaderboardWindow pizarra)
            {
                pizarra.Closed -= VentanaPizarra_Closed;
                if (ReferenceEquals(_ventanaPizarra, pizarra))
                {
                    _ventanaPizarra = null;
                }
            }

            EstadoPizarraCambiado?.Invoke(false);
        }

        private void AppState_CarreraActivaCambiada()
        {
            if (_estaCerrada || Dispatcher.HasShutdownStarted)
            {
                return;
            }

            _ = Dispatcher.InvokeAsync(() =>
            {
                if (_estaCerrada)
                {
                    return;
                }

                ActualizarTituloCarrera();
                if (AppState.CarreraActiva == null || ContenedorPrincipal.Content is not TimingView)
                {
                    CerrarPizarra();
                }
            });
        }

        // 1. Recibe la foto y decodifica la miniatura escalada (160 px) fuera del hilo UI
        private async void WebServer_NuevaFotoRecibida(string idFoto, DateTime horaLlegada, string rutaFoto)
        {
            if (_estaCerrada || Dispatcher.HasShutdownStarted)
            {
                return;
            }

            try
            {
                var foto = new FotoPendiente
                {
                    IdFoto = idFoto,
                    Hora_Llegada = horaLlegada,
                    Ruta_Foto = rutaFoto
                };

                await Dispatcher.InvokeAsync(() =>
                {
                    if (_estaCerrada || !WebServer.ObtenerIdsFotosPendientes().Contains(idFoto))
                    {
                        return;
                    }

                    if (!AppState.FotosEnEsperaGlobal.Any(f => f.IdFoto == idFoto))
                    {
                        AppState.FotosEnEsperaGlobal.Add(foto);
                    }
                });

                BitmapImage? miniatura = await ImageHelper
                    .CargarSinBloqueoAsync(rutaFoto, decodePixelWidth: 160, _cerrarCts.Token)
                    .ConfigureAwait(false);

                if (miniatura == null || _estaCerrada || _cerrarCts.IsCancellationRequested || Dispatcher.HasShutdownStarted)
                {
                    return;
                }

                await Dispatcher.InvokeAsync(() =>
                {
                    if (_estaCerrada)
                    {
                        return;
                    }

                    FotoPendiente? existente = AppState.FotosEnEsperaGlobal.FirstOrDefault(f => f.IdFoto == idFoto);
                    if (existente != null)
                    {
                        existente.Miniatura = miniatura;
                    }
                });
            }
            catch (OperationCanceledException) when (_cerrarCts.IsCancellationRequested || _estaCerrada)
            {
                // Ventana cerrándose
            }
            catch (Exception ex)
            {
                Trace.TraceWarning($"[MainWindow] Error al procesar recepción de foto '{idFoto}': {ex.Message}");
            }
        }

        // 2. Sincroniza en UI la asignación ya validada y persistida por WebServer + RaceDataService
        private void WebServer_FotoAsignadaRemota(string idFoto, int dorsalNum, AsignacionResultado resultado)
        {
            if (_estaCerrada || Dispatcher.HasShutdownStarted)
            {
                return;
            }

            _ = Dispatcher.InvokeAsync(() =>
            {
                if (_estaCerrada)
                {
                    return;
                }

                FotoPendiente? fotoActual = AppState.FotosEnEsperaGlobal.FirstOrDefault(f => f.IdFoto == idFoto);
                if (fotoActual != null)
                {
                    fotoActual.Miniatura = null;
                    AppState.FotosEnEsperaGlobal.Remove(fotoActual);
                }

                AppState.AgregarRegistroExitoso($"[{DateTime.Now:HH:mm:ss}] Remoto: {resultado.Mensaje}");
                if (resultado.HitFinalizado)
                {
                    AppState.AgregarRegistroExitoso(
                        $"[HIT FINALIZADO] ¡Todos los corredores de '{resultado.NombreHitFinalizado}' han llegado a la meta!");
                }

                AppState.NotificarNuevoTiempo();
            });
        }

        private void WebServer_FotoDescartadaRemota(string idFoto)
        {
            if (_estaCerrada || Dispatcher.HasShutdownStarted)
            {
                return;
            }

            _ = Dispatcher.InvokeAsync(() =>
            {
                if (_estaCerrada)
                {
                    return;
                }

                FotoPendiente? fotoActual = AppState.FotosEnEsperaGlobal.FirstOrDefault(f => f.IdFoto == idFoto);
                if (fotoActual != null)
                {
                    fotoActual.Miniatura = null;
                    AppState.FotosEnEsperaGlobal.Remove(fotoActual);
                }
            });
        }

        private void WebServer_ErrorCriticoServidor(Exception ex)
        {
            if (_estaCerrada || Dispatcher.HasShutdownStarted)
            {
                return;
            }

            _ = Dispatcher.InvokeAsync(() =>
            {
                if (_estaCerrada)
                {
                    return;
                }

                AppState.AgregarRegistroExitoso($"[SERVIDOR WEB] Error crítico: {ex.Message}");
                MostrarNotificacionGlobal(
                    NivelFeedback.Error,
                    $"No se pudo iniciar el servidor web local (puertos 5000/5001). Verifica que los puertos estén disponibles. Detalle: {ex.Message}");
            });
        }

        private void BtnHamburguesa_Click(object sender, RoutedEventArgs e)
        {
            PanelLateral.Visibility = PanelLateral.Visibility == Visibility.Visible
                ? Visibility.Collapsed
                : Visibility.Visible;
        }

        private void NavInicio_Click(object sender, RoutedEventArgs e)
        {
            if (ContenedorPrincipal.Content is WelcomeView)
            {
                PanelLateral.Visibility = Visibility.Collapsed;
                return;
            }

            NavegarAVista(new WelcomeView());
        }

        private void NavCronometraje_Click(object sender, RoutedEventArgs e)
        {
            if (ContenedorPrincipal.Content is TimingView)
            {
                PanelLateral.Visibility = Visibility.Collapsed;
                return;
            }

            NavegarAVista(new TimingView());
        }

        private void BtnCerrarToast_Click(object sender, RoutedEventArgs e)
        {
            OcultarNotificacionGlobal();
        }

        private void NavCorredores_Click(object sender, RoutedEventArgs e)
        {
            if (AppState.CarreraActiva == null)
            {
                PanelLateral.Visibility = Visibility.Collapsed;
                MostrarNotificacionGlobal(
                    NivelFeedback.Advertencia,
                    "Selecciona o crea una carrera en Inicio antes de abrir Gestión de Corredores.");
                return;
            }

            NavegarAVista(new CorredoresView());
        }

        private void NavHits_Click(object sender, RoutedEventArgs e)
        {
            if (AppState.CarreraActiva == null)
            {
                PanelLateral.Visibility = Visibility.Collapsed;
                MostrarNotificacionGlobal(
                    NivelFeedback.Advertencia,
                    "Selecciona o crea una carrera en Inicio antes de abrir Configuración de Hits.");
                return;
            }

            NavegarAVista(new HitsView());
        }

        protected override void OnClosed(EventArgs e)
        {
            _estaCerrada = true;
            _toastVersion++;
            DetenerYLiberarToastTimer();
            ToastGlobalBorder.BeginAnimation(OpacityProperty, null);

            try
            {
                _cerrarCts.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // Ignorar si ya fue liberado
            }

            WebServer.NuevaFotoRecibida -= WebServer_NuevaFotoRecibida;
            WebServer.FotoAsignadaRemota -= WebServer_FotoAsignadaRemota;
            WebServer.FotoDescartadaRemota -= WebServer_FotoDescartadaRemota;
            WebServer.ErrorCriticoServidor -= WebServer_ErrorCriticoServidor;
            AppState.CarreraActivaCambiada -= AppState_CarreraActivaCambiada;

            CerrarPizarra();
            _cerrarCts.Dispose();

            base.OnClosed(e);
        }
    }
}
