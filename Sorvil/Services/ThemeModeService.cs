using Windows.UI;
using Windows.UI.ViewManagement;
using Windows.UI.Xaml;

namespace Sorvil.Services
{
    // Aplica claro/escuro/automático no app inteiro. "auto" usa
    // ElementTheme.Default, que já significa "seguir o tema do sistema"
    // nativamente no UWP — não precisa reaplicar quando o sistema muda,
    // o próprio framework já atualiza os brushes tema-aware sozinho.
    //
    // A barra de status do telefone (relógio, wi-fi, bateria) é um
    // componente separado (Windows.UI.ViewManagement.StatusBar) que NÃO
    // acompanha o tema do app sozinho — sem isso, ela fica sempre com a
    // cor com que o sistema iniciou, ficando invisível quando o app troca
    // pro tema Claro (mesmo bug que o theartistsway teve).
    public static class ThemeModeService
    {
        public static void Apply(string themeMode)
        {
            if (!(Window.Current?.Content is FrameworkElement root))
            {
                return;
            }

            switch (themeMode)
            {
                case "light":
                    root.RequestedTheme = ElementTheme.Light;
                    break;
                case "dark":
                    root.RequestedTheme = ElementTheme.Dark;
                    break;
                default:
                    root.RequestedTheme = ElementTheme.Default;
                    break;
            }

            ApplyStatusBarColors(root.ActualTheme);
        }

        // Chamado uma vez no lançamento do app — garante que a barra de
        // status também acompanhe trocas de tema do sistema em tempo real
        // quando o modo escolhido é "automático".
        public static void AttachStatusBarSync()
        {
            if (!(Window.Current?.Content is FrameworkElement root))
            {
                return;
            }

            root.ActualThemeChanged -= OnActualThemeChanged;
            root.ActualThemeChanged += OnActualThemeChanged;
        }

        private static void OnActualThemeChanged(FrameworkElement sender, object args)
        {
            ApplyStatusBarColors(sender.ActualTheme);
        }

        private static void ApplyStatusBarColors(ElementTheme actualTheme)
        {
            StatusBar statusBar = StatusBar.GetForCurrentView();
            if (statusBar == null)
            {
                // Não é Windows 10 Mobile (StatusBar só existe na família
                // de telefone) — nada a fazer.
                return;
            }

            bool isLight = actualTheme == ElementTheme.Light;
            statusBar.ForegroundColor = isLight ? Colors.Black : Colors.White;
            statusBar.BackgroundColor = isLight ? Colors.White : Colors.Black;
            statusBar.BackgroundOpacity = 1;
        }
    }
}
