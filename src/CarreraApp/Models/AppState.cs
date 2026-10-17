using System;
using System.Collections.ObjectModel;

namespace CarreraApp.Models
{
    // Se muda la clase aquí para que sea accesible desde toda la aplicación
    public class FotoPendiente
    {
        public string IdFoto { get; set; } = string.Empty;
        public DateTime Hora_Llegada { get; set; }
        public string Ruta_Foto { get; set; } = string.Empty;
    }

    public static class AppState
    {
        public static Carrera? CarreraActiva { get; set; }
        public static Categoria? CategoriaPizarraActiva { get; set; }
        
        // Colecciones globales que no se borran al cambiar de pestaña
        public static ObservableCollection<FotoPendiente> FotosEnEsperaGlobal { get; set; } = new ObservableCollection<FotoPendiente>();
        public static ObservableCollection<string> RegistrosExitososGlobal { get; set; } = new ObservableCollection<string>();
        
        public static event Action? NuevoTiempoRegistrado;
        public static void NotificarNuevoTiempo() => NuevoTiempoRegistrado?.Invoke();

        public static event Action? CategoriaPizarraCambiada;
        public static void NotificarCambioCategoriaPizarra() => CategoriaPizarraCambiada?.Invoke();
    }
}