using System;
using System.Buffers;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace CarreraApp.Helpers
{
    public sealed record CorredorCsvFila(
        int Dorsal,
        string Nombre,
        string Categoria,
        string Hit,
        string Estado,
        string TiempoNeto,
        string HoraLlegada);

    public static class CsvExportHelper
    {
        private const string DirectivaSeparador = "sep=;";
        private static readonly UTF8Encoding Utf8ConBom = new(encoderShouldEmitUTF8Identifier: true);
        private static readonly SearchValues<char> CaracteresRfc4180 = SearchValues.Create(";\r\n\"");

        private static readonly string[] EncabezadosCorredores =
        {
            "Dorsal",
            "Nombre",
            "Categoría",
            "Hit de Salida",
            "Estado",
            "Tiempo Neto",
            "Hora de Llegada"
        };

        /// <summary>
        /// Neutraliza campos editables por el usuario que inicien (directamente o tras espacios iniciales)
        /// con '=', '+', '-', '@', '\t' o '\r' anteponiendo un apóstrofe ('), preservando valores del sistema
        /// como '--:--:--' cuando <paramref name="esValorSistema"/> es true, y aplica escape RFC 4180 si aplica.
        /// </summary>
        public static string SanitizarCeldaCsv(string? valor, bool esValorSistema = false)
        {
            string texto = valor ?? string.Empty;

            if (!esValorSistema && RequiereNeutralizacionFormula(texto))
            {
                texto = "'" + texto;
            }

            return EscaparRfc4180(texto);
        }

        /// <summary>
        /// Genera el contenido CSV completo con directiva 'sep=;', encabezados y filas sanitizadas.
        /// </summary>
        public static string GenerarContenidoCsv(
            IEnumerable<string> encabezados,
            IEnumerable<IEnumerable<(string? Valor, bool EsValorSistema)>> filas,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(encabezados);
            ArgumentNullException.ThrowIfNull(filas);

            var sb = new StringBuilder();
            sb.Append(DirectivaSeparador).Append("\r\n");

            var encabezadosList = new List<string>();
            foreach (string encabezado in encabezados)
            {
                encabezadosList.Add(SanitizarCeldaCsv(encabezado, esValorSistema: true));
            }

            sb.Append(string.Join(';', encabezadosList)).Append("\r\n");

            foreach (IEnumerable<(string? Valor, bool EsValorSistema)> fila in filas)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (fila == null)
                {
                    continue;
                }

                var celdas = new List<string>();
                foreach ((string? valor, bool esValorSistema) in fila)
                {
                    celdas.Add(SanitizarCeldaCsv(valor, esValorSistema));
                }

                sb.Append(string.Join(';', celdas)).Append("\r\n");
            }

            return sb.ToString();
        }

        /// <summary>
        /// Genera el texto CSV para una colección de corredores con directiva 'sep=;' y sanitización contra fórmulas.
        /// </summary>
        public static string GenerarCsvCorredores(
            IEnumerable<CorredorCsvFila> corredores,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(corredores);

            var sb = new StringBuilder();
            sb.Append(DirectivaSeparador).Append("\r\n");
            sb.Append(string.Join(';', EncabezadosCorredores)).Append("\r\n");

            foreach (CorredorCsvFila item in corredores)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (item == null)
                {
                    continue;
                }

                string dorsal = SanitizarCeldaCsv(item.Dorsal.ToString(CultureInfo.InvariantCulture), esValorSistema: true);
                string nombre = SanitizarCeldaCsv(item.Nombre, esValorSistema: false);
                string categoria = SanitizarCeldaCsv(item.Categoria, esValorSistema: false);
                string hit = SanitizarCeldaCsv(item.Hit, esValorSistema: false);
                string estado = SanitizarCeldaCsv(item.Estado, esValorSistema: true);
                string tiempoNeto = SanitizarCeldaCsv(item.TiempoNeto, esValorSistema: true);
                string horaLlegada = SanitizarCeldaCsv(item.HoraLlegada, esValorSistema: true);

                sb.Append(dorsal).Append(';')
                  .Append(nombre).Append(';')
                  .Append(categoria).Append(';')
                  .Append(hit).Append(';')
                  .Append(estado).Append(';')
                  .Append(tiempoNeto).Append(';')
                  .Append(horaLlegada).Append("\r\n");
            }

            return sb.ToString();
        }

        /// <summary>
        /// Escribe de forma asíncrona el contenido CSV en disco utilizando codificación UTF-8 con BOM.
        /// </summary>
        public static async Task EscribirArchivoCsvAsync(
            string rutaArchivo,
            string contenidoCsv,
            CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(rutaArchivo);
            ArgumentNullException.ThrowIfNull(contenidoCsv);

            cancellationToken.ThrowIfCancellationRequested();

            await Task.Run(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                string? directorio = Path.GetDirectoryName(Path.GetFullPath(rutaArchivo));
                if (!string.IsNullOrEmpty(directorio))
                {
                    Directory.CreateDirectory(directorio);
                }
            }, cancellationToken).ConfigureAwait(false);

            await File.WriteAllTextAsync(rutaArchivo, contenidoCsv, Utf8ConBom, cancellationToken)
                .ConfigureAwait(false);
        }

        /// <summary>
        /// Ejecuta lectura/transformación, sanitización, generación y escritura asíncrona del archivo CSV
        /// fuera del hilo llamante (UI) con directiva 'sep=;' y codificación UTF-8 con BOM.
        /// </summary>
        public static async Task ExportarCorredoresCsvAsync(
            string rutaArchivo,
            IEnumerable<CorredorCsvFila> corredores,
            CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(rutaArchivo);
            ArgumentNullException.ThrowIfNull(corredores);

            cancellationToken.ThrowIfCancellationRequested();

            string contenidoCsv = await Task.Run(
                () => GenerarCsvCorredores(corredores, cancellationToken),
                cancellationToken).ConfigureAwait(false);

            await EscribirArchivoCsvAsync(rutaArchivo, contenidoCsv, cancellationToken)
                .ConfigureAwait(false);
        }

        private static bool RequiereNeutralizacionFormula(string texto)
        {
            if (texto.Length == 0)
            {
                return false;
            }

            int idx = 0;
            // '\t' y '\r' también son IsWhiteSpace, pero forman parte de los prefijos peligrosos explícitos.
            while (idx < texto.Length &&
                   char.IsWhiteSpace(texto[idx]) &&
                   texto[idx] != '\t' &&
                   texto[idx] != '\r')
            {
                idx++;
            }

            if (idx >= texto.Length)
            {
                return false;
            }

            return texto[idx] is '=' or '+' or '-' or '@' or '\t' or '\r';
        }

        private static string EscaparRfc4180(string texto)
        {
            if (!texto.AsSpan().ContainsAny(CaracteresRfc4180))
            {
                return texto;
            }

            return $"\"{texto.Replace("\"", "\"\"")}\"";
        }
    }
}
