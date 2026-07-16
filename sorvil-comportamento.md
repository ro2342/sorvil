# Sorvil — Comportamento do Mockup

Descrição de como cada tela e cada interação funciona no protótipo HTML.

## 1. Tela Inicial (Home)

- Topbar **fixa** (não desliza, não some): fundo cinza claro, com um **botão quadrado verde forte** colado na borda esquerda contendo o ícone de hambúrguer (☰), e o título "Sorvil" em texto escuro ao lado.
- Três seções com rolagem horizontal de capas: **Você está lendo**, **Adicionado Recentemente**, **Você pode gostar de...**.
- Tocar em qualquer capa abre o livro diretamente na **tela de leitura**, em modo imersivo (ver seção 3).
- Tocar no botão quadrado verde abre a **gaveta lateral** (menu Início / Biblioteca / Baixados / Ajustes): ela ocupa **40% da largura e 100% da altura** da tela (encostada na borda esquerda, por baixo da topbar, que continua visível e fixa). "Início" aparece destacado num **verde mais escuro** que o do botão do hambúrguer, indicando que é a tela atual.

## 2. Botão físico "Voltar" (hardware, canto inferior esquerdo)

Sempre desfaz um passo por vez, na seguinte ordem de prioridade:
1. Se a gaveta do menu Home estiver aberta → fecha a gaveta.
2. Se estiver no livro com um **painel** (fonte/brilho/gestos) ou o **índice** aberto → volta para a barra de ferramentas simples (escura).
3. Se estiver no livro só com a barra de ferramentas visível → esconde tudo, volta ao modo imersivo.
4. Se estiver no livro em modo imersivo → volta para a Home.

## 3. Tela de Leitura — modo imersivo (padrão)

Ao abrir um livro, **nenhuma barra aparece** — nem a verde, nem a escura. É a tela do texto sozinha: ilustração, "Capítulo Um", título do capítulo e o corpo do texto (com os destaques em amarelo e verde exatamente como no material enviado).

Tocar em qualquer ponto do texto (fora dos botões) **revela as duas barras escuras** (de cima e de baixo) ao mesmo tempo. Tocar de novo no texto **esconde as duas**, voltando ao modo imersivo.

## 4. Barra escura de cima (toolbar)

Aparece deslizando de cima para baixo. Contém, da esquerda para a direita:
- 🏠 **"Voltar ao Início"** — leva direto para a tela Home (fecha tudo e reinicia o estado do livro).
- **AA** — abre o painel de fonte.
- ☀️ (sol) — abre o painel de brilho/tema.
- ⚙️ (engrenagem) — abre o painel de gestos.

Quando um desses três ícones tem seu painel aberto, o próprio ícone fica **verde** para indicar qual está ativo. Tocar de novo no mesmo ícone fecha o painel e volta para a barra simples.

**Importante:** esses três painéis (fonte, brilho/tema, gestos) aparecem *anexados logo abaixo* dessa mesma barra escura, ainda dentro do bloco escuro — não abrem por cima do texto como uma janela separada, e o texto do capítulo continua visível, normal, embaixo do painel.

### 4.1 Painel de Fonte (ícone "AA")
- **Font Face**: seletor mostrando "EB Garamond" (decorativo) + link "Advanced".
- **Font Size**: slider arrastável.
- **Line Spacing**: slider arrastável.
- **Margins**: slider arrastável.
- **Justification**: três botões (Off / alinhado à esquerda / justificado) — funcional, muda o alinhamento do texto do capítulo de verdade.

### 4.2 Painel de Brilho/Tema (ícone ☀️)
- **Brilho da Tela**: slider (decorativo, simulando ajuste de brilho).
- **Tema do livro**: três botões — Claro / Escuro / Sépia. Funcional: troca o fundo e a cor do texto da página de leitura de verdade.

### 4.3 Painel de Gestos (ícone ⚙️)
- Três alternadores (switches) funcionais, ligam/desligam ao tocar:
  - Zoom do texto com movimento de pinça
  - Toque nos cantos para mudar página
  - Mudar página com movimento de slide

## 5. Barra escura de baixo (toolbar bottom)

Só aparece junto com a barra de cima **quando nenhum painel/índice está aberto** (ou seja, some quando você abre fonte/brilho/gestos/índice, e volta quando você fecha qualquer um deles). Contém, de cima para baixo:

1. **Legenda do livro**: "As Crônicas de Nárnia - O Sobrinho do Mago (C. S. Lewis)", centralizada.
2. **Barra de progresso (scrubber)**: uma trilha arrastável que representa a posição atual dentro do livro inteiro — como a barra de progresso de uma música. Uma bolinha (thumb) mostra onde você está e pode ser arrastada para pular para outro ponto. Nas pontas, dois ícones (retroceder / avançar) dão pequenos saltos na posição.
3. **Linha de ícones**: 
   - ☰ à esquerda → abre o **Índice** (ver seção 6).
   - Texto central → nome do capítulo atual ("Capítulo Um - A Porta Errada").
   - 🔍 à direita → abre um campo de busca no lugar do texto central (pesquisa dentro do livro).

## 6. Índice de capítulos (o "botão de índices")

Só é acessado pelo ícone ☰ da barra de baixo (**não** existe mais um cabeçalho verde permanente no topo do livro).

Ao tocar nesse ícone:
- A barra escura de cima **é substituída** por um **cabeçalho no mesmo estilo da Home** (fundo cinza claro + botão quadrado verde forte com o hambúrguer, agora funcionando como "fechar índice") + título do livro em texto escuro.
- Logo abaixo desse cabeçalho aparece a **lista de capítulos**, ocupando **100% da altura restante da tela e encostada na borda esquerda** (largura de ~62%), deixando o restante do conteúdo (ilustração/texto) visível ao lado, sem escurecer o fundo.
- O capítulo atual aparece destacado num **verde mais escuro** (mesma cor usada no "Início" da gaveta da Home), diferente do verde mais forte do botão do hambúrguer.
- Tocar em um capítulo troca o conteúdo da tela (título, texto, legenda da barra de baixo) e fecha tudo, voltando ao modo imersivo.
- Tocar no botão quadrado verde do cabeçalho fecha o índice e volta para a barra escura simples (sem sair do livro).

## 7. Resumo do "mapa de estados" da tela de leitura

```
nenhum estado (imersivo)
   │  toca no texto
   ▼
barra escura (topo + rodapé)
   │            │            │              │
  AA           sol          engrenagem      ☰ (rodapé)
   ▼            ▼            ▼              ▼
painel fonte  painel tema  painel gestos   índice (cabeçalho verde + flyout)
   │            │            │              │
   └────────────┴────────────┴──────────────┘
              toca de novo / fecha → volta pra "barra escura"
                    │
              toca no texto → volta pro "imersivo"
```

## 8. Paleta de cores das topbars/gavetas

- **Fundo da topbar** (Home e cabeçalho do índice): cinza claro.
- **Botão do hambúrguer**: quadrado, verde forte, colado na borda esquerda da topbar, ícone branco.
- **Item ativo nas gavetas** ("Início" na Home / capítulo atual no índice): verde mais escuro que o botão do hambúrguer, para diferenciar "onde está o controle" de "onde você está".
- Título do app/livro na topbar: texto escuro, não branco (já que o fundo não é mais verde).

## 9. O que é só decorativo (não funcional de verdade)

- Trocar de "livro" na Home sempre abre o mesmo conteúdo de exemplo (Nárnia).
- Capítulos 2 e 3 têm texto de espaço reservado ("Lalala"/"Lalalala") em vez do conteúdo real.
- A barra de progresso (scrubber) não altera de fato a posição de leitura, é só visual/arrastável.
- O campo de busca não retorna resultados reais.
- Font Face, brilho da tela e as sliders de tamanho/espaçamento/margem são visuais, exceto a justificação de texto, que realmente muda o alinhamento.
