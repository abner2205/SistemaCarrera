using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using CarreraApp.Data;
using CarreraApp.Models;

namespace CarreraApp.UI.Views
{
    public partial class HitsView : UserControl
    {
        private readonly RaceDataService _raceDataService = new();

        // Variables para controlar el estado de edición
        private Hit? _hitEnEdicion = null;
        private Categoria? _categoriaEnEdicion = null;

        public HitsView()
        {
            InitializeComponent();
            _ = CargarDatosAsync();
        }

        private async Task CargarDatosAsync()
        {
            if (AppState.CarreraActiva == null) return;

            try
            {
                int idCarrera = AppState.CarreraActiva.ID_Carrera;
                TablaHits.ItemsSource = await _raceDataService.ObtenerHitsPorCarreraAsync(idCarrera);
                TablaCategorias.ItemsSource = await _raceDataService.ObtenerCategoriasPorCarreraAsync(idCarrera);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error al cargar hits y categorías: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        // --- LÓGICA DE HITS ---

        private async void BtnGuardarHit_Click(object sender, RoutedEventArgs e)
        {
            if (AppState.CarreraActiva == null) return;

            try
            {
                OperacionResultado<Hit> resultado = await _raceDataService.CrearOActualizarHitAsync(
                    _hitEnEdicion?.ID_Hit,
                    AppState.CarreraActiva.ID_Carrera,
                    InputNombreHit.Text);

                if (!resultado.Exito)
                {
                    MessageBox.Show(resultado.Mensaje, "Validación de Hit", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                _hitEnEdicion = null;
                BtnAccionHit.Content = "GUARDAR HIT";
                InputNombreHit.Clear();
                await CargarDatosAsync();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error al guardar el Hit: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void BtnEditarHit_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as Button)?.DataContext is Hit hitSeleccionado)
            {
                _hitEnEdicion = hitSeleccionado;
                InputNombreHit.Text = hitSeleccionado.Nombre_Hit;
                BtnAccionHit.Content = "ACTUALIZAR HIT";
            }
        }

        private async void BtnEliminarHit_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as Button)?.DataContext is Hit hitSeleccionado)
            {
                var result = MessageBox.Show($"¿Eliminar el hit '{hitSeleccionado.Nombre_Hit}'?", "Confirmar", MessageBoxButton.YesNo, MessageBoxImage.Warning);
                if (result == MessageBoxResult.Yes)
                {
                    try
                    {
                        OperacionResultado resultado = await _raceDataService.EliminarHitAsync(hitSeleccionado.ID_Hit);
                        if (!resultado.Exito)
                        {
                            MessageBox.Show(resultado.Mensaje, "Eliminación Protegida", MessageBoxButton.OK, MessageBoxImage.Error);
                            return;
                        }

                        if (_hitEnEdicion?.ID_Hit == hitSeleccionado.ID_Hit)
                        {
                            _hitEnEdicion = null;
                            BtnAccionHit.Content = "GUARDAR HIT";
                            InputNombreHit.Clear();
                        }

                        await CargarDatosAsync();
                    }
                    catch (Exception ex)
                    {
                        MessageBox.Show($"Error al eliminar el Hit: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                    }
                }
            }
        }

        // --- LÓGICA DE CATEGORÍAS ---

        private async void BtnGuardarCategoria_Click(object sender, RoutedEventArgs e)
        {
            if (AppState.CarreraActiva == null) return;

            try
            {
                OperacionResultado<Categoria> resultado = await _raceDataService.CrearOActualizarCategoriaAsync(
                    _categoriaEnEdicion?.ID_Categoria,
                    AppState.CarreraActiva.ID_Carrera,
                    InputNombreCategoria.Text);

                if (!resultado.Exito)
                {
                    MessageBox.Show(resultado.Mensaje, "Validación de Categoría", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                _categoriaEnEdicion = null;
                BtnAccionCategoria.Content = "GUARDAR CATEGORÍA";
                InputNombreCategoria.Clear();
                await CargarDatosAsync();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error al guardar la categoría: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void BtnEditarCategoria_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as Button)?.DataContext is Categoria catSeleccionada)
            {
                _categoriaEnEdicion = catSeleccionada;
                InputNombreCategoria.Text = catSeleccionada.Nombre;
                BtnAccionCategoria.Content = "ACTUALIZAR CATEGORÍA";
            }
        }

        private async void BtnEliminarCategoria_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as Button)?.DataContext is Categoria catSeleccionada)
            {
                var result = MessageBox.Show($"¿Eliminar la categoría '{catSeleccionada.Nombre}'?", "Confirmar", MessageBoxButton.YesNo, MessageBoxImage.Warning);
                if (result == MessageBoxResult.Yes)
                {
                    try
                    {
                        OperacionResultado resultado = await _raceDataService.EliminarCategoriaAsync(catSeleccionada.ID_Categoria);
                        if (!resultado.Exito)
                        {
                            MessageBox.Show(resultado.Mensaje, "Eliminación Protegida", MessageBoxButton.OK, MessageBoxImage.Error);
                            return;
                        }

                        if (_categoriaEnEdicion?.ID_Categoria == catSeleccionada.ID_Categoria)
                        {
                            _categoriaEnEdicion = null;
                            BtnAccionCategoria.Content = "GUARDAR CATEGORÍA";
                            InputNombreCategoria.Clear();
                        }

                        await CargarDatosAsync();
                    }
                    catch (Exception ex)
                    {
                        MessageBox.Show($"Error al eliminar la categoría: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                    }
                }
            }
        }
    }
}