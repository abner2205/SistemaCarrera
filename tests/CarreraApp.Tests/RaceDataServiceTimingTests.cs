using System;
using System.Collections.Generic;
using System.Data.Common;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CarreraApp.Data;
using CarreraApp.Helpers;
using CarreraApp.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace CarreraApp.Tests
{
    [Collection("IsolatedStorage")]
    public class RaceDataServiceTimingTests
    {
        [Fact]
        public async Task AsignarTiempoAsync_SinAplicacionWpf_YConFabricaDeContexto_OperaDeFormaAutonoma()
        {
            using var env = new TestEnvironmentFixture();
            await DatabaseMaintenanceHelper.InicializarYMigrarBaseDatosAsync(env.CrearFabricaContexto());

            Assert.Null(System.Windows.Application.Current);

            DateTime inicioHit = new(2026, 11, 13, 8, 0, 0);
            DateTime horaLlegada = inicioHit.AddMinutes(12).AddSeconds(34);
            var semilla = await CrearEscenarioBaseAsync(env, inicioHit, cantidadCorredores: 1);

            string rutaTemp = CrearArchivoTemporalImagen(env, "autonomo.jpg", new byte[] { 0xFF, 0xD8, 0xFF, 0xE0, 1, 2, 3 });

            var service = new RaceDataService(env.CrearFabricaContexto());
            AsignacionResultado resultado = await service.AsignarTiempoAsync(
                semilla.Carrera.ID_Carrera,
                semilla.Carrera.Nombre,
                dorsal: 1,
                horaLlegada,
                rutaTemp);

            Assert.True(resultado.Exito);
            Assert.Equal(200, resultado.StatusCode);
            Assert.False(resultado.EsConflicto);
            Assert.Equal("00:12:34", resultado.TiempoFormateado);
            Assert.True(resultado.HitFinalizado);
            Assert.Equal(semilla.Hit.Nombre_Hit, resultado.NombreHitFinalizado);
            Assert.False(string.IsNullOrWhiteSpace(resultado.RutaFotoDefinitiva));
            Assert.True(File.Exists(resultado.RutaFotoDefinitiva));
            Assert.False(File.Exists(rutaTemp));

            using var db = env.CrearContexto();
            Tiempo? tiempoDb = await db.Tiempos.SingleOrDefaultAsync(t => t.ID_Corredor == semilla.Corredores[0].ID_Corredor);
            Assert.NotNull(tiempoDb);
            Assert.Equal(horaLlegada, tiempoDb!.Hora_Llegada);
            Assert.Equal(horaLlegada - inicioHit, tiempoDb.Tiempo_Neto);
            Assert.Equal(resultado.RutaFotoDefinitiva, tiempoDb.Ruta_Foto);
        }

        [Fact]
        public async Task AsignarTiempoAsync_ConConstructorPorDefecto_UsaStorageHelperAislado()
        {
            using var env = new TestEnvironmentFixture();
            await DatabaseMaintenanceHelper.InicializarYMigrarBaseDatosAsync();

            DateTime inicioHit = new(2026, 11, 13, 9, 0, 0);
            var semilla = await CrearEscenarioBaseAsync(env, inicioHit, cantidadCorredores: 1);
            string rutaTemp = CrearArchivoTemporalImagen(env, "default_ctor.jpg");

            var service = new RaceDataService();
            AsignacionResultado resultado = await service.AsignarTiempoAsync(
                semilla.Carrera.ID_Carrera,
                semilla.Carrera.Nombre,
                dorsal: 1,
                inicioHit.AddSeconds(45),
                rutaTemp);

            Assert.True(resultado.Exito);
            Assert.Equal(200, resultado.StatusCode);

            List<Carrera> carreras = await service.ObtenerCarrerasAsync();
            Assert.Single(carreras);
        }

        [Theory]
        [InlineData(1, 0, "foto.jpg")]
        [InlineData(1, -10, "foto.jpg")]
        [InlineData(0, 1, "foto.jpg")]
        [InlineData(-1, 1, "foto.jpg")]
        [InlineData(1, 1, "")]
        [InlineData(1, 1, "   ")]
        public async Task AsignarTiempoAsync_ConDatosEntradaInvalidos_Retorna400YConservaTemporal(
            int idCarrera,
            int dorsal,
            string nombreTemp)
        {
            using var env = new TestEnvironmentFixture();
            await DatabaseMaintenanceHelper.InicializarYMigrarBaseDatosAsync(env.CrearFabricaContexto());

            string rutaTempReal = CrearArchivoTemporalImagen(env, "entrada_invalida.jpg");
            string rutaParametro = string.IsNullOrWhiteSpace(nombreTemp)
                ? nombreTemp
                : rutaTempReal;

            var service = new RaceDataService(env.CrearFabricaContexto());
            AsignacionResultado resultado = await service.AsignarTiempoAsync(
                idCarrera,
                "Carrera Test",
                dorsal,
                DateTime.Now,
                rutaParametro);

            Assert.False(resultado.Exito);
            Assert.Equal(400, resultado.StatusCode);
            Assert.False(resultado.EsConflicto);
            Assert.True(File.Exists(rutaTempReal));

            using var db = env.CrearContexto();
            Assert.Equal(0, await db.Tiempos.CountAsync());
        }

        [Fact]
        public async Task AsignarTiempoAsync_ConDorsalInexistenteOArchivoInexistente_Retorna404YNoPersiste()
        {
            using var env = new TestEnvironmentFixture();
            await DatabaseMaintenanceHelper.InicializarYMigrarBaseDatosAsync(env.CrearFabricaContexto());

            DateTime inicioHit = new(2026, 11, 13, 8, 0, 0);
            var semilla = await CrearEscenarioBaseAsync(env, inicioHit, cantidadCorredores: 1);
            string rutaTemp = CrearArchivoTemporalImagen(env, "no_encontrado.jpg");

            var service = new RaceDataService(env.CrearFabricaContexto());

            // 1. Dorsal no registrado
            AsignacionResultado resDorsal = await service.AsignarTiempoAsync(
                semilla.Carrera.ID_Carrera,
                semilla.Carrera.Nombre,
                dorsal: 999,
                inicioHit.AddSeconds(30),
                rutaTemp);

            Assert.False(resDorsal.Exito);
            Assert.Equal(404, resDorsal.StatusCode);
            Assert.True(File.Exists(rutaTemp));

            // 2. Archivo temporal inexistente
            string rutaInexistente = Path.Combine(env.FotosTempDir, "inexistente.jpg");
            AsignacionResultado resArchivo = await service.AsignarTiempoAsync(
                semilla.Carrera.ID_Carrera,
                semilla.Carrera.Nombre,
                dorsal: 1,
                inicioHit.AddSeconds(30),
                rutaInexistente);

            Assert.False(resArchivo.Exito);
            Assert.Equal(404, resArchivo.StatusCode);

            using var db = env.CrearContexto();
            Assert.Equal(0, await db.Tiempos.CountAsync());
        }

        [Fact]
        public async Task AsignarTiempoAsync_ConReglasDeNegocioInvalidas_Retorna422YConservaTemporal()
        {
            using var env = new TestEnvironmentFixture();
            await DatabaseMaintenanceHelper.InicializarYMigrarBaseDatosAsync(env.CrearFabricaContexto());

            DateTime inicioHit = new(2026, 11, 13, 10, 0, 0);
            var semilla = await CrearEscenarioBaseAsync(env, inicioHit: null, cantidadCorredores: 4);

            // Configurar estados y segundo hit cerrado
            using (var db = env.CrearContexto())
            {
                var c2 = await db.Corredores.FindAsync(semilla.Corredores[1].ID_Corredor);
                c2!.Estado = EstadoCorredor.DNS;

                var c3 = await db.Corredores.FindAsync(semilla.Corredores[2].ID_Corredor);
                c3!.Estado = EstadoCorredor.DNF;

                var hitCerrado = new Hit
                {
                    Nombre_Hit = "Hit Cerrado",
                    ID_Carrera = semilla.Carrera.ID_Carrera,
                    Hora_Inicio = inicioHit.AddHours(-1),
                    Hora_Fin = inicioHit.AddMinutes(-30)
                };
                db.Hits.Add(hitCerrado);
                await db.SaveChangesAsync();

                var c4 = await db.Corredores.FindAsync(semilla.Corredores[3].ID_Corredor);
                c4!.ID_Hit = hitCerrado.ID_Hit;
                await db.SaveChangesAsync();
            }

            string rutaTemp = CrearArchivoTemporalImagen(env, "validacion_422.jpg");
            var service = new RaceDataService(env.CrearFabricaContexto());

            // 1. Hit no iniciado
            AsignacionResultado resNoIniciado = await service.AsignarTiempoAsync(
                semilla.Carrera.ID_Carrera,
                semilla.Carrera.Nombre,
                dorsal: 1,
                inicioHit.AddMinutes(5),
                rutaTemp);
            Assert.False(resNoIniciado.Exito);
            Assert.Equal(422, resNoIniciado.StatusCode);
            Assert.True(File.Exists(rutaTemp));

            // Iniciar hit principal para las siguientes validaciones
            using (var db = env.CrearContexto())
            {
                var hit = await db.Hits.FindAsync(semilla.Hit.ID_Hit);
                hit!.Hora_Inicio = inicioHit;
                await db.SaveChangesAsync();
            }

            // 2. Corredor DNS
            AsignacionResultado resDns = await service.AsignarTiempoAsync(
                semilla.Carrera.ID_Carrera,
                semilla.Carrera.Nombre,
                dorsal: 2,
                inicioHit.AddMinutes(5),
                rutaTemp);
            Assert.False(resDns.Exito);
            Assert.Equal(422, resDns.StatusCode);
            Assert.True(File.Exists(rutaTemp));

            // 3. Corredor DNF
            AsignacionResultado resDnf = await service.AsignarTiempoAsync(
                semilla.Carrera.ID_Carrera,
                semilla.Carrera.Nombre,
                dorsal: 3,
                inicioHit.AddMinutes(5),
                rutaTemp);
            Assert.False(resDnf.Exito);
            Assert.Equal(422, resDnf.StatusCode);
            Assert.True(File.Exists(rutaTemp));

            // 4. Hit ya finalizado
            AsignacionResultado resHitCerrado = await service.AsignarTiempoAsync(
                semilla.Carrera.ID_Carrera,
                semilla.Carrera.Nombre,
                dorsal: 4,
                inicioHit.AddMinutes(5),
                rutaTemp);
            Assert.False(resHitCerrado.Exito);
            Assert.Equal(422, resHitCerrado.StatusCode);
            Assert.True(File.Exists(rutaTemp));

            // 5. Tiempo neto negativo (horaLlegada < Hora_Inicio)
            AsignacionResultado resNegativoMs = await service.AsignarTiempoAsync(
                semilla.Carrera.ID_Carrera,
                semilla.Carrera.Nombre,
                dorsal: 1,
                inicioHit.AddTicks(-1),
                rutaTemp);
            Assert.False(resNegativoMs.Exito);
            Assert.Equal(422, resNegativoMs.StatusCode);
            Assert.Contains("negativo", resNegativoMs.Mensaje, StringComparison.OrdinalIgnoreCase);
            Assert.True(File.Exists(rutaTemp));

            AsignacionResultado resNegativoMin = await service.AsignarTiempoAsync(
                semilla.Carrera.ID_Carrera,
                semilla.Carrera.Nombre,
                dorsal: 1,
                inicioHit.AddMinutes(-2),
                rutaTemp);
            Assert.False(resNegativoMin.Exito);
            Assert.Equal(422, resNegativoMin.StatusCode);
            Assert.True(File.Exists(rutaTemp));

            using (var db = env.CrearContexto())
            {
                Assert.Equal(0, await db.Tiempos.CountAsync());
            }
        }

        [Fact]
        public async Task AsignarTiempoAsync_PreservaHoraLlegadaOriginalYPermiteTiempoNetoCero()
        {
            using var env = new TestEnvironmentFixture();
            await DatabaseMaintenanceHelper.InicializarYMigrarBaseDatosAsync(env.CrearFabricaContexto());

            DateTime inicioHit = new(2026, 11, 13, 8, 30, 0, 250);
            var semilla = await CrearEscenarioBaseAsync(env, inicioHit, cantidadCorredores: 2);

            string temp1 = CrearArchivoTemporalImagen(env, "cero.jpg");
            string temp2 = CrearArchivoTemporalImagen(env, "historico.jpg");

            DateTime llegadaExactaInicio = inicioHit;
            DateTime llegadaHistorica = inicioHit.AddHours(1).AddMinutes(2).AddSeconds(3).AddMilliseconds(456);

            await Task.Delay(50);

            var service = new RaceDataService(env.CrearFabricaContexto());
            AsignacionResultado res1 = await service.AsignarTiempoAsync(
                semilla.Carrera.ID_Carrera,
                semilla.Carrera.Nombre,
                dorsal: 1,
                llegadaExactaInicio,
                temp1);

            AsignacionResultado res2 = await service.AsignarTiempoAsync(
                semilla.Carrera.ID_Carrera,
                semilla.Carrera.Nombre,
                dorsal: 2,
                llegadaHistorica,
                temp2);

            Assert.True(res1.Exito);
            Assert.Equal("00:00:00", res1.TiempoFormateado);
            Assert.True(res2.Exito);
            Assert.Equal("01:02:03", res2.TiempoFormateado);

            using var db = env.CrearContexto();
            Tiempo t1 = await db.Tiempos.SingleAsync(t => t.ID_Corredor == semilla.Corredores[0].ID_Corredor);
            Tiempo t2 = await db.Tiempos.SingleAsync(t => t.ID_Corredor == semilla.Corredores[1].ID_Corredor);

            Assert.Equal(llegadaExactaInicio, t1.Hora_Llegada);
            Assert.Equal(TimeSpan.Zero, t1.Tiempo_Neto);
            Assert.Equal(llegadaHistorica, t2.Hora_Llegada);
            Assert.Equal(llegadaHistorica - inicioHit, t2.Tiempo_Neto);
        }

        [Theory]
        [InlineData(".jpg", ".jpg")]
        [InlineData(".jpeg", ".jpeg")]
        [InlineData(".png", ".png")]
        [InlineData(".PNG", ".png")]
        [InlineData(".webp", ".webp")]
        public async Task AsignarTiempoAsync_RespetaExtensionYSanitizaRutaDefinitiva(
            string extensionEntrada,
            string extensionEsperada)
        {
            using var env = new TestEnvironmentFixture();
            await DatabaseMaintenanceHelper.InicializarYMigrarBaseDatosAsync(env.CrearFabricaContexto());

            DateTime inicioHit = new(2026, 11, 13, 7, 0, 0);
            var semilla = await CrearEscenarioBaseAsync(
                env,
                inicioHit,
                cantidadCorredores: 1,
                nombreCarrera: "../../Carrera: <2026> *?",
                nombreCategoria: "../Elite\\Sub",
                nombreCorredorPrefix: "CON/Juan..*");

            byte[] contenidoEsperado = { 0x89, 0x50, 0x4E, 0x47, 10, 20, 30, 40 };
            string rutaTemp = CrearArchivoTemporalImagen(env, $"captura{extensionEntrada}", contenidoEsperado);

            var service = new RaceDataService(env.CrearFabricaContexto());
            AsignacionResultado resultado = await service.AsignarTiempoAsync(
                semilla.Carrera.ID_Carrera,
                semilla.Carrera.Nombre,
                dorsal: 1,
                inicioHit.AddMinutes(3),
                rutaTemp);

            Assert.True(resultado.Exito);
            Assert.NotNull(resultado.RutaFotoDefinitiva);
            string baseDefinitivas = Path.GetFullPath(env.FotosDefinitivasDir)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            Assert.StartsWith(baseDefinitivas, Path.GetFullPath(resultado.RutaFotoDefinitiva!), StringComparison.OrdinalIgnoreCase);
            Assert.InRange(resultado.RutaFotoDefinitiva!.Length, 1, 239);
            Assert.Equal(extensionEsperada, Path.GetExtension(resultado.RutaFotoDefinitiva));
            Assert.Equal(contenidoEsperado, await File.ReadAllBytesAsync(resultado.RutaFotoDefinitiva));
            Assert.False(File.Exists(rutaTemp));
        }

        [Fact]
        public async Task AsignarTiempoAsync_CopiaDefinitivaAntesDeCommitYEliminaTemporalSoloDespuesDeCommit()
        {
            using var env = new TestEnvironmentFixture();
            await DatabaseMaintenanceHelper.InicializarYMigrarBaseDatosAsync(env.CrearFabricaContexto());

            DateTime inicioHit = new(2026, 11, 13, 8, 0, 0);
            var semilla = await CrearEscenarioBaseAsync(env, inicioHit, cantidadCorredores: 1);
            string rutaTemp = CrearArchivoTemporalImagen(env, "orden_commit.webp", new byte[] { 1, 2, 3, 4, 5 });

            var observador = new ObservadorTransaccionInterceptor(rutaTemp, env.FotosDefinitivasDir);
            var optionsObservadas = new DbContextOptionsBuilder<ApplicationDbContext>();
            ApplicationDbContext.ConfigurarOpcionesSqlite(optionsObservadas, env.DbPath);
            optionsObservadas.AddInterceptors(observador);

            var service = new RaceDataService(() => new ApplicationDbContext(optionsObservadas.Options));
            AsignacionResultado resultado = await service.AsignarTiempoAsync(
                semilla.Carrera.ID_Carrera,
                semilla.Carrera.Nombre,
                dorsal: 1,
                inicioHit.AddMinutes(1),
                rutaTemp);

            Assert.True(resultado.Exito);
            Assert.True(observador.SeInicioTransaccion);
            Assert.True(observador.SeEjecutoCommit);
            Assert.True(observador.TemporalExistiaAntesDeCommit, "El archivo temporal no debe eliminarse antes de CommitAsync.");
            Assert.True(observador.DefinitivoExistiaAntesDeCommit, "La copia definitiva debe completarse antes de CommitAsync.");
            Assert.False(File.Exists(rutaTemp), "El archivo temporal debe eliminarse después de CommitAsync.");
        }

        [Fact]
        public async Task AsignarTiempoAsync_AnteFalloEnSaveChanges_RevierteBDYArchivoDefinitivoConservandoTemporal()
        {
            using var env = new TestEnvironmentFixture();
            await DatabaseMaintenanceHelper.InicializarYMigrarBaseDatosAsync(env.CrearFabricaContexto());

            DateTime inicioHit = new(2026, 11, 13, 8, 0, 0);
            var semilla = await CrearEscenarioBaseAsync(env, inicioHit, cantidadCorredores: 1);
            byte[] bytesOriginales = { 0xFF, 0xD8, 0xFF, 0xE0, 9, 8, 7, 6 };
            string rutaTemp = CrearArchivoTemporalImagen(env, "fallo_save.jpg", bytesOriginales);

            var fallaSave = new FallaSaveChangesInterceptor();
            var optionsFalla = new DbContextOptionsBuilder<ApplicationDbContext>();
            ApplicationDbContext.ConfigurarOpcionesSqlite(optionsFalla, env.DbPath);
            optionsFalla.AddInterceptors(fallaSave);

            var serviceConFallo = new RaceDataService(() => new ApplicationDbContext(optionsFalla.Options));
            AsignacionResultado resFallo = await serviceConFallo.AsignarTiempoAsync(
                semilla.Carrera.ID_Carrera,
                semilla.Carrera.Nombre,
                dorsal: 1,
                inicioHit.AddMinutes(4),
                rutaTemp);

            Assert.False(resFallo.Exito);
            Assert.Equal(500, resFallo.StatusCode);

            // El temporal debe seguir intacto para reintento
            Assert.True(File.Exists(rutaTemp));
            Assert.Equal(bytesOriginales, await File.ReadAllBytesAsync(rutaTemp));

            // No debe quedar archivo definitivo parcial ni registro en BD
            string[] definitivos = Directory.GetFiles(env.FotosDefinitivasDir, "*", SearchOption.AllDirectories);
            Assert.Empty(definitivos);

            using (var db = env.CrearContexto())
            {
                Assert.Equal(0, await db.Tiempos.CountAsync());
                Hit? hit = await db.Hits.FindAsync(semilla.Hit.ID_Hit);
                Assert.Null(hit!.Hora_Fin);
            }

            // Reintento limpio con el mismo archivo temporal debe tener éxito
            var serviceLimpio = new RaceDataService(env.CrearFabricaContexto());
            AsignacionResultado resReintento = await serviceLimpio.AsignarTiempoAsync(
                semilla.Carrera.ID_Carrera,
                semilla.Carrera.Nombre,
                dorsal: 1,
                inicioHit.AddMinutes(4),
                rutaTemp);

            Assert.True(resReintento.Exito);
            Assert.Equal(200, resReintento.StatusCode);
            Assert.False(File.Exists(rutaTemp));
        }

        [Fact]
        public async Task AsignarTiempoAsync_AnteFalloEnCommit_RevierteTiemposYCierreHitYArchivoDefinitivoConservandoTemporal()
        {
            using var env = new TestEnvironmentFixture();
            await DatabaseMaintenanceHelper.InicializarYMigrarBaseDatosAsync(env.CrearFabricaContexto());

            DateTime inicioHit = new(2026, 11, 13, 8, 0, 0);
            var semilla = await CrearEscenarioBaseAsync(env, inicioHit, cantidadCorredores: 1);
            byte[] bytesOriginales = { 0xFF, 0xD8, 0xFF, 0xE0, 11, 22, 33, 44 };
            string rutaTemp = CrearArchivoTemporalImagen(env, "fallo_commit.jpg", bytesOriginales);

            var fallaCommit = new FallaCommitInterceptor();
            var optionsFalla = new DbContextOptionsBuilder<ApplicationDbContext>();
            ApplicationDbContext.ConfigurarOpcionesSqlite(optionsFalla, env.DbPath);
            optionsFalla.AddInterceptors(fallaCommit);

            var serviceConFallo = new RaceDataService(() => new ApplicationDbContext(optionsFalla.Options));
            AsignacionResultado resFallo = await serviceConFallo.AsignarTiempoAsync(
                semilla.Carrera.ID_Carrera,
                semilla.Carrera.Nombre,
                dorsal: 1,
                inicioHit.AddMinutes(6),
                rutaTemp);

            Assert.False(resFallo.Exito);
            Assert.Equal(500, resFallo.StatusCode);
            Assert.True(fallaCommit.SeIntentoCommit);

            // Temporal intacto
            Assert.True(File.Exists(rutaTemp));
            Assert.Equal(bytesOriginales, await File.ReadAllBytesAsync(rutaTemp));

            // Archivo definitivo copiado antes de Commit debe haberse eliminado tras rollback
            string[] definitivos = Directory.GetFiles(env.FotosDefinitivasDir, "*", SearchOption.AllDirectories);
            Assert.Empty(definitivos);

            // BD completamente revertida (ni Tiempo ni Hora_Fin del Hit)
            using var db = env.CrearContexto();
            Assert.Equal(0, await db.Tiempos.CountAsync());
            Hit? hit = await db.Hits.FindAsync(semilla.Hit.ID_Hit);
            Assert.Null(hit!.Hora_Fin);
        }

        [Fact]
        public async Task AsignarTiempoAsync_CierreAutomaticoHit_ExcluyeDnsDnfYUsaMaxHoraLlegadaFueraDeOrden()
        {
            using var env = new TestEnvironmentFixture();
            await DatabaseMaintenanceHelper.InicializarYMigrarBaseDatosAsync(env.CrearFabricaContexto());

            DateTime inicioHit = new(2026, 11, 13, 8, 0, 0);
            var semilla = await CrearEscenarioBaseAsync(env, inicioHit, cantidadCorredores: 4);

            using (var db = env.CrearContexto())
            {
                var c3 = await db.Corredores.FindAsync(semilla.Corredores[2].ID_Corredor);
                var c4 = await db.Corredores.FindAsync(semilla.Corredores[3].ID_Corredor);
                c3!.Estado = EstadoCorredor.DNS;
                c4!.Estado = EstadoCorredor.DNF;
                await db.SaveChangesAsync();
            }

            string tempLento = CrearArchivoTemporalImagen(env, "lento.jpg");
            string tempRapido = CrearArchivoTemporalImagen(env, "rapido.jpg");

            DateTime horaLento = inicioHit.AddMinutes(15);
            DateTime horaRapido = inicioHit.AddMinutes(9);

            var service = new RaceDataService(env.CrearFabricaContexto());

            // Se asigna primero el corredor más lento (15 min)
            AsignacionResultado res1 = await service.AsignarTiempoAsync(
                semilla.Carrera.ID_Carrera,
                semilla.Carrera.Nombre,
                dorsal: 2,
                horaLento,
                tempLento);

            Assert.True(res1.Exito);
            Assert.False(res1.HitFinalizado);

            // Luego se asigna el corredor más rápido (9 min)
            AsignacionResultado res2 = await service.AsignarTiempoAsync(
                semilla.Carrera.ID_Carrera,
                semilla.Carrera.Nombre,
                dorsal: 1,
                horaRapido,
                tempRapido);

            Assert.True(res2.Exito);
            Assert.True(res2.HitFinalizado);
            Assert.Equal(semilla.Hit.Nombre_Hit, res2.NombreHitFinalizado);

            using var dbCheck = env.CrearContexto();
            Hit? hitCerrado = await dbCheck.Hits.FindAsync(semilla.Hit.ID_Hit);
            Assert.NotNull(hitCerrado!.Hora_Fin);
            Assert.Equal(horaLento, hitCerrado.Hora_Fin);
            Assert.True(hitCerrado.Hora_Fin >= horaLento);
            Assert.True(hitCerrado.Hora_Fin >= horaRapido);
        }

        [Fact]
        public async Task AsignarTiempoAsync_HitSoloConDnsDnf_NoCierraAutomaticamente()
        {
            using var env = new TestEnvironmentFixture();
            await DatabaseMaintenanceHelper.InicializarYMigrarBaseDatosAsync(env.CrearFabricaContexto());

            DateTime inicioHit = new(2026, 11, 13, 8, 0, 0);
            var semilla = await CrearEscenarioBaseAsync(env, inicioHit, cantidadCorredores: 2);

            using (var db = env.CrearContexto())
            {
                foreach (var c in semilla.Corredores)
                {
                    var dbCorredor = await db.Corredores.FindAsync(c.ID_Corredor);
                    dbCorredor!.Estado = EstadoCorredor.DNS;
                }
                await db.SaveChangesAsync();

                CierreHitResultado cierre = await RaceDataService.EvaluarYCerrarHitAsync(db, semilla.Hit.ID_Hit);
                Assert.False(cierre.HitFinalizado);

                Hit? hitDb = await db.Hits.FindAsync(semilla.Hit.ID_Hit);
                Assert.Null(hitDb!.Hora_Fin);
            }
        }

        [Fact]
        public async Task AsignarTiempoAsync_ConcurrenteMismoDorsal_SoloUnoExitosoYResto409ConservandoTemporales()
        {
            using var env = new TestEnvironmentFixture();
            await DatabaseMaintenanceHelper.InicializarYMigrarBaseDatosAsync(env.CrearFabricaContexto());

            DateTime inicioHit = new(2026, 11, 13, 8, 0, 0);
            var semilla = await CrearEscenarioBaseAsync(env, inicioHit, cantidadCorredores: 2);

            const int totalIntentos = 6;
            string[] rutasTemp = Enumerable.Range(0, totalIntentos)
                .Select(i => CrearArchivoTemporalImagen(env, $"concurrente_{i}.jpg", new byte[] { 1, 2, 3, (byte)i }))
                .ToArray();

            var startBarrier = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var tareas = rutasTemp.Select((ruta, idx) => Task.Run(async () =>
            {
                await startBarrier.Task;
                var svc = new RaceDataService(env.CrearFabricaContexto());
                return await svc.AsignarTiempoAsync(
                    semilla.Carrera.ID_Carrera,
                    semilla.Carrera.Nombre,
                    dorsal: 1,
                    inicioHit.AddMinutes(5).AddSeconds(idx),
                    ruta);
            })).ToArray();

            startBarrier.SetResult();
            AsignacionResultado[] resultados = await Task.WhenAll(tareas);

            Assert.Equal(1, resultados.Count(r => r.Exito && r.StatusCode == 200));
            Assert.Equal(totalIntentos - 1, resultados.Count(r => !r.Exito && r.StatusCode == 409 && r.EsConflicto));

            using var db = env.CrearContexto();
            Assert.Equal(1, await db.Tiempos.CountAsync());

            string[] definitivos = Directory.GetFiles(env.FotosDefinitivasDir, "*", SearchOption.AllDirectories);
            Assert.Single(definitivos);

            int temporalesRestantes = rutasTemp.Count(File.Exists);
            Assert.Equal(totalIntentos - 1, temporalesRestantes);
        }

        [Fact]
        public async Task AsignarTiempoAsync_ConcurrenteMismaFotoDistintosDorsales_SoloUnoProcesaLaCaptura()
        {
            using var env = new TestEnvironmentFixture();
            await DatabaseMaintenanceHelper.InicializarYMigrarBaseDatosAsync(env.CrearFabricaContexto());

            DateTime inicioHit = new(2026, 11, 13, 8, 0, 0);
            var semilla = await CrearEscenarioBaseAsync(env, inicioHit, cantidadCorredores: 2);
            string rutaTempCompartida = CrearArchivoTemporalImagen(env, "unica_captura.jpg");

            var startBarrier = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var t1 = Task.Run(async () =>
            {
                await startBarrier.Task;
                var svc = new RaceDataService(env.CrearFabricaContexto());
                return await svc.AsignarTiempoAsync(
                    semilla.Carrera.ID_Carrera,
                    semilla.Carrera.Nombre,
                    dorsal: 1,
                    inicioHit.AddMinutes(3),
                    rutaTempCompartida);
            });

            var t2 = Task.Run(async () =>
            {
                await startBarrier.Task;
                var svc = new RaceDataService(env.CrearFabricaContexto());
                return await svc.AsignarTiempoAsync(
                    semilla.Carrera.ID_Carrera,
                    semilla.Carrera.Nombre,
                    dorsal: 2,
                    inicioHit.AddMinutes(3),
                    rutaTempCompartida);
            });

            startBarrier.SetResult();
            AsignacionResultado[] resultados = await Task.WhenAll(t1, t2);

            Assert.Equal(1, resultados.Count(r => r.Exito && r.StatusCode == 200));
            Assert.Equal(1, resultados.Count(r => !r.Exito));
            Assert.False(File.Exists(rutaTempCompartida));

            using var db = env.CrearContexto();
            Assert.Equal(1, await db.Tiempos.CountAsync());
            string[] definitivos = Directory.GetFiles(env.FotosDefinitivasDir, "*", SearchOption.AllDirectories);
            Assert.Single(definitivos);
        }

        [Fact]
        public async Task AsignarTiempoAsync_ConcurrenteMultiplesCorredores_SinColisionesNiBloqueosYCierraHit()
        {
            using var env = new TestEnvironmentFixture();
            await DatabaseMaintenanceHelper.InicializarYMigrarBaseDatosAsync(env.CrearFabricaContexto());

            DateTime inicioHit = new(2026, 11, 13, 8, 0, 0);
            const int totalCorredores = 15;
            var semilla = await CrearEscenarioBaseAsync(
                env,
                inicioHit,
                cantidadCorredores: totalCorredores,
                nombreCorredorPrefix: "MismoNombre");

            DateTime horaMaxima = inicioHit.AddMinutes(20);
            string[] rutasTemp = Enumerable.Range(0, totalCorredores)
                .Select(i => CrearArchivoTemporalImagen(env, $"burst_{i}.jpg", new byte[] { 0xFF, 0xD8, (byte)i }))
                .ToArray();

            var startBarrier = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var tareas = Enumerable.Range(0, totalCorredores).Select(i => Task.Run(async () =>
            {
                await startBarrier.Task;
                var svc = new RaceDataService(env.CrearFabricaContexto());
                DateTime hora = (i == 0) ? horaMaxima : inicioHit.AddMinutes(5);
                return await svc.AsignarTiempoAsync(
                    semilla.Carrera.ID_Carrera,
                    semilla.Carrera.Nombre,
                    dorsal: i + 1,
                    hora,
                    rutasTemp[i]);
            })).ToArray();

            startBarrier.SetResult();
            AsignacionResultado[] resultados = await Task.WhenAll(tareas);

            Assert.All(resultados, r =>
            {
                Assert.True(r.Exito, r.Mensaje);
                Assert.Equal(200, r.StatusCode);
            });
            Assert.Equal(1, resultados.Count(r => r.HitFinalizado));

            using var db = env.CrearContexto();
            Assert.Equal(totalCorredores, await db.Tiempos.CountAsync());
            Hit? hit = await db.Hits.FindAsync(semilla.Hit.ID_Hit);
            Assert.Equal(horaMaxima, hit!.Hora_Fin);

            string[] definitivos = Directory.GetFiles(env.FotosDefinitivasDir, "*", SearchOption.AllDirectories);
            Assert.Equal(totalCorredores, definitivos.Length);
        }

        private static string CrearArchivoTemporalImagen(
            TestEnvironmentFixture env,
            string nombreArchivo,
            byte[]? contenido = null)
        {
            Directory.CreateDirectory(env.FotosTempDir);
            string ruta = Path.Combine(env.FotosTempDir, nombreArchivo);
            File.WriteAllBytes(ruta, contenido ?? new byte[] { 0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10 });
            return ruta;
        }

        private static async Task<(Carrera Carrera, Categoria Categoria, Hit Hit, List<Corredor> Corredores)> CrearEscenarioBaseAsync(
            TestEnvironmentFixture env,
            DateTime? inicioHit,
            int cantidadCorredores,
            string nombreCarrera = "Carrera Test",
            string nombreCategoria = "General",
            string nombreCorredorPrefix = "Corredor")
        {
            using var db = env.CrearContexto();

            var carrera = new Carrera
            {
                Nombre = nombreCarrera,
                Fecha = new DateTime(2026, 11, 13)
            };
            db.Carreras.Add(carrera);
            await db.SaveChangesAsync();

            var categoria = new Categoria
            {
                Nombre = nombreCategoria,
                ID_Carrera = carrera.ID_Carrera
            };
            var hit = new Hit
            {
                Nombre_Hit = "Hit Principal",
                ID_Carrera = carrera.ID_Carrera,
                Hora_Inicio = inicioHit
            };
            db.Categorias.Add(categoria);
            db.Hits.Add(hit);
            await db.SaveChangesAsync();

            var corredores = new List<Corredor>(cantidadCorredores);
            for (int i = 1; i <= cantidadCorredores; i++)
            {
                corredores.Add(new Corredor
                {
                    Dorsal = i,
                    Nombre = $"{nombreCorredorPrefix} {i}",
                    ID_Carrera = carrera.ID_Carrera,
                    ID_Categoria = categoria.ID_Categoria,
                    ID_Hit = hit.ID_Hit,
                    Estado = EstadoCorredor.Activo
                });
            }

            db.Corredores.AddRange(corredores);
            await db.SaveChangesAsync();

            return (carrera, categoria, hit, corredores);
        }

        private sealed class ObservadorTransaccionInterceptor : DbTransactionInterceptor
        {
            private readonly string _rutaTemporal;
            private readonly string _carpetaDefinitivas;

            public ObservadorTransaccionInterceptor(string rutaTemporal, string carpetaDefinitivas)
            {
                _rutaTemporal = rutaTemporal;
                _carpetaDefinitivas = carpetaDefinitivas;
            }

            public bool SeInicioTransaccion { get; private set; }
            public bool SeEjecutoCommit { get; private set; }
            public bool TemporalExistiaAntesDeCommit { get; private set; }
            public bool DefinitivoExistiaAntesDeCommit { get; private set; }

            public override ValueTask<DbTransaction> TransactionStartedAsync(
                DbConnection connection,
                TransactionEndEventData eventData,
                DbTransaction result,
                CancellationToken cancellationToken = default)
            {
                SeInicioTransaccion = true;
                return base.TransactionStartedAsync(connection, eventData, result, cancellationToken);
            }

            public override ValueTask<InterceptionResult> TransactionCommittingAsync(
                DbTransaction transaction,
                TransactionEventData eventData,
                InterceptionResult result,
                CancellationToken cancellationToken = default)
            {
                TemporalExistiaAntesDeCommit = File.Exists(_rutaTemporal);
                DefinitivoExistiaAntesDeCommit = Directory.Exists(_carpetaDefinitivas) &&
                    Directory.GetFiles(_carpetaDefinitivas, "*", SearchOption.AllDirectories).Length > 0;
                return base.TransactionCommittingAsync(transaction, eventData, result, cancellationToken);
            }

            public override Task TransactionCommittedAsync(
                DbTransaction transaction,
                TransactionEndEventData eventData,
                CancellationToken cancellationToken = default)
            {
                SeEjecutoCommit = true;
                return base.TransactionCommittedAsync(transaction, eventData, cancellationToken);
            }
        }

        private sealed class FallaSaveChangesInterceptor : SaveChangesInterceptor
        {
            public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
                DbContextEventData eventData,
                InterceptionResult<int> result,
                CancellationToken cancellationToken = default)
            {
                throw new InvalidOperationException("Fallo simulado en SaveChangesAsync.");
            }
        }

        private sealed class FallaCommitInterceptor : DbTransactionInterceptor
        {
            public bool SeIntentoCommit { get; private set; }

            public override ValueTask<InterceptionResult> TransactionCommittingAsync(
                DbTransaction transaction,
                TransactionEventData eventData,
                InterceptionResult result,
                CancellationToken cancellationToken = default)
            {
                SeIntentoCommit = true;
                throw new IOException("Fallo simulado al confirmar transacción SQLite.");
            }
        }
    }
}
