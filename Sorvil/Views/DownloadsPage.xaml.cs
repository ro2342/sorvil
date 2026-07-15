using Sorvil.Models;
using Sorvil.Services;
using System.Collections.Generic;
using System.Threading.Tasks;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;

namespace Sorvil.Views
{
    public sealed partial class DownloadsPage : Page
    {
        public DownloadsPage()
        {
            this.InitializeComponent();
            this.Loaded += DownloadsPage_Loaded;
        }

        private async void DownloadsPage_Loaded(object sender, RoutedEventArgs e)
        {
            await RefreshAsync();
        }

        private async Task RefreshAsync()
        {
            List<BookRecord> records = await LibraryDataStore.GetAllAsync();
            List<BookRecordItemViewModel> items = new List<BookRecordItemViewModel>();
            foreach (BookRecord record in records)
            {
                items.Add(new BookRecordItemViewModel(record));
            }

            BooksList.ItemsSource = items;
            EmptyText.Visibility = items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            BooksList.Visibility = items.Count == 0 ? Visibility.Collapsed : Visibility.Visible;

            foreach (BookRecordItemViewModel item in items)
            {
                LoadCoverAsync(item);
            }
        }

        private async void LoadCoverAsync(BookRecordItemViewModel item)
        {
            // A capa já devia estar em cache local (foi baixada quando o
            // livro apareceu na Biblioteca/Home) — aqui só se lê o cache
            // pela chave, sem bater no servidor de novo (tela precisa
            // funcionar offline).
            item.Cover = await CoverCacheService.GetCachedImageIfExistsAsync(item.Record.CoverCacheKey);
        }

        private async void Delete_Click(object sender, RoutedEventArgs e)
        {
            BookRecordItemViewModel item = (BookRecordItemViewModel)((Button)sender).Tag;
            await DownloadService.DeleteAsync(item.Record.Id, item.Record.Format);
            await LibraryDataStore.DeleteAsync(item.Record.Id);
            await RefreshAsync();
        }

        private void BooksList_ItemClick(object sender, ItemClickEventArgs e)
        {
            BookRecordItemViewModel item = (BookRecordItemViewModel)e.ClickedItem;
            if (ReaderNavigation.TryOpen(Frame, item.Record))
            {
                return;
            }

            Flyout flyout = new Flyout
            {
                Content = new TextBlock
                {
                    Text = "Leitor desse formato ainda não está pronto nesta versão.",
                    TextWrapping = TextWrapping.Wrap,
                    MaxWidth = 240,
                },
            };
            flyout.ShowAt(BooksList);
        }
    }
}
