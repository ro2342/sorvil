using Windows.UI.Xaml;

namespace Sorvil.Services
{
    // Aplica claro/escuro/automático no app inteiro. "auto" usa
    // ElementTheme.Default, que já significa "seguir o tema do sistema"
    // nativamente no UWP — não precisa reaplicar quando o sistema muda,
    // o próprio framework já atualiza os brushes tema-aware sozinho.
    //
    // Uma tentativa anterior também tentava colorir a barra de status do
    // telefone (Windows.UI.ViewManagement.StatusBar), mas isso exigia uma
    // <SDKReference> pra extensão "Windows Mobile" que coincide
    // exatamente com o build em que um crash real de lançamento
    // ("InvalidCastException, InvalidCast_WinRT ... Frame") apareceu no
    // aparelho — removido até isolar/confirmar a causa com mais calma;
    // a cor da barra de status ficando errada no tema Claro é só estética
    // e não vale a pena arriscar o app nem abrir por causa disso.
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
