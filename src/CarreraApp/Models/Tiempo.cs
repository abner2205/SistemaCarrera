// Path: src/CarreraApp/Models/Tiempo.cs
using System;

namespace CarreraApp.Models
{
    public class Tiempo
    {
        public int ID_Tiempo { get; set; }
        public int ID_Corredor { get; set; }
        public DateTime Hora_Llegada { get; set; }
        public TimeSpan Tiempo_Neto { get; set; }
        public string Ruta_Foto { get; set; } = string.Empty;

        // Propiedad de navegación
        public virtual Corredor? Corredor { get; set; }
    }
}