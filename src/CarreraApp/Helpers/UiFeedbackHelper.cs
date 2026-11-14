using System;
using System.Globalization;
using System.Windows;

namespace CarreraApp.Helpers
{
    /// <summary>
    /// Nivel semántico de un aviso visual no bloqueante.
    /// </summary>
    public enum NivelFeedback
    {
        Exito,
        Informacion,
        Advertencia,
        Error
    }

    /// <summary>
    /// Metadatos inmutables para presentar un aviso no bloqueante con contraste WCAG AA.
    /// </summary>
    public sealed record ConfiguracionAvisoFeedback(
        NivelFeedback Nivel,
        string Mensaje,
        int DuracionAutoOcultamientoMs,
        bool RequiereCierreManual,
        string ColorFondoHex,
        string ColorTextoHex,
        string ColorBordeHex);

    /// <summary>
    /// Helper puro y testeable para validación de contraste WCAG AA, duraciones de transición
    /// respetando movimiento reducido y configuración de avisos visuales no bloqueantes.
    /// </summary>
    public static class UiFeedbackHelper
    {
        public const double UmbralTextoNormalWcagAA = 4.5;
        public const double UmbralTextoGrandeOControlWcagAA = 3.0;

        public const int DuracionTransicionMinMs = 150;
        public const int DuracionTransicionMaxMs = 250;
        public const int DuracionTransicionEstandarMs = 180;
        public const int DuracionMovimientoReducidoMs = 0;

        public const int DuracionToastMinMs = 3000;
        public const int DuracionToastMaxMs = 4000;
        public const int DuracionToastDefectoMs = 3500;

        /// <summary>
        /// Tokens hexadecimales de la paleta oficial "Mesa de Control y Pista".
        /// </summary>
        public static class TokensPaleta
        {
            public const string TrackInk = "#0F172A";
            public const string ChalkSurface = "#F8FAFC";
            public const string PureWhite = "#FFFFFF";
            public const string SlateMuted = "#475569";
            public const string SignalCobalt = "#0B57D0";
            public const string FinishEmerald = "#047857";
            public const string PenaltyCrimson = "#B91C1C";
            public const string CautionAmber = "#B45309";
            public const string CautionAmberDarkText = "#78350F";
            public const string CautionAmberSurface = "#FEF3C7";

            // Pizarra de clasificación (LeaderboardWindow)
            public const string LeaderboardBg = "#0B0E14";
            public const string LeaderboardText = "#F8FAFC";
            public const string LeaderboardGold = "#FACC15";
            public const string LeaderboardSilver = "#E2E8F0";
            public const string LeaderboardBronze = "#FDBA74";
        }

        /// <summary>
        /// Calcula la luminancia relativa sRGB (0.0 a 1.0) según WCAG 2.1 para un color hexadecimal (#RRGGBB o #RGB).
        /// </summary>
        public static double CalcularLuminanciaRelativa(string hexColor)
        {
            (byte r, byte g, byte b) = ParsearHexRgb(hexColor);

            double rLin = LinealizarCanalSrgb(r / 255.0);
            double gLin = LinealizarCanalSrgb(g / 255.0);
            double bLin = LinealizarCanalSrgb(b / 255.0);

            return (0.2126 * rLin) + (0.7152 * gLin) + (0.0722 * bLin);
        }

        /// <summary>
        /// Calcula la relación de contraste WCAG 2.1 (1.0 a 21.0) entre dos colores hexadecimales.
        /// </summary>
        public static double CalcularRelacionContraste(string hexPrimerPlano, string hexFondo)
        {
            double l1 = CalcularLuminanciaRelativa(hexPrimerPlano);
            double l2 = CalcularLuminanciaRelativa(hexFondo);

            double masClara = Math.Max(l1, l2);
            double masOscura = Math.Min(l1, l2);

            return (masClara + 0.05) / (masOscura + 0.05);
        }

        /// <summary>
        /// Verifica si el par de colores cumple el umbral mínimo WCAG AA
        /// (>= 4.5:1 para texto normal, >= 3.0:1 para texto grande, íconos y bordes de foco).
        /// </summary>
        public static bool CumpleContrasteWcagAA(
            string hexPrimerPlano,
            string hexFondo,
            bool esTextoGrandeOElementoGrafico = false)
        {
            double umbral = esTextoGrandeOElementoGrafico
                ? UmbralTextoGrandeOControlWcagAA
                : UmbralTextoNormalWcagAA;

            return CalcularRelacionContraste(hexPrimerPlano, hexFondo) >= umbral;
        }

        /// <summary>
        /// Consulta la preferencia de animaciones de interfaz en Windows sin depender de Application.Current.
        /// </summary>
        public static bool AnimacionesHabilitadasEnSistema()
        {
            try
            {
                return SystemParameters.ClientAreaAnimation;
            }
            catch
            {
                return true;
            }
        }

        /// <summary>
        /// Devuelve la duración de transición en milisegundos: 0 ms si el movimiento reducido está activo,
        /// o un valor acotado a [150, 250] ms en modo normal.
        /// </summary>
        public static int ObtenerDuracionTransicionMs(
            bool? animacionesHabilitadas = null,
            int duracionBaseMs = DuracionTransicionEstandarMs)
        {
            bool habilitadas = animacionesHabilitadas ?? AnimacionesHabilitadasEnSistema();
            if (!habilitadas)
            {
                return DuracionMovimientoReducidoMs;
            }

            return Math.Clamp(duracionBaseMs, DuracionTransicionMinMs, DuracionTransicionMaxMs);
        }

        /// <summary>
        /// Devuelve la duración de transición como <see cref="TimeSpan"/> respetando movimiento reducido.
        /// </summary>
        public static TimeSpan ObtenerDuracionTransicion(
            bool? animacionesHabilitadas = null,
            int duracionBaseMs = DuracionTransicionEstandarMs)
        {
            return TimeSpan.FromMilliseconds(ObtenerDuracionTransicionMs(animacionesHabilitadas, duracionBaseMs));
        }

        /// <summary>
        /// Construye la configuración visual y temporal de un aviso no bloqueante según su severidad.
        /// </summary>
        public static ConfiguracionAvisoFeedback CrearConfiguracionAviso(
            NivelFeedback nivel,
            string mensaje,
            int? duracionPersonalizadaMs = null)
        {
            if (string.IsNullOrWhiteSpace(mensaje))
            {
                throw new ArgumentException("El mensaje del aviso no puede estar vacío.", nameof(mensaje));
            }

            string mensajeLimpio = mensaje.Trim();

            (string fondo, string texto, string borde) = nivel switch
            {
                NivelFeedback.Exito => (TokensPaleta.FinishEmerald, TokensPaleta.PureWhite, TokensPaleta.FinishEmerald),
                NivelFeedback.Informacion => (TokensPaleta.SignalCobalt, TokensPaleta.PureWhite, TokensPaleta.SignalCobalt),
                NivelFeedback.Advertencia => (TokensPaleta.CautionAmberSurface, TokensPaleta.CautionAmberDarkText, TokensPaleta.CautionAmber),
                NivelFeedback.Error => (TokensPaleta.PenaltyCrimson, TokensPaleta.PureWhite, TokensPaleta.PenaltyCrimson),
                _ => throw new ArgumentOutOfRangeException(nameof(nivel), nivel, "Nivel de feedback no soportado.")
            };

            if (nivel == NivelFeedback.Error && (duracionPersonalizadaMs is null || duracionPersonalizadaMs.Value <= 0))
            {
                return new ConfiguracionAvisoFeedback(
                    nivel,
                    mensajeLimpio,
                    DuracionAutoOcultamientoMs: 0,
                    RequiereCierreManual: true,
                    ColorFondoHex: fondo,
                    ColorTextoHex: texto,
                    ColorBordeHex: borde);
            }

            int duracionMs = Math.Clamp(
                duracionPersonalizadaMs ?? DuracionToastDefectoMs,
                DuracionToastMinMs,
                DuracionToastMaxMs);

            return new ConfiguracionAvisoFeedback(
                nivel,
                mensajeLimpio,
                DuracionAutoOcultamientoMs: duracionMs,
                RequiereCierreManual: false,
                ColorFondoHex: fondo,
                ColorTextoHex: texto,
                ColorBordeHex: borde);
        }

        private static double LinealizarCanalSrgb(double canalNormalizado)
        {
            return canalNormalizado <= 0.04045
                ? canalNormalizado / 12.92
                : Math.Pow((canalNormalizado + 0.055) / 1.055, 2.4);
        }

        private static (byte R, byte G, byte B) ParsearHexRgb(string hexColor)
        {
            if (string.IsNullOrWhiteSpace(hexColor))
            {
                throw new ArgumentException("El color hexadecimal no puede ser nulo ni vacío.", nameof(hexColor));
            }

            ReadOnlySpan<char> span = hexColor.AsSpan().Trim();
            if (span.StartsWith("#", StringComparison.Ordinal))
            {
                span = span[1..];
            }

            if (span.Length == 3)
            {
                byte r = ParsearNibbleDuplicado(span[0], hexColor);
                byte g = ParsearNibbleDuplicado(span[1], hexColor);
                byte b = ParsearNibbleDuplicado(span[2], hexColor);
                return (r, g, b);
            }

            if (span.Length == 6)
            {
                if (!byte.TryParse(span[..2], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out byte r) ||
                    !byte.TryParse(span.Slice(2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out byte g) ||
                    !byte.TryParse(span.Slice(4, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out byte b))
                {
                    throw new ArgumentException($"Formato hexadecimal inválido: '{hexColor}'.", nameof(hexColor));
                }

                return (r, g, b);
            }

            throw new ArgumentException($"El color '{hexColor}' debe tener formato #RRGGBB o #RGB.", nameof(hexColor));
        }

        private static byte ParsearNibbleDuplicado(char c, string hexOriginal)
        {
            if (!byte.TryParse(stackalloc char[] { c, c }, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out byte valor))
            {
                throw new ArgumentException($"Formato hexadecimal inválido: '{hexOriginal}'.", nameof(hexOriginal));
            }

            return valor;
        }
    }
}
