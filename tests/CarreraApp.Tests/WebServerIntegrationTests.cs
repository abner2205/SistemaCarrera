using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CarreraApp.Data;
using CarreraApp.Helpers;
using CarreraApp.Models;
using CarreraApp.Server;
using Microsoft.EntityFrameworkCore;

namespace CarreraApp.Tests
{
    [Collection("IsolatedStorage")]
    public class WebServerIntegrationTests
    {
        [Fact]
        public void WebServerOptions_PorDefecto_ConfiguraPuertos5000Y5001ConHttpsHabilitado()
        {
            var options = new WebServerOptions();

            Assert.Equal(5000, options.PuertoHttp);
            Assert.Equal(5001, options.PuertoHttps);
            Assert.True(options.HabilitarHttps);
        }

        [Fact]
        public async Task IniciarServidorAsync_SinAplicacionWpfYConPuertoOcupado_NotificaErrorCriticoServidorSinLanzarUI()
        {
            using var env = new TestEnvironmentFixture();
            Assert.Null(System.Windows.Application.Current);

            using var ocupante = new TcpListener(IPAddress.Loopback, 0);
            ocupante.Start();
            int puertoOcupado = ((IPEndPoint)ocupante.LocalEndpoint).Port;

            Exception? errorNotificado = null;
            void Handler(Exception ex) => errorNotificado = ex;

            WebServer.ErrorCriticoServidor += Handler;
            try
            {
                await WebServer.IniciarServidorAsync(new WebServerOptions
                {
                    DireccionEscucha = IPAddress.Loopback,
                    PuertoHttp = puertoOcupado,
                    HabilitarHttps = false
                });

                Assert.NotNull(errorNotificado);
                Assert.False(WebServer.EstaActivo);
            }
            finally
            {
                WebServer.ErrorCriticoServidor -= Handler;
                await WebServer.DetenerServidorAsync();
                WebServer.LimpiarColaPendientes();
                AppState.CambiarCarreraActiva(null);
            }
        }

        [Fact]
        public async Task TokenSesionActual_EsUrlSafeDe12A16CaracteresYEstableDuranteLaSesion()
        {
            using var env = new TestEnvironmentFixture();
            string tokenAntes = WebServer.TokenSesionActual;

            Assert.InRange(tokenAntes.Length, 12, 16);
            Assert.Matches("^[A-Za-z0-9_-]{12,16}$", tokenAntes);

            await using var server = await WebServer.IniciarEnSegundoPlanoAsync(new WebServerOptions
            {
                DireccionEscucha = IPAddress.Loopback,
                PuertoHttp = 0,
                HabilitarHttps = false,
                DbContextFactory = env.CrearFabricaContexto()
            });

            string tokenDurante1 = WebServer.TokenSesionActual;
            string tokenDurante2 = WebServer.TokenSesionActual;

            Assert.Equal(tokenAntes, tokenDurante1);
            Assert.Equal(tokenAntes, tokenDurante2);

            WebServer.LimpiarColaPendientes();
            AppState.CambiarCarreraActiva(null);
        }

        [Fact]
        public async Task IniciarEnSegundoPlanoAsync_HttpYHttpsConPuertosEfimeros_SirveAmbosProtocolosYValidaToken()
        {
            using var env = new TestEnvironmentFixture();

            await using var server = await WebServer.IniciarEnSegundoPlanoAsync(new WebServerOptions
            {
                DireccionEscucha = IPAddress.Loopback,
                PuertoHttp = 0,
                PuertoHttps = 0,
                HabilitarHttps = true,
                RegenerarTokenAlIniciar = true,
                DbContextFactory = env.CrearFabricaContexto()
            });

            try
            {
                Assert.True(WebServer.EstaActivo);
                Assert.NotNull(WebServer.PuertoHttpActivo);
                Assert.NotNull(WebServer.PuertoHttpsActivo);
                Assert.True(WebServer.PuertoHttpActivo > 0);
                Assert.True(WebServer.PuertoHttpsActivo > 0);
                Assert.NotEqual(WebServer.PuertoHttpActivo, WebServer.PuertoHttpsActivo);

                using var httpClient = new HttpClient { BaseAddress = new Uri(WebServer.UrlBaseHttpActivo!) };
                using var httpsHandler = new HttpClientHandler
                {
                    ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator
                };
                using var httpsClient = new HttpClient(httpsHandler) { BaseAddress = new Uri(WebServer.UrlBaseHttpsActivo!) };

                HttpResponseMessage unauthHttp = await httpClient.GetAsync("/api/pending-photos");
                HttpResponseMessage unauthHttps = await httpsClient.GetAsync("/api/pending-photos");
                Assert.Equal(HttpStatusCode.Unauthorized, unauthHttp.StatusCode);
                Assert.Equal(HttpStatusCode.Unauthorized, unauthHttps.StatusCode);

                using var reqHttp = new HttpRequestMessage(HttpMethod.Get, "/api/pending-photos");
                reqHttp.Headers.Add("X-Auth-Token", WebServer.TokenSesionActual);
                HttpResponseMessage okHttp = await httpClient.SendAsync(reqHttp);

                using var reqHttps = new HttpRequestMessage(HttpMethod.Get, "/api/pending-photos");
                reqHttps.Headers.Add("X-Auth-Token", WebServer.TokenSesionActual);
                HttpResponseMessage okHttps = await httpsClient.SendAsync(reqHttps);

                Assert.Equal(HttpStatusCode.OK, okHttp.StatusCode);
                Assert.Equal(HttpStatusCode.OK, okHttps.StatusCode);
            }
            finally
            {
                WebServer.LimpiarColaPendientes();
                AppState.CambiarCarreraActiva(null);
            }
        }

        [Fact]
        public async Task RecursosEstaticosWwwroot_CarganSinTokenYApiExigeTokenValido()
        {
            using var env = new TestEnvironmentFixture();

            await using var server = await WebServer.IniciarEnSegundoPlanoAsync(new WebServerOptions
            {
                DireccionEscucha = IPAddress.Loopback,
                PuertoHttp = 0,
                HabilitarHttps = false,
                RegenerarTokenAlIniciar = true,
                DbContextFactory = env.CrearFabricaContexto()
            });

            try
            {
                using var client = new HttpClient { BaseAddress = new Uri(WebServer.UrlBaseHttpActivo!) };

                HttpResponseMessage rootResp = await client.GetAsync("/");
                HttpResponseMessage htmlResp = await client.GetAsync("/index.html");
                HttpResponseMessage jsResp = await client.GetAsync("/app.js");

                Assert.Equal(HttpStatusCode.OK, rootResp.StatusCode);
                Assert.Equal(HttpStatusCode.OK, htmlResp.StatusCode);
                Assert.Equal(HttpStatusCode.OK, jsResp.StatusCode);

                string html = await htmlResp.Content.ReadAsStringAsync();
                string js = await jsResp.Content.ReadAsStringAsync();
                Assert.Contains("Llegada de Corredores", html);
                Assert.Contains("/api/upload-photo", js);

                // Acceso autorizado vía query ?token= y vía cabecera X-Auth-Token
                HttpResponseMessage viaQuery = await client.GetAsync($"/api/pending-photos?token={Uri.EscapeDataString(WebServer.TokenSesionActual)}");
                Assert.Equal(HttpStatusCode.OK, viaQuery.StatusCode);

                using var viaHeaderReq = new HttpRequestMessage(HttpMethod.Get, "/api/pending-photos");
                viaHeaderReq.Headers.Add("X-Auth-Token", WebServer.TokenSesionActual);
                HttpResponseMessage viaHeader = await client.SendAsync(viaHeaderReq);
                Assert.Equal(HttpStatusCode.OK, viaHeader.StatusCode);
            }
            finally
            {
                WebServer.LimpiarColaPendientes();
                AppState.CambiarCarreraActiva(null);
            }
        }

        [Fact]
        public async Task EndpointsApi_SinTokenOTokenInvalidoOTokenDeSesionAnterior_Retornan401SinAlterarColaNiDisco()
        {
            using var env = new TestEnvironmentFixture();
            await DatabaseMaintenanceHelper.InicializarYMigrarBaseDatosAsync(env.CrearFabricaContexto());

            string tokenSesion1;
            await using (var server1 = await WebServer.IniciarEnSegundoPlanoAsync(new WebServerOptions
            {
                DireccionEscucha = IPAddress.Loopback,
                PuertoHttp = 0,
                HabilitarHttps = false,
                RegenerarTokenAlIniciar = true,
                DbContextFactory = env.CrearFabricaContexto()
            }))
            {
                tokenSesion1 = WebServer.TokenSesionActual;
                using var client1 = new HttpClient { BaseAddress = new Uri(WebServer.UrlBaseHttpActivo!) };

                // Sin token en todos los endpoints /api/*
                Assert.Equal(HttpStatusCode.Unauthorized, (await client1.GetAsync("/api/pending-photos")).StatusCode);
                Assert.Equal(HttpStatusCode.Unauthorized, (await client1.PostAsJsonAsync("/api/assign-dorsal", new { idFoto = Guid.NewGuid().ToString(), dorsal = 1 })).StatusCode);
                Assert.Equal(HttpStatusCode.Unauthorized, (await client1.PostAsJsonAsync("/api/discard-photo", new { idFoto = Guid.NewGuid().ToString() })).StatusCode);
                Assert.Equal(HttpStatusCode.Unauthorized, (await client1.GetAsync("/api/ruta-inexistente")).StatusCode);

                using var multipartSinToken = CrearMultipartFoto(CrearJpegValido(24, 24), "foto.jpg", "t-1");
                HttpResponseMessage uploadSinToken = await client1.PostAsync("/api/upload-photo", multipartSinToken);
                Assert.Equal(HttpStatusCode.Unauthorized, uploadSinToken.StatusCode);

                // Token manipulado (misma longitud y diferente longitud) en header y query
                string tokenManipulado = new string('X', tokenSesion1.Length);
                using var reqHeaderInvalido = new HttpRequestMessage(HttpMethod.Get, "/api/pending-photos");
                reqHeaderInvalido.Headers.Add("X-Auth-Token", tokenManipulado);
                Assert.Equal(HttpStatusCode.Unauthorized, (await client1.SendAsync(reqHeaderInvalido)).StatusCode);

                Assert.Equal(HttpStatusCode.Unauthorized, (await client1.GetAsync("/api/pending-photos?token=invalido")).StatusCode);
                if (Directory.Exists(env.FotosTempDir))
                {
                    Assert.Empty(Directory.GetFiles(env.FotosTempDir));
                }
                Assert.Empty(WebServer.ObtenerIdsFotosPendientes());
            }

            // Nueva sesión regenera token; el token anterior debe ser rechazado con 401
            await using var server2 = await WebServer.IniciarEnSegundoPlanoAsync(new WebServerOptions
            {
                DireccionEscucha = IPAddress.Loopback,
                PuertoHttp = 0,
                HabilitarHttps = false,
                RegenerarTokenAlIniciar = true,
                DbContextFactory = env.CrearFabricaContexto()
            });

            try
            {
                Assert.NotEqual(tokenSesion1, WebServer.TokenSesionActual);
                using var client2 = new HttpClient { BaseAddress = new Uri(WebServer.UrlBaseHttpActivo!) };
                using var reqTokenViejo = new HttpRequestMessage(HttpMethod.Get, "/api/pending-photos");
                reqTokenViejo.Headers.Add("X-Auth-Token", tokenSesion1);

                HttpResponseMessage respTokenViejo = await client2.SendAsync(reqTokenViejo);
                Assert.Equal(HttpStatusCode.Unauthorized, respTokenViejo.StatusCode);
            }
            finally
            {
                WebServer.LimpiarColaPendientes();
                AppState.CambiarCarreraActiva(null);
            }
        }

        [Fact]
        public async Task UploadPhoto_FormatosValidosJpegPngWebpYExacto10MB_GuardaConExtensionRealYRetorna200()
        {
            using var env = new TestEnvironmentFixture();

            await using var server = await WebServer.IniciarEnSegundoPlanoAsync(new WebServerOptions
            {
                DireccionEscucha = IPAddress.Loopback,
                PuertoHttp = 0,
                HabilitarHttps = false,
                RegenerarTokenAlIniciar = true,
                DbContextFactory = env.CrearFabricaContexto()
            });

            try
            {
                using var client = CrearClienteAutenticado();

                byte[] jpeg = CrearJpegValido(64, 48);
                byte[] png = CrearPngValido(48, 64);
                byte[] webp = CrearWebpLosslessValido(40, 40);
                byte[] png10Mb = CrearPngExactoDeTamano(ImageHelper.TamanoMaximoBytes);

                // Se envían nombres engañosos para confirmar que se usa la extensión real detectada por firma binaria
                string idJpeg = await SubirFotoYValidarExitoAsync(client, jpeg, "disfrazado.png", "temp-1");
                string idPng = await SubirFotoYValidarExitoAsync(client, png, "disfrazado.jpg", "temp-2");
                string idWebp = await SubirFotoYValidarExitoAsync(client, webp, "captura.webp", "temp-3");
                string id10Mb = await SubirFotoYValidarExitoAsync(client, png10Mb, "exacto10mb.png", "temp-4");

                Assert.True(File.Exists(Path.Combine(env.FotosTempDir, $"{idJpeg}.jpg")));
                Assert.True(File.Exists(Path.Combine(env.FotosTempDir, $"{idPng}.png")));
                Assert.True(File.Exists(Path.Combine(env.FotosTempDir, $"{idWebp}.webp")));
                Assert.True(File.Exists(Path.Combine(env.FotosTempDir, $"{id10Mb}.png")));

                string[]? pendientes = await client.GetFromJsonAsync<string[]>("/api/pending-photos");
                Assert.NotNull(pendientes);
                Assert.Contains(idJpeg, pendientes);
                Assert.Contains(idPng, pendientes);
                Assert.Contains(idWebp, pendientes);
                Assert.Contains(id10Mb, pendientes);
            }
            finally
            {
                WebServer.LimpiarColaPendientes();
                AppState.CambiarCarreraActiva(null);
            }
        }

        [Fact]
        public async Task UploadPhoto_EntradasInvalidas_Retornan400_413_415JsonSinEscribirEnDisco()
        {
            using var env = new TestEnvironmentFixture();

            await using var server = await WebServer.IniciarEnSegundoPlanoAsync(new WebServerOptions
            {
                DireccionEscucha = IPAddress.Loopback,
                PuertoHttp = 0,
                HabilitarHttps = false,
                RegenerarTokenAlIniciar = true,
                DbContextFactory = env.CrearFabricaContexto()
            });

            try
            {
                using var client = CrearClienteAutenticado();

                // 1. No multipart -> 400 JSON
                HttpResponseMessage noMultipart = await client.PostAsJsonAsync("/api/upload-photo", new { tempId = "123" });
                await AssertErrorJsonAsync(noMultipart, HttpStatusCode.BadRequest);

                // 2. Sin tempId -> 400 JSON
                using var sinTempId = CrearMultipartFoto(CrearJpegValido(20, 20), "foto.jpg", "");
                await AssertErrorJsonAsync(await client.PostAsync("/api/upload-photo", sinTempId), HttpStatusCode.BadRequest);

                // 3. tempId con traversal -> 400 JSON
                using var tempIdMalicioso = CrearMultipartFoto(CrearJpegValido(20, 20), "foto.jpg", "../bad");
                await AssertErrorJsonAsync(await client.PostAsync("/api/upload-photo", tempIdMalicioso), HttpStatusCode.BadRequest);

                // 4. Archivo vacío (0 bytes) -> 400 JSON
                using var vacio = CrearMultipartFoto(Array.Empty<byte>(), "vacio.jpg", "t-vacio");
                await AssertErrorJsonAsync(await client.PostAsync("/api/upload-photo", vacio), HttpStatusCode.BadRequest);

                // 5. Sin archivo 'foto' -> 400 JSON
                using var sinArchivo = new MultipartFormDataContent();
                sinArchivo.Add(new StringContent("t-sin-foto"), "tempId");
                await AssertErrorJsonAsync(await client.PostAsync("/api/upload-photo", sinArchivo), HttpStatusCode.BadRequest);

                // 6. Archivo > 10 MB (10 MB + 1 byte y 11 MB) -> 413 JSON
                byte[] excesoUnByte = new byte[ImageHelper.TamanoMaximoBytes + 1];
                byte[] jpegBase = CrearJpegValido(20, 20);
                Array.Copy(jpegBase, excesoUnByte, jpegBase.Length);
                using var payload10MbPlus1 = CrearMultipartFoto(excesoUnByte, "grande.jpg", "t-10plus1");
                await AssertErrorJsonAsync(await client.PostAsync("/api/upload-photo", payload10MbPlus1), HttpStatusCode.RequestEntityTooLarge);

                byte[] payload11MbBytes = new byte[11 * 1024 * 1024];
                Array.Copy(jpegBase, payload11MbBytes, jpegBase.Length);
                using var payload11Mb = CrearMultipartFoto(payload11MbBytes, "muygrande.jpg", "t-11mb");
                await AssertErrorJsonAsync(await client.PostAsync("/api/upload-photo", payload11Mb), HttpStatusCode.RequestEntityTooLarge);

                // 7. Texto plano o ejecutable renombrado a .jpg -> 415 JSON
                byte[] texto = Encoding.UTF8.GetBytes("MZ esto no es una imagen JPEG real");
                using var falsoJpg = CrearMultipartFoto(texto, "falso.jpg", "t-falso");
                await AssertErrorJsonAsync(await client.PostAsync("/api/upload-photo", falsoJpg), HttpStatusCode.UnsupportedMediaType);

                // 8. JPEG truncado / corrupto -> 415 JSON
                byte[] jpegTruncado = jpegBase.AsSpan(0, jpegBase.Length / 2).ToArray();
                using var corrupto = CrearMultipartFoto(jpegTruncado, "corrupto.jpg", "t-corrupto");
                await AssertErrorJsonAsync(await client.PostAsync("/api/upload-photo", corrupto), HttpStatusCode.UnsupportedMediaType);

                if (Directory.Exists(env.FotosTempDir))
                {
                    Assert.Empty(Directory.GetFiles(env.FotosTempDir));
                }
                Assert.Empty(WebServer.ObtenerIdsFotosPendientes());
            }
            finally
            {
                WebServer.LimpiarColaPendientes();
                AppState.CambiarCarreraActiva(null);
            }
        }

        [Fact]
        public async Task AssignDorsalYDiscardPhoto_ConIdFotoPathTraversalOInvalido_Retornan400BadRequest()
        {
            using var env = new TestEnvironmentFixture();

            await using var server = await WebServer.IniciarEnSegundoPlanoAsync(new WebServerOptions
            {
                DireccionEscucha = IPAddress.Loopback,
                PuertoHttp = 0,
                HabilitarHttps = false,
                RegenerarTokenAlIniciar = true,
                DbContextFactory = env.CrearFabricaContexto()
            });

            try
            {
                using var client = CrearClienteAutenticado();

                string[] idsMaliciosos =
                {
                    "",
                    "   ",
                    "..",
                    ".",
                    "../carrera.db",
                    "..\\carrera.db",
                    "sub/foto",
                    "sub\\foto",
                    "foto..123",
                    "foto:1",
                    "foto con espacios",
                    "foto%2e%2e"
                };

                foreach (string idMalicioso in idsMaliciosos)
                {
                    HttpResponseMessage respAssign = await client.PostAsJsonAsync(
                        "/api/assign-dorsal",
                        new { idFoto = idMalicioso, dorsal = 10 });
                    await AssertErrorJsonAsync(respAssign, HttpStatusCode.BadRequest);

                    HttpResponseMessage respDiscard = await client.PostAsJsonAsync(
                        "/api/discard-photo",
                        new { idFoto = idMalicioso });
                    await AssertErrorJsonAsync(respDiscard, HttpStatusCode.BadRequest);
                }
            }
            finally
            {
                WebServer.LimpiarColaPendientes();
                AppState.CambiarCarreraActiva(null);
            }
        }

        [Fact]
        public async Task AssignDorsal_SinCarreraActiva_Retorna422SinSeleccionarCarreraDeBdYConservaFotoPendiente()
        {
            using var env = new TestEnvironmentFixture();
            await DatabaseMaintenanceHelper.InicializarYMigrarBaseDatosAsync(env.CrearFabricaContexto());

            // Sembrar una carrera en BD para confirmar que NO se autoselecciona cuando CarreraActiva == null
            await CrearEscenarioCarreraAsync(env, DateTime.Now.AddMinutes(-5), dorsal: 5);
            AppState.CambiarCarreraActiva(null);

            await using var server = await WebServer.IniciarEnSegundoPlanoAsync(new WebServerOptions
            {
                DireccionEscucha = IPAddress.Loopback,
                PuertoHttp = 0,
                HabilitarHttps = false,
                RegenerarTokenAlIniciar = true,
                DbContextFactory = env.CrearFabricaContexto()
            });

            try
            {
                using var client = CrearClienteAutenticado();
                string idFoto = await SubirFotoYValidarExitoAsync(client, CrearJpegValido(40, 40), "meta.jpg", "sin-carrera");

                HttpResponseMessage response = await client.PostAsJsonAsync("/api/assign-dorsal", new { idFoto, dorsal = 5 });
                string mensaje = await AssertErrorJsonAsync(response, HttpStatusCode.UnprocessableEntity);

                Assert.Contains("carrera activa", mensaje, StringComparison.OrdinalIgnoreCase);
                Assert.Null(AppState.CarreraActiva);
                Assert.Contains(idFoto, WebServer.ObtenerIdsFotosPendientes());
                Assert.True(File.Exists(Path.Combine(env.FotosTempDir, $"{idFoto}.jpg")));
            }
            finally
            {
                WebServer.LimpiarColaPendientes();
                AppState.CambiarCarreraActiva(null);
            }
        }

        [Fact]
        public async Task CambiarCarreraActiva_LimpiaCategoriaPizarraColaLocalColaServidorYArchivosTemporales()
        {
            using var env = new TestEnvironmentFixture();
            await DatabaseMaintenanceHelper.InicializarYMigrarBaseDatosAsync(env.CrearFabricaContexto());

            var escenario1 = await CrearEscenarioCarreraAsync(env, DateTime.Now.AddMinutes(-10), dorsal: 1, nombreCarrera: "Carrera Uno");
            var escenario2 = await CrearEscenarioCarreraAsync(env, DateTime.Now.AddMinutes(-10), dorsal: 2, nombreCarrera: "Carrera Dos");

            // Archivos que NO deben verse afectados en Fotos_Temp (.gitkeep) ni Fotos_Definitivas
            string gitkeep = Path.Combine(env.FotosTempDir, ".gitkeep");
            Directory.CreateDirectory(env.FotosTempDir);
            await File.WriteAllTextAsync(gitkeep, string.Empty);

            Directory.CreateDirectory(env.FotosDefinitivasDir);
            string definitiva = Path.Combine(env.FotosDefinitivasDir, "definitiva_intacta.jpg");
            await File.WriteAllBytesAsync(definitiva, new byte[] { 0xFF, 0xD8, 0xFF, 0xD9 });

            AppState.CambiarCarreraActiva(escenario1.Carrera);
            AppState.CategoriaPizarraActiva = escenario1.Categoria;

            await using var server = await WebServer.IniciarEnSegundoPlanoAsync(new WebServerOptions
            {
                DireccionEscucha = IPAddress.Loopback,
                PuertoHttp = 0,
                HabilitarHttps = false,
                RegenerarTokenAlIniciar = true,
                DbContextFactory = env.CrearFabricaContexto()
            });

            bool eventoDisparado = false;
            void OnCambioCarrera() => eventoDisparado = true;
            AppState.CarreraActivaCambiada += OnCambioCarrera;

            try
            {
                using var client = CrearClienteAutenticado();
                string id1 = await SubirFotoYValidarExitoAsync(client, CrearJpegValido(32, 32), "c1.jpg", "t-c1");
                string id2 = await SubirFotoYValidarExitoAsync(client, CrearPngValido(32, 32), "c2.png", "t-c2");

                AppState.FotosEnEsperaGlobal.Add(new FotoPendiente
                {
                    IdFoto = id1,
                    Hora_Llegada = DateTime.Now,
                    Ruta_Foto = Path.Combine(env.FotosTempDir, $"{id1}.jpg")
                });

                AppState.CambiarCarreraActiva(escenario2.Carrera);

                Assert.True(eventoDisparado);
                Assert.Same(escenario2.Carrera, AppState.CarreraActiva);
                Assert.Null(AppState.CategoriaPizarraActiva);
                Assert.Empty(AppState.FotosEnEsperaGlobal);
                Assert.Empty(WebServer.ObtenerIdsFotosPendientes());

                string[]? pendientes = await client.GetFromJsonAsync<string[]>("/api/pending-photos");
                Assert.NotNull(pendientes);
                Assert.Empty(pendientes!);

                // No se debe poder asignar una foto de la carrera anterior sobre la nueva carrera
                HttpResponseMessage asignarVieja = await client.PostAsJsonAsync(
                    "/api/assign-dorsal",
                    new { idFoto = id1, dorsal = 2 });
                Assert.Equal(HttpStatusCode.NotFound, asignarVieja.StatusCode);

                Assert.False(File.Exists(Path.Combine(env.FotosTempDir, $"{id1}.jpg")));
                Assert.False(File.Exists(Path.Combine(env.FotosTempDir, $"{id2}.png")));
                Assert.True(File.Exists(gitkeep));
                Assert.True(File.Exists(definitiva));
            }
            finally
            {
                AppState.CarreraActivaCambiada -= OnCambioCarrera;
                WebServer.LimpiarColaPendientes();
                AppState.CambiarCarreraActiva(null);
            }
        }

        [Fact]
        public void AppState_RegistrosExitososGlobal_AcotaMaximo200Entradas()
        {
            AppState.RegistrosExitososGlobal.Clear();
            try
            {
                for (int i = 1; i <= 250; i++)
                {
                    AppState.AgregarRegistroExitoso($"Registro #{i}");
                }

                Assert.Equal(AppState.MaxRegistrosExitosos, AppState.RegistrosExitososGlobal.Count);
                Assert.Equal("Registro #250", AppState.RegistrosExitososGlobal[0]);
                Assert.Equal("Registro #51", AppState.RegistrosExitososGlobal[^1]);
                Assert.DoesNotContain("Registro #1", AppState.RegistrosExitososGlobal);

                // También acota si un llamador inserta directamente en la colección observable
                AppState.RegistrosExitososGlobal.Insert(0, "Directo #251");
                Assert.Equal(AppState.MaxRegistrosExitosos, AppState.RegistrosExitososGlobal.Count);
                Assert.Equal("Directo #251", AppState.RegistrosExitososGlobal[0]);
            }
            finally
            {
                AppState.RegistrosExitososGlobal.Clear();
            }
        }

        [Fact]
        public async Task AssignDorsal_EsperaPersistenciaRealAntesDe200Ok_YPreservaHoraOriginalDeLlegada()
        {
            using var env = new TestEnvironmentFixture();
            await DatabaseMaintenanceHelper.InicializarYMigrarBaseDatosAsync(env.CrearFabricaContexto());

            var escenario = await CrearEscenarioCarreraAsync(env, DateTime.Now.AddSeconds(-10), dorsal: 7);
            AppState.CambiarCarreraActiva(escenario.Carrera);

            await using var server = await WebServer.IniciarEnSegundoPlanoAsync(new WebServerOptions
            {
                DireccionEscucha = IPAddress.Loopback,
                PuertoHttp = 0,
                HabilitarHttps = false,
                RegenerarTokenAlIniciar = true,
                DbContextFactory = env.CrearFabricaContexto()
            });

            string? idAsignadoEvento = null;
            int? dorsalEvento = null;
            WebServer.FotoAsignadaRemota += OnAsignada;
            void OnAsignada(string id, int dorsal, AsignacionResultado res)
            {
                idAsignadoEvento = id;
                dorsalEvento = dorsal;
            }

            try
            {
                using var client = CrearClienteAutenticado();

                DateTime antesSubida = DateTime.Now;
                string idFoto = await SubirFotoYValidarExitoAsync(client, CrearJpegValido(48, 48), "llegada7.jpg", "t-7");
                DateTime despuesSubida = DateTime.Now;

                // Simular demora del juez antes de teclear el dorsal en el teléfono
                await Task.Delay(250);

                HttpResponseMessage response = await client.PostAsJsonAsync(
                    "/api/assign-dorsal",
                    new { idFoto, dorsal = 7 });

                Assert.Equal(HttpStatusCode.OK, response.StatusCode);

                // Al instante en que el móvil recibe 200 OK, el tiempo ya debe existir en SQLite
                using var db = env.CrearContexto();
                Tiempo? tiempoDb = await db.Tiempos.SingleOrDefaultAsync(t => t.ID_Corredor == escenario.Corredor.ID_Corredor);
                Assert.NotNull(tiempoDb);
                Assert.InRange(tiempoDb!.Hora_Llegada, antesSubida, despuesSubida);
                Assert.True(File.Exists(tiempoDb.Ruta_Foto));
                Assert.False(File.Exists(Path.Combine(env.FotosTempDir, $"{idFoto}.jpg")));
                Assert.DoesNotContain(idFoto, WebServer.ObtenerIdsFotosPendientes());
                Assert.Equal(idFoto, idAsignadoEvento);
                Assert.Equal(7, dorsalEvento);
            }
            finally
            {
                WebServer.FotoAsignadaRemota -= OnAsignada;
                WebServer.LimpiarColaPendientes();
                AppState.CambiarCarreraActiva(null);
            }
        }

        [Fact]
        public async Task AssignDorsal_TiempoNegativoODorsalInexistente_Retorna422O404YConservaFotoPendienteParaReintento()
        {
            using var env = new TestEnvironmentFixture();
            await DatabaseMaintenanceHelper.InicializarYMigrarBaseDatosAsync(env.CrearFabricaContexto());

            Carrera carrera;
            Corredor corredorHitFuturo;
            Corredor corredorHitValido;
            using (var db = env.CrearContexto())
            {
                carrera = new Carrera { Nombre = "Carrera Reintentos", Fecha = DateTime.Today };
                db.Carreras.Add(carrera);
                await db.SaveChangesAsync();

                var categoria = new Categoria { Nombre = "General", ID_Carrera = carrera.ID_Carrera };
                db.Categorias.Add(categoria);
                await db.SaveChangesAsync();

                var hitFuturo = new Hit
                {
                    Nombre_Hit = "Hit Futuro",
                    ID_Carrera = carrera.ID_Carrera,
                    Hora_Inicio = DateTime.Now.AddMinutes(10)
                };
                var hitValido = new Hit
                {
                    Nombre_Hit = "Hit Valido",
                    ID_Carrera = carrera.ID_Carrera,
                    Hora_Inicio = DateTime.Now.AddSeconds(-30)
                };
                db.Hits.AddRange(hitFuturo, hitValido);
                await db.SaveChangesAsync();

                corredorHitFuturo = new Corredor
                {
                    Dorsal = 10,
                    Nombre = "Corredor Tiempo Negativo",
                    ID_Carrera = carrera.ID_Carrera,
                    ID_Categoria = categoria.ID_Categoria,
                    ID_Hit = hitFuturo.ID_Hit
                };
                corredorHitValido = new Corredor
                {
                    Dorsal = 11,
                    Nombre = "Corredor Valido",
                    ID_Carrera = carrera.ID_Carrera,
                    ID_Categoria = categoria.ID_Categoria,
                    ID_Hit = hitValido.ID_Hit
                };
                db.Corredores.AddRange(corredorHitFuturo, corredorHitValido);
                await db.SaveChangesAsync();
            }

            AppState.CambiarCarreraActiva(carrera);

            await using var server = await WebServer.IniciarEnSegundoPlanoAsync(new WebServerOptions
            {
                DireccionEscucha = IPAddress.Loopback,
                PuertoHttp = 0,
                HabilitarHttps = false,
                RegenerarTokenAlIniciar = true,
                DbContextFactory = env.CrearFabricaContexto()
            });

            try
            {
                using var client = CrearClienteAutenticado();
                string idFoto = await SubirFotoYValidarExitoAsync(client, CrearJpegValido(40, 40), "reintento.jpg", "t-reintento");

                // 1. Dorsal inexistente -> 404 y foto permanece pendiente
                HttpResponseMessage resp404 = await client.PostAsJsonAsync("/api/assign-dorsal", new { idFoto, dorsal = 999 });
                await AssertErrorJsonAsync(resp404, HttpStatusCode.NotFound);
                Assert.Contains(idFoto, WebServer.ObtenerIdsFotosPendientes());
                Assert.True(File.Exists(Path.Combine(env.FotosTempDir, $"{idFoto}.jpg")));

                // 2. Hora llegada anterior a Hora_Inicio (tiempo neto negativo) -> 422 y foto permanece pendiente
                HttpResponseMessage resp422 = await client.PostAsJsonAsync("/api/assign-dorsal", new { idFoto, dorsal = 10 });
                string msgNegativo = await AssertErrorJsonAsync(resp422, HttpStatusCode.UnprocessableEntity);
                Assert.Contains("negativo", msgNegativo, StringComparison.OrdinalIgnoreCase);
                Assert.Contains(idFoto, WebServer.ObtenerIdsFotosPendientes());
                Assert.True(File.Exists(Path.Combine(env.FotosTempDir, $"{idFoto}.jpg")));

                // 3. Reintento con dorsal válido -> 200 OK
                HttpResponseMessage resp200 = await client.PostAsJsonAsync("/api/assign-dorsal", new { idFoto, dorsal = 11 });
                Assert.Equal(HttpStatusCode.OK, resp200.StatusCode);
                Assert.DoesNotContain(idFoto, WebServer.ObtenerIdsFotosPendientes());
            }
            finally
            {
                WebServer.LimpiarColaPendientes();
                AppState.CambiarCarreraActiva(null);
            }
        }

        [Fact]
        public async Task AssignDorsalYDiscard_ConcurrentesEntreMoviles_SoloUnoProcesaYLosDemasReciben409Conflict()
        {
            using var env = new TestEnvironmentFixture();
            await DatabaseMaintenanceHelper.InicializarYMigrarBaseDatosAsync(env.CrearFabricaContexto());

            Carrera carrera;
            using (var db = env.CrearContexto())
            {
                carrera = new Carrera { Nombre = "Carrera Concurrencia", Fecha = DateTime.Today };
                db.Carreras.Add(carrera);
                await db.SaveChangesAsync();

                var cat = new Categoria { Nombre = "General", ID_Carrera = carrera.ID_Carrera };
                var hit = new Hit { Nombre_Hit = "Hit 1", ID_Carrera = carrera.ID_Carrera, Hora_Inicio = DateTime.Now.AddSeconds(-30) };
                db.Categorias.Add(cat);
                db.Hits.Add(hit);
                await db.SaveChangesAsync();

                db.Corredores.AddRange(
                    new Corredor { Dorsal = 1, Nombre = "Uno", ID_Carrera = carrera.ID_Carrera, ID_Categoria = cat.ID_Categoria, ID_Hit = hit.ID_Hit },
                    new Corredor { Dorsal = 2, Nombre = "Dos", ID_Carrera = carrera.ID_Carrera, ID_Categoria = cat.ID_Categoria, ID_Hit = hit.ID_Hit });
                await db.SaveChangesAsync();
            }

            AppState.CambiarCarreraActiva(carrera);

            var dataService = new RaceDataService(env.CrearFabricaContexto());
            var primeraEnCurso = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var liberarPrimera = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            int invocacionesServicio = 0;

            await using var server = await WebServer.IniciarEnSegundoPlanoAsync(new WebServerOptions
            {
                DireccionEscucha = IPAddress.Loopback,
                PuertoHttp = 0,
                HabilitarHttps = false,
                RegenerarTokenAlIniciar = true,
                AsignarTiempoFunc = async (idC, nomC, dorsal, hora, ruta, ct) =>
                {
                    if (Interlocked.Increment(ref invocacionesServicio) == 1)
                    {
                        primeraEnCurso.TrySetResult();
                        await liberarPrimera.Task.WaitAsync(ct);
                    }

                    return await dataService.AsignarTiempoAsync(idC, nomC, dorsal, hora, ruta, ct);
                }
            });

            try
            {
                using var client1 = CrearClienteAutenticado();
                using var client2 = CrearClienteAutenticado();

                string idFoto = await SubirFotoYValidarExitoAsync(client1, CrearJpegValido(40, 40), "concurrente.jpg", "t-conc");

                Task<HttpResponseMessage> req1 = client1.PostAsJsonAsync("/api/assign-dorsal", new { idFoto, dorsal = 1 });
                await primeraEnCurso.Task.WaitAsync(TimeSpan.FromSeconds(5));

                // Mientras la petición 1 está en proceso, otra asignación y un descarte simultáneo deben recibir 409 Conflict
                Task<HttpResponseMessage> req2 = client2.PostAsJsonAsync("/api/assign-dorsal", new { idFoto, dorsal = 2 });
                Task<HttpResponseMessage> req3 = client2.PostAsJsonAsync("/api/discard-photo", new { idFoto });

                HttpResponseMessage resp2 = await req2;
                HttpResponseMessage resp3 = await req3;
                Assert.Equal(HttpStatusCode.Conflict, resp2.StatusCode);
                Assert.Equal(HttpStatusCode.Conflict, resp3.StatusCode);

                liberarPrimera.TrySetResult();
                HttpResponseMessage resp1 = await req1;
                Assert.Equal(HttpStatusCode.OK, resp1.StatusCode);
                Assert.Equal(1, invocacionesServicio);

                using var dbCheck = env.CrearContexto();
                Assert.Equal(1, await dbCheck.Tiempos.CountAsync());
            }
            finally
            {
                liberarPrimera.TrySetResult();
                WebServer.LimpiarColaPendientes();
                AppState.CambiarCarreraActiva(null);
            }
        }

        [Fact]
        public async Task ExclusividadMutua_MovilVsPcLocal_BloqueaAccesoSimultaneoYPermiteLiberacion()
        {
            using var env = new TestEnvironmentFixture();
            await DatabaseMaintenanceHelper.InicializarYMigrarBaseDatosAsync(env.CrearFabricaContexto());

            var escenario = await CrearEscenarioCarreraAsync(env, DateTime.Now.AddSeconds(-30), dorsal: 21);
            AppState.CambiarCarreraActiva(escenario.Carrera);

            var dataService = new RaceDataService(env.CrearFabricaContexto());
            var movilEnCurso = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var liberarMovil = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

            await using var server = await WebServer.IniciarEnSegundoPlanoAsync(new WebServerOptions
            {
                DireccionEscucha = IPAddress.Loopback,
                PuertoHttp = 0,
                HabilitarHttps = false,
                RegenerarTokenAlIniciar = true,
                AsignarTiempoFunc = async (idC, nomC, dorsal, hora, ruta, ct) =>
                {
                    movilEnCurso.TrySetResult();
                    await liberarMovil.Task.WaitAsync(ct);
                    return await dataService.AsignarTiempoAsync(idC, nomC, dorsal, hora, ruta, ct);
                }
            });

            try
            {
                using var client = CrearClienteAutenticado();

                // Caso 1: PC toma la foto 1 primero; móvil recibe 409; al liberarla la PC, móvil puede procesarla
                string id1 = await SubirFotoYValidarExitoAsync(client, CrearJpegValido(32, 32), "pc_primero.jpg", "t-pc1");
                Assert.True(WebServer.IntentarAdquirirFotoParaLocal(id1, out DateTime hora1, out string ruta1));
                Assert.NotEqual(default, hora1);
                Assert.True(File.Exists(ruta1));
                Assert.False(WebServer.IntentarAdquirirFotoParaLocal(id1));

                HttpResponseMessage movilAssignBloqueado = await client.PostAsJsonAsync("/api/assign-dorsal", new { idFoto = id1, dorsal = 21 });
                HttpResponseMessage movilDiscardBloqueado = await client.PostAsJsonAsync("/api/discard-photo", new { idFoto = id1 });
                Assert.Equal(HttpStatusCode.Conflict, movilAssignBloqueado.StatusCode);
                Assert.Equal(HttpStatusCode.Conflict, movilDiscardBloqueado.StatusCode);

                WebServer.LiberarFotoLocal(id1);

                // Caso 2: Móvil inicia procesamiento de foto 1 -> PC no puede adquirirla simultáneamente
                Task<HttpResponseMessage> reqMovil = client.PostAsJsonAsync("/api/assign-dorsal", new { idFoto = id1, dorsal = 21 });
                await movilEnCurso.Task.WaitAsync(TimeSpan.FromSeconds(5));

                Assert.False(WebServer.IntentarAdquirirFotoParaLocal(id1));

                liberarMovil.TrySetResult();
                HttpResponseMessage respMovil = await reqMovil;
                Assert.Equal(HttpStatusCode.OK, respMovil.StatusCode);
                Assert.False(WebServer.IntentarAdquirirFotoParaLocal(id1));
            }
            finally
            {
                liberarMovil.TrySetResult();
                WebServer.LimpiarColaPendientes();
                AppState.CambiarCarreraActiva(null);
            }
        }

        [Fact]
        public async Task DiscardPhoto_FotoExistente_EliminaDeColaYDiscoYRetorna404EnSegundoIntento()
        {
            using var env = new TestEnvironmentFixture();

            await using var server = await WebServer.IniciarEnSegundoPlanoAsync(new WebServerOptions
            {
                DireccionEscucha = IPAddress.Loopback,
                PuertoHttp = 0,
                HabilitarHttps = false,
                RegenerarTokenAlIniciar = true,
                DbContextFactory = env.CrearFabricaContexto()
            });

            string? idDescartadoEvento = null;
            void OnDescartada(string id) => idDescartadoEvento = id;
            WebServer.FotoDescartadaRemota += OnDescartada;

            try
            {
                using var client = CrearClienteAutenticado();
                string idFoto = await SubirFotoYValidarExitoAsync(client, CrearPngValido(32, 32), "descartar.png", "t-desc");
                string rutaArchivo = Path.Combine(env.FotosTempDir, $"{idFoto}.png");
                Assert.True(File.Exists(rutaArchivo));

                HttpResponseMessage resp1 = await client.PostAsJsonAsync("/api/discard-photo", new { idFoto });
                Assert.Equal(HttpStatusCode.OK, resp1.StatusCode);
                Assert.Equal(idFoto, idDescartadoEvento);
                Assert.False(File.Exists(rutaArchivo));
                Assert.DoesNotContain(idFoto, WebServer.ObtenerIdsFotosPendientes());

                HttpResponseMessage resp2 = await client.PostAsJsonAsync("/api/discard-photo", new { idFoto });
                await AssertErrorJsonAsync(resp2, HttpStatusCode.NotFound);
            }
            finally
            {
                WebServer.FotoDescartadaRemota -= OnDescartada;
                WebServer.LimpiarColaPendientes();
                AppState.CambiarCarreraActiva(null);
            }
        }

        [Fact]
        public async Task AssignDorsal_FalloPersistenciaOExcepcion_ConservaFotoPendienteParaReintentoYDisparaNuevaFotoRecibida()
        {
            using var env = new TestEnvironmentFixture();
            await DatabaseMaintenanceHelper.InicializarYMigrarBaseDatosAsync(env.CrearFabricaContexto());

            var escenario = await CrearEscenarioCarreraAsync(env, DateTime.Now.AddSeconds(-20), dorsal: 33);
            AppState.CambiarCarreraActiva(escenario.Carrera);

            var dataService = new RaceDataService(env.CrearFabricaContexto());
            int intento = 0;

            await using var server = await WebServer.IniciarEnSegundoPlanoAsync(new WebServerOptions
            {
                DireccionEscucha = IPAddress.Loopback,
                PuertoHttp = 0,
                HabilitarHttps = false,
                RegenerarTokenAlIniciar = true,
                AsignarTiempoFunc = (idC, nomC, dorsal, hora, ruta, ct) =>
                {
                    intento++;
                    if (intento == 1)
                    {
                        throw new InvalidOperationException("Fallo transitorio simulado de SQLite");
                    }

                    return dataService.AsignarTiempoAsync(idC, nomC, dorsal, hora, ruta, ct);
                }
            });

            string? idNotificado = null;
            DateTime horaNotificada = default;
            string? rutaNotificada = null;
            void OnNuevaFoto(string id, DateTime hora, string ruta)
            {
                idNotificado = id;
                horaNotificada = hora;
                rutaNotificada = ruta;
            }
            WebServer.NuevaFotoRecibida += OnNuevaFoto;

            try
            {
                using var client = CrearClienteAutenticado();
                string idFoto = await SubirFotoYValidarExitoAsync(client, CrearJpegValido(36, 36), "fallo_retry.jpg", "t-retry");

                Assert.Equal(idFoto, idNotificado);
                Assert.NotEqual(default, horaNotificada);
                Assert.True(File.Exists(rutaNotificada));

                // Primer intento falla con 500 -> la foto debe volver a Pendiente y seguir en disco y cola
                HttpResponseMessage falloResp = await client.PostAsJsonAsync("/api/assign-dorsal", new { idFoto, dorsal = 33 });
                await AssertErrorJsonAsync(falloResp, HttpStatusCode.InternalServerError);
                Assert.Contains(idFoto, WebServer.ObtenerIdsFotosPendientes());
                Assert.True(File.Exists(rutaNotificada));

                // Segundo intento reintenta y logra 200 OK
                HttpResponseMessage exitoResp = await client.PostAsJsonAsync("/api/assign-dorsal", new { idFoto, dorsal = 33 });
                Assert.Equal(HttpStatusCode.OK, exitoResp.StatusCode);
                Assert.DoesNotContain(idFoto, WebServer.ObtenerIdsFotosPendientes());
                Assert.False(File.Exists(rutaNotificada));
            }
            finally
            {
                WebServer.NuevaFotoRecibida -= OnNuevaFoto;
                WebServer.LimpiarColaPendientes();
                AppState.CambiarCarreraActiva(null);
            }
        }

        private static HttpClient CrearClienteAutenticado()
        {
            var client = new HttpClient { BaseAddress = new Uri(WebServer.UrlBaseHttpActivo!) };
            client.DefaultRequestHeaders.Add("X-Auth-Token", WebServer.TokenSesionActual);
            return client;
        }

        private static async Task<string> SubirFotoYValidarExitoAsync(
            HttpClient client,
            byte[] bytes,
            string nombreArchivo,
            string tempId)
        {
            using var form = CrearMultipartFoto(bytes, nombreArchivo, tempId);
            HttpResponseMessage resp = await client.PostAsync("/api/upload-photo", form);
            string json = await resp.Content.ReadAsStringAsync();
            Assert.True(resp.StatusCode == HttpStatusCode.OK, $"Se esperaba 200 OK pero se obtuvo {(int)resp.StatusCode}: {json}");

            using JsonDocument doc = JsonDocument.Parse(json);
            string? idFoto = doc.RootElement.GetProperty("idFoto").GetString();
            Assert.False(string.IsNullOrWhiteSpace(idFoto));
            return idFoto!;
        }

        private static MultipartFormDataContent CrearMultipartFoto(byte[] bytes, string nombreArchivo, string tempId)
        {
            var form = new MultipartFormDataContent();
            var fileContent = new ByteArrayContent(bytes);
            fileContent.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            form.Add(fileContent, "foto", nombreArchivo);
            form.Add(new StringContent(tempId), "tempId");
            return form;
        }

        private static async Task<string> AssertErrorJsonAsync(HttpResponseMessage response, HttpStatusCode esperado)
        {
            string body = await response.Content.ReadAsStringAsync();
            Assert.True(response.StatusCode == esperado, $"Esperado {(int)esperado}, obtenido {(int)response.StatusCode}. Cuerpo: {body}");
            Assert.False(string.IsNullOrWhiteSpace(body));

            using JsonDocument doc = JsonDocument.Parse(body);
            Assert.True(doc.RootElement.TryGetProperty("mensaje", out JsonElement msgProp));
            string? mensaje = msgProp.GetString();
            Assert.False(string.IsNullOrWhiteSpace(mensaje));
            return mensaje!;
        }

        private static async Task<(Carrera Carrera, Categoria Categoria, Hit Hit, Corredor Corredor)> CrearEscenarioCarreraAsync(
            TestEnvironmentFixture env,
            DateTime horaInicioHit,
            int dorsal,
            string nombreCarrera = "Carrera Prueba Web")
        {
            using var db = env.CrearContexto();
            var carrera = new Carrera { Nombre = nombreCarrera, Fecha = DateTime.Today };
            db.Carreras.Add(carrera);
            await db.SaveChangesAsync();

            var categoria = new Categoria { Nombre = "Elite", ID_Carrera = carrera.ID_Carrera };
            var hit = new Hit { Nombre_Hit = "Hit Principal", ID_Carrera = carrera.ID_Carrera, Hora_Inicio = horaInicioHit };
            db.Categorias.Add(categoria);
            db.Hits.Add(hit);
            await db.SaveChangesAsync();

            var corredor = new Corredor
            {
                Dorsal = dorsal,
                Nombre = $"Corredor {dorsal}",
                ID_Carrera = carrera.ID_Carrera,
                ID_Categoria = categoria.ID_Categoria,
                ID_Hit = hit.ID_Hit
            };
            db.Corredores.Add(corredor);
            await db.SaveChangesAsync();

            return (carrera, categoria, hit, corredor);
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

        private static BitmapSource CrearBitmapSintetico(int ancho, int alto)
        {
            int stride = ancho * 4;
            byte[] pixels = new byte[alto * stride];
            for (int y = 0; y < alto; y++)
            {
                for (int x = 0; x < ancho; x++)
                {
                    int idx = y * stride + x * 4;
                    pixels[idx + 0] = (byte)((x * 3 + y) % 256);
                    pixels[idx + 1] = (byte)((x + y * 5) % 256);
                    pixels[idx + 2] = (byte)((x * 7 + y * 2) % 256);
                    pixels[idx + 3] = 255;
                }
            }

            var bmp = BitmapSource.Create(ancho, alto, 96, 96, PixelFormats.Bgra32, null, pixels, stride);
            bmp.Freeze();
            return bmp;
        }

        private static byte[] CrearWebpLosslessValido(int ancho, int alto)
        {
            var writer = new LsbBitWriter();
            writer.WriteBits(0, 1);
            writer.WriteBits(0, 1);
            writer.WriteBits(0, 1);

            int[] simbolos = { 100, 150, 200, 255, 0 };
            foreach (int simbolo in simbolos)
            {
                writer.WriteBits(1, 1);
                writer.WriteBits(0, 1);
                writer.WriteBits(1, 1);
                writer.WriteBits(simbolo, 8);
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

            webp[20] = 0x2F;
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
    }
}

