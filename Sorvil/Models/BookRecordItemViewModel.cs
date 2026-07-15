using System.ComponentModel;
using Windows.UI.Xaml.Media;

namespace Sorvil.Models
{
    // Wrapper de exibição em volta de um BookRecord (livro já baixado) —
    // usado tanto em Baixados quanto nas seções "Você está lendo" e
    // "Adicionados recentemente" da Home. Só a capa muda depois da
    // criação (carrega assíncrono do cache local), por isso é a única
    // propriedade com INotifyPropertyChanged.
    public sealed class BookRecordItemViewModel : INotifyPropertyChanged
    {
        public BookRecord Record { get; }
        public string Title => Record.Title;
        public string Subtitle => (Record.Author ?? string.Empty) + " · " + Record.Format;

        private ImageSource _cover;
        public ImageSource Cover
        {
            get { return _cover; }
            set
            {
                _cover = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Cover"));
            }
        }

        public BookRecordItemViewModel(BookRecord record)
        {
            Record = record;
        }

        public event PropertyChangedEventHandler PropertyChanged;
    }
}
