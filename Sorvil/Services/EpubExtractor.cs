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
    public sealed class EpubTocEntry
    {
        public string Title { get; set; }
        public int SpineIndex { get; set; }
    }

    public sealed class EpubManifest
    {
        public List<string> SpineFiles { get; set; } = new List<string>();
        public List<EpubTocEntry> Toc { get; set; } = new List<EpubTocEntry>();
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
        private static readonly XNamespace NcxNs = "http://www.daisy.org/z3986/2005/ncx/";
        private const string NcxMediaType = "application/x-dtbncx+xml";

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
            string ncxHref = null;
            XElement manifestElement = opfDoc.Root.Element(OpfNs + "manifest");
            if (manifestElement != null)
            {
                foreach (XElement item in manifestElement.Elements(OpfNs + "item"))
                {
                    string id = (string)item.Attribute("id");
                    string href = (string)item.Attribute("href");
                    string mediaType = (string)item.Attribute("media-type");
                    if (id != null && href != null)
                    {
                        manifestIdToHref[id] = href;
                    }
                    if (mediaType == NcxMediaType && href != null)
                    {
                        ncxHref = href;
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

            if (ncxHref != null)
            {
                // Índice/sumário de verdade (títulos escritos pelo autor/
                // editora), não uma aproximação — se não existir ou não
                // conseguir ler, só fica sem índice (não quebra o resto).
                try
                {
                    manifest.Toc = await ParseTocAsync(bookFolder, opfDirectory + ncxHref, manifest.SpineFiles);
                }
                catch (Exception)
                {
                    manifest.Toc = new List<EpubTocEntry>();
                }
            }

            return manifest;
        }

        private static async Task<List<EpubTocEntry>> ParseTocAsync(StorageFolder bookFolder, string ncxPath, List<string> spineFiles)
        {
            StorageFile ncxFile = await GetFileByRelativePathAsync(bookFolder, ncxPath);
            string ncxXml = await FileIO.ReadTextAsync(ncxFile);
            XDocument ncxDoc = XDocument.Parse(ncxXml);

            string ncxDirectory = string.Empty;
            int lastSlash = ncxPath.LastIndexOf('/');
            if (lastSlash >= 0)
            {
                ncxDirectory = ncxPath.Substring(0, lastSlash + 1);
            }

            List<EpubTocEntry> toc = new List<EpubTocEntry>();
            XElement navMap = ncxDoc.Root.Element(NcxNs + "navMap");
            if (navMap != null)
            {
                CollectNavPoints(navMap, ncxDirectory, spineFiles, toc);
            }
            return toc;
        }

        // navPoint pode aninhar outros navPoints (sub-capítulos) — achata
        // tudo numa lista só, mais fácil de navegar numa tela pequena de
        // telefone do que uma árvore expansível.
        private static void CollectNavPoints(XElement parent, string ncxDirectory, List<string> spineFiles, List<EpubTocEntry> toc)
        {
            foreach (XElement navPoint in parent.Elements(NcxNs + "navPoint"))
            {
                XElement navLabel = navPoint.Element(NcxNs + "navLabel");
                XElement textElement = navLabel != null ? navLabel.Element(NcxNs + "text") : null;
                XElement content = navPoint.Element(NcxNs + "content");
                string src = content != null ? (string)content.Attribute("src") : null;

                if (textElement != null && src != null)
                {
                    string cleanSrc = ncxDirectory + src;
                    int hashIndex = cleanSrc.IndexOf('#');
                    if (hashIndex >= 0)
                    {
                        cleanSrc = cleanSrc.Substring(0, hashIndex);
                    }

                    int spineIndex = spineFiles.IndexOf(cleanSrc);
                    if (spineIndex >= 0)
                    {
                        toc.Add(new EpubTocEntry { Title = textElement.Value.Trim(), SpineIndex = spineIndex });
                    }
                }

                CollectNavPoints(navPoint, ncxDirectory, spineFiles, toc);
            }
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
