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
  // uma linha (não sobrescreve) direto no <div id="viewer"> — a própria
  // WebView já visível na tela — sem passar por notify()/ScriptNotify
  // nem pelo polling de getState(), que já se provaram não confiáveis
  // pra reportar progresso nessa engine. É só manipulação de DOM local,
  // não depende de nenhum canal de comunicação que possa estar quebrado:
  // se a linha aparecer na tela, aquele passo rodou; a última linha da
  // lista é sempre o ponto exato onde travou (ou o erro, se algo
  // lançou). O C# esconde o spinner/texto de carregamento assim que
  // esse console começa a ser usado, pra não competir visualmente.
  var _log = [];

  function checkpoint(text) {
    try {
      _log.push(text);
      var el = document.getElementById("viewer");
      if (el) {
        var html =
          '<div style="position:fixed;top:0;left:0;right:0;bottom:0;' +
          "overflow:auto;padding:12px;font-size:16px;line-height:1.5;" +
          'font-family:Consolas,monospace;color:#0f0;background:#000;' +
          'z-index:99999;white-space:pre-wrap;word-break:break-all;">' +
          _log
            .map(function (line, i) {
              return i + 1 + ". " + line;
            })
            .join("\n") +
          "</div>";
        el.innerHTML = html;
      }
    } catch (e) {
      // Se nem isso funcionar, não tem mais nada barato a tentar.
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
        href: item.href,
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
        cfi: location.start.cfi,
        href: location.start.href,
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
      rendition = book.renderTo("viewer", {
        width: "100%",
        height: "100%",
        flow: "scrolled-doc",
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

  window.SorvilReader = {
    beginBook: function (totalChunks) {
      checkpoint("beginBook chamado, esperando " + totalChunks + " pedaços");
      _bookChunks = [];
      notify({ type: "progress", stage: "recebendo", done: 0, total: totalChunks });
    },

    appendBookChunk: function (chunk) {
      _bookChunks.push(chunk);
      checkpoint("pedaço " + _bookChunks.length + " recebido (" + chunk.length + " chars)");
      notify({ type: "progress", stage: "recebendo", done: _bookChunks.length });
    },

    finishBook: function (startCfi, styleJson) {
      checkpoint("0/8 finishBook chamado (" + _bookChunks.length + " pedaços)");
      var base64Data = _bookChunks.join("");
      _bookChunks = [];
      notify({ type: "progress", stage: "recebido" });
      openBookFromBase64(base64Data, startCfi, styleJson);
    },

    next: function () {
      if (rendition) {
        rendition.next();
      }
    },

    prev: function () {
      if (rendition) {
        rendition.prev();
      }
    },

    goToHref: function (href) {
      if (rendition) {
        rendition.display(href);
      }
    },

    setStyle: function (styleJson) {
      try {
        applyStyle(JSON.parse(styleJson));
      } catch (err) {
        notify({ type: "error", message: "Falha ao aplicar estilo: " + String(err && err.message ? err.message : err) });
      }
    },

    // Chamado pelo C# via InvokeScriptAsync a cada intervalo — ver
    // comentário em pushState. Devolve o último estado (com "seq") como
    // JSON; o C# só reage quando "seq" muda.
    getState: function () {
      return JSON.stringify(_state);
    },
  };

  checkpoint(
    "bridge carregado — ePub existe? " + (typeof ePub) +
    " | JSZip existe? " + (typeof JSZip) +
    " | window.external? " + (typeof window.external)
  );
})();
