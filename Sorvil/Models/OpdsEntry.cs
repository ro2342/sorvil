using System;
using System.Collections.Generic;

namespace Sorvil.Models
{
    public sealed class OpdsAcquisitionLink
    {
        public string MimeType { get; set; }
        public Uri Href { get; set; }
    }

    // Uma entrada de feed OPDS pode ser um livro de verdade (tem link de
    // aquisição) ou uma pasta de navegação (só tem um link "ir pra cá",
    // sem aquisição/capa) — IsBook decide qual das duas é, sem precisar
    // de um tipo separado por feed.
    public sealed class OpdsEntry
    {
        public string Id { get; set; }
        public string Title { get; set; }
        public string Author { get; set; }
        public string Summary { get; set; }
        public Uri ImageUri { get; set; }
        public Uri ThumbnailUri { get; set; }
        public Uri NavigationUri { get; set; }
        public List<OpdsAcquisitionLink> Acquisitions { get; set; } = new List<OpdsAcquisitionLink>();

        public bool IsBook => Acquisitions.Count > 0;
    }

    public sealed class OpdsFeed
    {
        public string Title { get; set; }
        public List<OpdsEntry> Entries { get; set; } = new List<OpdsEntry>();
        public Uri NextUri { get; set; }
        public Uri SearchUri { get; set; }

        public bool IsBookFeed
        {
            get
            {
                foreach (OpdsEntry entry in Entries)
                {
                    if (entry.IsBook)
                    {
                        return true;
                    }
                }
                return false;
            }
        }
    }
}
