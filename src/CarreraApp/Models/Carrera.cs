using System;

namespace CarreraApp.Models
{
    public class Carrera
    {
        public int ID_Carrera { get; set; }
        public string Nombre { get; set; } = string.Empty;
        public DateTime Fecha { get; set; } = DateTime.Now;
        public string? RutaLogoIzquierdo { get; set; }
        public string? RutaLogoDerecho { get; set; }
    }
}