using Sorvil.Models;
using Sorvil.Views;

namespace Sorvil.Services
{
    // Decide pra qual leitor navegar de acordo com o formato do
    // BookRecord — reaproveitado por Baixados e pelas seções da Home
    // ("Você está lendo", "Adicionados recentemente"). Sempre navega no
    // Frame raiz da janela (App.RootFrame), nunca no ContentFrame aninhado
    // do MainPage — o leitor precisa tomar conta da tela inteira, por
    // cima do HeaderBar/SplitView do shell, não confinado abaixo dele.
    public static class ReaderNavigation
    {
        public static bool TryOpen(BookRecord record)
        {
            if (record.Format == "pdf")
            {
                App.RootFrame.Navigate(typeof(ReaderPdfPage), record.Id);
                return true;
            }
            if (record.Format == "epub" || record.Format == "kepub.epub")
            {
                App.RootFrame.Navigate(typeof(ReaderEpubPage), record.Id);
                return true;
            }
            return false;
        }
    }
}
