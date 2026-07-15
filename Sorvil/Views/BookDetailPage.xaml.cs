using Sorvil.Models;
using Sorvil.Services;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Windows.Storage;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Navigation;

namespace Sorvil.Views
{
    // Detalhe de um livro do catálogo: capa, metadados e um botão por
    // formato disponível pra baixar (ou apagar, se já baixado). Abrir o
    // leitor de verdade (EPUB/KEPUB via WebView, PDF via Windows.Data.Pdf)
    // chega nas próximas duas levas — por enquanto o download já deixa o
    // arquivo pronto no aparelho, visível em Baixados.
    public sealed partial class BookDetailPage : Page
    {
        public sealed class FormatOption
        {
            public OpdsAcquisitionLink Link { get; }
            public string Extension { get; }
            public string ButtonLabel { get; set; }

            public FormatOption(OpdsAcquisitionLink link, string extension, string label)
            {
                Link = link;
                Extension = extension;
                ButtonLabel = label;
            }
        }

        private OpdsEntry _entry;

        public BookDetailPage()
        {
            this.InitializeComponent();
        }

        protected override async void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);
            _entry = e.Parameter as OpdsEntry;
            if (_entry == null)
            {
                return;
            }

            TitleText.Text = _entry.Title;
            AuthorText.Text = _entry.Author;
            SummaryText.Text = _entry.Summary;

            await RefreshFormatOptionsAsync();

            Uri coverUri = _entry.ThumbnailUri ?? _entry.ImageUri;
            if (coverUri != null)
            {
                CoverImage.Source = await CoverCacheService.GetOrDownloadImageAsync(coverUri, _entry.Id ?? coverUri.ToString());
            }
        }

        private string RecordId(string extension)
        {
            return (_entry.Id ?? _entry.Title) + ":" + extension;
        }

        private async Task RefreshFormatOptionsAsync()
        {
            List<FormatOption> options = new List<FormatOption>();
            foreach (OpdsAcquisitionLink link in _entry.Acquisitions)
            {
                string extension = DownloadService.GuessExtension(link.MimeType);
                BookRecord existing = await LibraryDataStore.GetAsync(RecordId(extension));
                string label = existing != null
                    ? "Baixado (" + extension + ") — toque pra apagar"
                    : "Baixar (" + extension + ")";
                options.Add(new FormatOption(link, extension, label));
            }
            FormatsList.ItemsSource = options;
        }

        private async void FormatButton_Click(object sender, RoutedEventArgs e)
        {
            Button button = (Button)sender;
            FormatOption option = (FormatOption)button.Tag;
            string recordId = RecordId(option.Extension);

            BookRecord existing = await LibraryDataStore.GetAsync(recordId);
            if (existing != null)
            {
                await DownloadService.DeleteAsync(recordId, option.Extension);
                await LibraryDataStore.DeleteAsync(recordId);
                StatusText.Text = "Apagado.";
                await RefreshFormatOptionsAsync();
                return;
            }

            button.IsEnabled = false;
            DownloadProgress.Visibility = Visibility.Visible;
            DownloadProgress.Value = 0;
            StatusText.Text = "Baixando...";

            try
            {
                Progress<double> progress = new Progress<double>(value => DownloadProgress.Value = value);
                StorageFile file = await DownloadService.DownloadAsync(recordId, option.Link.Href, option.Extension, progress);

                BookRecord record = new BookRecord
                {
                    Id = recordId,
                    Title = _entry.Title,
                    Author = _entry.Author,
                    Format = option.Extension,
                    LocalFilePath = file.Name,
                    CoverCacheKey = _entry.Id,
                };
                await LibraryDataStore.SaveAsync(record);

                StatusText.Text = "Baixado! Abrir o leitor chega numa próxima leva — o arquivo já está salvo (vê em Baixados).";
            }
            catch (Exception ex)
            {
                StatusText.Text = "Erro no download: " + ex.Message;
            }
            finally
            {
                button.IsEnabled = true;
                DownloadProgress.Visibility = Visibility.Collapsed;
            }

            await RefreshFormatOptionsAsync();
        }
    }
}
