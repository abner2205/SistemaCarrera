using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Data.Common;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CarreraApp.Data;
using CarreraApp.Helpers;
using CarreraApp.Models;
using CarreraApp.Server;
using CarreraApp.UI;
using Microsoft.EntityFrameworkCore;

namespace CarreraApp.Tests
{
    [Collection("IsolatedStorage")]
    public class PerformanceAndConcurrencyTests
    {
        [Fact]
        public async Task SimulacionCargaObjetivo_40Corredores_3HitsActivos_2ClientesConcurrentes_CeroBloqueosYCierreAutomatico()
        {
            using var env = new TestEnvironmentFixture();
            await DatabaseMaintenanceHelper.InicializarYMigrarBaseDatosAsync(env.CrearFabricaContexto());

            // RF-25: Verificar que la BD aislada opera con WAL, foreign_keys=ON y busy_timeout >= 5000
            await using (var dbPragma = env.CrearContexto())
            {
                await dbPragma.Database.OpenConnectionAsync();
                DbConnection conn = dbPragma.Database.GetDbConnection();
                Assert.Equal("wal", (await EjecutarScalarAsync(conn, "PRAGMA journal_mode;"))?.ToString()?.Trim(), ignoreCase: true);
                Assert.Equal(1L, Convert.ToInt64(await EjecutarScalarAsync(conn, "PRAGMA foreign_keys;")));
                Assert.True(Convert.ToInt64(await EjecutarScalarAsync(conn, "PRAGMA busy_timeout;")) >= 5000L);
            }

            const int totalCorredores = 40;
            DateTime ahora = DateTime.Now;

            Carrera carrera;
            List<Hit> hits;
            List<Categoria> categorias;
            List<Corredor> corredores;

            await using (var dbSeed = env.CrearContexto())
            {
                carrera = new Carrera
                {
                    Nombre = "Maraton Carga Objetivo 2026",
                    Fecha = DateTime.Today
                };
                dbSeed.Carreras.Add(carrera);
                await dbSeed.SaveChangesAsync();

                var catElite = new Categoria { Nombre = "Elite", ID_Carrera = carrera.ID_Carrera };
                var catMaster = new Categoria { Nombre = "Master", ID_Carrera = carrera.ID_Carrera };
                categorias = new List<Categoria> { catElite, catMaster };
                dbSeed.Categorias.AddRange(categorias);

                var hit1 = new Hit { Nombre_Hit = "Hit 1", ID_Carrera = carrera.ID_Carrera, Hora_Inicio = ahora.AddMinutes(-12) };
                var hit2 = new Hit { Nombre_Hit = "Hit 2", ID_Carrera = carrera.ID_Carrera, Hora_Inicio = ahora.AddMinutes(-8) };
                var hit3 = new Hit { Nombre_Hit = "Hit 3", ID_Carrera = carrera.ID_Carrera, Hora_Inicio = ahora.AddMinutes(-5) };
                hits = new List<Hit> { hit1, hit2, hit3 };
                dbSeed.Hits.AddRange(hits);
                await dbSeed.SaveChangesAsync();

                corredores = new List<Corredor>(totalCorredores);
                for (int i = 1; i <= totalCorredores; i++)
                {
                    corredores.Add(new Corredor
                    {
                        Dorsal = i,
                        Nombre = $"Corredor Carga {i:00}",
                        ID_Carrera = carrera.ID_Carrera,
                        ID_Categoria = categorias[i % categorias.Count].ID_Categoria,
                        ID_Hit = hits[(i - 1) % hits.Count].ID_Hit,
                        Estado = EstadoCorredor.Activo
                    });
                }

                dbSeed.Corredores.AddRange(corredores);
                await dbSeed.SaveChangesAsync();
            }

            Directory.CreateDirectory(env.FotosTempDir);
            string gitkeepPath = Path.Combine(env.FotosTempDir, ".gitkeep");
            await File.WriteAllTextAsync(gitkeepPath, string.Empty);

            AppState.CambiarCarreraActiva(carrera);

            var traceErrors = new ConcurrentQueue<string>();
            using var traceListener = new CapturaErroresTraceListener(traceErrors);
            Trace.Listeners.Add(traceListener);

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
                byte[] imagenJpeg = CrearJpegValido(320, 240);
                byte[] imagenPng = CrearPngValido(240, 180);

                // Repartir los 40 corredores de forma intercalada entre 2 clientes móviles concurrentes
                var loteCliente1 = corredores.Where((_, idx) => idx % 2 == 0).ToList();
                var loteCliente2 = corredores.Where((_, idx) => idx % 2 == 1).ToList();

                using var ctsLectura = new CancellationTokenSource();
                var erroresLectura = new ConcurrentQueue<string>();
                int consultasLecturaExitosas = 0;

                var cronometrosHits = hits.Select(h => new HitTimerItem
                {
                    ID_Hit = h.ID_Hit,
                    Nombre_Hit = h.Nombre_Hit,
                    Hora_Inicio = h.Hora_Inicio,
                    Hora_Fin = h.Hora_Fin
                }).ToList();

                // Lecturas concurrentes de pizarra, detalle de corredores, hits y /api/pending-photos
                Task lectorClasificacionTask = Task.Run(async () =>
                {
                    var dataService = new RaceDataService(env.CrearFabricaContexto());
                    using var clientRead = CrearClienteAutenticado();

                    while (!ctsLectura.IsCancellationRequested)
                    {
                        try
                        {
                            foreach (Categoria cat in categorias)
                            {
                                await using var dbRead = env.CrearContexto();
                                var datosPizarra = await (from t in dbRead.Tiempos.AsNoTracking()
                                                          join c in dbRead.Corredores.AsNoTracking() on t.ID_Corredor equals c.ID_Corredor
                                                          where c.ID_Categoria == cat.ID_Categoria && c.ID_Carrera == carrera.ID_Carrera
                                                          select new { c.Dorsal, c.Nombre, t.Tiempo_Neto })
                                    .ToListAsync(ctsLectura.Token);
                                _ = datosPizarra.OrderBy(x => x.Tiempo_Neto).ToList();
                            }

                            _ = await dataService.ObtenerCorredoresDetallePorCarreraAsync(carrera.ID_Carrera, ctsLectura.Token);
                            _ = await dataService.ObtenerHitsPorCarreraAsync(carrera.ID_Carrera);
                            HttpResponseMessage pendingResp = await clientRead.GetAsync("/api/pending-photos", ctsLectura.Token);
                            pendingResp.EnsureSuccessStatusCode();

                            foreach (HitTimerItem timer in cronometrosHits)
                            {
                                timer.ActualizarCronometro();
                            }

                            Interlocked.Increment(ref consultasLecturaExitosas);
                            await Task.Delay(15, ctsLectura.Token);
                        }
                        catch (OperationCanceledException) when (ctsLectura.IsCancellationRequested)
                        {
                            break;
                        }
                        catch (Exception ex)
                        {
                            erroresLectura.Enqueue(ex.ToString());
                        }
                    }
                });

                var resultadosCliente1 = new ConcurrentBag<(int Dorsal, HttpStatusCode Status, string Body, bool PersistidoTras200)>();
                var resultadosCliente2 = new ConcurrentBag<(int Dorsal, HttpStatusCode Status, string Body, bool PersistidoTras200)>();

                var startBarrier = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var stopwatch = Stopwatch.StartNew();

                Task cliente1Task = EjecutarClienteMovilAsync(
                    "movil-1",
                    loteCliente1,
                    imagenJpeg,
                    "captura.jpg",
                    env,
                    startBarrier.Task,
                    resultadosCliente1);

                Task cliente2Task = EjecutarClienteMovilAsync(
                    "movil-2",
                    loteCliente2,
                    imagenPng,
                    "captura.png",
                    env,
                    startBarrier.Task,
                    resultadosCliente2);

                startBarrier.SetResult();
                await Task.WhenAll(cliente1Task, cliente2Task);
                stopwatch.Stop();

                await ctsLectura.CancelAsync();
                await lectorClasificacionTask;

                double llegadasPorMinuto = totalCorredores / Math.Max(stopwatch.Elapsed.TotalMinutes, 1e-6);
                Assert.True(
                    llegadasPorMinuto >= 10.0,
                    $"El ritmo observado ({llegadasPorMinuto:F1} llegadas/min) estuvo por debajo del objetivo (>= 10/min).");

                var todosResultados = resultadosCliente1.Concat(resultadosCliente2).ToList();
                Assert.Equal(totalCorredores, todosResultados.Count);

                Assert.All(todosResultados, item =>
                {
                    Assert.Equal(HttpStatusCode.OK, item.Status);
                    Assert.DoesNotContain("database is locked", item.Body, StringComparison.OrdinalIgnoreCase);
                    Assert.True(item.PersistidoTras200, $"El dorsal {item.Dorsal} recibió 200 OK antes de quedar persistido en SQLite.");
                });

                Assert.Empty(erroresLectura);
                Assert.True(consultasLecturaExitosas > 0, "Debieron ejecutarse consultas concurrentes de lectura durante la carga.");

                var erroresBloqueoTrace = traceErrors
                    .Where(m => m.Contains("database is locked", StringComparison.OrdinalIgnoreCase) ||
                                m.Contains("SQLite Error 5", StringComparison.OrdinalIgnoreCase) ||
                                m.Contains("SQLite Error 6", StringComparison.OrdinalIgnoreCase))
                    .ToList();
                Assert.Empty(erroresBloqueoTrace);

                // Validar integridad final en SQLite: 40 tiempos, 3 hits cerrados con Hora_Fin >= max(Hora_Llegada)
                await using var dbFinal = env.CrearContexto();
                List<Tiempo> tiemposDb = await dbFinal.Tiempos
                    .AsNoTracking()
                    .Include(t => t.Corredor)
                    .ToListAsync();

                Assert.Equal(totalCorredores, tiemposDb.Count);
                Assert.Equal(totalCorredores, tiemposDb.Select(t => t.ID_Corredor).Distinct().Count());
                Assert.All(tiemposDb, t =>
                {
                    Assert.True(t.Tiempo_Neto >= TimeSpan.Zero);
                    Assert.True(File.Exists(t.Ruta_Foto), $"No existe la foto definitiva '{t.Ruta_Foto}'.");
                });

                List<Hit> hitsFinales = await dbFinal.Hits
                    .AsNoTracking()
                    .Where(h => h.ID_Carrera == carrera.ID_Carrera)
                    .ToListAsync();
                Assert.Equal(3, hitsFinales.Count);

                foreach (Hit hitFinal in hitsFinales)
                {
                    Assert.NotNull(hitFinal.Hora_Inicio);
                    Assert.NotNull(hitFinal.Hora_Fin);

                    DateTime maxLlegadaHit = tiemposDb
                        .Where(t => t.Corredor!.ID_Hit == hitFinal.ID_Hit)
                        .Max(t => t.Hora_Llegada);

                    Assert.True(
                        hitFinal.Hora_Fin!.Value >= maxLlegadaHit,
                        $"El hit '{hitFinal.Nombre_Hit}' cerró con Hora_Fin {hitFinal.Hora_Fin:O} < max(Hora_Llegada) {maxLlegadaHit:O}.");
                    Assert.True(hitFinal.Hora_Fin.Value >= hitFinal.Hora_Inicio!.Value);
                }

                // Validar archivos en almacenamiento y cola vacía
                string[] definitivos = Directory.GetFiles(env.FotosDefinitivasDir, "*", SearchOption.AllDirectories);
                Assert.Equal(totalCorredores, definitivos.Length);

                string[] restantesTemp = Directory.GetFiles(env.FotosTempDir, "*", SearchOption.TopDirectoryOnly)
                    .Where(f => !string.Equals(Path.GetFileName(f), ".gitkeep", StringComparison.OrdinalIgnoreCase))
                    .ToArray();
                Assert.Empty(restantesTemp);
                Assert.True(File.Exists(gitkeepPath));
                Assert.Empty(WebServer.ObtenerIdsFotosPendientes());
            }
            finally
            {
                Trace.Listeners.Remove(traceListener);
                WebServer.LimpiarColaPendientes();
                AppState.CambiarCarreraActiva(null);
            }
        }

        [Fact]
        public async Task RafagaConcurrente_DosClientesMismaFotoYMismoDorsal_SoloUno200YOtro409SinDobleInsercion()
        {
            using var env = new TestEnvironmentFixture();
            await DatabaseMaintenanceHelper.InicializarYMigrarBaseDatosAsync(env.CrearFabricaContexto());

            Carrera carrera;
            await using (var db = env.CrearContexto())
            {
                carrera = new Carrera { Nombre = "Carrera Colision Rafaga", Fecha = DateTime.Today };
                db.Carreras.Add(carrera);
                await db.SaveChangesAsync();

                var cat = new Categoria { Nombre = "General", ID_Carrera = carrera.ID_Carrera };
                var hit = new Hit { Nombre_Hit = "Hit Unico", ID_Carrera = carrera.ID_Carrera, Hora_Inicio = DateTime.Now.AddMinutes(-2) };
                db.Categorias.Add(cat);
                db.Hits.Add(hit);
                await db.SaveChangesAsync();

                for (int d = 1; d <= 10; d++)
                {
                    db.Corredores.Add(new Corredor
                    {
                        Dorsal = d,
                        Nombre = $"Corredor {d}",
                        ID_Carrera = carrera.ID_Carrera,
                        ID_Categoria = cat.ID_Categoria,
                        ID_Hit = hit.ID_Hit
                    });
                }
                await db.SaveChangesAsync();
            }

            AppState.CambiarCarreraActiva(carrera);

            var dataService = new RaceDataService(env.CrearFabricaContexto());
            var primeraEnVuelo = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var liberarPrimera = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            int modoGate = 1;
            int invocacionesServicio = 0;

            await using var server = await WebServer.IniciarEnSegundoPlanoAsync(new WebServerOptions
            {
                DireccionEscucha = IPAddress.Loopback,
                PuertoHttp = 0,
                HabilitarHttps = false,
                RegenerarTokenAlIniciar = true,
                AsignarTiempoFunc = async (idC, nomC, dorsal, hora, ruta, ct) =>
                {
                    int n = Interlocked.Increment(ref invocacionesServicio);
                    if (Volatile.Read(ref modoGate) == 1 && n == 1)
                    {
                        primeraEnVuelo.TrySetResult();
                        await liberarPrimera.Task.WaitAsync(ct);
                    }

                    return await dataService.AsignarTiempoAsync(idC, nomC, dorsal, hora, ruta, ct);
                }
            });

            try
            {
                using var clientA = CrearClienteAutenticado();
                using var clientB = CrearClienteAutenticado();
                byte[] jpeg = CrearJpegValido(64, 48);

                // Parte 1: Dos clientes concurrentes atacando exactamente la misma IdFoto
                string idFotoCompartida = await SubirFotoAsync(clientA, jpeg, "misma_foto.jpg", "burst-same-photo");

                Task<HttpResponseMessage> reqA = clientA.PostAsJsonAsync("/api/assign-dorsal", new { idFoto = idFotoCompartida, dorsal = 1 });
                await primeraEnVuelo.Task.WaitAsync(TimeSpan.FromSeconds(5));

                Task<HttpResponseMessage> reqB = clientB.PostAsJsonAsync("/api/assign-dorsal", new { idFoto = idFotoCompartida, dorsal = 2 });
                HttpResponseMessage respB = await reqB;
                Assert.Equal(HttpStatusCode.Conflict, respB.StatusCode);

                liberarPrimera.TrySetResult();
                HttpResponseMessage respA = await reqA;
                Assert.Equal(HttpStatusCode.OK, respA.StatusCode);
                Assert.Equal(1, invocacionesServicio);

                // Parte 2: Ráfaga concurrente de dos clientes sobre distintas fotos pero intentando asignar el MISMO dorsal
                Volatile.Write(ref modoGate, 0);
                string fotoDorsalA = await SubirFotoAsync(clientA, jpeg, "dorsal3_a.jpg", "burst-d3-a");
                string fotoDorsalB = await SubirFotoAsync(clientB, jpeg, "dorsal3_b.jpg", "burst-d3-b");

                var startSameDorsal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                Task<HttpResponseMessage> tDorsalA = Task.Run(async () =>
                {
                    await startSameDorsal.Task;
                    return await clientA.PostAsJsonAsync("/api/assign-dorsal", new { idFoto = fotoDorsalA, dorsal = 3 });
                });
                Task<HttpResponseMessage> tDorsalB = Task.Run(async () =>
                {
                    await startSameDorsal.Task;
                    return await clientB.PostAsJsonAsync("/api/assign-dorsal", new { idFoto = fotoDorsalB, dorsal = 3 });
                });

                startSameDorsal.SetResult();
                HttpResponseMessage[] respsDorsal3 = await Task.WhenAll(tDorsalA, tDorsalB);

                Assert.Equal(1, respsDorsal3.Count(r => r.StatusCode == HttpStatusCode.OK));
                Assert.Equal(1, respsDorsal3.Count(r => r.StatusCode == HttpStatusCode.Conflict));

                await using var dbCheck = env.CrearContexto();
                Assert.Equal(2, await dbCheck.Tiempos.CountAsync());
                Assert.Equal(1, await dbCheck.Tiempos.CountAsync(t => t.Corredor!.Dorsal == 1 || t.Corredor.Dorsal == 2));
                Assert.Equal(1, await dbCheck.Tiempos.CountAsync(t => t.Corredor!.Dorsal == 3));

                // La foto cuyo dorsal dio conflicto 409 debe permanecer pendiente e íntegra en disco
                Assert.Single(WebServer.ObtenerIdsFotosPendientes());
            }
            finally
            {
                liberarPrimera.TrySetResult();
                WebServer.LimpiarColaPendientes();
                AppState.CambiarCarreraActiva(null);
            }
        }

        [Fact]
        public async Task ConsultasConcurrentesRapidas_ReloadVersionCoordinator_DescartaResultadosObsoletosYFueraDeOrden()
        {
            using var env = new TestEnvironmentFixture();
            await DatabaseMaintenanceHelper.InicializarYMigrarBaseDatosAsync(env.CrearFabricaContexto());

            int idCarrera;
            int idCategoria1;
            int idCategoria2;

            await using (var db = env.CrearContexto())
            {
                var carrera = new Carrera { Nombre = "Carrera Versionado", Fecha = DateTime.Today };
                db.Carreras.Add(carrera);
                await db.SaveChangesAsync();
                idCarrera = carrera.ID_Carrera;

                var cat1 = new Categoria { Nombre = "Categoria Vieja", ID_Carrera = idCarrera };
                var cat2 = new Categoria { Nombre = "Categoria Reciente", ID_Carrera = idCarrera };
                db.Categorias.AddRange(cat1, cat2);

                var hit = new Hit { Nombre_Hit = "Hit 1", ID_Carrera = idCarrera, Hora_Inicio = DateTime.Now.AddMinutes(-10) };
                db.Hits.Add(hit);
                await db.SaveChangesAsync();

                idCategoria1 = cat1.ID_Categoria;
                idCategoria2 = cat2.ID_Categoria;

                var c1 = new Corredor { Dorsal = 1, Nombre = "Obsoleto Uno", ID_Carrera = idCarrera, ID_Categoria = idCategoria1, ID_Hit = hit.ID_Hit };
                var c2 = new Corredor { Dorsal = 2, Nombre = "Vigente Dos", ID_Carrera = idCarrera, ID_Categoria = idCategoria2, ID_Hit = hit.ID_Hit };
                db.Corredores.AddRange(c1, c2);
                await db.SaveChangesAsync();

                db.Tiempos.AddRange(
                    new Tiempo { ID_Corredor = c1.ID_Corredor, Hora_Llegada = DateTime.Now.AddMinutes(-5), Tiempo_Neto = TimeSpan.FromMinutes(5), Ruta_Foto = "a.jpg" },
                    new Tiempo { ID_Corredor = c2.ID_Corredor, Hora_Llegada = DateTime.Now.AddMinutes(-4), Tiempo_Neto = TimeSpan.FromMinutes(6), Ruta_Foto = "b.jpg" });
                await db.SaveChangesAsync();
            }

            using var coordinator = new ReloadVersionCoordinator();
            var aplicacionLock = new object();
            string? resultadoAplicado = null;
            int aplicacionesEfectivas = 0;

            var lentaIniciada = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var liberarLenta = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

            // Disparar consulta lenta (v1) que intenta sobrescribir tras completar una ráfaga de consultas más recientes
            Task tareaObsoleta = Task.Run(async () =>
            {
                (long v1, CancellationToken ct1) = coordinator.IniciarNuevaRecarga();

                await using var db = env.CrearContexto();
                List<string> nombres = await (from t in db.Tiempos.AsNoTracking()
                                              join c in db.Corredores.AsNoTracking() on t.ID_Corredor equals c.ID_Corredor
                                              where c.ID_Carrera == idCarrera && c.ID_Categoria == idCategoria1
                                              select c.Nombre)
                    .ToListAsync();

                lentaIniciada.TrySetResult();
                await liberarLenta.Task;

                // Aunque la consulta SQL ya hubiera terminado antes de revisar token, la versión ya no es vigente
                if (!ct1.IsCancellationRequested && coordinator.EsVigente(v1))
                {
                    lock (aplicacionLock)
                    {
                        resultadoAplicado = string.Join(",", nombres);
                        aplicacionesEfectivas++;
                    }
                }
            });

            await lentaIniciada.Task.WaitAsync(TimeSpan.FromSeconds(5));

            // Ráfaga de 15 recargas rápidas concurrentes; solo la última versión debe quedar aplicada
            const int totalRafaga = 15;
            var tareasRafaga = new List<Task>(totalRafaga);
            long ultimaVersion = 0;

            for (int i = 1; i <= totalRafaga; i++)
            {
                int iter = i;
                (long v, CancellationToken ct) = coordinator.IniciarNuevaRecarga();
                ultimaVersion = v;

                tareasRafaga.Add(Task.Run(async () =>
                {
                    await using var db = env.CrearContexto();
                    List<string> nombres = await (from t in db.Tiempos.AsNoTracking()
                                                  join c in db.Corredores.AsNoTracking() on t.ID_Corredor equals c.ID_Corredor
                                                  where c.ID_Carrera == idCarrera && c.ID_Categoria == idCategoria2
                                                  select c.Nombre)
                        .ToListAsync(CancellationToken.None);

                    if (!ct.IsCancellationRequested && coordinator.EsVigente(v))
                    {
                        lock (aplicacionLock)
                        {
                            if (!ct.IsCancellationRequested && coordinator.EsVigente(v))
                            {
                                resultadoAplicado = $"{iter}:{string.Join(",", nombres)}";
                                aplicacionesEfectivas++;
                            }
                        }
                    }
                }));
            }

            await Task.WhenAll(tareasRafaga);

            // Liberar ahora la consulta v1 rezagada para comprobar que no pisa el estado más reciente
            liberarLenta.TrySetResult();
            await tareaObsoleta;

            Assert.True(coordinator.EsVigente(ultimaVersion));
            Assert.False(coordinator.EsVigente(1));
            Assert.Equal($"{totalRafaga}:Vigente Dos", resultadoAplicado);
            Assert.True(aplicacionesEfectivas >= 1);
            Assert.DoesNotContain("Obsoleto Uno", resultadoAplicado ?? string.Empty);
        }

        private static async Task EjecutarClienteMovilAsync(
            string nombreCliente,
            IReadOnlyList<Corredor> corredoresAsignados,
            byte[] bytesImagen,
            string nombreArchivo,
            TestEnvironmentFixture env,
            Task inicioCompartido,
            ConcurrentBag<(int Dorsal, HttpStatusCode Status, string Body, bool PersistidoTras200)> resultados)
        {
            await inicioCompartido;
            using var client = CrearClienteAutenticado();

            for (int i = 0; i < corredoresAsignados.Count; i++)
            {
                Corredor corredor = corredoresAsignados[i];
                string tempId = $"{nombreCliente}-{i}-{Guid.NewGuid():N}";

                string idFoto = await SubirFotoAsync(client, bytesImagen, nombreArchivo, tempId);

                // Simular ráfagas cortas (cada 4 llegadas una mínima pausa de 10 ms) manteniendo ritmo >= 10/min
                if (i % 4 == 3)
                {
                    await Task.Delay(10);
                }

                HttpResponseMessage assignResp = await client.PostAsJsonAsync(
                    "/api/assign-dorsal",
                    new { idFoto, dorsal = corredor.Dorsal });

                string body = await assignResp.Content.ReadAsStringAsync();
                bool persistidoTras200 = false;
                if (assignResp.StatusCode == HttpStatusCode.OK)
                {
                    await using var dbCheck = env.CrearContexto();
                    persistidoTras200 = await dbCheck.Tiempos
                        .AsNoTracking()
                        .AnyAsync(t => t.ID_Corredor == corredor.ID_Corredor);
                }

                resultados.Add((corredor.Dorsal, assignResp.StatusCode, body, persistidoTras200));
            }
        }

        private static HttpClient CrearClienteAutenticado()
        {
            var client = new HttpClient { BaseAddress = new Uri(WebServer.UrlBaseHttpActivo!) };
            client.DefaultRequestHeaders.Add("X-Auth-Token", WebServer.TokenSesionActual);
            return client;
        }

        private static async Task<string> SubirFotoAsync(
            HttpClient client,
            byte[] bytes,
            string nombreArchivo,
            string tempId)
        {
            using var form = new MultipartFormDataContent();
            var fileContent = new ByteArrayContent(bytes);
            fileContent.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            form.Add(fileContent, "foto", nombreArchivo);
            form.Add(new StringContent(tempId), "tempId");

            HttpResponseMessage resp = await client.PostAsync("/api/upload-photo", form);
            string json = await resp.Content.ReadAsStringAsync();
            Assert.True(resp.StatusCode == HttpStatusCode.OK, $"Fallo upload-photo ({(int)resp.StatusCode}): {json}");

            using JsonDocument doc = JsonDocument.Parse(json);
            return doc.RootElement.GetProperty("idFoto").GetString()!;
        }

        private static async Task<object?> EjecutarScalarAsync(DbConnection conn, string sql)
        {
            await using DbCommand cmd = conn.CreateCommand();
            cmd.CommandText = sql;
            return await cmd.ExecuteScalarAsync();
        }

        private static byte[] CrearJpegValido(int ancho, int alto)
        {
            BitmapSource fuente = CrearBitmapSintetico(ancho, alto);
            var encoder = new JpegBitmapEncoder { QualityLevel = 80 };
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
                    pixels[idx + 0] = (byte)((x * 5 + y) % 256);
                    pixels[idx + 1] = (byte)((x + y * 3) % 256);
                    pixels[idx + 2] = (byte)((x * 2 + y * 7) % 256);
                    pixels[idx + 3] = 255;
                }
            }

            var bmp = BitmapSource.Create(ancho, alto, 96, 96, PixelFormats.Bgra32, null, pixels, stride);
            bmp.Freeze();
            return bmp;
        }

        private sealed class CapturaErroresTraceListener : TraceListener
        {
            private readonly ConcurrentQueue<string> _mensajes;

            public CapturaErroresTraceListener(ConcurrentQueue<string> mensajes)
            {
                _mensajes = mensajes;
            }

            public override void Write(string? message)
            {
                if (!string.IsNullOrWhiteSpace(message))
                {
                    _mensajes.Enqueue(message);
                }
            }

            public override void WriteLine(string? message)
            {
                if (!string.IsNullOrWhiteSpace(message))
                {
                    _mensajes.Enqueue(message);
                }
            }
        }
    }
}
