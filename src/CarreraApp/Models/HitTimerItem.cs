using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace CarreraApp.Models
{
    public class HitTimerItem : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler? PropertyChanged;

        private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }

        public int ID_Hit { get; set; }
        public string Nombre_Hit { get; set; } = string.Empty;
        public DateTime? Hora_Inicio { get; set; }
        public DateTime? Hora_Fin { get; set; }

        private int _totalCorredores;
        public int TotalCorredores
        {
            get => _totalCorredores;
            set
            {
                if (_totalCorredores != value)
                {
                    _totalCorredores = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(InfoCorredores));
                }
            }
        }

        private int _corredoresLlegaron;
        public int CorredoresLlegaron
        {
            get => _corredoresLlegaron;
            set
            {
                if (_corredoresLlegaron != value)
                {
                    _corredoresLlegaron = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(InfoCorredores));
                }
            }
        }

        private string _cronometroTexto = "00:00:00";
        public string CronometroTexto
        {
            get => _cronometroTexto;
            set
            {
                if (_cronometroTexto != value)
                {
                    _cronometroTexto = value;
                    OnPropertyChanged();
                }
            }
        }

        public bool EstaIniciado => Hora_Inicio.HasValue;
        public bool EstaFinalizado => Hora_Fin.HasValue;
        public bool EstaActivo => EstaIniciado && !EstaFinalizado;
        public bool PuedeIniciar => !EstaIniciado;

        public string EstadoTexto
        {
            get
            {
                if (EstaFinalizado) return "FINALIZADO";
                if (EstaActivo) return "EN CURSO";
                return "PENDIENTE";
            }
        }

        public string InfoInicio
        {
            get
            {
                if (EstaFinalizado && Hora_Inicio.HasValue && Hora_Fin.HasValue)
                    return $"Inicio: {Hora_Inicio:HH:mm:ss} | Fin: {Hora_Fin:HH:mm:ss}";
                if (EstaIniciado && Hora_Inicio.HasValue)
                    return $"Inicio: {Hora_Inicio:HH:mm:ss}";
                return "Salida no iniciada";
            }
        }

        public string InfoCorredores => $"Llegadas: {CorredoresLlegaron} / {TotalCorredores} corredores";

        public string ColorBadge
        {
            get
            {
                if (EstaFinalizado) return "#6C757D"; // Gris
                if (EstaActivo) return "#28A745";     // Verde
                return "#0078D7";                     // Azul
            }
        }

        public string ColorCronometro
        {
            get
            {
                if (EstaFinalizado) return "#212529"; // Oscuro fijo
                if (EstaActivo) return "#28A745";     // Verde vivo
                return "#888888";                     // Gris atenuado
            }
        }

        public string BordeColor
        {
            get
            {
                if (EstaFinalizado) return "#CED4DA";
                if (EstaActivo) return "#28A745";
                return "#E0E0E0";
            }
        }

        public string ColorBoton
        {
            get
            {
                if (EstaFinalizado) return "#6C757D";
                if (EstaActivo) return "#17A2B8";
                return "#0078D7";
            }
        }

        public string TextoBoton
        {
            get
            {
                if (EstaFinalizado) return "COMPLETADO";
                if (EstaActivo) return "EN COMPETENCIA";
                return "INICIAR HIT";
            }
        }

        public void NotificarCambioEstado()
        {
            OnPropertyChanged(nameof(EstaIniciado));
            OnPropertyChanged(nameof(EstaFinalizado));
            OnPropertyChanged(nameof(EstaActivo));
            OnPropertyChanged(nameof(PuedeIniciar));
            OnPropertyChanged(nameof(EstadoTexto));
            OnPropertyChanged(nameof(InfoInicio));
            OnPropertyChanged(nameof(InfoCorredores));
            OnPropertyChanged(nameof(ColorBadge));
            OnPropertyChanged(nameof(ColorCronometro));
            OnPropertyChanged(nameof(BordeColor));
            OnPropertyChanged(nameof(ColorBoton));
            OnPropertyChanged(nameof(TextoBoton));
            ActualizarCronometro();
        }

        public void ActualizarCronometro()
        {
            if (EstaFinalizado && Hora_Inicio.HasValue && Hora_Fin.HasValue)
            {
                TimeSpan total = Hora_Fin.Value - Hora_Inicio.Value;
                CronometroTexto = $"{(int)total.TotalHours:00}:{total.Minutes:00}:{total.Seconds:00}";
            }
            else if (EstaActivo && Hora_Inicio.HasValue)
            {
                TimeSpan actual = DateTime.Now - Hora_Inicio.Value;
                CronometroTexto = $"{(int)actual.TotalHours:00}:{actual.Minutes:00}:{actual.Seconds:00}";
            }
            else
            {
                CronometroTexto = "00:00:00";
            }
        }
    }
}

