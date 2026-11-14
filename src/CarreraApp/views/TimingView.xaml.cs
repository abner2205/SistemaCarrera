using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Diagnostics;
using System.IO;
using System.Linq;
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
using Microsoft.EntityFrameworkCore;

namespace CarreraApp.UI.Views
{
    public partial class TimingView : UserControl
    {
        private static readonly SolidColorBrush BrushPizarraCerrada = CrearPincelCongelado("#6C757D");
        private static readonly SolidColorBrush BrushPizarraAbierta = CrearPincelCongelado("#DC3545");
        private static readonly SolidColorBrush BrushLogoActivo = CrearPincelCongelado("#2B6CB0");
        private static readonly SolidColorBrush BrushLogoInactivo = CrearPincelCongelado("#4A5568");

        private readonly RaceDataService _raceDataService = new();
        private readonly ReloadVersionCoordinator _hitsReloadCoordinator = new();
        private readonly DispatcherTimer _cronometroTimer;
        private readonly ObservableCollection<FotoPendiente> _fotosPendientesVista = new();
        private readonly ObservableCollection<string> _registrosExitososVista = new();

        private MainWindow? _mainWindow;
        private CancellationTokenSource? _viewCts;
        private CancellationTokenSource? _visorCts;
        private bool _cargandoCategoriasPizarra;
        private bool _procesandoLocal;
        private bool _estaDescargado;

        public ObservableCollection<HitTimerItem> ListaDeHits { get; set; }

        public TimingView()
        {
            InitializeComponent();
            ListaDeHits = new ObservableCollection<HitTimerItem>();
            ListaHits.ItemsSource = ListaDeHits;

            // Colecciones locales sincronizadas con AppState únicamente mientras la vista está cargada (RF-31)
            SincronizarColeccionCompleta(_fotosPendientesVista, AppState.FotosEnEsperaGlobal);
            SincronizarColeccionCompleta(_registrosExitososVista, AppState.RegistrosExitososGlobal);
            ListaFotosPendientes.ItemsSource = _fotosPendientesVista;
            LogExito.ItemsSource = _registrosExitososVista;

            InputDorsal.KeyDown += InputDorsal_KeyDown;
            ListaFotosPendientes.SelectionChanged += ListaFotosPendientes_SelectionChanged;

            _cronometroTimer = new DispatcherTimer(DispatcherPriority.Normal)
            {
                Interval = TimeSpan.FromMilliseconds(500)
            };

            Loaded += TimingView_Loaded;
            Unloaded += TimingView_Unloaded;
        }

        private static SolidColorBrush CrearPincelCongelado(string hex)
        {
            var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
            brush.Freeze();
            return brush;
        }

        private void TimingView_Loaded(object sender, RoutedEventArgs e)
        {
            _estaDescargado = false;
            CancelarYCrearViewCts();

            SincronizarColeccionCompleta(_fotosPendientesVista, AppState.FotosEnEsperaGlobal);
            SincronizarColeccionCompleta(_registrosExitososVista, AppState.RegistrosExitososGlobal);

            ListaHits.ItemsSource ??= ListaDeHits;
            ListaFotosPendientes.ItemsSource ??= _fotosPendientesVista;
            LogExito.ItemsSource ??= _registrosExitososVista;

            AppState.FotosEnEsperaGlobal.CollectionChanged -= AppState_FotosEnEspera_CollectionChanged;
            AppState.FotosEnEsperaGlobal.CollectionChanged += AppState_FotosEnEspera_CollectionChanged;
            AppState.RegistrosExitososGlobal.CollectionChanged -= AppState_RegistrosExitosos_CollectionChanged;
            AppState.RegistrosExitososGlobal.CollectionChanged += AppState_RegistrosExitosos_CollectionChanged;

            _mainWindow = (Window.GetWindow(this) as MainWindow) ?? (Application.Current?.MainWindow as MainWindow);
            if (_mainWindow != null)
            {
                _mainWindow.EstadoPizarraCambiado -= MainWindow_EstadoPizarraCambiado;
                _mainWindow.EstadoPizarraCambiado += MainWindow_EstadoPizarraCambiado;
            }

            AppState.NuevoTiempoRegistrado -= OnNuevoTiempoRegistrado;
            AppState.NuevoTiempoRegistrado += OnNuevoTiempoRegistrado;
            AppState.CarreraActivaCambiada -= OnCarreraActivaCambiada;
            AppState.CarreraActivaCambiada += OnCarreraActivaCambiada;

            _cronometroTimer.Tick -= CronometroTimer_Tick;
            _cronometroTimer.Tick += CronometroTimer_Tick;
            _cronometroTimer.Start();

            ActualizarEstadoBotonesPizarra(_mainWindow?.PizarraEstaAbierta == true);
            ActualizarEstadoBotonesLogos();

            CancellationToken ct = _viewCts?.Token ?? CancellationToken.None;
            _ = CargarDatosRealesAsync();
            _ = CargarCategoriasPizarraAsync(ct);
        }

        private void TimingView_Unloaded(object sender, RoutedEventArgs e)
        {
            _estaDescargado = true;

            AppState.NuevoTiempoRegistrado -= OnNuevoTiempoRegistrado;
            AppState.CarreraActivaCambiada -= OnCarreraActivaCambiada;
            AppState.FotosEnEsperaGlobal.CollectionChanged -= AppState_FotosEnEspera_CollectionChanged;
            AppState.RegistrosExitososGlobal.CollectionChanged -= AppState_RegistrosExitosos_CollectionChanged;

            if (_mainWindow != null)
            {
                _mainWindow.EstadoPizarraCambiado -= MainWindow_EstadoPizarraCambiado;
                _mainWindow.CerrarPizarra();
                _mainWindow = null;
            }

            _cronometroTimer.Stop();
            _cronometroTimer.Tick -= CronometroTimer_Tick;

            _hitsReloadCoordinator.CancelarYLimpiar();
            CancelarYDisponerCts(ref _visorCts);
            CancelarYDisponerCts(ref _viewCts);

            VisorFotoPrincipal.Source = null;
            ListaFotosPendientes.ItemsSource = null;
            LogExito.ItemsSource = null;
            ListaHits.ItemsSource = null;
            ComboCategoriasPizarra.ItemsSource = null;
            _fotosPendientesVista.Clear();
            _registrosExitososVista.Clear();
            ListaDeHits.Clear();
        }

        private void AppState_FotosEnEspera_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
        {
            AplicarCambioColeccionEnVista(_fotosPendientesVista, AppState.FotosEnEsperaGlobal, e);
        }

        private void AppState_RegistrosExitosos_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
        {
            AplicarCambioColeccionEnVista(_registrosExitososVista, AppState.RegistrosExitososGlobal, e);
        }

        private void AplicarCambioColeccionEnVista<T>(
            ObservableCollection<T> destino,
            IReadOnlyList<T> origen,
            NotifyCollectionChangedEventArgs e)
        {
            if (_estaDescargado)
            {
                return;
            }

            if (!Dispatcher.CheckAccess())
            {
                if (!Dispatcher.HasShutdownStarted)
                {
                    _ = Dispatcher.InvokeAsync(() =>
                    {
                        if (!_estaDescargado)
                        {
                            SincronizarColeccionCompleta(destino, origen);
                        }
                    });
                }
                return;
            }

            switch (e.Action)
            {
                case NotifyCollectionChangedAction.Add when e.NewItems != null &&
                                                           e.NewStartingIndex >= 0 &&
                                                           e.NewStartingIndex <= destino.Count:
                {
                    int idx = e.NewStartingIndex;
                    foreach (object? item in e.NewItems)
                    {
                        if (item is T tipado)
                        {
                            destino.Insert(idx++, tipado);
                        }
                    }
                    break;
                }
                case NotifyCollectionChangedAction.Remove when e.OldItems != null &&
                                                              e.OldStartingIndex >= 0 &&
                                                              e.OldStartingIndex + e.OldItems.Count <= destino.Count:
                {
                    for (int i = 0; i < e.OldItems.Count; i++)
                    {
                        destino.RemoveAt(e.OldStartingIndex);
                    }
                    break;
                }
                default:
                    SincronizarColeccionCompleta(destino, origen);
                    break;
            }
        }

        private static void SincronizarColeccionCompleta<T>(
            ObservableCollection<T> destino,
            IReadOnlyList<T> origen)
        {
            destino.Clear();
            for (int i = 0; i < origen.Count; i++)
            {
                destino.Add(origen[i]);
            }
        }

        private void MainWindow_EstadoPizarraCambiado(bool abierta)
        {
            if (_estaDescargado || Dispatcher.HasShutdownStarted)
            {
                return;
            }

            _ = Dispatcher.InvokeAsync(() =>
            {
                if (!_estaDescargado)
                {
                    ActualizarEstadoBotonesPizarra(abierta);
                }
            });
        }

        private void OnNuevoTiempoRegistrado()
        {
            if (_estaDescargado || Dispatcher.HasShutdownStarted)
            {
                return;
            }

            _ = Dispatcher.InvokeAsync(async () =>
            {
                if (!_estaDescargado)
                {
                    await CargarDatosRealesAsync();
                }
            });
        }

        private void OnCarreraActivaCambiada()
        {
            if (_estaDescargado || Dispatcher.HasShutdownStarted)
            {
                return;
            }

            _ = Dispatcher.InvokeAsync(async () =>
            {
                if (_estaDescargado)
                {
                    return;
                }

                VisorFotoPrincipal.Source = null;
                ActualizarEstadoBotonesLogos();
                CancellationToken ct = _viewCts?.Token ?? CancellationToken.None;
                await CargarDatosRealesAsync();
                await CargarCategoriasPizarraAsync(ct);
            });
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

        private async Task CargarDatosRealesAsync()
        {
            if (_estaDescargado)
            {
                return;
            }

            Carrera? carrera = AppState.CarreraActiva;
            if (carrera == null)
            {
                ListaDeHits.Clear();
                return;
            }

            int idCarreraActual = carrera.ID_Carrera;
            (long version, CancellationToken ct) = _hitsReloadCoordinator.IniciarNuevaRecarga();

            try
            {
                using var db = new ApplicationDbContext();

                var hitsDb = await db.Hits.AsNoTracking()
                    .Where(h => h.ID_Carrera == idCarreraActual)
                    .ToListAsync(ct)
                    .ConfigureAwait(false);

                var corredoresPorHit = await db.Corredores.AsNoTracking()
                    .Where(c => c.ID_Carrera == idCarreraActual && c.Estado == EstadoCorredor.Activo)
                    .GroupBy(c => c.ID_Hit)
                    .Select(g => new { ID_Hit = g.Key, Total = g.Count() })
                    .ToListAsync(ct)
                    .ConfigureAwait(false);

                var tiemposPorHit = await (from t in db.Tiempos.AsNoTracking()
                                           join c in db.Corredores.AsNoTracking() on t.ID_Corredor equals c.ID_Corredor
                                           where c.ID_Carrera == idCarreraActual
                                           group t by c.ID_Hit into g
                                           select new { ID_Hit = g.Key, Llegaron = g.Count() })
                    .ToListAsync(ct)
                    .ConfigureAwait(false);

                if (_estaDescargado || ct.IsCancellationRequested || !_hitsReloadCoordinator.EsVigente(version))
                {
                    return;
                }

                var dictTotal = corredoresPorHit.ToDictionary(x => x.ID_Hit, x => x.Total);
                var dictLlegaron = tiemposPorHit.ToDictionary(x => x.ID_Hit, x => x.Llegaron);

                await Dispatcher.InvokeAsync(() =>
                {
                    if (_estaDescargado || ct.IsCancellationRequested || !_hitsReloadCoordinator.EsVigente(version))
                    {
                        return;
                    }

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
            catch (OperationCanceledException) when (ct.IsCancellationRequested || _estaDescargado)
            {
                // Recarga reemplazada o vista descargada
            }
            catch (Exception ex)
            {
                Trace.TraceWarning($"[TimingView] Error al cargar hits: {ex.Message}");
            }
        }

        private async void ListaFotosPendientes_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_estaDescargado)
            {
                return;
            }

            if (ListaFotosPendientes.SelectedItem is FotoPendiente fotoSeleccionada)
            {
                InputDorsal.Focus();
                await CargarVisorPrincipalAsync(fotoSeleccionada);
            }
            else
            {
                CancelarYDisponerCts(ref _visorCts);
                VisorFotoPrincipal.Source = null;
            }
        }

        private async Task CargarVisorPrincipalAsync(FotoPendiente fotoSeleccionada)
        {
            CancelarYDisponerCts(ref _visorCts);
            _visorCts = new CancellationTokenSource();
            CancellationToken ct = _visorCts.Token;
            string idObjetivo = fotoSeleccionada.IdFoto;

            try
            {
                BitmapImage? bitmap = await ImageHelper
                    .CargarSinBloqueoAsync(fotoSeleccionada.Ruta_Foto, decodePixelWidth: 1000, ct);

                if (_estaDescargado || ct.IsCancellationRequested)
                {
                    return;
                }

                if (ListaFotosPendientes.SelectedItem is FotoPendiente actual && actual.IdFoto == idObjetivo)
                {
                    VisorFotoPrincipal.Source = bitmap;
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested || _estaDescargado)
            {
                // Cambio rápido de selección o vista descargada
            }
            catch (Exception ex)
            {
                Trace.TraceWarning($"[TimingView] Error al cargar imagen en visor principal: {ex.Message}");
            }
        }

        private async void InputDorsal_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                await ProcesarAsignacionAsync();
            }
        }

        private async void BtnAsignar_Click(object sender, RoutedEventArgs e)
        {
            await ProcesarAsignacionAsync();
        }

        private async void BtnDescartar_Click(object sender, RoutedEventArgs e)
        {
            if (_procesandoLocal || ListaFotosPendientes.SelectedItem is not FotoPendiente fotoActual)
            {
                return;
            }

            if (!WebServer.IntentarAdquirirFotoParaLocal(fotoActual.IdFoto, out _, out string rutaFotoServer))
            {
                ManejarFalloAdquisicionLocal(fotoActual);
                return;
            }

            string rutaParaBorrar = string.IsNullOrWhiteSpace(rutaFotoServer) ? fotoActual.Ruta_Foto : rutaFotoServer;
            bool descartada = false;
            _procesandoLocal = true;
            EstablecerEstadoControlesAsignacion(habilitado: false);

            try
            {
                VisorFotoPrincipal.Source = null;

                await Task.Run(() =>
                {
                    if (!string.IsNullOrWhiteSpace(rutaParaBorrar) && File.Exists(rutaParaBorrar))
                    {
                        try
                        {
                            File.Delete(rutaParaBorrar);
                        }
                        catch (IOException ex)
                        {
                            Trace.TraceWarning($"[TimingView] No se pudo eliminar archivo descartado '{rutaParaBorrar}': {ex.Message}");
                        }
                        catch (UnauthorizedAccessException ex)
                        {
                            Trace.TraceWarning($"[TimingView] Sin permisos para eliminar '{rutaParaBorrar}': {ex.Message}");
                        }
                    }
                });

                WebServer.EliminarFotoPendiente(fotoActual.IdFoto);
                fotoActual.Miniatura = null;
                AppState.FotosEnEsperaGlobal.Remove(fotoActual);
                descartada = true;

                InputDorsal.Clear();
                InputDorsal.Focus();
                if (AppState.FotosEnEsperaGlobal.Any())
                {
                    ListaFotosPendientes.SelectedIndex = 0;
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error al descartar: {ex.Message}", "Aviso", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            finally
            {
                if (!descartada)
                {
                    WebServer.LiberarFotoLocal(fotoActual.IdFoto);
                }

                _procesandoLocal = false;
                EstablecerEstadoControlesAsignacion(habilitado: true);
            }
        }

        private async Task ProcesarAsignacionAsync()
        {
            if (_procesandoLocal)
            {
                return;
            }

            string dorsalTexto = InputDorsal.Text.Trim();
            if (string.IsNullOrEmpty(dorsalTexto) || !int.TryParse(dorsalTexto, out int dorsalNum) || dorsalNum <= 0)
            {
                InputDorsal.Clear();
                return;
            }

            if (ListaFotosPendientes.SelectedItem is not FotoPendiente fotoActual)
            {
                return;
            }

            Carrera? carreraActiva = AppState.CarreraActiva;
            if (carreraActiva == null)
            {
                MessageBox.Show("No hay una carrera activa seleccionada en mesa.", "Atención", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            // RF-16: Exclusividad mutua compartida con WebServer antes de procesar en PC
            if (!WebServer.IntentarAdquirirFotoParaLocal(fotoActual.IdFoto, out DateTime horaLlegada, out string rutaFotoServer))
            {
                ManejarFalloAdquisicionLocal(fotoActual);
                return;
            }

            string rutaFoto = string.IsNullOrWhiteSpace(rutaFotoServer) ? fotoActual.Ruta_Foto : rutaFotoServer;
            bool lockPendienteDeLiberar = true;
            _procesandoLocal = true;
            EstablecerEstadoControlesAsignacion(habilitado: false);

            try
            {
                VisorFotoPrincipal.Source = null; // Liberar visor antes de mover archivo

                AsignacionResultado resultado = await _raceDataService.AsignarTiempoAsync(
                    carreraActiva.ID_Carrera,
                    carreraActiva.Nombre,
                    dorsalNum,
                    horaLlegada,
                    rutaFoto);

                if (resultado.Exito)
                {
                    lockPendienteDeLiberar = false;
                    WebServer.EliminarFotoPendiente(fotoActual.IdFoto);
                    fotoActual.Miniatura = null;
                    AppState.FotosEnEsperaGlobal.Remove(fotoActual);

                    AppState.AgregarRegistroExitoso($"[{DateTime.Now:HH:mm:ss}] Corredor {dorsalNum} OK - {resultado.TiempoFormateado}");
                    if (resultado.HitFinalizado)
                    {
                        AppState.AgregarRegistroExitoso(
                            $"[HIT FINALIZADO] ¡Todos los corredores de '{resultado.NombreHitFinalizado}' han llegado a la meta!");
                    }

                    // RF-30: Una única notificación consolidada refresca cronometraje y pizarra
                    AppState.NotificarNuevoTiempo();

                    InputDorsal.Clear();
                    InputDorsal.Focus();
                    if (AppState.FotosEnEsperaGlobal.Any())
                    {
                        ListaFotosPendientes.SelectedIndex = 0;
                    }
                }
                else
                {
                    // Liberar antes de mostrar el cuadro de diálogo modal para no bloquear reintentos remotos
                    WebServer.LiberarFotoLocal(fotoActual.IdFoto);
                    lockPendienteDeLiberar = false;

                    if (!_estaDescargado && ListaFotosPendientes.SelectedItem is FotoPendiente actual && actual.IdFoto == fotoActual.IdFoto)
                    {
                        _ = CargarVisorPrincipalAsync(fotoActual);
                    }

                    MessageBox.Show(resultado.Mensaje, "Asignación No Permitida", MessageBoxButton.OK, MessageBoxImage.Warning);
                    AppState.AgregarRegistroExitoso($"[RECHAZADO] {resultado.Mensaje}");
                    InputDorsal.SelectAll();
                    InputDorsal.Focus();
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Fallo al procesar el tiempo: {ex.Message}", "Error del Sistema", MessageBoxButton.OK, MessageBoxImage.Error);
                InputDorsal.Clear();
            }
            finally
            {
                if (lockPendienteDeLiberar)
                {
                    WebServer.LiberarFotoLocal(fotoActual.IdFoto);
                }

                _procesandoLocal = false;
                EstablecerEstadoControlesAsignacion(habilitado: true);
            }
        }

        private void ManejarFalloAdquisicionLocal(FotoPendiente fotoActual)
        {
            if (!WebServer.ObtenerIdsFotosPendientes().Contains(fotoActual.IdFoto))
            {
                fotoActual.Miniatura = null;
                AppState.FotosEnEsperaGlobal.Remove(fotoActual);
                VisorFotoPrincipal.Source = null;
                MessageBox.Show(
                    "Esta fotografía ya fue procesada o descartada desde otro dispositivo.",
                    "Captura Procesada",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
            }
            else
            {
                MessageBox.Show(
                    "Esta fotografía está siendo procesada en este instante desde un dispositivo móvil.",
                    "Captura en Proceso",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
            }
        }

        private void EstablecerEstadoControlesAsignacion(bool habilitado)
        {
            BtnAsignar.IsEnabled = habilitado;
            BtnDescartar.IsEnabled = habilitado;
            InputDorsal.IsEnabled = habilitado;
        }

        private async void BtnIniciarHit_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button btn || btn.Tag is not int idHit)
            {
                return;
            }

            btn.IsEnabled = false;
            try
            {
                using var db = new ApplicationDbContext();
                Hit? hit = await db.Hits.FindAsync(idHit);
                if (hit != null && hit.Hora_Inicio == null)
                {
                    hit.Hora_Inicio = DateTime.Now;
                    await db.SaveChangesAsync();

                    AppState.AgregarRegistroExitoso($"[SISTEMA] {hit.Nombre_Hit} INICIADO a las {hit.Hora_Inicio:HH:mm:ss}");
                    await CargarDatosRealesAsync();
                }
            }
            catch (Exception ex)
            {
                btn.IsEnabled = true;
                MessageBox.Show($"Error al iniciar el hit: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private async Task CargarCategoriasPizarraAsync(CancellationToken ct)
        {
            if (_estaDescargado)
            {
                return;
            }

            Carrera? carrera = AppState.CarreraActiva;
            if (carrera == null)
            {
                _cargandoCategoriasPizarra = true;
                try
                {
                    ComboCategoriasPizarra.ItemsSource = null;
                    AppState.CategoriaPizarraActiva = null;
                }
                finally
                {
                    _cargandoCategoriasPizarra = false;
                }
                return;
            }

            int idCarreraActual = carrera.ID_Carrera;

            try
            {
                using var db = new ApplicationDbContext();
                var categorias = await db.Categorias.AsNoTracking()
                    .Where(c => c.ID_Carrera == idCarreraActual)
                    .ToListAsync(ct);

                if (_estaDescargado || ct.IsCancellationRequested || AppState.CarreraActiva?.ID_Carrera != idCarreraActual)
                {
                    return;
                }

                _cargandoCategoriasPizarra = true;
                try
                {
                    ComboCategoriasPizarra.ItemsSource = categorias;

                    if (categorias.Count > 0)
                    {
                        if (AppState.CategoriaPizarraActiva != null &&
                            categorias.Any(c => c.ID_Categoria == AppState.CategoriaPizarraActiva.ID_Categoria))
                        {
                            ComboCategoriasPizarra.SelectedValue = AppState.CategoriaPizarraActiva.ID_Categoria;
                        }
                        else
                        {
                            AppState.CategoriaPizarraActiva = categorias[0];
                            ComboCategoriasPizarra.SelectedIndex = 0;

                            if (_mainWindow?.PizarraEstaAbierta == true)
                            {
                                AppState.NotificarCambioCategoriaPizarra();
                            }
                        }
                    }
                    else
                    {
                        AppState.CategoriaPizarraActiva = null;
                    }
                }
                finally
                {
                    _cargandoCategoriasPizarra = false;
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested || _estaDescargado)
            {
                // Operación cancelada al descargar la vista
            }
            catch (Exception ex)
            {
                Trace.TraceWarning($"[TimingView] Error al cargar categorías de pizarra: {ex.Message}");
            }
        }

        private void ComboCategoriasPizarra_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_cargandoCategoriasPizarra || _estaDescargado)
            {
                return;
            }

            if (ComboCategoriasPizarra.SelectedItem is Categoria cat)
            {
                if (AppState.CategoriaPizarraActiva?.ID_Categoria == cat.ID_Categoria)
                {
                    return;
                }

                AppState.CategoriaPizarraActiva = cat;
                // RF-30: Una única notificación consolidada hacia la pizarra
                AppState.NotificarCambioCategoriaPizarra();
            }
        }

        private void BtnPizarra_Click(object sender, RoutedEventArgs e)
        {
            var mainWindow = _mainWindow ?? (Window.GetWindow(this) as MainWindow) ?? (Application.Current?.MainWindow as MainWindow);
            if (mainWindow == null)
            {
                return;
            }

            mainWindow.AlternarPizarra();
            ActualizarEstadoBotonesPizarra(mainWindow.PizarraEstaAbierta);
        }

        private void BtnPantallaCompleta_Click(object sender, RoutedEventArgs e)
        {
            var mainWindow = _mainWindow ?? (Window.GetWindow(this) as MainWindow) ?? (Application.Current?.MainWindow as MainWindow);
            if (mainWindow == null || !mainWindow.PizarraEstaAbierta)
            {
                return;
            }

            bool enPantallaCompleta = mainWindow.AlternarPantallaCompletaPizarra();
            BtnPantallaCompleta.Content = enPantallaCompleta ? "MODO VENTANA" : "⛶ PANTALLA COMPLETA";
        }

        private void ActualizarEstadoBotonesPizarra(bool abierta)
        {
            BtnPizarra.Content = abierta ? "CERRAR PIZARRA" : "🖥️ ABRIR PIZARRA";
            BtnPizarra.Background = abierta ? BrushPizarraAbierta : BrushPizarraCerrada;

            if (!abierta)
            {
                BtnPantallaCompleta.Content = "⛶ PANTALLA COMPLETA";
            }
            else if (_mainWindow?.PizarraEstaEnPantallaCompleta == true)
            {
                BtnPantallaCompleta.Content = "MODO VENTANA";
            }
        }

        private async void BtnSubirLogoIzq_Click(object sender, RoutedEventArgs e)
        {
            await ProcesarSubidaLogoAsync(esIzquierdo: true);
        }

        private async void BtnSubirLogoDer_Click(object sender, RoutedEventArgs e)
        {
            await ProcesarSubidaLogoAsync(esIzquierdo: false);
        }

        private async void BtnQuitarLogoIzq_Click(object sender, RoutedEventArgs e)
        {
            await QuitarLogoAsync(esIzquierdo: true);
        }

        private async void BtnQuitarLogoDer_Click(object sender, RoutedEventArgs e)
        {
            await QuitarLogoAsync(esIzquierdo: false);
        }

        private async Task ProcesarSubidaLogoAsync(bool esIzquierdo)
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

            if (openDialog.ShowDialog() != true)
            {
                return;
            }

            string rutaOrigen = openDialog.FileName;
            string? rutaDestino = null;
            int idCarrera = AppState.CarreraActiva.ID_Carrera;
            CancellationToken ct = _viewCts?.Token ?? CancellationToken.None;

            try
            {
                ValidacionImagenResultado validacion;
                await using (var fs = new FileStream(rutaOrigen, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    validacion = await ImageHelper.ValidarImagenAsync(fs, fs.Length, ct);
                }

                if (!validacion.EsValida)
                {
                    MessageBox.Show(validacion.MensajeError, "Imagen no válida", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                string carpetaLogos = StorageHelper.ObtenerCarpetaLogos();
                string lado = esIzquierdo ? "Izq" : "Der";
                string nuevoNombre = $"Logo_{lado}_{idCarrera}_{Guid.NewGuid():N}{validacion.ExtensionReal}";
                rutaDestino = Path.Combine(carpetaLogos, nuevoNombre);

                await Task.Run(() => File.Copy(rutaOrigen, rutaDestino, overwrite: true), ct);

                string? rutaAnterior = null;
                using (var db = new ApplicationDbContext())
                {
                    Carrera? carreraDb = await db.Carreras.FindAsync(new object[] { idCarrera }, ct);
                    if (carreraDb != null)
                    {
                        if (esIzquierdo)
                        {
                            rutaAnterior = carreraDb.RutaLogoIzquierdo;
                            carreraDb.RutaLogoIzquierdo = rutaDestino;
                            if (AppState.CarreraActiva?.ID_Carrera == idCarrera)
                            {
                                AppState.CarreraActiva.RutaLogoIzquierdo = rutaDestino;
                            }
                        }
                        else
                        {
                            rutaAnterior = carreraDb.RutaLogoDerecho;
                            carreraDb.RutaLogoDerecho = rutaDestino;
                            if (AppState.CarreraActiva?.ID_Carrera == idCarrera)
                            {
                                AppState.CarreraActiva.RutaLogoDerecho = rutaDestino;
                            }
                        }

                        await db.SaveChangesAsync(ct);
                    }
                }

                if (!string.IsNullOrEmpty(rutaAnterior))
                {
                    await Task.Run(() => IntentarEliminarArchivoSilencioso(rutaAnterior));
                }

                ActualizarEstadoBotonesLogos();
                // RF-30: Una única notificación consolidada hacia la pizarra
                AppState.NotificarCambioCategoriaPizarra();

                MessageBox.Show($"Logo {(esIzquierdo ? "izquierdo" : "derecho")} actualizado correctamente.", "Logo Actualizado", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested || _estaDescargado)
            {
                if (!string.IsNullOrEmpty(rutaDestino))
                {
                    IntentarEliminarArchivoSilencioso(rutaDestino);
                }
            }
            catch (Exception ex)
            {
                if (!string.IsNullOrEmpty(rutaDestino))
                {
                    IntentarEliminarArchivoSilencioso(rutaDestino);
                }

                MessageBox.Show($"Error al guardar logo: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private async Task QuitarLogoAsync(bool esIzquierdo)
        {
            if (AppState.CarreraActiva == null)
            {
                return;
            }

            int idCarrera = AppState.CarreraActiva.ID_Carrera;
            CancellationToken ct = _viewCts?.Token ?? CancellationToken.None;

            try
            {
                string? rutaAnterior = null;
                using (var db = new ApplicationDbContext())
                {
                    Carrera? carreraDb = await db.Carreras.FindAsync(new object[] { idCarrera }, ct);
                    if (carreraDb != null)
                    {
                        if (esIzquierdo && !string.IsNullOrEmpty(carreraDb.RutaLogoIzquierdo))
                        {
                            rutaAnterior = carreraDb.RutaLogoIzquierdo;
                            carreraDb.RutaLogoIzquierdo = null;
                            if (AppState.CarreraActiva?.ID_Carrera == idCarrera)
                            {
                                AppState.CarreraActiva.RutaLogoIzquierdo = null;
                            }
                        }
                        else if (!esIzquierdo && !string.IsNullOrEmpty(carreraDb.RutaLogoDerecho))
                        {
                            rutaAnterior = carreraDb.RutaLogoDerecho;
                            carreraDb.RutaLogoDerecho = null;
                            if (AppState.CarreraActiva?.ID_Carrera == idCarrera)
                            {
                                AppState.CarreraActiva.RutaLogoDerecho = null;
                            }
                        }

                        await db.SaveChangesAsync(ct);
                    }
                }

                if (!string.IsNullOrEmpty(rutaAnterior))
                {
                    await Task.Run(() => IntentarEliminarArchivoSilencioso(rutaAnterior));
                }

                ActualizarEstadoBotonesLogos();
                // RF-30: Una única notificación consolidada hacia la pizarra
                AppState.NotificarCambioCategoriaPizarra();
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested || _estaDescargado)
            {
                // Operación cancelada por descarga de vista
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
                bool tieneIzq = !string.IsNullOrEmpty(AppState.CarreraActiva.RutaLogoIzquierdo) && File.Exists(AppState.CarreraActiva.RutaLogoIzquierdo);
                bool tieneDer = !string.IsNullOrEmpty(AppState.CarreraActiva.RutaLogoDerecho) && File.Exists(AppState.CarreraActiva.RutaLogoDerecho);

                BtnSubirLogoIzq.Content = tieneIzq ? "🖼️ Logo Izq ✓" : "🖼️ Logo Izq";
                BtnSubirLogoIzq.Background = tieneIzq ? BrushLogoActivo : BrushLogoInactivo;
                BtnQuitarLogoIzq.Visibility = tieneIzq ? Visibility.Visible : Visibility.Collapsed;

                BtnSubirLogoDer.Content = tieneDer ? "🖼️ Logo Der ✓" : "🖼️ Logo Der";
                BtnSubirLogoDer.Background = tieneDer ? BrushLogoActivo : BrushLogoInactivo;
                BtnQuitarLogoDer.Visibility = tieneDer ? Visibility.Visible : Visibility.Collapsed;
            }
            else
            {
                BtnSubirLogoIzq.Content = "🖼️ Logo Izq";
                BtnSubirLogoIzq.Background = BrushLogoInactivo;
                BtnQuitarLogoIzq.Visibility = Visibility.Collapsed;

                BtnSubirLogoDer.Content = "🖼️ Logo Der";
                BtnSubirLogoDer.Background = BrushLogoInactivo;
                BtnQuitarLogoDer.Visibility = Visibility.Collapsed;
            }
        }

        private void CancelarYCrearViewCts()
        {
            CancelarYDisponerCts(ref _viewCts);
            _viewCts = new CancellationTokenSource();
        }

        private static void CancelarYDisponerCts(ref CancellationTokenSource? cts)
        {
            CancellationTokenSource? actual = Interlocked.Exchange(ref cts, null);
            if (actual == null)
            {
                return;
            }

            try
            {
                actual.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // Ignorar si ya fue dispuesto
            }
            finally
            {
                actual.Dispose();
            }
        }

        private static void IntentarEliminarArchivoSilencioso(string ruta)
        {
            try
            {
                if (File.Exists(ruta))
                {
                    File.Delete(ruta);
                }
            }
            catch (IOException)
            {
                // Tolerar bloqueo transitorio
            }
            catch (UnauthorizedAccessException)
            {
                // Tolerar restricción de permisos
            }
        }
    }
}
