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
        private const string TapCornersKey = "ReaderTapCornersEnabled";
        private const string SwipeKey = "ReaderSwipeEnabled";
        private const string PinchToZoomKey = "ReaderPinchToZoomEnabled";

        public static int GetFontSizePercent()
        {
            object value = ApplicationData.Current.LocalSettings.Values[FontSizeKey];
            return value is int percent ? percent : 180;
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

        // Ligado por padrão — é o gesto que já existia desde o v1 (toque
        // nas bordas da tela vira página), então continua sendo o
        // comportamento padrão até o usuário desligar.
        public static bool GetTapCornersEnabled()
        {
            object value = ApplicationData.Current.LocalSettings.Values[TapCornersKey];
            return !(value is bool enabled) || enabled;
        }

        public static void SetTapCornersEnabled(bool enabled)
        {
            ApplicationData.Current.LocalSettings.Values[TapCornersKey] = enabled;
        }

        // Desligado por padrão — opcional, pra quem prefere arrastar em
        // vez de tocar nas bordas.
        public static bool GetSwipeEnabled()
        {
            object value = ApplicationData.Current.LocalSettings.Values[SwipeKey];
            return value is bool enabled && enabled;
        }

        public static void SetSwipeEnabled(bool enabled)
        {
            ApplicationData.Current.LocalSettings.Values[SwipeKey] = enabled;
        }

        // Desligado por padrão — pinça pra ajustar fonte é um gesto fácil
        // de acionar sem querer ao virar página, então fica opt-in.
        public static bool GetPinchToZoomEnabled()
        {
            object value = ApplicationData.Current.LocalSettings.Values[PinchToZoomKey];
            return value is bool enabled && enabled;
        }

        public static void SetPinchToZoomEnabled(bool enabled)
        {
            ApplicationData.Current.LocalSettings.Values[PinchToZoomKey] = enabled;
        }
    }
}
