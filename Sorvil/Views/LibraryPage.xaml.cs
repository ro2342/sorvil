using Sorvil.Models;
using Sorvil.Services;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Windows.System;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Input;
using Windows.UI.Xaml.Navigation;

namespace Sorvil.Views
{
    // Navegador genérico de catálogo OPDS: a mesma página serve tanto pra
    // uma pasta de navegação (lista com ícone) quanto pra um feed de
    // livros (grade de capas) — decide sozinha qual mostrar olhando se o
    // feed carregado tem alguma entrada com link de aquisição
    // (OpdsFeed.IsBookFeed). Navegar mais fundo empilha uma nova instância
    // desta mesma página no Frame, então o botão Voltar do sistema (já
    // tratado no MainPage) sobe um nível de cada vez.
    public sealed partial class LibraryPage : Page
    {
        private OpdsFeed _currentFeed;

        public LibraryPage()
        {
            this.InitializeComponent();
        }

        protected override async void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);
            await LoadFeedAsync(e.Parameter as Uri);
        }

        private async Task LoadFeedAsync(Uri feedUri)
        {
            ShowLoading();

            ServerProfile profile = ServerConfigStore.Load();
            if (!profile.IsConfigured)
            {
                ShowError("Configure o servidor em Ajustes → Servidor antes de navegar pela biblioteca.");
                return;
            }

            try
            {
                OpdsFeed feed = feedUri != null
                    ? await OpdsClient.GetFeedAsync(feedUri)
                    : await OpdsClient.GetRootFeedAsync();

                _currentFeed = feed;
                FeedTitleText.Text = string.IsNullOrEmpty(feed.Title) ? "Biblioteca" : feed.Title;
                RenderFeed(feed);
            }
            catch (Exception ex)
            {
                ShowError("Não consegui carregar o catálogo: " + ex.Message);
            }
        }

        private void RenderFeed(OpdsFeed feed)
        {
            List<LibraryItemViewModel> items = new List<LibraryItemViewModel>();
            foreach (OpdsEntry entry in feed.Entries)
            {
                items.Add(new LibraryItemViewModel(entry));
            }

            LoadingRing.IsActive = false;
            ErrorText.Visibility = Visibility.Collapsed;
            NextPageButton.Visibility = feed.NextUri != null ? Visibility.Visible : Visibility.Collapsed;

            if (feed.IsBookFeed)
            {
                BooksGrid.ItemsSource = items;
                BooksGrid.Visibility = Visibility.Visible;
                FoldersList.Visibility = Visibility.Collapsed;
            }
            else
            {
                FoldersList.ItemsSource = items;
                FoldersList.Visibility = Visibility.Visible;
                BooksGrid.Visibility = Visibility.Collapsed;
            }

            foreach (LibraryItemViewModel item in items)
            {
                Uri coverUri = item.Entry.ThumbnailUri ?? item.Entry.ImageUri;
                if (coverUri != null)
                {
                    LoadCoverAsync(item, coverUri);
                }
            }
        }

        private async Task LoadCoverAsync(LibraryItemViewModel item, Uri coverUri)
        {
            item.Cover = await CoverCacheService.GetOrDownloadImageAsync(coverUri, item.Entry.Id ?? coverUri.ToString());
        }

        private void ShowLoading()
        {
            LoadingRing.IsActive = true;
            ErrorText.Visibility = Visibility.Collapsed;
            BooksGrid.Visibility = Visibility.Collapsed;
            FoldersList.Visibility = Visibility.Collapsed;
            NextPageButton.Visibility = Visibility.Collapsed;
        }

        private void ShowError(string message)
        {
            LoadingRing.IsActive = false;
            ErrorText.Text = message;
            ErrorText.Visibility = Visibility.Visible;
            BooksGrid.Visibility = Visibility.Collapsed;
            FoldersList.Visibility = Visibility.Collapsed;
            NextPageButton.Visibility = Visibility.Collapsed;
        }

        private void BooksGrid_ItemClick(object sender, ItemClickEventArgs e)
        {
            LibraryItemViewModel item = (LibraryItemViewModel)e.ClickedItem;
            // A tela de detalhe/download do livro chega na próxima leva —
            // por enquanto só mostra um resumo rápido num flyout.
            TextBlock content = new TextBlock
            {
                Text = string.IsNullOrEmpty(item.Subtitle) ? item.Title : item.Title + "\n" + item.Subtitle,
                TextWrapping = TextWrapping.Wrap,
                MaxWidth = 240,
            };
            Flyout flyout = new Flyout { Content = content };
            flyout.ShowAt(BooksGrid);
        }

        private void FoldersList_ItemClick(object sender, ItemClickEventArgs e)
        {
            LibraryItemViewModel item = (LibraryItemViewModel)e.ClickedItem;
            if (item.Entry.NavigationUri != null)
            {
                Frame.Navigate(typeof(LibraryPage), item.Entry.NavigationUri);
            }
        }

        private async void NextPageButton_Click(object sender, RoutedEventArgs e)
        {
            if (_currentFeed != null && _currentFeed.NextUri != null)
            {
                await LoadFeedAsync(_currentFeed.NextUri);
            }
        }

        private async void SearchButton_Click(object sender, RoutedEventArgs e)
        {
            await RunSearchAsync();
        }

        private async void SearchBox_KeyDown(object sender, KeyRoutedEventArgs e)
        {
            if (e.Key == VirtualKey.Enter)
            {
                await RunSearchAsync();
            }
        }

        private async Task RunSearchAsync()
        {
            string query = SearchBox.Text != null ? SearchBox.Text.Trim() : null;
            if (string.IsNullOrEmpty(query))
            {
                return;
            }
            if (_currentFeed == null || _currentFeed.SearchUri == null)
            {
                ShowError("Busca não disponível aqui.");
                return;
            }

            ShowLoading();
            try
            {
                Uri searchUri = await OpdsClient.ResolveSearchUriAsync(_currentFeed.SearchUri, query);
                if (searchUri == null)
                {
                    ShowError("Não consegui montar a busca.");
                    return;
                }
                Frame.Navigate(typeof(LibraryPage), searchUri);
            }
            catch (Exception ex)
            {
                ShowError("Erro na busca: " + ex.Message);
            }
        }
    }
}
