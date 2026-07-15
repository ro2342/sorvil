using Sorvil.Models;
using Sorvil.Views;
using Windows.UI.Xaml.Controls;

namespace Sorvil.Services
{
    // Decide pra qual leitor navegar de acordo com o formato do
    // BookRecord — reaproveitado por Baixados e pelas seções da Home
    // ("Você está lendo", "Adicionados recentemente").
    public static class ReaderNavigation
    {
        public static bool TryOpen(Frame frame, BookRecord record)
        {
            if (record.Format == "pdf")
            {
                frame.Navigate(typeof(ReaderPdfPage), record.Id);
                return true;
            }
            if (record.Format == "epub" || record.Format == "kepub.epub")
            {
                frame.Navigate(typeof(ReaderEpubPage), record.Id);
                return true;
            }
            return false;
        }
    }
}
