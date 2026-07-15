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

                // Na primeira carga (feedUri nulo == aba Biblioteca aberta
                // direto, sem vir de um toque em pasta), o feed raiz do
                // Calibre-Web normalmente é só navegação ("Alphabetical
                // Books", "Recently added" etc.) — pula direto pra dentro
                // da entrada que já é a lista de livros de verdade, em vez
                // de obrigar o usuário a tocar numa pasta pra só então ver
                // capas.
                if (feedUri == null && !feed.IsBookFeed)
                {
                    OpdsEntry autoEntry = FindAutoBrowseEntry(feed);
                    if (autoEntry != null && autoEntry.NavigationUri != null)
                    {
                        feed = await OpdsClient.GetFeedAsync(autoEntry.NavigationUri);
                    }
                }

                _currentFeed = feed;
                FeedTitleText.Text = string.IsNullOrEmpty(feed.Title) ? "Biblioteca" : feed.Title;
                RenderFeed(feed);
            }
            catch (Exception ex)
            {
                ShowError("Não consegui carregar o catálogo: " + ex.Message);
            }
        }

        private static OpdsEntry FindAutoBrowseEntry(OpdsFeed feed)
        {
            foreach (OpdsEntry entry in feed.Entries)
            {
                if (entry.Title != null &&
                    (entry.Title.IndexOf("Alphabetical", StringComparison.OrdinalIgnoreCase) >= 0 ||
                     entry.Title.IndexOf("Todos os livros", StringComparison.OrdinalIgnoreCase) >= 0))
                {
                    return entry;
                }
            }
            return null;
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

        // GridView sem ItemWidth fixo mede cada item pelo próprio
        // conteúdo, e num Frame largo isso rendia só ~2 colunas com um
        // vão enorme sobrando — calcula a largura certa pra sempre caber
        // 4 colunas, do jeito que o usuário pediu, em qualquer tamanho de
        // tela (não só a do Lumia).
        private void BooksGrid_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            if (e.NewSize.Width <= 0)
            {
                return;
            }

            ItemsWrapGrid panel = BooksGrid.ItemsPanelRoot as ItemsWrapGrid;
            if (panel != null)
            {
                panel.ItemWidth = Math.Floor(e.NewSize.Width / 4);
            }
        }

        private void BooksGrid_ItemClick(object sender, ItemClickEventArgs e)
        {
            LibraryItemViewModel item = (LibraryItemViewModel)e.ClickedItem;
            Frame.Navigate(typeof(BookDetailPage), item.Entry);
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
