using Sorvil.Models;
using Sorvil.Services;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Windows.Storage;
using Windows.UI.Text;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Input;
using Windows.UI.Xaml.Media;
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
        private bool _chromeVisible;
        private Flyout _tocFlyout;
        private BookRecord _record;

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
                _record = await LibraryDataStore.GetAsync(_bookId);
                if (_record == null)
                {
                    ShowLoadError("Livro não encontrado.");
                    return;
                }

                BookTitleText.Text = _record.Title;
                BookAuthorText.Text = _record.Author;
                ApplyDimLevel();

                StorageFolder booksFolder = await ApplicationData.Current.LocalFolder.GetFolderAsync("Books");
                StorageFile epubFile = await booksFolder.GetFileAsync(_record.LocalFilePath);

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
                string[] parts = (_record.ReadingPositionJson ?? string.Empty).Split(':');
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

        // Progresso é uma aproximação por capítulo, não por bytes de
        // verdade — capítulos variam muito de tamanho, então "~X%" é o
        // mais honesto de mostrar sem pré-carregar e medir o livro inteiro
        // (caro, e a paginação já muda sozinha se a fonte/tema mudar).
        private void UpdateIndicator()
        {
            if (_manifest == null || _manifest.SpineFiles.Count == 0)
            {
                return;
            }

            double withinChapter = _totalPagesInChapter > 0
                ? (double)(_pageIndexInChapter + 1) / _totalPagesInChapter
                : 0;
            double approxPercent = (_chapterIndex + withinChapter) / _manifest.SpineFiles.Count * 100.0;

            ChapterIndicatorText.Text = "Cap. " + (_chapterIndex + 1) + "/" + _manifest.SpineFiles.Count +
                " · pág " + (_pageIndexInChapter + 1) + "/" + _totalPagesInChapter +
                " · ~" + Math.Round(approxPercent) + "%";
            ReadingProgressBar.Value = approxPercent;
        }

        // — índice (sumário) —

        private void TocButton_Click(object sender, RoutedEventArgs e)
        {
            if (_manifest == null)
            {
                return;
            }

            StackPanel panel = new StackPanel { Width = 260 };
            panel.Children.Add(new TextBlock
            {
                Text = _record != null ? _record.Title : "Índice",
                FontWeight = FontWeights.SemiBold,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(16, 12, 16, 8),
            });

            if (_manifest.Toc.Count == 0)
            {
                panel.Children.Add(new TextBlock
                {
                    Text = "Este EPUB não tem um índice (toc.ncx) que eu consiga ler.",
                    TextWrapping = TextWrapping.Wrap,
                    MaxWidth = 228,
                    Margin = new Thickness(16, 0, 16, 12),
                });
                Flyout emptyFlyout = new Flyout { Content = panel };
                emptyFlyout.ShowAt(TocButton);
                return;
            }

            // "Capítulo atual" é a entrada do índice com o maior SpineIndex
            // que ainda não passou do capítulo aberto — o NCX não tem uma
            // entrada pra cada arquivo do spine (um capítulo de verdade às
            // vezes vira vários arquivos internos), então é o "mais próximo
            // por baixo" que representa onde a leitura está.
            int currentTocIndex = FindCurrentTocIndex();
            SolidColorBrush accent = ThemeHelper.AccentBrush();

            ListView list = new ListView
            {
                MaxHeight = 320,
                SelectionMode = ListViewSelectionMode.None,
                IsItemClickEnabled = true,
            };
            ListViewItem currentContainer = null;
            for (int i = 0; i < _manifest.Toc.Count; i++)
            {
                EpubTocEntry entry = _manifest.Toc[i];
                bool isCurrent = i == currentTocIndex;
                TextBlock text = new TextBlock
                {
                    Text = entry.Title,
                    TextWrapping = TextWrapping.Wrap,
                    FontWeight = isCurrent ? FontWeights.Bold : FontWeights.Normal,
                };
                if (isCurrent)
                {
                    text.Foreground = accent;
                }
                ListViewItem container = new ListViewItem { Content = text, Tag = entry };
                if (isCurrent)
                {
                    currentContainer = container;
                }
                list.Items.Add(container);
            }
            list.ItemClick += TocList_ItemClick;
            panel.Children.Add(list);

            Flyout flyout = new Flyout { Content = panel };
            _tocFlyout = flyout;
            flyout.ShowAt(TocButton);

            if (currentContainer != null)
            {
                list.ScrollIntoView(currentContainer);
            }
        }

        private int FindCurrentTocIndex()
        {
            int best = -1;
            int bestSpineIndex = -1;
            for (int i = 0; i < _manifest.Toc.Count; i++)
            {
                int spineIndex = _manifest.Toc[i].SpineIndex;
                if (spineIndex <= _chapterIndex && spineIndex > bestSpineIndex)
                {
                    bestSpineIndex = spineIndex;
                    best = i;
                }
            }
            return best;
        }

        private async void TocList_ItemClick(object sender, ItemClickEventArgs e)
        {
            ListViewItem clickedContainer = e.ClickedItem as ListViewItem;
            EpubTocEntry entry = clickedContainer != null ? clickedContainer.Tag as EpubTocEntry : null;
            if (_tocFlyout != null)
            {
                _tocFlyout.Hide();
            }
            if (entry != null)
            {
                await NavigateToChapterAsync(entry.SpineIndex, null);
            }
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
            TopBar.Opacity = _chromeVisible ? 1 : 0;
            TopBar.IsHitTestVisible = _chromeVisible;
            BottomBar.Opacity = _chromeVisible ? 1 : 0;
            BottomBar.IsHitTestVisible = _chromeVisible;
        }

        private void BackToHome_Click(object sender, RoutedEventArgs e)
        {
            MainPage.Current.NavigateToTab(typeof(HomePage));
        }

        private void SearchButton_Click(object sender, RoutedEventArgs e)
        {
            Flyout flyout = new Flyout
            {
                Content = new TextBlock
                {
                    Text = "Busca dentro do livro ainda não existe nesta versão.",
                    TextWrapping = TextWrapping.Wrap,
                    MaxWidth = 240,
                },
            };
            flyout.ShowAt(TopBar);
        }

        private void GestureSettingsButton_Click(object sender, RoutedEventArgs e)
        {
            StackPanel panel = new StackPanel { Padding = new Thickness(16), Width = 260 };
            panel.Children.Add(new TextBlock
            {
                Text = "Gestos de navegação",
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(0, 0, 0, 12),
            });

            ToggleSwitch tapCornersSwitch = new ToggleSwitch
            {
                Header = "Tocar nas bordas vira página",
                IsOn = ReaderPreferenceStore.GetTapCornersEnabled(),
            };
            tapCornersSwitch.Toggled += (tapSender, tapArgs) =>
                ReaderPreferenceStore.SetTapCornersEnabled(tapCornersSwitch.IsOn);
            panel.Children.Add(tapCornersSwitch);

            ToggleSwitch swipeSwitch = new ToggleSwitch
            {
                Header = "Arrastar o dedo vira página",
                IsOn = ReaderPreferenceStore.GetSwipeEnabled(),
            };
            swipeSwitch.Toggled += (swipeSender, swipeArgs) =>
                ReaderPreferenceStore.SetSwipeEnabled(swipeSwitch.IsOn);
            panel.Children.Add(swipeSwitch);

            ToggleSwitch pinchSwitch = new ToggleSwitch
            {
                Header = "Pinça ajusta o tamanho da fonte",
                IsOn = ReaderPreferenceStore.GetPinchToZoomEnabled(),
            };
            pinchSwitch.Toggled += (pinchSender, pinchArgs) =>
                ReaderPreferenceStore.SetPinchToZoomEnabled(pinchSwitch.IsOn);
            panel.Children.Add(pinchSwitch);

            Flyout flyout = new Flyout { Content = panel };
            flyout.ShowAt(GestureSettingsButton);
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

        // — ajustes de leitura (fonte, tema, brilho) —

        private void TypographyButton_Click(object sender, RoutedEventArgs e)
        {
            StackPanel panel = new StackPanel { Padding = new Thickness(16), Width = 240 };

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
            flyout.ShowAt(TypographyButton);
        }

        private void BrightnessButton_Click(object sender, RoutedEventArgs e)
        {
            StackPanel panel = new StackPanel { Padding = new Thickness(16), Width = 240 };

            panel.Children.Add(new TextBlock { Text = "Escurecer tela", Margin = new Thickness(0, 0, 0, 8) });
            Slider dimSlider = new Slider
            {
                Minimum = 0,
                Maximum = 80,
                Value = ReaderPreferenceStore.GetDimLevelPercent(),
                Margin = new Thickness(0, 0, 0, 20),
            };
            dimSlider.ValueChanged += (dimSender, dimArgs) =>
            {
                int level = (int)dimArgs.NewValue;
                ReaderPreferenceStore.SetDimLevelPercent(level);
                ApplyDimLevel();
            };
            panel.Children.Add(dimSlider);

            panel.Children.Add(new TextBlock { Text = "Tema de leitura", Margin = new Thickness(0, 0, 0, 8) });
            StackPanel themeRow = new StackPanel { Orientation = Orientation.Horizontal };
            themeRow.Children.Add(CreateThemeButton("Claro", "light"));
            themeRow.Children.Add(CreateThemeButton("Sépia", "sepia"));
            themeRow.Children.Add(CreateThemeButton("Escuro", "dark"));
            panel.Children.Add(themeRow);

            Flyout flyout = new Flyout { Content = panel };
            flyout.ShowAt(BrightnessButton);
        }

        private void ApplyDimLevel()
        {
            int level = ReaderPreferenceStore.GetDimLevelPercent();
            DimOverlay.Opacity = level / 100.0;
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

            // pageWidth/pageHeight são medidos de DENTRO do próprio
            // JavaScript (document.documentElement.clientWidth/Height),
            // não calculados em C# a partir de ContentWebView.ActualWidth
            // — um valor vindo de fora pode não bater exatamente com o
            // que o motor de renderização considera sua própria largura
            // (diferença de escala/DPI entre DIPs do XAML e px CSS do
            // WebView), o que fazia a coluna sair um pouco menor que a
            // tela real e caber duas em vez de uma. Medindo por dentro,
            // column-width sempre bate exato com a largura de verdade.
            //
            // Fundo é forçado tanto em html quanto em body, e qualquer
            // elemento interno tem o próprio fundo zerado (background-color:
            // transparent) — sem isso, uma div/wrapper do próprio EPUB com
            // fundo branco embutido continua aparecendo por cima do tema
            // escolhido.
            string script =
                "(function() {" +
                "var pageWidth = document.documentElement.clientWidth;" +
                "var pageHeight = document.documentElement.clientHeight;" +
                "var style = document.getElementById('sorvil-reader-style');" +
                "if (!style) { style = document.createElement('style'); style.id = 'sorvil-reader-style'; document.head.appendChild(style); }" +
                "style.innerHTML = " +
                "'html { margin:0 !important; padding:0 !important; overflow:hidden !important; background-color: " + background + " !important; } ' +" +
                "'body { margin:0 !important; padding:0 !important; " +
                "font-size: " + fontSize + "% !important; " +
                "background-color: " + background + " !important; " +
                "line-height: 1.5 !important; " +
                "column-gap: 0px !important; " +
                "column-fill: auto !important; " +
                "overflow: hidden !important; " +
                "} ' +" +
                "'* { color: " + foreground + " !important; background-color: transparent !important; } ' +" +
                "'img, table { max-width: 100% !important; height: auto !important; background-color: initial !important; }';" +
                "style.innerHTML += 'body { height: ' + pageHeight + 'px !important; column-width: ' + pageWidth + 'px !important; }';" +
                "})();";

            await InvokeAsync(script);
        }
    }
}
