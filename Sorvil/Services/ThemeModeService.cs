using Windows.UI.Xaml;

namespace Sorvil.Services
{
    // Aplica claro/escuro/automático no app inteiro. "auto" usa
    // ElementTheme.Default, que já significa "seguir o tema do sistema"
    // nativamente no UWP — não precisa reaplicar quando o sistema muda,
    // o próprio framework já atualiza os brushes tema-aware sozinho.
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
        }
    }
}
