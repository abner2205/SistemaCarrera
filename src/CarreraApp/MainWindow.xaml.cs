using System;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
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
            PanelLateral.Visibility = Visibility.Collapsed;
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
            if (AppState.CarreraActiva != null)
            {
                TxtNombreCarrera.Text = $"CARRERA APP - {AppState.CarreraActiva.Nombre.ToUpper()}";
            }
            else
            {
                TxtNombreCarrera.Text = "CARRERA APP";
            }
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
                MessageBox.Show(
                    $"No se pudo iniciar o mantener activo el servidor web local (puertos 5000/5001).\nVerifica que los puertos estén disponibles.\n\nDetalle: {ex.Message}",
                    "Error del Servidor Web",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
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

        private void NavCorredores_Click(object sender, RoutedEventArgs e)
        {
            if (AppState.CarreraActiva == null)
            {
                MessageBox.Show("Por favor, selecciona o crea una carrera en el Inicio primero.", "Atención", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            NavegarAVista(new CorredoresView());
        }

        private void NavHits_Click(object sender, RoutedEventArgs e)
        {
            if (AppState.CarreraActiva == null)
            {
                MessageBox.Show("Por favor, selecciona o crea una carrera en el Inicio primero.", "Atención", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            NavegarAVista(new HitsView());
        }

        protected override void OnClosed(EventArgs e)
        {
            _estaCerrada = true;

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
