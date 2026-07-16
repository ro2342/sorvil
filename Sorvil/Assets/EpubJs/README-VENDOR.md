# Bibliotecas vendorizadas

Não editar `epub.legacy.min.js` nem `jszip.min.js` diretamente — são
builds de terceiros, baixados de:

- `epub.legacy.min.js` — [epubjs](https://github.com/futurepress/epub.js) v0.3.93,
  `dist/epub.legacy.min.js` (build "legacy", transpilado pra engines mais
  antigas — usar essa em vez de `epub.min.js`/`epub.js`, que assumem JS
  mais moderno). Espera `JSZip` como global (`window.JSZip`), não vem
  embutido.
- `jszip.min.js` — [jszip](https://github.com/Stuk/jszip) v3.10.1,
  `dist/jszip.min.js` — dependência do epub.js pra descompactar o EPUB
  (que é um .zip por baixo).

Pra atualizar: baixar a versão nova de `dist/` no pacote npm
correspondente (via `https://unpkg.com/<pacote>@<versão>/dist/<arquivo>`)
e substituir o arquivo aqui. `reader-bridge.js` (nosso, não de
terceiros) espera a API pública do epub.js 0.3.x (`ePub()`,
`book.renderTo()`, `rendition.themes`, eventos `relocated`/`rendered`)
— conferir o changelog deles antes de pular pra uma major diferente.

Esses três arquivos não são navegados como página — são lidos como
texto puro (`Package.Current.InstalledLocation` + `FileIO.ReadTextAsync`)
e embutidos inline num HTML montado em C#
(`ReaderEpubPage.xaml.cs`, `BuildReaderHtmlAsync`), carregado via
`WebView.NavigateToString`. `WebView.Navigate(new Uri("ms-appx:///..."))`
lançava "Operation aborted (E_ABORT)" de forma consistente num Lumia
real; `NavigateToString` com tudo inline evita isso de vez.
