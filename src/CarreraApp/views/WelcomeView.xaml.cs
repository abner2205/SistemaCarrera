using System;
using System.Diagnostics;
using System.IO;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using CarreraApp.Data;
using CarreraApp.Models;
using CarreraApp.Server;
using QRCoder;

namespace CarreraApp.UI.Views
{
    public partial class WelcomeView : UserControl
    {
        private readonly RaceDataService _raceDataService = new();
        private CancellationTokenSource? _viewCts;

        public WelcomeView()
        {
            InitializeComponent();
            InputFechaCarrera.SelectedDate = DateTime.Now;
            Loaded += WelcomeView_Loaded;
            Unloaded += WelcomeView_Unloaded;
        }

        private void WelcomeView_Loaded(object sender, RoutedEventArgs e)
        {
            CancelarOperacionesPendientes();
            _viewCts = new CancellationTokenSource();
            CancellationToken ct = _viewCts.Token;

            _ = CargarCarrerasAsync(ct);
            _ = GenerarCodigoQRAsync(ct);
        }

        private void WelcomeView_Unloaded(object sender, RoutedEventArgs e)
        {
            CancelarOperacionesPendientes();
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

                var carreras = await _raceDataService.ObtenerCarrerasAsync();
                if (!cancellationToken.IsCancellationRequested)
                {
                    ListaCarreras.ItemsSource = carreras;
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // Vista descargada antes de completar la carga
            }
            catch (Exception ex)
            {
                if (!cancellationToken.IsCancellationRequested)
                {
                    MessageBox.Show($"Error al cargar las carreras: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
        }

        private async void BtnCrearCarrera_Click(object sender, RoutedEventArgs e)
        {
            BtnCrearCarrera.IsEnabled = false;
            try
            {
                await App.InicializacionBaseDatosTask;

                OperacionResultado<Carrera> resultado = await _raceDataService.CrearOActualizarCarreraAsync(
                    null,
                    InputNombreCarrera.Text,
                    InputFechaCarrera.SelectedDate);

                if (!resultado.Exito || resultado.Entidad == null)
                {
                    MessageBox.Show(resultado.Mensaje, "Validación de Carrera", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                AbrirPanelGestion(resultado.Entidad);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error al crear la carrera: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                BtnCrearCarrera.IsEnabled = true;
            }
        }

        private void CarreraExistente_Click(object sender, MouseButtonEventArgs e)
        {
            if (sender is Border border && border.DataContext is Carrera carreraSeleccionada)
            {
                AbrirPanelGestion(carreraSeleccionada);
            }
        }

        private void AbrirPanelGestion(Carrera carrera)
        {
            // RF-13: cambiar carrera activa limpiando pizarra, cola UI y cola del servidor web
            AppState.CambiarCarreraActiva(carrera);

            var mainWindow = (Window.GetWindow(this) as MainWindow) ?? (Application.Current?.MainWindow as MainWindow);
            if (mainWindow != null)
            {
                mainWindow.ActualizarTituloCarrera();
                mainWindow.NavegarAVista(new TimingView());
            }
        }
    }
}
