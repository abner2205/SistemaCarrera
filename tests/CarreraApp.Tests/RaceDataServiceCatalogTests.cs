using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using CarreraApp.Data;
using CarreraApp.Helpers;
using CarreraApp.Models;
using Microsoft.EntityFrameworkCore;

namespace CarreraApp.Tests
{
    [Collection("IsolatedStorage")]
    public class RaceDataServiceCatalogTests
    {
        [Fact]
        public async Task CatalogoCrud_SinAplicacionWpf_OperaDeFormaAutonoma()
        {
            using var env = new TestEnvironmentFixture();
            await DatabaseMaintenanceHelper.InicializarYMigrarBaseDatosAsync(env.CrearFabricaContexto());

            Assert.Null(System.Windows.Application.Current);

            var service = new RaceDataService(env.CrearFabricaContexto());
            DateTime fecha = new(2026, 11, 14);

            OperacionResultado<Carrera> resCarrera = await service.CrearOActualizarCarreraAsync(
                null,
                "  Carrera Autónoma 10K  ",
                fecha);
            Assert.True(resCarrera.Exito);
            Assert.NotNull(resCarrera.Entidad);
            Assert.Equal("Carrera Autónoma 10K", resCarrera.Entidad!.Nombre);

            int idCarrera = resCarrera.Entidad.ID_Carrera;

            OperacionResultado<Categoria> resCat = await service.CrearOActualizarCategoriaAsync(
                null,
                idCarrera,
                "  Élite  ");
            Assert.True(resCat.Exito);
            Assert.Equal("Élite", resCat.Entidad!.Nombre);

            OperacionResultado<Hit> resHit = await service.CrearOActualizarHitAsync(
                null,
                idCarrera,
                "  Hit Principal  ");
            Assert.True(resHit.Exito);
            Assert.Equal("Hit Principal", resHit.Entidad!.Nombre_Hit);

            OperacionResultado<Corredor> resCorredor = await service.CrearOActualizarCorredorAsync(
                null,
                idCarrera,
                dorsal: 101,
                nombre: "  Ana Pérez  ",
                idCategoria: resCat.Entidad.ID_Categoria,
                idHit: resHit.Entidad.ID_Hit);
            Assert.True(resCorredor.Exito);
            Assert.NotNull(resCorredor.Entidad);
            Assert.Equal("Ana Pérez", resCorredor.Entidad!.Nombre);
            Assert.Equal(EstadoCorredor.Activo, resCorredor.Entidad.Estado);

            List<CorredorDetalleDto> detalle = await service.ObtenerCorredoresDetallePorCarreraAsync(idCarrera);
            Assert.Single(detalle);
            Assert.Equal(101, detalle[0].Dorsal);
            Assert.Equal("Élite", detalle[0].NombreCategoria);
            Assert.Equal("Hit Principal", detalle[0].NombreHit);
            Assert.Null(detalle[0].TiempoNeto);
        }

        [Theory]
        [InlineData(null, true)]
        [InlineData("", true)]
        [InlineData("   ", true)]
        [InlineData("Carrera Válida", false)]
        public async Task CrearOActualizarCarreraAsync_ConNombreVacioOFechaInvalida_RechazaYNoPersiste(
            string? nombre,
            bool usarFechaValida)
        {
            using var env = new TestEnvironmentFixture();
            await DatabaseMaintenanceHelper.InicializarYMigrarBaseDatosAsync(env.CrearFabricaContexto());
            var service = new RaceDataService(env.CrearFabricaContexto());

            DateTime?[] fechasInvalidas = usarFechaValida
                ? new DateTime?[] { new DateTime(2026, 11, 14) }
                : new DateTime?[]
                {
                    null,
                    default(DateTime),
                    DateTime.MinValue,
                    DateTime.MaxValue,
                    new DateTime(1850, 1, 1),
                    new DateTime(2150, 1, 1)
                };

            foreach (DateTime? fecha in fechasInvalidas)
            {
                OperacionResultado<Carrera> resultado = await service.CrearOActualizarCarreraAsync(null, nombre, fecha);
                Assert.False(resultado.Exito);
                Assert.Equal(400, resultado.StatusCode);
                Assert.Null(resultado.Entidad);
            }

            List<Carrera> carreras = await service.ObtenerCarrerasAsync();
            Assert.Empty(carreras);
        }

        [Fact]
        public async Task CrearOActualizarCarreraAsync_ConDatosValidos_CreaYEditaPreservandoLogos()
        {
            using var env = new TestEnvironmentFixture();
            await DatabaseMaintenanceHelper.InicializarYMigrarBaseDatosAsync(env.CrearFabricaContexto());
            var service = new RaceDataService(env.CrearFabricaContexto());

            OperacionResultado<Carrera> creada = await service.CrearOActualizarCarreraAsync(
                null,
                "Copa Sierra",
                new DateTime(2026, 10, 10),
                rutaLogoIzquierdo: "logo_izq.png",
                rutaLogoDerecho: "logo_der.png");

            Assert.True(creada.Exito);
            int idCarrera = creada.Entidad!.ID_Carrera;

            OperacionResultado<Carrera> editada = await service.CrearOActualizarCarreraAsync(
                idCarrera,
                "  Copa Sierra 2026  ",
                new DateTime(2026, 10, 11));

            Assert.True(editada.Exito);
            Assert.Equal("Copa Sierra 2026", editada.Entidad!.Nombre);
            Assert.Equal(new DateTime(2026, 10, 11), editada.Entidad.Fecha);
            Assert.Equal("logo_izq.png", editada.Entidad.RutaLogoIzquierdo);
            Assert.Equal("logo_der.png", editada.Entidad.RutaLogoDerecho);
        }

        [Fact]
        public async Task CrearOActualizarCategoriaAsync_ValidaVacioDuplicadoMismaCarreraYPermiteEnOtraCarrera()
        {
            using var env = new TestEnvironmentFixture();
            await DatabaseMaintenanceHelper.InicializarYMigrarBaseDatosAsync(env.CrearFabricaContexto());
            var service = new RaceDataService(env.CrearFabricaContexto());

            var c1 = (await service.CrearOActualizarCarreraAsync(null, "Carrera 1", new DateTime(2026, 11, 1))).Entidad!;
            var c2 = (await service.CrearOActualizarCarreraAsync(null, "Carrera 2", new DateTime(2026, 11, 2))).Entidad!;

            var vacia = await service.CrearOActualizarCategoriaAsync(null, c1.ID_Carrera, "   ");
            Assert.False(vacia.Exito);
            Assert.Equal(400, vacia.StatusCode);

            var cat1 = await service.CrearOActualizarCategoriaAsync(null, c1.ID_Carrera, "Libre Femenil");
            Assert.True(cat1.Exito);

            // Duplicado exacto, con espacios o distinto case en la misma carrera
            var dup1 = await service.CrearOActualizarCategoriaAsync(null, c1.ID_Carrera, "  libre femenil  ");
            Assert.False(dup1.Exito);
            Assert.Equal(409, dup1.StatusCode);

            var dup2 = await service.CrearOActualizarCategoriaAsync(null, c1.ID_Carrera, "LIBRE FEMENIL");
            Assert.False(dup2.Exito);
            Assert.Equal(409, dup2.StatusCode);

            // Mismo nombre en otra carrera debe permitirse
            var otraCarrera = await service.CrearOActualizarCategoriaAsync(null, c2.ID_Carrera, "Libre Femenil");
            Assert.True(otraCarrera.Exito);

            // Editar misma categoría cambiando mayúsculas debe permitirse
            var editarMisma = await service.CrearOActualizarCategoriaAsync(cat1.Entidad!.ID_Categoria, c1.ID_Carrera, "LIBRE FEMENIL");
            Assert.True(editarMisma.Exito);
            Assert.Equal("LIBRE FEMENIL", editarMisma.Entidad!.Nombre);

            // Editar a nombre de otra categoría existente en la misma carrera debe rechazarse
            var cat2 = await service.CrearOActualizarCategoriaAsync(null, c1.ID_Carrera, "Master");
            Assert.True(cat2.Exito);
            var colisionEdicion = await service.CrearOActualizarCategoriaAsync(cat2.Entidad!.ID_Categoria, c1.ID_Carrera, " libre femenil ");
            Assert.False(colisionEdicion.Exito);
            Assert.Equal(409, colisionEdicion.StatusCode);
        }

        [Fact]
        public async Task CrearOActualizarHitAsync_ValidaVacioDuplicadoMismaCarreraYPermiteEnOtraCarrera()
        {
            using var env = new TestEnvironmentFixture();
            await DatabaseMaintenanceHelper.InicializarYMigrarBaseDatosAsync(env.CrearFabricaContexto());
            var service = new RaceDataService(env.CrearFabricaContexto());

            var c1 = (await service.CrearOActualizarCarreraAsync(null, "Carrera 1", new DateTime(2026, 11, 1))).Entidad!;
            var c2 = (await service.CrearOActualizarCarreraAsync(null, "Carrera 2", new DateTime(2026, 11, 2))).Entidad!;

            var vacio = await service.CrearOActualizarHitAsync(null, c1.ID_Carrera, "");
            Assert.False(vacio.Exito);
            Assert.Equal(400, vacio.StatusCode);

            var hit1 = await service.CrearOActualizarHitAsync(null, c1.ID_Carrera, "Hit 1");
            Assert.True(hit1.Exito);

            var dup = await service.CrearOActualizarHitAsync(null, c1.ID_Carrera, "  HIT 1 ");
            Assert.False(dup.Exito);
            Assert.Equal(409, dup.StatusCode);

            var otro = await service.CrearOActualizarHitAsync(null, c2.ID_Carrera, "Hit 1");
            Assert.True(otro.Exito);

            var editarMismo = await service.CrearOActualizarHitAsync(hit1.Entidad!.ID_Hit, c1.ID_Carrera, "HIT 1");
            Assert.True(editarMismo.Exito);
            Assert.Equal("HIT 1", editarMismo.Entidad!.Nombre_Hit);
        }

        [Theory]
        [InlineData(0, "Corredor Válido")]
        [InlineData(-1, "Corredor Válido")]
        [InlineData(-99, "Corredor Válido")]
        [InlineData(10, "")]
        [InlineData(10, "   ")]
        [InlineData(10, null)]
        public async Task CrearOActualizarCorredorAsync_ConDorsalNoPositivoONombreVacio_Rechaza(
            int dorsal,
            string? nombre)
        {
            using var env = new TestEnvironmentFixture();
            await DatabaseMaintenanceHelper.InicializarYMigrarBaseDatosAsync(env.CrearFabricaContexto());
            var service = new RaceDataService(env.CrearFabricaContexto());

            var escenario = await CrearCarreraBaseAsync(env, service, "Carrera Dorsal", 1, 1);
            var resultado = await service.CrearOActualizarCorredorAsync(
                null,
                escenario.Carrera.ID_Carrera,
                dorsal,
                nombre,
                escenario.Categorias[0].ID_Categoria,
                escenario.Hits[0].ID_Hit);

            Assert.False(resultado.Exito);
            Assert.Equal(400, resultado.StatusCode);
        }

        [Fact]
        public async Task CrearOActualizarCorredorAsync_DorsalDuplicadoMismaCarrera_RechazaYPermiteEnOtraCarrera()
        {
            using var env = new TestEnvironmentFixture();
            await DatabaseMaintenanceHelper.InicializarYMigrarBaseDatosAsync(env.CrearFabricaContexto());
            var service = new RaceDataService(env.CrearFabricaContexto());

            var e1 = await CrearCarreraBaseAsync(env, service, "Carrera A", 1, 1);
            var e2 = await CrearCarreraBaseAsync(env, service, "Carrera B", 1, 1);

            var c1 = await service.CrearOActualizarCorredorAsync(
                null, e1.Carrera.ID_Carrera, 7, "Juan", e1.Categorias[0].ID_Categoria, e1.Hits[0].ID_Hit);
            Assert.True(c1.Exito);

            // Mismo dorsal en misma carrera
            var dup = await service.CrearOActualizarCorredorAsync(
                null, e1.Carrera.ID_Carrera, 7, "Pedro", e1.Categorias[0].ID_Categoria, e1.Hits[0].ID_Hit);
            Assert.False(dup.Exito);
            Assert.Equal(409, dup.StatusCode);

            // Mismo dorsal en otra carrera debe permitirse
            var otraCarrera = await service.CrearOActualizarCorredorAsync(
                null, e2.Carrera.ID_Carrera, 7, "Pedro", e2.Categorias[0].ID_Categoria, e2.Hits[0].ID_Hit);
            Assert.True(otraCarrera.Exito);

            // Editar al mismo corredor conservando su dorsal 7 debe permitirse
            var editarPropio = await service.CrearOActualizarCorredorAsync(
                c1.Entidad!.ID_Corredor, e1.Carrera.ID_Carrera, 7, "Juan Editado", e1.Categorias[0].ID_Categoria, e1.Hits[0].ID_Hit);
            Assert.True(editarPropio.Exito);
            Assert.Equal("Juan Editado", editarPropio.Entidad!.Nombre);

            // Categoría o Hit de otra carrera debe rechazarse
            var cruzado = await service.CrearOActualizarCorredorAsync(
                null, e1.Carrera.ID_Carrera, 8, "Inconsistente", e2.Categorias[0].ID_Categoria, e1.Hits[0].ID_Hit);
            Assert.False(cruzado.Exito);
        }

        [Fact]
        public async Task CambiarEstadoCorredorAsync_SinTiempoRegistrado_PermiteTransicionesEntreActivoDnsYDnf()
        {
            using var env = new TestEnvironmentFixture();
            await DatabaseMaintenanceHelper.InicializarYMigrarBaseDatosAsync(env.CrearFabricaContexto());
            var service = new RaceDataService(env.CrearFabricaContexto());

            var e = await CrearCarreraBaseAsync(env, service, "Carrera Estados", 1, 1);
            var corredor = (await service.CrearOActualizarCorredorAsync(
                null, e.Carrera.ID_Carrera, 1, "Sofía", e.Categorias[0].ID_Categoria, e.Hits[0].ID_Hit)).Entidad!;

            var aDns = await service.CambiarEstadoCorredorAsync(corredor.ID_Corredor, EstadoCorredor.DNS);
            Assert.True(aDns.Exito);
            Assert.Equal(EstadoCorredor.DNS, aDns.Entidad!.Estado);

            var aDnf = await service.CambiarEstadoCorredorAsync(corredor.ID_Corredor, EstadoCorredor.DNF);
            Assert.True(aDnf.Exito);
            Assert.Equal(EstadoCorredor.DNF, aDnf.Entidad!.Estado);

            var aActivo = await service.CambiarEstadoCorredorAsync(corredor.ID_Corredor, EstadoCorredor.Activo);
            Assert.True(aActivo.Exito);
            Assert.Equal(EstadoCorredor.Activo, aActivo.Entidad!.Estado);
        }

        [Fact]
        public async Task CorredorConTiempoRegistrado_ProhibeCambiarEstadoADnsDnfYProhibeCambiarDeHit()
        {
            using var env = new TestEnvironmentFixture();
            await DatabaseMaintenanceHelper.InicializarYMigrarBaseDatosAsync(env.CrearFabricaContexto());
            var service = new RaceDataService(env.CrearFabricaContexto());

            var e = await CrearCarreraBaseAsync(env, service, "Carrera Bloqueo Tiempo", 1, 2);
            DateTime inicio = new(2026, 11, 14, 9, 0, 0);
            await IniciarHitAsync(env, e.Hits[0].ID_Hit, inicio);

            var c1 = (await service.CrearOActualizarCorredorAsync(
                null, e.Carrera.ID_Carrera, 10, "Marco", e.Categorias[0].ID_Categoria, e.Hits[0].ID_Hit)).Entidad!;

            string fotoTemp = CrearArchivoTemporalImagen(env, "marco.jpg");
            var asig = await service.AsignarTiempoAsync(e.Carrera.ID_Carrera, e.Carrera.Nombre, 10, inicio.AddMinutes(4), fotoTemp);
            Assert.True(asig.Exito);

            // Prohibido cambiar estado a DNS o DNF mediante CambiarEstadoCorredorAsync
            var resDns = await service.CambiarEstadoCorredorAsync(c1.ID_Corredor, EstadoCorredor.DNS);
            Assert.False(resDns.Exito);
            Assert.Equal(422, resDns.StatusCode);

            var resDnf = await service.CambiarEstadoCorredorAsync(c1.ID_Corredor, EstadoCorredor.DNF);
            Assert.False(resDnf.Exito);
            Assert.Equal(422, resDnf.StatusCode);

            // Prohibido cambiar estado vía CrearOActualizarCorredorAsync
            var resEditEstado = await service.CrearOActualizarCorredorAsync(
                c1.ID_Corredor, e.Carrera.ID_Carrera, 10, "Marco", e.Categorias[0].ID_Categoria, e.Hits[0].ID_Hit, EstadoCorredor.DNF);
            Assert.False(resEditEstado.Exito);
            Assert.Equal(422, resEditEstado.StatusCode);

            // Prohibido cambiar Hit vía CrearOActualizarCorredorAsync
            var resEditHit = await service.CrearOActualizarCorredorAsync(
                c1.ID_Corredor, e.Carrera.ID_Carrera, 10, "Marco", e.Categorias[0].ID_Categoria, e.Hits[1].ID_Hit, EstadoCorredor.Activo);
            Assert.False(resEditHit.Exito);
            Assert.Equal(422, resEditHit.StatusCode);

            // Permitido corregir nombre manteniendo mismo Hit y mismo estado
            var resEditNombre = await service.CrearOActualizarCorredorAsync(
                c1.ID_Corredor, e.Carrera.ID_Carrera, 10, "Marco Antonio", e.Categorias[0].ID_Categoria, e.Hits[0].ID_Hit, EstadoCorredor.Activo);
            Assert.True(resEditNombre.Exito);
            Assert.Equal("Marco Antonio", resEditNombre.Entidad!.Nombre);

            using var db = env.CrearContexto();
            Corredor? corredorDb = await db.Corredores.FindAsync(c1.ID_Corredor);
            Assert.NotNull(corredorDb);
            Assert.Equal(EstadoCorredor.Activo, corredorDb!.Estado);
            Assert.Equal(e.Hits[0].ID_Hit, corredorDb.ID_Hit);
        }

        [Fact]
        public async Task CorredorSinTiempo_PermiteCambiarDeHit()
        {
            using var env = new TestEnvironmentFixture();
            await DatabaseMaintenanceHelper.InicializarYMigrarBaseDatosAsync(env.CrearFabricaContexto());
            var service = new RaceDataService(env.CrearFabricaContexto());

            var e = await CrearCarreraBaseAsync(env, service, "Carrera Cambio Hit", 1, 2);
            var c1 = (await service.CrearOActualizarCorredorAsync(
                null, e.Carrera.ID_Carrera, 15, "Laura", e.Categorias[0].ID_Categoria, e.Hits[0].ID_Hit)).Entidad!;

            var movido = await service.CrearOActualizarCorredorAsync(
                c1.ID_Corredor, e.Carrera.ID_Carrera, 15, "Laura", e.Categorias[0].ID_Categoria, e.Hits[1].ID_Hit);

            Assert.True(movido.Exito);
            Assert.Equal(e.Hits[1].ID_Hit, movido.Entidad!.ID_Hit);
        }

        [Theory]
        [InlineData(EstadoCorredor.DNS)]
        [InlineData(EstadoCorredor.DNF)]
        public async Task CambiarEstadoCorredorAsync_UltimoActivoSinTiempoPasaADnsODnf_CierraHitConHoraMaxima(EstadoCorredor estadoFinal)
        {
            using var env = new TestEnvironmentFixture();
            await DatabaseMaintenanceHelper.InicializarYMigrarBaseDatosAsync(env.CrearFabricaContexto());
            var service = new RaceDataService(env.CrearFabricaContexto());

            var e = await CrearCarreraBaseAsync(env, service, "Carrera Cierre DNS/DNF", 1, 1);
            DateTime inicio = new(2026, 11, 14, 8, 0, 0);
            DateTime llegada1 = inicio.AddMinutes(3).AddSeconds(20);
            DateTime llegada2 = inicio.AddMinutes(5).AddSeconds(45);
            await IniciarHitAsync(env, e.Hits[0].ID_Hit, inicio);

            var r1 = (await service.CrearOActualizarCorredorAsync(null, e.Carrera.ID_Carrera, 1, "R1", e.Categorias[0].ID_Categoria, e.Hits[0].ID_Hit)).Entidad!;
            var r2 = (await service.CrearOActualizarCorredorAsync(null, e.Carrera.ID_Carrera, 2, "R2", e.Categorias[0].ID_Categoria, e.Hits[0].ID_Hit)).Entidad!;
            var r3 = (await service.CrearOActualizarCorredorAsync(null, e.Carrera.ID_Carrera, 3, "R3", e.Categorias[0].ID_Categoria, e.Hits[0].ID_Hit)).Entidad!;

            Assert.True((await service.AsignarTiempoAsync(e.Carrera.ID_Carrera, e.Carrera.Nombre, 1, llegada1, CrearArchivoTemporalImagen(env, "r1.jpg"))).Exito);
            var asig2 = await service.AsignarTiempoAsync(e.Carrera.ID_Carrera, e.Carrera.Nombre, 2, llegada2, CrearArchivoTemporalImagen(env, "r2.jpg"));
            Assert.True(asig2.Exito);
            Assert.False(asig2.HitFinalizado);

            // Al marcar a R3 como DNS o DNF, el hit debe cerrarse automáticamente con Hora_Fin = llegada2
            var cambio = await service.CambiarEstadoCorredorAsync(r3.ID_Corredor, estadoFinal);
            Assert.True(cambio.Exito);
            Assert.True(cambio.HitFinalizado);
            Assert.Equal(e.Hits[0].Nombre_Hit, cambio.NombreHitFinalizado);

            using var db = env.CrearContexto();
            Hit? hitDb = await db.Hits.FindAsync(e.Hits[0].ID_Hit);
            Assert.NotNull(hitDb);
            Assert.Equal(llegada2, hitDb!.Hora_Fin);
        }

        [Fact]
        public async Task CambiarEstadoCorredorAsync_TodosLosCorredoresDnsODnf_NoCierraHit()
        {
            using var env = new TestEnvironmentFixture();
            await DatabaseMaintenanceHelper.InicializarYMigrarBaseDatosAsync(env.CrearFabricaContexto());
            var service = new RaceDataService(env.CrearFabricaContexto());

            var e = await CrearCarreraBaseAsync(env, service, "Carrera Todos Bajas", 1, 1);
            await IniciarHitAsync(env, e.Hits[0].ID_Hit, new DateTime(2026, 11, 14, 8, 0, 0));

            var r1 = (await service.CrearOActualizarCorredorAsync(null, e.Carrera.ID_Carrera, 1, "R1", e.Categorias[0].ID_Categoria, e.Hits[0].ID_Hit)).Entidad!;
            var r2 = (await service.CrearOActualizarCorredorAsync(null, e.Carrera.ID_Carrera, 2, "R2", e.Categorias[0].ID_Categoria, e.Hits[0].ID_Hit)).Entidad!;

            var res1 = await service.CambiarEstadoCorredorAsync(r1.ID_Corredor, EstadoCorredor.DNS);
            Assert.True(res1.Exito);
            Assert.False(res1.HitFinalizado);

            var res2 = await service.CambiarEstadoCorredorAsync(r2.ID_Corredor, EstadoCorredor.DNF);
            Assert.True(res2.Exito);
            Assert.False(res2.HitFinalizado);

            using var db = env.CrearContexto();
            Hit? hitDb = await db.Hits.FindAsync(e.Hits[0].ID_Hit);
            Assert.Null(hitDb!.Hora_Fin);
        }

        [Fact]
        public async Task CambiarEstadoCorredorAsync_VolverAActivoYAgregarCorredorEnHitCerrado_ReabreHitConsistentemente()
        {
            using var env = new TestEnvironmentFixture();
            await DatabaseMaintenanceHelper.InicializarYMigrarBaseDatosAsync(env.CrearFabricaContexto());
            var service = new RaceDataService(env.CrearFabricaContexto());

            var e = await CrearCarreraBaseAsync(env, service, "Carrera Reapertura", 1, 1);
            DateTime inicio = new(2026, 11, 14, 7, 30, 0);
            await IniciarHitAsync(env, e.Hits[0].ID_Hit, inicio);

            var r1 = (await service.CrearOActualizarCorredorAsync(null, e.Carrera.ID_Carrera, 1, "R1", e.Categorias[0].ID_Categoria, e.Hits[0].ID_Hit)).Entidad!;
            var r2 = (await service.CrearOActualizarCorredorAsync(null, e.Carrera.ID_Carrera, 2, "R2", e.Categorias[0].ID_Categoria, e.Hits[0].ID_Hit)).Entidad!;

            DateTime llegada1 = inicio.AddMinutes(4);
            Assert.True((await service.AsignarTiempoAsync(e.Carrera.ID_Carrera, e.Carrera.Nombre, 1, llegada1, CrearArchivoTemporalImagen(env, "r1.jpg"))).Exito);

            // Pasar R2 a DNS cierra el hit
            var cierre = await service.CambiarEstadoCorredorAsync(r2.ID_Corredor, EstadoCorredor.DNS);
            Assert.True(cierre.HitFinalizado);

            // Revertir R2 a Activo reabre el hit para permitir registrar su llegada
            var reactivar = await service.CambiarEstadoCorredorAsync(r2.ID_Corredor, EstadoCorredor.Activo);
            Assert.True(reactivar.Exito);

            using (var db = env.CrearContexto())
            {
                Hit? hitReabierto = await db.Hits.FindAsync(e.Hits[0].ID_Hit);
                Assert.Null(hitReabierto!.Hora_Fin);
            }

            // Registrar tiempo de R2 vuelve a cerrar el hit con la nueva hora máxima
            DateTime llegada2 = inicio.AddMinutes(6);
            var asig2 = await service.AsignarTiempoAsync(e.Carrera.ID_Carrera, e.Carrera.Nombre, 2, llegada2, CrearArchivoTemporalImagen(env, "r2.jpg"));
            Assert.True(asig2.Exito);
            Assert.True(asig2.HitFinalizado);

            // Agregar un corredor Activo al hit cerrado también reabre el hit consistentemente
            var r3 = await service.CrearOActualizarCorredorAsync(null, e.Carrera.ID_Carrera, 3, "R3", e.Categorias[0].ID_Categoria, e.Hits[0].ID_Hit);
            Assert.True(r3.Exito);

            using (var db = env.CrearContexto())
            {
                Hit? hitReabierto2 = await db.Hits.FindAsync(e.Hits[0].ID_Hit);
                Assert.Null(hitReabierto2!.Hora_Fin);
            }
        }

        [Fact]
        public async Task CrearOActualizarCorredorAsync_MoverUltimoActivoSinTiempoAOtroHit_CierraHitOrigen()
        {
            using var env = new TestEnvironmentFixture();
            await DatabaseMaintenanceHelper.InicializarYMigrarBaseDatosAsync(env.CrearFabricaContexto());
            var service = new RaceDataService(env.CrearFabricaContexto());

            var e = await CrearCarreraBaseAsync(env, service, "Carrera Mover Hit", 1, 2);
            DateTime inicio = new(2026, 11, 14, 11, 0, 0);
            DateTime llegada1 = inicio.AddMinutes(2);
            await IniciarHitAsync(env, e.Hits[0].ID_Hit, inicio);

            var r1 = (await service.CrearOActualizarCorredorAsync(null, e.Carrera.ID_Carrera, 1, "R1", e.Categorias[0].ID_Categoria, e.Hits[0].ID_Hit)).Entidad!;
            var r2 = (await service.CrearOActualizarCorredorAsync(null, e.Carrera.ID_Carrera, 2, "R2", e.Categorias[0].ID_Categoria, e.Hits[0].ID_Hit)).Entidad!;

            Assert.True((await service.AsignarTiempoAsync(e.Carrera.ID_Carrera, e.Carrera.Nombre, 1, llegada1, CrearArchivoTemporalImagen(env, "m1.jpg"))).Exito);

            // Mover R2 (único Activo pendiente en Hit 0) hacia Hit 1
            var mover = await service.CrearOActualizarCorredorAsync(
                r2.ID_Corredor, e.Carrera.ID_Carrera, 2, "R2", e.Categorias[0].ID_Categoria, e.Hits[1].ID_Hit);
            Assert.True(mover.Exito);

            using var db = env.CrearContexto();
            Hit? hitOrigen = await db.Hits.FindAsync(e.Hits[0].ID_Hit);
            Assert.Equal(llegada1, hitOrigen!.Hora_Fin);
        }

        [Fact]
        public async Task EliminarCorredorAsync_ConYSinTiempoRegistrado_ProtegeHistoricoYEliminaLimpioSinTiempo()
        {
            using var env = new TestEnvironmentFixture();
            await DatabaseMaintenanceHelper.InicializarYMigrarBaseDatosAsync(env.CrearFabricaContexto());
            var service = new RaceDataService(env.CrearFabricaContexto());

            var e = await CrearCarreraBaseAsync(env, service, "Carrera Eliminar Corredor", 1, 1);
            DateTime inicio = new(2026, 11, 14, 12, 0, 0);
            await IniciarHitAsync(env, e.Hits[0].ID_Hit, inicio);

            var rConTiempo = (await service.CrearOActualizarCorredorAsync(null, e.Carrera.ID_Carrera, 1, "Con Tiempo", e.Categorias[0].ID_Categoria, e.Hits[0].ID_Hit)).Entidad!;
            var rSinTiempo = (await service.CrearOActualizarCorredorAsync(null, e.Carrera.ID_Carrera, 2, "Sin Tiempo", e.Categorias[0].ID_Categoria, e.Hits[0].ID_Hit)).Entidad!;

            DateTime llegada1 = inicio.AddMinutes(3);
            Assert.True((await service.AsignarTiempoAsync(e.Carrera.ID_Carrera, e.Carrera.Nombre, 1, llegada1, CrearArchivoTemporalImagen(env, "ct.jpg"))).Exito);

            // Intentar eliminar corredor con tiempo -> BLOQUEADO (RF-22)
            OperacionResultado bloqueo = await service.EliminarCorredorAsync(rConTiempo.ID_Corredor);
            Assert.False(bloqueo.Exito);
            Assert.Contains("protegido", bloqueo.Mensaje, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("DNS", bloqueo.Mensaje, StringComparison.OrdinalIgnoreCase);

            // Eliminar corredor sin tiempo -> PERMITIDO y evalúa cierre de Hit
            OperacionResultado ok = await service.EliminarCorredorAsync(rSinTiempo.ID_Corredor);
            Assert.True(ok.Exito);

            using var db = env.CrearContexto();
            Assert.NotNull(await db.Corredores.FindAsync(rConTiempo.ID_Corredor));
            Assert.Null(await db.Corredores.FindAsync(rSinTiempo.ID_Corredor));
            Hit? hit = await db.Hits.FindAsync(e.Hits[0].ID_Hit);
            Assert.Equal(llegada1, hit!.Hora_Fin);
        }

        [Fact]
        public async Task EliminarHitAsync_ConTiempoBloquea_ConCorredorSinTiempoRestringe_VacioEliminaLimpio()
        {
            using var env = new TestEnvironmentFixture();
            await DatabaseMaintenanceHelper.InicializarYMigrarBaseDatosAsync(env.CrearFabricaContexto());
            var service = new RaceDataService(env.CrearFabricaContexto());

            var e = await CrearCarreraBaseAsync(env, service, "Carrera Eliminar Hit", 1, 3);
            DateTime inicio = new(2026, 11, 14, 13, 0, 0);
            await IniciarHitAsync(env, e.Hits[0].ID_Hit, inicio);

            // Hit 0: tiene corredor con tiempo
            await service.CrearOActualizarCorredorAsync(null, e.Carrera.ID_Carrera, 1, "R1", e.Categorias[0].ID_Categoria, e.Hits[0].ID_Hit);
            Assert.True((await service.AsignarTiempoAsync(e.Carrera.ID_Carrera, e.Carrera.Nombre, 1, inicio.AddMinutes(1), CrearArchivoTemporalImagen(env, "h0.jpg"))).Exito);

            // Hit 1: tiene corredor sin tiempo
            await service.CrearOActualizarCorredorAsync(null, e.Carrera.ID_Carrera, 2, "R2", e.Categorias[0].ID_Categoria, e.Hits[1].ID_Hit);

            // Hit 2: vacío

            OperacionResultado resConTiempo = await service.EliminarHitAsync(e.Hits[0].ID_Hit);
            Assert.False(resConTiempo.Exito);
            Assert.Contains("protegido", resConTiempo.Mensaje, StringComparison.OrdinalIgnoreCase);

            OperacionResultado resConCorredorSinTiempo = await service.EliminarHitAsync(e.Hits[1].ID_Hit);
            Assert.False(resConCorredorSinTiempo.Exito);

            OperacionResultado resVacio = await service.EliminarHitAsync(e.Hits[2].ID_Hit);
            Assert.True(resVacio.Exito);

            using var db = env.CrearContexto();
            Assert.NotNull(await db.Hits.FindAsync(e.Hits[0].ID_Hit));
            Assert.NotNull(await db.Hits.FindAsync(e.Hits[1].ID_Hit));
            Assert.Null(await db.Hits.FindAsync(e.Hits[2].ID_Hit));
        }

        [Fact]
        public async Task EliminarCategoriaAsync_ConTiempoBloquea_ConCorredorSinTiempoRestringe_VaciaEliminaLimpio()
        {
            using var env = new TestEnvironmentFixture();
            await DatabaseMaintenanceHelper.InicializarYMigrarBaseDatosAsync(env.CrearFabricaContexto());
            var service = new RaceDataService(env.CrearFabricaContexto());

            var e = await CrearCarreraBaseAsync(env, service, "Carrera Eliminar Categoria", 3, 1);
            DateTime inicio = new(2026, 11, 14, 14, 0, 0);
            await IniciarHitAsync(env, e.Hits[0].ID_Hit, inicio);

            // Cat 0: corredor con tiempo
            await service.CrearOActualizarCorredorAsync(null, e.Carrera.ID_Carrera, 1, "R1", e.Categorias[0].ID_Categoria, e.Hits[0].ID_Hit);
            Assert.True((await service.AsignarTiempoAsync(e.Carrera.ID_Carrera, e.Carrera.Nombre, 1, inicio.AddMinutes(1), CrearArchivoTemporalImagen(env, "c0.jpg"))).Exito);

            // Cat 1: corredor sin tiempo
            await service.CrearOActualizarCorredorAsync(null, e.Carrera.ID_Carrera, 2, "R2", e.Categorias[1].ID_Categoria, e.Hits[0].ID_Hit);

            // Cat 2: vacía

            OperacionResultado resConTiempo = await service.EliminarCategoriaAsync(e.Categorias[0].ID_Categoria);
            Assert.False(resConTiempo.Exito);
            Assert.Contains("protegido", resConTiempo.Mensaje, StringComparison.OrdinalIgnoreCase);

            OperacionResultado resConCorredorSinTiempo = await service.EliminarCategoriaAsync(e.Categorias[1].ID_Categoria);
            Assert.False(resConCorredorSinTiempo.Exito);

            OperacionResultado resVacia = await service.EliminarCategoriaAsync(e.Categorias[2].ID_Categoria);
            Assert.True(resVacia.Exito);

            using var db = env.CrearContexto();
            Assert.NotNull(await db.Categorias.FindAsync(e.Categorias[0].ID_Categoria));
            Assert.NotNull(await db.Categorias.FindAsync(e.Categorias[1].ID_Categoria));
            Assert.Null(await db.Categorias.FindAsync(e.Categorias[2].ID_Categoria));
        }

        [Fact]
        public async Task EliminarCarreraAsync_ConTiempoRegistradoBloquea_SinTiemposEliminaLimpioInclusoConHijos()
        {
            using var env = new TestEnvironmentFixture();
            await DatabaseMaintenanceHelper.InicializarYMigrarBaseDatosAsync(env.CrearFabricaContexto());
            var service = new RaceDataService(env.CrearFabricaContexto());

            // Carrera 1: con tiempo registrado -> protegida
            var conTiempos = await CrearCarreraBaseAsync(env, service, "Carrera Histórica", 1, 1);
            DateTime inicio = new(2026, 11, 14, 15, 0, 0);
            await IniciarHitAsync(env, conTiempos.Hits[0].ID_Hit, inicio);
            await service.CrearOActualizarCorredorAsync(null, conTiempos.Carrera.ID_Carrera, 1, "R1", conTiempos.Categorias[0].ID_Categoria, conTiempos.Hits[0].ID_Hit);
            Assert.True((await service.AsignarTiempoAsync(conTiempos.Carrera.ID_Carrera, conTiempos.Carrera.Nombre, 1, inicio.AddMinutes(2), CrearArchivoTemporalImagen(env, "ch.jpg"))).Exito);

            OperacionResultado bloqueo = await service.EliminarCarreraAsync(conTiempos.Carrera.ID_Carrera);
            Assert.False(bloqueo.Exito);
            Assert.Contains("protegido", bloqueo.Mensaje, StringComparison.OrdinalIgnoreCase);

            // Carrera 2: con categorías, hits y corredores SIN tiempo -> borrado limpio
            var sinTiempos = await CrearCarreraBaseAsync(env, service, "Carrera Borrador", 2, 2);
            await service.CrearOActualizarCorredorAsync(null, sinTiempos.Carrera.ID_Carrera, 10, "R10", sinTiempos.Categorias[0].ID_Categoria, sinTiempos.Hits[0].ID_Hit);
            await service.CrearOActualizarCorredorAsync(null, sinTiempos.Carrera.ID_Carrera, 11, "R11", sinTiempos.Categorias[1].ID_Categoria, sinTiempos.Hits[1].ID_Hit);

            OperacionResultado eliminada = await service.EliminarCarreraAsync(sinTiempos.Carrera.ID_Carrera);
            Assert.True(eliminada.Exito);

            using var db = env.CrearContexto();
            Assert.NotNull(await db.Carreras.FindAsync(conTiempos.Carrera.ID_Carrera));
            Assert.Null(await db.Carreras.FindAsync(sinTiempos.Carrera.ID_Carrera));
            Assert.Empty(await db.Corredores.Where(c => c.ID_Carrera == sinTiempos.Carrera.ID_Carrera).ToListAsync());
            Assert.Empty(await db.Hits.Where(h => h.ID_Carrera == sinTiempos.Carrera.ID_Carrera).ToListAsync());
            Assert.Empty(await db.Categorias.Where(c => c.ID_Carrera == sinTiempos.Carrera.ID_Carrera).ToListAsync());
        }

        [Fact]
        public async Task MetodosCatalogo_ConIdentificadoresInvalidosOInexistentes_Retornan400O404SinExcepciones()
        {
            using var env = new TestEnvironmentFixture();
            await DatabaseMaintenanceHelper.InicializarYMigrarBaseDatosAsync(env.CrearFabricaContexto());
            var service = new RaceDataService(env.CrearFabricaContexto());

            Assert.Equal(400, (await service.EliminarCorredorAsync(0)).StatusCode);
            Assert.Equal(404, (await service.EliminarCorredorAsync(9999)).StatusCode);
            Assert.Equal(400, (await service.EliminarHitAsync(-1)).StatusCode);
            Assert.Equal(404, (await service.EliminarHitAsync(9999)).StatusCode);
            Assert.Equal(400, (await service.EliminarCategoriaAsync(0)).StatusCode);
            Assert.Equal(404, (await service.EliminarCategoriaAsync(9999)).StatusCode);
            Assert.Equal(400, (await service.EliminarCarreraAsync(-5)).StatusCode);
            Assert.Equal(404, (await service.EliminarCarreraAsync(9999)).StatusCode);
            Assert.Equal(400, (await service.CambiarEstadoCorredorAsync(0, EstadoCorredor.DNS)).StatusCode);
            Assert.Equal(404, (await service.CambiarEstadoCorredorAsync(9999, EstadoCorredor.DNS)).StatusCode);
            Assert.Equal(400, (await service.CambiarEstadoCorredorAsync(1, (EstadoCorredor)999)).StatusCode);

            // Verificar contrato del DTO visual de CorredoresView
            var filaSinTiempo = new CarreraApp.UI.Views.CorredorFilaView { TiempoNeto = null };
            Assert.False(filaSinTiempo.TieneTiempo);
            Assert.True(filaSinTiempo.PuedeModificarEstadoYHit);
            Assert.Equal(3, filaSinTiempo.EstadosDisponibles.Count);

            var filaConTiempo = new CarreraApp.UI.Views.CorredorFilaView { TiempoNeto = TimeSpan.FromSeconds(75) };
            Assert.True(filaConTiempo.TieneTiempo);
            Assert.False(filaConTiempo.PuedeModificarEstadoYHit);
            Assert.Equal("00:01:15", filaConTiempo.TiempoFormateado);
        }

        private sealed record EscenarioCarrera(Carrera Carrera, List<Categoria> Categorias, List<Hit> Hits);

        private static async Task<EscenarioCarrera> CrearCarreraBaseAsync(
            TestEnvironmentFixture env,
            RaceDataService service,
            string nombreCarrera,
            int cantidadCategorias,
            int cantidadHits)
        {
            var carrera = (await service.CrearOActualizarCarreraAsync(null, nombreCarrera, new DateTime(2026, 11, 14))).Entidad!;

            var categorias = new List<Categoria>();
            for (int i = 1; i <= cantidadCategorias; i++)
            {
                categorias.Add((await service.CrearOActualizarCategoriaAsync(null, carrera.ID_Carrera, $"Categoría {i}")).Entidad!);
            }

            var hits = new List<Hit>();
            for (int i = 1; i <= cantidadHits; i++)
            {
                hits.Add((await service.CrearOActualizarHitAsync(null, carrera.ID_Carrera, $"Hit {i}")).Entidad!);
            }

            return new EscenarioCarrera(carrera, categorias, hits);
        }

        private static async Task IniciarHitAsync(TestEnvironmentFixture env, int idHit, DateTime horaInicio)
        {
            using var db = env.CrearContexto();
            Hit? hit = await db.Hits.FindAsync(idHit);
            hit!.Hora_Inicio = horaInicio;
            await db.SaveChangesAsync();
        }

        private static string CrearArchivoTemporalImagen(TestEnvironmentFixture env, string nombreArchivo)
        {
            Directory.CreateDirectory(env.FotosTempDir);
            string ruta = Path.Combine(env.FotosTempDir, nombreArchivo);
            File.WriteAllBytes(ruta, new byte[] { 0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10, 0x4A, 0x46, 0x49, 0x46, 0x00, 0x01 });
            return ruta;
        }
    }
}
