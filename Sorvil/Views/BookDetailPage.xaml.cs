using Sorvil.Models;
using Sorvil.Services;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Threading.Tasks;
using Windows.Storage;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Navigation;

namespace Sorvil.Views
{
    // Detalhe de um livro do catálogo: capa, metadados e um botão por
    // formato disponível — "Baixar" se ainda não tem, "Abrir" se já tem e
    // o formato tem leitor pronto (PDF; EPUB/KEPUB chegam na próxima
    // leva), "Apagar" ao lado pra remover o que já foi baixado.
    public sealed partial class BookDetailPage : Page
    {
        public sealed class FormatOption : INotifyPropertyChanged
        {
            public OpdsAcquisitionLink Link { get; }
            public string Extension { get; }
            public bool IsReadable { get; }

            private bool _isDownloaded;
            public bool IsDownloaded
            {
                get { return _isDownloaded; }
                set
                {
                    _isDownloaded = value;
                    Raise("IsDownloaded");
                    Raise("PrimaryLabel");
                    Raise("DeleteVisibility");
                }
            }

            public string PrimaryLabel
            {
                get
                {
                    if (!IsDownloaded)
                    {
                        return "Baixar (" + Extension + ")";
                    }
                    return IsReadable ? "Abrir (" + Extension + ")" : "Baixado (" + Extension + ")";
                }
            }

            public Visibility DeleteVisibility => IsDownloaded ? Visibility.Visible : Visibility.Collapsed;

            public FormatOption(OpdsAcquisitionLink link, string extension, bool isReadable, bool isDownloaded)
            {
                Link = link;
                Extension = extension;
                IsReadable = isReadable;
                _isDownloaded = isDownloaded;
            }

            public event PropertyChangedEventHandler PropertyChanged;

            private void Raise(string propertyName)
            {
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
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

        // Só PDF tem leitor pronto por enquanto — EPUB/KEPUB entram aqui
        // assim que o leitor deles (WebView) existir.
        private static bool IsReadableFormat(string extension)
        {
            return extension == "pdf";
        }

        private async Task RefreshFormatOptionsAsync()
        {
            List<FormatOption> options = new List<FormatOption>();
            foreach (OpdsAcquisitionLink link in _entry.Acquisitions)
            {
                string extension = DownloadService.GuessExtension(link.MimeType);
                BookRecord existing = await LibraryDataStore.GetAsync(RecordId(extension));
                options.Add(new FormatOption(link, extension, IsReadableFormat(extension), existing != null));
            }
            FormatsList.ItemsSource = options;
        }

        private async void PrimaryButton_Click(object sender, RoutedEventArgs e)
        {
            FormatOption option = (FormatOption)((Button)sender).Tag;

            if (!option.IsDownloaded)
            {
                await DownloadFormatAsync(option, (Button)sender);
                return;
            }

            if (option.IsReadable)
            {
                OpenReader(RecordId(option.Extension), option.Extension);
            }
        }

        private async void DeleteButton_Click(object sender, RoutedEventArgs e)
        {
            FormatOption option = (FormatOption)((Button)sender).Tag;
            string recordId = RecordId(option.Extension);

            await DownloadService.DeleteAsync(recordId, option.Extension);
            await LibraryDataStore.DeleteAsync(recordId);
            option.IsDownloaded = false;
            StatusText.Text = "Apagado.";
        }

        private void OpenReader(string recordId, string extension)
        {
            if (extension == "pdf")
            {
                Frame.Navigate(typeof(ReaderPdfPage), recordId);
            }
            else
            {
                StatusText.Text = "Leitor desse formato ainda não está pronto nesta versão.";
            }
        }

        private async Task DownloadFormatAsync(FormatOption option, Button button)
        {
            string recordId = RecordId(option.Extension);

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
                option.IsDownloaded = true;

                StatusText.Text = option.IsReadable
                    ? "Baixado! Toque em \"Abrir\" pra começar a ler."
                    : "Baixado! Esse formato ainda não tem leitor nesta versão — o arquivo já está salvo (vê em Baixados).";
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
        }
    }
}
