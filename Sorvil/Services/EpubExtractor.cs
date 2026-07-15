using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Threading.Tasks;
using System.Xml.Linq;
using Windows.Storage;

namespace Sorvil.Services
{
    public sealed class EpubManifest
    {
        public List<string> SpineFiles { get; set; } = new List<string>();
    }

    // Descompacta EPUB/KEPUB (mesmo container ZIP nos dois — KEPUB só tem
    // <span class="koboSpan"> extras da Kobo, que não atrapalham
    // renderização normal) uma vez pra
    // LocalFolder/BooksExtracted/{bookId}/, e lê container.xml + OPF pra
    // descobrir a ordem dos capítulos (spine).
    public static class EpubExtractor
    {
        private const string ExtractedFolderName = "BooksExtracted";
        private static readonly XNamespace ContainerNs = "urn:oasis:names:tc:opendocument:xmlns:container";
        private static readonly XNamespace OpfNs = "http://www.idpf.org/2007/opf";

        public static string GetExtractedFolderName(string bookId)
        {
            return SanitizeName(bookId);
        }

        // WebView nativo do UWP navega em conteúdo de ApplicationData local
        // via esse esquema fixo — cada segmento precisa ser escapado à
        // parte (nomes de capítulo podem ter espaço/acento).
        public static Uri BuildLocalContentUri(string folderName, string relativePath)
        {
            StringBuilder builder = new StringBuilder("ms-appdata:///local");
            string combined = ExtractedFolderName + "/" + folderName + "/" + relativePath;
            foreach (string segment in combined.Split('/'))
            {
                if (segment.Length == 0)
                {
                    continue;
                }
                builder.Append('/');
                builder.Append(Uri.EscapeDataString(segment));
            }
            return new Uri(builder.ToString());
        }

        public static async Task<EpubManifest> ExtractAndParseAsync(string bookId, StorageFile epubFile)
        {
            StorageFolder root = await ApplicationData.Current.LocalFolder.CreateFolderAsync(
                ExtractedFolderName, CreationCollisionOption.OpenIfExists);
            string folderName = GetExtractedFolderName(bookId);

            StorageFolder bookFolder;
            bool alreadyExtracted = true;
            try
            {
                bookFolder = await root.GetFolderAsync(folderName);
            }
            catch (FileNotFoundException)
            {
                alreadyExtracted = false;
                bookFolder = await root.CreateFolderAsync(folderName, CreationCollisionOption.ReplaceExisting);
            }

            if (!alreadyExtracted)
            {
                using (Stream fileStream = await epubFile.OpenStreamForReadAsync())
                using (ZipArchive archive = new ZipArchive(fileStream, ZipArchiveMode.Read))
                {
                    foreach (ZipArchiveEntry entry in archive.Entries)
                    {
                        if (string.IsNullOrEmpty(entry.Name))
                        {
                            // Entrada de diretório (FullName termina em "/") — nada a extrair.
                            continue;
                        }
                        await ExtractEntryAsync(bookFolder, entry);
                    }
                }
            }

            return await ParseManifestAsync(bookFolder);
        }

        private static async Task ExtractEntryAsync(StorageFolder rootFolder, ZipArchiveEntry entry)
        {
            string[] parts = entry.FullName.Split('/');
            StorageFolder currentFolder = rootFolder;
            for (int i = 0; i < parts.Length - 1; i++)
            {
                if (parts[i].Length == 0)
                {
                    continue;
                }
                currentFolder = await currentFolder.CreateFolderAsync(parts[i], CreationCollisionOption.OpenIfExists);
            }

            StorageFile outputFile = await currentFolder.CreateFileAsync(parts[parts.Length - 1], CreationCollisionOption.ReplaceExisting);
            using (Stream entryStream = entry.Open())
            using (Stream outputStream = await outputFile.OpenStreamForWriteAsync())
            {
                await entryStream.CopyToAsync(outputStream);
            }
        }

        private static async Task<StorageFile> GetFileByRelativePathAsync(StorageFolder root, string relativePath)
        {
            string[] parts = relativePath.Split('/');
            StorageFolder currentFolder = root;
            for (int i = 0; i < parts.Length - 1; i++)
            {
                currentFolder = await currentFolder.GetFolderAsync(parts[i]);
            }
            return await currentFolder.GetFileAsync(parts[parts.Length - 1]);
        }

        private static async Task<EpubManifest> ParseManifestAsync(StorageFolder bookFolder)
        {
            StorageFile containerFile = await GetFileByRelativePathAsync(bookFolder, "META-INF/container.xml");
            string containerXml = await FileIO.ReadTextAsync(containerFile);
            XDocument containerDoc = XDocument.Parse(containerXml);

            string opfPath = null;
            foreach (XElement rootfile in containerDoc.Descendants(ContainerNs + "rootfile"))
            {
                string fullPath = (string)rootfile.Attribute("full-path");
                if (!string.IsNullOrEmpty(fullPath))
                {
                    opfPath = fullPath;
                    break;
                }
            }

            if (opfPath == null)
            {
                throw new InvalidOperationException("container.xml sem rootfile.");
            }

            StorageFile opfFile = await GetFileByRelativePathAsync(bookFolder, opfPath);
            string opfXml = await FileIO.ReadTextAsync(opfFile);
            XDocument opfDoc = XDocument.Parse(opfXml);

            string opfDirectory = string.Empty;
            int lastSlash = opfPath.LastIndexOf('/');
            if (lastSlash >= 0)
            {
                opfDirectory = opfPath.Substring(0, lastSlash + 1);
            }

            Dictionary<string, string> manifestIdToHref = new Dictionary<string, string>();
            XElement manifestElement = opfDoc.Root.Element(OpfNs + "manifest");
            if (manifestElement != null)
            {
                foreach (XElement item in manifestElement.Elements(OpfNs + "item"))
                {
                    string id = (string)item.Attribute("id");
                    string href = (string)item.Attribute("href");
                    if (id != null && href != null)
                    {
                        manifestIdToHref[id] = href;
                    }
                }
            }

            EpubManifest manifest = new EpubManifest();
            XElement spineElement = opfDoc.Root.Element(OpfNs + "spine");
            if (spineElement != null)
            {
                foreach (XElement itemref in spineElement.Elements(OpfNs + "itemref"))
                {
                    string idref = (string)itemref.Attribute("idref");
                    string href;
                    if (idref != null && manifestIdToHref.TryGetValue(idref, out href))
                    {
                        manifest.SpineFiles.Add(opfDirectory + href);
                    }
                }
            }

            return manifest;
        }

        private static string SanitizeName(string key)
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
