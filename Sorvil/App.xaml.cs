using System;
using Sorvil.Services;
using Windows.ApplicationModel;
using Windows.ApplicationModel.Activation;
using Windows.UI.ViewManagement;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Navigation;

namespace Sorvil
{
    // Ponto de entrada do app — navega direto pro shell nativo (MainPage),
    // que cuida do resto. Sem onboarding: a primeira coisa que o usuário vê
    // é a Home; se o servidor ainda não estiver configurado, a Home é quem
    // decide mostrar um aviso levando pra aba Servidor de Ajustes.
    sealed partial class App : Application
    {
        // Frame raiz da janela — exposto pra páginas de leitura poderem
        // navegar por cima do shell inteiro (MainPage, com seu HeaderBar
        // verde fixo), em vez de dentro do ContentFrame aninhado do
        // MainPage. Sem isso, o leitor ficava confinado numa área menor
        // (abaixo do HeaderBar), o que parecia "não tela cheia"/"borda
        // preta" e deixava o cabeçalho do shell sempre visível durante a
        // leitura.
        public static Frame RootFrame { get; private set; }

        public App()
        {
            this.InitializeComponent();
            this.Suspending += OnSuspending;
            this.UnhandledException += App_UnhandledException;
        }

        private async void App_UnhandledException(object sender, Windows.UI.Xaml.UnhandledExceptionEventArgs e)
        {
            e.Handled = true;
            try
            {
                var dialog = new Windows.UI.Popups.MessageDialog(e.Message ?? "Erro desconhecido", "Erro inesperado no app");
                await dialog.ShowAsync();
            }
            catch
            {
                // Se nem o diálogo conseguir abrir, não há mais nada a fazer aqui.
            }
        }

        protected override void OnLaunched(LaunchActivatedEventArgs e)
        {
            Frame rootFrame = Window.Current.Content as Frame;

            if (rootFrame == null)
            {
                rootFrame = new Frame();
                rootFrame.NavigationFailed += OnNavigationFailed;
                Window.Current.Content = rootFrame;
                ThemeModeService.Apply(ThemePreferenceStore.Get());
            }

            RootFrame = rootFrame;

            if (e.PrelaunchActivated == false)
            {
                if (rootFrame.Content == null)
                {
                    rootFrame.Navigate(typeof(MainPage), e.Arguments);
                }
                Window.Current.Activate();

                // Tela cheia de verdade (sem barra de status) em vez de
                // tentar colorir a StatusBar — essa API é universal (não
                // exige a extensão "Windows Mobile" que causou o crash
                // anterior) e resolve o problema dos ícones brancos
                // invisíveis no tema Claro simplesmente não mostrando a
                // barra de status nenhuma, igual os apps nativos (News,
                // Forecast) fazem.
                ApplicationView.GetForCurrentView().TryEnterFullScreenMode();
            }
        }

        void OnNavigationFailed(object sender, NavigationFailedEventArgs e)
        {
            throw new Exception("Falha ao carregar a página: " + e.SourcePageType.FullName);
        }

        private void OnSuspending(object sender, SuspendingEventArgs e)
        {
            var deferral = e.SuspendingOperation.GetDeferral();
            deferral.Complete();
        }
    }
}
