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
    // Freda), não uma página de web corrida. A técnica: rolagem vertical
    // via script (window.scrollTo), um "tela inteira" de altura por vez —
    // ver a seção "scripts de paginação" mais abaixo pro porquê disso (não
    // CSS multi-column, que vazava uma segunda coluna de verdade em
    // aparelho real).
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

        // Fontes que já vêm instaladas no Windows 10 Mobile — não
        // precisa baixar/empacotar nada. "Padrão" é string vazia de
        // propósito: significa não mexer na fonte do livro (o CSS
        // original do EPUB continua no comando) — só troca de verdade
        // quando o usuário escolhe uma fonte específica.
        private static readonly string[] FontFamilyLabels = { "Padrão", "Segoe UI", "Times New Roman", "Verdana", "Consolas" };
        private static readonly string[] FontFamilyValues =
        {
            "",
            "'Segoe UI', sans-serif",
            "'Times New Roman', serif",
            "Verdana, sans-serif",
            "Consolas, monospace",
        };

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
                if (string.IsNullOrEmpty(_record.LocalFilePath))
                {
                    ShowLoadError("Esse livro não tem um arquivo baixado válido — apague e baixe de novo.");
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
            UpdateWebViewBackgroundColor();
            Uri uri = EpubExtractor.BuildLocalContentUri(_folderName, _manifest.SpineFiles[chapterIndex]);
            ContentWebView.Navigate(uri);
        }

        // O WebView pinta com fundo branco por padrão antes de qualquer
        // CSS carregar — como o tema escolhido só é aplicado DEPOIS que a
        // navegação termina (ContentWebView_NavigationCompleted), isso
        // aparecia como um flash branco antes de escurecer pro tema
        // escuro a cada troca de capítulo. Setando DefaultBackgroundColor
        // ANTES de navegar, o próprio WebView já nasce com a cor certa,
        // sem esperar o CSS.
        private void UpdateWebViewBackgroundColor()
        {
            string theme = ReaderPreferenceStore.GetTheme();
            Color color;
            switch (theme)
            {
                case "sepia":
                    color = Color.FromArgb(0xFF, 0xF4, 0xEC, 0xD8);
                    break;
                case "dark":
                    color = Color.FromArgb(0xFF, 0x1B, 0x1B, 0x1F);
                    break;
                default:
                    color = Colors.White;
                    break;
            }
            ContentWebView.DefaultBackgroundColor = color;
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
                // Tapped direto no item, em vez de depender do ItemClick da
                // ListView — mais confiável quando os itens já são
                // ListViewItem prontos (não objetos de dados com
                // DataTemplate), que é como este código monta a lista.
                container.Tapped += ChapterDrawerItem_Tapped;
                ChapterDrawerList.Items.Add(container);
            }
        }

        private async void ChapterDrawerItem_Tapped(object sender, TappedRoutedEventArgs e)
        {
            ListViewItem container = sender as ListViewItem;
            EpubTocEntry entry = container != null ? container.Tag as EpubTocEntry : null;
            _state = ReaderChromeState.None;
            ApplyReaderChromeState();
            if (entry != null)
            {
                await NavigateToChapterAsync(entry.SpineIndex, null);
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

        // Tocar fora da lista (na parte do livro que continua visível ao
        // lado) fecha o índice — mesmo comportamento de "tocar fora
        // fecha" que a gaveta de navegação da Home já tinha.
        private void IndexDismissArea_Tapped(object sender, TappedRoutedEventArgs e)
        {
            _state = ReaderChromeState.Toolbar;
            ApplyReaderChromeState();
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
        //
        // Não decide "virar página vs. virar capítulo" comparando
        // _pageIndexInChapter contra um _totalPagesInChapter pré-calculado
        // — esse total é só uma estimativa (usada pro indicador de
        // progresso) e, se sair errada, a comparação nasce falsa e todo
        // toque de "próxima" pula direto pro capítulo seguinte, mesmo com
        // o capítulo atual cheio de texto ainda não lido. Em vez disso,
        // TryStepPageAsync pergunta pra própria WebView, na hora, se ainda
        // dá pra rolar mais uma tela — só troca de capítulo quando a
        // rolagem de verdade já bateu no fim (ou início).

        private async Task GoToNextAsync()
        {
            bool advanced = await TryStepPageAsync(1);
            if (advanced)
            {
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
            bool advanced = await TryStepPageAsync(-1);
            if (advanced)
            {
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
            UpdateFontFamilyLabel();

            AddSegButton(JustificationRow, "Esquerda", "left", ApplyJustification);
            AddSegButton(JustificationRow, "Justificado", "justify", ApplyJustification);
            RefreshSegRow(JustificationRow, ReaderPreferenceStore.GetJustification());
        }

        private void UpdateFontFamilyLabel()
        {
            string current = ReaderPreferenceStore.GetFontFamily();
            int index = Array.IndexOf(FontFamilyValues, current);
            FontFamilyText.Text = (index >= 0 ? FontFamilyLabels[index] : FontFamilyLabels[0]) + " ▾";
        }

        private void FontFamilyButton_Click(object sender, RoutedEventArgs e)
        {
            ListView list = new ListView
            {
                ItemsSource = FontFamilyLabels,
                SelectionMode = ListViewSelectionMode.None,
                IsItemClickEnabled = true,
                Width = 200,
                MaxHeight = 260,
            };
            list.ItemClick += async (s, a) =>
            {
                int index = Array.IndexOf(FontFamilyLabels, (string)a.ClickedItem);
                if (index < 0)
                {
                    return;
                }
                ReaderPreferenceStore.SetFontFamily(FontFamilyValues[index]);
                UpdateFontFamilyLabel();
                await ReapplyStyleAndRepaginateAsync();
            };
            Flyout flyout = new Flyout { Content = list };
            flyout.ShowAt(FontFamilyButton);
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
            UpdateWebViewBackgroundColor();
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
        //
        // Paginação via CSS multi-column (column-width/column-count +
        // translateX) foi tentada três vezes e nas três sobrou uma "segunda
        // coluna" visível de verdade num aparelho real — não vazamento de
        // sub-pixel, uma faixa larga e legível de texto da coluna seguinte
        // (ver foto no histórico de commits/conversa). O suporte a CSS
        // multi-column nunca foi consistente entre motores de navegador, e
        // a WebView do Windows 10 Mobile claramente não faz esse cálculo do
        // jeito esperado. Em vez de insistir em coluna, a virada de página
        // agora é rolagem vertical de verdade (window.scrollTo) por exatos
        // "N linhas" por tela — rolagem é um caminho de código muito mais
        // simples e maduro em qualquer motor do que layout multi-coluna, e
        // não existe "segunda coluna" possível se não existem colunas.
        //
        // O "tamanho da página" em px é sempre um múltiplo exato da altura
        // de linha atual (lida de volta via getComputedStyle depois que o
        // <style> injetado já aplicou fonte/espaçamento) — garante que o
        // corte de página nunca cai no meio de uma linha de texto; o único
        // lugar onde a rolagem pode ficar "não perfeitamente alinhada" é
        // dentro da margem entre parágrafos, que não tem texto nenhum
        // pra cortar ao meio.

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

        // window.innerHeight em vez de document.documentElement.clientHeight
        // — mais confiável entre modos de renderização (quirks vs.
        // standards) do que ler direto do elemento raiz, que é justo onde
        // um HTML de EPUB real (às vezes sem doctype "limpo") pode fazer a
        // WebView escolher um modo diferente do esperado.
        private const string ViewportHeightJs = "window.innerHeight";

        // window.pageYOffset com fallback pra scrollTop de documentElement
        // e de body — cobre tanto modo standards (html é quem rola) quanto
        // quirks (body é quem rola), sem precisar saber qual é qual.
        private const string ScrollTopJs = "(window.pageYOffset || document.documentElement.scrollTop || document.body.scrollTop || 0)";

        // Maior entre scrollHeight de body e de documentElement — mesma
        // razão do ViewportHeightJs: em vez de apostar em qual dos dois
        // elementos é "o scroller de verdade" neste documento específico,
        // usa o maior valor entre os dois (o que não é o scroller real
        // tende a só reportar a altura do viewport, sempre menor).
        private const string ContentHeightJs = "Math.max(document.body.scrollHeight, document.documentElement.scrollHeight)";

        // Mesma expressão usada em GetTotalPagesAsync, GoToPageAsync e
        // TryStepPageAsync — texto idêntico de propósito, pra nunca haver
        // dois cálculos de "altura de página" ligeiramente diferentes
        // brigando entre si.
        private static string PageHeightJs(int margin)
        {
            return "(function() {" +
                "var lh = parseFloat(getComputedStyle(document.body).lineHeight) || 24;" +
                "var raw = " + ViewportHeightJs + " - (" + margin + " * 2);" +
                "var lines = Math.max(1, Math.floor(raw / lh));" +
                "return lines * lh;" +
                "})()";
        }

        // Só usado pro indicador de progresso (aproximado) — a decisão de
        // "ainda cabe mais uma página aqui ou já é hora de trocar de
        // capítulo" nunca depende deste número, ver TryStepPageAsync.
        private async Task<int> GetTotalPagesAsync()
        {
            int margin = ReaderPreferenceStore.GetMarginPx();
            string script =
                "(function() {" +
                "var pageHeight = " + PageHeightJs(margin) + ";" +
                "var totalHeight = " + ContentHeightJs + ";" +
                "return Math.max(1, Math.ceil(totalHeight / pageHeight));" +
                "})();";
            string result = await InvokeAsync(script);
            int pages;
            return int.TryParse(result, out pages) && pages > 0 ? pages : 1;
        }

        // Salto absoluto (abrir capítulo na primeira página, ou na última
        // — startPage < 0 — ao entrar vindo de trás). Sempre pinça o alvo
        // dentro de [0, maxScroll] medido na hora, então mesmo se
        // pageIndex vier de uma estimativa errada (ex.: _totalPagesInChapter
        // aproximado), a posição final ainda cai certinha no fim de
        // verdade do capítulo.
        private async Task GoToPageAsync(int pageIndex)
        {
            int margin = ReaderPreferenceStore.GetMarginPx();
            string script =
                "(function() {" +
                "var pageHeight = " + PageHeightJs(margin) + ";" +
                "var maxScroll = Math.max(0, " + ContentHeightJs + " - " + ViewportHeightJs + ");" +
                "var target = Math.min(maxScroll, Math.max(0, Math.round(" + pageIndex + " * pageHeight)));" +
                "window.scrollTo(0, target);" +
                "return Math.round(target / pageHeight);" +
                "})();";
            string result = await InvokeAsync(script);
            int actualIndex;
            _pageIndexInChapter = int.TryParse(result, out actualIndex) ? actualIndex : Math.Max(0, pageIndex);
        }

        // Passo relativo (usado pelos botões/zonas de toque de próxima e
        // anterior): mede rolagem atual e o quanto ainda dá pra rolar no
        // MESMO script, então nunca fica dessincronizado de nenhum total
        // pré-calculado. Retorna false (sem mexer em nada) quando já está
        // na borda — aí quem chamou decide trocar de capítulo.
        private async Task<bool> TryStepPageAsync(int direction)
        {
            int margin = ReaderPreferenceStore.GetMarginPx();
            string script =
                "(function() {" +
                "var pageHeight = " + PageHeightJs(margin) + ";" +
                "var maxScroll = Math.max(0, " + ContentHeightJs + " - " + ViewportHeightJs + ");" +
                "var current = " + ScrollTopJs + ";" +
                "var target = Math.min(maxScroll, Math.max(0, current + (" + direction + " * pageHeight)));" +
                "if (Math.abs(target - current) < 2) { return -1; }" +
                "window.scrollTo(0, target);" +
                "return Math.round(target / pageHeight);" +
                "})();";
            string result = await InvokeAsync(script);
            int newIndex;
            if (!int.TryParse(result, out newIndex) || newIndex < 0)
            {
                return false;
            }
            _pageIndexInChapter = newIndex;
            return true;
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
            string fontFamily = ReaderPreferenceStore.GetFontFamily();

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

            // Deixa o CSS do próprio livro em pé — o design original
            // (recuo/indentação, letra capitular, título centralizado,
            // cores de destaque, etc.) é exatamente o que o autor/editora
            // pensou pro livro, e o pedido explícito foi não recriar isso
            // do zero. Só quatro coisas são realmente controladas por
            // aqui, tudo o mais fica por conta do livro:
            //
            // 1. Tamanho da fonte: escala html { font-size: X% } (a base
            //    "em"/"rem" de tudo) em vez de forçar um valor fixo em
            //    body — qualquer CSS do livro que use unidade relativa
            //    (o padrão em EPUB) escala proporcionalmente junto,
            //    preservando a hierarquia de tamanhos que o livro definiu
            //    (título maior que corpo, nota de rodapé menor, etc.).
            // 2. Margem: padding em body.
            // 3. Espaçamento de linha: line-height em body (herda pra
            //    tudo que não define o próprio).
            // 4. Cor de fundo/tema: background-color forçado; a cor de
            //    texto (foreground) só é aplicada em body SEM !important
            //    — então continua sendo a cor-base herdada por tudo que o
            //    livro não colore explicitamente, mas qualquer coisa que
            //    o próprio livro pinta de propósito (uma citação, um
            //    destaque) continua com a cor que o livro escolheu, não a
            //    nossa.
            //
            // Fonte (font-family) só entra na jogada se o usuário
            // escolheu uma manualmente no painel — "Padrão" é string
            // vazia, então o CSS do livro decide sozinho.
            //
            // overflow:hidden em html só esconde a barra/gesto de rolagem
            // nativa (rolagem programática via scrollTo continua
            // funcionando normalmente com overflow:hidden — isso só
            // bloqueia rolagem iniciada pelo usuário, não script);
            // max-width:100% em html/img é só uma rede de segurança contra
            // um layout do próprio livro que force algo mais largo que a
            // tela (evita corte horizontal), não uma reescrita do design.
            string fontFamilyCss = string.IsNullOrEmpty(fontFamily)
                ? string.Empty
                : "font-family: " + fontFamily + " !important; ";
            // CSS de texto entre ASPAS DUPLAS aqui de propósito — valores
            // de font-family (ex.: "'Segoe UI', sans-serif") têm aspas
            // SIMPLES dentro, que fechariam uma string JS delimitada por
            // aspas simples no meio da frase e quebrariam a sintaxe do
            // script inteiro (o eval() nem chega a rodar — style.innerHTML
            // nunca é setado, sobra só o HTML cru do capítulo sem nenhum
            // dos nossos ajustes). Aspas duplas não colidem com aspas
            // simples de CSS.
            string script =
                "(function() {" +
                "var style = document.getElementById('sorvil-reader-style');" +
                "if (!style) { style = document.createElement('style'); style.id = 'sorvil-reader-style'; document.head.appendChild(style); }" +
                "style.innerHTML = " +
                "\"html { max-width: 100% !important; overflow: hidden !important; background-color: " + background + " !important; font-size: " + fontSize + "% !important; } \" +" +
                "\"body { margin: 0 !important; max-width: 100% !important; box-sizing: border-box !important; " +
                "padding: " + margin + "px " + margin + "px !important; " +
                "background-color: " + background + " !important; " +
                "color: " + foreground + "; " +
                "line-height: " + lineSpacing.ToString(System.Globalization.CultureInfo.InvariantCulture) + " !important; " +
                "text-align: " + justification + " !important; " +
                fontFamilyCss +
                "} \" +" +
                "\"img { max-width: 100% !important; height: auto !important; }\";" +
                "void document.body.offsetHeight;" +
                "})();";

            await InvokeAsync(script);
        }
    }
}
