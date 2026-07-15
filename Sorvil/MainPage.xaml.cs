using System;
using Sorvil.Views;
using Windows.UI;
using Windows.UI.Core;
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
    // Mesmo padrão do shell usado no the artistsway/uwp — mas com a
    // paleta de marca fixa do Sorvil (verde/cinza claro/carvão), não a cor
    // de destaque do sistema, então não precisa acompanhar troca de tema
    // em tempo real como antes.
    public sealed partial class MainPage : Page
    {
        public static MainPage Current { get; private set; }

        private static readonly SolidColorBrush ActiveTabBrush = new SolidColorBrush(Color.FromArgb(0xFF, 0x32, 0x60, 0x19));
        private static readonly SolidColorBrush InactiveTabBrush = new SolidColorBrush(Color.FromArgb(0xFF, 0xDD, 0xDD, 0xDD));

        private Type _currentTabPageType;

        public MainPage()
        {
            try
            {
                this.InitializeComponent();
                Current = this;
                this.Loaded += MainPage_Loaded;
                ContentFrame.Navigated += ContentFrame_Navigated;
                SystemNavigationManager.GetForCurrentView().BackRequested += OnBackRequested;
            }
            catch (Exception ex)
            {
                ShowFatalError("Erro ao iniciar a página: " + ex.Message);
            }
        }

        // A gaveta ocupa 40% da largura da tela (não um valor fixo em px)
        // — OpenPaneLength não aceita porcentagem direto no XAML, então
        // recalcula a cada mudança de tamanho/rotação.
        private void RootLayoutGrid_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            if (e.NewSize.Width > 0)
            {
                NavSplitView.OpenPaneLength = e.NewSize.Width * 0.4;
            }
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
            // Verde mais escuro que o do hambúrguer, pra diferenciar "onde
            // está o controle" (hambúrguer, verde forte) de "onde você
            // está" (item ativo, verde mais escuro) — cor fixa de marca,
            // não a cor de destaque do sistema.
            bool isHome = pageType == typeof(HomePage);
            bool isLibrary = pageType == typeof(LibraryPage);
            bool isDownloads = pageType == typeof(DownloadsPage);
            bool isSettings = pageType == typeof(SettingsPage);

            SetTabForeground(NavHomeLabel, NavHomeIcon, isHome);
            SetTabForeground(NavLibraryLabel, NavLibraryIcon, isLibrary);
            SetTabForeground(NavDownloadsLabel, NavDownloadsIcon, isDownloads);
            SetTabForeground(NavSettingsLabel, NavSettingsIcon, isSettings);

            if (isHome) HeaderTitleText.Text = "Início";
            else if (isLibrary) HeaderTitleText.Text = "Biblioteca";
            else if (isDownloads) HeaderTitleText.Text = "Baixados";
            else if (isSettings) HeaderTitleText.Text = "Ajustes";
        }

        private static void SetTabForeground(TextBlock label, IconElement icon, bool active)
        {
            Brush brush = active ? ActiveTabBrush : InactiveTabBrush;
            label.Foreground = brush;
            icon.Foreground = brush;
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
