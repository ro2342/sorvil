using Sorvil.Models;
using Sorvil.Services;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;

namespace Sorvil.Views
{
    public sealed partial class HomePage : Page
    {
        public HomePage()
        {
            this.InitializeComponent();
            this.Loaded += HomePage_Loaded;
        }

        private void HomePage_Loaded(object sender, RoutedEventArgs e)
        {
            ServerProfile profile = ServerConfigStore.Load();
            SetupHintBorder.Visibility = profile.IsConfigured ? Visibility.Collapsed : Visibility.Visible;
            ReadyText.Visibility = profile.IsConfigured ? Visibility.Visible : Visibility.Collapsed;
        }

        private void OpenSettings_Click(object sender, RoutedEventArgs e)
        {
            MainPage.Current.NavigateToTab(typeof(SettingsPage));
        }
    }
}
