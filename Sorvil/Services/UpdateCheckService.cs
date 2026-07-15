using System;
using System.IO;
using System.Net.Http;
using System.Threading.Tasks;
using Windows.ApplicationModel;
using Windows.Data.Json;
using Windows.Storage;
using Windows.Storage.AccessCache;
using Windows.Storage.Pickers;

namespace Sorvil.Services
{
    public sealed class UpdateCheckResult
    {
        public bool Success { get; set; }
        public string Latest { get; set; }
        public bool UpdateAvailable { get; set; }
        public string Error { get; set; }
    }

    // Checagem/download de atualização self-hosted (sem Store) — mesmo
    // padrão do theartistsway: version.json + .appxbundle publicados no
    // GitHub Pages a cada build verde do CI.
    public static class UpdateCheckService
    {
        private const string VersionUrl = "https://ro2342.github.io/sorvil/app/version.json";
        public const string DownloadPageUrl = "https://ro2342.github.io/sorvil/app/";
        public const string DownloadFileUrl = "https://ro2342.github.io/sorvil/app/app.appxbundle";

        // Um app sideloaded (sem associação com a Store) não consegue se
        // instalar sozinho — o máximo que dá pra automatizar é baixar com
        // progresso pra uma pasta escolhida uma vez (token persiste via
        // FutureAccessList) e abrir o instalador nativo do Windows com um
        // toque ao terminar.
        private const string DownloadFolderTokenKey = "UpdateDownloadFolder";
        private const string DownloadFileName = "app.appxbundle";

        public static bool HasDownloadFolder()
        {
            return StorageApplicationPermissions.FutureAccessList.ContainsItem(DownloadFolderTokenKey);
        }

        public static async Task<StorageFolder> PickDownloadFolderAsync()
        {
            FolderPicker picker = new FolderPicker
            {
                SuggestedStartLocation = PickerLocationId.Downloads,
            };
            picker.FileTypeFilter.Add("*");

            StorageFolder folder = await picker.PickSingleFolderAsync();
            if (folder == null)
            {
                return null;
            }

            StorageApplicationPermissions.FutureAccessList.AddOrReplace(DownloadFolderTokenKey, folder);
            return folder;
        }

        public static async Task<StorageFolder> GetOrPickDownloadFolderAsync()
        {
            if (HasDownloadFolder())
            {
                return await StorageApplicationPermissions.FutureAccessList.GetFolderAsync(DownloadFolderTokenKey);
            }
            return await PickDownloadFolderAsync();
        }

        public static async Task<StorageFile> DownloadUpdateAsync(IProgress<double> progress)
        {
            StorageFolder folder = await GetOrPickDownloadFolderAsync();
            if (folder == null)
            {
                return null;
            }

            StorageFile file = await folder.CreateFileAsync(DownloadFileName, CreationCollisionOption.ReplaceExisting);

            using (HttpClient client = new HttpClient())
            using (HttpResponseMessage response = await client.GetAsync(new Uri(DownloadFileUrl), HttpCompletionOption.ResponseHeadersRead))
            {
                response.EnsureSuccessStatusCode();
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
            }

            return file;
        }

        public static string GetInstalledVersion()
        {
            PackageVersion v = Package.Current.Id.Version;
            return $"{v.Major}.{v.Minor}.{v.Build}.{v.Revision}";
        }

        public static int CompareVersions(string a, string b)
        {
            string[] pa = a.Split('.');
            string[] pb = b.Split('.');
            int len = Math.Max(pa.Length, pb.Length);
            for (int i = 0; i < len; i++)
            {
                int na = i < pa.Length && int.TryParse(pa[i], out int va) ? va : 0;
                int nb = i < pb.Length && int.TryParse(pb[i], out int vb) ? vb : 0;
                if (na != nb)
                {
                    return na - nb;
                }
            }
            return 0;
        }

        public static async Task<UpdateCheckResult> CheckAsync()
        {
            try
            {
                using (HttpClient client = new HttpClient())
                {
                    client.Timeout = TimeSpan.FromSeconds(10);
                    string json = await client.GetStringAsync(new Uri(VersionUrl));
                    JsonObject obj = JsonObject.Parse(json);
                    string latest = obj.GetNamedString("version");
                    string installed = GetInstalledVersion();
                    return new UpdateCheckResult
                    {
                        Success = true,
                        Latest = latest,
                        UpdateAvailable = CompareVersions(latest, installed) > 0,
                    };
                }
            }
            catch (Exception ex)
            {
                return new UpdateCheckResult { Success = false, Error = ex.Message };
            }
        }
    }
}
