using Windows.UI.Xaml;
using Windows.UI.Xaml.Media;

namespace Sorvil.Services
{
    // Único lugar do app que lê a cor de destaque do sistema, pra não
    // espalhar a mesma leitura de recurso em cada página.
    public static class ThemeHelper
    {
        public static SolidColorBrush AccentBrush()
        {
            return new SolidColorBrush((Windows.UI.Color)Application.Current.Resources["SystemAccentColor"]);
        }
    }
}
