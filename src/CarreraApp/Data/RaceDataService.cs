using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CarreraApp.Helpers;
using CarreraApp.Models;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace CarreraApp.Data
{
    public record AsignacionResultado(
        bool Exito,
        string Mensaje,
        string? TiempoFormateado = null,
        bool HitFinalizado = false,
        string? NombreHitFinalizado = null,
        int StatusCode = 200,
        string? RutaFotoDefinitiva = null)
    {
        public bool EsConflicto => StatusCode == 409;
    }

    public sealed record CierreHitResultado(
        bool HitFinalizado,
        string? NombreHitFinalizado = null,
        DateTime? HoraFin = null);

    public sealed record OperacionResultado(
        bool Exito,
        string Mensaje,
        int StatusCode = 200);

    public sealed record OperacionResultado<T>(
        bool Exito,
        string Mensaje,
        T? Entidad = default,
        int StatusCode = 200,
        bool HitFinalizado = false,
        string? NombreHitFinalizado = null);

    public sealed record CorredorDetalleDto(
        int ID_Corredor,
        int ID_Carrera,
        int Dorsal,
        string Nombre,
        int ID_Categoria,
        string NombreCategoria,
        int ID_Hit,
        string NombreHit,
        EstadoCorredor Estado,
        TimeSpan? TiempoNeto,
        DateTime? HoraLlegada);

    public class RaceDataService
    {
        private const int SqliteErrorConstraint = 19;
        private const int SqliteExtendedConstraintPrimaryKey = 1555;
        private const int SqliteExtendedConstraintUnique = 2067;

        // Semáforo por ruta de base de datos para serializar asignaciones entre múltiples instancias (móvil/PC)
        private static readonly ConcurrentDictionary<string, SemaphoreSlim> _semaforosPorBaseDatos =
            new(StringComparer.OrdinalIgnoreCase);

        private static readonly SemaphoreSlim _semaforoFallback = new(1, 1);

        private readonly Func<ApplicationDbContext> _contextFactory;

        public RaceDataService() : this(null)
        {
        }

        public RaceDataService(Func<ApplicationDbContext>? contextFactory)
        {
            _contextFactory = contextFactory ?? (() => new ApplicationDbContext());
        }

        private ApplicationDbContext CrearContexto() => _contextFactory();

        public Task<AsignacionResultado> AsignarTiempoAsync(
            int idCarrera,
            string nombreCarrera,
            int dorsal,
            DateTime horaLlegada,
            string rutaFotoTemp) =>
            AsignarTiempoAsync(idCarrera, nombreCarrera, dorsal, horaLlegada, rutaFotoTemp, CancellationToken.None);

        public async Task<AsignacionResultado> AsignarTiempoAsync(
            int idCarrera,
            string nombreCarrera,
            int dorsal,
            DateTime horaLlegada,
            string rutaFotoTemp,
            CancellationToken cancellationToken)
        {
            if (ValidarEntradasBasicas(idCarrera, dorsal, rutaFotoTemp) is { } errorEntrada)
            {
                return errorEntrada;
            }

            using var db = CrearContexto();
            SemaphoreSlim semaforo = ObtenerSemaforoParaContexto(db);
            bool lockAdquirido = false;

            try
            {
                await semaforo.WaitAsync(cancellationToken).ConfigureAwait(false);
                lockAdquirido = true;

                // 1. Obtener corredor, hit, categoría y carrera
                Corredor? corredor = await db.Corredores
                    .Include(c => c.Hit)
                    .Include(c => c.Categoria)
                    .Include(c => c.Carrera)
                    .FirstOrDefaultAsync(c => c.Dorsal == dorsal && c.ID_Carrera == idCarrera, cancellationToken)
                    .ConfigureAwait(false);

                if (corredor == null)
                {
                    return new AsignacionResultado(
                        Exito: false,
                        Mensaje: $"Dorsal {dorsal} no registrado en esta carrera.",
                        StatusCode: 404);
                }

                // 2. Validar unicidad antes de estado de cierre del Hit para responder 409 ante doble envío concurrente
                bool yaTieneTiempo = await db.Tiempos
                    .AnyAsync(t => t.ID_Corredor == corredor.ID_Corredor, cancellationToken)
                    .ConfigureAwait(false);

                if (ValidarReglasCorredorYHit(corredor, yaTieneTiempo, dorsal, horaLlegada) is { } errorRegla)
                {
                    return errorRegla;
                }

                // 3. Verificar existencia de la fotografía temporal origen
                string? fotoOrigen = ResolverRutaFotoTempExistente(rutaFotoTemp);
                if (fotoOrigen == null)
                {
                    return new AsignacionResultado(
                        Exito: false,
                        Mensaje: "La fotografía temporal no existe o ya fue procesada.",
                        StatusCode: 404);
                }

                // 4. Cálculo exacto preservando horaLlegada original y construcción segura de ruta definitiva
                TimeSpan tiempoNeto = horaLlegada - corredor.Hit!.Hora_Inicio!.Value;
                string tiempoFormateado = FormatearTiempoNeto(tiempoNeto);
                string tiempoParaArchivo = tiempoFormateado.Replace(":", "-");
                string nombreCarreraEfectivo = !string.IsNullOrWhiteSpace(nombreCarrera)
                    ? nombreCarrera
                    : (corredor.Carrera?.Nombre ?? "Sin_Carrera");

                string rutaDefinitiva = StorageHelper.ConstruirRutaFotoDefinitivaSegura(
                    nombreCarreraEfectivo,
                    corredor.Categoria?.Nombre ?? "General",
                    corredor.Nombre,
                    corredor.Dorsal,
                    tiempoParaArchivo,
                    Path.GetExtension(fotoOrigen),
                    crearDirectorio: true);

                // 5. Transacción explícita: copia a ruta definitiva antes de CommitAsync(),
                //    guardado atómico en BD y borrado del temporal SOLO tras CommitAsync() exitoso.
                bool archivoDefinitivoCreadoPorTransaccion = false;
                await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    if (!File.Exists(rutaDefinitiva))
                    {
                        archivoDefinitivoCreadoPorTransaccion = true;
                    }

                    File.Copy(fotoOrigen, rutaDefinitiva, overwrite: false);

                    db.Tiempos.Add(new Tiempo
                    {
                        ID_Corredor = corredor.ID_Corredor,
                        Hora_Llegada = horaLlegada,
                        Tiempo_Neto = tiempoNeto,
                        Ruta_Foto = rutaDefinitiva
                    });

                    await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

                    CierreHitResultado cierreHit = await EvaluarYCerrarHitAsync(db, corredor.ID_Hit, cancellationToken)
                        .ConfigureAwait(false);

                    await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

                    // Eliminar temporal únicamente después de confirmar la transacción en SQLite
                    IntentarEliminarArchivo(fotoOrigen);

                    return new AsignacionResultado(
                        Exito: true,
                        Mensaje: $"Corredor {dorsal} ({corredor.Nombre}) registrado con éxito.",
                        TiempoFormateado: tiempoFormateado,
                        HitFinalizado: cierreHit.HitFinalizado,
                        NombreHitFinalizado: cierreHit.NombreHitFinalizado,
                        StatusCode: 200,
                        RutaFotoDefinitiva: rutaDefinitiva);
                }
                catch
                {
                    try
                    {
                        await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
                    }
                    catch (Exception rollbackEx)
                    {
                        Trace.TraceWarning($"[RaceDataService] Error secundario durante rollback: {rollbackEx.Message}");
                    }

                    if (archivoDefinitivoCreadoPorTransaccion)
                    {
                        IntentarEliminarArchivo(rutaDefinitiva);
                    }

                    throw;
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (DbUpdateException dbEx)
            {
                if (EsConflictoUnicidadSqlite(dbEx))
                {
                    return new AsignacionResultado(
                        Exito: false,
                        Mensaje: $"Conflicto en base de datos: El tiempo para el corredor {dorsal} ya fue procesado concurrentemente.",
                        StatusCode: 409);
                }

                Trace.TraceError($"[RaceDataService] Error de persistencia al registrar tiempo: {dbEx}");
                return new AsignacionResultado(
                    Exito: false,
                    Mensaje: $"Error de persistencia al registrar tiempo: {dbEx.Message}",
                    StatusCode: 500);
            }
            catch (Exception ex)
            {
                Trace.TraceError($"[RaceDataService] Error interno al registrar tiempo: {ex}");
                return new AsignacionResultado(
                    Exito: false,
                    Mensaje: $"Error interno al registrar tiempo: {ex.Message}",
                    StatusCode: 500);
            }
            finally
            {
                if (lockAdquirido)
                {
                    semaforo.Release();
                }
            }
        }

        /// <summary>
        /// Evalúa si todos los corredores en estado Activo de un Hit iniciado ya cuentan con tiempo registrado
        /// (excluyendo DNS/DNF y exigiendo al menos 1 Activo). Si aplica, cierra el Hit con Hora_Fin = max(Hora_Llegada).
        /// </summary>
        public static async Task<CierreHitResultado> EvaluarYCerrarHitAsync(
            ApplicationDbContext db,
            int idHit,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(db);

            if (idHit <= 0)
            {
                return new CierreHitResultado(false);
            }

            Hit? hitDb = await db.Hits
                .FirstOrDefaultAsync(h => h.ID_Hit == idHit, cancellationToken)
                .ConfigureAwait(false);

            if (hitDb == null || hitDb.Hora_Inicio == null || hitDb.Hora_Fin != null)
            {
                return new CierreHitResultado(false, null, hitDb?.Hora_Fin);
            }

            int activosEnHit = await db.Corredores
                .CountAsync(c => c.ID_Hit == idHit && c.Estado == EstadoCorredor.Activo, cancellationToken)
                .ConfigureAwait(false);

            if (activosEnHit <= 0)
            {
                return new CierreHitResultado(false);
            }

            int activosSinTiempo = await db.Corredores
                .CountAsync(
                    c => c.ID_Hit == idHit &&
                         c.Estado == EstadoCorredor.Activo &&
                         !db.Tiempos.Any(t => t.ID_Corredor == c.ID_Corredor),
                    cancellationToken)
                .ConfigureAwait(false);

            if (activosSinTiempo > 0)
            {
                return new CierreHitResultado(false);
            }

            List<DateTime> horasLlegada = await db.Tiempos
                .Where(t => db.Corredores.Any(c => c.ID_Hit == idHit && c.ID_Corredor == t.ID_Corredor))
                .Select(t => t.Hora_Llegada)
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);

            if (horasLlegada.Count == 0)
            {
                return new CierreHitResultado(false);
            }

            DateTime maxHoraLlegada = horasLlegada.Max();
            if (maxHoraLlegada < hitDb.Hora_Inicio.Value)
            {
                maxHoraLlegada = hitDb.Hora_Inicio.Value;
            }

            hitDb.Hora_Fin = maxHoraLlegada;
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

            return new CierreHitResultado(true, hitDb.Nombre_Hit, hitDb.Hora_Fin);
        }

        // Consultas optimizadas de Solo Lectura con AsNoTracking()
        public async Task<List<Carrera>> ObtenerCarrerasAsync()
        {
            using var db = CrearContexto();
            return await db.Carreras.AsNoTracking().OrderByDescending(c => c.Fecha).ToListAsync().ConfigureAwait(false);
        }

        public async Task<List<Hit>> ObtenerHitsPorCarreraAsync(int idCarrera)
        {
            using var db = CrearContexto();
            return await db.Hits.AsNoTracking().Where(h => h.ID_Carrera == idCarrera).ToListAsync().ConfigureAwait(false);
        }

        public async Task<List<Categoria>> ObtenerCategoriasPorCarreraAsync(int idCarrera)
        {
            using var db = CrearContexto();
            return await db.Categorias.AsNoTracking().Where(c => c.ID_Carrera == idCarrera).ToListAsync().ConfigureAwait(false);
        }

        public async Task<List<CorredorDetalleDto>> ObtenerCorredoresDetallePorCarreraAsync(
            int idCarrera,
            CancellationToken cancellationToken = default)
        {
            if (idCarrera <= 0)
            {
                return new List<CorredorDetalleDto>();
            }

            using var db = CrearContexto();
            return await (from c in db.Corredores.AsNoTracking()
                          where c.ID_Carrera == idCarrera
                          join cat in db.Categorias.AsNoTracking() on c.ID_Categoria equals cat.ID_Categoria into catJoin
                          from cat in catJoin.DefaultIfEmpty()
                          join hit in db.Hits.AsNoTracking() on c.ID_Hit equals hit.ID_Hit into hitJoin
                          from hit in hitJoin.DefaultIfEmpty()
                          join t in db.Tiempos.AsNoTracking() on c.ID_Corredor equals t.ID_Corredor into tJoin
                          from t in tJoin.DefaultIfEmpty()
                          orderby c.Dorsal
                          select new CorredorDetalleDto(
                              c.ID_Corredor,
                              c.ID_Carrera,
                              c.Dorsal,
                              c.Nombre,
                              c.ID_Categoria,
                              cat != null ? cat.Nombre : "Sin categoría",
                              c.ID_Hit,
                              hit != null ? hit.Nombre_Hit : "Sin hit",
                              c.Estado,
                              t != null ? t.Tiempo_Neto : null,
                              t != null ? t.Hora_Llegada : null))
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);
        }

        public async Task<OperacionResultado<Carrera>> CrearOActualizarCarreraAsync(
            int? idCarrera,
            string? nombre,
            DateTime? fecha,
            string? rutaLogoIzquierdo = null,
            string? rutaLogoDerecho = null,
            CancellationToken cancellationToken = default)
        {
            if (idCarrera.HasValue && idCarrera.Value <= 0)
            {
                return new OperacionResultado<Carrera>(false, "El identificador de la carrera es inválido.", StatusCode: 400);
            }

            string nombreLimpio = nombre?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(nombreLimpio))
            {
                return new OperacionResultado<Carrera>(false, "El nombre de la carrera es obligatorio.", StatusCode: 400);
            }

            if (!EsFechaCarreraValida(fecha))
            {
                return new OperacionResultado<Carrera>(false, "La fecha de la carrera no es válida.", StatusCode: 400);
            }

            return await EjecutarConSemaforoAsync(
                async db =>
                {
                    if (!idCarrera.HasValue)
                    {
                        var nueva = new Carrera
                        {
                            Nombre = nombreLimpio,
                            Fecha = fecha!.Value,
                            RutaLogoIzquierdo = rutaLogoIzquierdo,
                            RutaLogoDerecho = rutaLogoDerecho
                        };

                        db.Carreras.Add(nueva);
                        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

                        return new OperacionResultado<Carrera>(
                            true,
                            $"Carrera '{nueva.Nombre}' creada correctamente.",
                            Entidad: nueva,
                            StatusCode: 200);
                    }

                    Carrera? existente = await db.Carreras
                        .FirstOrDefaultAsync(c => c.ID_Carrera == idCarrera.Value, cancellationToken)
                        .ConfigureAwait(false);

                    if (existente == null)
                    {
                        return new OperacionResultado<Carrera>(false, "La carrera especificada no existe.", StatusCode: 404);
                    }

                    existente.Nombre = nombreLimpio;
                    existente.Fecha = fecha!.Value;
                    if (rutaLogoIzquierdo != null)
                    {
                        existente.RutaLogoIzquierdo = rutaLogoIzquierdo;
                    }
                    if (rutaLogoDerecho != null)
                    {
                        existente.RutaLogoDerecho = rutaLogoDerecho;
                    }

                    await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

                    return new OperacionResultado<Carrera>(
                        true,
                        $"Carrera '{existente.Nombre}' actualizada correctamente.",
                        Entidad: existente,
                        StatusCode: 200);
                },
                ex => CrearErrorResultado<Carrera>("guardar la carrera", ex),
                cancellationToken).ConfigureAwait(false);
        }

        public async Task<OperacionResultado<Categoria>> CrearOActualizarCategoriaAsync(
            int? idCategoria,
            int idCarrera,
            string? nombre,
            CancellationToken cancellationToken = default)
        {
            if (idCategoria.HasValue && idCategoria.Value <= 0)
            {
                return new OperacionResultado<Categoria>(false, "El identificador de la categoría es inválido.", StatusCode: 400);
            }

            if (idCarrera <= 0)
            {
                return new OperacionResultado<Categoria>(false, "El identificador de la carrera es inválido.", StatusCode: 400);
            }

            string nombreLimpio = nombre?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(nombreLimpio))
            {
                return new OperacionResultado<Categoria>(false, "El nombre de la categoría no puede estar vacío.", StatusCode: 400);
            }

            return await EjecutarConSemaforoAsync(
                async db =>
                {
                    bool carreraExiste = await db.Carreras
                        .AnyAsync(c => c.ID_Carrera == idCarrera, cancellationToken)
                        .ConfigureAwait(false);
                    if (!carreraExiste)
                    {
                        return new OperacionResultado<Categoria>(false, "La carrera especificada no existe.", StatusCode: 404);
                    }

                    List<string> nombresExistentes = await db.Categorias
                        .AsNoTracking()
                        .Where(c => c.ID_Carrera == idCarrera && (!idCategoria.HasValue || c.ID_Categoria != idCategoria.Value))
                        .Select(c => c.Nombre)
                        .ToListAsync(cancellationToken)
                        .ConfigureAwait(false);

                    if (nombresExistentes.Any(n => string.Equals(n?.Trim(), nombreLimpio, StringComparison.OrdinalIgnoreCase)))
                    {
                        return new OperacionResultado<Categoria>(
                            false,
                            $"Ya existe una categoría llamada '{nombreLimpio}' en esta carrera.",
                            StatusCode: 409);
                    }

                    if (!idCategoria.HasValue)
                    {
                        var nueva = new Categoria
                        {
                            Nombre = nombreLimpio,
                            ID_Carrera = idCarrera
                        };

                        db.Categorias.Add(nueva);
                        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

                        return new OperacionResultado<Categoria>(
                            true,
                            $"Categoría '{nueva.Nombre}' guardada correctamente.",
                            Entidad: nueva,
                            StatusCode: 200);
                    }

                    Categoria? catDb = await db.Categorias
                        .FirstOrDefaultAsync(c => c.ID_Categoria == idCategoria.Value, cancellationToken)
                        .ConfigureAwait(false);

                    if (catDb == null)
                    {
                        return new OperacionResultado<Categoria>(false, "La categoría especificada no existe.", StatusCode: 404);
                    }

                    if (catDb.ID_Carrera != idCarrera)
                    {
                        return new OperacionResultado<Categoria>(false, "La categoría no pertenece a la carrera activa.", StatusCode: 400);
                    }

                    catDb.Nombre = nombreLimpio;
                    await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

                    return new OperacionResultado<Categoria>(
                        true,
                        $"Categoría '{catDb.Nombre}' actualizada correctamente.",
                        Entidad: catDb,
                        StatusCode: 200);
                },
                ex => CrearErrorResultado<Categoria>("guardar la categoría", ex),
                cancellationToken).ConfigureAwait(false);
        }

        public async Task<OperacionResultado<Hit>> CrearOActualizarHitAsync(
            int? idHit,
            int idCarrera,
            string? nombreHit,
            CancellationToken cancellationToken = default)
        {
            if (idHit.HasValue && idHit.Value <= 0)
            {
                return new OperacionResultado<Hit>(false, "El identificador del Hit es inválido.", StatusCode: 400);
            }

            if (idCarrera <= 0)
            {
                return new OperacionResultado<Hit>(false, "El identificador de la carrera es inválido.", StatusCode: 400);
            }

            string nombreLimpio = nombreHit?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(nombreLimpio))
            {
                return new OperacionResultado<Hit>(false, "El nombre del Hit no puede estar vacío.", StatusCode: 400);
            }

            return await EjecutarConSemaforoAsync(
                async db =>
                {
                    bool carreraExiste = await db.Carreras
                        .AnyAsync(c => c.ID_Carrera == idCarrera, cancellationToken)
                        .ConfigureAwait(false);
                    if (!carreraExiste)
                    {
                        return new OperacionResultado<Hit>(false, "La carrera especificada no existe.", StatusCode: 404);
                    }

                    List<string> nombresExistentes = await db.Hits
                        .AsNoTracking()
                        .Where(h => h.ID_Carrera == idCarrera && (!idHit.HasValue || h.ID_Hit != idHit.Value))
                        .Select(h => h.Nombre_Hit)
                        .ToListAsync(cancellationToken)
                        .ConfigureAwait(false);

                    if (nombresExistentes.Any(n => string.Equals(n?.Trim(), nombreLimpio, StringComparison.OrdinalIgnoreCase)))
                    {
                        return new OperacionResultado<Hit>(
                            false,
                            $"Ya existe un Hit llamado '{nombreLimpio}' en esta carrera.",
                            StatusCode: 409);
                    }

                    if (!idHit.HasValue)
                    {
                        var nuevo = new Hit
                        {
                            Nombre_Hit = nombreLimpio,
                            ID_Carrera = idCarrera
                        };

                        db.Hits.Add(nuevo);
                        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

                        return new OperacionResultado<Hit>(
                            true,
                            $"Hit '{nuevo.Nombre_Hit}' guardado correctamente.",
                            Entidad: nuevo,
                            StatusCode: 200);
                    }

                    Hit? hitDb = await db.Hits
                        .FirstOrDefaultAsync(h => h.ID_Hit == idHit.Value, cancellationToken)
                        .ConfigureAwait(false);

                    if (hitDb == null)
                    {
                        return new OperacionResultado<Hit>(false, "El Hit especificado no existe.", StatusCode: 404);
                    }

                    if (hitDb.ID_Carrera != idCarrera)
                    {
                        return new OperacionResultado<Hit>(false, "El Hit no pertenece a la carrera activa.", StatusCode: 400);
                    }

                    hitDb.Nombre_Hit = nombreLimpio;
                    await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

                    return new OperacionResultado<Hit>(
                        true,
                        $"Hit '{hitDb.Nombre_Hit}' actualizado correctamente.",
                        Entidad: hitDb,
                        StatusCode: 200);
                },
                ex => CrearErrorResultado<Hit>("guardar el Hit", ex),
                cancellationToken).ConfigureAwait(false);
        }

        public async Task<OperacionResultado<Corredor>> CrearOActualizarCorredorAsync(
            int? idCorredor,
            int idCarrera,
            int dorsal,
            string? nombre,
            int idCategoria,
            int idHit,
            EstadoCorredor? estado = null,
            CancellationToken cancellationToken = default)
        {
            if (idCorredor.HasValue && idCorredor.Value <= 0)
            {
                return new OperacionResultado<Corredor>(false, "El identificador del corredor es inválido.", StatusCode: 400);
            }

            if (idCarrera <= 0)
            {
                return new OperacionResultado<Corredor>(false, "El identificador de la carrera es inválido.", StatusCode: 400);
            }

            if (dorsal <= 0)
            {
                return new OperacionResultado<Corredor>(false, "El dorsal debe ser un número entero positivo mayor a cero.", StatusCode: 400);
            }

            string nombreLimpio = nombre?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(nombreLimpio))
            {
                return new OperacionResultado<Corredor>(false, "El nombre del corredor es obligatorio.", StatusCode: 400);
            }

            if (idCategoria <= 0 || idHit <= 0)
            {
                return new OperacionResultado<Corredor>(false, "Debes seleccionar una categoría y un Hit válidos.", StatusCode: 400);
            }

            if (estado.HasValue && !Enum.IsDefined(typeof(EstadoCorredor), estado.Value))
            {
                return new OperacionResultado<Corredor>(false, "El estado especificado para el corredor no es válido.", StatusCode: 400);
            }

            return await EjecutarConSemaforoAsync(
                async db =>
                {
                    bool carreraExiste = await db.Carreras
                        .AnyAsync(c => c.ID_Carrera == idCarrera, cancellationToken)
                        .ConfigureAwait(false);
                    if (!carreraExiste)
                    {
                        return new OperacionResultado<Corredor>(false, "La carrera especificada no existe.", StatusCode: 404);
                    }

                    bool categoriaValida = await db.Categorias
                        .AnyAsync(c => c.ID_Categoria == idCategoria && c.ID_Carrera == idCarrera, cancellationToken)
                        .ConfigureAwait(false);
                    if (!categoriaValida)
                    {
                        return new OperacionResultado<Corredor>(false, "La categoría seleccionada no existe o no pertenece a la carrera.", StatusCode: 422);
                    }

                    bool hitValido = await db.Hits
                        .AnyAsync(h => h.ID_Hit == idHit && h.ID_Carrera == idCarrera, cancellationToken)
                        .ConfigureAwait(false);
                    if (!hitValido)
                    {
                        return new OperacionResultado<Corredor>(false, "El Hit seleccionado no existe o no pertenece a la carrera.", StatusCode: 422);
                    }

                    bool dorsalDuplicado = await db.Corredores
                        .AnyAsync(
                            c => c.ID_Carrera == idCarrera &&
                                 c.Dorsal == dorsal &&
                                 (!idCorredor.HasValue || c.ID_Corredor != idCorredor.Value),
                            cancellationToken)
                        .ConfigureAwait(false);
                    if (dorsalDuplicado)
                    {
                        return new OperacionResultado<Corredor>(
                            false,
                            $"El número de dorsal {dorsal} ya está registrado en esta carrera.",
                            StatusCode: 409);
                    }

                    await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
                    try
                    {
                        if (!idCorredor.HasValue)
                        {
                            EstadoCorredor estadoInicial = estado ?? EstadoCorredor.Activo;
                            var nuevo = new Corredor
                            {
                                Dorsal = dorsal,
                                Nombre = nombreLimpio,
                                ID_Categoria = idCategoria,
                                ID_Hit = idHit,
                                ID_Carrera = idCarrera,
                                Estado = estadoInicial
                            };

                            db.Corredores.Add(nuevo);
                            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

                            CierreHitResultado cierreNuevo = await ReevaluarEstadoHitTrasCambioCorredorAsync(db, idHit, cancellationToken)
                                .ConfigureAwait(false);

                            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

                            return new OperacionResultado<Corredor>(
                                true,
                                $"Corredor {nuevo.Dorsal} ({nuevo.Nombre}) registrado correctamente.",
                                Entidad: nuevo,
                                StatusCode: 200,
                                HitFinalizado: cierreNuevo.HitFinalizado,
                                NombreHitFinalizado: cierreNuevo.NombreHitFinalizado);
                        }

                        Corredor? corredorDb = await db.Corredores
                            .FirstOrDefaultAsync(c => c.ID_Corredor == idCorredor.Value, cancellationToken)
                            .ConfigureAwait(false);

                        if (corredorDb == null)
                        {
                            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
                            return new OperacionResultado<Corredor>(false, "Corredor no encontrado.", StatusCode: 404);
                        }

                        if (corredorDb.ID_Carrera != idCarrera)
                        {
                            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
                            return new OperacionResultado<Corredor>(false, "El corredor no pertenece a la carrera activa.", StatusCode: 400);
                        }

                        bool tieneTiempo = await db.Tiempos
                            .AnyAsync(t => t.ID_Corredor == corredorDb.ID_Corredor, cancellationToken)
                            .ConfigureAwait(false);

                        EstadoCorredor estadoFinal = estado ?? corredorDb.Estado;

                        if (tieneTiempo)
                        {
                            if (idHit != corredorDb.ID_Hit)
                            {
                                await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
                                return new OperacionResultado<Corredor>(
                                    false,
                                    "No se puede cambiar el Hit de un corredor que ya cuenta con un tiempo registrado.",
                                    StatusCode: 422);
                            }

                            if (estadoFinal != corredorDb.Estado)
                            {
                                await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
                                return new OperacionResultado<Corredor>(
                                    false,
                                    "No se puede cambiar el estado a DNS/DNF de un corredor que ya cuenta con un tiempo registrado.",
                                    StatusCode: 422);
                            }
                        }

                        int hitAnterior = corredorDb.ID_Hit;

                        corredorDb.Dorsal = dorsal;
                        corredorDb.Nombre = nombreLimpio;
                        corredorDb.ID_Categoria = idCategoria;
                        corredorDb.ID_Hit = idHit;
                        corredorDb.Estado = estadoFinal;

                        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

                        CierreHitResultado cierreHit = new(false);
                        if (!tieneTiempo)
                        {
                            if (hitAnterior != idHit)
                            {
                                CierreHitResultado cierreAnterior = await ReevaluarEstadoHitTrasCambioCorredorAsync(db, hitAnterior, cancellationToken)
                                    .ConfigureAwait(false);
                                if (cierreAnterior.HitFinalizado)
                                {
                                    cierreHit = cierreAnterior;
                                }
                            }

                            CierreHitResultado cierreDestino = await ReevaluarEstadoHitTrasCambioCorredorAsync(db, idHit, cancellationToken)
                                .ConfigureAwait(false);
                            if (cierreDestino.HitFinalizado)
                            {
                                cierreHit = cierreDestino;
                            }
                        }

                        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

                        return new OperacionResultado<Corredor>(
                            true,
                            $"Corredor {corredorDb.Dorsal} ({corredorDb.Nombre}) actualizado correctamente.",
                            Entidad: corredorDb,
                            StatusCode: 200,
                            HitFinalizado: cierreHit.HitFinalizado,
                            NombreHitFinalizado: cierreHit.NombreHitFinalizado);
                    }
                    catch
                    {
                        try
                        {
                            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
                        }
                        catch (Exception rollbackEx)
                        {
                            Trace.TraceWarning($"[RaceDataService] Error secundario en rollback de corredor: {rollbackEx.Message}");
                        }

                        throw;
                    }
                },
                ex => CrearErrorResultado<Corredor>("guardar el corredor", ex),
                cancellationToken).ConfigureAwait(false);
        }

        public async Task<OperacionResultado<Corredor>> CambiarEstadoCorredorAsync(
            int idCorredor,
            EstadoCorredor nuevoEstado,
            CancellationToken cancellationToken = default)
        {
            if (idCorredor <= 0)
            {
                return new OperacionResultado<Corredor>(false, "El identificador del corredor es inválido.", StatusCode: 400);
            }

            if (!Enum.IsDefined(typeof(EstadoCorredor), nuevoEstado))
            {
                return new OperacionResultado<Corredor>(false, "El estado especificado no es válido.", StatusCode: 400);
            }

            return await EjecutarConSemaforoAsync(
                async db =>
                {
                    Corredor? corredorDb = await db.Corredores
                        .FirstOrDefaultAsync(c => c.ID_Corredor == idCorredor, cancellationToken)
                        .ConfigureAwait(false);

                    if (corredorDb == null)
                    {
                        return new OperacionResultado<Corredor>(false, "Corredor no encontrado.", StatusCode: 404);
                    }

                    bool tieneTiempo = await db.Tiempos
                        .AnyAsync(t => t.ID_Corredor == idCorredor, cancellationToken)
                        .ConfigureAwait(false);

                    if (tieneTiempo)
                    {
                        return new OperacionResultado<Corredor>(
                            false,
                            "No se puede modificar el estado del corredor porque ya tiene un tiempo registrado.",
                            StatusCode: 422);
                    }

                    await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
                    try
                    {
                        corredorDb.Estado = nuevoEstado;
                        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

                        CierreHitResultado cierreHit = await ReevaluarEstadoHitTrasCambioCorredorAsync(db, corredorDb.ID_Hit, cancellationToken)
                            .ConfigureAwait(false);

                        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

                        return new OperacionResultado<Corredor>(
                            true,
                            $"Estado del corredor {corredorDb.Dorsal} actualizado a {nuevoEstado}.",
                            Entidad: corredorDb,
                            StatusCode: 200,
                            HitFinalizado: cierreHit.HitFinalizado,
                            NombreHitFinalizado: cierreHit.NombreHitFinalizado);
                    }
                    catch
                    {
                        try
                        {
                            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
                        }
                        catch (Exception rollbackEx)
                        {
                            Trace.TraceWarning($"[RaceDataService] Error secundario en rollback de estado: {rollbackEx.Message}");
                        }

                        throw;
                    }
                },
                ex => CrearErrorResultado<Corredor>("cambiar el estado del corredor", ex),
                cancellationToken).ConfigureAwait(false);
        }

        public async Task<OperacionResultado> EliminarCorredorAsync(
            int idCorredor,
            CancellationToken cancellationToken = default)
        {
            if (idCorredor <= 0)
            {
                return new OperacionResultado(false, "El identificador del corredor es inválido.", StatusCode: 400);
            }

            return await EjecutarConSemaforoAsync(
                async db =>
                {
                    Corredor? corredorDb = await db.Corredores
                        .FirstOrDefaultAsync(c => c.ID_Corredor == idCorredor, cancellationToken)
                        .ConfigureAwait(false);

                    if (corredorDb == null)
                    {
                        return new OperacionResultado(false, "Corredor no encontrado.", StatusCode: 404);
                    }

                    bool tieneTiempo = await db.Tiempos
                        .AnyAsync(t => t.ID_Corredor == idCorredor, cancellationToken)
                        .ConfigureAwait(false);

                    if (tieneTiempo)
                    {
                        return new OperacionResultado(
                            false,
                            "Registro histórico protegido: no se puede eliminar un corredor con tiempo registrado. Utiliza los estados DNS o DNF en corredores sin tiempo.",
                            StatusCode: 409);
                    }

                    await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
                    try
                    {
                        int idHit = corredorDb.ID_Hit;
                        db.Corredores.Remove(corredorDb);
                        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

                        await ReevaluarEstadoHitTrasCambioCorredorAsync(db, idHit, cancellationToken).ConfigureAwait(false);
                        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

                        return new OperacionResultado(true, $"Corredor {corredorDb.Dorsal} eliminado correctamente.", StatusCode: 200);
                    }
                    catch
                    {
                        try
                        {
                            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
                        }
                        catch (Exception rollbackEx)
                        {
                            Trace.TraceWarning($"[RaceDataService] Error secundario en rollback al eliminar corredor: {rollbackEx.Message}");
                        }

                        throw;
                    }
                },
                ex => CrearErrorResultado("eliminar el corredor", ex),
                cancellationToken).ConfigureAwait(false);
        }

        public async Task<OperacionResultado> EliminarHitAsync(
            int idHit,
            CancellationToken cancellationToken = default)
        {
            if (idHit <= 0)
            {
                return new OperacionResultado(false, "El identificador del Hit es inválido.", StatusCode: 400);
            }

            return await EjecutarConSemaforoAsync(
                async db =>
                {
                    Hit? hitDb = await db.Hits
                        .FirstOrDefaultAsync(h => h.ID_Hit == idHit, cancellationToken)
                        .ConfigureAwait(false);

                    if (hitDb == null)
                    {
                        return new OperacionResultado(false, "Hit no encontrado.", StatusCode: 404);
                    }

                    bool tieneTiempos = await db.Tiempos
                        .AnyAsync(t => db.Corredores.Any(c => c.ID_Corredor == t.ID_Corredor && c.ID_Hit == idHit), cancellationToken)
                        .ConfigureAwait(false);

                    if (tieneTiempos)
                    {
                        return new OperacionResultado(
                            false,
                            "Registro histórico protegido: no se puede eliminar el Hit porque contiene corredores con tiempos registrados. Utiliza los estados DNS o DNF para bajas.",
                            StatusCode: 409);
                    }

                    bool tieneCorredores = await db.Corredores
                        .AnyAsync(c => c.ID_Hit == idHit, cancellationToken)
                        .ConfigureAwait(false);

                    if (tieneCorredores)
                    {
                        return new OperacionResultado(
                            false,
                            "No se puede eliminar el Hit porque aún hay corredores asignados. Reasigna o elimina esos corredores primero.",
                            StatusCode: 409);
                    }

                    db.Hits.Remove(hitDb);
                    await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

                    return new OperacionResultado(true, $"Hit '{hitDb.Nombre_Hit}' eliminado correctamente.", StatusCode: 200);
                },
                ex => CrearErrorResultado("eliminar el Hit", ex),
                cancellationToken).ConfigureAwait(false);
        }

        public async Task<OperacionResultado> EliminarCategoriaAsync(
            int idCategoria,
            CancellationToken cancellationToken = default)
        {
            if (idCategoria <= 0)
            {
                return new OperacionResultado(false, "El identificador de la categoría es inválido.", StatusCode: 400);
            }

            return await EjecutarConSemaforoAsync(
                async db =>
                {
                    Categoria? catDb = await db.Categorias
                        .FirstOrDefaultAsync(c => c.ID_Categoria == idCategoria, cancellationToken)
                        .ConfigureAwait(false);

                    if (catDb == null)
                    {
                        return new OperacionResultado(false, "Categoría no encontrada.", StatusCode: 404);
                    }

                    bool tieneTiempos = await db.Tiempos
                        .AnyAsync(t => db.Corredores.Any(c => c.ID_Corredor == t.ID_Corredor && c.ID_Categoria == idCategoria), cancellationToken)
                        .ConfigureAwait(false);

                    if (tieneTiempos)
                    {
                        return new OperacionResultado(
                            false,
                            "Registro histórico protegido: no se puede eliminar la categoría porque contiene corredores con tiempos registrados. Utiliza los estados DNS o DNF para bajas.",
                            StatusCode: 409);
                    }

                    bool tieneCorredores = await db.Corredores
                        .AnyAsync(c => c.ID_Categoria == idCategoria, cancellationToken)
                        .ConfigureAwait(false);

                    if (tieneCorredores)
                    {
                        return new OperacionResultado(
                            false,
                            "No se puede eliminar la categoría porque aún hay corredores asignados. Reasigna o elimina esos corredores primero.",
                            StatusCode: 409);
                    }

                    db.Categorias.Remove(catDb);
                    await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

                    return new OperacionResultado(true, $"Categoría '{catDb.Nombre}' eliminada correctamente.", StatusCode: 200);
                },
                ex => CrearErrorResultado("eliminar la categoría", ex),
                cancellationToken).ConfigureAwait(false);
        }

        public async Task<OperacionResultado> EliminarCarreraAsync(
            int idCarrera,
            CancellationToken cancellationToken = default)
        {
            if (idCarrera <= 0)
            {
                return new OperacionResultado(false, "El identificador de la carrera es inválido.", StatusCode: 400);
            }

            return await EjecutarConSemaforoAsync(
                async db =>
                {
                    Carrera? carreraDb = await db.Carreras
                        .FirstOrDefaultAsync(c => c.ID_Carrera == idCarrera, cancellationToken)
                        .ConfigureAwait(false);

                    if (carreraDb == null)
                    {
                        return new OperacionResultado(false, "Carrera no encontrada.", StatusCode: 404);
                    }

                    bool tieneTiempos = await db.Tiempos
                        .AnyAsync(t => db.Corredores.Any(c => c.ID_Corredor == t.ID_Corredor && c.ID_Carrera == idCarrera), cancellationToken)
                        .ConfigureAwait(false);

                    if (tieneTiempos)
                    {
                        return new OperacionResultado(
                            false,
                            "Registro histórico protegido: no se puede eliminar la carrera porque contiene al menos un corredor con tiempo registrado. Utiliza los estados DNS o DNF en corredores sin tiempo.",
                            StatusCode: 409);
                    }

                    await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
                    try
                    {
                        // Eliminar primero corredores para respetar DeleteBehavior.Restrict hacia Categoría y Hit
                        List<Corredor> corredores = await db.Corredores
                            .Where(c => c.ID_Carrera == idCarrera)
                            .ToListAsync(cancellationToken)
                            .ConfigureAwait(false);
                        if (corredores.Count > 0)
                        {
                            db.Corredores.RemoveRange(corredores);
                            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                        }

                        List<Hit> hits = await db.Hits
                            .Where(h => h.ID_Carrera == idCarrera)
                            .ToListAsync(cancellationToken)
                            .ConfigureAwait(false);
                        if (hits.Count > 0)
                        {
                            db.Hits.RemoveRange(hits);
                        }

                        List<Categoria> categorias = await db.Categorias
                            .Where(c => c.ID_Carrera == idCarrera)
                            .ToListAsync(cancellationToken)
                            .ConfigureAwait(false);
                        if (categorias.Count > 0)
                        {
                            db.Categorias.RemoveRange(categorias);
                        }

                        db.Carreras.Remove(carreraDb);
                        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

                        return new OperacionResultado(true, $"Carrera '{carreraDb.Nombre}' eliminada correctamente.", StatusCode: 200);
                    }
                    catch
                    {
                        try
                        {
                            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
                        }
                        catch (Exception rollbackEx)
                        {
                            Trace.TraceWarning($"[RaceDataService] Error secundario en rollback al eliminar carrera: {rollbackEx.Message}");
                        }

                        throw;
                    }
                },
                ex => CrearErrorResultado("eliminar la carrera", ex),
                cancellationToken).ConfigureAwait(false);
        }

        private async Task<TResult> EjecutarConSemaforoAsync<TResult>(
            Func<ApplicationDbContext, Task<TResult>> accion,
            Func<Exception, TResult> onError,
            CancellationToken cancellationToken)
        {
            using var db = CrearContexto();
            SemaphoreSlim semaforo = ObtenerSemaforoParaContexto(db);
            bool lockAdquirido = false;

            try
            {
                await semaforo.WaitAsync(cancellationToken).ConfigureAwait(false);
                lockAdquirido = true;
                return await accion(db).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                return onError(ex);
            }
            finally
            {
                if (lockAdquirido)
                {
                    semaforo.Release();
                }
            }
        }

        private static async Task<CierreHitResultado> ReevaluarEstadoHitTrasCambioCorredorAsync(
            ApplicationDbContext db,
            int idHit,
            CancellationToken cancellationToken)
        {
            Hit? hitDb = await db.Hits
                .FirstOrDefaultAsync(h => h.ID_Hit == idHit, cancellationToken)
                .ConfigureAwait(false);

            if (hitDb == null || hitDb.Hora_Inicio == null)
            {
                return new CierreHitResultado(false);
            }

            if (hitDb.Hora_Fin == null)
            {
                return await EvaluarYCerrarHitAsync(db, idHit, cancellationToken).ConfigureAwait(false);
            }

            bool hayActivosSinTiempo = await db.Corredores
                .AnyAsync(
                    c => c.ID_Hit == idHit &&
                         c.Estado == EstadoCorredor.Activo &&
                         !db.Tiempos.Any(t => t.ID_Corredor == c.ID_Corredor),
                    cancellationToken)
                .ConfigureAwait(false);

            if (hayActivosSinTiempo)
            {
                hitDb.Hora_Fin = null;
                await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            }

            return new CierreHitResultado(false);
        }

        private static bool EsFechaCarreraValida(DateTime? fecha)
        {
            return fecha.HasValue &&
                   fecha.Value != default &&
                   fecha.Value.Year >= 1900 &&
                   fecha.Value.Year <= 2100;
        }

        private static OperacionResultado<T> CrearErrorResultado<T>(string operacion, Exception ex)
        {
            if (ex is DbUpdateException dbEx && EsConflictoUnicidadSqlite(dbEx))
            {
                return new OperacionResultado<T>(
                    false,
                    $"Conflicto de unicidad al {operacion}: ya existe un registro duplicado.",
                    StatusCode: 409);
            }

            Trace.TraceError($"[RaceDataService] Error al {operacion}: {ex}");
            return new OperacionResultado<T>(
                false,
                $"Error al {operacion}: {ex.Message}",
                StatusCode: 500);
        }

        private static OperacionResultado CrearErrorResultado(string operacion, Exception ex)
        {
            if (ex is DbUpdateException dbEx && EsConflictoUnicidadSqlite(dbEx))
            {
                return new OperacionResultado(
                    false,
                    $"Conflicto de unicidad al {operacion}: ya existe un registro duplicado.",
                    StatusCode: 409);
            }

            Trace.TraceError($"[RaceDataService] Error al {operacion}: {ex}");
            return new OperacionResultado(
                false,
                $"Error al {operacion}: {ex.Message}",
                StatusCode: 500);
        }

        private static AsignacionResultado? ValidarEntradasBasicas(int idCarrera, int dorsal, string rutaFotoTemp)
        {
            if (idCarrera <= 0)
            {
                return new AsignacionResultado(
                    Exito: false,
                    Mensaje: "El identificador de carrera es inválido.",
                    StatusCode: 400);
            }

            if (dorsal <= 0)
            {
                return new AsignacionResultado(
                    Exito: false,
                    Mensaje: "El dorsal debe ser un número entero positivo mayor a cero.",
                    StatusCode: 400);
            }

            if (string.IsNullOrWhiteSpace(rutaFotoTemp))
            {
                return new AsignacionResultado(
                    Exito: false,
                    Mensaje: "La ruta de la fotografía temporal es obligatoria.",
                    StatusCode: 400);
            }

            return null;
        }

        private static AsignacionResultado? ValidarReglasCorredorYHit(
            Corredor corredor,
            bool yaTieneTiempo,
            int dorsal,
            DateTime horaLlegada)
        {
            if (yaTieneTiempo)
            {
                return new AsignacionResultado(
                    Exito: false,
                    Mensaje: $"El corredor {dorsal} ({corredor.Nombre}) ya tiene un tiempo registrado.",
                    StatusCode: 409);
            }

            if (corredor.Estado != EstadoCorredor.Activo)
            {
                return new AsignacionResultado(
                    Exito: false,
                    Mensaje: $"El corredor {dorsal} ({corredor.Nombre}) tiene estado {corredor.Estado} y no puede registrar tiempo.",
                    StatusCode: 422);
            }

            if (corredor.Hit == null)
            {
                return new AsignacionResultado(
                    Exito: false,
                    Mensaje: $"El corredor {dorsal} ({corredor.Nombre}) no tiene ningún Hit asignado.",
                    StatusCode: 422);
            }

            if (corredor.Hit.Hora_Inicio == null)
            {
                return new AsignacionResultado(
                    Exito: false,
                    Mensaje: $"El Hit '{corredor.Hit.Nombre_Hit}' aún no ha iniciado. No se pueden registrar tiempos.",
                    StatusCode: 422);
            }

            if (corredor.Hit.Hora_Fin != null)
            {
                return new AsignacionResultado(
                    Exito: false,
                    Mensaje: $"El Hit '{corredor.Hit.Nombre_Hit}' ya ha finalizado. No se aceptan más registros.",
                    StatusCode: 422);
            }

            if (horaLlegada < corredor.Hit.Hora_Inicio.Value)
            {
                return new AsignacionResultado(
                    Exito: false,
                    Mensaje: $"No se permiten tiempos netos negativos: la hora de llegada ({horaLlegada:HH:mm:ss}) es anterior al inicio del Hit '{corredor.Hit.Nombre_Hit}' ({corredor.Hit.Hora_Inicio.Value:HH:mm:ss}).",
                    StatusCode: 422);
            }

            return null;
        }

        private static string? ResolverRutaFotoTempExistente(string rutaFotoTemp)
        {
            try
            {
                if (File.Exists(rutaFotoTemp))
                {
                    return Path.GetFullPath(rutaFotoTemp);
                }

                string nombreArchivo = Path.GetFileName(rutaFotoTemp);
                if (!string.IsNullOrWhiteSpace(nombreArchivo) &&
                    nombreArchivo != "." &&
                    nombreArchivo != "..")
                {
                    string alternativa = Path.Combine(StorageHelper.ObtenerCarpetaFotosTemp(), nombreArchivo);
                    if (File.Exists(alternativa))
                    {
                        return Path.GetFullPath(alternativa);
                    }
                }
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException or IOException)
            {
                return null;
            }

            return null;
        }

        private static string FormatearTiempoNeto(TimeSpan tiempoNeto)
        {
            return $"{(int)tiempoNeto.TotalHours:00}:{tiempoNeto.Minutes:00}:{tiempoNeto.Seconds:00}";
        }

        private static SemaphoreSlim ObtenerSemaforoParaContexto(ApplicationDbContext db)
        {
            try
            {
                string? connString = db.Database.GetConnectionString();
                if (string.IsNullOrWhiteSpace(connString))
                {
                    return _semaforoFallback;
                }

                var csb = new SqliteConnectionStringBuilder(connString);
                string dataSource = csb.DataSource?.Trim() ?? string.Empty;
                if (string.IsNullOrWhiteSpace(dataSource) ||
                    dataSource.Equals(":memory:", StringComparison.OrdinalIgnoreCase) ||
                    dataSource.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
                {
                    return _semaforoFallback;
                }

                string clave = Path.GetFullPath(dataSource);
                return _semaforosPorBaseDatos.GetOrAdd(clave, _ => new SemaphoreSlim(1, 1));
            }
            catch
            {
                return _semaforoFallback;
            }
        }

        private static bool EsConflictoUnicidadSqlite(DbUpdateException dbEx)
        {
            if (dbEx.InnerException is not SqliteException sqliteEx)
            {
                return false;
            }

            if (sqliteEx.SqliteExtendedErrorCode is SqliteExtendedConstraintUnique or SqliteExtendedConstraintPrimaryKey)
            {
                return true;
            }

            return sqliteEx.SqliteErrorCode == SqliteErrorConstraint &&
                   sqliteEx.Message.Contains("UNIQUE", StringComparison.OrdinalIgnoreCase);
        }

        private static void IntentarEliminarArchivo(string? ruta)
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
            catch (IOException ex)
            {
                Trace.TraceWarning($"[RaceDataService] No se pudo eliminar archivo '{ruta}': {ex.Message}");
            }
            catch (UnauthorizedAccessException ex)
            {
                Trace.TraceWarning($"[RaceDataService] Sin permisos para eliminar archivo '{ruta}': {ex.Message}");
            }
        }
    }
}
