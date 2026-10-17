// Path: src/CarreraApp/Helpers/ImageHelper.cs
using System;
using System.IO;
using System.Windows.Media.Imaging;

namespace CarreraApp.Helpers
{
    public static class ImageHelper
    {
        public static BitmapImage CargarSinBloqueo(string rutaArchivo)
        {
            if (!File.Exists(rutaArchivo))
                return new BitmapImage();

            var bitmap = new BitmapImage();
            using (var stream = new FileStream(rutaArchivo, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                using var memoryStream = new MemoryStream();
                stream.CopyTo(memoryStream);
                memoryStream.Position = 0;

                bitmap.BeginInit();
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.StreamSource = memoryStream;
                bitmap.EndInit();
            }

            // Congelar el BitmapImage para que sea inmutable y seguro entre hilos
            bitmap.Freeze();
            return bitmap;
        }
    }
}