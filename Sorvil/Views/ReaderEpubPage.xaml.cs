using Sorvil.Models;
using Sorvil.Services;
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Windows.Data.Json;
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
    // Leitor de EPUB/KEPUB — a renderização/paginação de verdade é feita
    // pelo epub.js (github.com/futurepress/epub.js, vendorizado em
    // Assets/EpubJs/, build "legacy" pra engines mais antigas) rodando
    // dentro da WebView, não por script nosso escrito à mão. Depois de
    // três rodadas de bugs de paginação (CSS multi-column vazando uma
    // segunda coluna de verdade, depois rolagem manual pulando de
    // capítulo errado) numa engine antiga e mal documentada, a decisão
    // foi trocar pra uma biblioteca madura, testada em produção há anos,
    // em vez de continuar reescrevendo a mesma lógica à mão.
    //
    // Ponte C#<->JS: ContentWebView.Navigate leva pro bootstrap estático
    // Assets/EpubJs/reader.html (carrega jszip.min.js + epub.legacy.min.js
    // + reader-bridge.js). Depois que ele termina de carregar
    // (ContentWebView_NavigationCompleted), chamamos
    // SorvilReader.openBook(base64, cfi, styleJson) via InvokeScriptAsync
    // passando o .epub inteiro em base64 como argumento — não como URL
    // pro epub.js buscar sozinho, porque não dava pra confiar que fetch()
    // atravessa o esquema ms-appx (onde mora reader.html) pro ms-appdata
    // (onde mora o arquivo baixado) nessa WebView específica sem poder
    // testar num aparelho de verdade. O JS devolve eventos (pronto,
    // relocalizado, erro) via window.external.notify(JSON), que chegam
    // aqui em ContentWebView_ScriptNotify — nunca fica em silêncio: todo
    // erro (síncrono, de Promise, ou não tratado) vira uma mensagem
    // visível, ver reader-bridge.js.
    //
    // A casca de leitura (barra de cima/baixo, painéis de fonte/tema/
    // gestos, índice) é a mesma máquina de estados de sempre
    // (ReaderChromeState) — só o que preenche o miolo da tela mudou.
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

        private sealed class TocEntry
        {
            public string Label;
            public string Href;
            public int Depth;
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
        private BookRecord _record;
        private List<TocEntry> _toc = new List<TocEntry>();
        private string _pendingBase64;
        private string _pendingStartCfi;
        private string _currentCfi;
        private string _currentHref;
        private double _currentPercentage;
        private bool _bookReady;
        private double _manipulationTranslationX;
        private double _manipulationScale = 1.0;
        private ReaderChromeState _state = ReaderChromeState.None;
        private bool _suppressSliderEvents;
        private int _styleChangeToken;

        public ReaderEpubPage()
        {
            this.InitializeComponent();
            ContentWebView.NavigationCompleted += ContentWebView_NavigationCompleted;
            ContentWebView.NavigationFailed += ContentWebView_NavigationFailed;
            ContentWebView.ScriptNotify += ContentWebView_ScriptNotify;
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
                UpdateWebViewBackgroundColor();

                LoadingStatusText.Text = "Lendo arquivo...";
                StorageFolder booksFolder = await ApplicationData.Current.LocalFolder.GetFolderAsync("Books");
                StorageFile epubFile = await booksFolder.GetFileAsync(_record.LocalFilePath);
                // Convert.ToBase64String em vez de CryptographicBuffer.
                // EncodeToBase64String: essa API do WinRT é pensada pra
                // chave/hash criptográfico, não pra codificar um arquivo
                // inteiro — na prática, deu "Operation aborted (0x80004004
                // E_ABORT)" com um EPUB de tamanho normal. Stream +
                // Convert.ToBase64String são .NET puro, sem esse teto.
                byte[] epubBytes;
                using (Stream fileStream = await epubFile.OpenStreamForReadAsync())
                using (MemoryStream memoryStream = new MemoryStream())
                {
                    await fileStream.CopyToAsync(memoryStream);
                    epubBytes = memoryStream.ToArray();
                }
                _pendingBase64 = Convert.ToBase64String(epubBytes);
                _pendingStartCfi = _record.ReadingPositionJson;

                LoadingStatusText.Text = "Carregando leitor...";
                ContentWebView.Navigate(new Uri("ms-appx:///Assets/EpubJs/reader.html"));
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

        // O WebView pinta com fundo branco por padrão antes de qualquer
        // CSS carregar — setando DefaultBackgroundColor ANTES de navegar,
        // o próprio WebView já nasce com a cor certa, sem flash branco
        // antes do tema escuro aparecer.
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

        // reader.html terminou de carregar (os dois scripts vendorizados +
        // a ponte) — agora manda o livro em si pro epub.js abrir.
        private async void ContentWebView_NavigationCompleted(WebView sender, WebViewNavigationCompletedEventArgs args)
        {
            if (!args.IsSuccess || _pendingBase64 == null)
            {
                return;
            }

            string base64 = _pendingBase64;
            string startCfi = _pendingStartCfi;
            _pendingBase64 = null;
            _pendingStartCfi = null;

            LoadingStatusText.Text = "Abrindo livro...";
            string styleJson = BuildStyleJson();
            await InvokeAsync("SorvilReader.openBook", new[] { base64, startCfi ?? string.Empty, styleJson });
        }

        private void ContentWebView_NavigationFailed(object sender, WebViewNavigationFailedEventArgs args)
        {
            ShowLoadError("Erro ao carregar o leitor.");
        }

        // Mensagens do lado JS (reader-bridge.js) — sempre um JSON com
        // "type". Qualquer coisa que não reconhece é ignorada em vez de
        // derrubar o leitor.
        private async void ContentWebView_ScriptNotify(object sender, NotifyEventArgs e)
        {
            JsonObject msg;
            try
            {
                msg = JsonObject.Parse(e.Value);
            }
            catch (Exception)
            {
                return;
            }

            string type = msg.GetNamedString("type", string.Empty);
            switch (type)
            {
                case "ready":
                    HandleReady(msg);
                    break;
                case "relocated":
                    await HandleRelocatedAsync(msg);
                    break;
                case "error":
                    ShowLoadError(msg.GetNamedString("message", "Erro desconhecido no leitor."));
                    break;
            }
        }

        private void HandleReady(JsonObject msg)
        {
            _toc.Clear();
            JsonArray tocArray = msg.GetNamedArray("toc", new JsonArray());
            foreach (IJsonValue item in tocArray)
            {
                JsonObject entryObj = item.GetObject();
                _toc.Add(new TocEntry
                {
                    Label = entryObj.GetNamedString("label", string.Empty),
                    Href = entryObj.GetNamedString("href", string.Empty),
                    Depth = (int)entryObj.GetNamedNumber("depth", 0),
                });
            }
            PopulateChapterDrawer();
            _bookReady = true;
        }

        private async Task HandleRelocatedAsync(JsonObject msg)
        {
            _currentCfi = msg.GetNamedString("cfi", null);
            _currentHref = msg.GetNamedString("href", null);
            _currentPercentage = msg.GetNamedNumber("percentage", 0);

            UpdateIndicator();
            if (_state == ReaderChromeState.Index)
            {
                HighlightCurrentChapterInDrawer();
            }
            await SavePositionAsync();

            LoadingRing.IsActive = false;
            LoadingStatusText.Text = string.Empty;
        }

        // Progresso vem pronto do epub.js (percentageFromCfi) quando dá
        // pra calcular; o rótulo do capítulo é o item do índice cujo
        // arquivo bate com a seção atual.
        private void UpdateIndicator()
        {
            ScrubberSlider.Value = _currentPercentage * 100.0;

            TocEntry current = FindCurrentTocEntry();
            TbCenterLabel.Text = current != null && !string.IsNullOrEmpty(current.Label)
                ? current.Label
                : "Lendo";
        }

        // — índice (sumário) —

        private void PopulateChapterDrawer()
        {
            ChapterDrawerList.Items.Clear();
            foreach (TocEntry entry in _toc)
            {
                TextBlock text = new TextBlock
                {
                    Text = entry.Label,
                    TextWrapping = TextWrapping.Wrap,
                    Foreground = new SolidColorBrush(Colors.White),
                    Padding = new Thickness(2),
                };
                ListViewItem container = new ListViewItem
                {
                    Content = text,
                    Tag = entry,
                    Padding = new Thickness(18 + entry.Depth * 16, 16, 18, 16),
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
            TocEntry entry = container != null ? container.Tag as TocEntry : null;
            _state = ReaderChromeState.None;
            ApplyReaderChromeState();
            if (entry != null)
            {
                await InvokeAsync("SorvilReader.goToHref", new[] { entry.Href });
            }
        }

        private void HighlightCurrentChapterInDrawer()
        {
            TocEntry current = FindCurrentTocEntry();
            SolidColorBrush activeBackground = new SolidColorBrush(Color.FromArgb(0xFF, 0x32, 0x60, 0x19));
            SolidColorBrush inactiveBackground = new SolidColorBrush(Colors.Transparent);

            for (int i = 0; i < ChapterDrawerList.Items.Count; i++)
            {
                ListViewItem container = (ListViewItem)ChapterDrawerList.Items[i];
                bool isCurrent = ReferenceEquals(container.Tag, current);
                container.Background = isCurrent ? activeBackground : inactiveBackground;
                TextBlock text = (TextBlock)container.Content;
                text.FontWeight = isCurrent ? FontWeights.Bold : FontWeights.Normal;
                if (isCurrent)
                {
                    ChapterDrawerList.ScrollIntoView(container);
                }
            }
        }

        // "Capítulo atual" é a última entrada do índice cujo arquivo
        // (ignorando #fragmento) bate com o href da seção que o epub.js
        // reportou no último "relocated" — comparação tolerante
        // (EndsWith nos dois sentidos) porque o caminho que o epub.js
        // resolve internamente às vezes não é byte-a-byte igual ao href
        // declarado no nav.xhtml/NCX (prefixo de diretório pode diferir).
        private TocEntry FindCurrentTocEntry()
        {
            if (string.IsNullOrEmpty(_currentHref))
            {
                return null;
            }
            TocEntry best = null;
            foreach (TocEntry entry in _toc)
            {
                if (HrefMatches(entry.Href, _currentHref))
                {
                    best = entry;
                }
            }
            return best;
        }

        private static bool HrefMatches(string a, string b)
        {
            string fa = HrefFile(a);
            string fb = HrefFile(b);
            if (string.IsNullOrEmpty(fa) || string.IsNullOrEmpty(fb))
            {
                return false;
            }
            return fa == fb ||
                fa.EndsWith(fb, StringComparison.OrdinalIgnoreCase) ||
                fb.EndsWith(fa, StringComparison.OrdinalIgnoreCase);
        }

        private static string HrefFile(string href)
        {
            if (string.IsNullOrEmpty(href))
            {
                return string.Empty;
            }
            int hashIndex = href.IndexOf('#');
            return hashIndex >= 0 ? href.Substring(0, hashIndex) : href;
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
            if (string.IsNullOrEmpty(_currentCfi))
            {
                return;
            }
            BookRecord record = await LibraryDataStore.GetAsync(_bookId);
            if (record == null)
            {
                return;
            }
            record.ReadingPositionJson = _currentCfi;
            record.LastOpenedAt = DateTimeOffset.UtcNow.ToString("o");
            await LibraryDataStore.SaveAsync(record);
        }

        // — navegação de página (usada pelos botões e pelas zonas de toque) —
        //
        // rendition.next()/prev() do epub.js já cuidam de tudo sozinhos
        // (virar página dentro da seção atual, ou passar pra próxima/
        // anterior seção quando a atual acaba) — não existe mais nenhuma
        // lógica nossa de "isso é página ou é capítulo" pra dar errado.

        private async Task GoToNextAsync()
        {
            await InvokeAsync("SorvilReader.next", new string[0]);
        }

        private async Task GoToPreviousAsync()
        {
            await InvokeAsync("SorvilReader.prev", new string[0]);
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
                    await ApplyStyleAsync();
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

            if (isIndex && _bookReady)
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
                await ApplyStyleAsync();
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
            await DebouncedApplyStyleAsync();
        }

        private async void LineSpacingSlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
        {
            if (_suppressSliderEvents)
            {
                return;
            }
            ReaderPreferenceStore.SetLineSpacing(Math.Round(e.NewValue, 1));
            await DebouncedApplyStyleAsync();
        }

        private async void MarginSlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
        {
            if (_suppressSliderEvents)
            {
                return;
            }
            ReaderPreferenceStore.SetMarginPx((int)e.NewValue);
            await DebouncedApplyStyleAsync();
        }

        // Arrastar um Slider dispara ValueChanged uma vez por pixel — sem
        // isso, cada tique da arrastada dispararia sua própria chamada
        // pra WebView, e várias ficariam concorrentes ao mesmo tempo; a
        // que terminar por último "vence" mesmo que não seja a mais
        // recente. Só a chamada mais recente (depois de a arrastada
        // ficar quieta por 250ms) de fato reaplica o estilo — as
        // intermediárias são descartadas. O gesto de pinça não precisa
        // disso porque só dispara uma vez, no fim do gesto
        // (ManipulationCompleted), não a cada frame.
        private async Task DebouncedApplyStyleAsync()
        {
            int myToken = ++_styleChangeToken;
            await Task.Delay(250);
            if (myToken != _styleChangeToken)
            {
                return;
            }
            await ApplyStyleAsync();
        }

        private async void ApplyJustification(string value)
        {
            ReaderPreferenceStore.SetJustification(value);
            RefreshSegRow(JustificationRow, value);
            await ApplyStyleAsync();
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
            await ApplyStyleAsync();
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

        // — ponte com o epub.js —

        private async Task<string> InvokeAsync(string functionName, string[] args)
        {
            try
            {
                return await ContentWebView.InvokeScriptAsync(functionName, args);
            }
            catch (Exception)
            {
                return null;
            }
        }

        private async Task ApplyStyleAsync()
        {
            if (!_bookReady)
            {
                return;
            }
            string styleJson = BuildStyleJson();
            await InvokeAsync("SorvilReader.setStyle", new[] { styleJson });
        }

        // JsonObject.Stringify() cuida de toda a escapagem — depois de
        // levar um bug feio de montar CSS/JS na mão com concatenação de
        // string (aspas de font-family quebrando a sintaxe inteira), tudo
        // que cruza a ponte pro JS passa a ir por aqui ou por argumentos
        // separados do InvokeScriptAsync, nunca mais por string concatenada
        // virando código.
        private string BuildStyleJson()
        {
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

            JsonObject obj = new JsonObject();
            obj["fontSize"] = JsonValue.CreateNumberValue(ReaderPreferenceStore.GetFontSizePercent());
            obj["margin"] = JsonValue.CreateNumberValue(ReaderPreferenceStore.GetMarginPx());
            obj["lineHeight"] = JsonValue.CreateNumberValue(ReaderPreferenceStore.GetLineSpacing());
            obj["justification"] = JsonValue.CreateStringValue(ReaderPreferenceStore.GetJustification());
            obj["fontFamily"] = JsonValue.CreateStringValue(ReaderPreferenceStore.GetFontFamily());
            obj["background"] = JsonValue.CreateStringValue(background);
            obj["foreground"] = JsonValue.CreateStringValue(foreground);
            return obj.Stringify();
        }
    }
}
