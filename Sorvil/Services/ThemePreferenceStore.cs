using Windows.Storage;

namespace Sorvil.Services
{
    public static class ThemePreferenceStore
    {
        private const string Key = "ThemeMode";

        public static string Get()
        {
            object value = ApplicationData.Current.LocalSettings.Values[Key];
            return value as string ?? "auto";
        }

        public static void Set(string mode)
        {
            ApplicationData.Current.LocalSettings.Values[Key] = mode;
        }
    }
}
