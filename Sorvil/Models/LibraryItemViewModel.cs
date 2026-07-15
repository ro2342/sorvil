using System.ComponentModel;
using Windows.UI.Xaml.Media;

namespace Sorvil.Models
{
    // Wrapper de exibição em volta de um OpdsEntry — só a capa muda depois
    // da criação (carrega assíncrono), por isso é a única propriedade que
    // precisa de INotifyPropertyChanged pra o binding do GridView/ListView
    // atualizar sozinho quando a imagem termina de baixar.
    public sealed class LibraryItemViewModel : INotifyPropertyChanged
    {
        public OpdsEntry Entry { get; }

        public string Title => Entry.Title;
        public string Subtitle => Entry.IsBook ? Entry.Author : null;

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

        public LibraryItemViewModel(OpdsEntry entry)
        {
            Entry = entry;
        }

        public event PropertyChangedEventHandler PropertyChanged;
    }
}
