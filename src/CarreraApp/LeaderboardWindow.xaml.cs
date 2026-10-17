using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using Microsoft.EntityFrameworkCore;
using CarreraApp.Data;
using CarreraApp.Models;

namespace CarreraApp.UI
{
    public class ResultadoView
    {
        public int Posicion { get; set; }
        public int Dorsal { get; set; }
        public string Nombre { get; set; } = string.Empty;
        public TimeSpan TiempoNeto { get; set; }
        public string TiempoFormateado => $"{(int)TiempoNeto.TotalHours:00}:{TiempoNeto.Minutes:00}:{TiempoNeto.Seconds:00}";

        public string IconoPuesto => $"{Posicion}°";

        // Estilos para el Podio (1°, 2° y 3°)
        public string ColorBordePodio => Posicion switch
        {
            1 => "#FFC700", // Oro brillante
            2 => "#B0B7BD", // Plata metálico
            3 => "#CD7F32", // Bronce cálido
            _ => "#333333"
        };

        public string ColorFondoPodio => Posicion switch
        {
            1 => "#1A1707", // Fondo oscuro con tinte oro
            2 => "#14171A", // Fondo oscuro con tinte plata
            3 => "#1A1108", // Fondo oscuro con tinte bronce
            _ => "#121216"
        };

        public string ColorTextoPodio => Posicion switch
        {
            1 => "#FFC700",
            2 => "#D0D4D9",
            3 => "#CD7F32",
            _ => "#888888"
        };

        public double AlturaPodio => Posicion switch
        {
            1 => 60,
            2 => 54,
            3 => 50,
            _ => 38
        };

        public double FontSizePuestoPodio => Posicion switch
        {
            1 => 28,
            2 => 24,
            3 => 22,
            _ => 16
        };

        public double FontSizeNombrePodio => Posicion switch
        {
            1 => 24,
            2 => 20,
            3 => 18,
            _ => 14
        };

        public double FontSizeTiempoPodio => Posicion switch
        {
            1 => 24,
            2 => 20,
            3 => 18,
            _ => 14
        };
    }

    public partial class LeaderboardWindow : Window
    {
        public LeaderboardWindow()
        {
            InitializeComponent();
            
            // Suscribirse a eventos de nuevos tiempos y cambio de categoría
            AppState.NuevoTiempoRegistrado += ActualizarPizarra;
            AppState.CategoriaPizarraCambiada += ActualizarPizarra;

            ActualizarPizarra();
        }

        public async void ActualizarPizarra()
        {
            try
            {
                if (AppState.CarreraActiva == null) return;
                int idCarreraActual = AppState.CarreraActiva.ID_Carrera;

                // Si no hay categoría activa fijada en AppState, obtener la primera de la carrera activa
                if (AppState.CategoriaPizarraActiva == null)
                {
                    using var dbInit = new ApplicationDbContext();
                    AppState.CategoriaPizarraActiva = await dbInit.Categorias.AsNoTracking()
                        .FirstOrDefaultAsync(c => c.ID_Carrera == idCarreraActual);
                }

                if (AppState.CategoriaPizarraActiva == null) return;

                int idCategoria = AppState.CategoriaPizarraActiva.ID_Categoria;
                string nombreCategoria = AppState.CategoriaPizarraActiva.Nombre;
                string nombreCarrera = AppState.CarreraActiva.Nombre;

                using var db = new ApplicationDbContext();

                // 1. Filtrar en SQLite por carrera y categoría seleccionada
                var datosBrutos = await (from t in db.Tiempos.AsNoTracking()
                                         join c in db.Corredores.AsNoTracking() on t.ID_Corredor equals c.ID_Corredor
                                         where c.ID_Categoria == idCategoria && c.ID_Carrera == idCarreraActual
                                         select new
                                         {
                                             c.Dorsal,
                                             c.Nombre,
                                             t.Tiempo_Neto
                                         }).ToListAsync();

                // 2. Ordenar en memoria (LINQ to Objects) con precisión completa de TimeSpan
                var consulta = datosBrutos
                    .OrderBy(x => x.Tiempo_Neto)
                    .Select((x, index) => new ResultadoView
                    {
                        Posicion = index + 1,
                        Dorsal = x.Dorsal,
                        Nombre = x.Nombre,
                        TiempoNeto = x.Tiempo_Neto
                    })
                    .ToList();

                // Separar Podio (1°, 2° y 3°) y Resto de competidores (4° en adelante)
                var podio = consulta.Take(3).ToList();
                var resto = consulta.Skip(3).ToList();

                Dispatcher.Invoke(() =>
                {
                    TxtNombreCarrera.Text = nombreCarrera.ToUpper();
                    TxtNombreCategoria.Text = $"Resultados: {nombreCategoria}";

                    ListaPodio.ItemsSource = podio;
                    ListaResto.ItemsSource = resto;

                    CargarLogos();
                });
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error al actualizar pizarra: {ex.Message}");
            }
        }

        private void CargarLogos()
        {
            if (AppState.CarreraActiva == null)
            {
                ImgLogoIzquierdo.Visibility = Visibility.Collapsed;
                ImgLogoDerecho.Visibility = Visibility.Collapsed;
                return;
            }

            // Logo Izquierdo
            if (!string.IsNullOrEmpty(AppState.CarreraActiva.RutaLogoIzquierdo) && File.Exists(AppState.CarreraActiva.RutaLogoIzquierdo))
            {
                ImgLogoIzquierdo.Source = Helpers.ImageHelper.CargarSinBloqueo(AppState.CarreraActiva.RutaLogoIzquierdo);
                ImgLogoIzquierdo.Visibility = Visibility.Visible;
            }
            else
            {
                ImgLogoIzquierdo.Source = null;
                ImgLogoIzquierdo.Visibility = Visibility.Collapsed;
            }

            // Logo Derecho
            if (!string.IsNullOrEmpty(AppState.CarreraActiva.RutaLogoDerecho) && File.Exists(AppState.CarreraActiva.RutaLogoDerecho))
            {
                ImgLogoDerecho.Source = Helpers.ImageHelper.CargarSinBloqueo(AppState.CarreraActiva.RutaLogoDerecho);
                ImgLogoDerecho.Visibility = Visibility.Visible;
            }
            else
            {
                ImgLogoDerecho.Source = null;
                ImgLogoDerecho.Visibility = Visibility.Collapsed;
            }
        }

        protected override void OnClosed(EventArgs e)
        {
            AppState.NuevoTiempoRegistrado -= ActualizarPizarra;
            AppState.CategoriaPizarraCambiada -= ActualizarPizarra;
            base.OnClosed(e);
        }
    }
}