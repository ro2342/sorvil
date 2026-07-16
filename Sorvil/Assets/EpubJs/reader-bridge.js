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

  function notify(payload) {
    try {
      if (window.external && typeof window.external.notify === "function") {
        window.external.notify(JSON.stringify(payload));
      }
    } catch (e) {
      // Sem WebView de verdade (ex.: abrindo reader.html direto num
      // navegador comum pra depurar) — não tem quem escute, ignora.
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

  window.SorvilReader = {
    // base64Data: o .epub inteiro, codificado em base64 pelo C# — evita
    // depender de fetch() entre esquemas ms-appx/ms-appdata dentro da
    // WebView (incerto se funciona nessa engine específica), passando
    // os bytes direto como argumento da chamada.
    openBook: function (base64Data, startCfi, styleJson) {
      try {
        var style = JSON.parse(styleJson);
        var binary = atob(base64Data);
        var len = binary.length;
        var bytes = new Uint8Array(len);
        for (var i = 0; i < len; i++) {
          bytes[i] = binary.charCodeAt(i);
        }

        book = ePub(bytes.buffer);
        rendition = book.renderTo("viewer", {
          width: "100%",
          height: "100%",
          flow: "scrolled-doc",
          spread: "none",
        });

        applyStyle(style);
        wireRendition();

        book.ready
          .then(function () {
            return book.loaded.navigation;
          })
          .then(function (nav) {
            notify({ type: "ready", toc: flattenToc(nav.toc) });
            return rendition.display(startCfi || undefined);
          })
          .catch(function () {
            // CFI salvo inválido/de um esquema antigo — abre do início
            // em vez de deixar a tela em branco.
            return rendition.display();
          })
          .catch(function (err) {
            notify({
              type: "error",
              message: "Falha ao exibir o livro: " + (err && err.message ? err.message : String(err)),
            });
          });
      } catch (err) {
        notify({
          type: "error",
          message: "Falha ao abrir o livro: " + (err && err.message ? err.message : String(err)),
        });
      }
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
  };
})();
