namespace Sorvil.Models
{
    // Estado local de um livro baixado — Id é composto (entrada OPDS +
    // formato, ex. "livro123:epub") porque o mesmo livro pode ter mais de
    // um formato baixado ao mesmo tempo (epub e pdf, por exemplo).
    public sealed class BookRecord
    {
        public string Id { get; set; }
        public string Title { get; set; }
        public string Author { get; set; }
        public string Format { get; set; }
        public string LocalFilePath { get; set; }
        public string CoverCacheKey { get; set; }
        public string LastOpenedAt { get; set; }
        public string ReadingPositionJson { get; set; }
    }
}
