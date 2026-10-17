using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using CarreraApp.Models;
using Microsoft.EntityFrameworkCore;

namespace CarreraApp.UI.Views
{
    public partial class TimingView : UserControl
    {
        // Variable para controlar la ventana de posiciones
        private LeaderboardWindow? _ventanaPizarra;

        // Colección de Hits con soporte reactivo para cronómetro en tiempo real
        public ObservableCollection<HitTimerItem> ListaDeHits { get; set; }

        // Temporizador de alta precisión para el cronómetro en vivo de los hits
        private readonly DispatcherTimer _cronometroTimer;

        public TimingView()
        {
            InitializeComponent();
            ListaDeHits = new ObservableCollection<HitTimerItem>();
            ListaHits.ItemsSource = ListaDeHits;

            // Usar las colecciones globales
            ListaFotosPendientes.ItemsSource = AppState.FotosEnEsperaGlobal;
            LogExito.ItemsSource = AppState.RegistrosExitososGlobal;

            InputDorsal.KeyDown += InputDorsal_KeyDown;
            BtnAsignar.Click += BtnAsignar_Click;
            ListaFotosPendientes.SelectionChanged += ListaFotosPendientes_SelectionChanged;

            // Configurar el timer del cronómetro (actualización cada 500 ms)
            _cronometroTimer = new DispatcherTimer(DispatcherPriority.Normal)
            {
                Interval = TimeSpan.FromMilliseconds(500)
            };
            _cronometroTimer.Tick += CronometroTimer_Tick;

            Loaded += TimingView_Loaded;
            Unloaded += TimingView_Unloaded;

            GenerarDatosDePruebaBD();
        }

        private void TimingView_Loaded(object sender, RoutedEventArgs e)
        {
            // Suscribirse a nuevos registros (locales o remotos) para refrescar los hits y sus conteos
            AppState.NuevoTiempoRegistrado += OnNuevoTiempoRegistrado;
            _cronometroTimer.Start();
            CargarDatosReales();
            CargarCategoriasPizarra();
            ActualizarEstadoBotonesLogos();
        }

        private void TimingView_Unloaded(object sender, RoutedEventArgs e)
        {
            // Desuscribirse y detener timer para evitar fugas de memoria
            AppState.NuevoTiempoRegistrado -= OnNuevoTiempoRegistrado;
            _cronometroTimer.Stop();
        }

        private void OnNuevoTiempoRegistrado()
        {
            Dispatcher.Invoke(CargarDatosReales);
        }

        private void CronometroTimer_Tick(object? sender, EventArgs e)
        {
            foreach (var hit in ListaDeHits)
            {
                if (hit.EstaActivo)
                {
                    hit.ActualizarCronometro();
                }
            }
        }

        private async void CargarDatosReales()
        {
            if (AppState.CarreraActiva == null) return;
            int idCarreraActual = AppState.CarreraActiva.ID_Carrera;

            try
            {
                using var db = new Data.ApplicationDbContext();

                // 1. Obtener los hits de la carrera actual
                var hitsDb = await db.Hits.AsNoTracking()
                    .Where(h => h.ID_Carrera == idCarreraActual)
                    .ToListAsync();

                // 2. Conteo de corredores activos por hit
                var corredoresPorHit = await db.Corredores.AsNoTracking()
                    .Where(c => c.ID_Carrera == idCarreraActual && c.Estado == EstadoCorredor.Activo)
                    .GroupBy(c => c.ID_Hit)
                    .Select(g => new { ID_Hit = g.Key, Total = g.Count() })
                    .ToListAsync();

                // 3. Conteo de corredores que ya registraron tiempo por hit
                var tiemposPorHit = await (from t in db.Tiempos.AsNoTracking()
                                           join c in db.Corredores.AsNoTracking() on t.ID_Corredor equals c.ID_Corredor
                                           where c.ID_Carrera == idCarreraActual
                                           group t by c.ID_Hit into g
                                           select new { ID_Hit = g.Key, Llegaron = g.Count() })
                                           .ToListAsync();

                var dictTotal = corredoresPorHit.ToDictionary(x => x.ID_Hit, x => x.Total);
                var dictLlegaron = tiemposPorHit.ToDictionary(x => x.ID_Hit, x => x.Llegaron);

                Dispatcher.Invoke(() =>
                {
                    ListaDeHits.Clear();
                    foreach (var h in hitsDb)
                    {
                        int total = dictTotal.GetValueOrDefault(h.ID_Hit, 0);
                        int llegaron = dictLlegaron.GetValueOrDefault(h.ID_Hit, 0);

                        var item = new HitTimerItem
                        {
                            ID_Hit = h.ID_Hit,
                            Nombre_Hit = h.Nombre_Hit,
                            Hora_Inicio = h.Hora_Inicio,
                            Hora_Fin = h.Hora_Fin,
                            TotalCorredores = total,
                            CorredoresLlegaron = llegaron
                        };
                        item.NotificarCambioEstado();
                        ListaDeHits.Add(item);
                    }
                });
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error al cargar hits: {ex.Message}");
            }
        }

        private async void GenerarDatosDePruebaBD()
        {
            if (AppState.CarreraActiva == null) return;
            int idCarreraActual = AppState.CarreraActiva.ID_Carrera;

            using (var db = new Data.ApplicationDbContext())
            {
                if (!await db.Categorias.AnyAsync(c => c.ID_Carrera == idCarreraActual))
                {
                    var categoria = new Categoria { Nombre = "Libre General", ID_Carrera = idCarreraActual };
                    db.Categorias.Add(categoria);
                    await db.SaveChangesAsync();

                    var hit = new Hit { Nombre_Hit = "Hit 1 (Prueba)", ID_Carrera = idCarreraActual };
                    db.Hits.Add(hit);
                    await db.SaveChangesAsync();

                    var corredor = new Corredor 
                    { 
                        Dorsal = 45, 
                        Nombre = "Armando (Prueba)", 
                        ID_Categoria = categoria.ID_Categoria, 
                        ID_Hit = hit.ID_Hit,
                        ID_Carrera = idCarreraActual
                    };
                    db.Corredores.Add(corredor);
                    await db.SaveChangesAsync();
                }
            }
        }

        private void ListaFotosPendientes_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (ListaFotosPendientes.SelectedItem is FotoPendiente fotoSeleccionada)
            {
                VisorFotoPrincipal.Source = Helpers.ImageHelper.CargarSinBloqueo(fotoSeleccionada.Ruta_Foto);
                InputDorsal.Focus();
            }
            else
            {
                VisorFotoPrincipal.Source = null;
            }
        }

        private void InputDorsal_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                ProcesarAsignacion();
            }
        }

        private void BtnAsignar_Click(object sender, RoutedEventArgs e)
        {
            ProcesarAsignacion();
        }

        private void BtnDescartar_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (ListaFotosPendientes.SelectedItem is FotoPendiente fotoActual)
                {
                    VisorFotoPrincipal.Source = null;
                    if (System.IO.File.Exists(fotoActual.Ruta_Foto))
                    {
                        try { System.IO.File.Delete(fotoActual.Ruta_Foto); } catch { }
                    }
                    
                    Server.WebServer.EliminarFotoPendiente(fotoActual.IdFoto);
                    AppState.FotosEnEsperaGlobal.Remove(fotoActual);
                    
                    InputDorsal.Clear();
                    InputDorsal.Focus();
                    if (AppState.FotosEnEsperaGlobal.Any()) ListaFotosPendientes.SelectedIndex = 0;
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error al descartar: {ex.Message}", "Aviso", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private async void ProcesarAsignacion()
        {
            string dorsalTexto = InputDorsal.Text.Trim();
            if (string.IsNullOrEmpty(dorsalTexto) || !int.TryParse(dorsalTexto, out int dorsalNum)) 
            {
                InputDorsal.Clear(); 
                return;
            }

            if (!(ListaFotosPendientes.SelectedItem is FotoPendiente fotoActual)) return;
            if (AppState.CarreraActiva == null) return;
            int idCarreraActual = AppState.CarreraActiva.ID_Carrera;

            try
            {
                VisorFotoPrincipal.Source = null; // Liberar visor antes de mover archivo

                // Usar el servicio centralizado que contiene las validaciones de Hit Activo y concurrencia
                var dataService = new Data.RaceDataService();
                var resultado = await dataService.AsignarTiempoAsync(
                    idCarreraActual,
                    AppState.CarreraActiva.Nombre,
                    dorsalNum,
                    fotoActual.Hora_Llegada,
                    fotoActual.Ruta_Foto
                );

                if (resultado.Exito)
                {
                    AppState.NotificarNuevoTiempo();
                    AppState.RegistrosExitososGlobal.Insert(0, $"[{DateTime.Now:HH:mm:ss}] Corredor {dorsalNum} OK - {resultado.TiempoFormateado}");

                    if (resultado.HitFinalizado)
                    {
                        AppState.RegistrosExitososGlobal.Insert(0, $"[HIT FINALIZADO] ¡Todos los corredores de '{resultado.NombreHitFinalizado}' han llegado a la meta!");
                    }

                    Server.WebServer.EliminarFotoPendiente(fotoActual.IdFoto);
                    AppState.FotosEnEsperaGlobal.Remove(fotoActual);

                    InputDorsal.Clear();
                    InputDorsal.Focus();
                    if (AppState.FotosEnEsperaGlobal.Any()) ListaFotosPendientes.SelectedIndex = 0;

                    CargarDatosReales();
                }
                else
                {
                    // Validación lógica de Hit o Corredor rechazada
                    MessageBox.Show(resultado.Mensaje, "Asignación No Permitida", MessageBoxButton.OK, MessageBoxImage.Warning);
                    AppState.RegistrosExitososGlobal.Insert(0, $"[RECHAZADO] {resultado.Mensaje}");
                    InputDorsal.SelectAll();
                    InputDorsal.Focus();
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Fallo al procesar el tiempo: {ex.Message}", "Error del Sistema", MessageBoxButton.OK, MessageBoxImage.Error);
                InputDorsal.Clear();
            }
        }

        private async void BtnIniciarHit_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is int idHit)
            {
                try
                {
                    using var db = new Data.ApplicationDbContext();
                    var hit = await db.Hits.FindAsync(idHit);
                    if (hit != null && hit.Hora_Inicio == null)
                    {
                        hit.Hora_Inicio = DateTime.Now;
                        await db.SaveChangesAsync();

                        AppState.RegistrosExitososGlobal.Insert(0, $"[SISTEMA] {hit.Nombre_Hit} INICIADO a las {hit.Hora_Inicio:HH:mm:ss}");
                        CargarDatosReales();
                    }
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"Error al iniciar el hit: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
        }

        private async void CargarCategoriasPizarra()
        {
            if (AppState.CarreraActiva == null) return;
            int idCarreraActual = AppState.CarreraActiva.ID_Carrera;

            try
            {
                using var db = new Data.ApplicationDbContext();
                var categorias = await db.Categorias.AsNoTracking()
                    .Where(c => c.ID_Carrera == idCarreraActual)
                    .ToListAsync();

                ComboCategoriasPizarra.ItemsSource = categorias;

                if (categorias.Any())
                {
                    if (AppState.CategoriaPizarraActiva != null && categorias.Any(c => c.ID_Categoria == AppState.CategoriaPizarraActiva.ID_Categoria))
                    {
                        ComboCategoriasPizarra.SelectedValue = AppState.CategoriaPizarraActiva.ID_Categoria;
                    }
                    else
                    {
                        ComboCategoriasPizarra.SelectedIndex = 0;
                        AppState.CategoriaPizarraActiva = categorias[0];
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error al cargar categorías de pizarra: {ex.Message}");
            }
        }

        private void ComboCategoriasPizarra_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (ComboCategoriasPizarra.SelectedItem is Categoria cat)
            {
                AppState.CategoriaPizarraActiva = cat;
                AppState.NotificarCambioCategoriaPizarra();
                _ventanaPizarra?.ActualizarPizarra();
            }
        }

        private void BtnPizarra_Click(object sender, RoutedEventArgs e)
        {
            if (_ventanaPizarra == null || !_ventanaPizarra.IsLoaded)
            {
                _ventanaPizarra = new LeaderboardWindow();
                _ventanaPizarra.Closed += (s, args) =>
                {
                    _ventanaPizarra = null;
                    BtnPizarra.Content = "ABRIR PIZARRA";
                    BtnPizarra.Background = new System.Windows.Media.SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#6C757D"));
                };
                _ventanaPizarra.Show();
                BtnPizarra.Content = "CERRAR PIZARRA";
                BtnPizarra.Background = new System.Windows.Media.SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#DC3545"));
            }
            else
            {
                _ventanaPizarra.Close();
                _ventanaPizarra = null;
                BtnPizarra.Content = "ABRIR PIZARRA";
                BtnPizarra.Background = new System.Windows.Media.SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#6C757D"));
            }
        }

        private void BtnPantallaCompleta_Click(object sender, RoutedEventArgs e)
        {
            if (_ventanaPizarra != null && _ventanaPizarra.IsLoaded)
            {
                if (_ventanaPizarra.WindowStyle == WindowStyle.None)
                {
                    _ventanaPizarra.WindowStyle = WindowStyle.SingleBorderWindow;
                    _ventanaPizarra.WindowState = WindowState.Normal;
                    BtnPantallaCompleta.Content = "PANTALLA COMPLETA";
                }
                else
                {
                    _ventanaPizarra.WindowStyle = WindowStyle.None;
                    _ventanaPizarra.WindowState = WindowState.Maximized;
                    BtnPantallaCompleta.Content = "MODO VENTANA";
                }
            }
        }

        private async void BtnSubirLogoIzq_Click(object sender, RoutedEventArgs e)
        {
            await ProcesarSubidaLogo(esIzquierdo: true);
        }

        private async void BtnSubirLogoDer_Click(object sender, RoutedEventArgs e)
        {
            await ProcesarSubidaLogo(esIzquierdo: false);
        }

        private async void BtnQuitarLogoIzq_Click(object sender, RoutedEventArgs e)
        {
            await QuitarLogo(esIzquierdo: true);
        }

        private async void BtnQuitarLogoDer_Click(object sender, RoutedEventArgs e)
        {
            await QuitarLogo(esIzquierdo: false);
        }

        private async System.Threading.Tasks.Task ProcesarSubidaLogo(bool esIzquierdo)
        {
            if (AppState.CarreraActiva == null)
            {
                MessageBox.Show("Por favor, selecciona una carrera activa primero.", "Atención", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var openDialog = new Microsoft.Win32.OpenFileDialog
            {
                Title = esIzquierdo ? "Seleccionar Logo Izquierdo para la Pizarra" : "Seleccionar Logo Derecho para la Pizarra",
                Filter = "Archivos de Imagen (*.png;*.jpg;*.jpeg;*.webp)|*.png;*.jpg;*.jpeg;*.webp|Todos los archivos (*.*)|*.*"
            };

            if (openDialog.ShowDialog() == true)
            {
                try
                {
                    string carpetaLogos = Helpers.StorageHelper.ObtenerCarpetaLogos();

                    string extension = System.IO.Path.GetExtension(openDialog.FileName);
                    string lado = esIzquierdo ? "Izq" : "Der";
                    string nuevoNombre = $"Logo_{lado}_{AppState.CarreraActiva.ID_Carrera}_{Guid.NewGuid():N}{extension}";
                    string rutaDestino = System.IO.Path.Combine(carpetaLogos, nuevoNombre);

                    System.IO.File.Copy(openDialog.FileName, rutaDestino, overwrite: true);

                    using var db = new Data.ApplicationDbContext();
                    var carreraDb = await db.Carreras.FindAsync(AppState.CarreraActiva.ID_Carrera);
                    if (carreraDb != null)
                    {
                        if (esIzquierdo)
                        {
                            if (!string.IsNullOrEmpty(carreraDb.RutaLogoIzquierdo) && System.IO.File.Exists(carreraDb.RutaLogoIzquierdo))
                            {
                                try { System.IO.File.Delete(carreraDb.RutaLogoIzquierdo); } catch { }
                            }
                            carreraDb.RutaLogoIzquierdo = rutaDestino;
                            AppState.CarreraActiva.RutaLogoIzquierdo = rutaDestino;
                        }
                        else
                        {
                            if (!string.IsNullOrEmpty(carreraDb.RutaLogoDerecho) && System.IO.File.Exists(carreraDb.RutaLogoDerecho))
                            {
                                try { System.IO.File.Delete(carreraDb.RutaLogoDerecho); } catch { }
                            }
                            carreraDb.RutaLogoDerecho = rutaDestino;
                            AppState.CarreraActiva.RutaLogoDerecho = rutaDestino;
                        }
                        await db.SaveChangesAsync();
                    }

                    ActualizarEstadoBotonesLogos();
                    AppState.NotificarCambioCategoriaPizarra();
                    _ventanaPizarra?.ActualizarPizarra();

                    MessageBox.Show($"Logo {(esIzquierdo ? "izquierdo" : "derecho")} actualizado correctamente.", "Logo Actualizado", MessageBoxButton.OK, MessageBoxImage.Information);
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"Error al guardar logo: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
        }

        private async System.Threading.Tasks.Task QuitarLogo(bool esIzquierdo)
        {
            if (AppState.CarreraActiva == null) return;

            try
            {
                using var db = new Data.ApplicationDbContext();
                var carreraDb = await db.Carreras.FindAsync(AppState.CarreraActiva.ID_Carrera);
                if (carreraDb != null)
                {
                    if (esIzquierdo && !string.IsNullOrEmpty(carreraDb.RutaLogoIzquierdo))
                    {
                        if (System.IO.File.Exists(carreraDb.RutaLogoIzquierdo))
                        {
                            try { System.IO.File.Delete(carreraDb.RutaLogoIzquierdo); } catch { }
                        }
                        carreraDb.RutaLogoIzquierdo = null;
                        AppState.CarreraActiva.RutaLogoIzquierdo = null;
                    }
                    else if (!esIzquierdo && !string.IsNullOrEmpty(carreraDb.RutaLogoDerecho))
                    {
                        if (System.IO.File.Exists(carreraDb.RutaLogoDerecho))
                        {
                            try { System.IO.File.Delete(carreraDb.RutaLogoDerecho); } catch { }
                        }
                        carreraDb.RutaLogoDerecho = null;
                        AppState.CarreraActiva.RutaLogoDerecho = null;
                    }
                    await db.SaveChangesAsync();
                }

                ActualizarEstadoBotonesLogos();
                AppState.NotificarCambioCategoriaPizarra();
                _ventanaPizarra?.ActualizarPizarra();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error al quitar logo: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void ActualizarEstadoBotonesLogos()
        {
            if (AppState.CarreraActiva != null)
            {
                bool tieneIzq = !string.IsNullOrEmpty(AppState.CarreraActiva.RutaLogoIzquierdo) && System.IO.File.Exists(AppState.CarreraActiva.RutaLogoIzquierdo);
                bool tieneDer = !string.IsNullOrEmpty(AppState.CarreraActiva.RutaLogoDerecho) && System.IO.File.Exists(AppState.CarreraActiva.RutaLogoDerecho);

                BtnSubirLogoIzq.Content = tieneIzq ? "🖼️ Logo Izq ✓" : "🖼️ Logo Izq";
                BtnSubirLogoIzq.Background = tieneIzq 
                    ? new System.Windows.Media.SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#2B6CB0"))
                    : new System.Windows.Media.SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#4A5568"));
                BtnQuitarLogoIzq.Visibility = tieneIzq ? Visibility.Visible : Visibility.Collapsed;

                BtnSubirLogoDer.Content = tieneDer ? "🖼️ Logo Der ✓" : "🖼️ Logo Der";
                BtnSubirLogoDer.Background = tieneDer 
                    ? new System.Windows.Media.SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#2B6CB0"))
                    : new System.Windows.Media.SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#4A5568"));
                BtnQuitarLogoDer.Visibility = tieneDer ? Visibility.Visible : Visibility.Collapsed;
            }
        }
    }
}