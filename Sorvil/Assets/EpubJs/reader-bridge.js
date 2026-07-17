// Ponte entre o C# (ReaderEpubPage.xaml.cs) e o epub.js. Superfície
// pequena de propósito: abrir livro, próxima/anterior página, ir pra um
// href do índice, e reaplicar estilo — tudo o resto (paginação real,
// parsing de OPF/NCX/nav, extração do zip) é responsabilidade do
// epub.js/JSZip, não nossa.
//
// Erro nenhum fica silencioso: qualquer exceção (síncrona ou de dentro
// de uma Promise) e qualquer erro não tratado da página inteira viram
// uma mensagem visível pro usuário via notify({type:"error"}) — sem
// isso, um erro aqui dentro vira tela em branco sem pista nenhuma do
// que aconteceu, e ninguém consegue depurar um Lumia real remotamente.
(function () {
  "use strict";

  var book = null;
  var rendition = null;

  // window.external.notify() (o canal JS->C# via ScriptNotify) não
  // chegava de volta pro C# de forma confiável pra conteúdo carregado
  // via NavigateToString — confirmado na prática (envio de pedaços do
  // livro via InvokeScriptAsync, C#->JS, funcionava normalmente; nada
  // do lado JS->C# nunca chegava). Em vez de depender só do "empurrar"
  // via notify, todo evento TAMBÉM atualiza um estado global com número
  // de sequência crescente — o C# "puxa" isso periodicamente chamando
  // SorvilReader.getState() via InvokeScriptAsync (o mesmo canal
  // C#->JS, já provado confiável). notify() continua sendo chamado
  // também, por garantia — não custa nada se às vezes funcionar.
  var _state = { seq: 0 };

  function pushState(partial) {
    var next = { seq: _state.seq + 1 };
    for (var key in partial) {
      if (Object.prototype.hasOwnProperty.call(partial, key)) {
        next[key] = partial[key];
      }
    }
    _state = next;
  }

  function notify(payload) {
    pushState(payload);
    try {
      if (window.external && typeof window.external.notify === "function") {
        window.external.notify(JSON.stringify(payload));
      }
    } catch (e) {
      // Sem WebView de verdade (ex.: abrindo reader.html direto num
      // navegador comum pra depurar) — não tem quem escute, ignora.
    }
  }

  // Console de diagnóstico de última instância: cada checkpoint() ACUMULA
  // uma linha (não sobrescreve) num elemento SEPARADO (#sorvil-console,
  // não #viewer) — sem passar por notify()/ScriptNotify nem pelo
  // polling de getState(), que já se provaram não confiáveis pra
  // reportar progresso nessa engine. É só manipulação de DOM local, não
  // depende de nenhum canal de comunicação que possa estar quebrado: se
  // a linha aparecer na tela, aquele passo rodou. Precisa ser um
  // elemento diferente de #viewer: uma versão anterior escrevia direto
  // em #viewer (onde book.renderTo() desenha o livro) e cada checkpoint
  // chamado DEPOIS do renderTo() apagava o iframe que o epub.js já
  // tinha desenhado ali — o livro carregava com sucesso (todo checkpoint
  // rodava) mas nunca aparecia, porque o próprio diagnóstico apagava o
  // resultado. hideConsole() remove #sorvil-console assim que o livro
  // termina de abrir de verdade, revelando #viewer por baixo.
  var _log = [];

  function checkpoint(text) {
    try {
      _log.push(text);
      var el = document.getElementById("sorvil-console");
      if (el) {
        el.textContent = _log
          .map(function (line, i) {
            return i + 1 + ". " + line;
          })
          .join("\n");
      }
    } catch (e) {
      // Se nem isso funcionar, não tem mais nada barato a tentar.
    }
  }

  function hideConsole() {
    try {
      var el = document.getElementById("sorvil-console");
      if (el && el.parentNode) {
        el.parentNode.removeChild(el);
      }
    } catch (e) {
      // Não crítico — na pior das hipóteses o console fica na tela.
    }
  }

  window.onerror = function (message, source, lineno, colno, error) {
    notify({
      type: "error",
      message: "Erro não tratado: " + message + " (linha " + lineno + ")",
    });
    return true;
  };

  // Achata a árvore de TOC do epub.js (que pode aninhar subitens) numa
  // lista só — mais fácil de mostrar numa ListView simples do que uma
  // árvore expansível.
  function flattenToc(items, depth) {
    var result = [];
    (items || []).forEach(function (item) {
      result.push({
        label: item.label ? item.label.trim() : "",
        href: item.href || "",
        depth: depth || 0,
      });
      if (item.subitems && item.subitems.length) {
        result = result.concat(flattenToc(item.subitems, (depth || 0) + 1));
      }
    });
    return result;
  }

  function applyStyle(style) {
    if (!rendition) {
      return;
    }

    // Mesma filosofia do leitor WebView anterior: só quatro coisas são
    // realmente controladas (tamanho de fonte, margem, espaçamento de
    // linha, cor de fundo/tema) — o resto do design do livro (recuo,
    // capitular, cores de destaque) fica intacto. p/div/li/blockquote
    // recebem font-size:1em pra responder ao controle de tamanho mesmo
    // em livros que fixam px direto nessas tags; h1-h6 ficam livres.
    var rules = {
      html: {
        "background-color": style.background + " !important",
      },
      body: {
        "background-color": style.background + " !important",
        color: style.foreground + " !important",
        "line-height": style.lineHeight + " !important",
        "text-align": style.justification + " !important",
        padding: style.margin + "px !important",
        margin: "0 !important",
      },
      "p, div, li, blockquote": {
        "font-size": "1em !important",
      },
      img: {
        "max-width": "100% !important",
        height: "auto !important",
      },
    };
    if (style.fontFamily) {
      rules.body["font-family"] = style.fontFamily + " !important";
    }

    rendition.themes.default(rules);
    rendition.themes.fontSize(style.fontSize + "%");
  }

  // atob + charCodeAt byte a byte — mais lento que um decode nativo em
  // teoria, mas O ÚNICO que se provou confiável nessa WebView. Uma
  // tentativa anterior usava XMLHttpRequest sobre uma URI "data:" com o
  // base64 inteiro embutido na própria URL pra decodificar em código
  // nativo do motor; isso travava de forma reproduzível mesmo com um
  // EPUB pequeno — bem o perfil de um bug de URL longa demais no
  // parser dessa engine antiga, travando o próprio xhr.open() antes de
  // sequer chamar send(). Como essa chamada nunca retornava, e a
  // WebView processa chamadas de script uma de cada vez, isso também
  // travava QUALQUER InvokeScriptAsync seguinte — inclusive o polling
  // de estado, o que explicava a tela ficar parada sem nenhum progresso
  // depois disso. Não reintroduzir esse atalho sem testar num aparelho
  // de verdade.
  function base64ToArrayBufferSlow(base64) {
    var binary = atob(base64);
    var len = binary.length;
    var bytes = new Uint8Array(len);
    for (var i = 0; i < len; i++) {
      bytes[i] = binary.charCodeAt(i);
    }
    return bytes.buffer;
  }

  function wireRendition() {
    rendition.on("relocated", function (location) {
      var percentage = 0;
      try {
        if (book.locations && book.locations.length()) {
          percentage = book.locations.percentageFromCfi(location.start.cfi);
        }
      } catch (e) {
        // locations não geradas (custam caro pro livro inteiro, não
        // geramos de propósito) — percentage fica 0, só o índice de
        // capítulo/TOC ainda funciona normalmente.
      }
      notify({
        type: "relocated",
        cfi: (location.start && location.start.cfi) || "",
        href: (location.start && location.start.href) || "",
        percentage: percentage,
        atStart: location.atStart === true,
        atEnd: location.atEnd === true,
      });
    });

    rendition.on("rendered", function (section) {
      notify({ type: "rendered", href: section.href });
    });
  }

  function openBookFromBase64(base64Data, startCfi, styleJson) {
    checkpoint("1/8 openBookFromBase64 iniciou (" + base64Data.length + " chars base64)");

    var style;
    try {
      style = JSON.parse(styleJson);
    } catch (err) {
      checkpoint("ERRO no passo 1 (JSON.parse estilo): " + String(err && err.message ? err.message : err));
      notify({ type: "error", message: "Falha ao ler estilo: " + String(err && err.message ? err.message : err) });
      return;
    }
    checkpoint("2/8 estilo interpretado");

    var arrayBuffer;
    try {
      arrayBuffer = base64ToArrayBufferSlow(base64Data);
    } catch (err) {
      checkpoint("ERRO no passo 2 (decode base64): " + String(err && err.message ? err.message : err));
      notify({ type: "error", message: "Falha ao decodificar o livro: " + String(err && err.message ? err.message : err) });
      return;
    }
    checkpoint("3/8 decodificado (" + arrayBuffer.byteLength + " bytes)");
    notify({ type: "progress", stage: "decodificado" });

    try {
      book = ePub(arrayBuffer);
      checkpoint("4/8 ePub() construído");
      // flow:"scrolled-continuous" sozinho NÃO faz o que o nome sugere —
      // conferido direto no código-fonte do epub.js (src/rendition.js):
      // é só um apelido que vira flow:"scrolled" puro e simples. Quem de
      // fato decide se os capítulos ficam emendados numa rolagem só
      // (em vez de mostrados um de cada vez) é a opção SEPARADA
      // "manager" — sem manager:"continuous" explícito, o manager padrão
      // trata cada seção/capítulo como uma unidade isolada, e é por
      // isso que next()/prev() pulavam capítulo inteiro em vez de rolar
      // uma tela. Com manager:"continuous" (conferido em
      // src/managers/continuous/index.js), next()/prev() fazem
      // scrollBy(0, this.layout.height) — uma tela por vez, cruzando
      // capítulo suavemente, exatamente o comportamento esperado.
      //
      // method:"write" força o mesmo document.write() em iframe que já
      // provou funcionar pro documento externo (via NavigateToString) —
      // sem isso, o epub.js detecta suporte a "srcdoc" e usa
      // iframe.srcdoc, que combinado com sandbox="allow-same-origin"
      // tem bug conhecido de engines EdgeHTML/Trident antigas tratando
      // o iframe como origem diferente ("Permission denied" ao tentar
      // acessar contentWindow/contentDocument depois). "write" é a
      // própria alternativa que o epub.js já tem pronta pra esse caso,
      // não lógica inventada por nós.
      rendition = book.renderTo("viewer", {
        width: "100%",
        height: "100%",
        manager: "continuous",
        flow: "scrolled",
        method: "write",
        spread: "none",
      });
      checkpoint("5/8 renderTo() chamado");

      applyStyle(style);
      wireRendition();
      checkpoint("6/8 estilo aplicado, eventos ligados — esperando book.ready");
    } catch (err) {
      checkpoint("ERRO no passo 4-6 (ePub/renderTo): " + String(err && err.message ? err.message : err));
      notify({ type: "error", message: "Falha ao inicializar o epub.js: " + String(err && err.message ? err.message : err) });
      return;
    }

    book.ready
      .then(function () {
        checkpoint("7/8 book.ready resolvido — buscando navegação/TOC");
        notify({ type: "progress", stage: "processado" });
        return book.loaded.navigation;
      })
      .then(function (nav) {
        checkpoint("8/8 TOC pronto — exibindo página");
        notify({ type: "ready", toc: flattenToc(nav.toc) });
        // Catch aninhado de propósito, só em volta do display() — se o
        // CFI salvo for inválido/de um esquema antigo, abre do início
        // em vez de deixar em branco. Um catch geral aqui embaixo (fora
        // deste .then) também pegaria falha de QUALQUER etapa anterior
        // (ePub() inválido, book.ready rejeitado) e tentaria
        // rendition.display() mesmo sem rendition existir, trocando a
        // mensagem de erro real por uma confusa.
        return rendition.display(startCfi || undefined).catch(function () {
          return rendition.display();
        });
      })
      .then(function () {
        hideConsole();
      })
      .catch(function (err) {
        checkpoint("ERRO no passo 7-8 (book.ready/navigation/display): " + (err && err.message ? err.message : String(err)));
        notify({
          type: "error",
          message: "Falha ao abrir o livro: " + (err && err.message ? err.message : String(err)),
        });
      });
  }

  // O .epub inteiro (base64) chega em pedaços pequenos via várias
  // chamadas InvokeScriptAsync (beginBook -> appendBookChunk* ->
  // finishBook) em vez de uma chamada só com um argumento de vários MB
  // — uma única chamada gigante parecia travar antes mesmo do primeiro
  // "progress" sair (nem "recebido" aparecia), o que aponta pro
  // marshaling C#->JS de uma string enorme como o gargalo de verdade,
  // não decodificação. Em pedaços, cada chamada é pequena e rápida, e
  // dá pra reportar progresso de verdade (quantos pedaços já chegaram).
  var _bookChunks = [];

  // Funções GLOBAIS simples (window.sorvilX), não um objeto
  // "SorvilReader" com métodos aninhados — WebView.InvokeScriptAsync
  // resolve o nome da função fazendo uma busca LITERAL em window pela
  // string inteira que a gente manda, não interpreta ponto como acesso
  // de propriedade. Chamar InvokeScriptAsync("SorvilReader.getState", ...)
  // procurava por uma propriedade CHAMADA "SorvilReader.getState" (com
  // o ponto no meio do nome), que nunca existiu — daí o erro real
  // "Unknown name" (DISP_E_UNKNOWNNAME, HRESULT 0x80020006) que só
  // apareceu quando paramos de engolir a exceção em silêncio. Bem
  // provável que TODAS as chamadas "SorvilReader.*" (beginBook,
  // appendBookChunk, finishBook, etc.) estivessem falhando do mesmo
  // jeito o tempo todo — e o "Enviando X/Y" que a gente via era só o
  // contador do laço em C#, que avança independente da chamada ter
  // funcionado ou não.
  window.sorvilBeginBook = function (totalChunks) {
    checkpoint("beginBook chamado, esperando " + totalChunks + " pedaços");
    _bookChunks = [];
    notify({ type: "progress", stage: "recebendo", done: 0, total: totalChunks });
  };

  window.sorvilAppendBookChunk = function (chunk) {
    _bookChunks.push(chunk);
    checkpoint("pedaço " + _bookChunks.length + " recebido (" + chunk.length + " chars)");
    notify({ type: "progress", stage: "recebendo", done: _bookChunks.length });
  };

  window.sorvilFinishBook = function (startCfi, styleJson) {
    checkpoint("0/8 finishBook chamado (" + _bookChunks.length + " pedaços)");
    var base64Data = _bookChunks.join("");
    _bookChunks = [];
    notify({ type: "progress", stage: "recebido" });
    openBookFromBase64(base64Data, startCfi, styleJson);
  };

  // Diagnóstico temporário pro bug "vira página pula capítulo" ainda
  // reproduzindo no Lumia real mesmo depois de manager:"continuous" —
  // testado num Chrome comum (com e sem ResizeObserver, simulando o
  // fallback que o EdgeHTML usa) e lá o next() rola uma tela por vez
  // corretamente, então a suspeita é que a geometria que o epub.js lê
  // do WebView real (layout.height / scrollTop do container) está
  // errada especificamente nessa engine. Este bloco expõe essa
  // geometria na tela via #sorvil-debug (elemento que NÃO é removido
  // por hideConsole) pra ler direto no aparelho. Remover depois que o
  // valor problemático for identificado.
  function debugGeometry() {
    var manager = rendition && rendition.manager;
    var container = manager && manager.container;
    var layoutHeight = manager && manager.layout ? manager.layout.height : undefined;
    return {
      scrollTop: container ? container.scrollTop : undefined,
      scrollHeight: container ? container.scrollHeight : undefined,
      clientHeight: container ? container.clientHeight : undefined,
      layoutHeight: layoutHeight,
      windowInnerHeight: window.innerHeight,
    };
  }

  function paintDebug(label, before, after) {
    try {
      var el = document.getElementById("sorvil-debug");
      if (!el) {
        return;
      }
      var delta = before && after && typeof before.scrollTop === "number" && typeof after.scrollTop === "number"
        ? after.scrollTop - before.scrollTop
        : undefined;
      el.textContent =
        label + " | href=" + (_state.href || "?") +
        " | delta=" + delta +
        " | layoutH=" + after.layoutHeight +
        " | scrollTop=" + after.scrollTop + "/" + after.scrollHeight +
        " | clientH=" + after.clientHeight +
        " | winH=" + after.windowInnerHeight;
    } catch (e) {
      // diagnóstico não pode ser causa de mais um erro.
    }
  }

  function withDebug(label, action) {
    var before = debugGeometry();
    action();
    // scrollBy/append podem ser assíncronos (continuous manager usa a
    // fila "q" pra encadear check()/append()) — duas voltas de rAF dão
    // tempo de sobra pro layout assentar antes de medir "depois".
    requestAnimationFrame(function () {
      requestAnimationFrame(function () {
        paintDebug(label, before, debugGeometry());
      });
    });
  }

  window.sorvilNext = function () {
    if (rendition) {
      withDebug("next()", rendition.next.bind(rendition));
    }
  };

  window.sorvilPrev = function () {
    if (rendition) {
      withDebug("prev()", rendition.prev.bind(rendition));
    }
  };

  window.sorvilGoToHref = function (href) {
    if (rendition) {
      rendition.display(href);
    }
  };

  window.sorvilSetStyle = function (styleJson) {
    try {
      applyStyle(JSON.parse(styleJson));
    } catch (err) {
      notify({ type: "error", message: "Falha ao aplicar estilo: " + String(err && err.message ? err.message : err) });
    }
  };

  // Chamado pelo C# via InvokeScriptAsync a cada intervalo — ver
  // comentário em pushState. Devolve o último estado (com "seq") como
  // JSON; o C# só reage quando "seq" muda.
  window.sorvilGetState = function () {
    return JSON.stringify(_state);
  };

  checkpoint(
    "bridge carregado — ePub existe? " + (typeof ePub) +
    " | JSZip existe? " + (typeof JSZip) +
    " | window.external? " + (typeof window.external)
  );
})();
