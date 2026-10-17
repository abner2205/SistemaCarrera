using System.Windows;
using CarreraApp.UI.Views;
using CarreraApp.Models;
using CarreraApp.Data;
using System.IO;
using Microsoft.EntityFrameworkCore;

namespace CarreraApp.UI
{
    public partial class MainWindow : Window
    {
        public MainWindow()
        {
            InitializeComponent();
            
            // Escuchar al servidor globalmente
            Server.WebServer.NuevaFotoRecibida += WebServer_NuevaFotoRecibida;
            Server.WebServer.DorsalAsignadoRemoto += WebServer_DorsalAsignadoRemoto;
            Server.WebServer.FotoDescartadaRemota += WebServer_FotoDescartadaRemota;

            ContenedorPrincipal.Content = new Views.WelcomeView();
        }

        // 1. Recibe la foto siempre, sin importar la pestaña abierta
        private void WebServer_NuevaFotoRecibida(string idFoto, DateTime horaLlegada, string rutaFoto)
        {
            Application.Current.Dispatcher.Invoke(() =>
            {
                AppState.FotosEnEsperaGlobal.Add(new FotoPendiente { IdFoto = idFoto, Hora_Llegada = horaLlegada, Ruta_Foto = rutaFoto });
            });
        }

        // 2. Procesa la asignación remota y mueve el archivo en segundo plano
        private async void WebServer_DorsalAsignadoRemoto(string idFoto, int dorsalNum)
        {
            try
            {
                FotoPendiente? fotoActual = null;
                Application.Current.Dispatcher.Invoke(() =>
                {
                    fotoActual = AppState.FotosEnEsperaGlobal.FirstOrDefault(f => f.IdFoto == idFoto);
                });

                // Respaldo: si la foto aún no entró a la colección de UI, buscarla físicamente en Fotos_Temp
                if (fotoActual == null)
                {
                    string rutaTemp = Path.Combine(Helpers.StorageHelper.ObtenerCarpetaFotosTemp(), $"{idFoto}.jpg");
                    if (File.Exists(rutaTemp))
                    {
                        fotoActual = new FotoPendiente
                        {
                            IdFoto = idFoto,
                            Hora_Llegada = DateTime.Now,
                            Ruta_Foto = rutaTemp
                        };
                    }
                }

                if (fotoActual == null)
                {
                    Application.Current.Dispatcher.Invoke(() =>
                        AppState.RegistrosExitososGlobal.Insert(0, $"[AVISO REMOTO] Foto {idFoto} no encontrada para dorsal {dorsalNum}."));
                    return;
                }

                // Si no hay carrera activa seleccionada en la UI, tomar la carrera más reciente de la BD
                if (AppState.CarreraActiva == null)
                {
                    using var db = new CarreraApp.Data.ApplicationDbContext();
                    AppState.CarreraActiva = await db.Carreras.OrderByDescending(c => c.Fecha).FirstOrDefaultAsync();
                }

                if (AppState.CarreraActiva == null)
                {
                    Application.Current.Dispatcher.Invoke(() =>
                        AppState.RegistrosExitososGlobal.Insert(0, $"[RECHAZADO REMOTO] No hay carreras registradas en el sistema."));
                    return;
                }

                var dataService = new CarreraApp.Data.RaceDataService();
                var resultado = await dataService.AsignarTiempoAsync(
                    AppState.CarreraActiva.ID_Carrera,
                    AppState.CarreraActiva.Nombre,
                    dorsalNum,
                    fotoActual.Hora_Llegada,
                    fotoActual.Ruta_Foto
                );

                Application.Current.Dispatcher.Invoke(() =>
                {
                    if (resultado.Exito)
                    {
                        AppState.NotificarNuevoTiempo();
                        AppState.RegistrosExitososGlobal.Insert(0, $"[{DateTime.Now:HH:mm:ss}] Remoto: {resultado.Mensaje}");
                        if (resultado.HitFinalizado)
                        {
                            AppState.RegistrosExitososGlobal.Insert(0, $"[HIT FINALIZADO] ¡Todos los corredores de '{resultado.NombreHitFinalizado}' han llegado a la meta!");
                        }
                        AppState.FotosEnEsperaGlobal.Remove(fotoActual);
                        Server.WebServer.EliminarFotoPendiente(idFoto);
                    }
                    else
                    {
                        AppState.RegistrosExitososGlobal.Insert(0, $"[RECHAZADO REMOTO] {resultado.Mensaje}");
                    }
                });
            }
            catch (Exception ex)
            {
                Application.Current.Dispatcher.Invoke(() => 
                    AppState.RegistrosExitososGlobal.Insert(0, $"[CRÍTICO] Fallo remoto: {ex.Message}"));
            }
        }

        // Mostrar / Ocultar menú lateral
        private void BtnHamburguesa_Click(object sender, RoutedEventArgs e)
        {
            PanelLateral.Visibility = PanelLateral.Visibility == Visibility.Visible 
                                      ? Visibility.Collapsed 
                                      : Visibility.Visible;
        }

        // Navegación a Inicio
        private void NavInicio_Click(object sender, RoutedEventArgs e)
        {
            ContenedorPrincipal.Content = new WelcomeView();
            PanelLateral.Visibility = Visibility.Collapsed; // Auto-ocultar menú al hacer clic
        }

        // Navegación al Cronometraje
        private void NavCronometraje_Click(object sender, RoutedEventArgs e)
        {
            ContenedorPrincipal.Content = new TimingView();
            PanelLateral.Visibility = Visibility.Collapsed;
        }
        private void NavCorredores_Click(object sender, RoutedEventArgs e)
        {
            if (Models.AppState.CarreraActiva == null)
            {
                MessageBox.Show("Por favor, selecciona o crea una carrera en el Inicio primero.", "Atención", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            ContenedorPrincipal.Content = new Views.CorredoresView();
            PanelLateral.Visibility = Visibility.Collapsed;
        }

        private void NavHits_Click(object sender, RoutedEventArgs e)
        {
            if (Models.AppState.CarreraActiva == null)
            {
                MessageBox.Show("Por favor, selecciona o crea una carrera en el Inicio primero.", "Atención", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            ContenedorPrincipal.Content = new Views.HitsView();
            PanelLateral.Visibility = Visibility.Collapsed;
        }
        // Método público para actualizar el título desde otras vistas
        public void ActualizarTituloCarrera()
        {
            if (Models.AppState.CarreraActiva != null)
            {
                TxtNombreCarrera.Text = $"CARRERA APP - {Models.AppState.CarreraActiva.Nombre.ToUpper()}";
            }
            else
            {
                TxtNombreCarrera.Text = "CARRERA APP";
            }
        }
        private void WebServer_FotoDescartadaRemota(string idFoto)
        {
            Application.Current.Dispatcher.Invoke(() =>
            {
                var fotoActual = AppState.FotosEnEsperaGlobal.FirstOrDefault(f => f.IdFoto == idFoto);
                if (fotoActual != null)
                {
                    if (System.IO.File.Exists(fotoActual.Ruta_Foto))
                    {
                        try { System.IO.File.Delete(fotoActual.Ruta_Foto); } catch { }
                    }
                    
                    Server.WebServer.EliminarFotoPendiente(fotoActual.IdFoto);
                    AppState.FotosEnEsperaGlobal.Remove(fotoActual);
                }
            });
        }
    }
}