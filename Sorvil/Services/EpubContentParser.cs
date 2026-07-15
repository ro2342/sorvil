using System;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Xml.Linq;
using Windows.Storage;
using Windows.Storage.Streams;
using Windows.UI.Text;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Documents;
using Windows.UI.Xaml.Media;
using Windows.UI.Xaml.Media.Imaging;

namespace Sorvil.Services
{
    // Como o texto do capítulo deve aparecer — aplicado direto em cada
    // Paragraph no momento em que ele é criado, não como propriedade de
    // um container só. Isso garante que a formatação fica igual não
    // importa qual RichTextBlock/RichTextBlockOverflow acaba mostrando
    // aquele parágrafo específico (a paginação nativa espalha os mesmos
    // objetos Paragraph por vários containers).
    public sealed class ReaderTextStyle
    {
        public double FontSize { get; set; }
        public FontFamily FontFamily { get; set; }
        public Brush Foreground { get; set; }
        public double LineHeight { get; set; }
        public TextAlignment TextAlignment { get; set; }
    }

    // Lê o XHTML de um capítulo já extraído (EpubExtractor cuida da
    // descompactação) e monta uma lista de Paragraph nativos — sem
    // WebView, sem CSS, sem depender de nenhum motor de renderização de
    // terceiros. Suporta as tags semânticas mais comuns (p, cabeçalhos,
    // ênfase/negrito, quebra de linha, imagem); qualquer coisa mais
    // exótica (tabelas, notas de rodapé) só desce recursivamente pro
    // texto interno, sem formatação especial — degrada de forma
    // razoável em vez de quebrar o capítulo inteiro.
    public static class EpubContentParser
    {
        private static readonly Regex WhitespaceRegex = new Regex(@"\s+", RegexOptions.Compiled);

        public static async Task<System.Collections.Generic.List<Paragraph>> ParseChapterAsync(
            StorageFolder bookFolder, string spineFilePath, ReaderTextStyle style)
        {
            System.Collections.Generic.List<Paragraph> blocks = new System.Collections.Generic.List<Paragraph>();
            try
            {
                StorageFile file = await EpubExtractor.GetFileByRelativePathAsync(bookFolder, spineFilePath);
                string xml = await FileIO.ReadTextAsync(file);
                XDocument doc = XDocument.Parse(xml);

                XElement body = null;
                foreach (XElement el in doc.Descendants())
                {
                    if (el.Name.LocalName == "body")
                    {
                        body = el;
                        break;
                    }
                }

                if (body != null)
                {
                    string chapterDir = GetDirectory(spineFilePath);
                    foreach (XNode node in body.Nodes())
                    {
                        await WalkBlockLevelAsync(node, blocks, bookFolder, chapterDir, style);
                    }
                }
            }
            catch (Exception)
            {
                // XHTML malformado ou algo inesperado — melhor mostrar o
                // capítulo em branco do que derrubar o leitor inteiro.
            }

            if (blocks.Count == 0)
            {
                blocks.Add(CreateParagraph(style));
            }
            return blocks;
        }

        private static async Task WalkBlockLevelAsync(
            XNode node, System.Collections.Generic.List<Paragraph> blocks,
            StorageFolder bookFolder, string chapterDir, ReaderTextStyle style)
        {
            XElement element = node as XElement;
            if (element == null)
            {
                XText textNode = node as XText;
                string text = textNode != null ? CleanText(textNode.Value) : null;
                if (!string.IsNullOrEmpty(text))
                {
                    Paragraph loose = CreateParagraph(style);
                    loose.Inlines.Add(new Run { Text = text });
                    blocks.Add(loose);
                }
                return;
            }

            string tag = element.Name.LocalName.ToLowerInvariant();
            switch (tag)
            {
                case "p":
                case "div":
                case "blockquote":
                case "li":
                {
                    Paragraph paragraph = CreateParagraph(style);
                    if (tag == "blockquote" || tag == "li")
                    {
                        paragraph.Margin = new Thickness(24, paragraph.Margin.Top, 0, paragraph.Margin.Bottom);
                    }
                    await WalkInlineAsync(element, paragraph.Inlines, bookFolder, chapterDir);
                    blocks.Add(paragraph);
                    break;
                }
                case "h1":
                case "h2":
                case "h3":
                case "h4":
                case "h5":
                case "h6":
                {
                    Paragraph heading = CreateParagraph(style);
                    heading.FontWeight = FontWeights.Bold;
                    heading.FontSize = style.FontSize * HeadingScale(tag);
                    heading.Margin = new Thickness(0, 16, 0, 12);
                    await WalkInlineAsync(element, heading.Inlines, bookFolder, chapterDir);
                    blocks.Add(heading);
                    break;
                }
                case "img":
                {
                    Paragraph imageParagraph = CreateParagraph(style);
                    await AddImageInlineAsync(element, imageParagraph.Inlines, bookFolder, chapterDir);
                    if (imageParagraph.Inlines.Count > 0)
                    {
                        blocks.Add(imageParagraph);
                    }
                    break;
                }
                default:
                    // Tag estrutural desconhecida (section, article, etc.)
                    // — desce pros filhos, preservando o texto de dentro.
                    foreach (XNode child in element.Nodes())
                    {
                        await WalkBlockLevelAsync(child, blocks, bookFolder, chapterDir, style);
                    }
                    break;
            }
        }

        private static async Task WalkInlineAsync(
            XElement parent, InlineCollection inlines, StorageFolder bookFolder, string chapterDir)
        {
            foreach (XNode node in parent.Nodes())
            {
                XText textNode = node as XText;
                if (textNode != null)
                {
                    string text = CleanText(textNode.Value);
                    if (text.Length > 0)
                    {
                        inlines.Add(new Run { Text = text });
                    }
                    continue;
                }

                XElement element = node as XElement;
                if (element == null)
                {
                    continue;
                }

                string tag = element.Name.LocalName.ToLowerInvariant();
                switch (tag)
                {
                    case "br":
                        inlines.Add(new LineBreak());
                        break;
                    case "em":
                    case "i":
                    {
                        Span span = new Span { FontStyle = FontStyle.Italic };
                        await WalkInlineAsync(element, span.Inlines, bookFolder, chapterDir);
                        inlines.Add(span);
                        break;
                    }
                    case "strong":
                    case "b":
                    {
                        Span span = new Span { FontWeight = FontWeights.Bold };
                        await WalkInlineAsync(element, span.Inlines, bookFolder, chapterDir);
                        inlines.Add(span);
                        break;
                    }
                    default:
                        // span/a/outras tags inline — não tenta encaixar
                        // <img> aqui dentro (InlineUIContainer só é válido
                        // direto no Inlines de um Paragraph, não dentro de
                        // um Span aninhado), só o texto.
                        await WalkInlineAsync(element, inlines, bookFolder, chapterDir);
                        break;
                }
            }
        }

        private static async Task AddImageInlineAsync(
            XElement imgElement, InlineCollection inlines, StorageFolder bookFolder, string chapterDir)
        {
            string src = (string)imgElement.Attribute("src");
            if (string.IsNullOrEmpty(src))
            {
                return;
            }

            string resolvedPath = ResolveRelativePath(chapterDir, src);
            if (resolvedPath == null)
            {
                return;
            }

            try
            {
                StorageFile imageFile = await EpubExtractor.GetFileByRelativePathAsync(bookFolder, resolvedPath);
                BitmapImage bitmap = new BitmapImage();
                using (IRandomAccessStream stream = await imageFile.OpenAsync(FileAccessMode.Read))
                {
                    await bitmap.SetSourceAsync(stream);
                }
                Image image = new Image
                {
                    Source = bitmap,
                    Stretch = Stretch.Uniform,
                    MaxWidth = 280,
                };
                inlines.Add(new InlineUIContainer { Child = image });
            }
            catch (Exception)
            {
                // Imagem ausente/corrompida — só pula, não quebra o capítulo.
            }
        }

        private static Paragraph CreateParagraph(ReaderTextStyle style)
        {
            return new Paragraph
            {
                FontSize = style.FontSize,
                FontFamily = style.FontFamily,
                Foreground = style.Foreground,
                LineHeight = style.LineHeight,
                LineStackingStrategy = LineStackingStrategy.BlockLineHeight,
                TextAlignment = style.TextAlignment,
                Margin = new Thickness(0, 0, 0, 12),
            };
        }

        private static double HeadingScale(string tag)
        {
            switch (tag)
            {
                case "h1": return 1.6;
                case "h2": return 1.4;
                case "h3": return 1.25;
                case "h4": return 1.15;
                default: return 1.1;
            }
        }

        private static string CleanText(string raw)
        {
            return WhitespaceRegex.Replace(raw ?? string.Empty, " ");
        }

        private static string GetDirectory(string path)
        {
            int lastSlash = path.LastIndexOf('/');
            return lastSlash >= 0 ? path.Substring(0, lastSlash + 1) : string.Empty;
        }

        // Resolve um src relativo (pode ter "../") contra o diretório do
        // capítulo atual — usa Uri só como ferramenta de resolução de
        // caminho, o resultado nunca é usado como URI de verdade.
        private static string ResolveRelativePath(string chapterDir, string relativeSrc)
        {
            if (relativeSrc.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                relativeSrc.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ||
                relativeSrc.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            try
            {
                Uri baseUri = new Uri("epubbase:///" + chapterDir.TrimStart('/'));
                Uri resolved = new Uri(baseUri, relativeSrc);
                return Uri.UnescapeDataString(resolved.AbsolutePath).TrimStart('/');
            }
            catch (Exception)
            {
                return null;
            }
        }
    }
}
