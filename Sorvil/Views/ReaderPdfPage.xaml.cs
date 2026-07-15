using Sorvil.Models;
using Sorvil.Services;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Threading.Tasks;
using Windows.Data.Pdf;
using Windows.Storage;
using Windows.Storage.Streams;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Media;
using Windows.UI.Xaml.Media.Imaging;
using Windows.UI.Xaml.Navigation;

namespace Sorvil.Views
{
    // Leitor de PDF 100% nativo via Windows.Data.Pdf — sem WebView aqui,
    // já que PDF é formato de página fixa (a UWP já tem API pronta pra
    // isso). Só renderiza a página atual +/-1 por vez e libera as demais
    // — fundamental num aparelho com pouca RAM como o Lumia, num PDF que
    // pode ter centenas de páginas.
    public sealed partial class ReaderPdfPage : Page
    {
        public sealed class PdfPageViewModel : INotifyPropertyChanged
        {
            public int Index { get; }

            private ImageSource _image;
            public ImageSource Image
            {
                get { return _image; }
                set
                {
                    _image = value;
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Image"));
                }
            }

            public PdfPageViewModel(int index)
            {
                Index = index;
            }

            public event PropertyChangedEventHandler PropertyChanged;
        }

        private string _bookId;
        private PdfDocument _document;
        private List<PdfPageViewModel> _pages;

        public ReaderPdfPage()
        {
            this.InitializeComponent();
        }

        protected override async void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);
            _bookId = e.Parameter as string;
            if (string.IsNullOrEmpty(_bookId))
            {
                return;
            }

            LoadingRing.IsActive = true;
            try
            {
                BookRecord record = await LibraryDataStore.GetAsync(_bookId);
                if (record == null)
                {
                    return;
                }

                StorageFolder folder = await ApplicationData.Current.LocalFolder.GetFolderAsync("Books");
                StorageFile file = await folder.GetFileAsync(record.LocalFilePath);
                _document = await PdfDocument.LoadFromFileAsync(file);

                _pages = new List<PdfPageViewModel>();
                for (uint i = 0; i < _document.PageCount; i++)
                {
                    _pages.Add(new PdfPageViewModel((int)i));
                }
                PagesFlipView.ItemsSource = _pages;

                int startIndex = 0;
                int.TryParse(record.ReadingPositionJson, out startIndex);
                if (startIndex < 0 || startIndex >= _pages.Count)
                {
                    startIndex = 0;
                }
                PagesFlipView.SelectedIndex = startIndex;

                await RenderAroundAsync(startIndex);
                UpdateIndicator(startIndex);
            }
            finally
            {
                LoadingRing.IsActive = false;
            }
        }

        private void PagesFlipView_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            int index = PagesFlipView.SelectedIndex;
            if (index < 0)
            {
                return;
            }
            UpdateIndicator(index);
            RenderAroundAsync(index);
            SavePositionAsync(index);
        }

        private void UpdateIndicator(int index)
        {
            if (_pages == null)
            {
                return;
            }
            PageIndicatorText.Text = (index + 1) + " / " + _pages.Count;
        }

        private async Task SavePositionAsync(int index)
        {
            BookRecord record = await LibraryDataStore.GetAsync(_bookId);
            if (record == null)
            {
                return;
            }
            record.ReadingPositionJson = index.ToString();
            record.LastOpenedAt = DateTimeOffset.UtcNow.ToString("o");
            await LibraryDataStore.SaveAsync(record);
        }

        private async Task RenderAroundAsync(int centerIndex)
        {
            if (_document == null || _pages == null)
            {
                return;
            }

            int from = Math.Max(0, centerIndex - 1);
            int to = Math.Min(_pages.Count - 1, centerIndex + 1);

            for (int i = from; i <= to; i++)
            {
                if (_pages[i].Image == null)
                {
                    _pages[i].Image = await RenderPageAsync(i);
                }
            }

            for (int i = 0; i < _pages.Count; i++)
            {
                if ((i < from || i > to) && _pages[i].Image != null)
                {
                    _pages[i].Image = null;
                }
            }
        }

        private async Task<BitmapImage> RenderPageAsync(int index)
        {
            using (PdfPage page = _document.GetPage((uint)index))
            {
                using (InMemoryRandomAccessStream stream = new InMemoryRandomAccessStream())
                {
                    await page.RenderToStreamAsync(stream);
                    BitmapImage bitmap = new BitmapImage();
                    await bitmap.SetSourceAsync(stream);
                    return bitmap;
                }
            }
        }
    }
}
