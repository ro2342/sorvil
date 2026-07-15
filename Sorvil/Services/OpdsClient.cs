using Sorvil.Models;
using System;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading.Tasks;
using System.Xml.Linq;

namespace Sorvil.Services
{
    public sealed class OpdsConnectionResult
    {
        public bool Success { get; set; }
        public string Error { get; set; }
    }

    // Fala com o catálogo OPDS do Calibre-Web/Calibre-Web-Automated —
    // mesma API respondendo em /opds nos dois, já que o CWA é um fork que
    // mantém compatibilidade total com o Calibre-Web original nesse ponto.
    public static class OpdsClient
    {
        private static readonly XNamespace AtomNs = "http://www.w3.org/2005/Atom";
        private static readonly XNamespace OpenSearchNs = "http://a9.com/-/spec/opensearch/1.1/";

        private const string RelImage = "http://opds-spec.org/image";
        private const string RelThumbnail = "http://opds-spec.org/image/thumbnail";
        private const string RelAcquisitionPrefix = "http://opds-spec.org/acquisition";
        private const string RelNext = "next";
        private const string RelSearch = "search";

        private static string TrimBaseUrl(string baseUrl)
        {
            return (baseUrl ?? string.Empty).Trim().TrimEnd('/');
        }

        public static Uri BuildOpdsRootUri(string baseUrl)
        {
            return new Uri(TrimBaseUrl(baseUrl) + "/opds");
        }

        // Rota fixa do próprio Calibre-Web pra "livros adicionados
        // recentemente" — não é configurável nem descoberta via feed, é
        // parte do roteamento interno do servidor.
        public static Uri BuildRecentUri(string baseUrl)
        {
            return new Uri(TrimBaseUrl(baseUrl) + "/opds/new");
        }

        public static async Task<OpdsConnectionResult> TestConnectionAsync(string baseUrl, string username, string password)
        {
            if (string.IsNullOrWhiteSpace(baseUrl))
            {
                return new OpdsConnectionResult { Success = false, Error = "Preencha a URL do servidor." };
            }

            Uri opdsUri;
            try
            {
                opdsUri = BuildOpdsRootUri(baseUrl);
            }
            catch (UriFormatException)
            {
                return new OpdsConnectionResult { Success = false, Error = "URL inválida." };
            }

            try
            {
                using (HttpClient client = new HttpClient())
                {
                    client.Timeout = TimeSpan.FromSeconds(10);
                    if (!string.IsNullOrEmpty(username) || !string.IsNullOrEmpty(password))
                    {
                        string token = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{username}:{password}"));
                        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", token);
                    }

                    HttpResponseMessage response = await client.GetAsync(opdsUri);
                    if (response.IsSuccessStatusCode)
                    {
                        return new OpdsConnectionResult { Success = true };
                    }
                    return new OpdsConnectionResult
                    {
                        Success = false,
                        Error = $"O servidor respondeu {(int)response.StatusCode} ({response.ReasonPhrase}).",
                    };
                }
            }
            catch (Exception ex)
            {
                return new OpdsConnectionResult { Success = false, Error = ex.Message };
            }
        }

        // Compartilhado por qualquer chamada autenticada contra o servidor
        // já salvo (catálogo, capas) — CoverCacheService reaproveita isso
        // em vez de duplicar a montagem do header Basic Auth.
        public static HttpClient CreateAuthenticatedClient()
        {
            ServerProfile profile = ServerConfigStore.Load();
            HttpClient client = new HttpClient();
            client.Timeout = TimeSpan.FromSeconds(20);
            if (!string.IsNullOrEmpty(profile.Username) || !string.IsNullOrEmpty(profile.Password))
            {
                string token = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{profile.Username}:{profile.Password}"));
                client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", token);
            }
            return client;
        }

        public static async Task<OpdsFeed> GetRootFeedAsync()
        {
            ServerProfile profile = ServerConfigStore.Load();
            return await GetFeedAsync(BuildOpdsRootUri(profile.BaseUrl));
        }

        public static async Task<OpdsFeed> GetFeedAsync(Uri feedUri)
        {
            using (HttpClient client = CreateAuthenticatedClient())
            {
                string xml = await client.GetStringAsync(feedUri);
                return ParseFeed(xml, feedUri);
            }
        }

        // Resolve o template OpenSearch (documento XML separado apontado
        // pelo link rel="search" do feed) pra uma URL de busca de verdade,
        // já com o termo digitado no lugar de {searchTerms}.
        public static async Task<Uri> ResolveSearchUriAsync(Uri openSearchDescriptionUri, string query)
        {
            using (HttpClient client = CreateAuthenticatedClient())
            {
                string xml = await client.GetStringAsync(openSearchDescriptionUri);
                XDocument doc = XDocument.Parse(xml);

                string template = null;
                foreach (XElement urlElement in doc.Root.Elements(OpenSearchNs + "Url"))
                {
                    string type = (string)urlElement.Attribute("type");
                    if (type == null || type.IndexOf("atom", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        template = (string)urlElement.Attribute("template");
                        break;
                    }
                }

                if (template == null)
                {
                    return null;
                }

                string encoded = Uri.EscapeDataString(query ?? string.Empty);
                string resolved = template.Replace("{searchTerms}", encoded);

                Uri absolute;
                if (Uri.TryCreate(resolved, UriKind.Absolute, out absolute))
                {
                    return absolute;
                }
                return new Uri(openSearchDescriptionUri, resolved);
            }
        }

        private static OpdsFeed ParseFeed(string xml, Uri feedUri)
        {
            XDocument doc = XDocument.Parse(xml);
            XElement root = doc.Root;
            OpdsFeed feed = new OpdsFeed();

            XElement titleElement = root.Element(AtomNs + "title");
            feed.Title = titleElement != null ? titleElement.Value : null;

            foreach (XElement linkElement in root.Elements(AtomNs + "link"))
            {
                string rel = (string)linkElement.Attribute("rel");
                string href = (string)linkElement.Attribute("href");
                if (href == null)
                {
                    continue;
                }
                Uri resolved = ResolveUri(feedUri, href);

                if (rel == RelNext)
                {
                    feed.NextUri = resolved;
                }
                else if (rel == RelSearch)
                {
                    feed.SearchUri = resolved;
                }
            }

            foreach (XElement entryElement in root.Elements(AtomNs + "entry"))
            {
                feed.Entries.Add(ParseEntry(entryElement, feedUri));
            }

            return feed;
        }

        private static OpdsEntry ParseEntry(XElement entryElement, Uri feedUri)
        {
            OpdsEntry entry = new OpdsEntry();

            XElement idElement = entryElement.Element(AtomNs + "id");
            entry.Id = idElement != null ? idElement.Value : null;

            XElement titleElement = entryElement.Element(AtomNs + "title");
            entry.Title = titleElement != null ? titleElement.Value : "(sem título)";

            XElement authorElement = entryElement.Element(AtomNs + "author");
            if (authorElement != null)
            {
                XElement nameElement = authorElement.Element(AtomNs + "name");
                entry.Author = nameElement != null ? nameElement.Value : null;
            }

            XElement summaryElement = entryElement.Element(AtomNs + "summary");
            if (summaryElement == null)
            {
                summaryElement = entryElement.Element(AtomNs + "content");
            }
            entry.Summary = summaryElement != null ? summaryElement.Value : null;

            foreach (XElement linkElement in entryElement.Elements(AtomNs + "link"))
            {
                string rel = (string)linkElement.Attribute("rel");
                string href = (string)linkElement.Attribute("href");
                string type = (string)linkElement.Attribute("type");
                if (href == null)
                {
                    continue;
                }
                Uri resolved = ResolveUri(feedUri, href);

                if (rel == RelImage)
                {
                    entry.ImageUri = resolved;
                }
                else if (rel == RelThumbnail)
                {
                    entry.ThumbnailUri = resolved;
                }
                else if (rel != null && rel.StartsWith(RelAcquisitionPrefix, StringComparison.OrdinalIgnoreCase))
                {
                    entry.Acquisitions.Add(new OpdsAcquisitionLink { MimeType = type, Href = resolved });
                }
                else if (entry.NavigationUri == null && (rel == "alternate" || rel == "subsection" || rel == null))
                {
                    entry.NavigationUri = resolved;
                }
            }

            return entry;
        }

        private static Uri ResolveUri(Uri baseUri, string href)
        {
            Uri result;
            if (Uri.TryCreate(href, UriKind.Absolute, out result))
            {
                return result;
            }
            return new Uri(baseUri, href);
        }
    }
}
