using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;
using System;
using System.Collections.Concurrent;
using System.IO;
using System.Threading.Tasks;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.Hosting;
using System.Net;
using System.Net.Sockets;

namespace CarreraApp.Server
{
    // Clase auxiliar para recibir el JSON
    public class AsignacionDorsalRequest
    {
        public string IdFoto { get; set; } = string.Empty;
        public int Dorsal { get; set; }
    }
    public class DescarteFotoRequest
    {
        public string IdFoto { get; set; } = string.Empty;
    }
    public static class WebServer
    {
        // 1. Declarar el evento para notificar a la interfaz WPF
        public static event Action<string, DateTime, string>? NuevaFotoRecibida;
        public static event Action<string, int>? DorsalAsignadoRemoto;
        // Diccionario seguro para hilos que guarda la hora exacta en que entró la foto
        private static readonly ConcurrentDictionary<string, DateTime> TiemposPendientes = new();
        public static event Action<string>? FotoDescartadaRemota;

        public static async Task IniciarServidorAsync()
        {
            try
            {
                string webRoot = Directory.Exists(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "wwwroot"))
                    ? Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "wwwroot")
                    : Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", "wwwroot"));

                var builder = WebApplication.CreateBuilder(new WebApplicationOptions
                {
                    Args = Array.Empty<string>(),
                    WebRootPath = Directory.Exists(webRoot) ? webRoot : null
                });

                // Configurar el servidor para HTTP (5000) y HTTPS (5001) autofirmado
                builder.WebHost.ConfigureKestrel(options =>
                {
                    // Puerto HTTP tradicional (sigue activo en el 5000 como respaldo)
                    options.ListenAnyIP(5000);

                    // Puerto HTTPS seguro (5001)
                    options.ListenAnyIP(5001, listenOptions =>
                    {
                        using var rsa = RSA.Create(2048);
                        var req = new CertificateRequest("cn=CarreraAppLocal", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
                        
                        // 1. NUEVO: Indicar explícitamente a Windows que este certificado es para un servidor web
                        req.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection { new Oid("1.3.6.1.5.5.7.3.1") }, false));
                        req.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, false));

                        // 2. Construir el SAN con las IPs locales (como ya lo teníamos)
                        var sanBuilder = new SubjectAlternativeNameBuilder();
                        sanBuilder.AddIpAddress(IPAddress.Parse("127.0.0.1"));
                        
                        var host = Dns.GetHostEntry(Dns.GetHostName());
                        foreach (var ip in host.AddressList)
                        {
                            if (ip.AddressFamily == AddressFamily.InterNetwork)
                            {
                                sanBuilder.AddIpAddress(ip);
                            }
                        }
                        req.CertificateExtensions.Add(sanBuilder.Build());

                        // 3. Crear certificado temporal
                        using var certTemp = req.CreateSelfSigned(DateTimeOffset.Now, DateTimeOffset.Now.AddYears(1));
                        
                        // 4. Empaquetarlo en bytes
                        byte[] pfxBytes = certTemp.Export(X509ContentType.Pfx, "password123");

                        // 5. NUEVO: Cargar el certificado usando Exportable. 
                        // Quitamos el EphemeralKeySet para que Schannel (Windows) pueda acceder a la llave y negociar el TLS.
                        var certFinal = X509CertificateLoader.LoadPkcs12(pfxBytes, "password123", X509KeyStorageFlags.Exportable);

                        listenOptions.UseHttps(certFinal);
                    });
                });
                var app = builder.Build();
                // Configurar directorios de almacenamiento unificado
                string tempFolder = Helpers.StorageHelper.ObtenerCarpetaFotosTemp();

                // Permitir servir la carpeta wwwroot (donde estará el HTML de los jueces)
                app.UseStaticFiles(); 

                // ENDPOINT 1: Recibir foto y registrar tiempo
                app.MapPost("/api/upload-photo", async (HttpRequest request) =>
                {
                    if (!request.HasFormContentType) return Results.BadRequest();

                    var form = await request.ReadFormAsync();
                    var file = form.Files.GetFile("foto");
                    var tempId = form["tempId"].ToString();

                    if (file == null || string.IsNullOrEmpty(tempId)) return Results.BadRequest();

                    // Registrar la hora exacta de llegada (El reloj maestro del servidor)
                    DateTime horaLlegada = DateTime.Now;
                    
                    // Generar un ID único real en el servidor
                    string serverId = Guid.NewGuid().ToString();
                    
                    // Guardar la hora asociada a ese ID
                    TiemposPendientes.TryAdd(serverId, horaLlegada);

                    // Guardar archivo físicamente
                    string filePath = Path.Combine(tempFolder, $"{serverId}.jpg");
                    using (var stream = new FileStream(filePath, FileMode.Create))
                    {
                        await file.CopyToAsync(stream);
                    }

                    // 2. Disparar el evento notificando a WPF
                    NuevaFotoRecibida?.Invoke(serverId, horaLlegada, filePath);

                    // Devolver el ID real al teléfono
                    return Results.Ok(new { idFoto = serverId });
                });

                app.MapGet("/api/pending-photos", () =>
                {
                    // Devuelve solo los IDs que siguen en memoria
                    return Results.Ok(TiemposPendientes.Keys);
                });
                // ENDPOINT 2: Asignar dorsal y procesar tiempo
                app.MapPost("/api/assign-dorsal", (AsignacionDorsalRequest data) =>
                {
                    if (string.IsNullOrEmpty(data.IdFoto) || data.Dorsal <= 0)
                        return Results.BadRequest("Datos inválidos");

                    if (TiemposPendientes.TryGetValue(data.IdFoto, out DateTime horaLlegada))
                    {
                        // Solo envía la orden a la PC, ya no elimina la foto de la memoria
                        DorsalAsignadoRemoto?.Invoke(data.IdFoto, data.Dorsal);
                        return Results.Ok(new { mensaje = "Orden enviada" });
                    }

                    return Results.NotFound("Foto no encontrada o ya procesada");
                });
                
                app.MapPost("/api/discard-photo", (DescarteFotoRequest data) =>
                {
                    if (string.IsNullOrEmpty(data.IdFoto)) return Results.BadRequest();

                    if (TiemposPendientes.ContainsKey(data.IdFoto))
                    {
                        // Avisar a la interfaz de PC
                        FotoDescartadaRemota?.Invoke(data.IdFoto);
                        return Results.Ok();
                    }
                    return Results.NotFound();
                });
                // Escuchar en todas las interfaces de red local en el puerto 5000
                app.Urls.Add("http://0.0.0.0:5000");

                // Iniciar servidor de forma asíncrona
                await app.RunAsync();
            }
            catch (Exception ex)
            {
                // 2. Si algo falla ahora, te mostrará exactamente qué fue en una ventana emergente
                System.Windows.Application.Current.Dispatcher.Invoke(() =>
                {
                    System.Windows.MessageBox.Show(
                        $"El servidor interno colapsó y no pudo iniciar.\n\nMotivo:\n{ex.Message}", 
                        "Error Crítico de Red", 
                        System.Windows.MessageBoxButton.OK, 
                        System.Windows.MessageBoxImage.Error);
                });
            }
        }
        public static void EliminarFotoPendiente(string idFoto)
        {
            TiemposPendientes.TryRemove(idFoto, out _);
        }
    }
}