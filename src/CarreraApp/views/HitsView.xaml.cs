using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using Microsoft.EntityFrameworkCore;
using CarreraApp.Data;
using CarreraApp.Models;

namespace CarreraApp.UI.Views
{
    public partial class HitsView : UserControl
    {
        // Variables para controlar el estado de edición
        private Hit? _hitEnEdicion = null;
        private Categoria? _categoriaEnEdicion = null;

        public HitsView()
        {
            InitializeComponent();
            CargarDatos();
        }

        private async void CargarDatos()
        {
            if (AppState.CarreraActiva == null) return;
            using (var db = new ApplicationDbContext())
            {
                int idCarrera = AppState.CarreraActiva.ID_Carrera;
                TablaHits.ItemsSource = await db.Hits.AsNoTracking().Where(h => h.ID_Carrera == idCarrera).ToListAsync();
                TablaCategorias.ItemsSource = await db.Categorias.AsNoTracking().Where(c => c.ID_Carrera == idCarrera).ToListAsync();
            }
        }

        // --- LÓGICA DE HITS ---

        private async void BtnGuardarHit_Click(object sender, RoutedEventArgs e)
        {
            string nombre = InputNombreHit.Text.Trim();
            if (string.IsNullOrEmpty(nombre) || AppState.CarreraActiva == null) return;

            using (var db = new ApplicationDbContext())
            {
                if (_hitEnEdicion == null)
                {
                    // Crear nuevo
                    db.Hits.Add(new Hit { Nombre_Hit = nombre, ID_Carrera = AppState.CarreraActiva.ID_Carrera });
                }
                else
                {
                    // Actualizar existente
                    var hitDb = await db.Hits.FindAsync(_hitEnEdicion.ID_Hit);
                    if (hitDb != null) hitDb.Nombre_Hit = nombre;
                    
                    _hitEnEdicion = null;
                    BtnAccionHit.Content = "GUARDAR HIT";
                }
                await db.SaveChangesAsync();
            }

            InputNombreHit.Clear();
            CargarDatos();
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
                        using (var db = new ApplicationDbContext())
                        {
                            db.Hits.Remove(hitSeleccionado);
                            await db.SaveChangesAsync();
                        }
                        CargarDatos();
                    }
                    catch (Exception)
                    {
                        MessageBox.Show("No se puede eliminar porque hay corredores asignados a este Hit.", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                    }
                }
            }
        }

        // --- LÓGICA DE CATEGORÍAS ---

        private async void BtnGuardarCategoria_Click(object sender, RoutedEventArgs e)
        {
            string nombre = InputNombreCategoria.Text.Trim();
            if (string.IsNullOrEmpty(nombre) || AppState.CarreraActiva == null) return;

            using (var db = new ApplicationDbContext())
            {
                if (_categoriaEnEdicion == null)
                {
                    db.Categorias.Add(new Categoria { Nombre = nombre, ID_Carrera = AppState.CarreraActiva.ID_Carrera });
                }
                else
                {
                    var catDb = await db.Categorias.FindAsync(_categoriaEnEdicion.ID_Categoria);
                    if (catDb != null) catDb.Nombre = nombre;

                    _categoriaEnEdicion = null;
                    BtnAccionCategoria.Content = "GUARDAR CATEGORÍA";
                }
                await db.SaveChangesAsync();
            }

            InputNombreCategoria.Clear();
            CargarDatos();
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
                        using (var db = new ApplicationDbContext())
                        {
                            db.Categorias.Remove(catSeleccionada);
                            await db.SaveChangesAsync();
                        }
                        CargarDatos();
                    }
                    catch (Exception)
                    {
                        MessageBox.Show("No se puede eliminar porque hay corredores asignados a esta Categoría.", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                    }
                }
            }
        }
    }
}