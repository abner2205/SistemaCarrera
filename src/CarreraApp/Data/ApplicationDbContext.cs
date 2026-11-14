// Path: src/CarreraApp/Data/ApplicationDbContext.cs
using System;
using System.Data.Common;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CarreraApp.Models;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;

namespace CarreraApp.Data
{
    public class ApplicationDbContext : DbContext
    {
        public ApplicationDbContext()
        {
        }

        public ApplicationDbContext(DbContextOptions<ApplicationDbContext> options)
            : base(options)
        {
        }

        public DbSet<Carrera> Carreras => Set<Carrera>();
        public DbSet<Categoria> Categorias => Set<Categoria>();
        public DbSet<Hit> Hits => Set<Hit>();
        public DbSet<Corredor> Corredores => Set<Corredor>();
        public DbSet<Tiempo> Tiempos => Set<Tiempo>();

        public static string ConstruirCadenaConexion(string dbPath)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(dbPath);

            var connectionStringBuilder = new SqliteConnectionStringBuilder
            {
                DataSource = Path.GetFullPath(dbPath),
                Mode = SqliteOpenMode.ReadWriteCreate,
                Cache = SqliteCacheMode.Default,
                DefaultTimeout = 5 // Espera hasta 5 segundos si otro hilo está escribiendo
            };

            return connectionStringBuilder.ToString();
        }

        public static void ConfigurarOpcionesSqlite(DbContextOptionsBuilder optionsBuilder, string dbPath)
        {
            ArgumentNullException.ThrowIfNull(optionsBuilder);

            optionsBuilder
                .UseSqlite(ConstruirCadenaConexion(dbPath))
                .AddInterceptors(SqliteConnectionPragmaInterceptor.Instance);
        }

        public static DbContextOptions<ApplicationDbContext> CrearOpcionesParaRuta(string dbPath)
        {
            var builder = new DbContextOptionsBuilder<ApplicationDbContext>();
            ConfigurarOpcionesSqlite(builder, dbPath);
            return builder.Options;
        }

        protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
        {
            if (!optionsBuilder.IsConfigured)
            {
                string dbPath = Helpers.StorageHelper.ObtenerRutaBaseDatos();
                ConfigurarOpcionesSqlite(optionsBuilder, dbPath);
            }
            else
            {
                var coreExt = optionsBuilder.Options.FindExtension<CoreOptionsExtension>();
                bool tieneInterceptor = coreExt?.Interceptors?.Any(i => i is SqliteConnectionPragmaInterceptor) == true;
                if (!tieneInterceptor)
                {
                    optionsBuilder.AddInterceptors(SqliteConnectionPragmaInterceptor.Instance);
                }
            }
        }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);

            // Llaves Primarias
            modelBuilder.Entity<Carrera>().HasKey(c => c.ID_Carrera);
            modelBuilder.Entity<Categoria>().HasKey(c => c.ID_Categoria);
            modelBuilder.Entity<Hit>().HasKey(h => h.ID_Hit);
            modelBuilder.Entity<Corredor>().HasKey(c => c.ID_Corredor);
            modelBuilder.Entity<Tiempo>().HasKey(t => t.ID_Tiempo);

            // Relaciones e Integridad Referencial
            modelBuilder.Entity<Categoria>()
                .HasOne<Carrera>()
                .WithMany()
                .HasForeignKey(c => c.ID_Carrera)
                .OnDelete(DeleteBehavior.Cascade);

            modelBuilder.Entity<Hit>()
                .HasOne<Carrera>()
                .WithMany()
                .HasForeignKey(h => h.ID_Carrera)
                .OnDelete(DeleteBehavior.Cascade);

            modelBuilder.Entity<Corredor>()
                .HasOne(c => c.Categoria)
                .WithMany()
                .HasForeignKey(c => c.ID_Categoria)
                .OnDelete(DeleteBehavior.Restrict);

            modelBuilder.Entity<Corredor>()
                .HasOne(c => c.Hit)
                .WithMany()
                .HasForeignKey(c => c.ID_Hit)
                .OnDelete(DeleteBehavior.Restrict);

            modelBuilder.Entity<Corredor>()
                .HasOne(c => c.Carrera)
                .WithMany()
                .HasForeignKey(c => c.ID_Carrera)
                .OnDelete(DeleteBehavior.Cascade);

            modelBuilder.Entity<Tiempo>()
                .HasOne(t => t.Corredor)
                .WithMany()
                .HasForeignKey(t => t.ID_Corredor)
                .OnDelete(DeleteBehavior.Cascade);

            // ÍNDICES Y RESTRICCIONES ÚNICAS
            modelBuilder.Entity<Tiempo>()
                .HasIndex(t => t.ID_Corredor)
                .HasDatabaseName("IX_Tiempos_ID_Corredor")
                .IsUnique();

            modelBuilder.Entity<Corredor>()
                .HasIndex(c => new { c.ID_Carrera, c.Dorsal })
                .HasDatabaseName("IX_Corredores_ID_Carrera_Dorsal")
                .IsUnique();

            modelBuilder.Entity<Corredor>()
                .HasIndex(c => c.ID_Hit)
                .HasDatabaseName("IX_Corredores_ID_Hit");

            modelBuilder.Entity<Corredor>()
                .HasIndex(c => c.ID_Categoria)
                .HasDatabaseName("IX_Corredores_ID_Categoria");

            modelBuilder.Entity<Hit>()
                .HasIndex(h => h.ID_Carrera)
                .HasDatabaseName("IX_Hits_ID_Carrera");

            modelBuilder.Entity<Categoria>()
                .HasIndex(c => c.ID_Carrera)
                .HasDatabaseName("IX_Categorias_ID_Carrera");
        }
    }

    internal sealed class SqliteConnectionPragmaInterceptor : DbConnectionInterceptor
    {
        public static readonly SqliteConnectionPragmaInterceptor Instance = new();

        private SqliteConnectionPragmaInterceptor()
        {
        }

        public override void ConnectionOpened(DbConnection connection, ConnectionEndEventData eventData)
        {
            base.ConnectionOpened(connection, eventData);
            AplicarPragmas(connection);
        }

        public override async Task ConnectionOpenedAsync(
            DbConnection connection,
            ConnectionEndEventData eventData,
            CancellationToken cancellationToken = default)
        {
            await base.ConnectionOpenedAsync(connection, eventData, cancellationToken).ConfigureAwait(false);
            await AplicarPragmasAsync(connection, cancellationToken).ConfigureAwait(false);
        }

        private static void AplicarPragmas(DbConnection connection)
        {
            if (connection is not SqliteConnection sqliteConnection)
            {
                return;
            }

            using (var cmd = sqliteConnection.CreateCommand())
            {
                cmd.CommandText = "PRAGMA busy_timeout=5000; PRAGMA foreign_keys=ON;";
                cmd.ExecuteNonQuery();
            }

            if (PuedeConfigurarWal(sqliteConnection))
            {
                using var checkCmd = sqliteConnection.CreateCommand();
                checkCmd.CommandText = "PRAGMA journal_mode;";
                string? journalActual = checkCmd.ExecuteScalar()?.ToString();
                if (!string.Equals(journalActual, "wal", StringComparison.OrdinalIgnoreCase))
                {
                    using var walCmd = sqliteConnection.CreateCommand();
                    walCmd.CommandText = "PRAGMA journal_mode=WAL;";
                    walCmd.ExecuteNonQuery();
                }
            }
        }

        private static async Task AplicarPragmasAsync(DbConnection connection, CancellationToken cancellationToken)
        {
            if (connection is not SqliteConnection sqliteConnection)
            {
                return;
            }

            await using (var cmd = sqliteConnection.CreateCommand())
            {
                cmd.CommandText = "PRAGMA busy_timeout=5000; PRAGMA foreign_keys=ON;";
                await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            if (PuedeConfigurarWal(sqliteConnection))
            {
                await using var checkCmd = sqliteConnection.CreateCommand();
                checkCmd.CommandText = "PRAGMA journal_mode;";
                object? scalar = await checkCmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
                string? journalActual = scalar?.ToString();
                if (!string.Equals(journalActual, "wal", StringComparison.OrdinalIgnoreCase))
                {
                    await using var walCmd = sqliteConnection.CreateCommand();
                    walCmd.CommandText = "PRAGMA journal_mode=WAL;";
                    await walCmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                }
            }
        }

        private static bool PuedeConfigurarWal(SqliteConnection connection)
        {
            string ds = connection.DataSource ?? string.Empty;
            if (string.IsNullOrWhiteSpace(ds) ||
                ds.Equals(":memory:", StringComparison.OrdinalIgnoreCase) ||
                ds.StartsWith("file::memory:", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            if (!string.IsNullOrWhiteSpace(connection.ConnectionString))
            {
                var csb = new SqliteConnectionStringBuilder(connection.ConnectionString);
                if (csb.Mode == SqliteOpenMode.Memory || csb.Mode == SqliteOpenMode.ReadOnly)
                {
                    return false;
                }
            }

            return true;
        }
    }
}
