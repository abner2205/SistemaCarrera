using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace CarreraApp.Helpers
{
    public static class StorageHelper
    {
        private const int MaxLongitudRutaDefinitiva = 239;

        private static readonly object _syncLock = new();
        private static string? _storageRoot;
        private static string? _dbPath;
        private static bool _usandoRutasDePruebas;

        private static readonly HashSet<string> ExtensionesImagenPermitidas = new(StringComparer.OrdinalIgnoreCase)
        {
            ".jpg",
            ".jpeg",
            ".png",
            ".webp"
        };

        private static readonly HashSet<string> NombresReservadosWindows = new(StringComparer.OrdinalIgnoreCase)
        {
            "CON", "PRN", "AUX", "NUL",
            "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
            "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"
        };

        private static readonly HashSet<char> CaracteresInvalidosSegmento = ConstruirCaracteresInvalidos();

        public static bool UsandoRutasDePruebas
        {
            get
            {
                lock (_syncLock)
                {
                    return _usandoRutasDePruebas;
                }
            }
        }

        /// <summary>
        /// Redirige de forma thread-safe las rutas de base de datos y almacenamiento hacia un directorio aislado.
        /// Pensado para pruebas automatizadas sin tocar carrera.db ni Storage/ de producción.
        /// </summary>
        public static void ConfigurarRutasParaPruebas(string dbPath, string storageRoot)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(dbPath);
            ArgumentException.ThrowIfNullOrWhiteSpace(storageRoot);

            string rutaDbCompleta = Path.GetFullPath(dbPath);
            string rutaStorageCompleta = Path.GetFullPath(storageRoot);

            string? directorioDb = Path.GetDirectoryName(rutaDbCompleta);
            if (!string.IsNullOrEmpty(directorioDb))
            {
                Directory.CreateDirectory(directorioDb);
            }

            Directory.CreateDirectory(rutaStorageCompleta);

            lock (_syncLock)
            {
                _dbPath = rutaDbCompleta;
                _storageRoot = rutaStorageCompleta;
                _usandoRutasDePruebas = true;
            }
        }

        /// <summary>
        /// Restablece la resolución automática de rutas por defecto (entorno de ejecución normal).
        /// </summary>
        public static void RestablecerRutasPorDefecto()
        {
            lock (_syncLock)
            {
                _dbPath = null;
                _storageRoot = null;
                _usandoRutasDePruebas = false;
            }
        }

        public static string ObtenerRutaStorage()
        {
            lock (_syncLock)
            {
                if (_storageRoot != null)
                {
                    return _storageRoot;
                }

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
                catch
                {
                    // Continuar con las rutas alternativas
                }

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
                catch
                {
                    // Continuar con la ruta junto a binarios
                }

                // 3. Carpeta Storage junto a los binarios ejecutables
                string rutaBase = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Storage");
                Directory.CreateDirectory(rutaBase);
                _storageRoot = rutaBase;
                return _storageRoot;
            }
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

        public static string ObtenerCarpetaRespaldos()
        {
            string ruta = Path.Combine(ObtenerRutaStorage(), "Backups");
            Directory.CreateDirectory(ruta);
            return ruta;
        }

        public static string ObtenerRutaBaseDatos()
        {
            lock (_syncLock)
            {
                if (_dbPath != null)
                {
                    return _dbPath;
                }

                try
                {
                    string rutaProyectoDb = Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", "carrera.db"));
                    if (File.Exists(rutaProyectoDb))
                    {
                        _dbPath = rutaProyectoDb;
                        return _dbPath;
                    }
                }
                catch
                {
                    // Continuar con las rutas alternativas
                }

                try
                {
                    string rutaCurrentDb = Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), "carrera.db"));
                    if (File.Exists(rutaCurrentDb))
                    {
                        _dbPath = rutaCurrentDb;
                        return _dbPath;
                    }
                }
                catch
                {
                    // Continuar con la ruta junto a binarios
                }

                _dbPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "carrera.db");
                return _dbPath;
            }
        }

        /// <summary>
        /// Sanitiza un segmento individual de ruta o nombre de archivo neutralizando secuencias de recorrido
        /// (., ..), separadores (/ y \), caracteres inválidos, nombres reservados de Windows y acotando la longitud.
        /// </summary>
        public static string SanitizarSegmentoRuta(string? valor, string fallback = "Sin_Nombre", int maxLength = 50)
        {
            int limite = Math.Max(1, maxLength);
            string fallbackSeguro = SanitizarSegmentoBase(fallback) ?? "Sin_Nombre";
            string limpio = SanitizarSegmentoBase(valor) ?? fallbackSeguro;
            return AjustarLongitudYReservado(limpio, limite, fallbackSeguro);
        }

        /// <summary>
        /// Construye una ruta segura dentro de Fotos_Definitivas, sanitizando segmentos, garantizando
        /// longitud total estrictamente menor a 240 caracteres y resolviendo colisiones sin sobreescribir archivos.
        /// </summary>
        public static string ConstruirRutaFotoDefinitivaSegura(
            string? nombreCarrera,
            string? nombreCategoria,
            string? nombreCorredor,
            int dorsal,
            string? tiempoParaArchivo,
            string extension = ".jpg",
            bool crearDirectorio = true)
        {
            string baseDir = Path.GetFullPath(ObtenerCarpetaFotosDefinitivas())
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            string basePrefix = baseDir + Path.DirectorySeparatorChar;

            string tiempoLimpio = (tiempoParaArchivo ?? "00-00-00").Replace(':', '-');
            string safeTiempo = SanitizarSegmentoRuta(tiempoLimpio, "00-00-00", 20);
            string safeExtension = NormalizarExtensionImagen(extension);
            string dorsalTexto = dorsal >= 0
                ? dorsal.ToString(CultureInfo.InvariantCulture)
                : SanitizarSegmentoRuta(dorsal.ToString(CultureInfo.InvariantCulture), "0", 12);

            string safeCarrera = SanitizarSegmentoRuta(nombreCarrera, "Sin_Carrera", 50);
            string safeCategoria = SanitizarSegmentoRuta(nombreCategoria, "General", 50);
            string safeCorredor = SanitizarSegmentoRuta(nombreCorredor, "Corredor", 50);

            const int separadoresYGuiones = 5; // \carrera\categoria\corredor_dorsal_tiempo
            const int reservaColision = 6;     // hasta _99999
            int overheadFijo = baseDir.Length + separadoresYGuiones + dorsalTexto.Length + safeTiempo.Length + safeExtension.Length;
            int maxTresSegmentos = MaxLongitudRutaDefinitiva - overheadFijo - reservaColision;

            if (maxTresSegmentos < 3)
            {
                safeTiempo = SanitizarSegmentoRuta(tiempoLimpio, "0", 8);
                overheadFijo = baseDir.Length + separadoresYGuiones + dorsalTexto.Length + safeTiempo.Length + safeExtension.Length;
                maxTresSegmentos = MaxLongitudRutaDefinitiva - overheadFijo - 3;
                if (maxTresSegmentos < 3)
                {
                    throw new PathTooLongException(
                        $"La ruta raíz '{baseDir}' no deja espacio seguro menor a {MaxLongitudRutaDefinitiva + 1} caracteres.");
                }
            }

            while (safeCarrera.Length + safeCategoria.Length + safeCorredor.Length > maxTresSegmentos)
            {
                int exceso = (safeCarrera.Length + safeCategoria.Length + safeCorredor.Length) - maxTresSegmentos;
                int recorte = Math.Max(1, exceso / 3);

                if (safeCorredor.Length >= safeCarrera.Length && safeCorredor.Length >= safeCategoria.Length && safeCorredor.Length > 1)
                {
                    int nuevoLimite = Math.Max(1, safeCorredor.Length - recorte);
                    safeCorredor = SanitizarSegmentoRuta(safeCorredor, "C", nuevoLimite);
                }
                else if (safeCarrera.Length >= safeCategoria.Length && safeCarrera.Length > 1)
                {
                    int nuevoLimite = Math.Max(1, safeCarrera.Length - recorte);
                    safeCarrera = SanitizarSegmentoRuta(safeCarrera, "R", nuevoLimite);
                }
                else if (safeCategoria.Length > 1)
                {
                    int nuevoLimite = Math.Max(1, safeCategoria.Length - recorte);
                    safeCategoria = SanitizarSegmentoRuta(safeCategoria, "G", nuevoLimite);
                }
                else
                {
                    break;
                }
            }

            string dirDefinitivo = Path.GetFullPath(Path.Combine(baseDir, safeCarrera, safeCategoria));
            if (!dirDefinitivo.StartsWith(basePrefix, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("El directorio definitivo generado excede el almacenamiento permitido.");
            }

            if (crearDirectorio)
            {
                Directory.CreateDirectory(dirDefinitivo);
            }

            string prefijoArchivo = $"{safeCorredor}_{dorsalTexto}_{safeTiempo}";
            string rutaCandidata = Path.Combine(dirDefinitivo, prefijoArchivo + safeExtension);

            if (File.Exists(rutaCandidata) || Directory.Exists(rutaCandidata))
            {
                int sufijo = 1;
                while (true)
                {
                    string textoSufijo = $"_{sufijo}";
                    int longitudProyectada = dirDefinitivo.Length + 1 + prefijoArchivo.Length + textoSufijo.Length + safeExtension.Length;
                    string prefijoAjustado = prefijoArchivo;

                    if (longitudProyectada > MaxLongitudRutaDefinitiva)
                    {
                        int excesoLongitud = longitudProyectada - MaxLongitudRutaDefinitiva;
                        int maxCorredorAjustado = Math.Max(1, safeCorredor.Length - excesoLongitud);
                        string corredorCorto = SanitizarSegmentoRuta(safeCorredor, "C", maxCorredorAjustado);
                        prefijoAjustado = $"{corredorCorto}_{dorsalTexto}_{safeTiempo}";
                    }

                    rutaCandidata = Path.Combine(dirDefinitivo, $"{prefijoAjustado}{textoSufijo}{safeExtension}");
                    if (!File.Exists(rutaCandidata) && !Directory.Exists(rutaCandidata))
                    {
                        break;
                    }

                    sufijo++;
                }
            }

            string rutaFinal = Path.GetFullPath(rutaCandidata);
            if (!rutaFinal.StartsWith(basePrefix, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("La ruta definitiva generada excede el almacenamiento permitido.");
            }

            if (rutaFinal.Length > MaxLongitudRutaDefinitiva)
            {
                throw new PathTooLongException(
                    $"La ruta definitiva '{rutaFinal}' ({rutaFinal.Length} caracteres) excede el límite seguro (< 240).");
            }

            return rutaFinal;
        }

        /// <summary>
        /// Elimina archivos de imagen huérfanos (.jpg, .jpeg, .png, .webp) en Fotos_Temp que no estén en la lista
        /// de archivos activos, preservando .gitkeep y sin alterar jamás Fotos_Definitivas.
        /// </summary>
        public static int LimpiarFotosTempHuerfanas(IEnumerable<string>? archivosActivos = null)
        {
            string carpetaTemp = Path.GetFullPath(ObtenerCarpetaFotosTemp());
            if (!Directory.Exists(carpetaTemp))
            {
                return 0;
            }

            var rutasActivas = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var nombresActivos = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            if (archivosActivos != null)
            {
                foreach (string? item in archivosActivos)
                {
                    if (string.IsNullOrWhiteSpace(item))
                    {
                        continue;
                    }

                    string entrada = item.Trim();
                    if (Path.IsPathRooted(entrada) ||
                        entrada.Contains(Path.DirectorySeparatorChar) ||
                        entrada.Contains(Path.AltDirectorySeparatorChar))
                    {
                        try
                        {
                            rutasActivas.Add(Path.GetFullPath(entrada));
                        }
                        catch
                        {
                            // Ignorar entradas malformadas en el listado de activos
                        }
                    }
                    else
                    {
                        nombresActivos.Add(entrada);
                    }
                }
            }

            int eliminados = 0;
            foreach (string archivo in Directory.EnumerateFiles(carpetaTemp, "*", SearchOption.TopDirectoryOnly))
            {
                string nombreArchivo = Path.GetFileName(archivo);
                if (string.Equals(nombreArchivo, ".gitkeep", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                string extension = Path.GetExtension(archivo);
                if (!ExtensionesImagenPermitidas.Contains(extension))
                {
                    continue;
                }

                string rutaCompleta = Path.GetFullPath(archivo);
                if (rutasActivas.Contains(rutaCompleta) || nombresActivos.Contains(nombreArchivo))
                {
                    continue;
                }

                try
                {
                    File.Delete(rutaCompleta);
                    eliminados++;
                }
                catch (IOException)
                {
                    // Tolerar archivo temporal en uso transitorio
                }
                catch (UnauthorizedAccessException)
                {
                    // Tolerar archivo sin permisos de borrado
                }
            }

            return eliminados;
        }

        private static HashSet<char> ConstruirCaracteresInvalidos()
        {
            var set = new HashSet<char>(Path.GetInvalidFileNameChars());
            foreach (char c in Path.GetInvalidPathChars())
            {
                set.Add(c);
            }

            set.Add('/');
            set.Add('\\');
            set.Add(':');
            set.Add('*');
            set.Add('?');
            set.Add('"');
            set.Add('<');
            set.Add('>');
            set.Add('|');
            return set;
        }

        private static string? SanitizarSegmentoBase(string? valor)
        {
            if (string.IsNullOrWhiteSpace(valor))
            {
                return null;
            }

            string[] partes = valor.Split(
                new[] { '/', '\\' },
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

            var segmentosValidos = new List<string>(partes.Length);
            foreach (string parte in partes)
            {
                if (parte == "." || parte == "..")
                {
                    continue;
                }

                segmentosValidos.Add(parte);
            }

            if (segmentosValidos.Count == 0)
            {
                return null;
            }

            string unido = string.Join("_", segmentosValidos);
            var sb = new StringBuilder(unido.Length);
            foreach (char c in unido)
            {
                if (CaracteresInvalidosSegmento.Contains(c) || char.IsWhiteSpace(c) || char.IsControl(c))
                {
                    sb.Append('_');
                }
                else
                {
                    sb.Append(c);
                }
            }

            string sinPuntosMultiples = Regex.Replace(sb.ToString(), @"\.{2,}", "_");
            string colapsado = Regex.Replace(sinPuntosMultiples, @"_+", "_").Trim('_', '.');

            return string.IsNullOrWhiteSpace(colapsado) ? null : colapsado;
        }

        private static string AjustarLongitudYReservado(string segmento, int maxLength, string fallbackSeguro)
        {
            string resultado = segmento;
            if (resultado.Length > maxLength)
            {
                resultado = resultado.Substring(0, maxLength).Trim('_', '.');
            }

            if (string.IsNullOrEmpty(resultado))
            {
                resultado = fallbackSeguro.Length > maxLength
                    ? fallbackSeguro.Substring(0, maxLength).Trim('_', '.')
                    : fallbackSeguro;
                if (string.IsNullOrEmpty(resultado))
                {
                    resultado = "X";
                }
            }

            string nombreBase = resultado.Split('.')[0];
            if (NombresReservadosWindows.Contains(nombreBase))
            {
                if (resultado.Length + 2 <= maxLength)
                {
                    resultado = $"F_{resultado}";
                }
                else if (resultado.Length + 1 <= maxLength)
                {
                    resultado = $"{resultado}_";
                }
                else if (maxLength > 1)
                {
                    resultado = $"{resultado.Substring(0, maxLength - 1)}_";
                }
                else
                {
                    resultado = "X";
                }
            }

            return resultado;
        }

        private static string NormalizarExtensionImagen(string? extension)
        {
            if (string.IsNullOrWhiteSpace(extension))
            {
                return ".jpg";
            }

            string normalizada = extension.Trim().ToLowerInvariant();
            if (!normalizada.StartsWith('.'))
            {
                normalizada = "." + normalizada;
            }

            return ExtensionesImagenPermitidas.Contains(normalizada) ? normalizada : ".jpg";
        }
    }
}
