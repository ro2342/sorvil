using System;
using Windows.Security.Credentials;

namespace Sorvil.Services
{
    // Usuário/senha do Calibre-Web nunca ficam em texto puro em disco —
    // vivem só no PasswordVault do próprio Windows, ligado à conta local
    // do aparelho.
    // .NET Native/UWP old-style não tem System.ValueTuple disponível sem
    // referência extra — por isso um retorno simples em classe em vez da
    // sintaxe de tupla nomeada do C# 7.
    public sealed class StoredCredential
    {
        public string Username { get; set; }
        public string Password { get; set; }
    }

    public static class CredentialService
    {
        private const string ResourceName = "Sorvil.CalibreWeb";

        public static void Save(string username, string password)
        {
            Clear();
            PasswordVault vault = new PasswordVault();
            vault.Add(new PasswordCredential(ResourceName, username ?? string.Empty, password ?? string.Empty));
        }

        public static StoredCredential TryGet()
        {
            PasswordVault vault = new PasswordVault();
            try
            {
                PasswordCredential credential = vault.FindAllByResource(ResourceName)[0];
                credential.RetrievePassword();
                return new StoredCredential { Username = credential.UserName, Password = credential.Password };
            }
            catch (Exception)
            {
                // FindAllByResource lança quando não existe nenhuma entrada
                // pra esse resource ainda — não é um erro real aqui.
                return null;
            }
        }

        public static void Clear()
        {
            PasswordVault vault = new PasswordVault();
            try
            {
                foreach (PasswordCredential credential in vault.FindAllByResource(ResourceName))
                {
                    vault.Remove(credential);
                }
            }
            catch (Exception)
            {
                // Nada cadastrado ainda pra esse resource — ok.
            }
        }
    }
}
