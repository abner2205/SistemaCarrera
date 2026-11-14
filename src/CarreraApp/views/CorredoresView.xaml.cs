using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using CarreraApp.Data;
using CarreraApp.Helpers;
using CarreraApp.Models;

namespace CarreraApp.UI.Views
{
    public sealed class EstadoCorredorDescripcionConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is EstadoCorredor estado)
            {
                return estado switch
                {
                    EstadoCorredor.Activo => "Activo",
                    EstadoCorredor.DNS => "DNS (No salió)",
                    EstadoCorredor.DNF => "DNF (No finalizó)",
                    _ => estado.ToString()
                };
            }

            return value?.ToString() ?? string.Empty;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => Binding.DoNothing;
    }

    public sealed class EstadoCorredorTooltipConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is EstadoCorredor estado)
            {
                return estado switch
                {
                    EstadoCorredor.Activo => "Activo: corredor habilitado para competir y registrar tiempo",
                    EstadoCorredor.DNS => "DNS (No salió): el corredor no se presentó en la línea de salida",
                    EstadoCorredor.DNF => "DNF (No finalizó): el corredor abandonó antes de cruzar la meta",
                    _ => string.Empty
                };
            }

            return string.Empty;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => Binding.DoNothing;
    }

    // DTO para la visualización enriquecida de corredores con su tiempo registrado
    public class CorredorFilaView
    {
        private static readonly EstadoCorredor[] _estadosDisponibles = Enum.GetValues<EstadoCorredor>();

        public int ID_Corredor { get; set; }
        public int Dorsal { get; set; }
        public string Nombre { get; set; } = string.Empty;
        public int ID_Categoria { get; set; }
        public string NombreCategoria { get; set; } = string.Empty;
        public int ID_Hit { get; set; }
        public string NombreHit { get; set; } = string.Empty;
        public EstadoCorredor Estado { get; set; }
        public TimeSpan? TiempoNeto { get; set; }
        public DateTime? HoraLlegada { get; set; }
        public bool TieneTiempo => TiempoNeto.HasValue;
        public bool PuedeModificarEstadoYHit => !TiempoNeto.HasValue;
        public IReadOnlyList<EstadoCorredor> EstadosDisponibles => _estadosDisponibles;
        public string TiempoFormateado => TiempoNeto.HasValue
            ? $"{(int)TiempoNeto.Value.TotalHours:00}:{TiempoNeto.Value.Minutes:00}:{TiempoNeto.Value.Seconds:00}"
            : "--:--:--";

        public string TooltipEstado => TieneTiempo
            ? "Estado bloqueado porque el corredor ya tiene tiempo registrado."
            : "Cambiar estado: Activo, DNS (No salió) o DNF (No finalizó).";
    }

    public partial class CorredoresView : UserControl
    {
        private readonly RaceDataService _raceDataService = new();
        private int? _idCorredorEnEdicion;
        private bool _actualizandoTabla;
        private bool _faltanCatalogos;
        private bool _estaDescargado;
        private CancellationTokenSource? _viewCts;
        private DispatcherTimer? _feedbackTimer;

        public CorredoresView()
        {
            InitializeComponent();
            ComboEstado.ItemsSource = Enum.GetValues<EstadoCorredor>();
            ComboEstado.SelectedItem = EstadoCorredor.Activo;
            Loaded += CorredoresView_Loaded;
            Unloaded += CorredoresView_Unloaded;
        }

        private void CorredoresView_Loaded(object sender, RoutedEventArgs e)
        {
            _estaDescargado = false;
            CancelarOperacionesPendientes();
            _viewCts = new CancellationTokenSource();
            _ = CargarDatosInicialesAsync(_viewCts.Token);
        }

        private void CorredoresView_Unloaded(object sender, RoutedEventArgs e)
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

        private async Task CargarDatosInicialesAsync(CancellationToken cancellationToken = default)
        {
            if (AppState.CarreraActiva == null)
            {
                ActualizarEstadoListaCorredores(Array.Empty<CorredorFilaView>(), 0, 0);
                return;
            }

            int idCarrera = AppState.CarreraActiva.ID_Carrera;

            try
            {
                List<Categoria> categorias = await _raceDataService.ObtenerCategoriasPorCarreraAsync(idCarrera);
                if (cancellationToken.IsCancellationRequested || _estaDescargado) return;

                List<Hit> hits = await _raceDataService.ObtenerHitsPorCarreraAsync(idCarrera);
                if (cancellationToken.IsCancellationRequested || _estaDescargado) return;

                List<CorredorDetalleDto> detalle = await _raceDataService.ObtenerCorredoresDetallePorCarreraAsync(idCarrera);
                if (cancellationToken.IsCancellationRequested || _estaDescargado) return;

                ComboCategoria.ItemsSource = categorias;
                ComboHit.ItemsSource = hits;

                var lista = detalle.Select(d => new CorredorFilaView
                {
                    ID_Corredor = d.ID_Corredor,
                    Dorsal = d.Dorsal,
                    Nombre = d.Nombre,
                    ID_Categoria = d.ID_Categoria,
                    NombreCategoria = d.NombreCategoria,
                    ID_Hit = d.ID_Hit,
                    NombreHit = d.NombreHit,
                    Estado = d.Estado,
                    TiempoNeto = d.TiempoNeto,
                    HoraLlegada = d.HoraLlegada
                }).ToList();

                _actualizandoTabla = true;
                try
                {
                    TablaCorredores.ItemsSource = lista;
                }
                finally
                {
                    _actualizandoTabla = false;
                }

                ActualizarEstadoListaCorredores(lista, categorias.Count, hits.Count);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // Vista descargada antes de completar la carga
            }
            catch (Exception ex)
            {
                if (!cancellationToken.IsCancellationRequested && !_estaDescargado)
                {
                    MostrarFeedback(NivelFeedback.Error, $"No se pudieron cargar los corredores: {ex.Message}");
                }
            }
        }

        private void ActualizarEstadoListaCorredores(
            IReadOnlyCollection<CorredorFilaView> lista,
            int cantidadCategorias,
            int cantidadHits)
        {
            int total = lista.Count;
            TxtContadorCorredores.Text = total == 1
                ? "1 corredor registrado"
                : $"{total} corredores registrados";

            _faltanCatalogos = cantidadCategorias == 0 || cantidadHits == 0;

            bool hayCorredores = total > 0;
            TablaCorredores.Visibility = hayCorredores ? Visibility.Visible : Visibility.Collapsed;
            PanelEstadoVacioCorredores.Visibility = hayCorredores ? Visibility.Collapsed : Visibility.Visible;

            if (!hayCorredores)
            {
                if (AppState.CarreraActiva == null)
                {
                    TxtEstadoVacioTitulo.Text = "Sin carrera activa";
                    TxtEstadoVacioDescripcion.Text = "Selecciona o crea una carrera en Inicio para inscribir corredores.";
                    BtnAccionEstadoVacio.Content = "Ir a Inicio";
                }
                else if (_faltanCatalogos)
                {
                    TxtEstadoVacioTitulo.Text = "Faltan categorías o Hits";
                    TxtEstadoVacioDescripcion.Text = "Crea al menos una categoría y un Hit de salida antes de inscribir corredores.";
                    BtnAccionEstadoVacio.Content = "Ir a Hits y Categorías";
                }
                else
                {
                    TxtEstadoVacioTitulo.Text = "Sin corredores inscritos";
                    TxtEstadoVacioDescripcion.Text = "Completa el formulario de la izquierda con dorsal, nombre, categoría y Hit para registrar al primer corredor.";
                    BtnAccionEstadoVacio.Content = "Inscribir primer corredor";
                }
            }
        }

        private void BtnAccionEstadoVacio_Click(object sender, RoutedEventArgs e)
        {
            var mainWindow = ObtenerMainWindow();
            if (AppState.CarreraActiva == null && mainWindow != null)
            {
                mainWindow.NavegarAVista(new WelcomeView());
                return;
            }

            if (_faltanCatalogos && mainWindow != null)
            {
                mainWindow.NavegarAVista(new HitsView());
                return;
            }

            InputDorsal.Focus();
            Keyboard.Focus(InputDorsal);
        }

        private void LimpiarFormulario()
        {
            _idCorredorEnEdicion = null;
            InputDorsal.Clear();
            InputNombre.Clear();
            ComboCategoria.SelectedItem = null;
            ComboHit.SelectedItem = null;
            ComboHit.IsEnabled = true;
            ComboEstado.SelectedItem = EstadoCorredor.Activo;
            ComboEstado.IsEnabled = true;
            TxtAvisoBloqueoTiempo.Visibility = Visibility.Collapsed;
            BtnCancelarEdicion.Visibility = Visibility.Collapsed;
            TxtTituloFormularioCorredor.Text = "Inscribir corredor";
            BtnAccionCorredor.Content = "Registrar corredor";
            LimpiarErroresFormulario();
            InputDorsal.Focus();
        }

        private void InputDorsal_TextChanged(object sender, TextChangedEventArgs e) => LimpiarErrorDorsal();

        private void InputNombre_TextChanged(object sender, TextChangedEventArgs e) => LimpiarErrorNombre();

        private void ComboCategoria_SelectionChanged(object sender, SelectionChangedEventArgs e) => LimpiarErrorCategoria();

        private void ComboHit_SelectionChanged(object sender, SelectionChangedEventArgs e) => LimpiarErrorHit();

        private async void BtnGuardarCorredor_Click(object sender, RoutedEventArgs e)
        {
            LimpiarErroresFormulario();

            if (AppState.CarreraActiva == null)
            {
                MostrarFeedback(NivelFeedback.Advertencia, "Selecciona una carrera activa antes de registrar corredores.");
                return;
            }

            if (!int.TryParse(InputDorsal.Text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int dorsalNum) || dorsalNum <= 0)
            {
                MostrarErrorDorsal("Ingresa un número entero positivo válido para el dorsal.");
                InputDorsal.Focus();
                return;
            }

            if (string.IsNullOrWhiteSpace(InputNombre.Text))
            {
                MostrarErrorNombre("Escribe el nombre del corredor antes de guardar.");
                InputNombre.Focus();
                return;
            }

            if (ComboCategoria.SelectedValue is not int idCategoria)
            {
                MostrarErrorCategoria("Selecciona una categoría válida.");
                ComboCategoria.Focus();
                return;
            }

            if (ComboHit.SelectedValue is not int idHit)
            {
                MostrarErrorHit("Selecciona un Hit de salida válido.");
                ComboHit.Focus();
                return;
            }

            EstadoCorredor estadoSeleccionado = ComboEstado.SelectedItem is EstadoCorredor estado
                ? estado
                : EstadoCorredor.Activo;

            int idCarrera = AppState.CarreraActiva.ID_Carrera;
            bool esEdicion = _idCorredorEnEdicion.HasValue;

            BtnAccionCorredor.IsEnabled = false;
            BtnAccionCorredor.Content = esEdicion ? "Actualizando..." : "Guardando...";
            ProgresoGuardadoCorredor.Visibility = Visibility.Visible;

            try
            {
                OperacionResultado<Corredor> resultado = await _raceDataService.CrearOActualizarCorredorAsync(
                    _idCorredorEnEdicion,
                    idCarrera,
                    dorsalNum,
                    InputNombre.Text,
                    idCategoria,
                    idHit,
                    estadoSeleccionado);

                if (!resultado.Exito)
                {
                    EnrutarErrorValidacionCorredor(resultado.Mensaje);
                    return;
                }

                string nombreGuardado = resultado.Entidad?.Nombre ?? InputNombre.Text.Trim();
                int dorsalGuardado = resultado.Entidad?.Dorsal ?? dorsalNum;

                LimpiarFormulario();
                await CargarDatosInicialesAsync(_viewCts?.Token ?? CancellationToken.None);

                if (resultado.HitFinalizado)
                {
                    MostrarFeedback(
                        NivelFeedback.Informacion,
                        $"Corredor #{dorsalGuardado} guardado. El Hit \"{resultado.NombreHitFinalizado}\" finalizó automáticamente.");
                }
                else
                {
                    MostrarFeedback(
                        NivelFeedback.Exito,
                        esEdicion
                            ? $"Corredor #{dorsalGuardado} ({nombreGuardado}) actualizado."
                            : $"Corredor #{dorsalGuardado} ({nombreGuardado}) registrado.");
                }
            }
            catch (Exception ex)
            {
                MostrarFeedback(NivelFeedback.Error, $"No se pudo guardar el corredor: {ex.Message}");
            }
            finally
            {
                ProgresoGuardadoCorredor.Visibility = Visibility.Collapsed;
                BtnAccionCorredor.IsEnabled = true;
                BtnAccionCorredor.Content = _idCorredorEnEdicion.HasValue ? "Actualizar corredor" : "Registrar corredor";
            }
        }

        private void EnrutarErrorValidacionCorredor(string mensaje)
        {
            if (mensaje.Contains("dorsal", StringComparison.OrdinalIgnoreCase))
            {
                MostrarErrorDorsal(mensaje);
                InputDorsal.Focus();
            }
            else if (mensaje.Contains("nombre", StringComparison.OrdinalIgnoreCase))
            {
                MostrarErrorNombre(mensaje);
                InputNombre.Focus();
            }
            else if (mensaje.Contains("categor", StringComparison.OrdinalIgnoreCase))
            {
                MostrarErrorCategoria(mensaje);
                ComboCategoria.Focus();
            }
            else if (mensaje.Contains("hit", StringComparison.OrdinalIgnoreCase))
            {
                MostrarErrorHit(mensaje);
                ComboHit.Focus();
            }
            else
            {
                MostrarFeedback(NivelFeedback.Error, mensaje);
            }
        }

        private void BtnEditarCorredor_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as Button)?.DataContext is CorredorFilaView corredorSel)
            {
                LimpiarErroresFormulario();
                _idCorredorEnEdicion = corredorSel.ID_Corredor;

                InputDorsal.Text = corredorSel.Dorsal.ToString(CultureInfo.InvariantCulture);
                InputNombre.Text = corredorSel.Nombre;
                ComboCategoria.SelectedValue = corredorSel.ID_Categoria;
                ComboHit.SelectedValue = corredorSel.ID_Hit;
                ComboEstado.SelectedItem = corredorSel.Estado;

                // Regla de bloqueo: impedir cambio de Hit y estado cuando ya existe tiempo registrado
                ComboHit.IsEnabled = !corredorSel.TieneTiempo;
                ComboEstado.IsEnabled = !corredorSel.TieneTiempo;
                TxtAvisoBloqueoTiempo.Visibility = corredorSel.TieneTiempo ? Visibility.Visible : Visibility.Collapsed;
                BtnCancelarEdicion.Visibility = Visibility.Visible;

                TxtTituloFormularioCorredor.Text = $"Editando dorsal #{corredorSel.Dorsal}";
                BtnAccionCorredor.Content = "Actualizar corredor";
                InputNombre.Focus();
            }
        }

        private void BtnCancelarEdicion_Click(object sender, RoutedEventArgs e)
        {
            LimpiarFormulario();
        }

        private async void ComboEstadoFila_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_actualizandoTabla ||
                sender is not ComboBox combo ||
                !combo.IsLoaded ||
                combo.DataContext is not CorredorFilaView fila ||
                combo.SelectedItem is not EstadoCorredor nuevoEstado ||
                nuevoEstado == fila.Estado)
            {
                return;
            }

            combo.IsEnabled = false;
            try
            {
                OperacionResultado<Corredor> resultado = await _raceDataService.CambiarEstadoCorredorAsync(
                    fila.ID_Corredor,
                    nuevoEstado);

                if (!resultado.Exito)
                {
                    MostrarFeedback(NivelFeedback.Advertencia, resultado.Mensaje);
                }
                else
                {
                    if (_idCorredorEnEdicion == fila.ID_Corredor)
                    {
                        ComboEstado.SelectedItem = nuevoEstado;
                    }

                    if (resultado.HitFinalizado)
                    {
                        MostrarFeedback(
                            NivelFeedback.Informacion,
                            $"Estado de #{fila.Dorsal} actualizado. El Hit \"{resultado.NombreHitFinalizado}\" finalizó automáticamente.");
                    }
                    else
                    {
                        MostrarFeedback(
                            NivelFeedback.Exito,
                            $"Estado de #{fila.Dorsal} ({fila.Nombre}) cambiado a {nuevoEstado}.");
                    }
                }

                await CargarDatosInicialesAsync(_viewCts?.Token ?? CancellationToken.None);
            }
            catch (Exception ex)
            {
                MostrarFeedback(NivelFeedback.Error, $"No se pudo actualizar el estado: {ex.Message}");
            }
            finally
            {
                combo.IsEnabled = fila.PuedeModificarEstadoYHit;
            }
        }

        private async void BtnEliminarCorredor_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as Button)?.DataContext is CorredorFilaView corredorSel)
            {
                var result = MessageBox.Show(
                    $"¿Eliminar a {corredorSel.Nombre} (Dorsal {corredorSel.Dorsal})?\nEsta acción no se puede deshacer.",
                    "Confirmar eliminación",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Warning);

                if (result == MessageBoxResult.Yes)
                {
                    try
                    {
                        OperacionResultado resultado = await _raceDataService.EliminarCorredorAsync(corredorSel.ID_Corredor);
                        if (!resultado.Exito)
                        {
                            MostrarFeedback(NivelFeedback.Error, resultado.Mensaje);
                            return;
                        }

                        if (_idCorredorEnEdicion == corredorSel.ID_Corredor)
                        {
                            LimpiarFormulario();
                        }

                        await CargarDatosInicialesAsync(_viewCts?.Token ?? CancellationToken.None);
                        MostrarFeedback(
                            NivelFeedback.Exito,
                            $"Corredor #{corredorSel.Dorsal} ({corredorSel.Nombre}) eliminado.");
                    }
                    catch (Exception ex)
                    {
                        MostrarFeedback(NivelFeedback.Error, $"No se pudo eliminar al corredor: {ex.Message}");
                    }
                }
            }
        }

        private async void BtnExportarExcel_Click(object sender, RoutedEventArgs e)
        {
            if (AppState.CarreraActiva == null)
            {
                MostrarFeedback(NivelFeedback.Advertencia, "Selecciona una carrera activa antes de exportar.");
                return;
            }

            if (TablaCorredores.ItemsSource is not IEnumerable<CorredorFilaView> origen)
            {
                MostrarFeedback(NivelFeedback.Informacion, "No hay corredores registrados en esta carrera para exportar.");
                return;
            }

            List<CorredorFilaView> snapshot = origen.ToList();
            if (snapshot.Count == 0)
            {
                MostrarFeedback(NivelFeedback.Informacion, "No hay corredores registrados en esta carrera para exportar.");
                return;
            }

            try
            {
                string safeCarrera = StorageHelper.SanitizarSegmentoRuta(AppState.CarreraActiva.Nombre, "Carrera", 60);

                var saveDialog = new Microsoft.Win32.SaveFileDialog
                {
                    Title = "Exportar corredores a CSV (Excel)",
                    Filter = "Archivo CSV compatible con Excel (*.csv)|*.csv|Todos los archivos (*.*)|*.*",
                    FileName = $"Corredores_{safeCarrera}_{DateTime.Now:yyyyMMdd_HHmm}.csv"
                };

                if (saveDialog.ShowDialog() == true)
                {
                    BtnExportarExcel.IsEnabled = false;
                    BtnExportarExcel.Content = "Exportando...";
                    ProgresoExportacionCorredores.Visibility = Visibility.Visible;

                    try
                    {
                        // Proyección diferida: las propiedades y la generación/escritura CSV se procesan fuera del hilo UI
                        IEnumerable<CorredorCsvFila> filasParaExportar = snapshot.Select(item => new CorredorCsvFila(
                            item.Dorsal,
                            item.Nombre,
                            item.NombreCategoria,
                            item.NombreHit,
                            item.Estado.ToString(),
                            item.TiempoFormateado,
                            item.HoraLlegada.HasValue
                                ? item.HoraLlegada.Value.ToString("HH:mm:ss", CultureInfo.InvariantCulture)
                                : "--:--:--"));

                        await CsvExportHelper.ExportarCorredoresCsvAsync(saveDialog.FileName, filasParaExportar);
                    }
                    finally
                    {
                        ProgresoExportacionCorredores.Visibility = Visibility.Collapsed;
                        BtnExportarExcel.IsEnabled = true;
                        BtnExportarExcel.Content = "Exportar CSV (Excel)";
                    }

                    string nombreArchivo = Path.GetFileName(saveDialog.FileName);
                    MostrarFeedback(
                        NivelFeedback.Exito,
                        $"Se exportaron {snapshot.Count} corredores a {nombreArchivo}.");
                }
            }
            catch (Exception ex)
            {
                MostrarFeedback(NivelFeedback.Error, $"No se pudo exportar el archivo CSV: {ex.Message}");
            }
        }

        private void MostrarErrorDorsal(string mensaje)
        {
            InputDorsal.BorderBrush = CrearBrushCongelado(UiFeedbackHelper.TokensPaleta.PenaltyCrimson);
            InputDorsal.BorderThickness = new Thickness(2);
            TxtErrorDorsal.Text = mensaje;
            TxtErrorDorsal.Visibility = Visibility.Visible;
        }

        private void LimpiarErrorDorsal()
        {
            InputDorsal.ClearValue(BorderBrushProperty);
            InputDorsal.ClearValue(BorderThicknessProperty);
            TxtErrorDorsal.Text = string.Empty;
            TxtErrorDorsal.Visibility = Visibility.Collapsed;
        }

        private void MostrarErrorNombre(string mensaje)
        {
            InputNombre.BorderBrush = CrearBrushCongelado(UiFeedbackHelper.TokensPaleta.PenaltyCrimson);
            InputNombre.BorderThickness = new Thickness(2);
            TxtErrorNombreCorredor.Text = mensaje;
            TxtErrorNombreCorredor.Visibility = Visibility.Visible;
        }

        private void LimpiarErrorNombre()
        {
            InputNombre.ClearValue(BorderBrushProperty);
            InputNombre.ClearValue(BorderThicknessProperty);
            TxtErrorNombreCorredor.Text = string.Empty;
            TxtErrorNombreCorredor.Visibility = Visibility.Collapsed;
        }

        private void MostrarErrorCategoria(string mensaje)
        {
            ComboCategoria.BorderBrush = CrearBrushCongelado(UiFeedbackHelper.TokensPaleta.PenaltyCrimson);
            ComboCategoria.BorderThickness = new Thickness(2);
            TxtErrorCategoria.Text = mensaje;
            TxtErrorCategoria.Visibility = Visibility.Visible;
        }

        private void LimpiarErrorCategoria()
        {
            ComboCategoria.ClearValue(BorderBrushProperty);
            ComboCategoria.ClearValue(BorderThicknessProperty);
            TxtErrorCategoria.Text = string.Empty;
            TxtErrorCategoria.Visibility = Visibility.Collapsed;
        }

        private void MostrarErrorHit(string mensaje)
        {
            ComboHit.BorderBrush = CrearBrushCongelado(UiFeedbackHelper.TokensPaleta.PenaltyCrimson);
            ComboHit.BorderThickness = new Thickness(2);
            TxtErrorHit.Text = mensaje;
            TxtErrorHit.Visibility = Visibility.Visible;
        }

        private void LimpiarErrorHit()
        {
            ComboHit.ClearValue(BorderBrushProperty);
            ComboHit.ClearValue(BorderThicknessProperty);
            TxtErrorHit.Text = string.Empty;
            TxtErrorHit.Visibility = Visibility.Collapsed;
        }

        private void LimpiarErroresFormulario()
        {
            LimpiarErrorDorsal();
            LimpiarErrorNombre();
            LimpiarErrorCategoria();
            LimpiarErrorHit();
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

            BannerFeedbackCorredores.Background = fondo;
            BannerFeedbackCorredores.BorderBrush = borde;
            TxtBannerFeedbackCorredores.Foreground = texto;
            BtnCerrarBannerCorredores.Foreground = texto;
            TxtBannerFeedbackCorredores.Text = config.Mensaje;
            BannerFeedbackCorredores.Visibility = Visibility.Visible;

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

        private void BtnCerrarBannerCorredores_Click(object sender, RoutedEventArgs e)
        {
            OcultarBannerEnLinea();
        }

        private void OcultarBannerEnLinea()
        {
            DetenerFeedbackTimer();
            BannerFeedbackCorredores.Visibility = Visibility.Collapsed;
            TxtBannerFeedbackCorredores.Text = string.Empty;
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
