using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using Microsoft.EntityFrameworkCore;
using CarreraApp.Data;
using CarreraApp.Models;

namespace CarreraApp.UI.Views
{
    // DTO para la visualización enriquecida de corredores con su tiempo registrado
    public class CorredorFilaView
    {
        public int ID_Corredor { get; set; }
        public int Dorsal { get; set; }
        public string Nombre { get; set; } = string.Empty;
        public int ID_Categoria { get; set; }
        public string NombreCategoria { get; set; } = string.Empty;
        public int ID_Hit { get; set; }
        public string NombreHit { get; set; } = string.Empty;
        public EstadoCorredor Estado { get; set; }
        public TimeSpan? TiempoNeto { get; set; }
        public DateTime? HoraLlegada { get; set; }
        public string TiempoFormateado => TiempoNeto.HasValue 
            ? $"{(int)TiempoNeto.Value.TotalHours:00}:{TiempoNeto.Value.Minutes:00}:{TiempoNeto.Value.Seconds:00}" 
            : "--:--:--";
    }

    public partial class CorredoresView : UserControl
    {
        private int? _idCorredorEnEdicion = null;

        public CorredoresView()
        {
            InitializeComponent();
            CargarDatosIniciales();
        }

        private async void CargarDatosIniciales()
        {
            if (AppState.CarreraActiva == null) return;
            int idCarrera = AppState.CarreraActiva.ID_Carrera;

            try
            {
                using var db = new ApplicationDbContext();

                ComboCategoria.ItemsSource = await db.Categorias.AsNoTracking()
                    .Where(c => c.ID_Carrera == idCarrera)
                    .ToListAsync();

                ComboHit.ItemsSource = await db.Hits.AsNoTracking()
                    .Where(h => h.ID_Carrera == idCarrera)
                    .ToListAsync();

                // Consulta consolidada con Categoría, Hit y Tiempo Neto registrado
                var lista = await (from c in db.Corredores.AsNoTracking()
                                   where c.ID_Carrera == idCarrera
                                   join cat in db.Categorias.AsNoTracking() on c.ID_Categoria equals cat.ID_Categoria into catJoin
                                   from cat in catJoin.DefaultIfEmpty()
                                   join hit in db.Hits.AsNoTracking() on c.ID_Hit equals hit.ID_Hit into hitJoin
                                   from hit in hitJoin.DefaultIfEmpty()
                                   join t in db.Tiempos.AsNoTracking() on c.ID_Corredor equals t.ID_Corredor into tJoin
                                   from t in tJoin.DefaultIfEmpty()
                                   orderby c.Dorsal
                                   select new CorredorFilaView
                                   {
                                       ID_Corredor = c.ID_Corredor,
                                       Dorsal = c.Dorsal,
                                       Nombre = c.Nombre,
                                       ID_Categoria = c.ID_Categoria,
                                       NombreCategoria = cat != null ? cat.Nombre : "Sin categoría",
                                       ID_Hit = c.ID_Hit,
                                       NombreHit = hit != null ? hit.Nombre_Hit : "Sin hit",
                                       Estado = c.Estado,
                                       TiempoNeto = t != null ? (TimeSpan?)t.Tiempo_Neto : null,
                                       HoraLlegada = t != null ? (DateTime?)t.Hora_Llegada : null
                                   }).ToListAsync();

                TablaCorredores.ItemsSource = lista;
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error al cargar corredores: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private async void BtnGuardarCorredor_Click(object sender, RoutedEventArgs e)
        {
            if (AppState.CarreraActiva == null) return;
            
            if (!int.TryParse(InputDorsal.Text.Trim(), out int dorsalNum) || 
                string.IsNullOrEmpty(InputNombre.Text.Trim()) ||
                ComboCategoria.SelectedValue == null || 
                ComboHit.SelectedValue == null)
            {
                MessageBox.Show("Verifica que todos los campos del corredor estén llenos correctamente.", "Error", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            int idCarrera = AppState.CarreraActiva.ID_Carrera;

            try
            {
                using var db = new ApplicationDbContext();

                // Validación estricta: Prevenir dorsales duplicados excluyendo al que estamos editando
                bool dorsalExiste = await db.Corredores.AnyAsync(c => 
                    c.Dorsal == dorsalNum && 
                    c.ID_Carrera == idCarrera && 
                    (!_idCorredorEnEdicion.HasValue || c.ID_Corredor != _idCorredorEnEdicion.Value));

                if (dorsalExiste)
                {
                    MessageBox.Show("Ese número de dorsal ya está registrado en esta carrera.", "Dorsal Duplicado", MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }

                if (!_idCorredorEnEdicion.HasValue)
                {
                    // Crear nuevo corredor
                    db.Corredores.Add(new Corredor
                    {
                        Dorsal = dorsalNum,
                        Nombre = InputNombre.Text.Trim(),
                        ID_Categoria = (int)ComboCategoria.SelectedValue,
                        ID_Hit = (int)ComboHit.SelectedValue,
                        ID_Carrera = idCarrera,
                        Estado = EstadoCorredor.Activo
                    });
                }
                else
                {
                    // Actualizar corredor existente
                    var corredorDb = await db.Corredores.FindAsync(_idCorredorEnEdicion.Value);
                    if (corredorDb != null)
                    {
                        corredorDb.Dorsal = dorsalNum;
                        corredorDb.Nombre = InputNombre.Text.Trim();
                        corredorDb.ID_Categoria = (int)ComboCategoria.SelectedValue;
                        corredorDb.ID_Hit = (int)ComboHit.SelectedValue;
                    }
                    _idCorredorEnEdicion = null;
                    BtnAccionCorredor.Content = "REGISTRAR CORREDOR";
                }

                await db.SaveChangesAsync();

                // Limpiar formulario
                InputDorsal.Clear();
                InputNombre.Clear();
                ComboCategoria.SelectedItem = null;
                ComboHit.SelectedItem = null;
                InputDorsal.Focus();
                
                CargarDatosIniciales();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error al guardar corredor: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void BtnEditarCorredor_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as Button)?.DataContext is CorredorFilaView corredorSel)
            {
                _idCorredorEnEdicion = corredorSel.ID_Corredor;
                
                InputDorsal.Text = corredorSel.Dorsal.ToString();
                InputNombre.Text = corredorSel.Nombre;
                ComboCategoria.SelectedValue = corredorSel.ID_Categoria;
                ComboHit.SelectedValue = corredorSel.ID_Hit;
                
                BtnAccionCorredor.Content = "ACTUALIZAR CORREDOR";
                InputNombre.Focus();
            }
        }

        private async void BtnEliminarCorredor_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as Button)?.DataContext is CorredorFilaView corredorSel)
            {
                var result = MessageBox.Show($"¿Eliminar a {corredorSel.Nombre} (Dorsal {corredorSel.Dorsal})?", "Confirmar", MessageBoxButton.YesNo, MessageBoxImage.Warning);
                if (result == MessageBoxResult.Yes)
                {
                    try
                    {
                        using var db = new ApplicationDbContext();
                        var corredorDb = await db.Corredores.FindAsync(corredorSel.ID_Corredor);
                        if (corredorDb != null)
                        {
                            db.Corredores.Remove(corredorDb);
                            await db.SaveChangesAsync();
                        }
                        CargarDatosIniciales();
                    }
                    catch (Exception)
                    {
                        MessageBox.Show("Error al eliminar al corredor. Es posible que tenga tiempos ya registrados.", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                    }
                }
            }
        }

        private void BtnExportarExcel_Click(object sender, RoutedEventArgs e)
        {
            if (AppState.CarreraActiva == null)
            {
                MessageBox.Show("Por favor selecciona una carrera activa primero.", "Atención", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (TablaCorredores.ItemsSource is not List<CorredorFilaView> lista || !lista.Any())
            {
                MessageBox.Show("No hay corredores registrados en esta carrera para exportar.", "Información", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            try
            {
                char[] invalidChars = Path.GetInvalidFileNameChars();
                string safeCarrera = string.Join("_", AppState.CarreraActiva.Nombre.Split(invalidChars)).Replace(" ", "_");

                var saveDialog = new Microsoft.Win32.SaveFileDialog
                {
                    Title = "Exportar Corredores a Excel",
                    Filter = "Archivo CSV compatible con Excel (*.csv)|*.csv|Todos los archivos (*.*)|*.*",
                    FileName = $"Corredores_{safeCarrera}_{DateTime.Now:yyyyMMdd_HHmm}.csv"
                };

                if (saveDialog.ShowDialog() == true)
                {
                    var sb = new StringBuilder();

                    // 'sep=;' indica explícitamente a Excel el delimitador de columnas sin importar la configuración regional de Windows
                    sb.AppendLine("sep=;");

                    // Encabezados de columnas
                    sb.AppendLine("Dorsal;Nombre;Categoría;Hit de Salida;Estado;Tiempo Neto;Hora de Llegada");

                    // Registros de corredores
                    foreach (var item in lista)
                    {
                        string safeNombre = item.Nombre.Replace("\"", "\"\"");
                        string safeCat = item.NombreCategoria.Replace("\"", "\"\"");
                        string safeHit = item.NombreHit.Replace("\"", "\"\"");
                        string hora = item.HoraLlegada.HasValue ? item.HoraLlegada.Value.ToString("HH:mm:ss") : "--:--:--";

                        sb.AppendLine($"{item.Dorsal};\"{safeNombre}\";\"{safeCat}\";\"{safeHit}\";\"{item.Estado}\";\"{item.TiempoFormateado}\";\"{hora}\"");
                    }

                    // Guardar con codificación UTF-8 con BOM (Byte Order Mark) para soportar acentos y caracteres latinos en Excel
                    File.WriteAllText(saveDialog.FileName, sb.ToString(), new UTF8Encoding(true));

                    var resultado = MessageBox.Show(
                        $"Se exportaron exitosamente {lista.Count} corredores a:\n{saveDialog.FileName}\n\n¿Deseas abrir el archivo en Excel ahora?",
                        "Exportación Completada",
                        MessageBoxButton.YesNo,
                        MessageBoxImage.Information);

                    if (resultado == MessageBoxResult.Yes)
                    {
                        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(saveDialog.FileName)
                        {
                            UseShellExecute = true
                        });
                    }
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error al exportar a Excel: {ex.Message}", "Error de Exportación", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }
}