using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using Windows.Storage;

namespace Sorvil.Services
{
    // Baixa o arquivo do formato escolhido pra LocalFolder/Books/{id}.{ext},
    // com progresso — mesmo desenho de streaming que
    // UpdateCheckService.DownloadUpdateAsync usa no theartistsway.
    public static class DownloadService
    {
        private const string FolderName = "Books";

        private static readonly Dictionary<string, string> MimeToExtension = new Dictionary<string, string>
        {
            { "application/epub+zip", "epub" },
            { "application/x-kepub+zip", "kepub.epub" },
            { "application/pdf", "pdf" },
            { "application/x-mobipocket-ebook", "mobi" },
            { "application/vnd.amazon.ebook", "azw" },
            { "application/x-cbz", "cbz" },
            { "application/x-cbr", "cbr" },
        };

        public static string GuessExtension(string mimeType)
        {
            if (mimeType != null && MimeToExtension.ContainsKey(mimeType))
            {
                return MimeToExtension[mimeType];
            }
            return "bin";
        }

        public static async Task<StorageFile> DownloadAsync(string bookId, Uri acquisitionUri, string extension, IProgress<double> progress)
        {
            using (HttpClient client = OpdsClient.CreateAuthenticatedClient())
            using (HttpResponseMessage response = await client.GetAsync(acquisitionUri, HttpCompletionOption.ResponseHeadersRead))
            {
                if (!response.IsSuccessStatusCode)
                {
                    // Não usa EnsureSuccessStatusCode() aqui — no UWP a
                    // exceção que ele lança às vezes surge só como um
                    // código WinRT genérico (tipo
                    // "NET_HTTP_MESSAGE_NOT_SUCESS_STATUSCODE"), sem dizer
                    // qual foi o status de verdade. Isso é o que de fato
                    // ajuda a diagnosticar por que um formato específico
                    // não baixa (ex.: 404 quando o servidor não tem esse
                    // formato convertido pra aquele livro).
                    throw new Exception("O servidor respondeu " + (int)response.StatusCode +
                        " (" + response.ReasonPhrase + ") pra esse link de download.");
                }

                StorageFolder folder = await ApplicationData.Current.LocalFolder.CreateFolderAsync(
                    FolderName, CreationCollisionOption.OpenIfExists);
                string fileName = SanitizeFileName(bookId) + "." + (extension ?? "bin");
                StorageFile file = await folder.CreateFileAsync(fileName, CreationCollisionOption.ReplaceExisting);

                long? totalBytes = response.Content.Headers.ContentLength;

                using (Stream networkStream = await response.Content.ReadAsStreamAsync())
                using (Stream fileStream = await file.OpenStreamForWriteAsync())
                {
                    byte[] buffer = new byte[81920];
                    long totalRead = 0;
                    int read;
                    while ((read = await networkStream.ReadAsync(buffer, 0, buffer.Length)) > 0)
                    {
                        await fileStream.WriteAsync(buffer, 0, read);
                        totalRead += read;
                        if (totalBytes.HasValue && totalBytes.Value > 0)
                        {
                            progress?.Report((double)totalRead / totalBytes.Value * 100.0);
                        }
                    }
                }

                return file;
            }
        }

        public static async Task DeleteAsync(string bookId, string extension)
        {
            try
            {
                StorageFolder folder = await ApplicationData.Current.LocalFolder.GetFolderAsync(FolderName);
                StorageFile file = await folder.GetFileAsync(SanitizeFileName(bookId) + "." + (extension ?? "bin"));
                await file.DeleteAsync();
            }
            catch (FileNotFoundException)
            {
                // já não existe — ok.
            }
        }

        private static string SanitizeFileName(string key)
        {
            StringBuilder builder = new StringBuilder();
            foreach (char c in key ?? string.Empty)
            {
                builder.Append(char.IsLetterOrDigit(c) ? c : '_');
            }
            return builder.ToString();
        }
    }
}
