using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using CarreraApp.Helpers;
using CarreraApp.Models;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace CarreraApp.Tests
{
    [Collection("IsolatedStorage")]
    public class StorageAndBackupTests
    {
        private static readonly HashSet<string> NombresReservadosWindows = new(StringComparer.OrdinalIgnoreCase)
        {
            "CON", "PRN", "AUX", "NUL",
            "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
            "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"
        };

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData(".")]
        [InlineData("..")]
        [InlineData("...")]
        [InlineData("../..")]
        [InlineData("..\\..")]
        public void SanitizarSegmentoRuta_ConValoresVaciosOPuntosDeRecorrido_RetornaFallbackSeguro(string? entrada)
        {
            string resultado = StorageHelper.SanitizarSegmentoRuta(entrada);

            Assert.False(string.IsNullOrWhiteSpace(resultado));
            Assert.NotEqual(".", resultado);
            Assert.NotEqual("..", resultado);
            Assert.DoesNotContain("..", resultado);
            Assert.DoesNotContain("/", resultado);
            Assert.DoesNotContain("\\", resultado);
        }

        [Theory]
        [InlineData("../../Windows/System32", "Windows_System32")]
        [InlineData("..\\..\\secreto", "secreto")]
        [InlineData("a/../b/./c", "a_b_c")]
        [InlineData("Carrera: <2026> * \"Edicion\" | ?", "Carrera_2026_Edicion")]
        [InlineData("archivo..malicioso", "archivo_malicioso")]
        public void SanitizarSegmentoRuta_ConTraversalYCaracteresInvalidos_NeutralizaEntrada(string entrada, string esperado)
        {
            string resultado = StorageHelper.SanitizarSegmentoRuta(entrada);

            Assert.Equal(esperado, resultado);
            Assert.DoesNotContain("..", resultado);
            Assert.DoesNotContain("/", resultado);
            Assert.DoesNotContain("\\", resultado);
            foreach (char c in Path.GetInvalidFileNameChars())
            {
                Assert.DoesNotContain(c, resultado);
            }
        }

        [Theory]
        [InlineData("CON")]
        [InlineData("PRN")]
        [InlineData("AUX")]
        [InlineData("NUL")]
        [InlineData("COM1")]
        [InlineData("LPT9")]
        [InlineData("con.jpg")]
        public void SanitizarSegmentoRuta_ConNombresReservadosWindows_NeutralizaNombreReservado(string entrada)
        {
            string resultado = StorageHelper.SanitizarSegmentoRuta(entrada);
            string baseSegmento = resultado.Split('.')[0];

            Assert.DoesNotContain(baseSegmento, NombresReservadosWindows);
        }

        [Fact]
        public void SanitizarSegmentoRuta_ConCadenaMuyLarga_AcotaLongitudMaxima()
        {
            string entradaLarga = new string('X', 200) + "...";
            string resultado = StorageHelper.SanitizarSegmentoRuta(entradaLarga, maxLength: 32);

            Assert.InRange(resultado.Length, 1, 32);
            Assert.False(resultado.EndsWith('.'));
            Assert.False(resultado.EndsWith('_'));

            string reservadoCorto = StorageHelper.SanitizarSegmentoRuta("CON_12345", maxLength: 3);
            Assert.InRange(reservadoCorto.Length, 1, 3);
            Assert.DoesNotContain(reservadoCorto, NombresReservadosWindows);
        }

        [Fact]
        public void ConstruirRutaFotoDefinitivaSegura_ConEntradasTraversal_PermaneceDentroDeFotosDefinitivas()
        {
            using var env = new TestEnvironmentFixture();
            string baseDefinitivas = Path.GetFullPath(env.FotosDefinitivasDir)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;

            string ruta = StorageHelper.ConstruirRutaFotoDefinitivaSegura(
                nombreCarrera: "../../escape/Windows",
                nombreCategoria: "..\\..\\System32\\CON",
                nombreCorredor: "../Juan:Perez*?",
                dorsal: 42,
                tiempoParaArchivo: "00:12:34");

            string rutaCompleta = Path.GetFullPath(ruta);
            Assert.StartsWith(baseDefinitivas, rutaCompleta, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("..", rutaCompleta);
            Assert.True(rutaCompleta.Length < 240, $"La ruta '{rutaCompleta}' tiene {rutaCompleta.Length} caracteres (>= 240).");
            Assert.EndsWith(".jpg", rutaCompleta, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("_42_00-12-34.jpg", Path.GetFileName(rutaCompleta), StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void ConstruirRutaFotoDefinitivaSegura_ConNombresExtremadamenteLargos_AcotaRutaTotalMenorA240InclusoEnColision()
        {
            using var env = new TestEnvironmentFixture();
            string nombreLargo = new string('A', 250);

            string ruta1 = StorageHelper.ConstruirRutaFotoDefinitivaSegura(
                nombreCarrera: nombreLargo,
                nombreCategoria: new string('B', 250),
                nombreCorredor: new string('C', 250),
                dorsal: 1234,
                tiempoParaArchivo: "01-45-59");

            Assert.True(ruta1.Length < 240, $"Primera ruta excede límite: {ruta1.Length}");
            File.WriteAllText(ruta1, "foto-1");

            string ruta2 = StorageHelper.ConstruirRutaFotoDefinitivaSegura(
                nombreCarrera: nombreLargo,
                nombreCategoria: new string('B', 250),
                nombreCorredor: new string('C', 250),
                dorsal: 1234,
                tiempoParaArchivo: "01-45-59");

            Assert.NotEqual(ruta1, ruta2);
            Assert.True(ruta2.Length < 240, $"Segunda ruta en colisión excede límite: {ruta2.Length}");
        }

        [Fact]
        public void ConstruirRutaFotoDefinitivaSegura_ConDirectorioRaizProfundo_RespetaLimiteMenorA240()
        {
            using var env = new TestEnvironmentFixture();
            string subCarpeta = Path.Combine(env.RootDirectory, new string('R', 40), new string('S', 40));
            string deepStorage = Path.Combine(subCarpeta, "Storage");
            StorageHelper.ConfigurarRutasParaPruebas(env.DbPath, deepStorage);

            string ruta = StorageHelper.ConstruirRutaFotoDefinitivaSegura(
                nombreCarrera: "CarreraMuyLargaDePrueba2026",
                nombreCategoria: "CategoriaEliteMasculina",
                nombreCorredor: "CorredorConNombreExtremadamenteLargo",
                dorsal: 99,
                tiempoParaArchivo: "00-55-12");

            Assert.True(ruta.Length < 240, $"Ruta con raíz profunda excede límite: {ruta.Length}");
            Assert.True(File.Exists(ruta) || Directory.Exists(Path.GetDirectoryName(ruta)));
        }

        [Fact]
        public void ConstruirRutaFotoDefinitivaSegura_CuandoArchivoExiste_ResuelveColisionesSinSobreescribir()
        {
            using var env = new TestEnvironmentFixture();

            string ruta1 = StorageHelper.ConstruirRutaFotoDefinitivaSegura("Rally 1", "Libre", "Ana Maria", 7, "00-10-00");
            File.WriteAllText(ruta1, "contenido-original");

            string ruta2 = StorageHelper.ConstruirRutaFotoDefinitivaSegura("Rally 1", "Libre", "Ana Maria", 7, "00-10-00");
            File.WriteAllText(ruta2, "contenido-segundo");

            string ruta3 = StorageHelper.ConstruirRutaFotoDefinitivaSegura("Rally 1", "Libre", "Ana Maria", 7, "00-10-00");

            Assert.NotEqual(ruta1, ruta2);
            Assert.NotEqual(ruta1, ruta3);
            Assert.NotEqual(ruta2, ruta3);
            Assert.EndsWith("_1.jpg", ruta2, StringComparison.OrdinalIgnoreCase);
            Assert.EndsWith("_2.jpg", ruta3, StringComparison.OrdinalIgnoreCase);
            Assert.Equal("contenido-original", File.ReadAllText(ruta1));
            Assert.Equal("contenido-segundo", File.ReadAllText(ruta2));
        }

        [Theory]
        [InlineData(".png", ".png")]
        [InlineData("WEBP", ".webp")]
        [InlineData(".jpeg", ".jpeg")]
        [InlineData("../evil.exe", ".jpg")]
        [InlineData("", ".jpg")]
        public void ConstruirRutaFotoDefinitivaSegura_NormalizaExtensionImagenSegura(string extensionEntrada, string extensionEsperada)
        {
            using var env = new TestEnvironmentFixture();

            string ruta = StorageHelper.ConstruirRutaFotoDefinitivaSegura("Carrera", "Cat", "Luis", 5, "00-05-00", extensionEntrada);
            Assert.EndsWith(extensionEsperada, ruta, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void LimpiarFotosTempHuerfanas_EliminaSoloHuerfanosSoportadosPreservaGitkeepActivosYFotosDefinitivas()
        {
            using var env = new TestEnvironmentFixture();
            string tempDir = StorageHelper.ObtenerCarpetaFotosTemp();
            string defDir = StorageHelper.ObtenerCarpetaFotosDefinitivas();

            string gitkeepTemp = Path.Combine(tempDir, ".gitkeep");
            string huerfanaJpg = Path.Combine(tempDir, "huerfana1.jpg");
            string huerfanaJpeg = Path.Combine(tempDir, "huerfana2.JPEG");
            string huerfanaPng = Path.Combine(tempDir, "huerfana3.png");
            string huerfanaWebp = Path.Combine(tempDir, "huerfana4.webp");
            string activaPorNombre = Path.Combine(tempDir, "activa_nombre.jpg");
            string activaPorRuta = Path.Combine(tempDir, "activa_ruta.webp");
            string archivoNoImagen = Path.Combine(tempDir, "registro.txt");

            string subDirDef = Path.Combine(defDir, "Rally_1", "Libre");
            Directory.CreateDirectory(subDirDef);
            string definitiva1 = Path.Combine(subDirDef, "corredor_1_00-10-00.jpg");
            string definitiva2 = Path.Combine(defDir, "corredor_2.png");

            foreach (var archivo in new[] { gitkeepTemp, huerfanaJpg, huerfanaJpeg, huerfanaPng, huerfanaWebp, activaPorNombre, activaPorRuta, archivoNoImagen, definitiva1, definitiva2 })
            {
                File.WriteAllText(archivo, "dato");
            }

            int eliminados = StorageHelper.LimpiarFotosTempHuerfanas(new[]
            {
                "activa_nombre.jpg",
                activaPorRuta,
                Path.Combine(defDir, "huerfana1.jpg") // Ruta en otro directorio no debe proteger huerfana1.jpg en Temp
            });

            Assert.Equal(4, eliminados);
            Assert.False(File.Exists(huerfanaJpg));
            Assert.False(File.Exists(huerfanaJpeg));
            Assert.False(File.Exists(huerfanaPng));
            Assert.False(File.Exists(huerfanaWebp));

            Assert.True(File.Exists(gitkeepTemp));
            Assert.True(File.Exists(activaPorNombre));
            Assert.True(File.Exists(activaPorRuta));
            Assert.True(File.Exists(archivoNoImagen));
            Assert.True(File.Exists(definitiva1));
            Assert.True(File.Exists(definitiva2));
        }

        [Fact]
        public void LimpiarFotosTempHuerfanas_SinParametroActivos_EliminaTodasLasImagenesTempPreservandoGitkeep()
        {
            using var env = new TestEnvironmentFixture();
            string tempDir = StorageHelper.ObtenerCarpetaFotosTemp();

            string gitkeep = Path.Combine(tempDir, ".gitkeep");
            string temp1 = Path.Combine(tempDir, "foto1.jpg");
            string temp2 = Path.Combine(tempDir, "foto2.png");

            File.WriteAllText(gitkeep, string.Empty);
            File.WriteAllText(temp1, "img1");
            File.WriteAllText(temp2, "img2");

            int eliminados = StorageHelper.LimpiarFotosTempHuerfanas();

            Assert.Equal(2, eliminados);
            Assert.True(File.Exists(gitkeep));
            Assert.False(File.Exists(temp1));
            Assert.False(File.Exists(temp2));
        }

        [Fact]
        public async Task RespaldarBaseDatosConsistentementeAsync_SinBasePrevia_OmiteSinErrorYSinCrearArchivos()
        {
            using var env = new TestEnvironmentFixture();

            var resultado = await DatabaseMaintenanceHelper.RespaldarBaseDatosConsistentementeAsync();

            Assert.True(resultado.Exito);
            Assert.True(resultado.OmitidoPorInexistencia);
            Assert.Null(resultado.RutaRespaldo);
            Assert.Null(resultado.Error);
            Assert.False(File.Exists(env.DbPath));
            Assert.False(Directory.Exists(env.BackupsDir) && Directory.GetFiles(env.BackupsDir, "*.db").Length > 0);
        }

        [Fact]
        public async Task RespaldarBaseDatosConsistentementeAsync_EnModoWalSinCheckpoint_GeneraCopiaConsistenteConTodosLosDatos()
        {
            using var env = new TestEnvironmentFixture();
            await DatabaseMaintenanceHelper.InicializarYMigrarBaseDatosAsync(env.CrearFabricaContexto());

            using (var db = env.CrearContexto())
            {
                db.Carreras.Add(new Carrera { Nombre = "Carrera Base", Fecha = DateTime.Today });
                await db.SaveChangesAsync();
            }

            // Mantener conexión abierta en WAL con autocheckpoint deshabilitado para que la segunda escritura permanezca en -wal
            var csb = new SqliteConnectionStringBuilder
            {
                DataSource = env.DbPath,
                Mode = SqliteOpenMode.ReadWrite,
                Pooling = false
            };

            await using var writerConn = new SqliteConnection(csb.ToString());
            await writerConn.OpenAsync();
            await using (var pragmaCmd = writerConn.CreateCommand())
            {
                pragmaCmd.CommandText = "PRAGMA wal_autocheckpoint=0;";
                await pragmaCmd.ExecuteNonQueryAsync();
            }

            await using (var insertCmd = writerConn.CreateCommand())
            {
                insertCmd.CommandText = "INSERT INTO Carreras (Nombre, Fecha) VALUES ('Carrera En WAL', '2026-11-14 09:00:00');";
                await insertCmd.ExecuteNonQueryAsync();
            }

            string walPath = env.DbPath + "-wal";
            Assert.True(File.Exists(walPath));
            Assert.True(new FileInfo(walPath).Length > 0);

            var resultado = await DatabaseMaintenanceHelper.RespaldarBaseDatosConsistentementeAsync();

            Assert.True(resultado.Exito, resultado.Mensaje);
            Assert.False(resultado.OmitidoPorInexistencia);
            Assert.NotNull(resultado.RutaRespaldo);
            Assert.True(File.Exists(resultado.RutaRespaldo));
            Assert.Equal(Path.GetFullPath(env.BackupsDir), Path.GetFullPath(Path.GetDirectoryName(resultado.RutaRespaldo)!));

            // Verificar integridad y datos completos en el archivo respaldado
            var backupCsb = new SqliteConnectionStringBuilder
            {
                DataSource = resultado.RutaRespaldo,
                Mode = SqliteOpenMode.ReadOnly,
                Pooling = false
            };

            await using var backupConn = new SqliteConnection(backupCsb.ToString());
            await backupConn.OpenAsync();

            await using (var integrityCmd = backupConn.CreateCommand())
            {
                integrityCmd.CommandText = "PRAGMA integrity_check;";
                string? integrity = (await integrityCmd.ExecuteScalarAsync())?.ToString();
                Assert.Equal("ok", integrity, ignoreCase: true);
            }

            var nombres = new List<string>();
            await using (var queryCmd = backupConn.CreateCommand())
            {
                queryCmd.CommandText = "SELECT Nombre FROM Carreras ORDER BY ID_Carrera;";
                await using var reader = await queryCmd.ExecuteReaderAsync();
                while (await reader.ReadAsync())
                {
                    nombres.Add(reader.GetString(0));
                }
            }

            Assert.Equal(new[] { "Carrera Base", "Carrera En WAL" }, nombres);
        }

        [Fact]
        public async Task RespaldarBaseDatosConsistentementeAsync_SuperandoDiezRespaldos_RotaYEliminaLosMasAntiguos()
        {
            using var env = new TestEnvironmentFixture();
            await DatabaseMaintenanceHelper.InicializarYMigrarBaseDatosAsync(env.CrearFabricaContexto());

            string backupsDir = StorageHelper.ObtenerCarpetaRespaldos();
            string gitkeep = Path.Combine(backupsDir, ".gitkeep");
            File.WriteAllText(gitkeep, string.Empty);

            // Sembrar 3 respaldos antiguos
            var antiguos = new List<string>();
            for (int i = 1; i <= 3; i++)
            {
                string rutaAntigua = Path.Combine(backupsDir, $"antiguo_{i}.db");
                File.WriteAllText(rutaAntigua, $"old-{i}");
                File.SetLastWriteTimeUtc(rutaAntigua, DateTime.UtcNow.AddHours(-10 + i));
                antiguos.Add(rutaAntigua);
            }

            // Generar 12 respaldos consistentes consecutivos (total 15 -> deben quedar máximo 10)
            var creados = new List<string>();
            for (int i = 1; i <= 12; i++)
            {
                var res = await DatabaseMaintenanceHelper.RespaldarBaseDatosConsistentementeAsync();
                Assert.True(res.Exito, res.Mensaje);
                Assert.NotNull(res.RutaRespaldo);
                creados.Add(res.RutaRespaldo!);
            }

            string[] archivosRestantes = Directory.GetFiles(backupsDir, "*.db");
            Assert.Equal(10, archivosRestantes.Length);
            Assert.True(File.Exists(gitkeep));

            foreach (string rutaAntigua in antiguos)
            {
                Assert.False(File.Exists(rutaAntigua), $"El respaldo antiguo {rutaAntigua} debió eliminarse.");
            }

            // Los 2 primeros respaldos nuevos también debieron rotarse, y los últimos 10 permanecer
            Assert.False(File.Exists(creados[0]));
            Assert.False(File.Exists(creados[1]));
            for (int i = 2; i < creados.Count; i++)
            {
                Assert.True(File.Exists(creados[i]), $"El respaldo reciente {creados[i]} debió conservarse.");
            }
        }

        [Fact]
        public async Task RespaldarBaseDatosConsistentementeAsync_CuandoFallaRespaldo_RetornaErrorEstructuradoSinLanzarExcepcion()
        {
            using var env = new TestEnvironmentFixture();
            await DatabaseMaintenanceHelper.InicializarYMigrarBaseDatosAsync(env.CrearFabricaContexto());

            // Escenario 1: carpeta de destino bloqueada por un archivo existente con el mismo nombre
            string rutaBloqueadaComoArchivo = Path.Combine(env.RootDirectory, "archivo_bloqueante");
            File.WriteAllText(rutaBloqueadaComoArchivo, "bloqueo");

            var resultadoCarpetaInvalida = await DatabaseMaintenanceHelper.RespaldarBaseDatosConsistentementeAsync(
                dbPath: env.DbPath,
                carpetaRespaldos: rutaBloqueadaComoArchivo);

            Assert.False(resultadoCarpetaInvalida.Exito);
            Assert.NotNull(resultadoCarpetaInvalida.Error);
            Assert.False(string.IsNullOrWhiteSpace(resultadoCarpetaInvalida.Mensaje));
            Assert.Null(resultadoCarpetaInvalida.RutaRespaldo);

            // Escenario 2: archivo origen corrupto (no es SQLite válido)
            string bdCorrupta = Path.Combine(env.RootDirectory, "corrupta.db");
            File.WriteAllText(bdCorrupta, "contenido corrupto que no es SQLite");

            var resultadoCorrupto = await DatabaseMaintenanceHelper.RespaldarBaseDatosConsistentementeAsync(
                dbPath: bdCorrupta,
                carpetaRespaldos: env.BackupsDir);

            Assert.False(resultadoCorrupto.Exito);
            Assert.NotNull(resultadoCorrupto.Error);
            Assert.False(string.IsNullOrWhiteSpace(resultadoCorrupto.Mensaje));
            Assert.Null(resultadoCorrupto.RutaRespaldo);
            Assert.Empty(Directory.GetFiles(env.BackupsDir));
        }
    }
}
