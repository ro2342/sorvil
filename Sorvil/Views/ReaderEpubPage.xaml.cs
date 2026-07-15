using Sorvil.Models;
using Sorvil.Services;
using System;
using System.Threading.Tasks;
using Windows.Storage;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Input;
using Windows.UI.Xaml.Navigation;

namespace Sorvil.Views
{
    // Leitor de EPUB/KEPUB com paginação real de e-reader (tipo Kindle/
    // Freda), não uma página de web corrida. A técnica: injeta CSS que
    // transforma o <body> do capítulo em colunas do tamanho exato do
    // WebView (column-width = largura do WebView, column-gap 0) — o
    // motor de renderização já quebra o texto em "páginas" sozinho. Virar
    // página é só aplicar um transform: translateX deslocando o conteúdo
    // uma tela inteira pro lado (sem rolagem nativa visível, controle
    // total via script). Zonas de toque nas laterais fazem o gesto
    // "tocar pra virar página"; a casca (Anterior/Próximo/Aa) continua
    // funcionando pra quem preferir botão.
    //
    // KEPUB é tratado como EPUB comum — os <span> extras da Kobo não
    // atrapalham a paginação. O EPUB só é descompactado (custo real) na
    // primeira abertura — EpubExtractor já cacheia isso em
    // BooksExtracted/, então reabrir o mesmo livro é rápido.
    public sealed partial class ReaderEpubPage : Page
    {
        private string _bookId;
        private string _folderName;
        private EpubManifest _manifest;
        private int _chapterIndex;
        private int _pageIndexInChapter;
        private int _totalPagesInChapter = 1;
        private int? _pendingStartPage;
        private bool _chromeVisible = true;

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

                int startChapter = 0;
                int startPage = 0;
                string[] parts = (record.ReadingPositionJson ?? string.Empty).Split(':');
                if (parts.Length >= 1)
                {
                    int.TryParse(parts[0], out startChapter);
                }
                if (parts.Length >= 2)
                {
                    int.TryParse(parts[1], out startPage);
                }
                if (startChapter < 0 || startChapter >= _manifest.SpineFiles.Count)
                {
                    startChapter = 0;
                    startPage = 0;
                }

                // Não desliga o indicador aqui — ContentWebView_NavigationCompleted
                // cuida disso quando o capítulo realmente terminar de renderizar.
                await NavigateToChapterAsync(startChapter, startPage);
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

        // startPage: null = primeira página, -1 = última página (entrando
        // de trás pra frente, ex.: botão Anterior no início de um capítulo).
        private async Task NavigateToChapterAsync(int chapterIndex, int? startPage)
        {
            _chapterIndex = chapterIndex;
            _pendingStartPage = startPage;
            LoadingRing.IsActive = true;
            ChapterIndicatorText.Text = "Carregando capítulo...";
            Uri uri = EpubExtractor.BuildLocalContentUri(_folderName, _manifest.SpineFiles[chapterIndex]);
            ContentWebView.Navigate(uri);
        }

        private async void ContentWebView_NavigationCompleted(WebView sender, WebViewNavigationCompletedEventArgs args)
        {
            await ApplyReaderStyleAsync();
            _totalPagesInChapter = await GetTotalPagesAsync();

            int targetPage;
            if (_pendingStartPage == null)
            {
                targetPage = 0;
            }
            else if (_pendingStartPage.Value < 0)
            {
                targetPage = _totalPagesInChapter - 1;
            }
            else
            {
                targetPage = Math.Min(_pendingStartPage.Value, _totalPagesInChapter - 1);
            }
            _pendingStartPage = null;

            await GoToPageAsync(targetPage);
            UpdateIndicator();
            await SavePositionAsync();

            LoadingRing.IsActive = false;
        }

        private void ContentWebView_NavigationFailed(object sender, WebViewNavigationFailedEventArgs args)
        {
            LoadingRing.IsActive = false;
            ChapterIndicatorText.Text = "Erro ao carregar o capítulo.";
        }

        private async void ContentWebView_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            // Rotação de tela ou primeira medição de layout — reaplica a
            // paginação pro tamanho novo. Como isso é raro num telefone
            // (orientação fixa na prática), não tenta preservar a posição
            // exata: só reancora no início do capítulo atual.
            if (_manifest == null || e.NewSize.Width <= 0 || e.NewSize.Height <= 0)
            {
                return;
            }
            await ApplyReaderStyleAsync();
            _totalPagesInChapter = await GetTotalPagesAsync();
            await GoToPageAsync(0);
            UpdateIndicator();
        }

        private void UpdateIndicator()
        {
            if (_manifest == null)
            {
                return;
            }
            ChapterIndicatorText.Text = "Capítulo " + (_chapterIndex + 1) + "/" + _manifest.SpineFiles.Count +
                " · página " + (_pageIndexInChapter + 1) + "/" + _totalPagesInChapter;
        }

        private async Task SavePositionAsync()
        {
            BookRecord record = await LibraryDataStore.GetAsync(_bookId);
            if (record == null)
            {
                return;
            }
            record.ReadingPositionJson = _chapterIndex + ":" + _pageIndexInChapter;
            record.LastOpenedAt = DateTimeOffset.UtcNow.ToString("o");
            await LibraryDataStore.SaveAsync(record);
        }

        // — navegação de página (usada pelos botões e pelas zonas de toque) —

        private async Task GoToNextAsync()
        {
            if (_pageIndexInChapter < _totalPagesInChapter - 1)
            {
                await GoToPageAsync(_pageIndexInChapter + 1);
                UpdateIndicator();
                await SavePositionAsync();
            }
            else if (_manifest != null && _chapterIndex < _manifest.SpineFiles.Count - 1)
            {
                await NavigateToChapterAsync(_chapterIndex + 1, null);
            }
        }

        private async Task GoToPreviousAsync()
        {
            if (_pageIndexInChapter > 0)
            {
                await GoToPageAsync(_pageIndexInChapter - 1);
                UpdateIndicator();
                await SavePositionAsync();
            }
            else if (_manifest != null && _chapterIndex > 0)
            {
                await NavigateToChapterAsync(_chapterIndex - 1, -1);
            }
        }

        private async void PreviousButton_Click(object sender, RoutedEventArgs e)
        {
            await GoToPreviousAsync();
        }

        private async void NextButton_Click(object sender, RoutedEventArgs e)
        {
            await GoToNextAsync();
        }

        private async void PreviousZone_Tapped(object sender, TappedRoutedEventArgs e)
        {
            await GoToPreviousAsync();
        }

        private async void NextZone_Tapped(object sender, TappedRoutedEventArgs e)
        {
            await GoToNextAsync();
        }

        private void ToggleChrome_Tapped(object sender, TappedRoutedEventArgs e)
        {
            // Opacity em vez de Visibility.Collapsed — a linha continua
            // reservada no layout, então o WebView não muda de tamanho (o
            // que invalidaria a paginação) só por causa do toque no meio.
            _chromeVisible = !_chromeVisible;
            BottomBar.Opacity = _chromeVisible ? 1 : 0;
            BottomBar.IsHitTestVisible = _chromeVisible;
        }

        // — scripts de paginação —

        private async Task<string> InvokeAsync(string script)
        {
            try
            {
                return await ContentWebView.InvokeScriptAsync("eval", new[] { script });
            }
            catch (Exception)
            {
                return null;
            }
        }

        private async Task<int> GetTotalPagesAsync()
        {
            string result = await InvokeAsync(
                "(function() { return Math.max(1, Math.ceil(document.body.scrollWidth / document.documentElement.clientWidth)); })();");
            int pages;
            return int.TryParse(result, out pages) && pages > 0 ? pages : 1;
        }

        private async Task GoToPageAsync(int pageIndex)
        {
            _pageIndexInChapter = pageIndex;
            string script =
                "(function() { document.body.style.transform = 'translateX(-' + (" + pageIndex +
                " * document.documentElement.clientWidth) + 'px)'; })();";
            await InvokeAsync(script);
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
                await ReapplyStyleAndRepaginateAsync();
            };
            return button;
        }

        private async Task AdjustFontSizeAsync(int delta)
        {
            int current = ReaderPreferenceStore.GetFontSizePercent();
            int updated = Math.Max(70, Math.Min(250, current + delta));
            ReaderPreferenceStore.SetFontSizePercent(updated);
            await ReapplyStyleAndRepaginateAsync();
        }

        // Mudar fonte/tema muda quantas páginas cabem no capítulo — por
        // isso repagina do zero (volta pra página 0) em vez de só reaplicar
        // o CSS mantendo o índice de página antigo, que ficaria errado.
        private async Task ReapplyStyleAndRepaginateAsync()
        {
            await ApplyReaderStyleAsync();
            _totalPagesInChapter = await GetTotalPagesAsync();
            await GoToPageAsync(0);
            UpdateIndicator();
            await SavePositionAsync();
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

            double pageWidth = ContentWebView.ActualWidth;
            double pageHeight = ContentWebView.ActualHeight;
            if (pageWidth <= 0)
            {
                pageWidth = 400;
            }
            if (pageHeight <= 0)
            {
                pageHeight = 600;
            }

            // column-width faz o próprio motor de renderização quebrar o
            // texto em "páginas" do tamanho do WebView, lado a lado —
            // GoToPageAsync desloca esse conteúdo via transform em vez de
            // rolagem nativa (sem barra de rolagem visível, controle
            // exato). Sem padding no body de propósito — o respiro visual
            // já vem da margem do WebView em XAML, evitando qualquer
            // ambiguidade de box-model com column-width.
            string script =
                "(function() {" +
                "var style = document.getElementById('sorvil-reader-style');" +
                "if (!style) { style = document.createElement('style'); style.id = 'sorvil-reader-style'; document.head.appendChild(style); }" +
                "style.innerHTML = " +
                "'html { margin:0 !important; padding:0 !important; overflow:hidden !important; } " +
                "body { " +
                "margin:0 !important; padding:0 !important; " +
                "font-size: " + fontSize + "% !important; " +
                "background-color: " + background + " !important; " +
                "line-height: 1.5 !important; " +
                "height: " + (int)pageHeight + "px !important; " +
                "column-width: " + (int)pageWidth + "px !important; " +
                "column-gap: 0px !important; " +
                "column-fill: auto !important; " +
                "overflow: hidden !important; " +
                "transition: none !important; " +
                "} " +
                "* { color: " + foreground + " !important; } " +
                "img, table { max-width: 100% !important; height: auto !important; }';" +
                "})();";

            await InvokeAsync(script);
        }
    }
}
