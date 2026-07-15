using Sorvil.Models;
using Sorvil.Services;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
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

        private async void HomePage_Loaded(object sender, RoutedEventArgs e)
        {
            ServerProfile profile = ServerConfigStore.Load();
            SetupHintBorder.Visibility = profile.IsConfigured ? Visibility.Collapsed : Visibility.Visible;

            if (profile.IsConfigured)
            {
                await LoadRecentAsync(profile);
            }
            else
            {
                RecentSection.Visibility = Visibility.Collapsed;
                ReadyText.Visibility = Visibility.Collapsed;
            }
        }

        private async Task LoadRecentAsync(ServerProfile profile)
        {
            try
            {
                // /opds/new é rota fixa do Calibre-Web, não descoberta via
                // feed — se um dia deixar de existir numa versão nova do
                // servidor, essa seção só some (catch abaixo) sem quebrar o
                // resto do app; a Biblioteca continua acessível pelo feed
                // raiz normalmente.
                Uri recentUri = OpdsClient.BuildRecentUri(profile.BaseUrl);
                OpdsFeed feed = await OpdsClient.GetFeedAsync(recentUri);

                List<LibraryItemViewModel> items = new List<LibraryItemViewModel>();
                foreach (OpdsEntry entry in feed.Entries)
                {
                    if (items.Count >= 10)
                    {
                        break;
                    }
                    items.Add(new LibraryItemViewModel(entry));
                }

                if (items.Count == 0)
                {
                    RecentSection.Visibility = Visibility.Collapsed;
                    ReadyText.Visibility = Visibility.Visible;
                    return;
                }

                RecentItems.ItemsSource = items;
                RecentSection.Visibility = Visibility.Visible;
                ReadyText.Visibility = Visibility.Collapsed;

                foreach (LibraryItemViewModel item in items)
                {
                    Uri coverUri = item.Entry.ThumbnailUri ?? item.Entry.ImageUri;
                    if (coverUri != null)
                    {
                        LoadCoverAsync(item, coverUri);
                    }
                }
            }
            catch (Exception)
            {
                RecentSection.Visibility = Visibility.Collapsed;
                ReadyText.Visibility = Visibility.Visible;
            }
        }

        private async Task LoadCoverAsync(LibraryItemViewModel item, Uri coverUri)
        {
            item.Cover = await CoverCacheService.GetOrDownloadImageAsync(coverUri, item.Entry.Id ?? coverUri.ToString());
        }

        private void OpenSettings_Click(object sender, RoutedEventArgs e)
        {
            MainPage.Current.NavigateToTab(typeof(SettingsPage));
        }

        private void ViewLibrary_Click(object sender, RoutedEventArgs e)
        {
            MainPage.Current.NavigateToTab(typeof(LibraryPage));
        }
    }
}
