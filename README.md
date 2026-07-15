# Sorvil

App UWP nativo em C#/XAML pra ler ebooks a partir de um servidor
**Calibre-Web** ou **Calibre-Web-Automated** — catálogo com capas, leitura
de EPUB/KEPUB/PDF direto no aparelho (offline depois de baixado), no mesmo
estilo visual "News nativo da Microsoft" (SplitView com hambúrguer,
cabeçalho fixo) já usado no app irmão
[the artistsway](https://github.com/ro2342/theartistsway).

Alvo: Windows 10 Mobile (Lumia), sideload via certificado autoassinado (sem
Store), distribuído por uma página de download simples no GitHub Pages.

## Por que UWP nativo (e não WebView pro app inteiro)

A casca do app (navegação, biblioteca, capas, ajustes) é 100% XAML nativo.
Só o *conteúdo* do livro em si — que é HTML/CSS por natureza do formato
EPUB — roda dentro de um `WebView` nativo do UWP na tela de leitura. Não
existe motor de reflow de EPUB em XAML puro disponível pra UWP; essa é a
mesma solução usada por praticamente todo leitor de EPUB sério (inclusive o
Freda). PDF já renderiza 100% nativo via `Windows.Data.Pdf`, sem WebView.

## Como o app fala com o Calibre-Web

Via **OPDS** (`/opds` no servidor) — o catálogo padrão que tanto o
Calibre-Web quanto o Calibre-Web-Automated expõem de forma idêntica (CWA é
um fork que mantém 100% de compatibilidade com essa API). O app lê o feed
Atom/OPDS (capas via link `.../image` e `.../image/thumbnail`, download por
formato via link de aquisição, paginação via `rel="next"`, busca via
OpenSearch) com autenticação HTTP Basic guardada no
`Windows.Security.Credentials.PasswordVault` do aparelho — nunca em texto
puro.

## Um detalhe importante de rede

Servidor Calibre-Web caseiro costuma estar num IP de rede local
(`192.168.x.x`), não na "internet" propriamente dita. Por isso o
`Package.appxmanifest` declara tanto `internetClient` quanto
`privateNetworkClientServer` — só o primeiro não bastaria pro UWP deixar o
app alcançar um servidor na LAN.

## Estrutura

```
Sorvil/
├── Sorvil.csproj              ← projeto C# (old-style, sem wildcard de Compile/Page)
├── Package.appxmanifest        ← manifesto do app
├── App.xaml / App.xaml.cs      ← inicialização
├── MainPage.xaml(.cs)          ← shell nativo: SplitView + hambúrguer + cabeçalho fixo
├── Models/                     ← ServerProfile, OpdsEntry, BookRecord (em progresso)
├── Services/                   ← ThemeHelper, CredentialService, OpdsClient, etc. (em progresso)
├── Views/                      ← uma página XAML por tela do app
├── Assets/                     ← ícones/tiles gerados por generate_tile_assets.py
└── generate_tile_assets.py     ← gera os PNGs a partir de logo.svg
site/                           ← página de download publicada no GitHub Pages
.github/workflows/              ← geração de certificado + build automático do appxbundle
```

## Build e sideload

Mesmo fluxo do the artistsway:

1. **Uma vez só**: aba Actions → workflow **"01 - Gerar certificado de
   assinatura"** → Run workflow. Salva a chave privada no repositório.
2. **A cada push** que mexer em `Sorvil/`: o workflow **"02 - Build do
   appxbundle"** roda sozinho, builda e publica `app.appxbundle` +
   `version.json` na raiz do GitHub Pages do repositório.
3. No aparelho: ative o **Modo desenvolvedor** (Configurações → Atualização
   e segurança → Para desenvolvedores), baixe o `.appxbundle` pela página de
   download e abra pelo Explorador de Arquivos pra instalar.

**Aviso importante**: não existe toolchain UWP fora do Windows, então nada
disso builda localmente aqui (ambiente Linux) — a validação real acontece
no primeiro build do GitHub Actions após cada push. Qualquer erro do log do
Actions é o próximo passo a resolver.
