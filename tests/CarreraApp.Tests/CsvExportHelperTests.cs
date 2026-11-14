using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using CarreraApp.Helpers;

namespace CarreraApp.Tests
{
    public class CsvExportHelperTests
    {
        [Theory]
        [InlineData("=1+1", "'=1+1")]
        [InlineData("=CMD('calc')", "'=CMD('calc')")]
        [InlineData("+SUM(A1:A5)", "'+SUM(A1:A5)")]
        [InlineData("-2+3", "'-2+3")]
        [InlineData("@SUM(1,2)", "'@SUM(1,2)")]
        [InlineData("\t=1+1", "'\t=1+1")]
        [InlineData("\tHola", "'\tHola")]
        [InlineData("   =1+1", "'   =1+1")]
        [InlineData("  +cmd", "'  +cmd")]
        [InlineData(" -@SUM", "' -@SUM")]
        [InlineData("   @A1", "'   @A1")]
        [InlineData("  \t=cmd", "'  \t=cmd")]
        [InlineData("\u00A0=1+1", "'\u00A0=1+1")]
        [InlineData("\n=1+1", "\"'\n=1+1\"")]
        [InlineData("\r=1+1", "\"'\r=1+1\"")]
        [InlineData("  \r+cmd", "\"'  \r+cmd\"")]
        [InlineData("\r\n=1+1", "\"'\r\n=1+1\"")]
        public void SanitizarCeldaCsv_ConPrefijosPeligrososDirectosOTrasEspacios_AnteponeApostrofe(
            string entrada,
            string esperado)
        {
            string resultado = CsvExportHelper.SanitizarCeldaCsv(entrada);

            Assert.Equal(esperado, resultado);
        }

        [Theory]
        [InlineData(null, "")]
        [InlineData("", "")]
        [InlineData("   ", "   ")]
        [InlineData("Juan Pérez", "Juan Pérez")]
        [InlineData("Ana - María", "Ana - María")]
        [InlineData("juez@carrera.local", "juez@carrera.local")]
        [InlineData("10 + 5K", "10 + 5K")]
        [InlineData("Categoría Élite", "Categoría Élite")]
        [InlineData("'=1+1", "'=1+1")]
        public void SanitizarCeldaCsv_ConTextoNormal_NoAlteraValor(string? entrada, string esperado)
        {
            string resultado = CsvExportHelper.SanitizarCeldaCsv(entrada);

            Assert.Equal(esperado, resultado);
        }

        [Theory]
        [InlineData("--:--:--", "--:--:--")]
        [InlineData("00:12:34", "00:12:34")]
        [InlineData("-00:01:00", "-00:01:00")]
        [InlineData("Activo", "Activo")]
        [InlineData("DNS", "DNS")]
        [InlineData("DNF", "DNF")]
        public void SanitizarCeldaCsv_ConValorSistema_PreservaValorSinApostrofe(string entrada, string esperado)
        {
            string resultado = CsvExportHelper.SanitizarCeldaCsv(entrada, esValorSistema: true);

            Assert.Equal(esperado, resultado);
        }

        [Theory]
        [InlineData("Pérez; Juan", "\"Pérez; Juan\"")]
        [InlineData("Juan \"El Rayo\" Díaz", "\"Juan \"\"El Rayo\"\" Díaz\"")]
        [InlineData("Línea 1\nLínea 2", "\"Línea 1\nLínea 2\"")]
        [InlineData("Línea 1\rLínea 2", "\"Línea 1\rLínea 2\"")]
        [InlineData("=CONCAT(\"A\";\"B\")", "\"'=CONCAT(\"\"A\"\";\"\"B\"\")\"")]
        [InlineData("  +cmd;\"x\"", "\"'  +cmd;\"\"x\"\"\"")]
        public void SanitizarCeldaCsv_ConDelimitadorComillasOSaltos_EscapaSegunRfc4180(
            string entrada,
            string esperado)
        {
            string resultado = CsvExportHelper.SanitizarCeldaCsv(entrada);

            Assert.Equal(esperado, resultado);
        }

        [Fact]
        public void GenerarCsvCorredores_IncluyeDirectivaSepEncabezadosSanitizacionYValoresSistemaIntactos()
        {
            var corredores = new List<CorredorCsvFila>
            {
                new(
                    Dorsal: 101,
                    Nombre: "  =CMD|' /C calc'!A0",
                    Categoria: "+Élite;Pro",
                    Hit: "-Hit \"1\"",
                    Estado: "Activo",
                    TiempoNeto: "--:--:--",
                    HoraLlegada: "--:--:--"),
                new(
                    Dorsal: 102,
                    Nombre: "María José Ñúñez",
                    Categoria: "@General",
                    Hit: "\tHit Mañana",
                    Estado: "DNF",
                    TiempoNeto: "00:45:12",
                    HoraLlegada: "09:15:12")
            };

            string csv = CsvExportHelper.GenerarCsvCorredores(corredores);
            string[] lineas = csv.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);

            Assert.Equal("sep=;", lineas[0]);
            Assert.Equal("Dorsal;Nombre;Categoría;Hit de Salida;Estado;Tiempo Neto;Hora de Llegada", lineas[1]);
            Assert.Equal(
                "101;'  =CMD|' /C calc'!A0;\"'+Élite;Pro\";\"'-Hit \"\"1\"\"\";Activo;--:--:--;--:--:--",
                lineas[2]);
            Assert.Equal(
                "102;María José Ñúñez;'@General;'\tHit Mañana;DNF;00:45:12;09:15:12",
                lineas[3]);
        }

        [Fact]
        public async Task ExportarCorredoresCsvAsync_EscribeArchivoConUtf8BomDirectivaSepYContenidoSanitizado()
        {
            using var env = new TestEnvironmentFixture(configurarStorageGlobal: false);
            string rutaCsv = Path.Combine(env.RootDirectory, "export", "corredores_test.csv");

            var corredores = new List<CorredorCsvFila>
            {
                new(7, "=1+1", "Categoría A", "Hit 1", "Activo", "--:--:--", "--:--:--"),
                new(8, "  -10+2", "Categoría Ñ", "Hit 2", "DNS", "00:10:05", "08:10:05")
            };

            await CsvExportHelper.ExportarCorredoresCsvAsync(rutaCsv, corredores);

            Assert.True(File.Exists(rutaCsv));

            byte[] bytes = await File.ReadAllBytesAsync(rutaCsv);
            Assert.True(bytes.Length >= 3);
            Assert.Equal(0xEF, bytes[0]);
            Assert.Equal(0xBB, bytes[1]);
            Assert.Equal(0xBF, bytes[2]);

            string textoLeido = Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);
            string[] lineas = textoLeido.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);

            Assert.Equal("sep=;", lineas[0]);
            Assert.Equal("Dorsal;Nombre;Categoría;Hit de Salida;Estado;Tiempo Neto;Hora de Llegada", lineas[1]);
            Assert.Equal("7;'=1+1;Categoría A;Hit 1;Activo;--:--:--;--:--:--", lineas[2]);
            Assert.Equal("8;'  -10+2;Categoría Ñ;Hit 2;DNS;00:10:05;08:10:05", lineas[3]);
        }

        [Fact]
        public void GenerarContenidoCsv_ConEncabezadosYFilasGenericas_AplicaSanitizacionYDirectiva()
        {
            var encabezados = new[] { "Col1", "Col;2" };
            var filas = new[]
            {
                new (string? Valor, bool EsValorSistema)[]
                {
                    ("=1+1", false),
                    ("--:--:--", true)
                },
                new (string? Valor, bool EsValorSistema)[]
                {
                    ("Texto \"normal\"", false),
                    (null, false)
                }
            };

            string csv = CsvExportHelper.GenerarContenidoCsv(encabezados, filas);
            string[] lineas = csv.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);

            Assert.Equal("sep=;", lineas[0]);
            Assert.Equal("Col1;\"Col;2\"", lineas[1]);
            Assert.Equal("'=1+1;--:--:--", lineas[2]);
            Assert.Equal("\"Texto \"\"normal\"\"\";", lineas[3]);
        }

        [Fact]
        public async Task ExportarCorredoresCsvAsync_EjecutaLecturaGeneracionYEscrituraFueraDelHiloLlamante()
        {
            using var env = new TestEnvironmentFixture(configurarStorageGlobal: false);
            string rutaCsv = Path.Combine(env.RootDirectory, "corredores_async.csv");

            int hiloLlamante = Thread.CurrentThread.ManagedThreadId;
            int? hiloEnumeracion = null;
            using var callerRetornoInmediatamente = new ManualResetEventSlim(false);
            bool senalRecibidaEnWorker = false;

            IEnumerable<CorredorCsvFila> EnumerarCorredores()
            {
                hiloEnumeracion = Thread.CurrentThread.ManagedThreadId;
                // Si la lectura/generación se ejecutara de forma síncrona en el hilo llamante, esto provocaría timeout
                senalRecibidaEnWorker = callerRetornoInmediatamente.Wait(TimeSpan.FromSeconds(5));

                for (int i = 1; i <= 50; i++)
                {
                    yield return new CorredorCsvFila(
                        i,
                        $"Corredor {i}",
                        "General",
                        "Hit 1",
                        "Activo",
                        "--:--:--",
                        "--:--:--");
                }
            }

            Task tarea = CsvExportHelper.ExportarCorredoresCsvAsync(rutaCsv, EnumerarCorredores());
            callerRetornoInmediatamente.Set();
            await tarea;

            Assert.True(senalRecibidaEnWorker, "La enumeración de datos bloqueó al hilo llamante en lugar de correr asíncronamente.");
            Assert.NotNull(hiloEnumeracion);
            Assert.NotEqual(hiloLlamante, hiloEnumeracion);
            Assert.True(File.Exists(rutaCsv));
        }

        [Fact]
        public async Task ExportarCorredoresCsvAsync_ConTokenCancelado_LanzaOperationCanceledExceptionSinCrearArchivo()
        {
            using var env = new TestEnvironmentFixture(configurarStorageGlobal: false);
            string rutaCsv = Path.Combine(env.RootDirectory, "cancelado.csv");
            using var cts = new CancellationTokenSource();
            cts.Cancel();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                CsvExportHelper.ExportarCorredoresCsvAsync(
                    rutaCsv,
                    new[] { new CorredorCsvFila(1, "Ana", "Cat", "Hit", "Activo", "--:--:--", "--:--:--") },
                    cts.Token));

            Assert.False(File.Exists(rutaCsv));
        }

        [Theory]
        [InlineData("")]
        [InlineData("   ")]
        public async Task EscribirArchivoCsvAsync_ConRutaInvalida_LanzaArgumentException(string rutaInvalida)
        {
            await Assert.ThrowsAsync<ArgumentException>(() =>
                CsvExportHelper.EscribirArchivoCsvAsync(rutaInvalida, "sep=;\r\n"));
        }
    }
}
