using System;
using System.Buffers.Text;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using CarreraApp.Data;
using CarreraApp.Helpers;
using CarreraApp.Models;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace CarreraApp.Server
{
    public class AsignacionDorsalRequest
    {
        public string IdFoto { get; set; } = string.Empty;
        public int Dorsal { get; set; }
    }

    public class DescarteFotoRequest
    {
        public string IdFoto { get; set; } = string.Empty;
    }

    public sealed class WebServerOptions
    {
        public int? PuertoHttp { get; set; } = 5000;
        public int? PuertoHttps { get; set; } = 5001;
        public bool HabilitarHttps { get; set; } = true;
        public IPAddress DireccionEscucha { get; set; } = IPAddress.Any;
        public string? WebRootPath { get; set; }
        public string? TokenSesion { get; set; }
        public bool RegenerarTokenAlIniciar { get; set; }
        public Func<Carrera?>? ObtenerCarreraActiva { get; set; }
        public Func<ApplicationDbContext>? DbContextFactory { get; set; }
        public Func<int, string, int, DateTime, string, CancellationToken, Task<AsignacionResultado>>? AsignarTiempoFunc { get; set; }
    }

    public static class WebServer
    {
        private const int LongitudTokenBytes = 12; // 12 bytes Base64Url => 16 caracteres URL-safe
        private const int LimiteOverheadMultipart = 256 * 1024;
        private const int LimiteCuerpoMultipart = ImageHelper.TamanoMaximoBytes + LimiteOverheadMultipart;
        private const int LimiteMaximoKestrelBytes = 32 * 1024 * 1024;

        private static readonly Regex RegexIdFotoSeguro = new("^[a-zA-Z0-9_-]{1,64}$", RegexOptions.Compiled);
        private static readonly Regex RegexTempIdSeguro = new("^[a-zA-Z0-9_-]{1,128}$", RegexOptions.Compiled);

        private static readonly object _tokenLock = new();
        private static string? _tokenSesionActual;

        private static readonly SemaphoreSlim _lifecycleLock = new(1, 1);
        private static readonly SemaphoreSlim _colaStorageLock = new(1, 1);
        private static readonly ConcurrentDictionary<string, EstadoFotoPendiente> _fotosPendientes = new(StringComparer.Ordinal);
        private static readonly object _colaCtsLock = new();
        private static CancellationTokenSource _colaCts = new();

        private static WebApplication? _appActual;
        private static CancellationTokenSource? _servidorCts;
        private static Task? _tareaServidorActual;

        public static event Action<string, DateTime, string>? NuevaFotoRecibida;
        public static event Action<string, int, AsignacionResultado>? FotoAsignadaRemota;
        public static event Action<string>? FotoDescartadaRemota;
        public static event Action<Exception>? ErrorCriticoServidor;

#pragma warning disable CS0067
        // Conservado temporalmente por compatibilidad de firma con MainWindow hasta T9.
        public static event Action<string, int>? DorsalAsignadoRemoto;
#pragma warning restore CS0067

        public static string TokenSesionActual
        {
            get
            {
                lock (_tokenLock)
                {
                    _tokenSesionActual ??= GenerarTokenSesionSeguro();
                    return _tokenSesionActual;
                }
            }
        }

        public static int? PuertoHttpActivo { get; private set; }
        public static int? PuertoHttpsActivo { get; private set; }
        public static bool EstaActivo { get; private set; }

        public static string? UrlBaseHttpActivo =>
            PuertoHttpActivo is int port ? $"http://127.0.0.1:{port}" : null;

        public static string? UrlBaseHttpsActivo =>
            PuertoHttpsActivo is int port ? $"https://127.0.0.1:{port}" : null;

        /// <summary>
        /// Genera un token aleatorio criptográfico URL-safe de 16 caracteres (12 bytes en Base64Url).
        /// </summary>
        public static string GenerarTokenSesionSeguro()
        {
            byte[] bytes = RandomNumberGenerator.GetBytes(LongitudTokenBytes);
            return Base64Url.EncodeToString(bytes);
        }

        /// <summary>
        /// Regenera el token de sesión vigente (útil en pruebas de cambio de sesión o reinicio).
        /// </summary>
        public static string RegenerarTokenSesion()
        {
            lock (_tokenLock)
            {
                _tokenSesionActual = GenerarTokenSesionSeguro();
                return _tokenSesionActual;
            }
        }

        /// <summary>
        /// Valida el token recibido comparando su resumen SHA-256 en tiempo constante.
        /// </summary>
        public static bool ValidarToken(string? tokenRecibido)
        {
            if (string.IsNullOrWhiteSpace(tokenRecibido))
            {
                return false;
            }

            string esperado = TokenSesionActual;
            Span<byte> hashRecibido = stackalloc byte[32];
            Span<byte> hashEsperado = stackalloc byte[32];

            byte[] bytesRecibidos = Encoding.UTF8.GetBytes(tokenRecibido);
            byte[] bytesEsperados = Encoding.UTF8.GetBytes(esperado);

            SHA256.HashData(bytesRecibidos, hashRecibido);
            SHA256.HashData(bytesEsperados, hashEsperado);

            return CryptographicOperations.FixedTimeEquals(hashRecibido, hashEsperado);
        }

        public static bool EsIdFotoValido(string? idFoto)
        {
            if (string.IsNullOrWhiteSpace(idFoto) || idFoto.Length > 64)
            {
                return false;
            }

            if (idFoto.Contains("..", StringComparison.Ordinal) ||
                idFoto.Contains('/') ||
                idFoto.Contains('\\'))
            {
                return false;
            }

            return RegexIdFotoSeguro.IsMatch(idFoto);
        }

        public static Task IniciarServidorAsync() =>
            IniciarServidorAsync(null, CancellationToken.None);

        public static async Task IniciarServidorAsync(
            WebServerOptions? options,
            CancellationToken cancellationToken = default)
        {
            Task? tareaServidor;
            try
            {
                tareaServidor = await IniciarInternoAsync(options, cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                // ErrorCriticoServidor ya fue invocado durante el intento de arranque.
                return;
            }

            if (tareaServidor != null)
            {
                await tareaServidor.ConfigureAwait(false);
            }
        }

        /// <summary>
        /// Inicia Kestrel en segundo plano y espera hasta que los puertos estén vinculados y listos.
        /// </summary>
        public static async Task<IAsyncDisposable> IniciarEnSegundoPlanoAsync(
            WebServerOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            await IniciarInternoAsync(options, cancellationToken).ConfigureAwait(false);
            return new SesionServidorHandle();
        }

        public static async Task DetenerServidorAsync()
        {
            await _lifecycleLock.WaitAsync().ConfigureAwait(false);
            try
            {
                await DetenerInternoSinLockAsync().ConfigureAwait(false);
            }
            finally
            {
                _lifecycleLock.Release();
            }
        }

        public static bool IntentarAdquirirFotoParaLocal(string idFoto) =>
            IntentarAdquirirFotoParaLocal(idFoto, out _, out _);

        public static bool IntentarAdquirirFotoParaLocal(
            string idFoto,
            out DateTime horaLlegada,
            out string rutaFoto)
        {
            horaLlegada = default;
            rutaFoto = string.Empty;

            if (!EsIdFotoValido(idFoto))
            {
                return false;
            }

            (EstadoAdquisicionFoto estado, EstadoFotoPendiente? entrada) = IntentarAdquirirEntrada(idFoto);
            if (estado != EstadoAdquisicionFoto.Adquirida || entrada == null)
            {
                return false;
            }

            horaLlegada = entrada.HoraLlegada;
            rutaFoto = entrada.RutaFoto;
            return true;
        }

        public static void LiberarFotoLocal(string idFoto)
        {
            if (string.IsNullOrWhiteSpace(idFoto))
            {
                return;
            }

            if (_fotosPendientes.TryGetValue(idFoto, out EstadoFotoPendiente? entrada))
            {
                LiberarEntrada(entrada);
            }
        }

        public static void EliminarFotoPendiente(string idFoto)
        {
            if (string.IsNullOrWhiteSpace(idFoto))
            {
                return;
            }

            if (_fotosPendientes.TryGetValue(idFoto, out EstadoFotoPendiente? entrada))
            {
                FinalizarYEliminarEntrada(entrada);
            }
        }

        /// <summary>
        /// Limpia toda la cola en memoria de fotografías pendientes y elimina archivos temporales
        /// en Fotos_Temp (p. ej. al cambiar de carrera activa).
        /// </summary>
        public static int LimpiarColaPendientes()
        {
            _colaStorageLock.Wait();
            try
            {
                CancellationTokenSource nuevoCts = new();
                CancellationTokenSource antiguoCts;
                lock (_colaCtsLock)
                {
                    antiguoCts = _colaCts;
                    _colaCts = nuevoCts;
                }

                try
                {
                    antiguoCts.Cancel();
                }
                catch
                {
                    // Ignorar cancelación ya ejecutada
                }
                finally
                {
                    antiguoCts.Dispose();
                }

                int eliminadas = 0;
                foreach (KeyValuePair<string, EstadoFotoPendiente> par in _fotosPendientes.ToArray())
                {
                    EstadoFotoPendiente entrada = par.Value;
                    FinalizarYEliminarEntrada(entrada);
                    IntentarEliminarArchivo(entrada.RutaFoto);
                    eliminadas++;
                }

                eliminadas += StorageHelper.LimpiarFotosTempHuerfanas();
                return eliminadas;
            }
            finally
            {
                _colaStorageLock.Release();
            }
        }

        public static IReadOnlyCollection<string> ObtenerIdsFotosPendientes() =>
            _fotosPendientes.Keys.ToArray();

        private static async Task<Task?> IniciarInternoAsync(
            WebServerOptions? options,
            CancellationToken cancellationToken)
        {
            WebServerOptions opciones = options ?? new WebServerOptions();

            await _lifecycleLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (_appActual != null || _tareaServidorActual != null)
                {
                    await DetenerInternoSinLockAsync().ConfigureAwait(false);
                }

                ConfigurarTokenParaArranque(opciones);

                CancellationTokenSource servidorCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                var readyTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

                _servidorCts = servidorCts;
                Task tareaServidor = Task.Run(
                    () => EjecutarServidorAsync(opciones, servidorCts.Token, readyTcs),
                    CancellationToken.None);
                _tareaServidorActual = tareaServidor;

                await readyTcs.Task.ConfigureAwait(false);
                return tareaServidor;
            }
            finally
            {
                _lifecycleLock.Release();
            }
        }

        private static async Task DetenerInternoSinLockAsync()
        {
            CancellationTokenSource? cts = _servidorCts;
            Task? tarea = _tareaServidorActual;

            _servidorCts = null;
            _tareaServidorActual = null;

            if (cts != null)
            {
                try
                {
                    await cts.CancelAsync().ConfigureAwait(false);
                }
                catch
                {
                    // Ignorar errores de cancelación al apagar
                }
            }

            if (tarea != null)
            {
                try
                {
                    await tarea.ConfigureAwait(false);
                }
                catch
                {
                    // Ignorar errores ya reportados durante el apagado
                }
            }

            cts?.Dispose();
            PuertoHttpActivo = null;
            PuertoHttpsActivo = null;
            EstaActivo = false;
        }

        private static async Task EjecutarServidorAsync(
            WebServerOptions opciones,
            CancellationToken shutdownToken,
            TaskCompletionSource readyTcs)
        {
            WebApplication? app = null;
            try
            {
                if (!opciones.PuertoHttp.HasValue && !(opciones.HabilitarHttps && opciones.PuertoHttps.HasValue))
                {
                    throw new InvalidOperationException("Debe configurarse al menos un puerto HTTP o HTTPS para iniciar el servidor.");
                }

                string? webRoot = ResolverWebRoot(opciones.WebRootPath);
                var builder = WebApplication.CreateBuilder(new WebApplicationOptions
                {
                    Args = Array.Empty<string>(),
                    ContentRootPath = AppDomain.CurrentDomain.BaseDirectory,
                    WebRootPath = webRoot
                });

                builder.Logging.SetMinimumLevel(LogLevel.Warning);

                builder.Services.Configure<HostOptions>(hostOptions =>
                {
                    hostOptions.ShutdownTimeout = TimeSpan.FromSeconds(2);
                });

                builder.Services.Configure<FormOptions>(formOptions =>
                {
                    formOptions.MemoryBufferThreshold = LimiteCuerpoMultipart;
                    formOptions.MultipartBodyLengthLimit = LimiteCuerpoMultipart;
                    formOptions.ValueLengthLimit = 64 * 1024;
                });

                builder.WebHost.ConfigureKestrel(kestrelOptions =>
                {
                    kestrelOptions.Limits.MaxRequestBodySize = LimiteMaximoKestrelBytes;
                    ConfigurarPuertosKestrel(kestrelOptions, opciones);
                });

                app = builder.Build();
                _appActual = app;

                if (!string.IsNullOrEmpty(webRoot) && Directory.Exists(webRoot))
                {
                    app.UseDefaultFiles();
                    app.UseStaticFiles();
                }

                RegistrarMiddlewareAutenticacion(app);
                MapearEndpointsApi(app, opciones, shutdownToken);

                await app.StartAsync(shutdownToken).ConfigureAwait(false);

                ActualizarPuertosActivos(app);
                EstaActivo = true;
                readyTcs.TrySetResult();

                await EsperarCancelacionAsync(shutdownToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (shutdownToken.IsCancellationRequested && !readyTcs.Task.IsCompleted)
            {
                var cancelEx = new OperationCanceledException("El inicio del servidor web fue cancelado.", shutdownToken);
                readyTcs.TrySetException(cancelEx);
            }
            catch (Exception ex)
            {
                Trace.TraceError($"[WebServer] Error crítico en servidor web: {ex}");
                NotificarErrorCritico(ex);
                readyTcs.TrySetException(ex);
            }
            finally
            {
                EstaActivo = false;
                PuertoHttpActivo = null;
                PuertoHttpsActivo = null;

                if (app != null)
                {
                    try
                    {
                        using var stopTimeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                        await app.StopAsync(stopTimeoutCts.Token).ConfigureAwait(false);
                    }
                    catch
                    {
                        // Tolerar timeout al cerrar conexiones keep-alive
                    }

                    try
                    {
                        await app.DisposeAsync().ConfigureAwait(false);
                    }
                    catch
                    {
                        // Ignorar fallo en DisposeAsync al apagar
                    }
                }

                if (ReferenceEquals(_appActual, app))
                {
                    _appActual = null;
                }
            }
        }

        private static void ConfigurarTokenParaArranque(WebServerOptions opciones)
        {
            lock (_tokenLock)
            {
                if (!string.IsNullOrWhiteSpace(opciones.TokenSesion))
                {
                    _tokenSesionActual = opciones.TokenSesion.Trim();
                }
                else if (opciones.RegenerarTokenAlIniciar || string.IsNullOrWhiteSpace(_tokenSesionActual))
                {
                    _tokenSesionActual = GenerarTokenSesionSeguro();
                }
            }
        }

        private static void ConfigurarPuertosKestrel(
            Microsoft.AspNetCore.Server.Kestrel.Core.KestrelServerOptions kestrelOptions,
            WebServerOptions opciones)
        {
            IPAddress direccion = opciones.DireccionEscucha ?? IPAddress.Any;

            if (opciones.PuertoHttp.HasValue)
            {
                if (direccion.Equals(IPAddress.Any))
                {
                    kestrelOptions.ListenAnyIP(opciones.PuertoHttp.Value);
                }
                else
                {
                    kestrelOptions.Listen(direccion, opciones.PuertoHttp.Value);
                }
            }

            if (opciones.HabilitarHttps && opciones.PuertoHttps.HasValue)
            {
                X509Certificate2 certificado = CrearCertificadoAutofirmadoLocal();
                void ConfigurarHttps(Microsoft.AspNetCore.Server.Kestrel.Core.ListenOptions listenOptions) =>
                    listenOptions.UseHttps(certificado);

                if (direccion.Equals(IPAddress.Any))
                {
                    kestrelOptions.ListenAnyIP(opciones.PuertoHttps.Value, ConfigurarHttps);
                }
                else
                {
                    kestrelOptions.Listen(direccion, opciones.PuertoHttps.Value, ConfigurarHttps);
                }
            }
        }

        private static X509Certificate2 CrearCertificadoAutofirmadoLocal()
        {
            using var rsa = RSA.Create(2048);
            var req = new CertificateRequest(
                "cn=CarreraAppLocal",
                rsa,
                HashAlgorithmName.SHA256,
                RSASignaturePadding.Pkcs1);

            req.CertificateExtensions.Add(
                new X509EnhancedKeyUsageExtension(new OidCollection { new Oid("1.3.6.1.5.5.7.3.1") }, false));
            req.CertificateExtensions.Add(
                new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, false));

            var sanBuilder = new SubjectAlternativeNameBuilder();
            sanBuilder.AddIpAddress(IPAddress.Loopback);
            sanBuilder.AddIpAddress(IPAddress.IPv6Loopback);

            try
            {
                IPHostEntry host = Dns.GetHostEntry(Dns.GetHostName());
                foreach (IPAddress ip in host.AddressList)
                {
                    if (ip.AddressFamily == AddressFamily.InterNetwork)
                    {
                        sanBuilder.AddIpAddress(ip);
                    }
                }
            }
            catch
            {
                // Mantener al menos loopback si la resolución DNS local falla
            }

            req.CertificateExtensions.Add(sanBuilder.Build());

            using var certTemp = req.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));
            byte[] pfxBytes = certTemp.Export(X509ContentType.Pfx, "password123");
            return X509CertificateLoader.LoadPkcs12(pfxBytes, "password123", X509KeyStorageFlags.Exportable);
        }

        private static string? ResolverWebRoot(string? rutaPersonalizada)
        {
            if (!string.IsNullOrWhiteSpace(rutaPersonalizada) && Directory.Exists(rutaPersonalizada))
            {
                return Path.GetFullPath(rutaPersonalizada);
            }

            string[] candidatas =
            {
                Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "wwwroot"),
                Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", "wwwroot")),
                Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", "..", "..", "src", "CarreraApp", "wwwroot"))
            };

            return candidatas.FirstOrDefault(Directory.Exists);
        }

        private static void ActualizarPuertosActivos(WebApplication app)
        {
            int? httpPort = null;
            int? httpsPort = null;

            IServerAddressesFeature? addressesFeature = app.Services
                .GetRequiredService<IServer>()
                .Features
                .Get<IServerAddressesFeature>();

            if (addressesFeature != null)
            {
                foreach (string rawAddress in addressesFeature.Addresses)
                {
                    string normalized = rawAddress
                        .Replace("[::]", "127.0.0.1", StringComparison.Ordinal)
                        .Replace("0.0.0.0", "127.0.0.1", StringComparison.Ordinal)
                        .Replace("*", "127.0.0.1", StringComparison.Ordinal);

                    if (!Uri.TryCreate(normalized, UriKind.Absolute, out Uri? uri))
                    {
                        continue;
                    }

                    if (string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase))
                    {
                        httpPort ??= uri.Port;
                    }
                    else if (string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
                    {
                        httpsPort ??= uri.Port;
                    }
                }
            }

            PuertoHttpActivo = httpPort;
            PuertoHttpsActivo = httpsPort;
        }

        private static void RegistrarMiddlewareAutenticacion(WebApplication app)
        {
            app.Use(async (context, next) =>
            {
                if (context.Request.Path.StartsWithSegments("/api", StringComparison.OrdinalIgnoreCase))
                {
                    string? token = ExtraerTokenPeticion(context.Request);
                    if (!ValidarToken(token))
                    {
                        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                        await context.Response.WriteAsJsonAsync(new
                        {
                            error = "No autorizado",
                            mensaje = "Credencial de sesión faltante o inválida. Reescanea el código QR de la mesa."
                        }).ConfigureAwait(false);
                        return;
                    }
                }

                await next().ConfigureAwait(false);
            });
        }

        private static string? ExtraerTokenPeticion(HttpRequest request)
        {
            if (request.Headers.TryGetValue("X-Auth-Token", out var headerValues) && headerValues.Count > 0)
            {
                return headerValues.Count == 1 ? headerValues[0] : null;
            }

            if (request.Query.TryGetValue("token", out var queryValues) && queryValues.Count > 0)
            {
                return queryValues.Count == 1 ? queryValues[0] : null;
            }

            return null;
        }

        private static void MapearEndpointsApi(
            WebApplication app,
            WebServerOptions opciones,
            CancellationToken shutdownToken)
        {
            Func<Carrera?> obtenerCarreraActiva = opciones.ObtenerCarreraActiva ?? (() => AppState.CarreraActiva);
            Func<int, string, int, DateTime, string, CancellationToken, Task<AsignacionResultado>> asignarTiempoFunc =
                opciones.AsignarTiempoFunc ??
                ((idCarrera, nombreCarrera, dorsal, horaLlegada, rutaFoto, ct) =>
                {
                    var service = new RaceDataService(opciones.DbContextFactory);
                    return service.AsignarTiempoAsync(idCarrera, nombreCarrera, dorsal, horaLlegada, rutaFoto, ct);
                });

            app.MapPost("/api/upload-photo", async (HttpRequest request, CancellationToken ct) =>
            {
                using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, shutdownToken);
                return await ManejarUploadPhotoAsync(request, linkedCts.Token).ConfigureAwait(false);
            });

            app.MapGet("/api/pending-photos", () =>
                Results.Ok(_fotosPendientes.Keys.ToArray()));

            app.MapPost("/api/assign-dorsal", async (HttpRequest request, CancellationToken ct) =>
            {
                using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, shutdownToken);
                return await ManejarAssignDorsalAsync(
                    request,
                    obtenerCarreraActiva,
                    asignarTiempoFunc,
                    linkedCts.Token).ConfigureAwait(false);
            });

            app.MapPost("/api/discard-photo", async (HttpRequest request, CancellationToken ct) =>
            {
                using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, shutdownToken);
                return await ManejarDiscardPhotoAsync(request, linkedCts.Token).ConfigureAwait(false);
            });
        }

        private static async Task<IResult> ManejarUploadPhotoAsync(HttpRequest request, CancellationToken ct)
        {
            // Capturar la hora exacta de llegada en el reloj maestro del servidor antes de leer/decodificar
            DateTime horaLlegada = DateTime.Now;

            if (!request.HasFormContentType)
            {
                return CrearRespuestaError(StatusCodes.Status400BadRequest, "La solicitud debe tener formato multipart/form-data.");
            }

            if (request.ContentLength.HasValue && request.ContentLength.Value > LimiteCuerpoMultipart)
            {
                await DrenarFlujoSilenciosamenteAsync(request.Body, LimiteMaximoKestrelBytes, ct).ConfigureAwait(false);
                return CrearRespuestaError(
                    StatusCodes.Status413PayloadTooLarge,
                    $"La imagen supera el tamaño máximo permitido de {ImageHelper.TamanoMaximoBytes / (1024 * 1024)} MB.");
            }

            IFormCollection form;
            try
            {
                form = await request.ReadFormAsync(ct).ConfigureAwait(false);
            }
            catch (BadHttpRequestException ex) when (ex.StatusCode == StatusCodes.Status413PayloadTooLarge)
            {
                return CrearRespuestaError(
                    StatusCodes.Status413PayloadTooLarge,
                    $"La imagen supera el tamaño máximo permitido de {ImageHelper.TamanoMaximoBytes / (1024 * 1024)} MB.");
            }
            catch (InvalidDataException ex) when (ex.Message.Contains("length limit", StringComparison.OrdinalIgnoreCase))
            {
                return CrearRespuestaError(
                    StatusCodes.Status413PayloadTooLarge,
                    $"La imagen supera el tamaño máximo permitido de {ImageHelper.TamanoMaximoBytes / (1024 * 1024)} MB.");
            }
            catch (InvalidDataException)
            {
                return CrearRespuestaError(StatusCodes.Status400BadRequest, "Cuerpo multipart inválido o incompleto.");
            }
            catch (IOException)
            {
                return CrearRespuestaError(StatusCodes.Status400BadRequest, "No se pudo leer el contenido de la fotografía enviada.");
            }

            string tempId = form["tempId"].ToString().Trim();
            if (string.IsNullOrEmpty(tempId) || !RegexTempIdSeguro.IsMatch(tempId))
            {
                return CrearRespuestaError(StatusCodes.Status400BadRequest, "El parámetro 'tempId' es obligatorio y debe ser alfanumérico.");
            }

            IFormFile? file = form.Files.GetFile("foto");
            if (file == null)
            {
                return CrearRespuestaError(StatusCodes.Status400BadRequest, "No se recibió el archivo en el campo 'foto'.");
            }

            if (file.Length <= 0)
            {
                return CrearRespuestaError(StatusCodes.Status400BadRequest, "El archivo de imagen está vacío.");
            }

            if (file.Length > ImageHelper.TamanoMaximoBytes)
            {
                return CrearRespuestaError(
                    StatusCodes.Status413PayloadTooLarge,
                    $"La imagen supera el tamaño máximo permitido de {ImageHelper.TamanoMaximoBytes / (1024 * 1024)} MB.");
            }

            // Leer y validar íntegramente en memoria antes de escribir cualquier archivo en disco
            using var memoryStream = new MemoryStream((int)Math.Min(file.Length, ImageHelper.TamanoMaximoBytes));
            byte[] buffer = new byte[81920];
            int totalLeido = 0;

            await using (Stream fileStream = file.OpenReadStream())
            {
                while (true)
                {
                    int porLeer = Math.Min(buffer.Length, (ImageHelper.TamanoMaximoBytes + 1) - totalLeido);
                    int leidos = await fileStream.ReadAsync(buffer.AsMemory(0, porLeer), ct).ConfigureAwait(false);
                    if (leidos <= 0)
                    {
                        break;
                    }

                    totalLeido += leidos;
                    if (totalLeido > ImageHelper.TamanoMaximoBytes)
                    {
                        return CrearRespuestaError(
                            StatusCodes.Status413PayloadTooLarge,
                            $"La imagen supera el tamaño máximo permitido de {ImageHelper.TamanoMaximoBytes / (1024 * 1024)} MB.");
                    }

                    memoryStream.Write(buffer, 0, leidos);
                }
            }

            if (totalLeido == 0)
            {
                return CrearRespuestaError(StatusCodes.Status400BadRequest, "El archivo de imagen está vacío.");
            }

            memoryStream.Position = 0;
            ValidacionImagenResultado validacion = await ImageHelper
                .ValidarImagenAsync(memoryStream, totalLeido, ct)
                .ConfigureAwait(false);

            if (!validacion.EsValida)
            {
                return CrearRespuestaError(validacion.CodigoHttp, validacion.MensajeError);
            }

            string serverId = Guid.NewGuid().ToString();
            string rutaArchivo;

            await _colaStorageLock.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                string tempFolder = StorageHelper.ObtenerCarpetaFotosTemp();
                rutaArchivo = Path.Combine(tempFolder, $"{serverId}{validacion.ExtensionReal}");

                try
                {
                    memoryStream.Position = 0;
                    await using (var fs = new FileStream(
                        rutaArchivo,
                        FileMode.CreateNew,
                        FileAccess.Write,
                        FileShare.None,
                        81920,
                        useAsync: true))
                    {
                        await memoryStream.CopyToAsync(fs, ct).ConfigureAwait(false);
                        await fs.FlushAsync(ct).ConfigureAwait(false);
                    }
                }
                catch
                {
                    IntentarEliminarArchivo(rutaArchivo);
                    throw;
                }

                var entrada = new EstadoFotoPendiente
                {
                    IdFoto = serverId,
                    HoraLlegada = horaLlegada,
                    RutaFoto = rutaArchivo
                };

                _fotosPendientes[serverId] = entrada;
            }
            finally
            {
                _colaStorageLock.Release();
            }

            InvocarEventoSeguro(() => NuevaFotoRecibida?.Invoke(serverId, horaLlegada, rutaArchivo));

            return Results.Ok(new
            {
                idFoto = serverId,
                formato = validacion.ExtensionReal,
                horaLlegada
            });
        }

        private static async Task<IResult> ManejarAssignDorsalAsync(
            HttpRequest request,
            Func<Carrera?> obtenerCarreraActiva,
            Func<int, string, int, DateTime, string, CancellationToken, Task<AsignacionResultado>> asignarTiempoFunc,
            CancellationToken ct)
        {
            (bool jsonValido, AsignacionDorsalRequest? data, string errorJson) =
                await LeerJsonSeguroAsync<AsignacionDorsalRequest>(request, ct).ConfigureAwait(false);

            if (!jsonValido || data == null)
            {
                return CrearRespuestaError(StatusCodes.Status400BadRequest, errorJson);
            }

            if (!EsIdFotoValido(data.IdFoto))
            {
                return CrearRespuestaError(StatusCodes.Status400BadRequest, "Identificador de fotografía inválido.");
            }

            if (data.Dorsal <= 0)
            {
                return CrearRespuestaError(StatusCodes.Status400BadRequest, "El dorsal debe ser un número entero positivo.");
            }

            // RF-12: rechazar explícitamente si no hay carrera activa en mesa (sin auto-seleccionar ninguna de BD)
            Carrera? carreraActiva = obtenerCarreraActiva();
            if (carreraActiva == null)
            {
                return CrearRespuestaError(
                    StatusCodes.Status422UnprocessableEntity,
                    "No hay una carrera activa seleccionada en la mesa de cronometraje.");
            }

            (EstadoAdquisicionFoto estado, EstadoFotoPendiente? entrada) = IntentarAdquirirEntrada(data.IdFoto);
            if (estado == EstadoAdquisicionFoto.EnConflicto)
            {
                return CrearRespuestaError(
                    StatusCodes.Status409Conflict,
                    "La fotografía ya está siendo procesada por otro dispositivo u operador.");
            }

            if (estado == EstadoAdquisicionFoto.NoEncontrada || entrada == null)
            {
                return CrearRespuestaError(StatusCodes.Status404NotFound, "Fotografía no encontrada o ya procesada.");
            }

            CancellationToken colaToken = ObtenerTokenColaActual();
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, colaToken);

            try
            {
                AsignacionResultado resultado = await asignarTiempoFunc(
                    carreraActiva.ID_Carrera,
                    carreraActiva.Nombre,
                    data.Dorsal,
                    entrada.HoraLlegada,
                    entrada.RutaFoto,
                    linkedCts.Token).ConfigureAwait(false);

                if (resultado.Exito)
                {
                    FinalizarYEliminarEntrada(entrada);
                    InvocarEventoSeguro(() => FotoAsignadaRemota?.Invoke(entrada.IdFoto, data.Dorsal, resultado));

                    return Results.Ok(new
                    {
                        mensaje = resultado.Mensaje,
                        tiempoFormateado = resultado.TiempoFormateado,
                        hitFinalizado = resultado.HitFinalizado,
                        nombreHitFinalizado = resultado.NombreHitFinalizado
                    });
                }

                // Fallo de validación o persistencia: devolver a estado Pendiente para permitir reintento
                LiberarEntrada(entrada);
                int status = resultado.StatusCode >= 400 ? resultado.StatusCode : StatusCodes.Status422UnprocessableEntity;
                return CrearRespuestaError(status, resultado.Mensaje);
            }
            catch (OperationCanceledException) when (colaToken.IsCancellationRequested)
            {
                LiberarEntrada(entrada);
                return CrearRespuestaError(
                    StatusCodes.Status409Conflict,
                    "La cola pendiente se reinició por un cambio de carrera activa.");
            }
            catch (Exception ex)
            {
                LiberarEntrada(entrada);
                Trace.TraceError($"[WebServer] Error al procesar asignación remota para {entrada.IdFoto}: {ex}");
                return CrearRespuestaError(
                    StatusCodes.Status500InternalServerError,
                    "Error interno al registrar el tiempo en la base de datos.");
            }
        }

        private static async Task<IResult> ManejarDiscardPhotoAsync(HttpRequest request, CancellationToken ct)
        {
            (bool jsonValido, DescarteFotoRequest? data, string errorJson) =
                await LeerJsonSeguroAsync<DescarteFotoRequest>(request, ct).ConfigureAwait(false);

            if (!jsonValido || data == null)
            {
                return CrearRespuestaError(StatusCodes.Status400BadRequest, errorJson);
            }

            if (!EsIdFotoValido(data.IdFoto))
            {
                return CrearRespuestaError(StatusCodes.Status400BadRequest, "Identificador de fotografía inválido.");
            }

            (EstadoAdquisicionFoto estado, EstadoFotoPendiente? entrada) = IntentarAdquirirEntrada(data.IdFoto);
            if (estado == EstadoAdquisicionFoto.EnConflicto)
            {
                return CrearRespuestaError(
                    StatusCodes.Status409Conflict,
                    "La fotografía está siendo procesada en este momento.");
            }

            if (estado == EstadoAdquisicionFoto.NoEncontrada || entrada == null)
            {
                return CrearRespuestaError(StatusCodes.Status404NotFound, "Fotografía no encontrada o ya procesada.");
            }

            IntentarEliminarArchivo(entrada.RutaFoto);
            FinalizarYEliminarEntrada(entrada);
            InvocarEventoSeguro(() => FotoDescartadaRemota?.Invoke(entrada.IdFoto));

            return Results.Ok(new { mensaje = "Fotografía descartada correctamente." });
        }

        private static async Task<(bool Valido, T? Valor, string MensajeError)> LeerJsonSeguroAsync<T>(
            HttpRequest request,
            CancellationToken ct) where T : class
        {
            try
            {
                T? data = await request.ReadFromJsonAsync<T>(ct).ConfigureAwait(false);
                if (data == null)
                {
                    return (false, null, "El cuerpo JSON de la solicitud no puede ser nulo.");
                }

                return (true, data, string.Empty);
            }
            catch (JsonException)
            {
                return (false, null, "El cuerpo de la solicitud no tiene un formato JSON válido.");
            }
            catch (BadHttpRequestException ex)
            {
                return (false, null, $"Solicitud HTTP inválida: {ex.Message}");
            }
            catch (InvalidOperationException)
            {
                return (false, null, "Se esperaba una solicitud con Content-Type application/json.");
            }
        }

        private static IResult CrearRespuestaError(int statusCode, string mensaje) =>
            Results.Json(new { mensaje }, statusCode: statusCode);

        private static (EstadoAdquisicionFoto Estado, EstadoFotoPendiente? Entrada) IntentarAdquirirEntrada(string idFoto)
        {
            if (!_fotosPendientes.TryGetValue(idFoto, out EstadoFotoPendiente? entrada))
            {
                return (EstadoAdquisicionFoto.NoEncontrada, null);
            }

            lock (entrada.SyncRoot)
            {
                if (entrada.Eliminada)
                {
                    return (EstadoAdquisicionFoto.NoEncontrada, null);
                }

                if (entrada.EnProceso)
                {
                    return (EstadoAdquisicionFoto.EnConflicto, null);
                }

                entrada.EnProceso = true;
                return (EstadoAdquisicionFoto.Adquirida, entrada);
            }
        }

        private static void LiberarEntrada(EstadoFotoPendiente entrada)
        {
            lock (entrada.SyncRoot)
            {
                if (!entrada.Eliminada)
                {
                    entrada.EnProceso = false;
                }
            }
        }

        private static void FinalizarYEliminarEntrada(EstadoFotoPendiente entrada)
        {
            lock (entrada.SyncRoot)
            {
                entrada.Eliminada = true;
                entrada.EnProceso = false;
            }

            _fotosPendientes.TryRemove(new KeyValuePair<string, EstadoFotoPendiente>(entrada.IdFoto, entrada));
        }

        private static CancellationToken ObtenerTokenColaActual()
        {
            lock (_colaCtsLock)
            {
                return _colaCts.Token;
            }
        }

        private static async Task DrenarFlujoSilenciosamenteAsync(Stream stream, int maxBytes, CancellationToken ct)
        {
            try
            {
                byte[] buffer = new byte[16384];
                int leido = 0;
                while (leido < maxBytes)
                {
                    int n = await stream.ReadAsync(buffer, ct).ConfigureAwait(false);
                    if (n <= 0)
                    {
                        break;
                    }

                    leido += n;
                }
            }
            catch
            {
                // Ignorar errores al drenar flujos sobredimensionados
            }
        }

        private static void IntentarEliminarArchivo(string ruta)
        {
            if (string.IsNullOrWhiteSpace(ruta))
            {
                return;
            }

            try
            {
                if (File.Exists(ruta))
                {
                    File.Delete(ruta);
                }
            }
            catch (IOException)
            {
                // Tolerar bloqueo transitorio del sistema de archivos
            }
            catch (UnauthorizedAccessException)
            {
                // Tolerar error de permisos al limpiar temporal
            }
        }

        private static void InvocarEventoSeguro(Action accion)
        {
            try
            {
                accion();
            }
            catch (Exception ex)
            {
                Trace.TraceError($"[WebServer] Error en suscriptor de evento del servidor: {ex}");
            }
        }

        private static void NotificarErrorCritico(Exception ex)
        {
            InvocarEventoSeguro(() => ErrorCriticoServidor?.Invoke(ex));
        }

        private static async Task EsperarCancelacionAsync(CancellationToken cancellationToken)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return;
            }

            var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using CancellationTokenRegistration reg = cancellationToken.Register(
                static state => ((TaskCompletionSource)state!).TrySetResult(),
                tcs);
            await tcs.Task.ConfigureAwait(false);
        }

        private enum EstadoAdquisicionFoto
        {
            Adquirida = 0,
            NoEncontrada = 1,
            EnConflicto = 2
        }

        private sealed class EstadoFotoPendiente
        {
            public required string IdFoto { get; init; }
            public required DateTime HoraLlegada { get; init; }
            public required string RutaFoto { get; init; }
            public object SyncRoot { get; } = new();
            public bool EnProceso { get; set; }
            public bool Eliminada { get; set; }
        }

        private sealed class SesionServidorHandle : IAsyncDisposable
        {
            private int _disposed;

            public async ValueTask DisposeAsync()
            {
                if (Interlocked.Exchange(ref _disposed, 1) == 0)
                {
                    await DetenerServidorAsync().ConfigureAwait(false);
                }
            }
        }
    }
}
