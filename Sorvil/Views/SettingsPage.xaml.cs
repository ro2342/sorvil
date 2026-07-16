using Sorvil.Models;
using Sorvil.Services;
using System;
using System.Threading.Tasks;
using Windows.Storage;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;

namespace Sorvil.Views
{
    public sealed partial class SettingsPage : Page
    {
        private bool _isApplyingLoadedTheme;

        public SettingsPage()
        {
            this.InitializeComponent();
            this.Loaded += SettingsPage_Loaded;
        }

        private void SettingsPage_Loaded(object sender, RoutedEventArgs e)
        {
            ServerProfile profile = ServerConfigStore.Load();
            BaseUrlBox.Text = profile.BaseUrl ?? string.Empty;
            UsernameBox.Text = profile.Username ?? string.Empty;
            PasswordBox.Password = profile.Password ?? string.Empty;

            LoadThemeSelection();

            VersionText.Text = "Versão instalada: " + UpdateCheckService.GetInstalledVersion();
        }

        // — Servidor —

        private async void TestButton_Click(object sender, RoutedEventArgs e)
        {
            TestButton.IsEnabled = false;
            SaveButton.IsEnabled = false;
            TestProgress.IsActive = true;
            StatusText.Text = string.Empty;

            OpdsConnectionResult result = await OpdsClient.TestConnectionAsync(
                BaseUrlBox.Text, UsernameBox.Text, PasswordBox.Password);

            TestProgress.IsActive = false;
            TestButton.IsEnabled = true;
            SaveButton.IsEnabled = true;

            StatusText.Text = result.Success
                ? "Conectado! O catálogo OPDS respondeu normalmente."
                : "Não consegui conectar: " + result.Error;
        }

        private void SaveButton_Click(object sender, RoutedEventArgs e)
        {
            ServerProfile profile = new ServerProfile
            {
                BaseUrl = BaseUrlBox.Text?.Trim(),
                Username = UsernameBox.Text,
                Password = PasswordBox.Password,
            };
            ServerConfigStore.Save(profile);
            StatusText.Text = "Salvo.";
        }

        // — Aparência —

        private void LoadThemeSelection()
        {
            _isApplyingLoadedTheme = true;
            string mode = ThemePreferenceStore.Get();
            ThemeAutoRadio.IsChecked = mode == "auto";
            ThemeLightRadio.IsChecked = mode == "light";
            ThemeDarkRadio.IsChecked = mode == "dark";
            _isApplyingLoadedTheme = false;
        }

        private void ThemeRadio_Checked(object sender, RoutedEventArgs e)
        {
            if (_isApplyingLoadedTheme)
            {
                return;
            }
            string mode = (string)((RadioButton)sender).Tag;
            ThemePreferenceStore.Set(mode);
            ThemeModeService.Apply(mode);
        }

        // — Sobre / Atualização —

        // Arquivo já baixado, pronto pra instalar — enquanto for null, o
        // botão (se visível) tenta baixar; assim que o download termina,
        // vira só "abrir o instalador", sem baixar de novo.
        private StorageFile _downloadedUpdateFile;

        private async void CheckUpdate_Click(object sender, RoutedEventArgs e)
        {
            UpdateStatusText.Text = "Checando...";
            UpdateActionButton.Visibility = Visibility.Collapsed;
            _downloadedUpdateFile = null;

            UpdateCheckResult result = await UpdateCheckService.CheckAsync();
            if (!result.Success)
            {
                UpdateStatusText.Text = "Não consegui checar: " + result.Error;
                return;
            }

            if (!result.UpdateAvailable)
            {
                UpdateStatusText.Text = "Você já está na versão mais recente.";
                return;
            }

            // Achou atualização — baixa na hora, sem exigir um segundo
            // toque só pra iniciar o download; só pede um toque a mais
            // (no botão) quando já tem o instalador pronto.
            UpdateStatusText.Text = "Nova versão disponível: " + result.Latest + " — baixando...";
            await DownloadUpdateAsync();
        }

        private async void UpdateActionButton_Click(object sender, RoutedEventArgs e)
        {
            if (_downloadedUpdateFile != null)
            {
                UpdateStatusText.Text = "Abrindo o instalador...";
                await Windows.System.Launcher.LaunchFileAsync(_downloadedUpdateFile);
                return;
            }
            await DownloadUpdateAsync();
        }

        private async Task DownloadUpdateAsync()
        {
            UpdateActionButton.IsEnabled = false;
            UpdateProgress.Visibility = Visibility.Visible;
            UpdateProgress.Value = 0;

            try
            {
                Progress<double> progress = new Progress<double>(value => UpdateProgress.Value = value);
                StorageFile file = await UpdateCheckService.DownloadUpdateAsync(progress);
                if (file == null)
                {
                    UpdateStatusText.Text = "Download cancelado (nenhuma pasta escolhida).";
                    UpdateActionButton.Content = "Baixar de novo";
                    UpdateActionButton.Visibility = Visibility.Visible;
                }
                else
                {
                    _downloadedUpdateFile = file;
                    UpdateStatusText.Text = "Baixado! Toque em \"Instalar\" pra atualizar.";
                    UpdateActionButton.Content = "Instalar";
                    UpdateActionButton.Visibility = Visibility.Visible;
                }
            }
            catch (Exception ex)
            {
                UpdateStatusText.Text = "Erro no download: " + ex.Message;
                UpdateActionButton.Content = "Tentar de novo";
                UpdateActionButton.Visibility = Visibility.Visible;
            }
            finally
            {
                UpdateActionButton.IsEnabled = true;
                UpdateProgress.Visibility = Visibility.Collapsed;
            }
        }
    }
}
