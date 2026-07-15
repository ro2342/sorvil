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

            if (!profile.IsConfigured)
            {
                ContinuingSection.Visibility = Visibility.Collapsed;
                RecentSection.Visibility = Visibility.Collapsed;
                ReadyText.Visibility = Visibility.Collapsed;
                return;
            }

            bool hasContinuing = await LoadContinuingAsync();
            bool hasRecent = await LoadRecentAsync(profile);

            ReadyText.Visibility = (!hasContinuing && !hasRecent) ? Visibility.Visible : Visibility.Collapsed;
        }

        // — Você está lendo (BookRecord local, ordenado por última leitura) —

        private async Task<bool> LoadContinuingAsync()
        {
            List<BookRecord> records = await LibraryDataStore.GetAllAsync();
            List<BookRecord> withPosition = new List<BookRecord>();
            foreach (BookRecord record in records)
            {
                if (!string.IsNullOrEmpty(record.LastOpenedAt))
                {
                    withPosition.Add(record);
                }
            }

            // LastOpenedAt é ISO 8601 ("o") — ordena lexicograficamente
            // igual a ordenar cronologicamente, sem precisar parsear.
            withPosition.Sort((a, b) => string.CompareOrdinal(b.LastOpenedAt, a.LastOpenedAt));

            List<BookRecordItemViewModel> items = new List<BookRecordItemViewModel>();
            foreach (BookRecord record in withPosition)
            {
                if (items.Count >= 10)
                {
                    break;
                }
                items.Add(new BookRecordItemViewModel(record));
            }

            if (items.Count == 0)
            {
                ContinuingSection.Visibility = Visibility.Collapsed;
                return false;
            }

            ContinuingGrid.ItemsSource = items;
            ContinuingSection.Visibility = Visibility.Visible;

            foreach (BookRecordItemViewModel item in items)
            {
                LoadContinuingCoverAsync(item);
            }

            return true;
        }

        private async void LoadContinuingCoverAsync(BookRecordItemViewModel item)
        {
            item.Cover = await CoverCacheService.GetCachedImageIfExistsAsync(item.Record.CoverCacheKey);
        }

        private void ContinuingGrid_ItemClick(object sender, ItemClickEventArgs e)
        {
            BookRecordItemViewModel item = (BookRecordItemViewModel)e.ClickedItem;
            ReaderNavigation.TryOpen(Frame, item.Record);
        }

        // — Adicionados recentemente (catálogo OPDS, /opds/new) —

        private async Task<bool> LoadRecentAsync(ServerProfile profile)
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
                    return false;
                }

                RecentGrid.ItemsSource = items;
                RecentSection.Visibility = Visibility.Visible;

                foreach (LibraryItemViewModel item in items)
                {
                    Uri coverUri = item.Entry.ThumbnailUri ?? item.Entry.ImageUri;
                    if (coverUri != null)
                    {
                        LoadRecentCoverAsync(item, coverUri);
                    }
                }

                return true;
            }
            catch (Exception)
            {
                RecentSection.Visibility = Visibility.Collapsed;
                return false;
            }
        }

        private async void LoadRecentCoverAsync(LibraryItemViewModel item, Uri coverUri)
        {
            item.Cover = await CoverCacheService.GetOrDownloadImageAsync(coverUri, item.Entry.Id ?? coverUri.ToString());
        }

        private void RecentGrid_ItemClick(object sender, ItemClickEventArgs e)
        {
            LibraryItemViewModel item = (LibraryItemViewModel)e.ClickedItem;
            Frame.Navigate(typeof(BookDetailPage), item.Entry);
        }

        // — utilidades —

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
