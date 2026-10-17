using System;
using System.IO;

namespace CarreraApp.Helpers
{
    public static class StorageHelper
    {
        private static string? _storageRoot;
        private static string? _dbPath;

        public static string ObtenerRutaStorage()
        {
            if (_storageRoot != null) return _storageRoot;

            // 1. Detectar si estamos ejecutando desde bin/Debug o bin/Release en desarrollo:
            // La carpeta de fuentes src/CarreraApp/Storage está 3 niveles arriba de bin/Debug/net10.0-windows
            try
            {
                string rutaProyecto = Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", "Storage"));
                if (Directory.Exists(rutaProyecto))
                {
                    _storageRoot = rutaProyecto;
                    return _storageRoot;
                }
            }
            catch { }

            // 2. Si se ejecuta desde el directorio de trabajo (ej. 'dotnet run' desde src/CarreraApp)
            try
            {
                string rutaCurrent = Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), "Storage"));
                if (Directory.Exists(rutaCurrent))
                {
                    _storageRoot = rutaCurrent;
                    return _storageRoot;
                }
            }
            catch { }

            // 3. Carpeta Storage junto a los binarios ejecutables
            string rutaBase = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Storage");
            Directory.CreateDirectory(rutaBase);
            _storageRoot = rutaBase;
            return _storageRoot;
        }

        public static string ObtenerCarpetaFotosTemp()
        {
            string ruta = Path.Combine(ObtenerRutaStorage(), "Fotos_Temp");
            Directory.CreateDirectory(ruta);
            return ruta;
        }

        public static string ObtenerCarpetaFotosDefinitivas()
        {
            string ruta = Path.Combine(ObtenerRutaStorage(), "Fotos_Definitivas");
            Directory.CreateDirectory(ruta);
            return ruta;
        }

        public static string ObtenerCarpetaLogos()
        {
            string ruta = Path.Combine(ObtenerRutaStorage(), "Logos");
            Directory.CreateDirectory(ruta);
            return ruta;
        }

        public static string ObtenerRutaBaseDatos()
        {
            if (_dbPath != null) return _dbPath;

            try
            {
                string rutaProyectoDb = Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", "carrera.db"));
                if (File.Exists(rutaProyectoDb))
                {
                    _dbPath = rutaProyectoDb;
                    return _dbPath;
                }
            }
            catch { }

            try
            {
                string rutaCurrentDb = Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), "carrera.db"));
                if (File.Exists(rutaCurrentDb))
                {
                    _dbPath = rutaCurrentDb;
                    return _dbPath;
                }
            }
            catch { }

            _dbPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "carrera.db");
            return _dbPath;
        }
    }
}

