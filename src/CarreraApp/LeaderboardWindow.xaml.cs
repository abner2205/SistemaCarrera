using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using CarreraApp.Data;
using CarreraApp.Helpers;
using CarreraApp.Models;
using Microsoft.EntityFrameworkCore;

namespace CarreraApp.UI
{
    /// <summary>
    /// Coordina recargas concurrentes asignando versiones monotónicas y cancelando
    /// operaciones previas para descartar resultados obsoletos o fuera de orden.
    /// </summary>
    public sealed class ReloadVersionCoordinator : IDisposable
    {
        private readonly object _sync = new();
        private CancellationTokenSource? _ctsActual;
        private long _versionActual;
        private bool _disposed;

        public (long Version, CancellationToken Token) IniciarNuevaRecarga()
        {
            long nuevaVersion = Interlocked.Increment(ref _versionActual);
            var nuevoCts = new CancellationTokenSource();
            CancellationTokenSource? anterior;

            lock (_sync)
            {
                if (_disposed)
                {
                    nuevoCts.Cancel();
                    return (nuevaVersion, nuevoCts.Token);
                }

                anterior = _ctsActual;
                _ctsActual = nuevoCts;
            }

            CancelarYDisponerSilencioso(anterior);
            return (nuevaVersion, nuevoCts.Token);
        }

        public bool EsVigente(long version) =>
            !Volatile.Read(ref _disposed) && version == Volatile.Read(ref _versionActual);

        public void CancelarYLimpiar()
        {
            Interlocked.Increment(ref _versionActual);
            CancellationTokenSource? anterior;
            lock (_sync)
            {
                anterior = _ctsActual;
                _ctsActual = null;
            }

            CancelarYDisponerSilencioso(anterior);
        }

        public void Dispose()
        {
            Volatile.Write(ref _disposed, true);
            CancelarYLimpiar();
        }

        private static void CancelarYDisponerSilencioso(CancellationTokenSource? cts)
        {
            if (cts == null)
            {
                return;
            }

            try
            {
                cts.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // Ignorar si ya fue dispuesto
            }
            finally
            {
                cts.Dispose();
            }
        }
    }

    /// <summary>
    /// Caché en memoria de logotipos por ruta normalizada para evitar lecturas
    /// repetidas desde disco cuando las rutas no cambian.
    /// </summary>
    public sealed class LogoRouteCache
    {
        private const int LimiteEntradas = 16;
        private readonly Dictionary<string, BitmapImage> _cache = new(StringComparer.OrdinalIgnoreCase);
        private readonly object _sync = new();

        public int Count
        {
            get
            {
                lock (_sync)
                {
                    return _cache.Count;
                }
            }
        }

        public bool ContieneRuta(string? ruta)
        {
            if (string.IsNullOrWhiteSpace(ruta))
            {
                return false;
            }

            string clave = NormalizarClave(ruta);
            lock (_sync)
            {
                return _cache.ContainsKey(clave);
            }
        }

        public async Task<BitmapImage?> ObtenerOCargarAsync(
            string? ruta,
            Func<string, CancellationToken, Task<BitmapImage?>>? cargador = null,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(ruta))
            {
                return null;
            }

            string clave = NormalizarClave(ruta);
            lock (_sync)
            {
                if (_cache.TryGetValue(clave, out BitmapImage? existente))
                {
                    return existente;
                }
            }

            var loader = cargador ?? ((path, ct) => ImageHelper.CargarSinBloqueoAsync(path, decodePixelWidth: 280, ct));
            BitmapImage? cargado = await loader(ruta, cancellationToken).ConfigureAwait(false);
            if (cargado == null)
            {
                return null;
            }

            if (!cargado.IsFrozen && cargado.CanFreeze)
            {
                cargado.Freeze();
            }

            lock (_sync)
            {
                if (_cache.Count >= LimiteEntradas && !_cache.ContainsKey(clave))
                {
                    _cache.Clear();
                }

                _cache[clave] = cargado;
            }

            return cargado;
        }

        public void RetenerSolo(params string?[] rutasActivas)
        {
            var clavesActivas = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string? ruta in rutasActivas)
            {
                if (!string.IsNullOrWhiteSpace(ruta))
                {
                    clavesActivas.Add(NormalizarClave(ruta));
                }
            }

            lock (_sync)
            {
                if (_cache.Count == 0)
                {
                    return;
                }

                var obsoletas = _cache.Keys.Where(k => !clavesActivas.Contains(k)).ToList();
                foreach (string clave in obsoletas)
                {
                    _cache.Remove(clave);
                }
            }
        }

        public void Limpiar()
        {
            lock (_sync)
            {
                _cache.Clear();
            }
        }

        private static string NormalizarClave(string ruta)
        {
            try
            {
                return Path.GetFullPath(ruta.Trim());
            }
            catch
            {
                return ruta.Trim();
            }
        }
    }

    public class ResultadoView
    {
        private static readonly SolidColorBrush BordeOro = CrearPincelCongelado("#FACC15");
        private static readonly SolidColorBrush BordePlata = CrearPincelCongelado("#E2E8F0");
        private static readonly SolidColorBrush BordeBronce = CrearPincelCongelado("#FDBA74");
        private static readonly SolidColorBrush BordeDefault = CrearPincelCongelado("#334155");

        private static readonly SolidColorBrush FondoOro = CrearPincelCongelado("#17150B");
        private static readonly SolidColorBrush FondoPlata = CrearPincelCongelado("#131822");
        private static readonly SolidColorBrush FondoBronce = CrearPincelCongelado("#1A130D");
        private static readonly SolidColorBrush FondoDefault = CrearPincelCongelado("#111827");

        private static readonly SolidColorBrush TextoOro = BordeOro;
        private static readonly SolidColorBrush TextoPlata = BordePlata;
        private static readonly SolidColorBrush TextoBronce = BordeBronce;
        private static readonly SolidColorBrush TextoDefault = CrearPincelCongelado("#CBD5E1");

        public int Posicion { get; set; }
        public int Dorsal { get; set; }
        public string Nombre { get; set; } = string.Empty;
        public TimeSpan TiempoNeto { get; set; }
        public string TiempoFormateado => $"{(int)TiempoNeto.TotalHours:00}:{TiempoNeto.Minutes:00}:{TiempoNeto.Seconds:00}";

        public string IconoPuesto => $"{Posicion}°";
        public string DorsalFormateado => $"#{Dorsal}";

        // Estilos con pinceles congelados para el Podio (1°, 2° y 3°)
        public Brush ColorBordePodio => Posicion switch
        {
            1 => BordeOro,
            2 => BordePlata,
            3 => BordeBronce,
            _ => BordeDefault
        };

        public Brush ColorFondoPodio => Posicion switch
        {
            1 => FondoOro,
            2 => FondoPlata,
            3 => FondoBronce,
            _ => FondoDefault
        };

        public Brush ColorTextoPodio => Posicion switch
        {
            1 => TextoOro,
            2 => TextoPlata,
            3 => TextoBronce,
            _ => TextoDefault
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

        private static SolidColorBrush CrearPincelCongelado(string hex)
        {
            var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
            brush.Freeze();
            return brush;
        }
    }

    public partial class LeaderboardWindow : Window
    {
        private static readonly SolidColorBrush FondoPizarraRespaldo = CrearPincelCongelado("#0B0E14");
        private static readonly SolidColorBrush TextoPizarraRespaldo = CrearPincelCongelado("#F8FAFC");

        private readonly Func<ApplicationDbContext> _contextFactory;
        private readonly ReloadVersionCoordinator _reloadCoordinator = new();
        private readonly LogoRouteCache _logoCache = new();
        private bool _cerrada;

        public LeaderboardWindow() : this(null)
        {
        }

        public LeaderboardWindow(Func<ApplicationDbContext>? contextFactory)
        {
            _contextFactory = contextFactory ?? (() => new ApplicationDbContext());
            InitializeComponent();

            if (Background == null)
            {
                Background = FondoPizarraRespaldo;
            }
            if (Foreground == null)
            {
                Foreground = TextoPizarraRespaldo;
            }

            AppState.NuevoTiempoRegistrado += OnEventoPizarra;
            AppState.CategoriaPizarraCambiada += OnEventoPizarra;
            AppState.CarreraActivaCambiada += OnEventoPizarra;

            ActualizarPizarra();
        }

        private static SolidColorBrush CrearPincelCongelado(string hex)
        {
            var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
            brush.Freeze();
            return brush;
        }

        private void OnEventoPizarra()
        {
            if (_cerrada)
            {
                return;
            }

            _ = ActualizarPizarraAsync();
        }

        public void ActualizarPizarra()
        {
            _ = ActualizarPizarraAsync();
        }

        public async Task ActualizarPizarraAsync()
        {
            if (_cerrada)
            {
                return;
            }

            (long versionSolicitada, CancellationToken ct) = _reloadCoordinator.IniciarNuevaRecarga();

            try
            {
                Carrera? carrera = AppState.CarreraActiva;
                if (carrera == null)
                {
                    _logoCache.Limpiar();
                    await AplicarEstadoVacioAsync(versionSolicitada, ct).ConfigureAwait(false);
                    return;
                }

                int idCarreraActual = carrera.ID_Carrera;
                string nombreCarrera = carrera.Nombre;
                string? rutaLogoIzq = carrera.RutaLogoIzquierdo;
                string? rutaLogoDer = carrera.RutaLogoDerecho;
                Categoria? categoriaActiva = AppState.CategoriaPizarraActiva;

                if (categoriaActiva == null || categoriaActiva.ID_Carrera != idCarreraActual)
                {
                    using var dbInit = _contextFactory();
                    categoriaActiva = await dbInit.Categorias
                        .AsNoTracking()
                        .FirstOrDefaultAsync(c => c.ID_Carrera == idCarreraActual, ct)
                        .ConfigureAwait(false);

                    if (!PuedeAplicarResultado(versionSolicitada, ct))
                    {
                        return;
                    }

                    if (AppState.CarreraActiva?.ID_Carrera == idCarreraActual)
                    {
                        AppState.CategoriaPizarraActiva = categoriaActiva;
                    }
                }

                BitmapImage? logoIzq = await _logoCache
                    .ObtenerOCargarAsync(rutaLogoIzq, cancellationToken: ct)
                    .ConfigureAwait(false);
                BitmapImage? logoDer = await _logoCache
                    .ObtenerOCargarAsync(rutaLogoDer, cancellationToken: ct)
                    .ConfigureAwait(false);

                _logoCache.RetenerSolo(rutaLogoIzq, rutaLogoDer);

                if (!PuedeAplicarResultado(versionSolicitada, ct))
                {
                    return;
                }

                if (categoriaActiva == null)
                {
                    await AplicarEnUiSiVigenteAsync(versionSolicitada, ct, () =>
                    {
                        TxtNombreCarrera.Text = nombreCarrera;
                        TxtNombreCategoria.Text = "Sin categorías registradas";
                        TxtUltimaActualizacion.Text = string.Empty;
                        ListaPodio.ItemsSource = Array.Empty<ResultadoView>();
                        ListaResto.ItemsSource = Array.Empty<ResultadoView>();
                        TxtEstadoVacioTitulo.Text = "No hay categorías registradas";
                        TxtEstadoVacioDetalle.Text = "Crea al menos una categoría en la carrera activa para mostrar la clasificación.";
                        PanelEstadoVacio.Visibility = Visibility.Visible;
                        AplicarLogosEnControles(logoIzq, logoDer);
                    }).ConfigureAwait(false);
                    return;
                }

                int idCategoria = categoriaActiva.ID_Categoria;
                string nombreCategoria = categoriaActiva.Nombre;

                using var db = _contextFactory();
                var datosBrutos = await (from t in db.Tiempos.AsNoTracking()
                                         join c in db.Corredores.AsNoTracking() on t.ID_Corredor equals c.ID_Corredor
                                         where c.ID_Categoria == idCategoria && c.ID_Carrera == idCarreraActual
                                         select new
                                         {
                                             c.Dorsal,
                                             c.Nombre,
                                             t.Tiempo_Neto
                                         })
                    .ToListAsync(ct)
                    .ConfigureAwait(false);

                if (!PuedeAplicarResultado(versionSolicitada, ct))
                {
                    return;
                }

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

                var podio = consulta.Take(3).ToList();
                var resto = consulta.Skip(3).ToList();

                await AplicarEnUiSiVigenteAsync(versionSolicitada, ct, () =>
                {
                    TxtNombreCarrera.Text = nombreCarrera;
                    TxtNombreCategoria.Text = $"Resultados: {nombreCategoria}";
                    TxtUltimaActualizacion.Text = $"Actualizado {DateTime.Now:HH:mm:ss}";
                    ListaPodio.ItemsSource = podio;
                    ListaResto.ItemsSource = resto;

                    if (podio.Count == 0 && resto.Count == 0)
                    {
                        TxtEstadoVacioTitulo.Text = "Aún no hay tiempos registrados";
                        TxtEstadoVacioDetalle.Text = $"Los resultados de {nombreCategoria} aparecerán aquí automáticamente al cruzar la meta.";
                        PanelEstadoVacio.Visibility = Visibility.Visible;
                    }
                    else
                    {
                        PanelEstadoVacio.Visibility = Visibility.Collapsed;
                    }

                    AplicarLogosEnControles(logoIzq, logoDer);
                }).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested || _cerrada)
            {
                // Recarga cancelada por existir una solicitud más reciente o cierre de ventana
            }
            catch (Exception ex)
            {
                Trace.TraceWarning($"[LeaderboardWindow] Error al actualizar pizarra: {ex.Message}");
            }
        }

        private bool PuedeAplicarResultado(long versionSolicitada, CancellationToken ct) =>
            !_cerrada && !ct.IsCancellationRequested && _reloadCoordinator.EsVigente(versionSolicitada);

        private async Task AplicarEstadoVacioAsync(long versionSolicitada, CancellationToken ct)
        {
            await AplicarEnUiSiVigenteAsync(versionSolicitada, ct, () =>
            {
                TxtNombreCarrera.Text = "Sin carrera activa";
                TxtNombreCategoria.Text = "Selecciona una carrera";
                TxtUltimaActualizacion.Text = string.Empty;
                ListaPodio.ItemsSource = Array.Empty<ResultadoView>();
                ListaResto.ItemsSource = Array.Empty<ResultadoView>();
                TxtEstadoVacioTitulo.Text = "Sin carrera activa";
                TxtEstadoVacioDetalle.Text = "Selecciona una carrera en la ventana principal para proyectar la clasificación oficial.";
                PanelEstadoVacio.Visibility = Visibility.Visible;
                AplicarLogosEnControles(null, null);
            }).ConfigureAwait(false);
        }

        private async Task AplicarEnUiSiVigenteAsync(long versionSolicitada, CancellationToken ct, Action actualizarUi)
        {
            if (!PuedeAplicarResultado(versionSolicitada, ct) || Dispatcher.HasShutdownStarted)
            {
                return;
            }

            await Dispatcher.InvokeAsync(() =>
            {
                if (!PuedeAplicarResultado(versionSolicitada, ct))
                {
                    return;
                }

                actualizarUi();
            }, DispatcherPriority.Normal);
        }

        private void AplicarLogosEnControles(BitmapImage? logoIzq, BitmapImage? logoDer)
        {
            ImgLogoIzquierdo.Source = logoIzq;
            ImgLogoIzquierdo.Visibility = logoIzq != null ? Visibility.Visible : Visibility.Collapsed;

            ImgLogoDerecho.Source = logoDer;
            ImgLogoDerecho.Visibility = logoDer != null ? Visibility.Visible : Visibility.Collapsed;
        }

        protected override void OnClosed(EventArgs e)
        {
            _cerrada = true;

            AppState.NuevoTiempoRegistrado -= OnEventoPizarra;
            AppState.CategoriaPizarraCambiada -= OnEventoPizarra;
            AppState.CarreraActivaCambiada -= OnEventoPizarra;

            _reloadCoordinator.Dispose();
            _logoCache.Limpiar();

            ImgLogoIzquierdo.Source = null;
            ImgLogoDerecho.Source = null;
            ListaPodio.ItemsSource = null;
            ListaResto.ItemsSource = null;

            base.OnClosed(e);
        }
    }
}
