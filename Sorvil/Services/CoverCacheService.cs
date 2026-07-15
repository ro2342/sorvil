using System;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using Windows.Storage;
using Windows.Storage.Streams;
using Windows.UI.Xaml.Media.Imaging;

namespace Sorvil.Services
{
    // Cache local de capas em LocalFolder/Covers/ — evita bater no
    // servidor de novo pra toda capa já vista, e deixa a Biblioteca
    // utilizável offline pro que já foi carregado antes.
    public static class CoverCacheService
    {
        private const string FolderName = "Covers";

        public static async Task<BitmapImage> GetOrDownloadImageAsync(Uri coverUri, string cacheKey)
        {
            if (coverUri == null)
            {
                return null;
            }

            try
            {
                StorageFile file = await GetOrDownloadFileAsync(coverUri, cacheKey);
                using (IRandomAccessStream stream = await file.OpenAsync(FileAccessMode.Read))
                {
                    BitmapImage bitmap = new BitmapImage();
                    await bitmap.SetSourceAsync(stream);
                    return bitmap;
                }
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static async Task<StorageFile> GetOrDownloadFileAsync(Uri coverUri, string cacheKey)
        {
            StorageFolder folder = await ApplicationData.Current.LocalFolder.CreateFolderAsync(
                FolderName, CreationCollisionOption.OpenIfExists);
            string fileName = SanitizeFileName(cacheKey) + ".img";

            try
            {
                return await folder.GetFileAsync(fileName);
            }
            catch (FileNotFoundException)
            {
                // Ainda não tem em cache — baixa agora.
            }

            using (HttpClient client = OpdsClient.CreateAuthenticatedClient())
            {
                byte[] bytes = await client.GetByteArrayAsync(coverUri);
                StorageFile file = await folder.CreateFileAsync(fileName, CreationCollisionOption.ReplaceExisting);
                await FileIO.WriteBytesAsync(file, bytes);
                return file;
            }
        }

        private static string SanitizeFileName(string key)
        {
            StringBuilder builder = new StringBuilder();
            foreach (char c in key)
            {
                builder.Append(char.IsLetterOrDigit(c) ? c : '_');
            }
            return builder.ToString();
        }
    }
}
