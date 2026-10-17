using System;

namespace CarreraApp.Models
{
    public class Hit
    {
        public int ID_Hit { get; set; }
        public string Nombre_Hit { get; set; } = string.Empty;
        public DateTime? Hora_Inicio { get; set; } // Puede ser nulo antes de iniciar
        public DateTime? Hora_Fin { get; set; }    // Nulo mientras el hit esté en curso, se fija al llegar el último corredor
        public int ID_Carrera { get; set; }
    }
}