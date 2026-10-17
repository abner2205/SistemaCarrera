namespace CarreraApp.Models
{
    public class Categoria
    {
        public int ID_Categoria { get; set; }
        public string Nombre { get; set; } = string.Empty;
        public int ID_Carrera { get; set; }
    }
}