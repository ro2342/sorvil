namespace Sorvil.Models
{
    // Configuração do servidor Calibre-Web/Calibre-Web-Automated. BaseUrl
    // não é segredo (fica em LocalSettings via ServerConfigStore);
    // Username/Password são carregados à parte, do PasswordVault.
    public sealed class ServerProfile
    {
        public string BaseUrl { get; set; }
        public string Username { get; set; }
        public string Password { get; set; }

        public bool IsConfigured => !string.IsNullOrWhiteSpace(BaseUrl);
    }
}
