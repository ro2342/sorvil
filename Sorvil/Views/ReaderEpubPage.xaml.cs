using Sorvil.Models;
using Sorvil.Services;
using System;
using System.Threading.Tasks;
using Windows.Storage;
using Windows.UI;
using Windows.UI.Core;
using Windows.UI.Text;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Controls.Primitives;
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
    // total via script).
    //
    // A casca de leitura é uma máquina de estados só (ReaderChromeState),
    // igual ao protótipo em sorvil-mockup.html: nenhum estado (imersivo),
    // barra simples, um dos três painéis (fonte/tema/gestos) anexado
    // logo abaixo da barra de cima, ou o índice (que substitui a barra de
    // cima por um cabeçalho claro + lista de capítulos).
    //
    // KEPUB é tratado como EPUB comum — os <span> extras da Kobo não
    // atrapalham a paginação. O EPUB só é descompactado (custo real) na
    // primeira abertura — EpubExtractor já cacheia isso em
    // BooksExtracted/, então reabrir o mesmo livro é rápido.
    public sealed partial class ReaderEpubPage : Page
    {
        private enum ReaderChromeState
        {
            None,
            Toolbar,
            Font,
            Theme,
            Gestures,
            Index,
        }

        private string _bookId;
        private string _folderName;
        private EpubManifest _manifest;
        private int _chapterIndex;
        private int _pageIndexInChapter;
        private int _totalPagesInChapter = 1;
        private int? _pendingStartPage;
        private BookRecord _record;
        private double _manipulationTranslationX;
        private double _manipulationScale = 1.0;
        private ReaderChromeState _state = ReaderChromeState.None;
        private bool _suppressSliderEvents;

        public ReaderEpubPage()
        {
            this.InitializeComponent();
            ContentWebView.NavigationCompleted += ContentWebView_NavigationCompleted;
            ContentWebView.NavigationFailed += ContentWebView_NavigationFailed;
            SystemNavigationManager.GetForCurrentView().BackRequested += OnBackRequested;

            _suppressSliderEvents = true;
            BuildFontPanel();
            BuildThemePanel();
            BuildGesturesPanel();
            _suppressSliderEvents = false;
        }

        // Esta página navega no Frame raiz da janela (App.RootFrame), não
        // no ContentFrame aninhado do MainPage — então é dela mesma (e não
        // do MainPage) cuidar do botão Voltar do sistema enquanto estiver
        // em tela. A guarda por this.Frame.Content evita agir quando essa
        // inscrição antiga ainda existe mas a página não está mais visível.
        private void OnBackRequested(object sender, BackRequestedEventArgs e)
        {
            if (this.Frame.Content != this)
            {
                return;
            }

            // Um passo de cada vez, de dentro pra fora: índice/painel volta
            // pra barra simples; barra simples esconde tudo (imersivo);
            // imersivo sai do leitor.
            if (_state == ReaderChromeState.None)
            {
                e.Handled = true;
                App.RootFrame.GoBack();
                return;
            }

            e.Handled = true;
            _state = _state == ReaderChromeState.Toolbar ? ReaderChromeState.None : ReaderChromeState.Toolbar;
            ApplyReaderChromeState();
        }

        private void RootGrid_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            if (e.NewSize.Width > 0)
            {
                ChapterDrawerList.Width = e.NewSize.Width * 0.62;
            }
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
            LoadingStatusText.Text = "Abrindo...";

            try
            {
                _record = await LibraryDataStore.GetAsync(_bookId);
                if (_record == null)
                {
                    ShowLoadError("Livro não encontrado.");
                    return;
                }

                IndexHeaderTitleText.Text = _record.Title;
                BottomCaptionText.Text = string.IsNullOrEmpty(_record.Author)
                    ? _record.Title
                    : _record.Title + " (" + _record.Author + ")";
                ApplyDimLevel();
                UpdateGestureMode();

                StorageFolder booksFolder = await ApplicationData.Current.LocalFolder.GetFolderAsync("Books");
                StorageFile epubFile = await booksFolder.GetFileAsync(_record.LocalFilePath);

                LoadingStatusText.Text = "Extraindo...";
                _manifest = await EpubExtractor.ExtractAndParseAsync(_bookId, epubFile);
                _folderName = EpubExtractor.GetExtractedFolderName(_bookId);

                if (_manifest.SpineFiles.Count == 0)
                {
                    ShowLoadError("Não consegui ler o índice deste EPUB.");
                    return;
                }

                PopulateChapterDrawer();

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
            LoadingStatusText.Text = message;
        }

        // startPage: null = primeira página, -1 = última página (entrando
        // de trás pra frente, ex.: botão Anterior no início de um capítulo).
        private async Task NavigateToChapterAsync(int chapterIndex, int? startPage)
        {
            _chapterIndex = chapterIndex;
            _pendingStartPage = startPage;
            LoadingRing.IsActive = true;
            LoadingStatusText.Text = "Carregando capítulo...";
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
            LoadingStatusText.Text = string.Empty;
        }

        private void ContentWebView_NavigationFailed(object sender, WebViewNavigationFailedEventArgs args)
        {
            LoadingRing.IsActive = false;
            LoadingStatusText.Text = "Erro ao carregar o capítulo.";
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
            ScrubberSlider.Value = approxPercent;

            int currentTocIndex = FindCurrentTocIndex();
            string chapterTitle = currentTocIndex >= 0 ? _manifest.Toc[currentTocIndex].Title : null;
            TbCenterLabel.Text = chapterTitle != null
                ? "Capítulo " + (_chapterIndex + 1) + " - " + chapterTitle
                : "Capítulo " + (_chapterIndex + 1);
        }

        // — índice (sumário) —

        private void PopulateChapterDrawer()
        {
            ChapterDrawerList.Items.Clear();
            for (int i = 0; i < _manifest.Toc.Count; i++)
            {
                EpubTocEntry entry = _manifest.Toc[i];
                TextBlock text = new TextBlock
                {
                    Text = entry.Title,
                    TextWrapping = TextWrapping.Wrap,
                    Foreground = new SolidColorBrush(Colors.White),
                    Padding = new Thickness(2),
                };
                ListViewItem container = new ListViewItem
                {
                    Content = text,
                    Tag = entry,
                    Padding = new Thickness(18, 16, 18, 16),
                    Background = new SolidColorBrush(Colors.Transparent),
                };
                ChapterDrawerList.Items.Add(container);
            }
        }

        private void HighlightCurrentChapterInDrawer()
        {
            int currentTocIndex = FindCurrentTocIndex();
            SolidColorBrush activeBackground = new SolidColorBrush(Color.FromArgb(0xFF, 0x32, 0x60, 0x19));
            SolidColorBrush inactiveBackground = new SolidColorBrush(Colors.Transparent);

            for (int i = 0; i < ChapterDrawerList.Items.Count; i++)
            {
                ListViewItem container = (ListViewItem)ChapterDrawerList.Items[i];
                bool isCurrent = i == currentTocIndex;
                container.Background = isCurrent ? activeBackground : inactiveBackground;
                TextBlock text = (TextBlock)container.Content;
                text.FontWeight = isCurrent ? FontWeights.Bold : FontWeights.Normal;
                if (isCurrent)
                {
                    ChapterDrawerList.ScrollIntoView(container);
                }
            }
        }

        // "Capítulo atual" é a entrada do índice com o maior SpineIndex que
        // ainda não passou do capítulo aberto — o NCX não tem uma entrada
        // pra cada arquivo do spine (um capítulo de verdade às vezes vira
        // vários arquivos internos), então é o "mais próximo por baixo"
        // que representa onde a leitura está.
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

        private void OpenIndex_Click(object sender, RoutedEventArgs e)
        {
            _state = ReaderChromeState.Index;
            ApplyReaderChromeState();
        }

        private void CloseIndex_Click(object sender, RoutedEventArgs e)
        {
            _state = ReaderChromeState.Toolbar;
            ApplyReaderChromeState();
        }

        private async void ChapterDrawerList_ItemClick(object sender, ItemClickEventArgs e)
        {
            ListViewItem clickedContainer = e.ClickedItem as ListViewItem;
            EpubTocEntry entry = clickedContainer != null ? clickedContainer.Tag as EpubTocEntry : null;
            _state = ReaderChromeState.None;
            ApplyReaderChromeState();
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

        // Os botões de "retroceder/avançar" do scrubber dão um pequeno
        // salto — a barra em si (arrastar o thumb) é só visual, igual ao
        // protótipo, mas esses dois botões são um atalho real e barato:
        // uma página pra cada lado.
        private async void ScrubBack_Click(object sender, RoutedEventArgs e)
        {
            await GoToPreviousAsync();
        }

        private async void ScrubForward_Click(object sender, RoutedEventArgs e)
        {
            await GoToNextAsync();
        }

        private async void PreviousZone_Tapped(object sender, TappedRoutedEventArgs e)
        {
            if (!ReaderPreferenceStore.GetTapCornersEnabled())
            {
                return;
            }
            await GoToPreviousAsync();
        }

        private async void NextZone_Tapped(object sender, TappedRoutedEventArgs e)
        {
            if (!ReaderPreferenceStore.GetTapCornersEnabled())
            {
                return;
            }
            await GoToNextAsync();
        }

        // — gestos opcionais: arrastar vira página, pinça ajusta a fonte —
        // Só habilitados de verdade (ManipulationMode diferente de
        // "System") quando o usuário liga um dos dois no painel de
        // gestos — por padrão a zona de toque continua se comportando
        // exatamente como antes (só Tapped, sem processar manipulação
        // nenhuma), pra não arriscar interferir no toque simples que já
        // era a experiência padrão testada no aparelho.
        private void UpdateGestureMode()
        {
            bool swipeEnabled = ReaderPreferenceStore.GetSwipeEnabled();
            bool pinchEnabled = ReaderPreferenceStore.GetPinchToZoomEnabled();

            if (!swipeEnabled && !pinchEnabled)
            {
                GestureZoneGrid.ManipulationMode = ManipulationModes.System;
                return;
            }

            ManipulationModes modes = ManipulationModes.None;
            if (swipeEnabled)
            {
                modes |= ManipulationModes.TranslateX;
            }
            if (pinchEnabled)
            {
                modes |= ManipulationModes.Scale;
            }
            GestureZoneGrid.ManipulationMode = modes;
        }

        private void GestureZoneGrid_ManipulationStarted(object sender, ManipulationStartedRoutedEventArgs e)
        {
            _manipulationTranslationX = 0;
            _manipulationScale = 1.0;
        }

        private void GestureZoneGrid_ManipulationDelta(object sender, ManipulationDeltaRoutedEventArgs e)
        {
            _manipulationTranslationX += e.Delta.Translation.X;
            if (e.Delta.Scale > 0)
            {
                _manipulationScale *= e.Delta.Scale;
            }
        }

        private const double SwipeThresholdPixels = 60;
        private const double PinchScaleThreshold = 0.08;

        private async void GestureZoneGrid_ManipulationCompleted(object sender, ManipulationCompletedRoutedEventArgs e)
        {
            bool isPinch = Math.Abs(_manipulationScale - 1.0) > PinchScaleThreshold;

            if (isPinch && ReaderPreferenceStore.GetPinchToZoomEnabled())
            {
                int delta = (int)Math.Round((_manipulationScale - 1.0) * 100);
                if (delta != 0)
                {
                    SetFontSizePercent(ReaderPreferenceStore.GetFontSizePercent() + delta);
                    await ReapplyStyleAndRepaginateAsync();
                }
            }
            else if (!isPinch && ReaderPreferenceStore.GetSwipeEnabled() && Math.Abs(_manipulationTranslationX) > SwipeThresholdPixels)
            {
                if (_manipulationTranslationX < 0)
                {
                    await GoToNextAsync();
                }
                else
                {
                    await GoToPreviousAsync();
                }
            }
        }

        // — casca de leitura: máquina de estados —

        private void ReaderArea_Tapped(object sender, TappedRoutedEventArgs e)
        {
            if (_state == ReaderChromeState.None)
            {
                _state = ReaderChromeState.Toolbar;
            }
            else if (_state == ReaderChromeState.Toolbar)
            {
                _state = ReaderChromeState.None;
            }
            else
            {
                _state = ReaderChromeState.Toolbar;
            }
            ApplyReaderChromeState();
        }

        private void TogglePanel(ReaderChromeState which)
        {
            _state = _state == which ? ReaderChromeState.Toolbar : which;
            ApplyReaderChromeState();
        }

        private void TypographyButton_Click(object sender, RoutedEventArgs e)
        {
            TogglePanel(ReaderChromeState.Font);
        }

        private void BrightnessButton_Click(object sender, RoutedEventArgs e)
        {
            TogglePanel(ReaderChromeState.Theme);
        }

        private void GestureSettingsButton_Click(object sender, RoutedEventArgs e)
        {
            TogglePanel(ReaderChromeState.Gestures);
        }

        private void BackToHome_Click(object sender, RoutedEventArgs e)
        {
            App.RootFrame.GoBack();
        }

        private void ToggleSearch_Click(object sender, RoutedEventArgs e)
        {
            bool opening = TbSearchInput.Visibility == Visibility.Collapsed;
            TbSearchInput.Visibility = opening ? Visibility.Visible : Visibility.Collapsed;
            TbCenterLabel.Visibility = opening ? Visibility.Collapsed : Visibility.Visible;
            if (opening)
            {
                TbSearchInput.Focus(FocusState.Programmatic);
            }
        }

        private void ApplyReaderChromeState()
        {
            bool showTop = _state != ReaderChromeState.None;
            TopChrome.Opacity = showTop ? 1 : 0;
            TopChrome.IsHitTestVisible = showTop;

            bool isIndex = _state == ReaderChromeState.Index;
            ToolbarTop.Visibility = isIndex ? Visibility.Collapsed : Visibility.Visible;
            IndexOverlay.Visibility = isIndex ? Visibility.Visible : Visibility.Collapsed;

            FontPanel.Visibility = _state == ReaderChromeState.Font ? Visibility.Visible : Visibility.Collapsed;
            ThemePanel.Visibility = _state == ReaderChromeState.Theme ? Visibility.Visible : Visibility.Collapsed;
            GesturesPanel.Visibility = _state == ReaderChromeState.Gestures ? Visibility.Visible : Visibility.Collapsed;

            bool showBottom = _state == ReaderChromeState.Toolbar;
            BottomBar.Opacity = showBottom ? 1 : 0;
            BottomBar.IsHitTestVisible = showBottom;
            if (!showBottom)
            {
                TbSearchInput.Visibility = Visibility.Collapsed;
                TbCenterLabel.Visibility = Visibility.Visible;
            }

            SolidColorBrush activeIconBrush = new SolidColorBrush(Color.FromArgb(0xFF, 0x5D, 0xA1, 0x30));
            SolidColorBrush inactiveIconBrush = new SolidColorBrush(Color.FromArgb(0xFF, 0xEE, 0xEE, 0xEE));
            AaText.Foreground = _state == ReaderChromeState.Font ? activeIconBrush : inactiveIconBrush;
            BrightnessIcon.Foreground = _state == ReaderChromeState.Theme ? activeIconBrush : inactiveIconBrush;
            GestureIcon.Foreground = _state == ReaderChromeState.Gestures ? activeIconBrush : inactiveIconBrush;

            if (isIndex && _manifest != null)
            {
                HighlightCurrentChapterInDrawer();
            }
        }

        // — painel de fonte —

        private void BuildFontPanel()
        {
            FontSizeSlider.Value = ReaderPreferenceStore.GetFontSizePercent();
            LineSpacingSlider.Value = ReaderPreferenceStore.GetLineSpacing();
            MarginSlider.Value = ReaderPreferenceStore.GetMarginPx();

            AddSegButton(JustificationRow, "Esquerda", "left", ApplyJustification);
            AddSegButton(JustificationRow, "Justificado", "justify", ApplyJustification);
            RefreshSegRow(JustificationRow, ReaderPreferenceStore.GetJustification());
        }

        private void SetFontSizePercent(int percent)
        {
            int clamped = Math.Max(70, Math.Min(350, percent));
            ReaderPreferenceStore.SetFontSizePercent(clamped);
            _suppressSliderEvents = true;
            FontSizeSlider.Value = clamped;
            _suppressSliderEvents = false;
        }

        private async void FontSizeSlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
        {
            if (_suppressSliderEvents)
            {
                return;
            }
            ReaderPreferenceStore.SetFontSizePercent((int)e.NewValue);
            await ReapplyStyleAndRepaginateAsync();
        }

        private async void LineSpacingSlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
        {
            if (_suppressSliderEvents)
            {
                return;
            }
            ReaderPreferenceStore.SetLineSpacing(Math.Round(e.NewValue, 1));
            await ReapplyStyleAndRepaginateAsync();
        }

        private async void MarginSlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
        {
            if (_suppressSliderEvents)
            {
                return;
            }
            ReaderPreferenceStore.SetMarginPx((int)e.NewValue);
            await ReapplyStyleAndRepaginateAsync();
        }

        private async void ApplyJustification(string value)
        {
            ReaderPreferenceStore.SetJustification(value);
            RefreshSegRow(JustificationRow, value);
            await ReapplyStyleAndRepaginateAsync();
        }

        // — painel de tema/brilho —

        private void BuildThemePanel()
        {
            DimSlider.Value = ReaderPreferenceStore.GetDimLevelPercent();

            AddSegButton(ThemeRow, "Claro", "light", ApplyTheme);
            AddSegButton(ThemeRow, "Escuro", "dark", ApplyTheme);
            AddSegButton(ThemeRow, "Sépia", "sepia", ApplyTheme);
            RefreshSegRow(ThemeRow, ReaderPreferenceStore.GetTheme());
        }

        private void DimSlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
        {
            if (_suppressSliderEvents)
            {
                return;
            }
            ReaderPreferenceStore.SetDimLevelPercent((int)e.NewValue);
            ApplyDimLevel();
        }

        private async void ApplyTheme(string value)
        {
            ReaderPreferenceStore.SetTheme(value);
            RefreshSegRow(ThemeRow, value);
            await ReapplyStyleAndRepaginateAsync();
        }

        private void ApplyDimLevel()
        {
            int level = ReaderPreferenceStore.GetDimLevelPercent();
            DimOverlay.Opacity = level / 100.0;
        }

        // — painel de gestos —

        private void BuildGesturesPanel()
        {
            ToggleSwitch pinchSwitch = new ToggleSwitch
            {
                Header = "Zoom do texto com movimento de pinça",
                IsOn = ReaderPreferenceStore.GetPinchToZoomEnabled(),
            };
            pinchSwitch.Toggled += (s, a) =>
            {
                ReaderPreferenceStore.SetPinchToZoomEnabled(pinchSwitch.IsOn);
                UpdateGestureMode();
            };
            GesturesPanel.Children.Add(pinchSwitch);

            ToggleSwitch tapCornersSwitch = new ToggleSwitch
            {
                Header = "Toque nos cantos para mudar página",
                IsOn = ReaderPreferenceStore.GetTapCornersEnabled(),
            };
            tapCornersSwitch.Toggled += (s, a) =>
                ReaderPreferenceStore.SetTapCornersEnabled(tapCornersSwitch.IsOn);
            GesturesPanel.Children.Add(tapCornersSwitch);

            ToggleSwitch swipeSwitch = new ToggleSwitch
            {
                Header = "Mudar página com movimento de slide",
                IsOn = ReaderPreferenceStore.GetSwipeEnabled(),
            };
            swipeSwitch.Toggled += (s, a) =>
            {
                ReaderPreferenceStore.SetSwipeEnabled(swipeSwitch.IsOn);
                UpdateGestureMode();
            };
            GesturesPanel.Children.Add(swipeSwitch);
        }

        // — botões segmentados (tema/justificação) —

        private void AddSegButton(StackPanel row, string label, string value, Action<string> onSelect)
        {
            Button button = new Button
            {
                Content = label,
                Tag = value,
                Style = (Style)Resources["SegButtonStyle"],
                Margin = new Thickness(0, 0, 8, 0),
            };
            button.Click += (s, a) => onSelect(value);
            row.Children.Add(button);
        }

        private static void RefreshSegRow(StackPanel row, string activeValue)
        {
            SolidColorBrush activeBackground = new SolidColorBrush(Colors.WhiteSmoke);
            SolidColorBrush activeForeground = new SolidColorBrush(Color.FromArgb(0xFF, 0x22, 0x22, 0x22));
            SolidColorBrush inactiveBackground = new SolidColorBrush(Color.FromArgb(0xFF, 0x55, 0x55, 0x55));
            SolidColorBrush inactiveForeground = new SolidColorBrush(Colors.WhiteSmoke);

            foreach (object child in row.Children)
            {
                Button button = child as Button;
                if (button == null)
                {
                    continue;
                }
                bool active = (string)button.Tag == activeValue;
                button.Background = active ? activeBackground : inactiveBackground;
                button.Foreground = active ? activeForeground : inactiveForeground;
            }
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
            int margin = ReaderPreferenceStore.GetMarginPx();
            string script =
                "(function() {" +
                "var stepWidth = document.documentElement.clientWidth - (" + margin + " * 2);" +
                "var usableScroll = document.body.scrollWidth - (" + margin + " * 2);" +
                "return Math.max(1, Math.ceil(usableScroll / stepWidth));" +
                "})();";
            string result = await InvokeAsync(script);
            int pages;
            return int.TryParse(result, out pages) && pages > 0 ? pages : 1;
        }

        private async Task GoToPageAsync(int pageIndex)
        {
            _pageIndexInChapter = pageIndex;
            int margin = ReaderPreferenceStore.GetMarginPx();
            string script =
                "(function() {" +
                "var stepWidth = document.documentElement.clientWidth - (" + margin + " * 2);" +
                "document.body.style.transform = 'translateX(-' + (" + pageIndex + " * stepWidth) + 'px)';" +
                "})();";
            await InvokeAsync(script);
        }

        // Mudar fonte/tema/espaçamento/margem muda quantas páginas cabem
        // no capítulo — por isso repagina do zero (volta pra página 0) em
        // vez de só reaplicar o CSS mantendo o índice de página antigo,
        // que ficaria errado.
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
            double lineSpacing = ReaderPreferenceStore.GetLineSpacing();
            int margin = ReaderPreferenceStore.GetMarginPx();
            string justification = ReaderPreferenceStore.GetJustification();

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
            // Mesmo assim, arredondamento de sub-pixel (o layout real do
            // WebView pode ter uma fração de pixel de diferença do inteiro
            // que clientWidth reporta) ainda deixava o motor decidir criar
            // uma segunda coluna bem estreita — visível como um "resto de
            // texto" à direita, mais perceptível quanto menor a fonte
            // (mais linhas cabem naquela fresta estreita). column-count:1
            // força exatamente uma coluna sempre, não importa o quão perto
            // column-width chegue do valor exato — column-width continua
            // declarado só pra manter o cálculo de paginação em
            // GetTotalPagesAsync/GoToPageAsync consistente com o que a
            // coluna única realmente ocupa.
            //
            // A margem de leitura vira padding do body, não Margin do
            // WebView no XAML — um Margin ali deixava a cor de fundo da
            // Page (não a do tema de leitura escolhido) visível como uma
            // borda ao redor do texto. column-width já sai descontando
            // essa margem duas vezes (esquerda+direita) pra bater com o
            // mesmo cálculo usado em GetTotalPagesAsync/GoToPageAsync.
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
                "var margin = " + margin + ";" +
                "var style = document.getElementById('sorvil-reader-style');" +
                "if (!style) { style = document.createElement('style'); style.id = 'sorvil-reader-style'; document.head.appendChild(style); }" +
                "style.innerHTML = " +
                "'html { margin:0 !important; padding:0 !important; overflow:hidden !important; background-color: " + background + " !important; } ' +" +
                "'body { margin:0 !important; " +
                "font-size: " + fontSize + "% !important; " +
                "background-color: " + background + " !important; " +
                "line-height: " + lineSpacing.ToString(System.Globalization.CultureInfo.InvariantCulture) + " !important; " +
                "text-align: " + justification + " !important; " +
                "column-gap: 0px !important; " +
                "column-fill: auto !important; " +
                "column-count: 1 !important; " +
                "overflow: hidden !important; " +
                "} ' +" +
                "'* { color: " + foreground + " !important; background-color: transparent !important; } ' +" +
                "'img, table { max-width: 100% !important; height: auto !important; background-color: initial !important; }';" +
                "style.innerHTML += 'body { height: ' + pageHeight + 'px !important; padding: 0 ' + margin + 'px !important; column-width: ' + (pageWidth - margin * 2) + 'px !important; }';" +
                "})();";

            await InvokeAsync(script);
        }
    }
}
