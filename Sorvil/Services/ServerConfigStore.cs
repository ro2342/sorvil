using Sorvil.Models;
using Windows.Storage;

namespace Sorvil.Services
{
    // Junta a parte não secreta (BaseUrl, em LocalSettings) com a parte
    // secreta (usuário/senha, no PasswordVault via CredentialService) num
    // único ServerProfile pra quem for consumir isso não precisar saber
    // que são dois armazenamentos diferentes por baixo.
    public static class ServerConfigStore
    {
        private const string BaseUrlKey = "ServerBaseUrl";

        public static ServerProfile Load()
        {
            string baseUrl = ApplicationData.Current.LocalSettings.Values[BaseUrlKey] as string;
            StoredCredential credential = CredentialService.TryGet();
            return new ServerProfile
            {
                BaseUrl = baseUrl,
                Username = credential?.Username,
                Password = credential?.Password,
            };
        }

        public static void Save(ServerProfile profile)
        {
            ApplicationData.Current.LocalSettings.Values[BaseUrlKey] = profile.BaseUrl ?? string.Empty;

            if (string.IsNullOrEmpty(profile.Username) && string.IsNullOrEmpty(profile.Password))
            {
                CredentialService.Clear();
            }
            else
            {
                CredentialService.Save(profile.Username, profile.Password);
            }
        }
    }
}
