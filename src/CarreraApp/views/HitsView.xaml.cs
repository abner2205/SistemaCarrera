using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using CarreraApp.Data;
using CarreraApp.Helpers;
using CarreraApp.Models;

namespace CarreraApp.UI.Views
{
    public partial class HitsView : UserControl
    {
        private readonly RaceDataService _raceDataService = new();
        private Hit? _hitEnEdicion;
        private Categoria? _categoriaEnEdicion;
        private bool _estaDescargado;
        private CancellationTokenSource? _viewCts;
        private DispatcherTimer? _feedbackTimer;

        public HitsView()
        {
            InitializeComponent();
            Loaded += HitsView_Loaded;
            Unloaded += HitsView_Unloaded;
        }

        private void HitsView_Loaded(object sender, RoutedEventArgs e)
        {
            _estaDescargado = false;
            CancelarOperacionesPendientes();
            _viewCts = new CancellationTokenSource();
            _ = CargarDatosAsync(_viewCts.Token);
        }

        private void HitsView_Unloaded(object sender, RoutedEventArgs e)
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

        private async Task CargarDatosAsync(CancellationToken cancellationToken = default)
        {
            if (AppState.CarreraActiva == null)
            {
                ActualizarEstadoListas(Array.Empty<Hit>(), Array.Empty<Categoria>());
                return;
            }

            try
            {
                int idCarrera = AppState.CarreraActiva.ID_Carrera;
                List<Hit> hits = await _raceDataService.ObtenerHitsPorCarreraAsync(idCarrera);
                if (cancellationToken.IsCancellationRequested || _estaDescargado) return;

                List<Categoria> categorias = await _raceDataService.ObtenerCategoriasPorCarreraAsync(idCarrera);
                if (cancellationToken.IsCancellationRequested || _estaDescargado) return;

                TablaHits.ItemsSource = hits;
                TablaCategorias.ItemsSource = categorias;
                ActualizarEstadoListas(hits, categorias);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // Vista descargada antes de completar la carga
            }
            catch (Exception ex)
            {
                if (!cancellationToken.IsCancellationRequested && !_estaDescargado)
                {
                    MostrarFeedback(NivelFeedback.Error, $"No se pudieron cargar los Hits y categorías: {ex.Message}");
                }
            }
        }

        private void ActualizarEstadoListas(IReadOnlyCollection<Hit> hits, IReadOnlyCollection<Categoria> categorias)
        {
            TxtContadorHits.Text = hits.Count == 1 ? "1 Hit" : $"{hits.Count} Hits";
            TxtContadorCategorias.Text = categorias.Count == 1 ? "1 categoría" : $"{categorias.Count} categorías";

            bool hayHits = hits.Count > 0;
            TablaHits.Visibility = hayHits ? Visibility.Visible : Visibility.Collapsed;
            PanelEstadoVacioHits.Visibility = hayHits ? Visibility.Collapsed : Visibility.Visible;

            bool hayCategorias = categorias.Count > 0;
            TablaCategorias.Visibility = hayCategorias ? Visibility.Visible : Visibility.Collapsed;
            PanelEstadoVacioCategorias.Visibility = hayCategorias ? Visibility.Collapsed : Visibility.Visible;
        }

        // --- LÓGICA DE HITS ---

        private void InputNombreHit_TextChanged(object sender, TextChangedEventArgs e) => LimpiarErrorHit();

        private void InputNombreHit_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                e.Handled = true;
                BtnGuardarHit_Click(BtnAccionHit, new RoutedEventArgs());
            }
        }

        private void BtnIrACrearHit_Click(object sender, RoutedEventArgs e)
        {
            LimpiarErrorHit();
            InputNombreHit.Focus();
            Keyboard.Focus(InputNombreHit);
        }

        private async void BtnGuardarHit_Click(object sender, RoutedEventArgs e)
        {
            LimpiarErrorHit();

            if (AppState.CarreraActiva == null)
            {
                MostrarFeedback(NivelFeedback.Advertencia, "Selecciona una carrera activa antes de crear o editar Hits.");
                return;
            }

            string nombreIngresado = InputNombreHit.Text?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(nombreIngresado))
            {
                MostrarErrorHit("Escribe un nombre para el Hit (por ejemplo, Hit 1).");
                InputNombreHit.Focus();
                return;
            }

            bool esEdicion = _hitEnEdicion != null;
            BtnAccionHit.IsEnabled = false;
            BtnAccionHit.Content = esEdicion ? "Actualizando..." : "Guardando...";
            ProgresoHit.Visibility = Visibility.Visible;

            try
            {
                OperacionResultado<Hit> resultado = await _raceDataService.CrearOActualizarHitAsync(
                    _hitEnEdicion?.ID_Hit,
                    AppState.CarreraActiva.ID_Carrera,
                    nombreIngresado);

                if (!resultado.Exito)
                {
                    MostrarErrorHit(resultado.Mensaje);
                    InputNombreHit.Focus();
                    return;
                }

                string nombreGuardado = resultado.Entidad?.Nombre_Hit ?? nombreIngresado;
                LimpiarFormularioHit();
                await CargarDatosAsync(_viewCts?.Token ?? CancellationToken.None);

                MostrarFeedback(
                    NivelFeedback.Exito,
                    esEdicion
                        ? $"Hit \"{nombreGuardado}\" actualizado."
                        : $"Hit \"{nombreGuardado}\" guardado.");
            }
            catch (Exception ex)
            {
                MostrarFeedback(NivelFeedback.Error, $"No se pudo guardar el Hit: {ex.Message}");
            }
            finally
            {
                ProgresoHit.Visibility = Visibility.Collapsed;
                BtnAccionHit.IsEnabled = true;
                BtnAccionHit.Content = _hitEnEdicion != null ? "Actualizar Hit" : "Guardar Hit";
            }
        }

        private void BtnEditarHit_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as Button)?.DataContext is Hit hitSeleccionado)
            {
                LimpiarErrorHit();
                _hitEnEdicion = hitSeleccionado;
                InputNombreHit.Text = hitSeleccionado.Nombre_Hit;
                TxtTituloFormularioHit.Text = $"Editando Hit #{hitSeleccionado.ID_Hit}";
                BtnAccionHit.Content = "Actualizar Hit";
                BtnCancelarEdicionHit.Visibility = Visibility.Visible;
                InputNombreHit.Focus();
            }
        }

        private void BtnCancelarEdicionHit_Click(object sender, RoutedEventArgs e)
        {
            LimpiarFormularioHit();
        }

        private void LimpiarFormularioHit()
        {
            _hitEnEdicion = null;
            TxtTituloFormularioHit.Text = "Nuevo Hit de salida";
            BtnAccionHit.Content = "Guardar Hit";
            BtnCancelarEdicionHit.Visibility = Visibility.Collapsed;
            InputNombreHit.Clear();
            LimpiarErrorHit();
        }

        private async void BtnEliminarHit_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as Button)?.DataContext is Hit hitSeleccionado)
            {
                var result = MessageBox.Show(
                    $"¿Eliminar el Hit '{hitSeleccionado.Nombre_Hit}'?\nEsta acción no se puede deshacer.",
                    "Confirmar eliminación",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Warning);

                if (result == MessageBoxResult.Yes)
                {
                    try
                    {
                        OperacionResultado resultado = await _raceDataService.EliminarHitAsync(hitSeleccionado.ID_Hit);
                        if (!resultado.Exito)
                        {
                            MostrarFeedback(NivelFeedback.Error, resultado.Mensaje);
                            return;
                        }

                        if (_hitEnEdicion?.ID_Hit == hitSeleccionado.ID_Hit)
                        {
                            LimpiarFormularioHit();
                        }

                        await CargarDatosAsync(_viewCts?.Token ?? CancellationToken.None);
                        MostrarFeedback(NivelFeedback.Exito, $"Hit \"{hitSeleccionado.Nombre_Hit}\" eliminado.");
                    }
                    catch (Exception ex)
                    {
                        MostrarFeedback(NivelFeedback.Error, $"No se pudo eliminar el Hit: {ex.Message}");
                    }
                }
            }
        }

        // --- LÓGICA DE CATEGORÍAS ---

        private void InputNombreCategoria_TextChanged(object sender, TextChangedEventArgs e) => LimpiarErrorCategoria();

        private void InputNombreCategoria_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                e.Handled = true;
                BtnGuardarCategoria_Click(BtnAccionCategoria, new RoutedEventArgs());
            }
        }

        private void BtnIrACrearCategoria_Click(object sender, RoutedEventArgs e)
        {
            LimpiarErrorCategoria();
            InputNombreCategoria.Focus();
            Keyboard.Focus(InputNombreCategoria);
        }

        private async void BtnGuardarCategoria_Click(object sender, RoutedEventArgs e)
        {
            LimpiarErrorCategoria();

            if (AppState.CarreraActiva == null)
            {
                MostrarFeedback(NivelFeedback.Advertencia, "Selecciona una carrera activa antes de crear o editar categorías.");
                return;
            }

            string nombreIngresado = InputNombreCategoria.Text?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(nombreIngresado))
            {
                MostrarErrorCategoria("Escribe un nombre para la categoría (por ejemplo, Libre Masculino).");
                InputNombreCategoria.Focus();
                return;
            }

            bool esEdicion = _categoriaEnEdicion != null;
            BtnAccionCategoria.IsEnabled = false;
            BtnAccionCategoria.Content = esEdicion ? "Actualizando..." : "Guardando...";
            ProgresoCategoria.Visibility = Visibility.Visible;

            try
            {
                OperacionResultado<Categoria> resultado = await _raceDataService.CrearOActualizarCategoriaAsync(
                    _categoriaEnEdicion?.ID_Categoria,
                    AppState.CarreraActiva.ID_Carrera,
                    nombreIngresado);

                if (!resultado.Exito)
                {
                    MostrarErrorCategoria(resultado.Mensaje);
                    InputNombreCategoria.Focus();
                    return;
                }

                string nombreGuardado = resultado.Entidad?.Nombre ?? nombreIngresado;
                LimpiarFormularioCategoria();
                await CargarDatosAsync(_viewCts?.Token ?? CancellationToken.None);

                MostrarFeedback(
                    NivelFeedback.Exito,
                    esEdicion
                        ? $"Categoría \"{nombreGuardado}\" actualizada."
                        : $"Categoría \"{nombreGuardado}\" guardada.");
            }
            catch (Exception ex)
            {
                MostrarFeedback(NivelFeedback.Error, $"No se pudo guardar la categoría: {ex.Message}");
            }
            finally
            {
                ProgresoCategoria.Visibility = Visibility.Collapsed;
                BtnAccionCategoria.IsEnabled = true;
                BtnAccionCategoria.Content = _categoriaEnEdicion != null ? "Actualizar categoría" : "Guardar categoría";
            }
        }

        private void BtnEditarCategoria_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as Button)?.DataContext is Categoria catSeleccionada)
            {
                LimpiarErrorCategoria();
                _categoriaEnEdicion = catSeleccionada;
                InputNombreCategoria.Text = catSeleccionada.Nombre;
                TxtTituloFormularioCategoria.Text = $"Editando categoría #{catSeleccionada.ID_Categoria}";
                BtnAccionCategoria.Content = "Actualizar categoría";
                BtnCancelarEdicionCategoria.Visibility = Visibility.Visible;
                InputNombreCategoria.Focus();
            }
        }

        private void BtnCancelarEdicionCategoria_Click(object sender, RoutedEventArgs e)
        {
            LimpiarFormularioCategoria();
        }

        private void LimpiarFormularioCategoria()
        {
            _categoriaEnEdicion = null;
            TxtTituloFormularioCategoria.Text = "Nueva categoría";
            BtnAccionCategoria.Content = "Guardar categoría";
            BtnCancelarEdicionCategoria.Visibility = Visibility.Collapsed;
            InputNombreCategoria.Clear();
            LimpiarErrorCategoria();
        }

        private async void BtnEliminarCategoria_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as Button)?.DataContext is Categoria catSeleccionada)
            {
                var result = MessageBox.Show(
                    $"¿Eliminar la categoría '{catSeleccionada.Nombre}'?\nEsta acción no se puede deshacer.",
                    "Confirmar eliminación",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Warning);

                if (result == MessageBoxResult.Yes)
                {
                    try
                    {
                        OperacionResultado resultado = await _raceDataService.EliminarCategoriaAsync(catSeleccionada.ID_Categoria);
                        if (!resultado.Exito)
                        {
                            MostrarFeedback(NivelFeedback.Error, resultado.Mensaje);
                            return;
                        }

                        if (_categoriaEnEdicion?.ID_Categoria == catSeleccionada.ID_Categoria)
                        {
                            LimpiarFormularioCategoria();
                        }

                        await CargarDatosAsync(_viewCts?.Token ?? CancellationToken.None);
                        MostrarFeedback(NivelFeedback.Exito, $"Categoría \"{catSeleccionada.Nombre}\" eliminada.");
                    }
                    catch (Exception ex)
                    {
                        MostrarFeedback(NivelFeedback.Error, $"No se pudo eliminar la categoría: {ex.Message}");
                    }
                }
            }
        }

        // --- VALIDACIÓN EN LÍNEA Y FEEDBACK NO BLOQUEANTE ---

        private void MostrarErrorHit(string mensaje)
        {
            InputNombreHit.BorderBrush = CrearBrushCongelado(UiFeedbackHelper.TokensPaleta.PenaltyCrimson);
            InputNombreHit.BorderThickness = new Thickness(2);
            TxtErrorNombreHit.Text = mensaje;
            TxtErrorNombreHit.Visibility = Visibility.Visible;
        }

        private void LimpiarErrorHit()
        {
            InputNombreHit.ClearValue(BorderBrushProperty);
            InputNombreHit.ClearValue(BorderThicknessProperty);
            TxtErrorNombreHit.Text = string.Empty;
            TxtErrorNombreHit.Visibility = Visibility.Collapsed;
        }

        private void MostrarErrorCategoria(string mensaje)
        {
            InputNombreCategoria.BorderBrush = CrearBrushCongelado(UiFeedbackHelper.TokensPaleta.PenaltyCrimson);
            InputNombreCategoria.BorderThickness = new Thickness(2);
            TxtErrorNombreCategoria.Text = mensaje;
            TxtErrorNombreCategoria.Visibility = Visibility.Visible;
        }

        private void LimpiarErrorCategoria()
        {
            InputNombreCategoria.ClearValue(BorderBrushProperty);
            InputNombreCategoria.ClearValue(BorderThicknessProperty);
            TxtErrorNombreCategoria.Text = string.Empty;
            TxtErrorNombreCategoria.Visibility = Visibility.Collapsed;
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

            BannerFeedbackHits.Background = fondo;
            BannerFeedbackHits.BorderBrush = borde;
            TxtBannerFeedbackHits.Foreground = texto;
            BtnCerrarBannerHits.Foreground = texto;
            TxtBannerFeedbackHits.Text = config.Mensaje;
            BannerFeedbackHits.Visibility = Visibility.Visible;

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

        private void BtnCerrarBannerHits_Click(object sender, RoutedEventArgs e)
        {
            OcultarBannerEnLinea();
        }

        private void OcultarBannerEnLinea()
        {
            DetenerFeedbackTimer();
            BannerFeedbackHits.Visibility = Visibility.Collapsed;
            TxtBannerFeedbackHits.Text = string.Empty;
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
