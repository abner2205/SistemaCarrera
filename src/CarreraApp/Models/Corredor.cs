// Path: src/CarreraApp/Models/Corredor.cs
namespace CarreraApp.Models
{
    public enum EstadoCorredor
    {
        Activo,
        DNS,
        DNF
    }

    public class Corredor
    {
        public int ID_Corredor { get; set; }
        public int Dorsal { get; set; }
        public string Nombre { get; set; } = string.Empty;
        public int ID_Categoria { get; set; }
        public int ID_Hit { get; set; }
        public EstadoCorredor Estado { get; set; } = EstadoCorredor.Activo;
        public int ID_Carrera { get; set; }

        // Propiedades de navegación
        public virtual Categoria? Categoria { get; set; }
        public virtual Hit? Hit { get; set; }
        public virtual Carrera? Carrera { get; set; }
    }
}