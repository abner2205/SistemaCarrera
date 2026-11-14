using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
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

namespace CarreraApp.Tests
{
    [Collection("IsolatedStorage")]
    public class FrontendContractTests
    {
        [Fact]
        public async Task RecursosEstaticosFrontend_SirvenHtmlYJsSinTokenConControlesDeSeguridadYCola()
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

                Assert.Contains("text/html", htmlResp.Content.Headers.ContentType?.MediaType, StringComparison.OrdinalIgnoreCase);
                Assert.Contains("javascript", jsResp.Content.Headers.ContentType?.MediaType, StringComparison.OrdinalIgnoreCase);

                string html = await htmlResp.Content.ReadAsStringAsync();
                string js = await jsResp.Content.ReadAsStringAsync();

                Assert.Contains("Llegada de Corredores", html);
                Assert.Contains("src=\"app.js\"", html);
                Assert.Contains("id=\"camera-feed\"", html);
                Assert.Contains("id=\"btn-capture\"", html);
                Assert.Contains("id=\"pending-queue\"", html);

                // Controles de autenticación, banner 401 y respaldo manual de token
                Assert.Contains("id=\"auth-banner\"", html);
                Assert.Contains("role=\"alert\"", html);
                Assert.Contains("id=\"auth-message\"", html);
                Assert.Contains("id=\"token-input\"", html);
                Assert.Contains("id=\"btn-save-token\"", html);
                Assert.Contains("id=\"status-banner\"", html);

                // Sin canvas global compartido ni handlers onclick inline con interpolación
                Assert.DoesNotContain("id=\"canvas\"", html);
                Assert.DoesNotContain("onclick=\"assignRunner", html);
                Assert.DoesNotContain("onclick=\"discardPhoto", html);

                Assert.Contains("/api/upload-photo", js);
                Assert.Contains("/api/pending-photos", js);
                Assert.Contains("/api/assign-dorsal", js);
                Assert.Contains("/api/discard-photo", js);
            }
            finally
            {
                WebServer.LimpiarColaPendientes();
                AppState.CambiarCarreraActiva(null);
            }
        }

        [Fact]
        public async Task ClienteMovil_ExtraeTokenDeUrlPersisteEnSesionYEnviaXAuthTokenEnTodosLosApi()
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
                using var unauthClient = new HttpClient { BaseAddress = new Uri(WebServer.UrlBaseHttpActivo!) };
                string js = await unauthClient.GetStringAsync("/app.js");

                // Extracción ?token= y persistencia en sesión del navegador
                Assert.Contains("URLSearchParams", js);
                Assert.Matches(@"\.get\(\s*['""]token['""]\s*\)", js);
                Assert.Contains("sessionStorage", js);
                Assert.Contains("token-input", js);
                Assert.Contains("btn-save-token", js);

                // Cabecera X-Auth-Token aplicada de forma centralizada
                Assert.Contains("X-Auth-Token", js);
                Assert.Matches(@"function\s+apiFetch\s*\(", js);

                // Todas las llamadas /api/* deben pasar por apiFetch (ningún fetch('/api/...') directo fuera del helper)
                MatchCollection directFetchApiMatches = Regex.Matches(js, @"\bfetch\s*\(\s*['""`]/api/");
                Assert.Empty(directFetchApiMatches);

                Assert.Matches(@"apiFetch\s*\(\s*['""]/api/upload-photo['""]", js);
                Assert.Matches(@"apiFetch\s*\(\s*['""]/api/pending-photos['""]", js);
                Assert.Matches(@"apiFetch\s*\(\s*['""]/api/assign-dorsal['""]", js);
                Assert.Matches(@"apiFetch\s*\(\s*['""]/api/discard-photo['""]", js);

                // Contrato real con WebServer: X-Auth-Token es aceptado y su ausencia devuelve 401
                HttpResponseMessage sinToken = await unauthClient.GetAsync("/api/pending-photos");
                Assert.Equal(HttpStatusCode.Unauthorized, sinToken.StatusCode);

                using var reqConHeader = new HttpRequestMessage(HttpMethod.Get, "/api/pending-photos");
                reqConHeader.Headers.Add("X-Auth-Token", WebServer.TokenSesionActual);
                HttpResponseMessage conHeader = await unauthClient.SendAsync(reqConHeader);
                Assert.Equal(HttpStatusCode.OK, conHeader.StatusCode);
            }
            finally
            {
                WebServer.LimpiarColaPendientes();
                AppState.CambiarCarreraActiva(null);
            }
        }

        [Fact]
        public async Task ClienteMovil_Maneja401VisualmenteYDetieneSondeoAnteCredencialInvalida()
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
                string html = await client.GetStringAsync("/index.html");
                string js = await client.GetStringAsync("/app.js");

                // UI debe explicar claramente que debe reescanear QR o actualizar el token
                string combinado = html + "\n" + js;
                Assert.Contains("401", js);
                Assert.Contains("QR", combinado, StringComparison.OrdinalIgnoreCase);
                Assert.Contains("token", combinado, StringComparison.OrdinalIgnoreCase);
                Assert.Matches(@"status\s*===\s*401", js);
                Assert.Matches(@"function\s+handleUnauthorized\s*\(", js);
                Assert.Contains("isAuthRejected", js);
                Assert.Contains("authBanner", js);

                // Contrato real con WebServer ante token inválido -> 401 JSON con propiedad "mensaje"
                using var reqInvalido = new HttpRequestMessage(HttpMethod.Get, "/api/pending-photos");
                reqInvalido.Headers.Add("X-Auth-Token", "token-caducado");
                HttpResponseMessage resp401 = await client.SendAsync(reqInvalido);
                Assert.Equal(HttpStatusCode.Unauthorized, resp401.StatusCode);

                using JsonDocument doc = JsonDocument.Parse(await resp401.Content.ReadAsStringAsync());
                Assert.True(doc.RootElement.TryGetProperty("mensaje", out JsonElement msgProp));
                Assert.False(string.IsNullOrWhiteSpace(msgProp.GetString()));
            }
            finally
            {
                WebServer.LimpiarColaPendientes();
                AppState.CambiarCarreraActiva(null);
            }
        }

        [Fact]
        public async Task ClienteMovil_BloqueaControlesContraDobleClicDuranteSolicitudesEnVuelo()
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
                string js = await client.GetStringAsync("/app.js");

                // Bloqueo anti doble clic por estado busy en assignRunner y discardPhoto
                Assert.Contains("dataset.busy", js);
                Assert.Matches(@"function\s+setItemBusy\s*\(", js);

                string cuerpoAssign = ExtraerBloqueFuncion(js, "assignRunner");
                string cuerpoDiscard = ExtraerBloqueFuncion(js, "discardPhoto");

                int idxBusyAssign = cuerpoAssign.IndexOf("setItemBusy(", StringComparison.Ordinal);
                int idxFetchAssign = cuerpoAssign.IndexOf("apiFetch(", StringComparison.Ordinal);
                Assert.True(idxBusyAssign >= 0 && idxFetchAssign > idxBusyAssign,
                    "assignRunner debe bloquear los controles del elemento antes de enviar la petición HTTP.");
                Assert.Contains("finally", cuerpoAssign);

                int idxBusyDiscard = cuerpoDiscard.IndexOf("setItemBusy(", StringComparison.Ordinal);
                int idxFetchDiscard = cuerpoDiscard.IndexOf("apiFetch(", StringComparison.Ordinal);
                int idxRemoveDiscard = cuerpoDiscard.IndexOf("eliminarItemUIRecuperandoMemoria", idxFetchDiscard, StringComparison.Ordinal);
                Assert.True(idxBusyDiscard >= 0 && idxFetchDiscard > idxBusyDiscard,
                    "discardPhoto debe bloquear los controles antes de llamar al servidor.");
                Assert.True(idxRemoveDiscard > idxFetchDiscard,
                    "discardPhoto no debe eliminar de la UI un elemento ya subido antes de recibir respuesta del servidor.");
                Assert.Contains("finally", cuerpoDiscard);
            }
            finally
            {
                WebServer.LimpiarColaPendientes();
                AppState.CambiarCarreraActiva(null);
            }
        }

        [Fact]
        public async Task ClienteMovil_ManejaErroresRealesDelServidorYRedSinMarcarVerde()
        {
            using var env = new TestEnvironmentFixture();
            await DatabaseMaintenanceHelper.InicializarYMigrarBaseDatosAsync(env.CrearFabricaContexto());

            Carrera carrera;
            using (var db = env.CrearContexto())
            {
                carrera = new Carrera { Nombre = "Carrera Errores Frontend", Fecha = DateTime.Today };
                db.Carreras.Add(carrera);
                await db.SaveChangesAsync();

                var cat = new Categoria { Nombre = "General", ID_Carrera = carrera.ID_Carrera };
                var hitFuturo = new Hit { Nombre_Hit = "Hit Futuro", ID_Carrera = carrera.ID_Carrera, Hora_Inicio = DateTime.Now.AddMinutes(10) };
                db.Categorias.Add(cat);
                db.Hits.Add(hitFuturo);
                await db.SaveChangesAsync();

                db.Corredores.Add(new Corredor
                {
                    Dorsal = 55,
                    Nombre = "Corredor Futuro",
                    ID_Carrera = carrera.ID_Carrera,
                    ID_Categoria = cat.ID_Categoria,
                    ID_Hit = hitFuturo.ID_Hit
                });
                await db.SaveChangesAsync();
            }

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
                client.DefaultRequestHeaders.Add("X-Auth-Token", WebServer.TokenSesionActual);
                string js = await client.GetStringAsync("/app.js");

                // No usar alert() bloqueante ni descartar mensajes reales de error del servidor
                Assert.DoesNotContain("alert(", js);
                Assert.Contains("extractErrorMessage", js);
                Assert.Contains("data.mensaje", js);
                Assert.Contains("item-error", js);

                // El color verde (#d4edda / item-success) solo debe aparecer en el bloque positivo de assignRunner
                string cuerpoAssign = ExtraerBloqueFuncion(js, "assignRunner");
                int idxIfOk = cuerpoAssign.IndexOf("if (res.ok)", StringComparison.Ordinal);
                int idxElse = cuerpoAssign.IndexOf("} else {", idxIfOk, StringComparison.Ordinal);
                int idxVerde = cuerpoAssign.IndexOf("#d4edda", StringComparison.Ordinal);
                Assert.True(idxIfOk >= 0 && idxElse > idxIfOk && idxVerde > idxIfOk && idxVerde < idxElse,
                    "El marcado verde (#d4edda) solo puede ejecutarse cuando res.ok es verdadero.");

                // Verificar que errores reales del servidor (400, 404, 409, 413, 415, 422) entreguen JSON { mensaje }
                byte[] jpeg = CrearJpegValido(24, 24);

                // 422 (sin carrera activa — nota: CambiarCarreraActiva limpia cola, por eso se prueba con foto subida sin carrera activa)
                AppState.CambiarCarreraActiva(null);
                using (var formSinCarrera = CrearMultipartFoto(jpeg, "sincarrera.jpg", "t-err-0"))
                {
                    HttpResponseMessage uploadSinCarrera = await client.PostAsync("/api/upload-photo", formSinCarrera);
                    Assert.Equal(HttpStatusCode.OK, uploadSinCarrera.StatusCode);
                    using JsonDocument upDoc0 = JsonDocument.Parse(await uploadSinCarrera.Content.ReadAsStringAsync());
                    string idFotoSinCarrera = upDoc0.RootElement.GetProperty("idFoto").GetString()!;

                    await AssertTieneMensajeErrorAsync(
                        await client.PostAsJsonAsync("/api/assign-dorsal", new { idFoto = idFotoSinCarrera, dorsal = 55 }),
                        HttpStatusCode.UnprocessableEntity);
                }

                // Activar carrera y subir foto para validar 400, 422 (tiempo negativo), 404 y 409
                AppState.CambiarCarreraActiva(carrera);
                using (var form = CrearMultipartFoto(jpeg, "ok.jpg", "t-err-1"))
                {
                    HttpResponseMessage uploadOk = await client.PostAsync("/api/upload-photo", form);
                    Assert.Equal(HttpStatusCode.OK, uploadOk.StatusCode);
                    using JsonDocument upDoc = JsonDocument.Parse(await uploadOk.Content.ReadAsStringAsync());
                    string idFoto = upDoc.RootElement.GetProperty("idFoto").GetString()!;

                    // 400
                    await AssertTieneMensajeErrorAsync(
                        await client.PostAsJsonAsync("/api/assign-dorsal", new { idFoto, dorsal = 0 }),
                        HttpStatusCode.BadRequest);

                    // 422 (tiempo neto negativo)
                    await AssertTieneMensajeErrorAsync(
                        await client.PostAsJsonAsync("/api/assign-dorsal", new { idFoto, dorsal = 55 }),
                        HttpStatusCode.UnprocessableEntity);

                    // 404 (dorsal inexistente)
                    await AssertTieneMensajeErrorAsync(
                        await client.PostAsJsonAsync("/api/assign-dorsal", new { idFoto, dorsal = 9999 }),
                        HttpStatusCode.NotFound);

                    // 409 (foto adquirida por otro operador)
                    Assert.True(WebServer.IntentarAdquirirFotoParaLocal(idFoto));
                    await AssertTieneMensajeErrorAsync(
                        await client.PostAsJsonAsync("/api/assign-dorsal", new { idFoto, dorsal = 55 }),
                        HttpStatusCode.Conflict);
                    WebServer.LiberarFotoLocal(idFoto);
                }

                // 415 (archivo no imagen)
                using (var fake = CrearMultipartFoto(Encoding.UTF8.GetBytes("NO_ES_IMAGEN"), "fake.jpg", "t-err-415"))
                {
                    await AssertTieneMensajeErrorAsync(
                        await client.PostAsync("/api/upload-photo", fake),
                        HttpStatusCode.UnsupportedMediaType);
                }

                // 413 (> 10 MB)
                byte[] big = new byte[ImageHelper.TamanoMaximoBytes + 1];
                Array.Copy(jpeg, big, jpeg.Length);
                using (var tooBig = CrearMultipartFoto(big, "big.jpg", "t-err-413"))
                {
                    await AssertTieneMensajeErrorAsync(
                        await client.PostAsync("/api/upload-photo", tooBig),
                        HttpStatusCode.RequestEntityTooLarge);
                }
            }
            finally
            {
                WebServer.LimpiarColaPendientes();
                AppState.CambiarCarreraActiva(null);
            }
        }

        [Fact]
        public async Task ClienteMovil_GeneraTempIdUnicoYAislaCanvasPorCapturaEnRafagas()
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
                string js = await client.GetStringAsync("/app.js");

                // tempId único: crypto.randomUUID() + respaldo secuencial ${Date.now()}-${seq}
                Assert.DoesNotContain("Date.now().toString()", js);
                Assert.Matches(@"function\s+generateTempId\s*\(", js);
                string cuerpoTempId = ExtraerBloqueFuncion(js, "generateTempId");
                Assert.Contains("crypto.randomUUID", cuerpoTempId);
                Assert.Contains("Date.now()", cuerpoTempId);
                Assert.Contains("tempIdSequence", cuerpoTempId);

                // Aislamiento de canvas en ráfagas: crear canvas dedicado por captura, nunca reutilizar canvas global
                Assert.DoesNotContain("document.getElementById('canvas')", js);
                Assert.DoesNotContain("document.getElementById(\"canvas\")", js);

                string cuerpoCapture = ExtraerBloqueFuncion(js, "capturePhotoFrame");
                Assert.Matches(@"document\.createElement\(\s*['""]canvas['""]\s*\)", cuerpoCapture);
                Assert.Contains(".drawImage(", cuerpoCapture);
                Assert.Contains(".toBlob(", cuerpoCapture);
            }
            finally
            {
                WebServer.LimpiarColaPendientes();
                AppState.CambiarCarreraActiva(null);
            }
        }

        [Fact]
        public async Task ClienteMovil_UsaSondeoSecuencialConSetTimeoutSinSetInterval()
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
                string js = await client.GetStringAsync("/app.js");

                Assert.DoesNotContain("setInterval", js);
                Assert.Contains("setTimeout", js);

                string cuerpoPoll = ExtraerBloqueFuncion(js, "pollPendingPhotos");
                Assert.Contains("isPolling", cuerpoPoll);
                Assert.Contains("/api/pending-photos", cuerpoPoll);
                Assert.Contains("finally", cuerpoPoll);
                Assert.Contains("scheduleNextPoll", cuerpoPoll);

                string cuerpoSync = ExtraerBloqueFuncion(js, "syncPendingQueueWithServer");
                Assert.Contains("dataset.busy", cuerpoSync);
                Assert.Contains("eliminarItemUIRecuperandoMemoria", cuerpoSync);
            }
            finally
            {
                WebServer.LimpiarColaPendientes();
                AppState.CambiarCarreraActiva(null);
            }
        }

        private static string ExtraerBloqueFuncion(string codigoJs, string nombreFuncion)
        {
            Match match = Regex.Match(codigoJs, $@"(async\s+)?function\s+{Regex.Escape(nombreFuncion)}\s*\([^)]*\)\s*\{{");
            Assert.True(match.Success, $"No se encontró la función '{nombreFuncion}' en app.js.");

            int inicio = match.Index;
            int idxLlaveApertura = codigoJs.IndexOf('{', match.Index);
            int profundidad = 0;
            bool enComillaSimple = false;
            bool enComillaDoble = false;
            bool enTemplate = false;
            bool escape = false;

            for (int i = idxLlaveApertura; i < codigoJs.Length; i++)
            {
                char c = codigoJs[i];

                if (escape)
                {
                    escape = false;
                    continue;
                }

                if (c == '\\' && (enComillaSimple || enComillaDoble || enTemplate))
                {
                    escape = true;
                    continue;
                }

                if (!enComillaDoble && !enTemplate && c == '\'')
                {
                    enComillaSimple = !enComillaSimple;
                    continue;
                }

                if (!enComillaSimple && !enTemplate && c == '"')
                {
                    enComillaDoble = !enComillaDoble;
                    continue;
                }

                if (!enComillaSimple && !enComillaDoble && c == '`')
                {
                    enTemplate = !enTemplate;
                    continue;
                }

                if (enComillaSimple || enComillaDoble || enTemplate)
                {
                    continue;
                }

                if (c == '{')
                {
                    profundidad++;
                }
                else if (c == '}')
                {
                    profundidad--;
                    if (profundidad == 0)
                    {
                        return codigoJs.Substring(inicio, i - inicio + 1);
                    }
                }
            }

            throw new InvalidOperationException($"No se encontró cierre de llave para la función '{nombreFuncion}'.");
        }

        private static async Task AssertTieneMensajeErrorAsync(HttpResponseMessage response, HttpStatusCode esperado)
        {
            string body = await response.Content.ReadAsStringAsync();
            Assert.True(response.StatusCode == esperado, $"Esperado {(int)esperado}, obtenido {(int)response.StatusCode}: {body}");
            using JsonDocument doc = JsonDocument.Parse(body);
            Assert.True(doc.RootElement.TryGetProperty("mensaje", out JsonElement msgProp));
            Assert.False(string.IsNullOrWhiteSpace(msgProp.GetString()));
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

        private static byte[] CrearJpegValido(int ancho, int alto)
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
            var encoder = new JpegBitmapEncoder { QualityLevel = 85 };
            encoder.Frames.Add(BitmapFrame.Create(bmp));
            using var ms = new MemoryStream();
            encoder.Save(ms);
            return ms.ToArray();
        }
    }
}
