using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Xml.Linq;
using Windows.Storage;
using Windows.Storage.Streams;
using Windows.UI.Text;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
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
    // WebView, mas com um subconjunto pequeno do CSS do próprio livro
    // (style= inline, <style> embutido e stylesheets linkados) resolvido
    // por cima do estilo base do leitor: text-align, negrito/itálico por
    // classe e margem de parágrafo. Não é um motor de CSS de verdade
    // (sem seletores compostos, sem cascata por especificidade real) —
    // só o suficiente pra recuperar a intenção mais comum de formatação
    // de um EPUB (títulos/poemas centralizados, respiro entre
    // parágrafos) sem arriscar quebrar o capítulo se o CSS for estranho.
    public static class EpubContentParser
    {
        private static readonly Regex WhitespaceRegex = new Regex(@"\s+", RegexOptions.Compiled);

        // Base pra converter em/rem em px quando resolvendo margem —
        // independente do tamanho de fonte escolhido pelo leitor (que
        // multiplica esse resultado depois, em CreateParagraph), só a
        // referência usada pra interpretar o número que estava no CSS.
        private const double CssEmReferencePx = 16.0;

        private sealed class CssDeclarations
        {
            public TextAlignment? TextAlign;
            public bool? Italic;
            public bool? Bold;
            public double? MarginTopEm;
            public double? MarginBottomEm;

            public bool IsEmpty
            {
                get
                {
                    return TextAlign == null && Italic == null && Bold == null &&
                        MarginTopEm == null && MarginBottomEm == null;
                }
            }

            public CssDeclarations Clone()
            {
                return new CssDeclarations
                {
                    TextAlign = TextAlign,
                    Italic = Italic,
                    Bold = Bold,
                    MarginTopEm = MarginTopEm,
                    MarginBottomEm = MarginBottomEm,
                };
            }
        }

        public static async Task<List<Paragraph>> ParseChapterAsync(
            StorageFolder bookFolder, string spineFilePath, ReaderTextStyle style)
        {
            List<Paragraph> blocks = new List<Paragraph>();
            try
            {
                StorageFile file = await EpubExtractor.GetFileByRelativePathAsync(bookFolder, spineFilePath);
                string xml = await FileIO.ReadTextAsync(file);
                XDocument doc = XDocument.Parse(xml);
                string chapterDir = GetDirectory(spineFilePath);

                Dictionary<string, CssDeclarations> cssRules = new Dictionary<string, CssDeclarations>();
                await LoadCssRulesAsync(doc, bookFolder, chapterDir, cssRules);

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
                    foreach (XNode node in body.Nodes())
                    {
                        await WalkBlockLevelAsync(node, blocks, bookFolder, chapterDir, style, cssRules);
                    }
                }
            }
            catch (Exception ex)
            {
                // XHTML malformado ou algo inesperado — melhor avisar o
                // usuário (a página ficava em branco, sem pista nenhuma do
                // que deu errado) do que derrubar o leitor inteiro.
                blocks.Clear();
                Paragraph errorParagraph = CreateParagraph(style, null);
                errorParagraph.Inlines.Add(new Run { Text = "Não consegui carregar este capítulo (" + ex.Message + ")." });
                blocks.Add(errorParagraph);
            }

            if (blocks.Count == 0)
            {
                Paragraph emptyParagraph = CreateParagraph(style, null);
                emptyParagraph.Inlines.Add(new Run { Text = "Este capítulo está vazio." });
                blocks.Add(emptyParagraph);
            }
            return blocks;
        }

        // Junta as regras de todo <style> embutido no XHTML do capítulo
        // com as de qualquer stylesheet linkado (<link rel="stylesheet">)
        // — stylesheet ausente/corrompido só é ignorado, nunca derruba o
        // capítulo inteiro.
        private static async Task LoadCssRulesAsync(
            XDocument doc, StorageFolder bookFolder, string chapterDir, Dictionary<string, CssDeclarations> rules)
        {
            foreach (XElement styleElement in doc.Descendants())
            {
                if (styleElement.Name.LocalName.Equals("style", StringComparison.OrdinalIgnoreCase))
                {
                    MiniCssParser.ParseInto(styleElement.Value, rules);
                }
            }

            List<string> stylesheetHrefs = new List<string>();
            foreach (XElement linkElement in doc.Descendants())
            {
                if (!linkElement.Name.LocalName.Equals("link", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                string rel = (string)linkElement.Attribute("rel");
                string href = (string)linkElement.Attribute("href");
                if (rel == null || href == null || rel.IndexOf("stylesheet", StringComparison.OrdinalIgnoreCase) < 0)
                {
                    continue;
                }
                stylesheetHrefs.Add(href);
            }

            foreach (string href in stylesheetHrefs)
            {
                string resolvedPath = ResolveRelativePath(chapterDir, href);
                if (resolvedPath == null)
                {
                    continue;
                }
                try
                {
                    StorageFile cssFile = await EpubExtractor.GetFileByRelativePathAsync(bookFolder, resolvedPath);
                    string cssText = await FileIO.ReadTextAsync(cssFile);
                    MiniCssParser.ParseInto(cssText, rules);
                }
                catch (Exception)
                {
                    // Stylesheet referenciado mas ausente/corrompido — segue
                    // sem essas regras específicas.
                }
            }
        }

        private static async Task WalkBlockLevelAsync(
            XNode node, List<Paragraph> blocks, StorageFolder bookFolder, string chapterDir,
            ReaderTextStyle style, Dictionary<string, CssDeclarations> cssRules)
        {
            XElement element = node as XElement;
            if (element == null)
            {
                XText textNode = node as XText;
                string text = textNode != null ? CleanText(textNode.Value) : null;
                if (!string.IsNullOrEmpty(text))
                {
                    Paragraph loose = CreateParagraph(style, null);
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
                    CssDeclarations decl = ResolveDeclarations(cssRules, tag, element);
                    Paragraph paragraph = CreateParagraph(style, decl);
                    if (tag == "blockquote" || tag == "li")
                    {
                        paragraph.Margin = new Thickness(24, paragraph.Margin.Top, 0, paragraph.Margin.Bottom);
                    }
                    await WalkInlineAsync(element, paragraph.Inlines, bookFolder, chapterDir, cssRules);
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
                    CssDeclarations decl = ResolveDeclarations(cssRules, tag, element);
                    Paragraph heading = CreateParagraph(style, decl);
                    heading.FontWeight = FontWeights.Bold;
                    heading.FontSize = style.FontSize * HeadingScale(tag);
                    heading.Margin = new Thickness(0, 16, 0, 12);
                    await WalkInlineAsync(element, heading.Inlines, bookFolder, chapterDir, cssRules);
                    blocks.Add(heading);
                    break;
                }
                case "img":
                {
                    CssDeclarations decl = ResolveDeclarations(cssRules, tag, element);
                    Paragraph imageParagraph = CreateParagraph(style, decl);
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
                        await WalkBlockLevelAsync(child, blocks, bookFolder, chapterDir, style, cssRules);
                    }
                    break;
            }
        }

        private static async Task WalkInlineAsync(
            XElement parent, InlineCollection inlines, StorageFolder bookFolder, string chapterDir,
            Dictionary<string, CssDeclarations> cssRules)
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
                    case "img":
                        // <img> dentro de um <p>/<div> (muito comum pra
                        // ilustrações de página cheia) — sem isso, a
                        // imagem inteira era descartada em silêncio.
                        await AddImageInlineAsync(element, inlines, bookFolder, chapterDir);
                        break;
                    case "em":
                    case "i":
                    {
                        Span span = new Span { FontStyle = FontStyle.Italic };
                        await WalkInlineAsync(element, span.Inlines, bookFolder, chapterDir, cssRules);
                        inlines.Add(span);
                        break;
                    }
                    case "strong":
                    case "b":
                    {
                        Span span = new Span { FontWeight = FontWeights.Bold };
                        await WalkInlineAsync(element, span.Inlines, bookFolder, chapterDir, cssRules);
                        inlines.Add(span);
                        break;
                    }
                    default:
                    {
                        // span/a/outras tags inline — sem <em>/<strong>,
                        // livros costumam marcar ênfase por classe
                        // (ex.: <span class="italico">); só embrulha num
                        // Span de verdade se a classe resolver pra
                        // itálico/negrito, senão seria overhead à toa.
                        CssDeclarations decl = ResolveDeclarations(cssRules, tag, element);
                        if (decl.Italic == true || decl.Bold == true)
                        {
                            Span span = new Span();
                            if (decl.Italic == true)
                            {
                                span.FontStyle = FontStyle.Italic;
                            }
                            if (decl.Bold == true)
                            {
                                span.FontWeight = FontWeights.Bold;
                            }
                            await WalkInlineAsync(element, span.Inlines, bookFolder, chapterDir, cssRules);
                            inlines.Add(span);
                        }
                        else
                        {
                            // Não tenta encaixar <img> aqui dentro
                            // (InlineUIContainer só é válido direto no
                            // Inlines de um Paragraph, não dentro de um
                            // Span aninhado) — só o texto desce.
                            await WalkInlineAsync(element, inlines, bookFolder, chapterDir, cssRules);
                        }
                        break;
                    }
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

        // Junta, por especificidade crescente (tag < classe < tag.classe <
        // id < style= inline), as declarações que a gente entende — o que
        // vier depois sobrescreve só as propriedades que de fato define,
        // então uma regra mais específica sem text-align não apaga o
        // text-align herdado de uma menos específica.
        private static CssDeclarations ResolveDeclarations(
            Dictionary<string, CssDeclarations> cssRules, string tag, XElement element)
        {
            CssDeclarations resolved = new CssDeclarations();

            CssDeclarations tagRule;
            if (cssRules.TryGetValue(tag, out tagRule))
            {
                MergeInto(resolved, tagRule);
            }

            string classAttr = (string)element.Attribute("class");
            if (!string.IsNullOrEmpty(classAttr))
            {
                foreach (string rawClassName in classAttr.Split(' '))
                {
                    string className = rawClassName.Trim().ToLowerInvariant();
                    if (className.Length == 0)
                    {
                        continue;
                    }

                    CssDeclarations classRule;
                    if (cssRules.TryGetValue("." + className, out classRule))
                    {
                        MergeInto(resolved, classRule);
                    }

                    CssDeclarations tagClassRule;
                    if (cssRules.TryGetValue(tag + "." + className, out tagClassRule))
                    {
                        MergeInto(resolved, tagClassRule);
                    }
                }
            }

            string idAttr = (string)element.Attribute("id");
            if (!string.IsNullOrEmpty(idAttr))
            {
                CssDeclarations idRule;
                if (cssRules.TryGetValue("#" + idAttr.Trim().ToLowerInvariant(), out idRule))
                {
                    MergeInto(resolved, idRule);
                }
            }

            string inlineStyle = (string)element.Attribute("style");
            if (!string.IsNullOrEmpty(inlineStyle))
            {
                MergeInto(resolved, MiniCssParser.ParseDeclarationsPublic(inlineStyle));
            }

            return resolved;
        }

        private static void MergeInto(CssDeclarations target, CssDeclarations source)
        {
            if (source.TextAlign.HasValue)
            {
                target.TextAlign = source.TextAlign;
            }
            if (source.Italic.HasValue)
            {
                target.Italic = source.Italic;
            }
            if (source.Bold.HasValue)
            {
                target.Bold = source.Bold;
            }
            if (source.MarginTopEm.HasValue)
            {
                target.MarginTopEm = source.MarginTopEm;
            }
            if (source.MarginBottomEm.HasValue)
            {
                target.MarginBottomEm = source.MarginBottomEm;
            }
        }

        // Parser de CSS bem pequeno: acha blocos "seletor { declarações }"
        // com uma regex que ignora nível de aninhamento — na prática, isso
        // já pula sozinho o "wrapper" de um @media (a regex só bate com o
        // conteúdo mais interno, sem chave aninhada), então @media nem
        // precisa ser tratado à parte. Seletores compostos (combinador,
        // pseudo-classe) são ignorados com segurança: não tem espaço nem
        // ":" nas chaves que ResolveDeclarations consulta, então nunca
        // batem por acidente.
        private static class MiniCssParser
        {
            private static readonly Regex RuleRegex = new Regex(@"([^{}]+)\{([^{}]*)\}", RegexOptions.Compiled);
            private static readonly Regex LengthRegex = new Regex(@"^(-?[0-9.]+)\s*(em|px|pt|rem)?$", RegexOptions.Compiled);

            public static void ParseInto(string css, Dictionary<string, CssDeclarations> rules)
            {
                if (string.IsNullOrEmpty(css))
                {
                    return;
                }

                foreach (Match match in RuleRegex.Matches(css))
                {
                    CssDeclarations decl = ParseDeclarationsPublic(match.Groups[2].Value);
                    if (decl.IsEmpty)
                    {
                        continue;
                    }

                    foreach (string rawSelector in match.Groups[1].Value.Split(','))
                    {
                        string selector = NormalizeSelector(rawSelector);
                        if (selector.Length == 0)
                        {
                            continue;
                        }

                        CssDeclarations existing;
                        if (rules.TryGetValue(selector, out existing))
                        {
                            MergeInto(existing, decl);
                        }
                        else
                        {
                            rules[selector] = decl.Clone();
                        }
                    }
                }
            }

            private static string NormalizeSelector(string raw)
            {
                string trimmed = (raw ?? string.Empty).Trim();
                // Só aceita seletor simples (tag, .classe, tag.classe,
                // #id) — qualquer coisa com combinador (espaço, ">", "+",
                // "~") ou pseudo-classe (":") não bate com nenhuma chave
                // que ResolveDeclarations monta, então é ignorado aqui
                // mesmo em vez de guardado à toa.
                if (trimmed.IndexOfAny(new[] { ' ', '\t', '\n', '\r', '>', '+', '~', ':' }) >= 0)
                {
                    return string.Empty;
                }
                return trimmed.ToLowerInvariant();
            }

            public static CssDeclarations ParseDeclarationsPublic(string block)
            {
                CssDeclarations decl = new CssDeclarations();
                foreach (string rawDecl in (block ?? string.Empty).Split(';'))
                {
                    int colonIndex = rawDecl.IndexOf(':');
                    if (colonIndex < 0)
                    {
                        continue;
                    }
                    string prop = rawDecl.Substring(0, colonIndex).Trim().ToLowerInvariant();
                    string value = rawDecl.Substring(colonIndex + 1).Trim().ToLowerInvariant();
                    ApplyProperty(decl, prop, value);
                }
                return decl;
            }

            private static void ApplyProperty(CssDeclarations decl, string prop, string value)
            {
                switch (prop)
                {
                    case "text-align":
                        if (value.IndexOf("center", StringComparison.Ordinal) >= 0)
                        {
                            decl.TextAlign = TextAlignment.Center;
                        }
                        else if (value.IndexOf("right", StringComparison.Ordinal) >= 0)
                        {
                            decl.TextAlign = TextAlignment.Right;
                        }
                        else if (value.IndexOf("justify", StringComparison.Ordinal) >= 0)
                        {
                            decl.TextAlign = TextAlignment.Justify;
                        }
                        else if (value.IndexOf("left", StringComparison.Ordinal) >= 0)
                        {
                            decl.TextAlign = TextAlignment.Left;
                        }
                        break;
                    case "font-style":
                        if (value.IndexOf("italic", StringComparison.Ordinal) >= 0 ||
                            value.IndexOf("oblique", StringComparison.Ordinal) >= 0)
                        {
                            decl.Italic = true;
                        }
                        else if (value.IndexOf("normal", StringComparison.Ordinal) >= 0)
                        {
                            decl.Italic = false;
                        }
                        break;
                    case "font-weight":
                        if (value.IndexOf("bold", StringComparison.Ordinal) >= 0 ||
                            value == "700" || value == "800" || value == "900")
                        {
                            decl.Bold = true;
                        }
                        else if (value.IndexOf("normal", StringComparison.Ordinal) >= 0 || value == "400")
                        {
                            decl.Bold = false;
                        }
                        break;
                    case "margin-top":
                        decl.MarginTopEm = ParseLengthEm(value);
                        break;
                    case "margin-bottom":
                        decl.MarginBottomEm = ParseLengthEm(value);
                        break;
                    case "margin":
                    {
                        string[] parts = value.Split(' ');
                        double? shorthand = parts.Length > 0 ? ParseLengthEm(parts[0]) : null;
                        if (shorthand.HasValue)
                        {
                            decl.MarginTopEm = shorthand;
                            decl.MarginBottomEm = shorthand;
                        }
                        break;
                    }
                }
            }

            private static double? ParseLengthEm(string value)
            {
                Match match = LengthRegex.Match(value ?? string.Empty);
                if (!match.Success)
                {
                    return null;
                }

                double number;
                if (!double.TryParse(match.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out number))
                {
                    return null;
                }

                string unit = match.Groups[2].Success ? match.Groups[2].Value : "px";
                switch (unit)
                {
                    case "em":
                    case "rem":
                        return number;
                    case "pt":
                        return number * (96.0 / 72.0) / CssEmReferencePx;
                    default:
                        return number / CssEmReferencePx;
                }
            }
        }

        private static Paragraph CreateParagraph(ReaderTextStyle style, CssDeclarations overrides)
        {
            Paragraph paragraph = new Paragraph
            {
                FontSize = style.FontSize,
                FontFamily = style.FontFamily,
                Foreground = style.Foreground,
                LineHeight = style.LineHeight,
                LineStackingStrategy = LineStackingStrategy.BlockLineHeight,
                TextAlignment = style.TextAlignment,
                Margin = new Thickness(0, 0, 0, 12),
            };

            if (overrides != null)
            {
                if (overrides.TextAlign.HasValue)
                {
                    paragraph.TextAlignment = overrides.TextAlign.Value;
                }
                if (overrides.Italic == true)
                {
                    paragraph.FontStyle = FontStyle.Italic;
                }
                if (overrides.Bold == true)
                {
                    paragraph.FontWeight = FontWeights.Bold;
                }
                if (overrides.MarginTopEm.HasValue || overrides.MarginBottomEm.HasValue)
                {
                    double top = overrides.MarginTopEm.HasValue ? overrides.MarginTopEm.Value * style.FontSize : paragraph.Margin.Top;
                    double bottom = overrides.MarginBottomEm.HasValue ? overrides.MarginBottomEm.Value * style.FontSize : paragraph.Margin.Bottom;
                    paragraph.Margin = new Thickness(paragraph.Margin.Left, top, paragraph.Margin.Right, bottom);
                }
            }

            return paragraph;
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
