using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using CarreraApp.Data;
using CarreraApp.Helpers;
using CarreraApp.Models;
using CarreraApp.Server;
using QRCoder;

namespace CarreraApp.UI.Views
{
    public partial class WelcomeView : UserControl
    {
        private readonly RaceDataService _raceDataService = new();
        private CancellationTokenSource? _viewCts;
        private DispatcherTimer? _feedbackTimer;
        private bool _estaDescargado;

        public WelcomeView()
        {
            InitializeComponent();
            InputFechaCarrera.SelectedDate = DateTime.Now;
            Loaded += WelcomeView_Loaded;
            Unloaded += WelcomeView_Unloaded;
        }

        private void WelcomeView_Loaded(object sender, RoutedEventArgs e)
        {
            _estaDescargado = false;
            CancelarOperacionesPendientes();
            _viewCts = new CancellationTokenSource();
            CancellationToken ct = _viewCts.Token;

            ActualizarEstadoSeleccion();
            _ = CargarCarrerasAsync(ct);
            _ = GenerarCodigoQRAsync(ct);
        }

        private void WelcomeView_Unloaded(object sender, RoutedEventArgs e)
        {
            _estaDescargado = true;
            DetenerFeedbackTimer();
            CancelarOperacionesPendientes();
        }

        private void DetenerFeedbackTimer()
        {
            if (_feedbackTimer != null)
            {
                _feedbackTimer.Stop();
                _feedbackTimer.Tick -= FeedbackTimer_Tick;
                _feedbackTimer = null;
            }
        }

        private void CancelarOperacionesPendientes()
        {
            CancellationTokenSource? anterior = _viewCts;
            _viewCts = null;
            if (anterior == null)
            {
                return;
            }

            try
            {
                anterior.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // Ignorar si ya fue dispuesto
            }
            finally
            {
                anterior.Dispose();
            }
        }

        private async Task GenerarCodigoQRAsync(CancellationToken cancellationToken)
        {
            try
            {
                string token = WebServer.TokenSesionActual;
                TxtTokenSesion.Text = token;

                (string UrlServidor, BitmapImage QrBitmap) resultado = await Task.Run(() =>
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    string ip = ObtenerIPLocal();
                    string urlServidor = $"https://{ip}:5001/index.html?token={Uri.EscapeDataString(token)}";

                    using var qrGenerator = new QRCodeGenerator();
                    QRCodeData qrCodeData = qrGenerator.CreateQrCode(urlServidor, QRCodeGenerator.ECCLevel.Q);
                    using var qrCode = new PngByteQRCode(qrCodeData);
                    byte[] qrBytes = qrCode.GetGraphic(20);

                    using var ms = new MemoryStream(qrBytes);
                    var bitmapImage = new BitmapImage();
                    bitmapImage.BeginInit();
                    bitmapImage.StreamSource = ms;
                    bitmapImage.CacheOption = BitmapCacheOption.OnLoad;
                    bitmapImage.EndInit();
                    bitmapImage.Freeze();

                    return (urlServidor, bitmapImage);
                }, cancellationToken);

                if (cancellationToken.IsCancellationRequested)
                {
                    return;
                }

                TxtUrlServer.Text = resultado.UrlServidor;
                TxtTokenSesion.Text = token;
                QrImage.Source = resultado.QrBitmap;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // Vista descargada antes de completar la generación de QR
            }
            catch (Exception ex)
            {
                Trace.TraceWarning($"[WelcomeView] Error al generar código QR: {ex.Message}");
            }
        }

        private static string ObtenerIPLocal()
        {
            string ipRespaldo = "127.0.0.1";

            foreach (NetworkInterface item in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (item.OperationalStatus == OperationalStatus.Up &&
                   (item.NetworkInterfaceType == NetworkInterfaceType.Ethernet ||
                    item.NetworkInterfaceType == NetworkInterfaceType.Wireless80211))
                {
                    string nombre = item.Name.ToLowerInvariant();
                    string descripcion = item.Description.ToLowerInvariant();

                    bool esVirtual = nombre.Contains("radmin") || descripcion.Contains("radmin") ||
                                     nombre.Contains("virtual") || descripcion.Contains("virtual") ||
                                     nombre.Contains("vmware") || descripcion.Contains("vmware") ||
                                     nombre.Contains("hamachi") || nombre.Contains("vethernet");

                    foreach (UnicastIPAddressInformation ip in item.GetIPProperties().UnicastAddresses)
                    {
                        if (ip.Address.AddressFamily == AddressFamily.InterNetwork)
                        {
                            if (!esVirtual)
                            {
                                return ip.Address.ToString();
                            }

                            ipRespaldo = ip.Address.ToString();
                        }
                    }
                }
            }

            return ipRespaldo;
        }

        private async Task CargarCarrerasAsync(CancellationToken cancellationToken)
        {
            try
            {
                await App.InicializacionBaseDatosTask;
                if (cancellationToken.IsCancellationRequested)
                {
                    return;
                }

                List<Carrera> carreras = await _raceDataService.ObtenerCarrerasAsync();
                if (cancellationToken.IsCancellationRequested)
                {
                    return;
                }

                ListaCarreras.ItemsSource = carreras;

                bool hayCarreras = carreras.Count > 0;
                ListaCarreras.Visibility = hayCarreras ? Visibility.Visible : Visibility.Collapsed;
                PanelEstadoVacioCarreras.Visibility = hayCarreras ? Visibility.Collapsed : Visibility.Visible;

                if (hayCarreras && AppState.CarreraActiva != null)
                {
                    Carrera? carreraActivaEnLista = carreras.FirstOrDefault(c => c.ID_Carrera == AppState.CarreraActiva.ID_Carrera);
                    if (carreraActivaEnLista != null)
                    {
                        ListaCarreras.SelectedItem = carreraActivaEnLista;
                    }
                }

                ActualizarEstadoSeleccion();
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // Vista descargada antes de completar la carga
            }
            catch (Exception ex)
            {
                if (!cancellationToken.IsCancellationRequested)
                {
                    MostrarFeedback(NivelFeedback.Error, $"No se pudieron cargar las carreras: {ex.Message}");
                }
            }
        }

        private void ActualizarEstadoSeleccion()
        {
            if (ListaCarreras.SelectedItem is Carrera seleccionada)
            {
                BtnAbrirCarrera.IsEnabled = true;
                BtnAbrirCarrera.Content = "Abrir carrera seleccionada";
                TxtResumenSeleccion.Text = $"Seleccionada: {seleccionada.Nombre}";
            }
            else
            {
                BtnAbrirCarrera.IsEnabled = false;
                BtnAbrirCarrera.Content = "Abrir carrera seleccionada";
                TxtResumenSeleccion.Text = ListaCarreras.Items.Count > 0
                    ? "Selecciona una carrera de la lista"
                    : "Sin carreras registradas";
            }

            TxtEstadoCarreraActiva.Text = AppState.CarreraActiva != null
                ? $"Activa ahora: {AppState.CarreraActiva.Nombre}"
                : "Ninguna carrera activa";
        }

        private void ListaCarreras_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            ActualizarEstadoSeleccion();
        }

        private void ListaCarreras_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (ListaCarreras.SelectedItem is Carrera carreraSeleccionada)
            {
                AbrirPanelGestion(carreraSeleccionada);
            }
        }

        private void ListaCarreras_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter && ListaCarreras.SelectedItem is Carrera carreraSeleccionada)
            {
                e.Handled = true;
                AbrirPanelGestion(carreraSeleccionada);
            }
        }

        private void BtnAbrirCarrera_Click(object sender, RoutedEventArgs e)
        {
            if (ListaCarreras.SelectedItem is Carrera carreraSeleccionada)
            {
                AbrirPanelGestion(carreraSeleccionada);
            }
        }

        private void BtnIrACrearCarrera_Click(object sender, RoutedEventArgs e)
        {
            LimpiarErroresFormulario();
            InputNombreCarrera.Focus();
            Keyboard.Focus(InputNombreCarrera);
        }

        private void InputNombreCarrera_TextChanged(object sender, TextChangedEventArgs e)
        {
            LimpiarErrorNombre();
        }

        private void InputNombreCarrera_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                e.Handled = true;
                BtnCrearCarrera_Click(BtnCrearCarrera, new RoutedEventArgs());
            }
        }

        private void InputFechaCarrera_SelectedDateChanged(object? sender, SelectionChangedEventArgs e)
        {
            LimpiarErrorFecha();
        }

        private async void BtnCrearCarrera_Click(object sender, RoutedEventArgs e)
        {
            LimpiarErroresFormulario();

            string nombreIngresado = InputNombreCarrera.Text?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(nombreIngresado))
            {
                MostrarErrorNombre("Escribe un nombre para la carrera antes de continuar.");
                InputNombreCarrera.Focus();
                return;
            }

            if (InputFechaCarrera.SelectedDate == null)
            {
                MostrarErrorFecha("Selecciona una fecha válida para el evento.");
                InputFechaCarrera.Focus();
                return;
            }

            BtnCrearCarrera.IsEnabled = false;
            try
            {
                await App.InicializacionBaseDatosTask;

                OperacionResultado<Carrera> resultado = await _raceDataService.CrearOActualizarCarreraAsync(
                    null,
                    nombreIngresado,
                    InputFechaCarrera.SelectedDate);

                if (!resultado.Exito || resultado.Entidad == null)
                {
                    if (resultado.Mensaje.Contains("fecha", StringComparison.OrdinalIgnoreCase))
                    {
                        MostrarErrorFecha(resultado.Mensaje);
                        InputFechaCarrera.Focus();
                    }
                    else
                    {
                        MostrarErrorNombre(resultado.Mensaje);
                        InputNombreCarrera.Focus();
                    }
                    return;
                }

                AbrirPanelGestion(
                    resultado.Entidad,
                    $"Carrera \"{resultado.Entidad.Nombre}\" creada y activada.");
            }
            catch (Exception ex)
            {
                MostrarFeedback(NivelFeedback.Error, $"No se pudo crear la carrera: {ex.Message}");
            }
            finally
            {
                BtnCrearCarrera.IsEnabled = true;
            }
        }

        private void BtnCopiarUrl_Click(object sender, RoutedEventArgs e)
        {
            CopiarAlPortapapeles(
                TxtUrlServer.Text,
                "Enlace del servidor copiado al portapapeles.",
                "El enlace del servidor aún no está listo.");
        }

        private void BtnCopiarToken_Click(object sender, RoutedEventArgs e)
        {
            CopiarAlPortapapeles(
                TxtTokenSesion.Text,
                "Token de sesión copiado al portapapeles.",
                "El token de sesión aún no está disponible.");
        }

        private void CopiarAlPortapapeles(string? texto, string mensajeExito, string mensajeVacio)
        {
            if (string.IsNullOrWhiteSpace(texto))
            {
                MostrarFeedback(NivelFeedback.Advertencia, mensajeVacio);
                return;
            }

            try
            {
                Clipboard.SetText(texto);
                MostrarFeedback(NivelFeedback.Exito, mensajeExito);
            }
            catch (Exception ex)
            {
                Trace.TraceWarning($"[WelcomeView] No se pudo copiar al portapapeles: {ex.Message}");
                MostrarFeedback(NivelFeedback.Advertencia, "No se pudo acceder al portapapeles. Copia el texto manualmente.");
            }
        }

        private void MostrarErrorNombre(string mensaje)
        {
            InputNombreCarrera.BorderBrush = CrearBrushCongelado(UiFeedbackHelper.TokensPaleta.PenaltyCrimson);
            InputNombreCarrera.BorderThickness = new Thickness(2);
            TxtErrorNombreCarrera.Text = mensaje;
            TxtErrorNombreCarrera.Visibility = Visibility.Visible;
        }

        private void LimpiarErrorNombre()
        {
            InputNombreCarrera.ClearValue(BorderBrushProperty);
            InputNombreCarrera.ClearValue(BorderThicknessProperty);
            TxtErrorNombreCarrera.Text = string.Empty;
            TxtErrorNombreCarrera.Visibility = Visibility.Collapsed;
        }

        private void MostrarErrorFecha(string mensaje)
        {
            InputFechaCarrera.BorderBrush = CrearBrushCongelado(UiFeedbackHelper.TokensPaleta.PenaltyCrimson);
            InputFechaCarrera.BorderThickness = new Thickness(2);
            TxtErrorFechaCarrera.Text = mensaje;
            TxtErrorFechaCarrera.Visibility = Visibility.Visible;
        }

        private void LimpiarErrorFecha()
        {
            InputFechaCarrera.ClearValue(BorderBrushProperty);
            InputFechaCarrera.ClearValue(BorderThicknessProperty);
            TxtErrorFechaCarrera.Text = string.Empty;
            TxtErrorFechaCarrera.Visibility = Visibility.Collapsed;
        }

        private void LimpiarErroresFormulario()
        {
            LimpiarErrorNombre();
            LimpiarErrorFecha();
        }

        private void AbrirPanelGestion(Carrera carrera, string? mensajeConfirmacion = null)
        {
            // RF-13: cambiar carrera activa limpiando pizarra, cola UI y cola del servidor web
            AppState.CambiarCarreraActiva(carrera);

            var mainWindow = ObtenerMainWindow();
            if (mainWindow != null)
            {
                mainWindow.ActualizarTituloCarrera();
                mainWindow.NavegarAVista(new TimingView());
                mainWindow.MostrarNotificacionGlobal(
                    NivelFeedback.Exito,
                    mensajeConfirmacion ?? $"Carrera \"{carrera.Nombre}\" abierta.");
            }
            else if (!string.IsNullOrWhiteSpace(mensajeConfirmacion))
            {
                MostrarBannerEnLinea(NivelFeedback.Exito, mensajeConfirmacion);
            }
        }

        private void MostrarFeedback(NivelFeedback nivel, string mensaje)
        {
            var mainWindow = ObtenerMainWindow();
            if (mainWindow != null)
            {
                mainWindow.MostrarNotificacionGlobal(nivel, mensaje);
                return;
            }

            MostrarBannerEnLinea(nivel, mensaje);
        }

        private void MostrarBannerEnLinea(NivelFeedback nivel, string mensaje)
        {
            if (_estaDescargado || Dispatcher.HasShutdownStarted)
            {
                return;
            }

            ConfiguracionAvisoFeedback config = UiFeedbackHelper.CrearConfiguracionAviso(nivel, mensaje);
            DetenerFeedbackTimer();

            Brush fondo = CrearBrushCongelado(config.ColorFondoHex);
            Brush borde = CrearBrushCongelado(config.ColorBordeHex);
            Brush texto = CrearBrushCongelado(config.ColorTextoHex);

            BannerFeedbackWelcome.Background = fondo;
            BannerFeedbackWelcome.BorderBrush = borde;
            TxtBannerFeedbackWelcome.Foreground = texto;
            BtnCerrarBannerWelcome.Foreground = texto;
            TxtBannerFeedbackWelcome.Text = config.Mensaje;
            BannerFeedbackWelcome.Visibility = Visibility.Visible;

            if (!config.RequiereCierreManual && config.DuracionAutoOcultamientoMs > 0)
            {
                _feedbackTimer = new DispatcherTimer(DispatcherPriority.Normal, Dispatcher)
                {
                    Interval = TimeSpan.FromMilliseconds(config.DuracionAutoOcultamientoMs)
                };
                _feedbackTimer.Tick += FeedbackTimer_Tick;
                _feedbackTimer.Start();
            }
        }

        private void FeedbackTimer_Tick(object? sender, EventArgs e)
        {
            OcultarBannerEnLinea();
        }

        private void BtnCerrarBannerWelcome_Click(object sender, RoutedEventArgs e)
        {
            OcultarBannerEnLinea();
        }

        private void OcultarBannerEnLinea()
        {
            DetenerFeedbackTimer();
            BannerFeedbackWelcome.Visibility = Visibility.Collapsed;
            TxtBannerFeedbackWelcome.Text = string.Empty;
        }

        private MainWindow? ObtenerMainWindow()
        {
            return (Window.GetWindow(this) as MainWindow) ?? (Application.Current?.MainWindow as MainWindow);
        }

        private static SolidColorBrush CrearBrushCongelado(string hex)
        {
            var brush = (SolidColorBrush)new BrushConverter().ConvertFromString(hex)!;
            brush.Freeze();
            return brush;
        }
    }
}
