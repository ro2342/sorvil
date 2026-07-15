using Sorvil.Models;
using Sorvil.Services;
using System;
using System.Threading.Tasks;
using Windows.Storage;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Navigation;

namespace Sorvil.Views
{
    // Leitor de EPUB/KEPUB: casca 100% nativa (esta página, navegação por
    // capítulo, indicador), mas o conteúdo do capítulo — que é HTML/CSS
    // por natureza do formato — roda num WebView nativo do UWP. KEPUB é
    // tratado como EPUB comum (as marcações extras da Kobo não atrapalham
    // renderização normal). Navegação é por capítulo inteiro (sem rolagem
    // fina rastreada) — posição salva é só o índice do capítulo atual.
    public sealed partial class ReaderEpubPage : Page
    {
        private string _bookId;
        private string _folderName;
        private EpubManifest _manifest;
        private int _chapterIndex;

        public ReaderEpubPage()
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
                    ChapterIndicatorText.Text = "Livro não encontrado.";
                    return;
                }

                StorageFolder booksFolder = await ApplicationData.Current.LocalFolder.GetFolderAsync("Books");
                StorageFile epubFile = await booksFolder.GetFileAsync(record.LocalFilePath);

                _manifest = await EpubExtractor.ExtractAndParseAsync(_bookId, epubFile);
                _folderName = EpubExtractor.GetExtractedFolderName(_bookId);

                if (_manifest.SpineFiles.Count == 0)
                {
                    ChapterIndicatorText.Text = "Não consegui ler o índice deste EPUB.";
                    return;
                }

                int startIndex = 0;
                int.TryParse(record.ReadingPositionJson, out startIndex);
                if (startIndex < 0 || startIndex >= _manifest.SpineFiles.Count)
                {
                    startIndex = 0;
                }

                await NavigateToChapterAsync(startIndex);
            }
            catch (Exception ex)
            {
                ChapterIndicatorText.Text = "Erro ao abrir o EPUB: " + ex.Message;
            }
            finally
            {
                LoadingRing.IsActive = false;
            }
        }

        private async Task NavigateToChapterAsync(int index)
        {
            _chapterIndex = index;
            Uri uri = EpubExtractor.BuildLocalContentUri(_folderName, _manifest.SpineFiles[index]);
            ContentWebView.Navigate(uri);
            ChapterIndicatorText.Text = "Capítulo " + (index + 1) + " / " + _manifest.SpineFiles.Count;
            await SavePositionAsync(index);
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

        private async void PreviousButton_Click(object sender, RoutedEventArgs e)
        {
            if (_manifest != null && _chapterIndex > 0)
            {
                await NavigateToChapterAsync(_chapterIndex - 1);
            }
        }

        private async void NextButton_Click(object sender, RoutedEventArgs e)
        {
            if (_manifest != null && _chapterIndex < _manifest.SpineFiles.Count - 1)
            {
                await NavigateToChapterAsync(_chapterIndex + 1);
            }
        }
    }
}
