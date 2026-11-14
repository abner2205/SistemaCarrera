using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using CarreraApp.Data;
using CarreraApp.Helpers;
using CarreraApp.Models;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace CarreraApp.Tests
{
    [Collection("IsolatedStorage")]
    public class DatabaseConfigurationTests
    {
        [Fact]
        public void StorageHelper_ConfigurarRutasParaPruebas_AislaRutasFueraDeProduccion()
        {
            using var env = new TestEnvironmentFixture();

            string dbActual = StorageHelper.ObtenerRutaBaseDatos();
            string storageActual = StorageHelper.ObtenerRutaStorage();
            string fotosTemp = StorageHelper.ObtenerCarpetaFotosTemp();
            string fotosDef = StorageHelper.ObtenerCarpetaFotosDefinitivas();
            string logos = StorageHelper.ObtenerCarpetaLogos();
            string backups = StorageHelper.ObtenerCarpetaRespaldos();

            Assert.True(StorageHelper.UsandoRutasDePruebas);
            Assert.Equal(Path.GetFullPath(env.DbPath), Path.GetFullPath(dbActual));
            Assert.Equal(Path.GetFullPath(env.StorageRoot), Path.GetFullPath(storageActual));
            Assert.StartsWith(Path.GetFullPath(env.StorageRoot), Path.GetFullPath(fotosTemp), StringComparison.OrdinalIgnoreCase);
            Assert.StartsWith(Path.GetFullPath(env.StorageRoot), Path.GetFullPath(fotosDef), StringComparison.OrdinalIgnoreCase);
            Assert.StartsWith(Path.GetFullPath(env.StorageRoot), Path.GetFullPath(logos), StringComparison.OrdinalIgnoreCase);
            Assert.StartsWith(Path.GetFullPath(env.StorageRoot), Path.GetFullPath(backups), StringComparison.OrdinalIgnoreCase);
            Assert.True(Directory.Exists(backups));
            Assert.DoesNotContain(
                Path.Combine("src", "CarreraApp"),
                dbActual,
                StringComparison.OrdinalIgnoreCase);

            StorageHelper.RestablecerRutasPorDefecto();
            Assert.False(StorageHelper.UsandoRutasDePruebas);
        }

        [Fact]
        public async Task StorageHelper_AccesoConcurrente_EsThreadSafe()
        {
            using var env = new TestEnvironmentFixture();

            var tareas = Enumerable.Range(0, 16).Select(_ => Task.Run(() =>
            {
                string db = StorageHelper.ObtenerRutaBaseDatos();
                string temp = StorageHelper.ObtenerCarpetaFotosTemp();
                string def = StorageHelper.ObtenerCarpetaFotosDefinitivas();
                string logos = StorageHelper.ObtenerCarpetaLogos();
                string backups = StorageHelper.ObtenerCarpetaRespaldos();

                Assert.Equal(Path.GetFullPath(env.DbPath), Path.GetFullPath(db));
                Assert.True(Directory.Exists(temp));
                Assert.True(Directory.Exists(def));
                Assert.True(Directory.Exists(logos));
                Assert.True(Directory.Exists(backups));
            }));

            await Task.WhenAll(tareas);
        }

        [Fact]
        public void ApplicationDbContext_PorDefecto_NoUsaSharedCacheYConfiguraDefaultTimeout()
        {
            using var env = new TestEnvironmentFixture();
            using var db = new ApplicationDbContext();

            string? connString = db.Database.GetConnectionString();
            Assert.False(string.IsNullOrWhiteSpace(connString));

            var builder = new SqliteConnectionStringBuilder(connString);
            Assert.Equal(Path.GetFullPath(env.DbPath), Path.GetFullPath(builder.DataSource));
            Assert.NotEqual(SqliteCacheMode.Shared, builder.Cache);
            Assert.Equal(5, builder.DefaultTimeout);
        }

        [Fact]
        public async Task ApplicationDbContext_AlAbrirConexion_HabilitaWalForeignKeysYBusyTimeout()
        {
            using var env = new TestEnvironmentFixture();
            await DatabaseMaintenanceHelper.InicializarYMigrarBaseDatosAsync();

            using var db = new ApplicationDbContext();
            await db.Database.OpenConnectionAsync();
            var conn = db.Database.GetDbConnection();

            string? journalMode = await EjecutarScalarStringAsync(conn, "PRAGMA journal_mode;");
            long foreignKeys = await EjecutarScalarLongAsync(conn, "PRAGMA foreign_keys;");
            long busyTimeout = await EjecutarScalarLongAsync(conn, "PRAGMA busy_timeout;");

            Assert.Equal("wal", journalMode?.Trim(), ignoreCase: true);
            Assert.Equal(1, foreignKeys);
            Assert.True(busyTimeout >= 5000, $"Se esperaba busy_timeout >= 5000 y fue {busyTimeout}");
        }

        [Fact]
        public async Task ApplicationDbContext_ForeignKeysActivas_RechazanReferenciasHuerfanasYProtegenRestrict()
        {
            using var env = new TestEnvironmentFixture();
            await DatabaseMaintenanceHelper.InicializarYMigrarBaseDatosAsync(env.CrearFabricaContexto());

            using (var db = env.CrearContexto())
            {
                db.Categorias.Add(new Categoria { Nombre = "Huérfana", ID_Carrera = 9999 });
                await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
            }

            using (var db = env.CrearContexto())
            {
                db.Hits.Add(new Hit { Nombre_Hit = "Hit Huérfano", ID_Carrera = 9999 });
                await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
            }

            // Crear datos válidos y probar DeleteBehavior.Restrict en Hit/Categoria con Corredor
            int idCategoria;
            int idHit;
            using (var db = env.CrearContexto())
            {
                var carrera = new Carrera { Nombre = "Carrera FK", Fecha = DateTime.Today };
                db.Carreras.Add(carrera);
                await db.SaveChangesAsync();

                var cat = new Categoria { Nombre = "General", ID_Carrera = carrera.ID_Carrera };
                var hit = new Hit { Nombre_Hit = "Hit 1", ID_Carrera = carrera.ID_Carrera };
                db.Categorias.Add(cat);
                db.Hits.Add(hit);
                await db.SaveChangesAsync();

                idCategoria = cat.ID_Categoria;
                idHit = hit.ID_Hit;

                db.Corredores.Add(new Corredor
                {
                    Dorsal = 10,
                    Nombre = "Corredor Test",
                    ID_Carrera = carrera.ID_Carrera,
                    ID_Categoria = idCategoria,
                    ID_Hit = idHit,
                    Estado = EstadoCorredor.Activo
                });
                await db.SaveChangesAsync();
            }

            using (var db = env.CrearContexto())
            {
                var hit = await db.Hits.FindAsync(idHit);
                Assert.NotNull(hit);
                db.Hits.Remove(hit!);
                await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
            }

            using (var db = env.CrearContexto())
            {
                var cat = await db.Categorias.FindAsync(idCategoria);
                Assert.NotNull(cat);
                db.Categorias.Remove(cat!);
                await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
            }
        }

        [Fact]
        public async Task ApplicationDbContext_EsquemaNuevo_CreaTodosLosIndicesRequeridos()
        {
            using var env = new TestEnvironmentFixture();
            await DatabaseMaintenanceHelper.InicializarYMigrarBaseDatosAsync(env.CrearFabricaContexto());

            using var db = env.CrearContexto();
            await db.Database.OpenConnectionAsync();
            var indices = await ObtenerIndicesAsync(db.Database.GetDbConnection());

            Assert.Contains("IX_Corredores_ID_Hit", indices);
            Assert.Contains("IX_Corredores_ID_Categoria", indices);
            Assert.Contains("IX_Hits_ID_Carrera", indices);
            Assert.Contains("IX_Categorias_ID_Carrera", indices);
            Assert.Contains("IX_Corredores_ID_Carrera_Dorsal", indices);
            Assert.Contains("IX_Tiempos_ID_Corredor", indices);
        }

        [Fact]
        public async Task DatabaseMaintenanceHelper_MigracionDesdeBaseHeredada_EsIdempotenteYAgregaColumnasEIndices()
        {
            using var env = new TestEnvironmentFixture();

            // Construir manualmente una BD SQLite heredada sin Hora_Fin, RutaLogoIzquierdo, RutaLogoDerecho ni los 4 índices nuevos
            var csb = new SqliteConnectionStringBuilder
            {
                DataSource = env.DbPath,
                Mode = SqliteOpenMode.ReadWriteCreate
            };

            await using (var rawConn = new SqliteConnection(csb.ToString()))
            {
                await rawConn.OpenAsync();
                await using var cmd = rawConn.CreateCommand();
                cmd.CommandText = @"
                    PRAGMA journal_mode=DELETE;
                    CREATE TABLE Carreras (
                        ID_Carrera INTEGER PRIMARY KEY AUTOINCREMENT,
                        Nombre TEXT NOT NULL,
                        Fecha TEXT NOT NULL
                    );
                    CREATE TABLE Categorias (
                        ID_Categoria INTEGER PRIMARY KEY AUTOINCREMENT,
                        Nombre TEXT NOT NULL,
                        ID_Carrera INTEGER NOT NULL
                    );
                    CREATE TABLE Hits (
                        ID_Hit INTEGER PRIMARY KEY AUTOINCREMENT,
                        Nombre_Hit TEXT NOT NULL,
                        Hora_Inicio TEXT NULL,
                        ID_Carrera INTEGER NOT NULL
                    );
                    CREATE TABLE Corredores (
                        ID_Corredor INTEGER PRIMARY KEY AUTOINCREMENT,
                        Dorsal INTEGER NOT NULL,
                        Nombre TEXT NOT NULL,
                        ID_Categoria INTEGER NOT NULL,
                        ID_Hit INTEGER NOT NULL,
                        Estado INTEGER NOT NULL,
                        ID_Carrera INTEGER NOT NULL
                    );
                    CREATE TABLE Tiempos (
                        ID_Tiempo INTEGER PRIMARY KEY AUTOINCREMENT,
                        ID_Corredor INTEGER NOT NULL,
                        Hora_Llegada TEXT NOT NULL,
                        Tiempo_Neto TEXT NOT NULL,
                        Ruta_Foto TEXT NOT NULL
                    );
                    INSERT INTO Carreras (Nombre, Fecha) VALUES ('Carrera Heredada', '2026-01-01 08:00:00');
                    INSERT INTO Hits (Nombre_Hit, Hora_Inicio, ID_Carrera) VALUES ('Hit Heredado', NULL, 1);
                ";
                await cmd.ExecuteNonQueryAsync();
            }

            SqliteConnection.ClearAllPools();

            // Primera ejecución: migra columnas, índices y WAL
            await DatabaseMaintenanceHelper.InicializarYMigrarBaseDatosAsync(env.CrearFabricaContexto());
            // Segunda ejecución: debe ser completamente idempotente sin lanzar errores
            await DatabaseMaintenanceHelper.InicializarYMigrarBaseDatosAsync(env.CrearFabricaContexto());

            using var db = env.CrearContexto();
            await db.Database.OpenConnectionAsync();
            var conn = db.Database.GetDbConnection();

            var colsHits = await ObtenerColumnasAsync(conn, "Hits");
            var colsCarreras = await ObtenerColumnasAsync(conn, "Carreras");
            var indices = await ObtenerIndicesAsync(conn);
            string? journalMode = await EjecutarScalarStringAsync(conn, "PRAGMA journal_mode;");

            Assert.Contains("Hora_Fin", colsHits);
            Assert.Contains("RutaLogoIzquierdo", colsCarreras);
            Assert.Contains("RutaLogoDerecho", colsCarreras);
            Assert.Contains("IX_Corredores_ID_Hit", indices);
            Assert.Contains("IX_Corredores_ID_Categoria", indices);
            Assert.Contains("IX_Hits_ID_Carrera", indices);
            Assert.Contains("IX_Categorias_ID_Carrera", indices);
            Assert.Equal("wal", journalMode?.Trim(), ignoreCase: true);

            // Verificar que los datos heredados siguen intactos y se consultan vía EF Core
            var carreras = await db.Carreras.AsNoTracking().ToListAsync();
            var hits = await db.Hits.AsNoTracking().ToListAsync();
            Assert.Single(carreras);
            Assert.Equal("Carrera Heredada", carreras[0].Nombre);
            Assert.Null(carreras[0].RutaLogoIzquierdo);
            Assert.Null(carreras[0].RutaLogoDerecho);
            Assert.Single(hits);
            Assert.Equal("Hit Heredado", hits[0].Nombre_Hit);
            Assert.Null(hits[0].Hora_Fin);
        }

        [Fact]
        public async Task ApplicationDbContext_OpcionesExplicitas_NoModificaRutasGlobales()
        {
            string rutaAntes = StorageHelper.ObtenerRutaBaseDatos();
            using var env = new TestEnvironmentFixture(configurarStorageGlobal: false);

            await DatabaseMaintenanceHelper.InicializarYMigrarBaseDatosAsync(env.CrearFabricaContexto());

            using var db = env.CrearContexto();
            db.Carreras.Add(new Carrera { Nombre = "Aislada Sin Global", Fecha = DateTime.Today });
            await db.SaveChangesAsync();

            Assert.True(File.Exists(env.DbPath));
            Assert.Equal(rutaAntes, StorageHelper.ObtenerRutaBaseDatos());
        }

        private static async Task<string?> EjecutarScalarStringAsync(System.Data.Common.DbConnection conn, string sql)
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = sql;
            object? result = await cmd.ExecuteScalarAsync();
            return result?.ToString();
        }

        private static async Task<long> EjecutarScalarLongAsync(System.Data.Common.DbConnection conn, string sql)
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = sql;
            object? result = await cmd.ExecuteScalarAsync();
            return Convert.ToInt64(result);
        }

        private static async Task<HashSet<string>> ObtenerColumnasAsync(System.Data.Common.DbConnection conn, string tabla)
        {
            var columnas = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = $"PRAGMA table_info(\"{tabla}\");";
            await using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                columnas.Add(reader.GetString(1));
            }
            return columnas;
        }

        private static async Task<HashSet<string>> ObtenerIndicesAsync(System.Data.Common.DbConnection conn)
        {
            var indices = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT name FROM sqlite_master WHERE type = 'index' AND name NOT LIKE 'sqlite_%';";
            await using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                indices.Add(reader.GetString(0));
            }
            return indices;
        }
    }
}
