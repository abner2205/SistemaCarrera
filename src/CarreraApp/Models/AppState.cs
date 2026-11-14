using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Media.Imaging;
using CarreraApp.Server;

namespace CarreraApp.Models
{
    // Se muda la clase aquí para que sea accesible desde toda la aplicación
    public class FotoPendiente : INotifyPropertyChanged
    {
        private BitmapSource? _miniatura;

        public event PropertyChangedEventHandler? PropertyChanged;

        public string IdFoto { get; set; } = string.Empty;
        public DateTime Hora_Llegada { get; set; }
        public string Ruta_Foto { get; set; } = string.Empty;

        /// <summary>
        /// Miniatura pre-decodificada a escala reducida y congelada para el listado de espera.
        /// </summary>
        public BitmapSource? Miniatura
        {
            get => _miniatura;
            set
            {
                if (!ReferenceEquals(_miniatura, value))
                {
                    _miniatura = value;
                    OnPropertyChanged();
                }
            }
        }

        private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }

    public static class AppState
    {
        public const int MaxRegistrosExitosos = 200;

        public static Carrera? CarreraActiva { get; set; }
        public static Categoria? CategoriaPizarraActiva { get; set; }

        // Colecciones globales que no se borran al cambiar de pestaña
        public static ObservableCollection<FotoPendiente> FotosEnEsperaGlobal { get; set; } = new ObservableCollection<FotoPendiente>();
        public static ObservableCollection<string> RegistrosExitososGlobal { get; set; } = new ObservableCollectionAcotada(MaxRegistrosExitosos);

        public static event Action? NuevoTiempoRegistrado;
        public static void NotificarNuevoTiempo() => NuevoTiempoRegistrado?.Invoke();

        public static event Action? CategoriaPizarraCambiada;
        public static void NotificarCambioCategoriaPizarra() => CategoriaPizarraCambiada?.Invoke();

        public static event Action? CarreraActivaCambiada;

        /// <summary>
        /// Cambia la carrera activa de forma coordinada: limpia la categoría de pizarra,
        /// vacía la cola local de fotos en espera, purga la cola en memoria + archivos temporales
        /// del servidor web y notifica a los suscriptores.
        /// </summary>
        public static void CambiarCarreraActiva(Carrera? carrera)
        {
            CarreraActiva = carrera;
            CategoriaPizarraActiva = null;
            FotosEnEsperaGlobal.Clear();
            WebServer.LimpiarColaPendientes();
            CarreraActivaCambiada?.Invoke();
        }

        /// <summary>
        /// Agrega una entrada al principio del log manteniendo el acotamiento máximo de registros en memoria.
        /// </summary>
        public static void AgregarRegistroExitoso(string mensaje)
        {
            if (string.IsNullOrWhiteSpace(mensaje))
            {
                return;
            }

            RegistrosExitososGlobal.Insert(0, mensaje);
        }

        private sealed class ObservableCollectionAcotada : ObservableCollection<string>
        {
            private readonly int _capacidadMaxima;

            public ObservableCollectionAcotada(int capacidadMaxima)
            {
                _capacidadMaxima = Math.Max(1, capacidadMaxima);
            }

            protected override void InsertItem(int index, string item)
            {
                base.InsertItem(index, item);

                while (Count > _capacidadMaxima)
                {
                    int indiceAEliminar = index < Count - 1 ? Count - 1 : 0;
                    RemoveAt(indiceAEliminar);
                }
            }
        }
    }
}