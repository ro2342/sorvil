using System;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading.Tasks;

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
    // Nesta fatia só existe o teste de conexão; o parse completo do feed
    // Atom (capas, paginação, aquisição por formato) entra na tarefa do
    // catálogo.
    public static class OpdsClient
    {
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

        public static Uri BuildOpdsRootUri(string baseUrl)
        {
            string trimmed = (baseUrl ?? string.Empty).Trim().TrimEnd('/');
            return new Uri(trimmed + "/opds");
        }
    }
}
