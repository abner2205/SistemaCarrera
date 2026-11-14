// Path: src/CarreraApp/Helpers/ImageHelper.cs
using System;
using System.Buffers.Binary;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media.Imaging;

namespace CarreraApp.Helpers
{
    public enum FormatoImagenPermitido
    {
        Desconocido = 0,
        Jpeg,
        Png,
        Webp
    }

    public sealed record ValidacionImagenResultado(
        bool EsValida,
        int CodigoHttp,
        string MensajeError,
        FormatoImagenPermitido Formato,
        string ExtensionReal,
        string ContentType,
        int AnchoPixeles,
        int AltoPixeles)
    {
        public static ValidacionImagenResultado Exito(
            FormatoImagenPermitido formato,
            string extensionReal,
            string contentType,
            int anchoPixeles,
            int altoPixeles) =>
            new(true, 200, string.Empty, formato, extensionReal, contentType, anchoPixeles, altoPixeles);

        public static ValidacionImagenResultado Invalida(int codigoHttp, string mensajeError) =>
            new(false, codigoHttp, mensajeError, FormatoImagenPermitido.Desconocido, string.Empty, string.Empty, 0, 0);
    }

    public static class ImageHelper
    {
        /// <summary>
        /// Tamaño máximo permitido para carga de fotografías (10 MB).
        /// </summary>
        public const int TamanoMaximoBytes = 10 * 1024 * 1024;

        private const int MaxDimensionPixeles = 16384;
        private const long MaxPixelesTotales = 100_000_000L;
        private const int TamanoBufferLectura = 81920;

        /// <summary>
        /// Valida de forma síncrona un arreglo de bytes comprobando tamaño (1 B .. 10 MB),
        /// firma binaria (magic bytes) para JPEG/PNG/WebP y decodificabilidad real de imagen
        /// sin depender de infraestructura de ventana WPF.
        /// </summary>
        public static ValidacionImagenResultado ValidarImagen(byte[]? datos)
        {
            if (datos == null || datos.Length == 0)
            {
                return ValidacionImagenResultado.Invalida(400, "El archivo de imagen está vacío.");
            }

            if (datos.Length > TamanoMaximoBytes)
            {
                return ValidacionImagenResultado.Invalida(
                    413,
                    $"La imagen supera el tamaño máximo permitido de {TamanoMaximoBytes / (1024 * 1024)} MB.");
            }

            FormatoImagenPermitido formato = DetectarFormatoPorMagicBytes(datos);
            if (formato == FormatoImagenPermitido.Desconocido)
            {
                return ValidacionImagenResultado.Invalida(
                    415,
                    "Formato de archivo no admitido. Solo se permiten imágenes JPEG, PNG o WebP válidas.");
            }

            return ValidarContenidoDecodificable(datos, formato);
        }

        /// <summary>
        /// Valida un flujo de imagen leyendo como máximo 10 MB + 1 byte para proteger la memoria
        /// y evitar escrituras en disco antes de validar.
        /// </summary>
        public static ValidacionImagenResultado ValidarImagen(Stream? flujo, long? tamanoDeclarado = null)
        {
            if (ValidarPrecondicionesFlujo(flujo, tamanoDeclarado) is { } errorPrecondicion)
            {
                return errorPrecondicion;
            }

            if (flujo!.CanSeek && flujo.Position > 0)
            {
                flujo.Position = 0;
            }

            using var memoryStream = new MemoryStream();
            byte[] buffer = new byte[TamanoBufferLectura];
            int totalLeido = 0;

            while (true)
            {
                int porLeer = Math.Min(buffer.Length, (TamanoMaximoBytes + 1) - totalLeido);
                int leidos = flujo.Read(buffer, 0, porLeer);
                if (leidos <= 0)
                {
                    break;
                }

                totalLeido += leidos;
                if (totalLeido > TamanoMaximoBytes)
                {
                    return ValidacionImagenResultado.Invalida(
                        413,
                        $"La imagen supera el tamaño máximo permitido de {TamanoMaximoBytes / (1024 * 1024)} MB.");
                }

                memoryStream.Write(buffer, 0, leidos);
            }

            if (totalLeido == 0)
            {
                return ValidacionImagenResultado.Invalida(400, "El archivo de imagen está vacío.");
            }

            return ValidarImagen(memoryStream.ToArray());
        }

        /// <summary>
        /// Valida de forma asíncrona y fuera del hilo llamante un flujo de imagen
        /// comprobando tamaño, firma binaria y decodificabilidad real.
        /// </summary>
        public static async Task<ValidacionImagenResultado> ValidarImagenAsync(
            Stream? flujo,
            long? tamanoDeclarado = null,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (ValidarPrecondicionesFlujo(flujo, tamanoDeclarado) is { } errorPrecondicion)
            {
                return errorPrecondicion;
            }

            return await Task.Run(async () =>
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (flujo!.CanSeek && flujo.Position > 0)
                {
                    flujo.Position = 0;
                }

                using var memoryStream = new MemoryStream();
                byte[] buffer = new byte[TamanoBufferLectura];
                int totalLeido = 0;

                while (true)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    int porLeer = Math.Min(buffer.Length, (TamanoMaximoBytes + 1) - totalLeido);
                    int leidos = await flujo.ReadAsync(buffer.AsMemory(0, porLeer), cancellationToken)
                        .ConfigureAwait(false);
                    if (leidos <= 0)
                    {
                        break;
                    }

                    totalLeido += leidos;
                    if (totalLeido > TamanoMaximoBytes)
                    {
                        return ValidacionImagenResultado.Invalida(
                            413,
                            $"La imagen supera el tamaño máximo permitido de {TamanoMaximoBytes / (1024 * 1024)} MB.");
                    }

                    memoryStream.Write(buffer, 0, leidos);
                }

                if (totalLeido == 0)
                {
                    return ValidacionImagenResultado.Invalida(400, "El archivo de imagen está vacío.");
                }

                cancellationToken.ThrowIfCancellationRequested();
                return ValidarImagen(memoryStream.ToArray());
            }, cancellationToken).ConfigureAwait(false);
        }

        /// <summary>
        /// Carga una imagen desde disco sin mantener bloqueado el archivo, con soporte opcional
        /// de escalado durante la decodificación (<paramref name="decodePixelWidth"/>) y congelación (<see cref="Freezable.Freeze"/>).
        /// Retorna <see langword="null"/> si el archivo no existe, está vacío o no puede decodificarse.
        /// </summary>
        public static BitmapImage? CargarSinBloqueo(string? rutaArchivo, int? decodePixelWidth = null)
        {
            if (decodePixelWidth is <= 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(decodePixelWidth),
                    "El ancho de decodificación debe ser un entero positivo.");
            }

            if (string.IsNullOrWhiteSpace(rutaArchivo) || !File.Exists(rutaArchivo))
            {
                return null;
            }

            try
            {
                byte[] bytes;
                using (var stream = new FileStream(rutaArchivo, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                {
                    if (stream.Length == 0)
                    {
                        return null;
                    }

                    using var memoryStream = new MemoryStream();
                    stream.CopyTo(memoryStream);
                    bytes = memoryStream.ToArray();
                }

                return DecodificarBitmapImage(bytes, decodePixelWidth);
            }
            catch (Exception ex)
            {
                Trace.TraceWarning($"[ImageHelper] No se pudo cargar la imagen '{rutaArchivo}': {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Carga y decodifica una imagen de forma asíncrona fuera del hilo principal de UI,
        /// aplicando escalado opcional y congelación para uso seguro entre hilos.
        /// Retorna <see langword="null"/> si el archivo no existe, está vacío o no puede decodificarse.
        /// </summary>
        public static async Task<BitmapImage?> CargarSinBloqueoAsync(
            string? rutaArchivo,
            int? decodePixelWidth = null,
            CancellationToken cancellationToken = default)
        {
            if (decodePixelWidth is <= 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(decodePixelWidth),
                    "El ancho de decodificación debe ser un entero positivo.");
            }

            cancellationToken.ThrowIfCancellationRequested();

            if (string.IsNullOrWhiteSpace(rutaArchivo) || !File.Exists(rutaArchivo))
            {
                return null;
            }

            return await Task.Run(async () =>
            {
                cancellationToken.ThrowIfCancellationRequested();

                try
                {
                    byte[] bytes;
                    await using (var stream = new FileStream(
                        rutaArchivo,
                        FileMode.Open,
                        FileAccess.Read,
                        FileShare.ReadWrite,
                        TamanoBufferLectura,
                        FileOptions.Asynchronous | FileOptions.SequentialScan))
                    {
                        if (stream.Length == 0)
                        {
                            return null;
                        }

                        using var memoryStream = new MemoryStream();
                        await stream.CopyToAsync(memoryStream, cancellationToken).ConfigureAwait(false);
                        bytes = memoryStream.ToArray();
                    }

                    cancellationToken.ThrowIfCancellationRequested();
                    return DecodificarBitmapImage(bytes, decodePixelWidth);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    Trace.TraceWarning($"[ImageHelper] No se pudo cargar la imagen de forma asíncrona '{rutaArchivo}': {ex.Message}");
                    return null;
                }
            }, cancellationToken).ConfigureAwait(false);
        }

        private static ValidacionImagenResultado? ValidarPrecondicionesFlujo(Stream? flujo, long? tamanoDeclarado)
        {
            if (flujo == null)
            {
                return ValidacionImagenResultado.Invalida(400, "No se recibió ningún flujo de imagen.");
            }

            if (tamanoDeclarado.HasValue)
            {
                if (tamanoDeclarado.Value <= 0)
                {
                    return ValidacionImagenResultado.Invalida(400, "El archivo de imagen está vacío.");
                }

                if (tamanoDeclarado.Value > TamanoMaximoBytes)
                {
                    return ValidacionImagenResultado.Invalida(
                        413,
                        $"La imagen supera el tamaño máximo permitido de {TamanoMaximoBytes / (1024 * 1024)} MB.");
                }
            }

            return null;
        }

        public static FormatoImagenPermitido DetectarFormatoPorMagicBytes(ReadOnlySpan<byte> datos)
        {
            // JPEG: SOI (FF D8) seguido de marcador (FF xx)
            if (datos.Length >= 3 &&
                datos[0] == 0xFF &&
                datos[1] == 0xD8 &&
                datos[2] == 0xFF)
            {
                return FormatoImagenPermitido.Jpeg;
            }

            // PNG: 89 50 4E 47 0D 0A 1A 0A
            if (datos.Length >= 8 &&
                datos[0] == 0x89 &&
                datos[1] == 0x50 &&
                datos[2] == 0x4E &&
                datos[3] == 0x47 &&
                datos[4] == 0x0D &&
                datos[5] == 0x0A &&
                datos[6] == 0x1A &&
                datos[7] == 0x0A)
            {
                return FormatoImagenPermitido.Png;
            }

            // WebP: "RIFF" + tamaño(4) + "WEBP" + chunk ("VP8 ", "VP8L" o "VP8X")
            if (datos.Length >= 16 &&
                datos[0] == (byte)'R' &&
                datos[1] == (byte)'I' &&
                datos[2] == (byte)'F' &&
                datos[3] == (byte)'F' &&
                datos[8] == (byte)'W' &&
                datos[9] == (byte)'E' &&
                datos[10] == (byte)'B' &&
                datos[11] == (byte)'P' &&
                EsFourCcWebpValido(datos.Slice(12, 4)))
            {
                return FormatoImagenPermitido.Webp;
            }

            return FormatoImagenPermitido.Desconocido;
        }

        private static bool EsFourCcWebpValido(ReadOnlySpan<byte> fourCc)
        {
            return fourCc.Length == 4 &&
                   fourCc[0] == (byte)'V' &&
                   fourCc[1] == (byte)'P' &&
                   fourCc[2] == (byte)'8' &&
                   (fourCc[3] == (byte)' ' || fourCc[3] == (byte)'L' || fourCc[3] == (byte)'X');
        }

        private static ValidacionImagenResultado ValidarContenidoDecodificable(byte[] datos, FormatoImagenPermitido formatoEsperado)
        {
            bool estructuraValida = formatoEsperado switch
            {
                FormatoImagenPermitido.Jpeg => TieneEstructuraJpegCompleta(datos),
                FormatoImagenPermitido.Png => TieneEstructuraPngCompleta(datos),
                FormatoImagenPermitido.Webp => TieneEstructuraRiffWebpConsistente(datos),
                _ => false
            };

            if (!estructuraValida)
            {
                return ValidacionImagenResultado.Invalida(
                    415,
                    $"La imagen {formatoEsperado} está dañada o truncada y no puede decodificarse.");
            }

            try
            {
                using var ms = new MemoryStream(datos, writable: false);
                BitmapDecoder decoder = BitmapDecoder.Create(
                    ms,
                    BitmapCreateOptions.PreservePixelFormat | BitmapCreateOptions.IgnoreColorProfile,
                    BitmapCacheOption.OnLoad);

                if (!CoincideCodecConFormato(decoder, formatoEsperado))
                {
                    return ValidacionImagenResultado.Invalida(
                        415,
                        "La firma binaria no coincide con un formato de imagen permitido (JPEG, PNG o WebP).");
                }

                if (decoder.Frames.Count == 0)
                {
                    return ValidacionImagenResultado.Invalida(
                        415,
                        "La imagen no contiene ningún fotograma válido.");
                }

                BitmapFrame frame = decoder.Frames[0];
                int ancho = frame.PixelWidth;
                int alto = frame.PixelHeight;

                if (ancho <= 0 ||
                    alto <= 0 ||
                    ancho > MaxDimensionPixeles ||
                    alto > MaxDimensionPixeles ||
                    (long)ancho * alto > MaxPixelesTotales)
                {
                    return ValidacionImagenResultado.Invalida(
                        415,
                        "Las dimensiones de la imagen son inválidas o exceden los límites permitidos.");
                }

                // Forzar la decodificación real hasta el último píxel (1x1) para detectar flujos truncados/corruptos sin asignar un buffer completo
                int stride = Math.Max(4, (frame.Format.BitsPerPixel + 7) / 8);
                byte[] pixelTest = new byte[stride];
                frame.CopyPixels(new Int32Rect(ancho - 1, alto - 1, 1, 1), pixelTest, stride, 0);

                (string extension, string contentType) = ObtenerMetadatosFormato(formatoEsperado);
                return ValidacionImagenResultado.Exito(formatoEsperado, extension, contentType, ancho, alto);
            }
            catch (Exception)
            {
                return ValidacionImagenResultado.Invalida(
                    415,
                    "El archivo no es una imagen JPEG, PNG o WebP decodificable o está corrupto.");
            }
        }

        private static bool TieneEstructuraJpegCompleta(ReadOnlySpan<byte> datos)
        {
            if (datos.Length < 4 || datos[0] != 0xFF || datos[1] != 0xD8)
            {
                return false;
            }

            int pos = 2;
            bool tieneSof = false;
            bool tieneSos = false;

            while (pos < datos.Length)
            {
                // Cada marcador JPEG comienza con uno o más bytes 0xFF
                if (datos[pos] != 0xFF)
                {
                    return false;
                }

                while (pos < datos.Length && datos[pos] == 0xFF)
                {
                    pos++;
                }

                if (pos >= datos.Length)
                {
                    return false;
                }

                byte marker = datos[pos++];

                // Marcadores sin longitud
                if (marker == 0x00 || marker == 0x01 || (marker >= 0xD0 && marker <= 0xD8))
                {
                    continue;
                }

                if (marker == 0xD9) // EOI (End of Image)
                {
                    return tieneSof && tieneSos;
                }

                // El resto de marcadores tiene longitud big-endian de 2 bytes (incluidos esos 2 bytes)
                if (pos + 2 > datos.Length)
                {
                    return false;
                }

                int segmentLength = BinaryPrimitives.ReadUInt16BigEndian(datos.Slice(pos, 2));
                if (segmentLength < 2 || pos + segmentLength > datos.Length)
                {
                    return false;
                }

                if ((marker >= 0xC0 && marker <= 0xCF) &&
                    marker != 0xC4 && marker != 0xC8 && marker != 0xCC)
                {
                    tieneSof = true;
                }

                pos += segmentLength;

                if (marker == 0xDA) // SOS (Start of Scan): sigue flujo entrópico hasta el próximo marcador
                {
                    tieneSos = true;
                    while (pos < datos.Length)
                    {
                        if (datos[pos] != 0xFF)
                        {
                            pos++;
                            continue;
                        }

                        int markerStart = pos;
                        while (pos < datos.Length && datos[pos] == 0xFF)
                        {
                            pos++;
                        }

                        if (pos >= datos.Length)
                        {
                            return false;
                        }

                        byte siguiente = datos[pos];
                        if (siguiente == 0x00 || (siguiente >= 0xD0 && siguiente <= 0xD7))
                        {
                            // Byte-stuffing (FF 00) o marcador RSTn dentro del flujo comprimido
                            pos++;
                            continue;
                        }

                        // Se encontró un nuevo marcador real (p. ej. EOI 0xD9 o siguiente scan progresivo)
                        pos = markerStart;
                        break;
                    }

                    if (pos >= datos.Length)
                    {
                        return false;
                    }
                }
            }

            return false;
        }

        private static bool TieneEstructuraPngCompleta(ReadOnlySpan<byte> datos)
        {
            if (datos.Length < 45)
            {
                return false;
            }

            int pos = 8; // Después de los 8 bytes de firma PNG
            bool tieneIhdr = false;
            bool tieneIdat = false;
            bool esPrimerChunk = true;

            while (pos + 12 <= datos.Length)
            {
                uint length = BinaryPrimitives.ReadUInt32BigEndian(datos.Slice(pos, 4));
                if (length > int.MaxValue - 12 || pos + 12L + length > datos.Length)
                {
                    return false;
                }

                ReadOnlySpan<byte> type = datos.Slice(pos + 4, 4);
                if (esPrimerChunk)
                {
                    if (type[0] != (byte)'I' || type[1] != (byte)'H' || type[2] != (byte)'D' || type[3] != (byte)'R' || length != 13)
                    {
                        return false;
                    }

                    tieneIhdr = true;
                    esPrimerChunk = false;
                }
                else if (type[0] == (byte)'I' && type[1] == (byte)'D' && type[2] == (byte)'A' && type[3] == (byte)'T')
                {
                    tieneIdat = true;
                }
                else if (type[0] == (byte)'I' && type[1] == (byte)'E' && type[2] == (byte)'N' && type[3] == (byte)'D')
                {
                    return tieneIhdr && tieneIdat && length == 0;
                }

                pos += 12 + (int)length;
            }

            return false;
        }

        private static bool TieneEstructuraRiffWebpConsistente(ReadOnlySpan<byte> datos)
        {
            if (datos.Length < 20)
            {
                return false;
            }

            uint riffSize = BinaryPrimitives.ReadUInt32LittleEndian(datos.Slice(4, 4));
            if (riffSize < 12)
            {
                return false;
            }

            long tamanoEsperadoRiff = 8L + riffSize;
            if (datos.Length < tamanoEsperadoRiff)
            {
                return false;
            }

            int pos = 12;
            bool tieneImagenChunk = false;

            while (pos + 8 <= tamanoEsperadoRiff)
            {
                ReadOnlySpan<byte> fourCc = datos.Slice(pos, 4);
                uint chunkSize = BinaryPrimitives.ReadUInt32LittleEndian(datos.Slice(pos + 4, 4));
                long paddedChunkSize = (long)chunkSize + (chunkSize % 2);
                long nextPos = pos + 8L + paddedChunkSize;

                if (nextPos > tamanoEsperadoRiff || pos + 8L + chunkSize > datos.Length)
                {
                    return false;
                }

                if (fourCc[0] == (byte)'V' && fourCc[1] == (byte)'P' && fourCc[2] == (byte)'8' &&
                    (fourCc[3] == (byte)' ' || fourCc[3] == (byte)'L'))
                {
                    tieneImagenChunk = true;
                }

                pos = (int)nextPos;
            }

            return pos == tamanoEsperadoRiff && tieneImagenChunk;
        }

        private static bool CoincideCodecConFormato(BitmapDecoder decoder, FormatoImagenPermitido formato)
        {
            return formato switch
            {
                FormatoImagenPermitido.Jpeg => decoder is JpegBitmapDecoder,
                FormatoImagenPermitido.Png => decoder is PngBitmapDecoder,
                FormatoImagenPermitido.Webp =>
                    (decoder.CodecInfo?.FileExtensions?.Contains(".webp", StringComparison.OrdinalIgnoreCase) ?? false) ||
                    (decoder.CodecInfo?.MimeTypes?.Contains("image/webp", StringComparison.OrdinalIgnoreCase) ?? false) ||
                    (decoder.CodecInfo?.FriendlyName?.Contains("WebP", StringComparison.OrdinalIgnoreCase) ?? false),
                _ => false
            };
        }

        private static (string Extension, string ContentType) ObtenerMetadatosFormato(FormatoImagenPermitido formato)
        {
            return formato switch
            {
                FormatoImagenPermitido.Jpeg => (".jpg", "image/jpeg"),
                FormatoImagenPermitido.Png => (".png", "image/png"),
                FormatoImagenPermitido.Webp => (".webp", "image/webp"),
                _ => (string.Empty, string.Empty)
            };
        }

        private static BitmapImage DecodificarBitmapImage(byte[] bytes, int? decodePixelWidth)
        {
            int? anchoObjetivo = null;
            if (decodePixelWidth.HasValue)
            {
                using var msMeta = new MemoryStream(bytes, writable: false);
                var decoder = BitmapDecoder.Create(
                    msMeta,
                    BitmapCreateOptions.DelayCreation,
                    BitmapCacheOption.None);

                if (decoder.Frames.Count > 0)
                {
                    int anchoOriginal = decoder.Frames[0].PixelWidth;
                    if (anchoOriginal > decodePixelWidth.Value)
                    {
                        anchoObjetivo = decodePixelWidth.Value;
                    }
                }
            }

            using var memoryStream = new MemoryStream(bytes, writable: false);
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            if (anchoObjetivo.HasValue)
            {
                bitmap.DecodePixelWidth = anchoObjetivo.Value;
            }

            bitmap.StreamSource = memoryStream;
            bitmap.EndInit();
            bitmap.Freeze();
            return bitmap;
        }
    }
}
