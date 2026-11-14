using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CarreraApp.Data;
using CarreraApp.Helpers;
using CarreraApp.Models;
using CarreraApp.UI;
using Microsoft.EntityFrameworkCore;

namespace CarreraApp.Tests
{
    [Collection("IsolatedStorage")]
    public class AppStateAndUiOptimizationTests
    {
        [Fact]
        public void FotoPendiente_Miniatura_NotificaPropertyChangedYSoportaBitmapSourceCongelado()
        {
            var foto = new FotoPendiente
            {
                IdFoto = "foto-1",
                Hora_Llegada = DateTime.Now,
                Ruta_Foto = "temp.jpg"
            };

            var propiedadesNotificadas = new List<string?>();
            foto.PropertyChanged += (_, args) => propiedadesNotificadas.Add(args.PropertyName);

            BitmapSource miniatura = CrearBitmapCongelado(160, 120);
            foto.Miniatura = miniatura;
            // Asignar la misma referencia no debe disparar notificación redundante
            foto.Miniatura = miniatura;

            Assert.Same(miniatura, foto.Miniatura);
            Assert.Single(propiedadesNotificadas);
            Assert.Equal(nameof(FotoPendiente.Miniatura), propiedadesNotificadas[0]);
        }

        [Fact]
        public async Task EjecutarSecuenciaInicializacionAsync_RespaldaMigraYLimpiarHuerfanosSinBloquearSiFallaRespaldo()
        {
            using var env = new TestEnvironmentFixture();
            Assert.Null(Application.Current);

            // Crear BD previa para verificar respaldo + migración idempotente + limpieza Fotos_Temp
            await DatabaseMaintenanceHelper.InicializarYMigrarBaseDatosAsync(env.CrearFabricaContexto());

            Directory.CreateDirectory(env.FotosTempDir);
            Directory.CreateDirectory(env.FotosDefinitivasDir);
            string gitkeep = Path.Combine(env.FotosTempDir, ".gitkeep");
            string huerfanaJpg = Path.Combine(env.FotosTempDir, "huerfana.jpg");
            string huerfanaWebp = Path.Combine(env.FotosTempDir, "huerfana.webp");
            string definitiva = Path.Combine(env.FotosDefinitivasDir, "meta_final.jpg");

            await File.WriteAllTextAsync(gitkeep, string.Empty);
            await File.WriteAllBytesAsync(huerfanaJpg, new byte[] { 1, 2, 3 });
            await File.WriteAllBytesAsync(huerfanaWebp, new byte[] { 4, 5, 6 });
            await File.WriteAllBytesAsync(definitiva, new byte[] { 7, 8, 9 });

            RespaldoResultado ok = await App.EjecutarSecuenciaInicializacionAsync(env.CrearFabricaContexto());
            Assert.True(ok.Exito, ok.Mensaje);
            Assert.NotNull(ok.RutaRespaldo);
            Assert.True(File.Exists(ok.RutaRespaldo));
            Assert.True(File.Exists(gitkeep));
            Assert.False(File.Exists(huerfanaJpg));
            Assert.False(File.Exists(huerfanaWebp));
            Assert.True(File.Exists(definitiva));

            // Simular fallo de respaldo pasando un archivo existente como carpeta de respaldos
            string archivoBloqueante = Path.Combine(env.RootDirectory, "no_es_directorio.txt");
            await File.WriteAllTextAsync(archivoBloqueante, "bloqueo");
            await File.WriteAllBytesAsync(huerfanaJpg, new byte[] { 1, 2, 3 });

            RespaldoResultado falloControlado = await App.EjecutarSecuenciaInicializacionAsync(
                env.CrearFabricaContexto(),
                carpetaRespaldos: archivoBloqueante);

            Assert.False(falloControlado.Exito);
            // A pesar del fallo de respaldo, la inicialización continúa y limpia temporales huérfanos
            Assert.False(File.Exists(huerfanaJpg));
            using var db = env.CrearContexto();
            Assert.True(await db.Database.CanConnectAsync());
        }

        [Fact]
        public async Task LogoRouteCache_ReutilizaImagenCongeladaSinReleerDiscoCuandoRutaNoCambia()
        {
            using var env = new TestEnvironmentFixture(configurarStorageGlobal: false);
            string rutaLogo1 = Path.Combine(env.RootDirectory, "logo1.png");
            string rutaLogo2 = Path.Combine(env.RootDirectory, "logo2.png");
            await File.WriteAllBytesAsync(rutaLogo1, CrearPngValido(320, 160));
            await File.WriteAllBytesAsync(rutaLogo2, CrearPngValido(320, 160));

            int lecturasDisco = 0;
            Task<BitmapImage?> CargadorContado(string ruta, CancellationToken ct)
            {
                Interlocked.Increment(ref lecturasDisco);
                return ImageHelper.CargarSinBloqueoAsync(ruta, decodePixelWidth: 280, ct);
            }

            var cache = new LogoRouteCache();

            BitmapImage? primera = await cache.ObtenerOCargarAsync(rutaLogo1, CargadorContado);
            BitmapImage? segunda = await cache.ObtenerOCargarAsync(rutaLogo1, CargadorContado);
            BitmapImage? tercera = await cache.ObtenerOCargarAsync(Path.GetFullPath(rutaLogo1), CargadorContado);

            Assert.NotNull(primera);
            Assert.True(primera!.IsFrozen);
            Assert.Same(primera, segunda);
            Assert.Same(primera, tercera);
            Assert.Equal(1, lecturasDisco);
            Assert.Equal(280, primera.PixelWidth);

            // Si cambia la ruta, carga la nueva y al retener solo la activa libera la anterior
            BitmapImage? segundoLogo = await cache.ObtenerOCargarAsync(rutaLogo2, CargadorContado);
            Assert.NotNull(segundoLogo);
            Assert.Equal(2, lecturasDisco);

            cache.RetenerSolo(rutaLogo2);
            Assert.Equal(1, cache.Count);
            Assert.False(cache.ContieneRuta(rutaLogo1));
            Assert.True(cache.ContieneRuta(rutaLogo2));

            BitmapImage? nulo = await cache.ObtenerOCargarAsync(null, CargadorContado);
            Assert.Null(nulo);

            cache.Limpiar();
            Assert.Equal(0, cache.Count);
        }

        [Fact]
        public void ReloadVersionCoordinator_CancelaRecargasPreviasYDescartaVersionesObsoletas()
        {
            var coordinator = new ReloadVersionCoordinator();

            (long v1, CancellationToken ct1) = coordinator.IniciarNuevaRecarga();
            Assert.True(coordinator.EsVigente(v1));
            Assert.False(ct1.IsCancellationRequested);

            (long v2, CancellationToken ct2) = coordinator.IniciarNuevaRecarga();
            Assert.True(v2 > v1);
            Assert.True(ct1.IsCancellationRequested);
            Assert.False(coordinator.EsVigente(v1));
            Assert.True(coordinator.EsVigente(v2));
            Assert.False(ct2.IsCancellationRequested);

            coordinator.CancelarYLimpiar();
            Assert.True(ct2.IsCancellationRequested);
            Assert.False(coordinator.EsVigente(v2));
        }

        [Fact]
        public void ResultadoView_UsaPincelesCongeladosYFormateaTiempoCorrectamente()
        {
            var primero = new ResultadoView { Posicion = 1, TiempoNeto = new TimeSpan(1, 2, 3) };
            var cuarto = new ResultadoView { Posicion = 4, TiempoNeto = new TimeSpan(0, 45, 9) };

            Assert.Equal("01:02:03", primero.TiempoFormateado);
            Assert.Equal("00:45:09", cuarto.TiempoFormateado);
            Assert.True(primero.ColorBordePodio.IsFrozen);
            Assert.True(primero.ColorFondoPodio.IsFrozen);
            Assert.True(primero.ColorTextoPodio.IsFrozen);
            Assert.Same(primero.ColorBordePodio, new ResultadoView { Posicion = 1 }.ColorBordePodio);
            Assert.True(cuarto.ColorBordePodio.IsFrozen);
        }

        private static BitmapSource CrearBitmapCongelado(int ancho, int alto)
        {
            int stride = ancho * 4;
            byte[] pixels = new byte[alto * stride];
            var bmp = BitmapSource.Create(ancho, alto, 96, 96, PixelFormats.Bgra32, null, pixels, stride);
            bmp.Freeze();
            return bmp;
        }

        private static byte[] CrearPngValido(int ancho, int alto)
        {
            BitmapSource fuente = CrearBitmapCongelado(ancho, alto);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(fuente));
            using var ms = new MemoryStream();
            encoder.Save(ms);
            return ms.ToArray();
        }
    }
}
