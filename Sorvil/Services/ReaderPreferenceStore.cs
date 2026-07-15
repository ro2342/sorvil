using Windows.Storage;

namespace Sorvil.Services
{
    // Preferências de leitura do EPUB (tamanho de fonte e tema de leitura)
    // — separado de ThemePreferenceStore porque isso é sobre a aparência
    // do texto do livro dentro do WebView, não do app em si.
    public static class ReaderPreferenceStore
    {
        private const string FontSizeKey = "ReaderFontSizePercent";
        private const string ThemeKey = "ReaderTheme";
        private const string DimLevelKey = "ReaderDimLevelPercent";

        public static int GetFontSizePercent()
        {
            object value = ApplicationData.Current.LocalSettings.Values[FontSizeKey];
            return value is int percent ? percent : 130;
        }

        public static void SetFontSizePercent(int percent)
        {
            ApplicationData.Current.LocalSettings.Values[FontSizeKey] = percent;
        }

        // "light", "sepia" ou "dark"
        public static string GetTheme()
        {
            object value = ApplicationData.Current.LocalSettings.Values[ThemeKey];
            return value as string ?? "light";
        }

        public static void SetTheme(string theme)
        {
            ApplicationData.Current.LocalSettings.Values[ThemeKey] = theme;
        }

        // 0 = sem escurecimento, 80 = quase preto. Puramente um véu por
        // cima da tela (Opacity de um Border) — não é brilho de hardware.
        public static int GetDimLevelPercent()
        {
            object value = ApplicationData.Current.LocalSettings.Values[DimLevelKey];
            return value is int percent ? percent : 0;
        }

        public static void SetDimLevelPercent(int percent)
        {
            ApplicationData.Current.LocalSettings.Values[DimLevelKey] = percent;
        }
    }
}
