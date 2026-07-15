using Sorvil.Models;
using Sorvil.Services;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Windows.Storage;
using Windows.UI;
using Windows.UI.Core;
using Windows.UI.Text;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Controls.Primitives;
using Windows.UI.Xaml.Documents;
using Windows.UI.Xaml.Input;
using Windows.UI.Xaml.Media;
using Windows.UI.Xaml.Navigation;

namespace Sorvil.Views
{
    // Leitor de EPUB/KEPUB com paginação real de e-reader (tipo Kindle/
    // Freda), renderizado 100% nativo — sem WebView, sem CSS. Cada
    // capítulo vira uma lista de Paragraph nativos (EpubContentParser) e a
    // paginação usa o padrão nativo do UWP pra texto fluido: um
    // RichTextBlock (primeira "página") encadeado com RichTextBlockOverflow
    // (páginas seguintes) via OverflowContentTarget — o próprio motor de
    // texto do XAML decide onde cada página termina, sem depender de
    // nenhum motor de CSS/coluna de terceiros. Só a página atual fica
    // visível (Visibility) por vez; as outras já ficam prontas por baixo.
    //
    // Essa troca veio depois de várias tentativas de fazer o mesmo via
    // WebView+CSS (column-width/column-count) esbarrarem repetidas vezes
    // em bugs de motor de renderização antigo (segunda coluna aparecendo,
    // tema/fonte não aplicando de forma confiável) — nativo dá controle
    // total sobre cada propriedade de texto, sem surpresa de engine.
    //
    // A casca de leitura é uma máquina de estados só (ReaderChromeState),
    // igual ao protótipo em sorvil-mockup.html: nenhum estado (imersivo),
    // barra simples, um dos três painéis (fonte/tema/gestos) anexado
    // logo abaixo da barra de cima, ou o índice (que substitui a barra de
    // cima por um cabeçalho claro + lista de capítulos).
    //
    // KEPUB é tratado como EPUB comum — os <span> extras da Kobo não
    // atrapalham a leitura, só viram texto normal. O EPUB só é
    // descompactado (custo real) na primeira abertura — EpubExtractor já
    // cacheia isso em BooksExtracted/, então reabrir o mesmo livro é rápido.
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
        // precisa baixar/empacotar nada.
        private static readonly string[] FontFamilyLabels = { "Padrão", "Segoe UI", "Times New Roman", "Verdana", "Consolas" };
        // FontFamily nativo aceita lista separada por vírgula como
        // fallback (igual CSS) — Segoe UI é a fonte de sistema, sempre
        // presente, então serve de rede de segurança se a fonte principal
        // não existir no aparelho.
        private static readonly string[] FontFamilyValues =
        {
            "Georgia,Segoe UI",
            "Segoe UI",
            "Times New Roman,Segoe UI",
            "Verdana,Segoe UI",
            "Consolas,Segoe UI",
        };

        private const double BaseFontSize = 16.0;

        private string _bookId;
        private StorageFolder _bookFolder;
        private EpubManifest _manifest;
        private int _chapterIndex;
        private int _pageIndexInChapter;
        private int _totalPagesInChapter = 1;
        private List<FrameworkElement> _currentPages = new List<FrameworkElement>();
        private bool _initialChapterLoaded;
        private int _pendingInitialChapter;
        private int? _pendingInitialPage;
        private BookRecord _record;
        private double _manipulationTranslationX;
        private double _manipulationScale = 1.0;
        private ReaderChromeState _state = ReaderChromeState.None;
        private bool _suppressSliderEvents;

        public ReaderEpubPage()
        {
            this.InitializeComponent();
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
                _bookFolder = await EpubExtractor.GetExtractedBookFolderAsync(_bookId);

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

                _pendingInitialChapter = startChapter;
                _pendingInitialPage = startPage;
                _chapterIndex = startChapter;

                // Se o PagesHost ainda não tiver um tamanho de verdade
                // (layout ainda não rodou), TryStartInitialLoadAsync não
                // faz nada agora — PagesHost_SizeChanged termina o
                // trabalho assim que o primeiro tamanho real chegar.
                await TryStartInitialLoadAsync();
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

        private async Task TryStartInitialLoadAsync()
        {
            if (_initialChapterLoaded || _manifest == null)
            {
                return;
            }
            if (PagesHost.ActualWidth <= 0 || PagesHost.ActualHeight <= 0)
            {
                return;
            }
            _initialChapterLoaded = true;
            await OpenChapterAsync(_pendingInitialChapter, _pendingInitialPage);
        }

        private async void PagesHost_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            if (e.NewSize.Width <= 0 || e.NewSize.Height <= 0)
            {
                return;
            }

            if (!_initialChapterLoaded)
            {
                await TryStartInitialLoadAsync();
                return;
            }

            if (_manifest == null)
            {
                return;
            }

            // Rotação de tela ou primeira medição de layout — reaplica a
            // paginação pro tamanho novo. Como isso é raro num telefone
            // (orientação fixa na prática), não tenta preservar a posição
            // exata: só reancora no início do capítulo atual.
            await RebuildCurrentChapterPagesAsync();
            ShowPage(0);
            UpdateIndicator();
        }

        // startPage: null = primeira página, -1 = última página (entrando
        // de trás pra frente, ex.: botão Anterior no início de um capítulo).
        private async Task OpenChapterAsync(int chapterIndex, int? startPage)
        {
            _chapterIndex = chapterIndex;
            LoadingRing.IsActive = true;
            LoadingStatusText.Text = "Carregando capítulo...";

            await RebuildCurrentChapterPagesAsync();

            int targetPage;
            if (startPage == null)
            {
                targetPage = 0;
            }
            else if (startPage.Value < 0)
            {
                targetPage = _currentPages.Count - 1;
            }
            else
            {
                targetPage = Math.Min(startPage.Value, _currentPages.Count - 1);
            }

            ShowPage(targetPage);
            UpdateIndicator();
            await SavePositionAsync();

            LoadingRing.IsActive = false;
            LoadingStatusText.Text = string.Empty;
        }

        // O coração da paginação nativa: constrói o capítulo inteiro como
        // Paragraphs, joga no primeiro RichTextBlock, e vai encadeando
        // RichTextBlockOverflow enquanto sobrar conteúdo que não coube —
        // UpdateLayout() força uma passada de layout síncrona depois de
        // cada container novo, senão HasOverflowContent ainda não teria
        // sido calculado (só fica certo depois que o elemento é medido).
        private async Task RebuildCurrentChapterPagesAsync()
        {
            ReaderTextStyle style = BuildCurrentTextStyle();
            List<Paragraph> paragraphs = await EpubContentParser.ParseChapterAsync(
                _bookFolder, _manifest.SpineFiles[_chapterIndex], style);

            PagesHost.Children.Clear();
            _currentPages = new List<FrameworkElement>();

            double pageWidth = PagesHost.ActualWidth;
            double pageHeight = PagesHost.ActualHeight;
            int marginPx = ReaderPreferenceStore.GetMarginPx();

            RichTextBlock main = new RichTextBlock
            {
                Width = pageWidth,
                Height = pageHeight,
                Padding = new Thickness(marginPx),
                IsTextSelectionEnabled = false,
            };
            foreach (Paragraph paragraph in paragraphs)
            {
                main.Blocks.Add(paragraph);
            }

            PagesHost.Children.Add(main);
            main.UpdateLayout();
            _currentPages.Add(main);

            FrameworkElement previous = main;
            bool hasMore = main.HasOverflowContent;
            while (hasMore)
            {
                RichTextBlockOverflow overflow = new RichTextBlockOverflow
                {
                    Width = pageWidth,
                    Height = pageHeight,
                    Padding = new Thickness(marginPx),
                };

                RichTextBlock previousMain = previous as RichTextBlock;
                if (previousMain != null)
                {
                    previousMain.OverflowContentTarget = overflow;
                }
                else
                {
                    ((RichTextBlockOverflow)previous).OverflowContentTarget = overflow;
                }

                PagesHost.Children.Add(overflow);
                overflow.UpdateLayout();
                _currentPages.Add(overflow);

                hasMore = overflow.HasOverflowContent;
                previous = overflow;
            }

            _totalPagesInChapter = _currentPages.Count;
        }

        private void ShowPage(int pageIndex)
        {
            _pageIndexInChapter = pageIndex;
            for (int i = 0; i < _currentPages.Count; i++)
            {
                _currentPages[i].Visibility = i == pageIndex ? Visibility.Visible : Visibility.Collapsed;
            }
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
                await OpenChapterAsync(entry.SpineIndex, null);
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

        private async Task GoToNextAsync()
        {
            if (_pageIndexInChapter < _totalPagesInChapter - 1)
            {
                ShowPage(_pageIndexInChapter + 1);
                UpdateIndicator();
                await SavePositionAsync();
            }
            else if (_manifest != null && _chapterIndex < _manifest.SpineFiles.Count - 1)
            {
                await OpenChapterAsync(_chapterIndex + 1, null);
            }
        }

        private async Task GoToPreviousAsync()
        {
            if (_pageIndexInChapter > 0)
            {
                ShowPage(_pageIndexInChapter - 1);
                UpdateIndicator();
                await SavePositionAsync();
            }
            else if (_manifest != null && _chapterIndex > 0)
            {
                await OpenChapterAsync(_chapterIndex - 1, -1);
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

        // Mudar fonte/tema/espaçamento/margem/justificação muda quantas
        // páginas cabem no capítulo — por isso reconstrói a paginação do
        // zero (volta pra página 0) em vez de tentar preservar o índice de
        // página antigo, que ficaria errado.
        private async Task ReapplyStyleAndRepaginateAsync()
        {
            if (_manifest == null || _bookFolder == null)
            {
                return;
            }
            await RebuildCurrentChapterPagesAsync();
            ShowPage(0);
            UpdateIndicator();
            await SavePositionAsync();
        }

        // Cor/tamanho/fonte/espaçamento/alinhamento aplicados direto em
        // cada Paragraph (ver EpubContentParser) — nada de injeção de
        // CSS, nada de motor de renderização terceiro decidindo se a
        // propriedade "pegou" ou não.
        private ReaderTextStyle BuildCurrentTextStyle()
        {
            string theme = ReaderPreferenceStore.GetTheme();
            Color foregroundColor;
            Color backgroundColor;
            switch (theme)
            {
                case "sepia":
                    foregroundColor = Color.FromArgb(0xFF, 0x5B, 0x46, 0x36);
                    backgroundColor = Color.FromArgb(0xFF, 0xF4, 0xEC, 0xD8);
                    break;
                case "dark":
                    foregroundColor = Color.FromArgb(0xFF, 0xE8, 0xE8, 0xEA);
                    backgroundColor = Color.FromArgb(0xFF, 0x1B, 0x1B, 0x1F);
                    break;
                default:
                    foregroundColor = Color.FromArgb(0xFF, 0x1B, 0x1B, 0x1F);
                    backgroundColor = Colors.White;
                    break;
            }
            PagesHost.Background = new SolidColorBrush(backgroundColor);

            double fontSize = BaseFontSize * ReaderPreferenceStore.GetFontSizePercent() / 100.0;
            double lineHeight = fontSize * ReaderPreferenceStore.GetLineSpacing();
            TextAlignment textAlignment = ReaderPreferenceStore.GetJustification() == "justify"
                ? TextAlignment.Justify
                : TextAlignment.Left;

            return new ReaderTextStyle
            {
                FontSize = fontSize,
                FontFamily = new FontFamily(ReaderPreferenceStore.GetFontFamily()),
                Foreground = new SolidColorBrush(foregroundColor),
                LineHeight = lineHeight,
                TextAlignment = textAlignment,
            };
        }
    }
}
