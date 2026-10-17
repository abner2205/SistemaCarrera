using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.EntityFrameworkCore;
using CarreraApp.Models;
using CarreraApp.Data;
using QRCoder;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Windows.Media.Imaging;
using System.Net.NetworkInformation;
namespace CarreraApp.UI.Views
{
    public partial class WelcomeView : UserControl
    {
        public WelcomeView()
        {
            InitializeComponent();
            InputFechaCarrera.SelectedDate = DateTime.Now;
            CargarCarreras();
            GenerarCodigoQR(); // Nueva llamada
        }

        private void GenerarCodigoQR()
        {
            string ip = ObtenerIPLocal();
            string urlServidor = $"https://{ip}:5001/index.html";
            TxtUrlServer.Text = urlServidor;

            // Generar el código QR sin depender de librerías gráficas antiguas
            using (QRCodeGenerator qrGenerator = new QRCodeGenerator())
            {
                QRCodeData qrCodeData = qrGenerator.CreateQrCode(urlServidor, QRCodeGenerator.ECCLevel.Q);
                using (PngByteQRCode qrCode = new PngByteQRCode(qrCodeData))
                {
                    byte[] qrBytes = qrCode.GetGraphic(20);
                    using (MemoryStream ms = new MemoryStream(qrBytes))
                    {
                        BitmapImage bitmapImage = new BitmapImage();
                        bitmapImage.BeginInit();
                        bitmapImage.StreamSource = ms;
                        bitmapImage.CacheOption = BitmapCacheOption.OnLoad;
                        bitmapImage.EndInit();
                        
                        QrImage.Source = bitmapImage;
                    }
                }
            }
        }

        private string ObtenerIPLocal()
        {
            string ipRespaldo = "127.0.0.1";

            // Inspeccionar todas las tarjetas de red de la computadora
            foreach (NetworkInterface item in NetworkInterface.GetAllNetworkInterfaces())
            {
                // Solo nos interesan las conexiones activas (Up) y que sean Ethernet o Wi-Fi
                if (item.OperationalStatus == OperationalStatus.Up &&
                   (item.NetworkInterfaceType == NetworkInterfaceType.Ethernet || 
                    item.NetworkInterfaceType == NetworkInterfaceType.Wireless80211))
                {
                    string nombre = item.Name.ToLower();
                    string descripcion = item.Description.ToLower();

                    // Filtrar adaptadores virtuales conocidos (Radmin, VMware, VirtualBox, Hamachi)
                    bool esVirtual = nombre.Contains("radmin") || descripcion.Contains("radmin") ||
                                     nombre.Contains("virtual") || descripcion.Contains("virtual") ||
                                     nombre.Contains("vmware") || descripcion.Contains("vmware") ||
                                     nombre.Contains("hamachi") || nombre.Contains("vethernet");

                    foreach (UnicastIPAddressInformation ip in item.GetIPProperties().UnicastAddresses)
                    {
                        // Asegurarnos de que sea una dirección IPv4 tradicional
                        if (ip.Address.AddressFamily == AddressFamily.InterNetwork)
                        {
                            if (!esVirtual)
                            {
                                // Si no es virtual, es nuestra tarjeta física real. Devolvemos esta.
                                return ip.Address.ToString();
                            }
                            else
                            {
                                // Si es virtual, la guardamos como respaldo por si no hay otra opción
                                ipRespaldo = ip.Address.ToString();
                            }
                        }
                    }
                }
            }

            // Si por alguna razón no encuentra la tarjeta física, devuelve el respaldo
            return ipRespaldo;
        }

        private async void CargarCarreras()
        {
            using (var db = new ApplicationDbContext())
            {
                // Ordenar por las más recientes primero
                var carreras = await db.Carreras.OrderByDescending(c => c.Fecha).ToListAsync();
                ListaCarreras.ItemsSource = carreras;
            }
        }

        private async void BtnCrearCarrera_Click(object sender, RoutedEventArgs e)
        {
            string nombre = InputNombreCarrera.Text.Trim();
            
            if (string.IsNullOrEmpty(nombre) || !InputFechaCarrera.SelectedDate.HasValue)
            {
                MessageBox.Show("Ingresa un nombre y una fecha válidos.", "Error", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var nuevaCarrera = new Carrera
            {
                Nombre = nombre,
                Fecha = InputFechaCarrera.SelectedDate.Value
            };

            using (var db = new ApplicationDbContext())
            {
                db.Carreras.Add(nuevaCarrera);
                await db.SaveChangesAsync();
            }

            AbrirPanelGestion(nuevaCarrera);
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
            // 1. Asignar la carrera al estado global
            AppState.CarreraActiva = carrera;

            // 2. Navegar a la pantalla de cronometraje y actualizar título
            if (Application.Current.MainWindow is MainWindow mainWindow)
            {
                mainWindow.ActualizarTituloCarrera();
                mainWindow.ContenedorPrincipal.Content = new TimingView();
            }
        }
    }
}