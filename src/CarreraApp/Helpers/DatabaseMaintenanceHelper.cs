using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CarreraApp.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace CarreraApp.Helpers
{
    public sealed record RespaldoResultado(
        bool Exito,
        string Mensaje,
        string? RutaRespaldo = null,
        Exception? Error = null,
        bool OmitidoPorInexistencia = false);

    public static class DatabaseMaintenanceHelper
    {
        private static long _secuenciaRespaldo;
        private static readonly SemaphoreSlim _lockRespaldo = new(1, 1);

        private static readonly HashSet<string> TablasPermitidas = new(StringComparer.OrdinalIgnoreCase)
        {
            "Carreras",
            "Categorias",
            "Hits",
            "Corredores",
            "Tiempos"
        };

        /// <summary>
        /// Inicializa el esquema si no existe, aplica modo WAL/FK/busy_timeout y ejecuta migraciones
        /// idempotentes de columnas e índices sobre bases de datos SQLite heredadas.
        /// </summary>
        public static async Task InicializarYMigrarBaseDatosAsync(
            Func<ApplicationDbContext>? contextFactory = null,
            CancellationToken cancellationToken = default)
        {
            var factory = contextFactory ?? (() => new ApplicationDbContext());

            using var context = factory();
            await context.Database.EnsureCreatedAsync(cancellationToken).ConfigureAwait(false);

            await context.Database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                DbConnection connection = context.Database.GetDbConnection();

                await EjecutarComandoAsync(
                    connection,
                    "PRAGMA busy_timeout=5000; PRAGMA foreign_keys=ON;",
                    cancellationToken).ConfigureAwait(false);

                if (connection is SqliteConnection sqliteConnection &&
                    !string.IsNullOrWhiteSpace(sqliteConnection.DataSource) &&
                    !sqliteConnection.DataSource.Equals(":memory:", StringComparison.OrdinalIgnoreCase))
                {
                    await EjecutarComandoAsync(
                        connection,
                        "PRAGMA journal_mode=WAL;",
                        cancellationToken).ConfigureAwait(false);
                }

                // Migraciones idempotentes mediante inspección previa con PRAGMA table_info
                HashSet<string> columnasHits = await ObtenerColumnasTablaAsync(connection, "Hits", cancellationToken)
                    .ConfigureAwait(false);
                if (!columnasHits.Contains("Hora_Fin"))
                {
                    await EjecutarComandoAsync(
                        connection,
                        "ALTER TABLE Hits ADD COLUMN Hora_Fin TEXT NULL;",
                        cancellationToken).ConfigureAwait(false);
                }

                HashSet<string> columnasCarreras = await ObtenerColumnasTablaAsync(connection, "Carreras", cancellationToken)
                    .ConfigureAwait(false);
                if (!columnasCarreras.Contains("RutaLogoIzquierdo"))
                {
                    await EjecutarComandoAsync(
                        connection,
                        "ALTER TABLE Carreras ADD COLUMN RutaLogoIzquierdo TEXT NULL;",
                        cancellationToken).ConfigureAwait(false);
                }

                if (!columnasCarreras.Contains("RutaLogoDerecho"))
                {
                    await EjecutarComandoAsync(
                        connection,
                        "ALTER TABLE Carreras ADD COLUMN RutaLogoDerecho TEXT NULL;",
                        cancellationToken).ConfigureAwait(false);
                }

                // Creación idempotente de índices requeridos en esquemas heredados
                HashSet<string> indicesExistentes = await ObtenerIndicesExistentesAsync(connection, cancellationToken)
                    .ConfigureAwait(false);

                var indicesRequeridos = new (string Nombre, string Sql)[]
                {
                    ("IX_Corredores_ID_Hit", "CREATE INDEX IF NOT EXISTS \"IX_Corredores_ID_Hit\" ON \"Corredores\" (\"ID_Hit\");"),
                    ("IX_Corredores_ID_Categoria", "CREATE INDEX IF NOT EXISTS \"IX_Corredores_ID_Categoria\" ON \"Corredores\" (\"ID_Categoria\");"),
                    ("IX_Hits_ID_Carrera", "CREATE INDEX IF NOT EXISTS \"IX_Hits_ID_Carrera\" ON \"Hits\" (\"ID_Carrera\");"),
                    ("IX_Categorias_ID_Carrera", "CREATE INDEX IF NOT EXISTS \"IX_Categorias_ID_Carrera\" ON \"Categorias\" (\"ID_Carrera\");")
                };

                foreach (var (nombre, sql) in indicesRequeridos)
                {
                    if (!indicesExistentes.Contains(nombre))
                    {
                        await EjecutarComandoAsync(connection, sql, cancellationToken).ConfigureAwait(false);
                    }
                }
            }
            finally
            {
                await context.Database.CloseConnectionAsync().ConfigureAwait(false);
            }
        }

        private static async Task<HashSet<string>> ObtenerColumnasTablaAsync(
            DbConnection connection,
            string nombreTabla,
            CancellationToken cancellationToken)
        {
            if (!TablasPermitidas.Contains(nombreTabla))
            {
                throw new ArgumentException($"Tabla no permitida para inspección: {nombreTabla}", nameof(nombreTabla));
            }

            var columnas = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            await using var cmd = connection.CreateCommand();
            cmd.CommandText = $"PRAGMA table_info(\"{nombreTabla}\");";
            await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                if (!reader.IsDBNull(1))
                {
                    columnas.Add(reader.GetString(1));
                }
            }

            return columnas;
        }

        private static async Task<HashSet<string>> ObtenerIndicesExistentesAsync(
            DbConnection connection,
            CancellationToken cancellationToken)
        {
            var indices = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            await using var cmd = connection.CreateCommand();
            cmd.CommandText = "SELECT name FROM sqlite_master WHERE type = 'index';";
            await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                if (!reader.IsDBNull(0))
                {
                    indices.Add(reader.GetString(0));
                }
            }

            return indices;
        }

        private static async Task EjecutarComandoAsync(
            DbConnection connection,
            string sql,
            CancellationToken cancellationToken)
        {
            await using var cmd = connection.CreateCommand();
            cmd.CommandText = sql;
            await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        /// <summary>
        /// Genera un respaldo consistente de carrera.db compatible con modo WAL usando SqliteConnection.BackupDatabase,
        /// rota automáticamente conservando como máximo <paramref name="maxRespaldos"/> archivos históricos
        /// y retorna un resultado estructurado sin lanzar excepciones no controladas si falla la copia.
        /// </summary>
        public static async Task<RespaldoResultado> RespaldarBaseDatosConsistentementeAsync(
            string? dbPath = null,
            string? carpetaRespaldos = null,
            int maxRespaldos = 10,
            CancellationToken cancellationToken = default)
        {
            bool lockAdquirido = false;
            try
            {
                await _lockRespaldo.WaitAsync(cancellationToken).ConfigureAwait(false);
                lockAdquirido = true;

                string rutaOrigen = string.IsNullOrWhiteSpace(dbPath)
                    ? StorageHelper.ObtenerRutaBaseDatos()
                    : Path.GetFullPath(dbPath);

                if (!File.Exists(rutaOrigen))
                {
                    return new RespaldoResultado(
                        Exito: true,
                        Mensaje: "No existe base de datos previa para respaldar.",
                        OmitidoPorInexistencia: true);
                }

                string dirRespaldos = string.IsNullOrWhiteSpace(carpetaRespaldos)
                    ? StorageHelper.ObtenerCarpetaRespaldos()
                    : Path.GetFullPath(carpetaRespaldos);

                Directory.CreateDirectory(dirRespaldos);

                int limiteRetencion = Math.Max(1, maxRespaldos);
                long secuencia = Interlocked.Increment(ref _secuenciaRespaldo);
                string marcaTiempo = DateTime.UtcNow.ToString("yyyyMMdd_HHmmss_fffffff", CultureInfo.InvariantCulture);
                string nombreRespaldo = $"carrera_backup_{marcaTiempo}_{secuencia:D4}.db";
                string rutaFinal = Path.Combine(dirRespaldos, nombreRespaldo);
                string rutaTemporal = Path.Combine(dirRespaldos, $".backup_tmp_{Guid.NewGuid():N}.db");

                await Task.Run(() =>
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    var origenCsb = new SqliteConnectionStringBuilder
                    {
                        DataSource = rutaOrigen,
                        Mode = SqliteOpenMode.ReadWrite,
                        DefaultTimeout = 5,
                        Pooling = false
                    };

                    var destinoCsb = new SqliteConnectionStringBuilder
                    {
                        DataSource = rutaTemporal,
                        Mode = SqliteOpenMode.ReadWriteCreate,
                        DefaultTimeout = 5,
                        Pooling = false
                    };

                    try
                    {
                        using (var origenConn = new SqliteConnection(origenCsb.ToString()))
                        using (var destinoConn = new SqliteConnection(destinoCsb.ToString()))
                        {
                            origenConn.Open();
                            using (var pragmaCmd = origenConn.CreateCommand())
                            {
                                pragmaCmd.CommandText = "PRAGMA busy_timeout=5000;";
                                pragmaCmd.ExecuteNonQuery();
                            }

                            destinoConn.Open();
                            origenConn.BackupDatabase(destinoConn);
                        }

                        File.Move(rutaTemporal, rutaFinal, overwrite: true);
                    }
                    finally
                    {
                        EliminarArchivoSilencioso(rutaTemporal);
                        EliminarArchivoSilencioso(rutaTemporal + "-wal");
                        EliminarArchivoSilencioso(rutaTemporal + "-shm");
                    }

                    RotarRespaldosAntiguos(dirRespaldos, limiteRetencion);
                }, cancellationToken).ConfigureAwait(false);

                return new RespaldoResultado(
                    Exito: true,
                    Mensaje: $"Respaldo generado correctamente en '{rutaFinal}'.",
                    RutaRespaldo: rutaFinal);
            }
            catch (Exception ex)
            {
                Trace.TraceError($"[DatabaseMaintenanceHelper] Error al generar respaldo de SQLite: {ex}");
                return new RespaldoResultado(
                    Exito: false,
                    Mensaje: $"No se pudo generar el respaldo de la base de datos: {ex.Message}",
                    Error: ex);
            }
            finally
            {
                if (lockAdquirido)
                {
                    _lockRespaldo.Release();
                }
            }
        }

        private static void RotarRespaldosAntiguos(string dirRespaldos, int limiteRetencion)
        {
            var dirInfo = new DirectoryInfo(dirRespaldos);
            var respaldos = dirInfo
                .EnumerateFiles("*.db", SearchOption.TopDirectoryOnly)
                .OrderByDescending(f => f.LastWriteTimeUtc)
                .ThenByDescending(f => f.Name, StringComparer.Ordinal)
                .ToList();

            foreach (FileInfo excedente in respaldos.Skip(limiteRetencion))
            {
                try
                {
                    excedente.Delete();
                }
                catch (IOException ex)
                {
                    Trace.TraceWarning($"[DatabaseMaintenanceHelper] No se pudo eliminar respaldo antiguo '{excedente.FullName}': {ex.Message}");
                }
                catch (UnauthorizedAccessException ex)
                {
                    Trace.TraceWarning($"[DatabaseMaintenanceHelper] Sin permisos para eliminar respaldo antiguo '{excedente.FullName}': {ex.Message}");
                }
            }
        }

        private static void EliminarArchivoSilencioso(string ruta)
        {
            try
            {
                if (File.Exists(ruta))
                {
                    File.Delete(ruta);
                }
            }
            catch
            {
                // Ignorar fallos al limpiar temporales fallidos
            }
        }
    }
}
