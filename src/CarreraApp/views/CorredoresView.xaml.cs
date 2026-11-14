using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using CarreraApp.Data;
using CarreraApp.Helpers;
using CarreraApp.Models;

namespace CarreraApp.UI.Views
{
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
    }

    public partial class CorredoresView : UserControl
    {
        private readonly RaceDataService _raceDataService = new();
        private int? _idCorredorEnEdicion = null;
        private bool _actualizandoTabla = false;

        public CorredoresView()
        {
            InitializeComponent();
            ComboEstado.ItemsSource = Enum.GetValues<EstadoCorredor>();
            ComboEstado.SelectedItem = EstadoCorredor.Activo;
            _ = CargarDatosInicialesAsync();
        }

        private async Task CargarDatosInicialesAsync()
        {
            if (AppState.CarreraActiva == null) return;
            int idCarrera = AppState.CarreraActiva.ID_Carrera;

            try
            {
                List<Categoria> categorias = await _raceDataService.ObtenerCategoriasPorCarreraAsync(idCarrera);
                List<Hit> hits = await _raceDataService.ObtenerHitsPorCarreraAsync(idCarrera);
                List<CorredorDetalleDto> detalle = await _raceDataService.ObtenerCorredoresDetallePorCarreraAsync(idCarrera);

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
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error al cargar corredores: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
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
            BtnAccionCorredor.Content = "REGISTRAR CORREDOR";
            InputDorsal.Focus();
        }

        private async void BtnGuardarCorredor_Click(object sender, RoutedEventArgs e)
        {
            if (AppState.CarreraActiva == null) return;

            if (!int.TryParse(InputDorsal.Text.Trim(), out int dorsalNum))
            {
                MessageBox.Show("Ingresa un número entero positivo válido para el dorsal.", "Validación", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (ComboCategoria.SelectedValue is not int idCategoria || ComboHit.SelectedValue is not int idHit)
            {
                MessageBox.Show("Selecciona una categoría y un Hit de salida válidos.", "Validación", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            EstadoCorredor estadoSeleccionado = ComboEstado.SelectedItem is EstadoCorredor estado
                ? estado
                : EstadoCorredor.Activo;

            int idCarrera = AppState.CarreraActiva.ID_Carrera;

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
                    MessageBox.Show(resultado.Mensaje, "Validación de Corredor", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                LimpiarFormulario();

                if (resultado.HitFinalizado)
                {
                    MessageBox.Show(
                        $"El Hit '{resultado.NombreHitFinalizado}' finalizó automáticamente porque no quedan corredores activos pendientes.",
                        "Hit Finalizado",
                        MessageBoxButton.OK,
                        MessageBoxImage.Information);
                }

                await CargarDatosInicialesAsync();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error al guardar corredor: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void BtnEditarCorredor_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as Button)?.DataContext is CorredorFilaView corredorSel)
            {
                _idCorredorEnEdicion = corredorSel.ID_Corredor;
                
                InputDorsal.Text = corredorSel.Dorsal.ToString();
                InputNombre.Text = corredorSel.Nombre;
                ComboCategoria.SelectedValue = corredorSel.ID_Categoria;
                ComboHit.SelectedValue = corredorSel.ID_Hit;
                ComboEstado.SelectedItem = corredorSel.Estado;

                // RF-21: bloquear cambio de Hit y cambio de estado cuando ya existe tiempo registrado
                ComboHit.IsEnabled = !corredorSel.TieneTiempo;
                ComboEstado.IsEnabled = !corredorSel.TieneTiempo;
                TxtAvisoBloqueoTiempo.Visibility = corredorSel.TieneTiempo ? Visibility.Visible : Visibility.Collapsed;
                BtnCancelarEdicion.Visibility = Visibility.Visible;

                BtnAccionCorredor.Content = "ACTUALIZAR CORREDOR";
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
                    MessageBox.Show(resultado.Mensaje, "Cambio de Estado Bloqueado", MessageBoxButton.OK, MessageBoxImage.Warning);
                }
                else
                {
                    if (_idCorredorEnEdicion == fila.ID_Corredor)
                    {
                        ComboEstado.SelectedItem = nuevoEstado;
                    }

                    if (resultado.HitFinalizado)
                    {
                        MessageBox.Show(
                            $"El Hit '{resultado.NombreHitFinalizado}' finalizó automáticamente porque no quedan corredores activos pendientes.",
                            "Hit Finalizado",
                            MessageBoxButton.OK,
                            MessageBoxImage.Information);
                    }
                }

                await CargarDatosInicialesAsync();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error al actualizar estado: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
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
                var result = MessageBox.Show($"¿Eliminar a {corredorSel.Nombre} (Dorsal {corredorSel.Dorsal})?", "Confirmar", MessageBoxButton.YesNo, MessageBoxImage.Warning);
                if (result == MessageBoxResult.Yes)
                {
                    try
                    {
                        OperacionResultado resultado = await _raceDataService.EliminarCorredorAsync(corredorSel.ID_Corredor);
                        if (!resultado.Exito)
                        {
                            MessageBox.Show(resultado.Mensaje, "Eliminación Protegida", MessageBoxButton.OK, MessageBoxImage.Error);
                            return;
                        }

                        if (_idCorredorEnEdicion == corredorSel.ID_Corredor)
                        {
                            LimpiarFormulario();
                        }

                        await CargarDatosInicialesAsync();
                    }
                    catch (Exception ex)
                    {
                        MessageBox.Show($"Error al eliminar al corredor: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                    }
                }
            }
        }

        private async void BtnExportarExcel_Click(object sender, RoutedEventArgs e)
        {
            if (AppState.CarreraActiva == null)
            {
                MessageBox.Show("Por favor selecciona una carrera activa primero.", "Atención", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (TablaCorredores.ItemsSource is not IEnumerable<CorredorFilaView> origen)
            {
                MessageBox.Show("No hay corredores registrados en esta carrera para exportar.", "Información", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            List<CorredorFilaView> snapshot = origen.ToList();
            if (snapshot.Count == 0)
            {
                MessageBox.Show("No hay corredores registrados en esta carrera para exportar.", "Información", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            try
            {
                string safeCarrera = StorageHelper.SanitizarSegmentoRuta(AppState.CarreraActiva.Nombre, "Carrera", 60);

                var saveDialog = new Microsoft.Win32.SaveFileDialog
                {
                    Title = "Exportar Corredores a Excel",
                    Filter = "Archivo CSV compatible con Excel (*.csv)|*.csv|Todos los archivos (*.*)|*.*",
                    FileName = $"Corredores_{safeCarrera}_{DateTime.Now:yyyyMMdd_HHmm}.csv"
                };

                if (saveDialog.ShowDialog() == true)
                {
                    BtnExportarExcel.IsEnabled = false;
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
                        BtnExportarExcel.IsEnabled = true;
                    }

                    var resultado = MessageBox.Show(
                        $"Se exportaron exitosamente {snapshot.Count} corredores a:\n{saveDialog.FileName}\n\n¿Deseas abrir el archivo en Excel ahora?",
                        "Exportación Completada",
                        MessageBoxButton.YesNo,
                        MessageBoxImage.Information);

                    if (resultado == MessageBoxResult.Yes)
                    {
                        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(saveDialog.FileName)
                        {
                            UseShellExecute = true
                        });
                    }
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error al exportar a Excel: {ex.Message}", "Error de Exportación", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }
}