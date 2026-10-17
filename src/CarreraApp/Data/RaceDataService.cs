using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using CarreraApp.Models;

namespace CarreraApp.Data
{
    public record AsignacionResultado(bool Exito, string Mensaje, string? TiempoFormateado = null, bool HitFinalizado = false, string? NombreHitFinalizado = null);

    public class RaceDataService
    {
        // Semáforo para serializar operaciones críticas de asignación y evitar condiciones de carrera
        private static readonly SemaphoreSlim _lockAsignacion = new(1, 1);

        public async Task<AsignacionResultado> AsignarTiempoAsync(int idCarrera, string nombreCarrera, int dorsal, DateTime horaLlegada, string rutaFotoTemp)
        {
            await _lockAsignacion.WaitAsync();
            try
            {
                using var db = new ApplicationDbContext();

                // 1. Obtener corredor, hit y categoría en una sola consulta relacional
                var corredor = await db.Corredores
                    .Include(c => c.Hit)
                    .Include(c => c.Categoria)
                    .FirstOrDefaultAsync(c => c.Dorsal == dorsal && c.ID_Carrera == idCarrera);

                if (corredor == null)
                    return new AsignacionResultado(false, $"Dorsal {dorsal} no registrado en esta carrera.");

                // 2. Validar que el Hit esté actualmente activo (iniciado y no finalizado)
                if (corredor.Hit == null)
                    return new AsignacionResultado(false, $"El corredor {dorsal} ({corredor.Nombre}) no tiene ningún Hit asignado.");

                if (corredor.Hit.Hora_Inicio == null)
                    return new AsignacionResultado(false, $"El Hit '{corredor.Hit.Nombre_Hit}' aún no ha iniciado. No se pueden registrar tiempos.");

                if (corredor.Hit.Hora_Fin != null)
                    return new AsignacionResultado(false, $"El Hit '{corredor.Hit.Nombre_Hit}' ya ha finalizado. No se aceptan más registros.");

                // 3. Validar que no tenga tiempo previo registrado
                bool yaTieneTiempo = await db.Tiempos.AnyAsync(t => t.ID_Corredor == corredor.ID_Corredor);
                if (yaTieneTiempo)
                    return new AsignacionResultado(false, $"El corredor {dorsal} ({corredor.Nombre}) ya tiene un tiempo registrado.");

                // 4. Cálculo de tiempos
                TimeSpan tiempoNeto = horaLlegada - corredor.Hit.Hora_Inicio.Value;
                string tiempoFormateado = $"{(int)tiempoNeto.TotalHours:00}:{tiempoNeto.Minutes:00}:{tiempoNeto.Seconds:00}";
                string tiempoParaArchivo = tiempoFormateado.Replace(":", "-");

                // 5. Sanitización segura de rutas para el sistema de archivos
                char[] invalidChars = Path.GetInvalidFileNameChars();
                string safeCarrera = string.Join("_", nombreCarrera.Split(invalidChars)).Replace(" ", "_");
                string safeCategoria = string.Join("_", (corredor.Categoria?.Nombre ?? "General").Split(invalidChars)).Replace(" ", "_");
                string safeCorredor = string.Join("_", corredor.Nombre.Split(invalidChars)).Replace(" ", "_");

                string dirDefinitivo = Path.Combine(Helpers.StorageHelper.ObtenerCarpetaFotosDefinitivas(), safeCarrera, safeCategoria);
                Directory.CreateDirectory(dirDefinitivo);

                string nombreArchivo = $"{safeCorredor}_{corredor.Dorsal}_{tiempoParaArchivo}.jpg";
                string rutaDefinitiva = Path.Combine(dirDefinitivo, nombreArchivo);

                // 6. Transferencia de archivo segura con búsqueda de respaldo en Fotos_Temp
                string fotoOrigen = rutaFotoTemp;
                if (!File.Exists(fotoOrigen))
                {
                    string alternativa = Path.Combine(Helpers.StorageHelper.ObtenerCarpetaFotosTemp(), Path.GetFileName(rutaFotoTemp));
                    if (File.Exists(alternativa))
                    {
                        fotoOrigen = alternativa;
                    }
                }

                if (File.Exists(fotoOrigen))
                {
                    File.Copy(fotoOrigen, rutaDefinitiva, overwrite: true);
                    try { File.Delete(fotoOrigen); } catch { /* Se tolera bloqueo transitorio */ }
                }

                // 7. Inserción atómica en base de datos
                db.Tiempos.Add(new Tiempo
                {
                    ID_Corredor = corredor.ID_Corredor,
                    Hora_Llegada = horaLlegada,
                    Tiempo_Neto = tiempoNeto,
                    Ruta_Foto = rutaDefinitiva
                });

                await db.SaveChangesAsync();

                // 8. Evaluar si este era el último corredor activo del hit para finalizarlo automáticamente
                int idHit = corredor.ID_Hit;
                int totalCorredoresHit = await db.Corredores
                    .CountAsync(c => c.ID_Hit == idHit && c.Estado == EstadoCorredor.Activo);

                int tiemposRegistradosHit = await db.Tiempos
                    .CountAsync(t => db.Corredores.Any(c => c.ID_Hit == idHit && c.ID_Corredor == t.ID_Corredor));

                bool hitFinalizado = false;
                string? nombreHitFinalizado = null;

                if (totalCorredoresHit > 0 && tiemposRegistradosHit >= totalCorredoresHit)
                {
                    var hitDb = await db.Hits.FindAsync(idHit);
                    if (hitDb != null && hitDb.Hora_Fin == null)
                    {
                        hitDb.Hora_Fin = horaLlegada;
                        await db.SaveChangesAsync();
                        hitFinalizado = true;
                        nombreHitFinalizado = hitDb.Nombre_Hit;
                    }
                }

                return new AsignacionResultado(
                    true, 
                    $"Corredor {dorsal} ({corredor.Nombre}) registrado con éxito.", 
                    tiempoFormateado, 
                    hitFinalizado, 
                    nombreHitFinalizado
                );
            }
            catch (DbUpdateException)
            {
                return new AsignacionResultado(false, $"Conflicto en base de datos: El tiempo para el corredor {dorsal} ya fue procesado concurrentemente.");
            }
            catch (Exception ex)
            {
                return new AsignacionResultado(false, $"Error interno al registrar tiempo: {ex.Message}");
            }
            finally
            {
                _lockAsignacion.Release();
            }
        }

        // Consultas optimizadas de Solo Lectura con AsNoTracking()
        public async Task<List<Carrera>> ObtenerCarrerasAsync()
        {
            using var db = new ApplicationDbContext();
            return await db.Carreras.AsNoTracking().OrderByDescending(c => c.Fecha).ToListAsync();
        }

        public async Task<List<Hit>> ObtenerHitsPorCarreraAsync(int idCarrera)
        {
            using var db = new ApplicationDbContext();
            return await db.Hits.AsNoTracking().Where(h => h.ID_Carrera == idCarrera).ToListAsync();
        }

        public async Task<List<Categoria>> ObtenerCategoriasPorCarreraAsync(int idCarrera)
        {
            using var db = new ApplicationDbContext();
            return await db.Categorias.AsNoTracking().Where(c => c.ID_Carrera == idCarrera).ToListAsync();
        }
    }
}