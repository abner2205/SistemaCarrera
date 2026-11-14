using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CarreraApp.Helpers;

namespace CarreraApp.Tests
{
    public class ImageHelperAndValidatorTests
    {
        [Fact]
        public void ValidarImagen_JpegAutentico_RetornaValidaConFormatoYExtensionJpg()
        {
            Assert.Null(Application.Current);
            byte[] jpeg = CrearJpegValido(320, 240);

            ValidacionImagenResultado resultado = ImageHelper.ValidarImagen(jpeg);

            Assert.True(resultado.EsValida, resultado.MensajeError);
            Assert.Equal(200, resultado.CodigoHttp);
            Assert.Equal(FormatoImagenPermitido.Jpeg, resultado.Formato);
            Assert.Equal(".jpg", resultado.ExtensionReal);
            Assert.Equal("image/jpeg", resultado.ContentType);
            Assert.Equal(320, resultado.AnchoPixeles);
            Assert.Equal(240, resultado.AltoPixeles);
        }

        [Fact]
        public void ValidarImagen_PngAutentico_RetornaValidaConFormatoYExtensionPng()
        {
            Assert.Null(Application.Current);
            byte[] png = CrearPngValido(280, 180);

            ValidacionImagenResultado resultado = ImageHelper.ValidarImagen(png);

            Assert.True(resultado.EsValida, resultado.MensajeError);
            Assert.Equal(200, resultado.CodigoHttp);
            Assert.Equal(FormatoImagenPermitido.Png, resultado.Formato);
            Assert.Equal(".png", resultado.ExtensionReal);
            Assert.Equal("image/png", resultado.ContentType);
            Assert.Equal(280, resultado.AnchoPixeles);
            Assert.Equal(180, resultado.AltoPixeles);
        }

        [Fact]
        public void ValidarImagen_WebpAutentico_RetornaValidaConFormatoYExtensionWebp()
        {
            Assert.Null(Application.Current);
            byte[] webp = CrearWebpLosslessValido(200, 120);

            ValidacionImagenResultado resultado = ImageHelper.ValidarImagen(webp);

            Assert.True(resultado.EsValida, resultado.MensajeError);
            Assert.Equal(200, resultado.CodigoHttp);
            Assert.Equal(FormatoImagenPermitido.Webp, resultado.Formato);
            Assert.Equal(".webp", resultado.ExtensionReal);
            Assert.Equal("image/webp", resultado.ContentType);
            Assert.Equal(200, resultado.AnchoPixeles);
            Assert.Equal(120, resultado.AltoPixeles);
        }

        [Fact]
        public async Task ValidarImagenAsync_ConTamanoExacto10MB_EsAceptada()
        {
            byte[] pngExacto10Mb = CrearPngExactoDeTamano(ImageHelper.TamanoMaximoBytes);

            Assert.Equal(ImageHelper.TamanoMaximoBytes, pngExacto10Mb.Length);

            using var stream = new MemoryStream(pngExacto10Mb, writable: false);
            ValidacionImagenResultado resultado = await ImageHelper.ValidarImagenAsync(stream, stream.Length);

            Assert.True(resultado.EsValida, resultado.MensajeError);
            Assert.Equal(200, resultado.CodigoHttp);
            Assert.Equal(FormatoImagenPermitido.Png, resultado.Formato);
        }

        [Fact]
        public async Task ValidarImagen_CeroBytes_Retorna400BadRequest()
        {
            ValidacionImagenResultado rNull = ImageHelper.ValidarImagen((byte[]?)null);
            ValidacionImagenResultado rVacio = ImageHelper.ValidarImagen(Array.Empty<byte>());

            using var streamVacio = new MemoryStream();
            ValidacionImagenResultado rStream = await ImageHelper.ValidarImagenAsync(streamVacio, 0);

            Assert.False(rNull.EsValida);
            Assert.Equal(400, rNull.CodigoHttp);
            Assert.False(string.IsNullOrWhiteSpace(rNull.MensajeError));

            Assert.False(rVacio.EsValida);
            Assert.Equal(400, rVacio.CodigoHttp);

            Assert.False(rStream.EsValida);
            Assert.Equal(400, rStream.CodigoHttp);
        }

        [Fact]
        public async Task ValidarImagenAsync_MayorA10MB_Retorna413PayloadTooLarge()
        {
            byte[] exceso = new byte[ImageHelper.TamanoMaximoBytes + 1];
            byte[] jpeg = CrearJpegValido(32, 32);
            Array.Copy(jpeg, exceso, jpeg.Length);

            ValidacionImagenResultado rBytes = ImageHelper.ValidarImagen(exceso);

            using var streamSinLongitudDeclarada = new StreamNoBuscable(exceso);
            ValidacionImagenResultado rStream = await ImageHelper.ValidarImagenAsync(streamSinLongitudDeclarada, tamanoDeclarado: null);

            using var streamSoloTamano = new MemoryStream(jpeg);
            ValidacionImagenResultado rDeclarado = await ImageHelper.ValidarImagenAsync(
                streamSoloTamano,
                tamanoDeclarado: ImageHelper.TamanoMaximoBytes + 1L);

            Assert.False(rBytes.EsValida);
            Assert.Equal(413, rBytes.CodigoHttp);

            Assert.False(rStream.EsValida);
            Assert.Equal(413, rStream.CodigoHttp);

            Assert.False(rDeclarado.EsValida);
            Assert.Equal(413, rDeclarado.CodigoHttp);
        }

        [Fact]
        public void ValidarImagen_ArchivoTextoOEjecutableRenombradoAJpg_Retorna415UnsupportedMediaType()
        {
            byte[] textoPlano = Encoding.UTF8.GetBytes("Esto no es una foto JPEG, aunque se llame meta.jpg");
            byte[] ejecutablePe = new byte[]
            {
                0x4D, 0x5A, 0x90, 0x00, 0x03, 0x00, 0x00, 0x00,
                0x04, 0x00, 0x00, 0x00, 0xFF, 0xFF, 0x00, 0x00
            };

            ValidacionImagenResultado rTexto = ImageHelper.ValidarImagen(textoPlano);
            ValidacionImagenResultado rExe = ImageHelper.ValidarImagen(ejecutablePe);

            Assert.False(rTexto.EsValida);
            Assert.Equal(415, rTexto.CodigoHttp);
            Assert.False(string.IsNullOrWhiteSpace(rTexto.MensajeError));

            Assert.False(rExe.EsValida);
            Assert.Equal(415, rExe.CodigoHttp);
        }

        [Fact]
        public void ValidarImagen_FormatoImagenNoPermitidoComoBmpOGif_Retorna415UnsupportedMediaType()
        {
            byte[] bmp = CrearBmpValido(16, 16);
            byte[] gif = CrearGifValido(16, 16);

            ValidacionImagenResultado rBmp = ImageHelper.ValidarImagen(bmp);
            ValidacionImagenResultado rGif = ImageHelper.ValidarImagen(gif);

            Assert.False(rBmp.EsValida);
            Assert.Equal(415, rBmp.CodigoHttp);

            Assert.False(rGif.EsValida);
            Assert.Equal(415, rGif.CodigoHttp);
        }

        [Fact]
        public void ValidarImagen_CabecerasFalsas_Retorna415UnsupportedMediaType()
        {
            byte[] fakeJpeg = { 0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10, 0x4A, 0x46, 0x49, 0x46, 0x00, 0x01, 0x99, 0x88, 0x77 };
            byte[] fakePng = { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x00, 0x00, 0x00, 0x0D, 0x49, 0x48, 0x44, 0x52 };
            byte[] fakeWebp = Encoding.ASCII.GetBytes("RIFF\x20\x00\x00\x00WEBPVP8L\x10\x00\x00\x00\x2F\x00\x00\x00\x00\xFF\xFF");

            foreach (byte[] payload in new[] { fakeJpeg, fakePng, fakeWebp })
            {
                ValidacionImagenResultado resultado = ImageHelper.ValidarImagen(payload);
                Assert.False(resultado.EsValida);
                Assert.Equal(415, resultado.CodigoHttp);
            }
        }

        [Fact]
        public void ValidarImagen_ImagenesTruncadasOCorruptas_Retorna415UnsupportedMediaType()
        {
            byte[] jpegReal = CrearJpegValido(200, 150);
            byte[] pngReal = CrearPngValido(200, 150);
            byte[] webpReal = CrearWebpLosslessValido(200, 150);

            byte[] jpegTruncadoMitad = jpegReal[..(jpegReal.Length / 2)];
            byte[] jpegSinEoi = jpegReal[..^2];
            byte[] jpegSinScan = new byte[] { 0xFF, 0xD8, 0xFF, 0xD9 };
            byte[] pngTruncadoMitad = pngReal[..(pngReal.Length / 2)];
            byte[] pngSinIend = pngReal[..^12];
            byte[] webpTruncado = webpReal[..(webpReal.Length - 3)];
            byte[] webpSinImagen = CrearRiffWebpSinChunkImagen();

            foreach (byte[] payload in new[]
            {
                jpegTruncadoMitad,
                jpegSinEoi,
                jpegSinScan,
                pngTruncadoMitad,
                pngSinIend,
                webpTruncado,
                webpSinImagen
            })
            {
                ValidacionImagenResultado resultado = ImageHelper.ValidarImagen(payload);
                Assert.False(resultado.EsValida, "Una imagen truncada o sin estructura completa debió rechazarse.");
                Assert.Equal(415, resultado.CodigoHttp);
            }
        }

        [Fact]
        public void ValidarImagen_BombaDescompresionDimensionesExcesivas_Retorna415SinConsumirMemoria()
        {
            byte[] pngReal = CrearPngValido(16, 16);
            // Alterar IHDR width y height a 60000 x 60000
            byte[] pngBomba = (byte[])pngReal.Clone();
            BinaryPrimitives.WriteUInt32BigEndian(pngBomba.AsSpan(16, 4), 60000);
            BinaryPrimitives.WriteUInt32BigEndian(pngBomba.AsSpan(20, 4), 60000);

            ValidacionImagenResultado resultado = ImageHelper.ValidarImagen(pngBomba);

            Assert.False(resultado.EsValida);
            Assert.Equal(415, resultado.CodigoHttp);
        }

        [Theory]
        [InlineData(0)]
        [InlineData(-10)]
        public async Task CargarSinBloqueo_ConDecodePixelWidthInvalido_LanzaArgumentOutOfRangeException(int anchoInvalido)
        {
            Assert.Throws<ArgumentOutOfRangeException>(() =>
                ImageHelper.CargarSinBloqueo("cualquiera.jpg", decodePixelWidth: anchoInvalido));

            await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
                ImageHelper.CargarSinBloqueoAsync("cualquiera.jpg", decodePixelWidth: anchoInvalido));
        }

        [Fact]
        public async Task ValidarImagenAsync_SeEjecutaEnHiloDeFondoSinBloquearHiloLlamante()
        {
            byte[] jpeg = CrearJpegValido(120, 90);
            int hiloLlamante = Thread.CurrentThread.ManagedThreadId;
            int? hiloLectura = null;
            using var callerContinuo = new ManualResetEventSlim(false);
            bool senalRecibida = false;

            using var streamLento = new StreamConSemaforo(jpeg, () =>
            {
                hiloLectura = Thread.CurrentThread.ManagedThreadId;
                senalRecibida = callerContinuo.Wait(TimeSpan.FromSeconds(5));
            });

            Task<ValidacionImagenResultado> tarea = ImageHelper.ValidarImagenAsync(streamLento, jpeg.Length);
            callerContinuo.Set();
            ValidacionImagenResultado resultado = await tarea;

            Assert.True(senalRecibida, "La validación bloqueó al hilo llamante en lugar de ejecutarse asíncronamente.");
            Assert.NotNull(hiloLectura);
            Assert.NotEqual(hiloLlamante, hiloLectura);
            Assert.True(resultado.EsValida);
        }

        [Theory]
        [InlineData("jpg")]
        [InlineData("png")]
        [InlineData("webp")]
        public async Task CargarSinBloqueoYAsync_ConDecodePixelWidth_DecodificaEscaladaCongeladaYSinBloquearArchivo(string formato)
        {
            using var env = new TestEnvironmentFixture(configurarStorageGlobal: false);
            byte[] bytes = formato switch
            {
                "jpg" => CrearJpegValido(800, 600),
                "png" => CrearPngValido(800, 600),
                "webp" => CrearWebpLosslessValido(800, 600),
                _ => throw new ArgumentOutOfRangeException(nameof(formato))
            };

            string rutaFoto = Path.Combine(env.RootDirectory, $"meta_800x600.{formato}");
            await File.WriteAllBytesAsync(rutaFoto, bytes);

            BitmapImage? syncEscalada = ImageHelper.CargarSinBloqueo(rutaFoto, decodePixelWidth: 160);
            BitmapImage? asyncEscalada = await ImageHelper.CargarSinBloqueoAsync(rutaFoto, decodePixelWidth: 160);
            Assert.NotNull(syncEscalada);
            Assert.NotNull(asyncEscalada);

            foreach (BitmapImage bmp in new[] { syncEscalada!, asyncEscalada! })
            {
                Assert.True(bmp.IsFrozen);
                Assert.Equal(BitmapCacheOption.OnLoad, bmp.CacheOption);
                Assert.Equal(160, bmp.PixelWidth);
                Assert.Equal(120, bmp.PixelHeight);
            }

            // Verificar que el archivo en disco no queda bloqueado tras la carga
            string rutaDestino = Path.Combine(env.RootDirectory, $"movida.{formato}");
            File.Move(rutaFoto, rutaDestino);
            Assert.True(File.Exists(rutaDestino));
        }

        [Fact]
        public async Task CargarSinBloqueoAsync_ConImagenMasPequenaQueDecodePixelWidth_NoAmpliaTamanoInnecesariamente()
        {
            using var env = new TestEnvironmentFixture(configurarStorageGlobal: false);
            string rutaFoto = Path.Combine(env.RootDirectory, "pequena.png");
            await File.WriteAllBytesAsync(rutaFoto, CrearPngValido(100, 80));

            BitmapImage? bmp = await ImageHelper.CargarSinBloqueoAsync(rutaFoto, decodePixelWidth: 400);

            Assert.NotNull(bmp);
            Assert.True(bmp!.IsFrozen);
            Assert.Equal(100, bmp.PixelWidth);
            Assert.Equal(80, bmp.PixelHeight);
        }

        [Fact]
        public async Task CargarSinBloqueoAsync_SeEjecutaFueraDelHiloLlamanteYPermiteCancelacion()
        {
            using var env = new TestEnvironmentFixture(configurarStorageGlobal: false);
            string rutaFoto = Path.Combine(env.RootDirectory, "async_foto.jpg");
            await File.WriteAllBytesAsync(rutaFoto, CrearJpegValido(400, 300));

            BitmapImage? desdeFondo = null;
            await Task.Run(async () =>
            {
                desdeFondo = await ImageHelper.CargarSinBloqueoAsync(rutaFoto, decodePixelWidth: 160);
            });

            Assert.NotNull(desdeFondo);
            Assert.True(desdeFondo!.IsFrozen);
            Assert.Equal(160, desdeFondo.PixelWidth);

            using var cts = new CancellationTokenSource();
            cts.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                ImageHelper.CargarSinBloqueoAsync(rutaFoto, decodePixelWidth: 160, cts.Token));
        }

        [Fact]
        public async Task CargarSinBloqueo_ConRutaInexistente_RetornaNullSinExcepcion()
        {
            BitmapImage? sync = ImageHelper.CargarSinBloqueo(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".jpg"));
            BitmapImage? asyncBmp = await ImageHelper.CargarSinBloqueoAsync(null, decodePixelWidth: 160);

            Assert.Null(sync);
            Assert.Null(asyncBmp);
        }

        [Fact]
        public async Task CargarSinBloqueo_ConArchivoCorrupto_RetornaNullSinExcepcion()
        {
            using var env = new TestEnvironmentFixture(configurarStorageGlobal: false);
            string rutaCorrupta = Path.Combine(env.RootDirectory, "corrupta.jpg");
            await File.WriteAllBytesAsync(rutaCorrupta, Encoding.UTF8.GetBytes("contenido invalido"));

            BitmapImage? sync = ImageHelper.CargarSinBloqueo(rutaCorrupta, decodePixelWidth: 160);
            BitmapImage? asyncBmp = await ImageHelper.CargarSinBloqueoAsync(rutaCorrupta, decodePixelWidth: 160);

            Assert.Null(sync);
            Assert.Null(asyncBmp);
        }

        [Fact]
        public void ValidarImagen_SobrecargaStreamSincrona_RebobinaYValidaCorrectamente()
        {
            byte[] jpeg = CrearJpegValido(160, 120);
            using var ms = new MemoryStream(jpeg, writable: false);
            ms.Position = jpeg.Length; // Simular lector que olvidó rebobinar

            ValidacionImagenResultado ok = ImageHelper.ValidarImagen(ms, jpeg.Length);
            ValidacionImagenResultado nulo = ImageHelper.ValidarImagen((Stream?)null);

            Assert.True(ok.EsValida);
            Assert.Equal(FormatoImagenPermitido.Jpeg, ok.Formato);
            Assert.False(nulo.EsValida);
            Assert.Equal(400, nulo.CodigoHttp);
        }

        private static byte[] CrearJpegValido(int ancho, int alto)
        {
            BitmapSource fuente = CrearBitmapSintetico(ancho, alto);
            var encoder = new JpegBitmapEncoder { QualityLevel = 85 };
            encoder.Frames.Add(BitmapFrame.Create(fuente));
            using var ms = new MemoryStream();
            encoder.Save(ms);
            return ms.ToArray();
        }

        private static byte[] CrearPngValido(int ancho, int alto)
        {
            BitmapSource fuente = CrearBitmapSintetico(ancho, alto);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(fuente));
            using var ms = new MemoryStream();
            encoder.Save(ms);
            return ms.ToArray();
        }

        private static byte[] CrearBmpValido(int ancho, int alto)
        {
            BitmapSource fuente = CrearBitmapSintetico(ancho, alto);
            var encoder = new BmpBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(fuente));
            using var ms = new MemoryStream();
            encoder.Save(ms);
            return ms.ToArray();
        }

        private static byte[] CrearGifValido(int ancho, int alto)
        {
            BitmapSource fuente = CrearBitmapSintetico(ancho, alto);
            var encoder = new GifBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(fuente));
            using var ms = new MemoryStream();
            encoder.Save(ms);
            return ms.ToArray();
        }

        private static BitmapSource CrearBitmapSintetico(int ancho, int alto)
        {
            int stride = ancho * 4;
            byte[] pixels = new byte[alto * stride];
            for (int y = 0; y < alto; y++)
            {
                for (int x = 0; x < ancho; x++)
                {
                    int idx = y * stride + x * 4;
                    pixels[idx + 0] = (byte)((x * 3 + y) % 256);     // B
                    pixels[idx + 1] = (byte)((x + y * 5) % 256);     // G
                    pixels[idx + 2] = (byte)((x * 7 + y * 2) % 256); // R
                    pixels[idx + 3] = 255;                           // A
                }
            }

            var bmp = BitmapSource.Create(ancho, alto, 96, 96, PixelFormats.Bgra32, null, pixels, stride);
            bmp.Freeze();
            return bmp;
        }

        /// <summary>
        /// Genera un contenedor RIFF/WEBP VP8L (lossless) auténtico y conforme a RFC 9649.
        /// </summary>
        private static byte[] CrearWebpLosslessValido(int ancho, int alto)
        {
            var writer = new LsbBitWriter();
            writer.WriteBits(0, 1); // transform_present = 0
            writer.WriteBits(0, 1); // color_cache_used = 0
            writer.WriteBits(0, 1); // meta_prefix_codes_used = 0

            // 5 árboles Huffman simples con 1 símbolo cada uno (código de 0 bits por símbolo):
            // Green=100, Red=150, Blue=200, Alpha=255, Distance=0
            int[] simbolos = { 100, 150, 200, 255, 0 };
            foreach (int simbolo in simbolos)
            {
                writer.WriteBits(1, 1);       // simple_code = 1
                writer.WriteBits(0, 1);       // num_symbols - 1 = 0 (1 símbolo)
                writer.WriteBits(1, 1);       // is_first_8bits = 1
                writer.WriteBits(simbolo, 8); // valor del símbolo
            }

            byte[] bitstream = writer.ToArray();
            int vp8lPayloadSize = 1 + 4 + bitstream.Length;
            int chunkPaddedSize = vp8lPayloadSize + (vp8lPayloadSize % 2);
            int riffPayloadSize = 4 + 8 + chunkPaddedSize;

            byte[] webp = new byte[8 + riffPayloadSize];
            Encoding.ASCII.GetBytes("RIFF", webp.AsSpan(0, 4));
            BinaryPrimitives.WriteUInt32LittleEndian(webp.AsSpan(4, 4), (uint)riffPayloadSize);
            Encoding.ASCII.GetBytes("WEBP", webp.AsSpan(8, 4));
            Encoding.ASCII.GetBytes("VP8L", webp.AsSpan(12, 4));
            BinaryPrimitives.WriteUInt32LittleEndian(webp.AsSpan(16, 4), (uint)vp8lPayloadSize);

            webp[20] = 0x2F; // firma VP8L
            uint header32 = (uint)((ancho - 1) | ((alto - 1) << 14) | (1 << 28));
            BinaryPrimitives.WriteUInt32LittleEndian(webp.AsSpan(21, 4), header32);
            bitstream.CopyTo(webp.AsSpan(25));

            return webp;
        }

        private static byte[] CrearPngExactoDeTamano(int tamanoObjetivo)
        {
            byte[] pngBase = CrearPngValido(32, 32);
            const int iendLength = 12;
            int sinIend = pngBase.Length - iendLength;
            int payloadTextLen = tamanoObjetivo - sinIend - 12 - iendLength;

            byte[] resultado = new byte[tamanoObjetivo];
            Buffer.BlockCopy(pngBase, 0, resultado, 0, sinIend);

            int pos = sinIend;
            BinaryPrimitives.WriteUInt32BigEndian(resultado.AsSpan(pos, 4), (uint)payloadTextLen);
            pos += 4;

            int crcStart = pos;
            Encoding.ASCII.GetBytes("tEXt", resultado.AsSpan(pos, 4));
            pos += 4;

            byte[] clave = Encoding.ASCII.GetBytes("Meta\0");
            Buffer.BlockCopy(clave, 0, resultado, pos, clave.Length);
            resultado.AsSpan(pos + clave.Length, payloadTextLen - clave.Length).Fill((byte)'A');
            pos += payloadTextLen;

            uint crc = CalcularCrc32(resultado.AsSpan(crcStart, 4 + payloadTextLen));
            BinaryPrimitives.WriteUInt32BigEndian(resultado.AsSpan(pos, 4), crc);
            pos += 4;

            Buffer.BlockCopy(pngBase, sinIend, resultado, pos, iendLength);
            return resultado;
        }

        private static byte[] CrearRiffWebpSinChunkImagen()
        {
            byte[] webp = new byte[20];
            Encoding.ASCII.GetBytes("RIFF", webp.AsSpan(0, 4));
            BinaryPrimitives.WriteUInt32LittleEndian(webp.AsSpan(4, 4), 12);
            Encoding.ASCII.GetBytes("WEBP", webp.AsSpan(8, 4));
            Encoding.ASCII.GetBytes("VP8X", webp.AsSpan(12, 4));
            BinaryPrimitives.WriteUInt32LittleEndian(webp.AsSpan(16, 4), 0);
            return webp;
        }

        private static uint CalcularCrc32(ReadOnlySpan<byte> datos)
        {
            uint crc = 0xFFFFFFFFu;
            foreach (byte b in datos)
            {
                crc ^= b;
                for (int bit = 0; bit < 8; bit++)
                {
                    crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320u : crc >> 1;
                }
            }

            return ~crc;
        }

        private sealed class LsbBitWriter
        {
            private readonly List<byte> _bytes = new();
            private int _currentByte;
            private int _bitPos;

            public void WriteBits(int value, int bitCount)
            {
                for (int i = 0; i < bitCount; i++)
                {
                    if (((value >> i) & 1) != 0)
                    {
                        _currentByte |= (1 << _bitPos);
                    }

                    _bitPos++;
                    if (_bitPos == 8)
                    {
                        _bytes.Add((byte)_currentByte);
                        _currentByte = 0;
                        _bitPos = 0;
                    }
                }
            }

            public byte[] ToArray()
            {
                if (_bitPos > 0)
                {
                    _bytes.Add((byte)_currentByte);
                }

                return _bytes.ToArray();
            }
        }

        private sealed class StreamNoBuscable : Stream
        {
            private readonly MemoryStream _inner;

            public StreamNoBuscable(byte[] data) => _inner = new MemoryStream(data, writable: false);
            public override bool CanRead => true;
            public override bool CanSeek => false;
            public override bool CanWrite => false;
            public override long Length => throw new NotSupportedException();
            public override long Position
            {
                get => throw new NotSupportedException();
                set => throw new NotSupportedException();
            }
            public override void Flush() { }
            public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);
            public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
                => _inner.ReadAsync(buffer, cancellationToken);
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
            protected override void Dispose(bool disposing)
            {
                if (disposing) _inner.Dispose();
                base.Dispose(disposing);
            }
        }

        private sealed class StreamConSemaforo : Stream
        {
            private readonly MemoryStream _inner;
            private readonly Action _onFirstRead;
            private bool _invocado;

            public StreamConSemaforo(byte[] data, Action onFirstRead)
            {
                _inner = new MemoryStream(data, writable: false);
                _onFirstRead = onFirstRead;
            }

            public override bool CanRead => true;
            public override bool CanSeek => false;
            public override bool CanWrite => false;
            public override long Length => _inner.Length;
            public override long Position
            {
                get => _inner.Position;
                set => throw new NotSupportedException();
            }
            public override void Flush() { }
            public override int Read(byte[] buffer, int offset, int count)
            {
                if (!_invocado)
                {
                    _invocado = true;
                    _onFirstRead();
                }

                return _inner.Read(buffer, offset, count);
            }

            public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
            {
                if (!_invocado)
                {
                    _invocado = true;
                    _onFirstRead();
                }

                return _inner.ReadAsync(buffer, cancellationToken);
            }

            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
            protected override void Dispose(bool disposing)
            {
                if (disposing) _inner.Dispose();
                base.Dispose(disposing);
            }
        }
    }
}
