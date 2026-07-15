using Sorvil.Models;
using Sorvil.Services;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;

namespace Sorvil.Views
{
    public sealed partial class SettingsPage : Page
    {
        public SettingsPage()
        {
            this.InitializeComponent();
            this.Loaded += SettingsPage_Loaded;
        }

        private void SettingsPage_Loaded(object sender, RoutedEventArgs e)
        {
            ServerProfile profile = ServerConfigStore.Load();
            BaseUrlBox.Text = profile.BaseUrl ?? string.Empty;
            UsernameBox.Text = profile.Username ?? string.Empty;
            PasswordBox.Password = profile.Password ?? string.Empty;
        }

        private async void TestButton_Click(object sender, RoutedEventArgs e)
        {
            TestButton.IsEnabled = false;
            SaveButton.IsEnabled = false;
            TestProgress.IsActive = true;
            StatusText.Text = string.Empty;

            OpdsConnectionResult result = await OpdsClient.TestConnectionAsync(
                BaseUrlBox.Text, UsernameBox.Text, PasswordBox.Password);

            TestProgress.IsActive = false;
            TestButton.IsEnabled = true;
            SaveButton.IsEnabled = true;

            StatusText.Text = result.Success
                ? "Conectado! O catálogo OPDS respondeu normalmente."
                : "Não consegui conectar: " + result.Error;
        }

        private void SaveButton_Click(object sender, RoutedEventArgs e)
        {
            ServerProfile profile = new ServerProfile
            {
                BaseUrl = BaseUrlBox.Text?.Trim(),
                Username = UsernameBox.Text,
                Password = PasswordBox.Password,
            };
            ServerConfigStore.Save(profile);
            StatusText.Text = "Salvo.";
        }
    }
}
