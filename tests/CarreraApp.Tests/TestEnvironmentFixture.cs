using System;
using System.IO;
using CarreraApp.Data;
using CarreraApp.Helpers;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace CarreraApp.Tests
{
    [CollectionDefinition("IsolatedStorage", DisableParallelization = true)]
    public class IsolatedStorageCollection : ICollectionFixture<IsolatedStorageMarker>
    {
    }

    public class IsolatedStorageMarker
    {
    }

    /// <summary>
    /// Fixture de entorno aislado por prueba: crea un directorio temporal único para SQLite y Storage
    /// de modo que ninguna prueba lea o modifique carrera.db ni Storage/ de producción.
    /// </summary>
    public sealed class TestEnvironmentFixture : IDisposable
    {
        private readonly bool _configurarStorageGlobal;
        private bool _disposed;

        public string RootDirectory { get; }
        public string DbPath { get; }
        public string StorageRoot { get; }
        public string FotosTempDir => Path.Combine(StorageRoot, "Fotos_Temp");
        public string FotosDefinitivasDir => Path.Combine(StorageRoot, "Fotos_Definitivas");
        public string LogosDir => Path.Combine(StorageRoot, "Logos");
        public string BackupsDir => Path.Combine(StorageRoot, "Backups");
        public DbContextOptions<ApplicationDbContext> DbOptions { get; }

        public TestEnvironmentFixture(bool configurarStorageGlobal = true)
        {
            _configurarStorageGlobal = configurarStorageGlobal;
            RootDirectory = Path.Combine(
                Path.GetTempPath(),
                "SistemaCarreraTests",
                Guid.NewGuid().ToString("N"));
            StorageRoot = Path.Combine(RootDirectory, "Storage");
            DbPath = Path.Combine(RootDirectory, "carrera.db");

            Directory.CreateDirectory(RootDirectory);
            Directory.CreateDirectory(StorageRoot);

            if (_configurarStorageGlobal)
            {
                StorageHelper.ConfigurarRutasParaPruebas(DbPath, StorageRoot);
            }

            DbOptions = ApplicationDbContext.CrearOpcionesParaRuta(DbPath);
        }

        public ApplicationDbContext CrearContexto()
        {
            return new ApplicationDbContext(DbOptions);
        }

        public Func<ApplicationDbContext> CrearFabricaContexto()
        {
            return CrearContexto;
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            SqliteConnection.ClearAllPools();

            if (_configurarStorageGlobal)
            {
                StorageHelper.RestablecerRutasPorDefecto();
            }

            try
            {
                if (Directory.Exists(RootDirectory))
                {
                    Directory.Delete(RootDirectory, recursive: true);
                }
            }
            catch
            {
                // Ignorar bloqueos transitorios del sistema de archivos al finalizar el test
            }
        }
    }
}
