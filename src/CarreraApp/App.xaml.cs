using System.Windows;
using CarreraApp.Data;
using CarreraApp.Server;
using System.Threading.Tasks;

namespace CarreraApp
{
    public partial class App : Application
    {
        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            // 1. Inicializar base de datos y migrar nuevas columnas si la BD ya existía
            using (var context = new ApplicationDbContext())
            {
                context.Database.EnsureCreated();
                try
                {
                    Microsoft.EntityFrameworkCore.RelationalDatabaseFacadeExtensions.ExecuteSqlRaw(
                        context.Database, "ALTER TABLE Hits ADD COLUMN Hora_Fin TEXT NULL;");
                }
                catch { }

                try
                {
                    Microsoft.EntityFrameworkCore.RelationalDatabaseFacadeExtensions.ExecuteSqlRaw(
                        context.Database, "ALTER TABLE Carreras ADD COLUMN RutaLogoIzquierdo TEXT NULL;");
                }
                catch { }

                try
                {
                    Microsoft.EntityFrameworkCore.RelationalDatabaseFacadeExtensions.ExecuteSqlRaw(
                        context.Database, "ALTER TABLE Carreras ADD COLUMN RutaLogoDerecho TEXT NULL;");
                }
                catch { }
            }

            // 2. Levantar el servidor web en un hilo secundario para no congelar la UI
            Task.Run(() => WebServer.IniciarServidorAsync());
        }
    }
}