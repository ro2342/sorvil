using System;
using Sorvil.Services;
using Sorvil.Views;
using Windows.UI.Core;
using Windows.UI.ViewManagement;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Input;
using Windows.UI.Xaml.Media;
using Windows.UI.Xaml.Navigation;

namespace Sorvil
{
    // Shell de navegação nativo: um Frame pro conteúdo + um cabeçalho fixo
    // no topo (hambúrguer + título da seção atual) que abre um SplitView
    // deslizando por cima do conteúdo, no espírito da barra de cima dos apps
    // nativos da Microsoft (News/Forecast/Settings: "☰ Nome da seção").
    // Mesmo padrão do shell usado no the artistsway/uwp.
    public sealed partial class MainPage : Page
    {
        public static MainPage Current { get; private set; }

        private Type _currentTabPageType;
        private readonly UISettings _uiSettings = new UISettings();

        public MainPage()
        {
            try
            {
                this.InitializeComponent();
                Current = this;
                StyleMenuButton();
                this.Loaded += MainPage_Loaded;
                ContentFrame.Navigated += ContentFrame_Navigated;
                SystemNavigationManager.GetForCurrentView().BackRequested += OnBackRequested;

                // SystemAccentColor já é {ThemeResource} em tudo que a gente
                // não copia manualmente, então atualiza sozinho — mas o
                // MenuButton e o item ativo do painel usam SolidColorBrush
                // copiado uma vez (ThemeHelper.AccentBrush), que não
                // acompanha a troca ao vivo.
                _uiSettings.ColorValuesChanged += UiSettings_ColorValuesChanged;
            }
            catch (Exception ex)
            {
                ShowFatalError("Erro ao iniciar a página: " + ex.Message);
            }
        }

        private async void UiSettings_ColorValuesChanged(UISettings sender, object args)
        {
            await Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () =>
            {
                StyleMenuButton();
                if (_currentTabPageType != null)
                {
                    UpdateActiveTab(_currentTabPageType);
                }
            });
        }

        private void StyleMenuButton()
        {
            SolidColorBrush accent = ThemeHelper.AccentBrush();
            MenuButton.Background = accent;
            MenuButton.Foreground = new SolidColorBrush(Windows.UI.Colors.White);
        }

        private void MainPage_Loaded(object sender, RoutedEventArgs e)
        {
            try
            {
                HeaderBar.Visibility = Visibility.Visible;
                NavigateToTab(typeof(HomePage));
            }
            catch (Exception ex)
            {
                ShowFatalError("Erro ao carregar o app: " + ex.Message);
            }
        }

        // — painel de navegação —

        private void MenuButton_Click(object sender, RoutedEventArgs e)
        {
            SetPaneOpen(!NavSplitView.IsPaneOpen);
        }

        private void PaneDismissOverlay_Tapped(object sender, TappedRoutedEventArgs e)
        {
            SetPaneOpen(false);
        }

        private void SetPaneOpen(bool open)
        {
            NavSplitView.IsPaneOpen = open;
            PaneDismissOverlay.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
        }

        private void NavItem_Click(object sender, RoutedEventArgs e)
        {
            string tag = (string)((FrameworkElement)sender).Tag;
            Type pageType;
            switch (tag)
            {
                case "Home":
                    pageType = typeof(HomePage);
                    break;
                case "Library":
                    pageType = typeof(LibraryPage);
                    break;
                case "Downloads":
                    pageType = typeof(DownloadsPage);
                    break;
                case "Settings":
                    pageType = typeof(SettingsPage);
                    break;
                default:
                    return;
            }
            NavigateToTab(pageType);
            SetPaneOpen(false);
        }

        public void NavigateToTab(Type pageType, object parameter = null)
        {
            ContentFrame.Navigate(pageType, parameter);
            ContentFrame.BackStack.Clear();
            _currentTabPageType = pageType;
            UpdateActiveTab(pageType);
        }

        private void UpdateActiveTab(Type pageType)
        {
            // Nunca calcula um brush "padrão" na mão aqui: um lookup via
            // Application.Current.Resources[...] não acompanha troca de
            // tema em tempo real. Em vez disso, limpa o valor local
            // (ClearValue) pra herdar o Foreground padrão, que É
            // theme-aware de verdade via {ThemeResource}.
            SolidColorBrush accent = ThemeHelper.AccentBrush();

            bool isHome = pageType == typeof(HomePage);
            bool isLibrary = pageType == typeof(LibraryPage);
            bool isDownloads = pageType == typeof(DownloadsPage);
            bool isSettings = pageType == typeof(SettingsPage);

            SetTabForeground(NavHomeLabel, NavHomeIcon, isHome, accent);
            SetTabForeground(NavLibraryLabel, NavLibraryIcon, isLibrary, accent);
            SetTabForeground(NavDownloadsLabel, NavDownloadsIcon, isDownloads, accent);
            SetTabForeground(NavSettingsLabel, NavSettingsIcon, isSettings, accent);

            if (isHome) HeaderTitleText.Text = "Início";
            else if (isLibrary) HeaderTitleText.Text = "Biblioteca";
            else if (isDownloads) HeaderTitleText.Text = "Baixados";
            else if (isSettings) HeaderTitleText.Text = "Ajustes";
        }

        private static void SetTabForeground(TextBlock label, IconElement icon, bool active, Brush accent)
        {
            if (active)
            {
                label.Foreground = accent;
                icon.Foreground = accent;
            }
            else
            {
                label.ClearValue(TextBlock.ForegroundProperty);
                icon.ClearValue(IconElement.ForegroundProperty);
            }
        }

        // — navegação/voltar —

        private void ContentFrame_Navigated(object sender, NavigationEventArgs e)
        {
            SystemNavigationManager.GetForCurrentView().AppViewBackButtonVisibility =
                ContentFrame.CanGoBack ? AppViewBackButtonVisibility.Visible : AppViewBackButtonVisibility.Collapsed;
        }

        private void OnBackRequested(object sender, BackRequestedEventArgs e)
        {
            // MainPage continua inscrita nesse evento mesmo depois de o
            // leitor assumir a tela inteira no Frame raiz (RootFrame.Navigate
            // não desliga essa inscrição) — sem essa guarda, o botão Voltar
            // do sistema acabaria fechando o painel/navegando o ContentFrame
            // de um MainPage que nem está mais visível.
            if (this.Frame.Content != this)
            {
                return;
            }

            if (NavSplitView.IsPaneOpen)
            {
                e.Handled = true;
                SetPaneOpen(false);
                return;
            }
            if (ContentFrame.CanGoBack)
            {
                e.Handled = true;
                ContentFrame.GoBack();
            }
        }

        // — erro fatal —

        private void ShowFatalError(string message)
        {
            if (ErrorText != null && ErrorPanel != null)
            {
                ErrorText.Text = message;
                ErrorPanel.Visibility = Visibility.Visible;
            }
        }
    }
}
