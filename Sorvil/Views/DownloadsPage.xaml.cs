using Sorvil.Models;
using Sorvil.Services;
using System.Collections.Generic;
using System.ComponentModel;
using System.Threading.Tasks;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Media;

namespace Sorvil.Views
{
    public sealed partial class DownloadsPage : Page
    {
        public sealed class DownloadedItemViewModel : INotifyPropertyChanged
        {
            public BookRecord Record { get; }
            public string Title => Record.Title;
            public string Subtitle => (Record.Author ?? string.Empty) + " · " + Record.Format;

            private ImageSource _cover;
            public ImageSource Cover
            {
                get { return _cover; }
                set
                {
                    _cover = value;
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Cover"));
                }
            }

            public DownloadedItemViewModel(BookRecord record)
            {
                Record = record;
            }

            public event PropertyChangedEventHandler PropertyChanged;
        }

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
            List<DownloadedItemViewModel> items = new List<DownloadedItemViewModel>();
            foreach (BookRecord record in records)
            {
                items.Add(new DownloadedItemViewModel(record));
            }

            BooksList.ItemsSource = items;
            EmptyText.Visibility = items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            BooksList.Visibility = items.Count == 0 ? Visibility.Collapsed : Visibility.Visible;

            foreach (DownloadedItemViewModel item in items)
            {
                LoadCoverAsync(item);
            }
        }

        private async void LoadCoverAsync(DownloadedItemViewModel item)
        {
            // A capa já devia estar em cache local (foi baixada quando o
            // livro apareceu na Biblioteca/Home) — aqui só se lê o cache
            // pela chave, sem bater no servidor de novo (tela precisa
            // funcionar offline).
            item.Cover = await CoverCacheService.GetCachedImageIfExistsAsync(item.Record.CoverCacheKey);
        }

        private async void Delete_Click(object sender, RoutedEventArgs e)
        {
            DownloadedItemViewModel item = (DownloadedItemViewModel)((Button)sender).Tag;
            await DownloadService.DeleteAsync(item.Record.Id, item.Record.Format);
            await LibraryDataStore.DeleteAsync(item.Record.Id);
            await RefreshAsync();
        }

        private void BooksList_ItemClick(object sender, ItemClickEventArgs e)
        {
            DownloadedItemViewModel item = (DownloadedItemViewModel)e.ClickedItem;
            if (item.Record.Format == "pdf")
            {
                Frame.Navigate(typeof(ReaderPdfPage), item.Record.Id);
                return;
            }
            if (item.Record.Format == "epub" || item.Record.Format == "kepub.epub")
            {
                Frame.Navigate(typeof(ReaderEpubPage), item.Record.Id);
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
