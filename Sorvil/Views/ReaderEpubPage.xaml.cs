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
    // capítulo, indicador, ajustes de leitura), mas o conteúdo do
    // capítulo — que é HTML/CSS por natureza do formato — roda num
    // WebView nativo do UWP. KEPUB é tratado como EPUB comum (as
    // marcações extras da Kobo não atrapalham renderização normal).
    // Navegação é por capítulo inteiro (sem rolagem fina rastreada) —
    // posição salva é só o índice do capítulo atual.
    //
    // O EPUB só é descompactado (custo real) na primeira abertura —
    // EpubExtractor já cacheia o resultado em BooksExtracted/, então
    // reabrir o mesmo livro é rápido. O indicador de carregamento só
    // desliga quando o WebView termina de renderizar o capítulo
    // (NavigationCompleted), não antes — senão a tela fica em branco sem
    // feedback nenhum durante o tempo de extração/renderização.
    //
    // Tamanho de fonte e tema de leitura (Claro/Sépia/Escuro) são
    // aplicados injetando um <style> no documento já carregado via
    // WebView.InvokeScriptAsync — o livro não tem controle nenhum
    // sobre esses estilos por padrão, então sem isso o texto sai do
    // tamanho que o CSS do próprio EPUB definir (geralmente pensado pra
    // tela grande, ficando minúsculo num telefone).
    public sealed partial class ReaderEpubPage : Page
    {
        private string _bookId;
        private string _folderName;
        private EpubManifest _manifest;
        private int _chapterIndex;

        public ReaderEpubPage()
        {
            this.InitializeComponent();
            ContentWebView.NavigationCompleted += ContentWebView_NavigationCompleted;
            ContentWebView.NavigationFailed += ContentWebView_NavigationFailed;
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
            ChapterIndicatorText.Text = "Abrindo...";

            try
            {
                BookRecord record = await LibraryDataStore.GetAsync(_bookId);
                if (record == null)
                {
                    ShowLoadError("Livro não encontrado.");
                    return;
                }

                StorageFolder booksFolder = await ApplicationData.Current.LocalFolder.GetFolderAsync("Books");
                StorageFile epubFile = await booksFolder.GetFileAsync(record.LocalFilePath);

                ChapterIndicatorText.Text = "Extraindo...";
                _manifest = await EpubExtractor.ExtractAndParseAsync(_bookId, epubFile);
                _folderName = EpubExtractor.GetExtractedFolderName(_bookId);

                if (_manifest.SpineFiles.Count == 0)
                {
                    ShowLoadError("Não consegui ler o índice deste EPUB.");
                    return;
                }

                int startIndex = 0;
                int.TryParse(record.ReadingPositionJson, out startIndex);
                if (startIndex < 0 || startIndex >= _manifest.SpineFiles.Count)
                {
                    startIndex = 0;
                }

                // Não desliga o indicador aqui — ContentWebView_NavigationCompleted
                // cuida disso quando o capítulo realmente terminar de renderizar.
                await NavigateToChapterAsync(startIndex);
            }
            catch (Exception ex)
            {
                ShowLoadError("Erro ao abrir o EPUB: " + ex.Message);
            }
        }

        private void ShowLoadError(string message)
        {
            LoadingRing.IsActive = false;
            ChapterIndicatorText.Text = message;
        }

        private async Task NavigateToChapterAsync(int index)
        {
            _chapterIndex = index;
            LoadingRing.IsActive = true;
            ChapterIndicatorText.Text = "Carregando capítulo...";
            Uri uri = EpubExtractor.BuildLocalContentUri(_folderName, _manifest.SpineFiles[index]);
            ContentWebView.Navigate(uri);
            await SavePositionAsync(index);
        }

        private async void ContentWebView_NavigationCompleted(WebView sender, WebViewNavigationCompletedEventArgs args)
        {
            LoadingRing.IsActive = false;
            if (_manifest != null)
            {
                ChapterIndicatorText.Text = "Capítulo " + (_chapterIndex + 1) + " / " + _manifest.SpineFiles.Count;
            }
            await ApplyReaderStyleAsync();
        }

        private void ContentWebView_NavigationFailed(object sender, WebViewNavigationFailedEventArgs args)
        {
            LoadingRing.IsActive = false;
            ChapterIndicatorText.Text = "Erro ao carregar o capítulo.";
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

        // — ajustes de leitura (fonte + tema) —

        private void ReaderSettings_Click(object sender, RoutedEventArgs e)
        {
            StackPanel panel = new StackPanel { Padding = new Thickness(16), Width = 240 };

            panel.Children.Add(new TextBlock { Text = "Tema de leitura", Margin = new Thickness(0, 0, 0, 8) });

            StackPanel themeRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 20) };
            themeRow.Children.Add(CreateThemeButton("Claro", "light"));
            themeRow.Children.Add(CreateThemeButton("Sépia", "sepia"));
            themeRow.Children.Add(CreateThemeButton("Escuro", "dark"));
            panel.Children.Add(themeRow);

            panel.Children.Add(new TextBlock { Text = "Tamanho da fonte", Margin = new Thickness(0, 0, 0, 8) });

            StackPanel fontRow = new StackPanel { Orientation = Orientation.Horizontal };
            Button smallerButton = new Button { Content = "A-", Margin = new Thickness(0, 0, 8, 0) };
            smallerButton.Click += async (fontSender, fontArgs) => await AdjustFontSizeAsync(-10);
            Button biggerButton = new Button { Content = "A+" };
            biggerButton.Click += async (fontSender, fontArgs) => await AdjustFontSizeAsync(10);
            fontRow.Children.Add(smallerButton);
            fontRow.Children.Add(biggerButton);
            panel.Children.Add(fontRow);

            Flyout flyout = new Flyout { Content = panel };
            flyout.ShowAt(ReaderSettingsButton);
        }

        private Button CreateThemeButton(string label, string themeKey)
        {
            Button button = new Button { Content = label, Margin = new Thickness(0, 0, 8, 0) };
            button.Click += async (sender, args) =>
            {
                ReaderPreferenceStore.SetTheme(themeKey);
                await ApplyReaderStyleAsync();
            };
            return button;
        }

        private async Task AdjustFontSizeAsync(int delta)
        {
            int current = ReaderPreferenceStore.GetFontSizePercent();
            int updated = Math.Max(70, Math.Min(250, current + delta));
            ReaderPreferenceStore.SetFontSizePercent(updated);
            await ApplyReaderStyleAsync();
        }

        private async Task ApplyReaderStyleAsync()
        {
            int fontSize = ReaderPreferenceStore.GetFontSizePercent();
            string theme = ReaderPreferenceStore.GetTheme();

            string background;
            string foreground;
            switch (theme)
            {
                case "sepia":
                    background = "#f4ecd8";
                    foreground = "#5b4636";
                    break;
                case "dark":
                    background = "#1b1b1f";
                    foreground = "#e8e8ea";
                    break;
                default:
                    background = "#ffffff";
                    foreground = "#1b1b1f";
                    break;
            }

            string script =
                "(function() {" +
                "var style = document.getElementById('sorvil-reader-style');" +
                "if (!style) { style = document.createElement('style'); style.id = 'sorvil-reader-style'; document.head.appendChild(style); }" +
                "style.innerHTML = 'html, body { font-size: " + fontSize + "% !important; background-color: " + background + " !important; line-height: 1.5 !important; } " +
                "body { padding: 16px !important; box-sizing: border-box !important; } " +
                "* { color: " + foreground + " !important; } " +
                "img, table { max-width: 100% !important; height: auto !important; }';" +
                "})();";

            try
            {
                await ContentWebView.InvokeScriptAsync("eval", new[] { script });
            }
            catch (Exception)
            {
                // WebView pode não ter documento carregado ainda (ex.:
                // chamado logo após um NavigationFailed) — sem problema,
                // o próximo NavigationCompleted reaplica.
            }
        }
    }
}
