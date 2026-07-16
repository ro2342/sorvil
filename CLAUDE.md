# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What this is

Sorvil is a native UWP ebook reader (C#/XAML) for Windows 10 Mobile (Lumia),
targeting a self-hosted **Calibre-Web** / **Calibre-Web-Automated** server. It's
a spiritual sibling to `the artistsway` (same visual language, same
build/sideload pipeline). Distributed by sideload (self-signed cert), not the
Store.

## No local toolchain — CI is the only build/validation signal

**There is no UWP toolchain on this Linux machine.** Nothing under `Sorvil/`
builds or runs locally. The only way to know if a change compiles is to push
and watch GitHub Actions:

```
git push
gh run watch          # or: gh run list, then gh run view --log-failed
```

Workflow **"02 - Build do appxbundle"** (`.github/workflows/02-build-appx.yml`)
runs on `windows-latest` for any push touching `Sorvil/**` or `site/**`, builds
`Release|ARM` with `.NET Native` (this combination is deliberate — see the
comment in the workflow and in `Services/ThemeModeService.cs`; Debug does not
run sideloaded outside Visual Studio), and on success auto-commits
`app/app.appxbundle` + `app/version.json` back to `main` and republishes
`site/` to GitHub Pages. Workflow **"01 - Gerar certificado de assinatura"**
only needs to be run once (already done) to produce `Sorvil_TemporaryKey.pfx`.

Because these workflow-triggered auto-commits can race with a manual push
(binary add/add conflict git can't merge), reconcile with
`git fetch origin main && git reset --soft origin/main` and recommit rather
than `git pull --rebase` if you hit that.

The expected workflow here is fully autonomous: make a change, sanity-check
any hand-edited XML/XAML locally (e.g.
`python3 -c "import xml.dom.minidom as m; m.parse('Sorvil/Views/ReaderEpubPage.xaml')"`),
commit, push, watch the run, and if it fails, read the log and fix the root
cause and re-push — repeat without stopping to check in.

**After every push that touches `Sorvil/**` or `site/**`, you are responsible
for watching that build through to a green result — this is not optional and
not "fire and forget":**

1. `git push`, then `gh run watch <id> --exit-status` (or `gh run list` +
   `gh run view --log-failed` if you didn't capture the id).
2. If it fails, read the failure log yourself, fix the root cause in the
   code, commit, and push again — repeat until green. Don't ask the user to
   check Actions or paste you a log; you have `gh` for that.
3. Only a **green** run actually ships anything: on success it auto-commits
   `app/app.appxbundle` + `app/version.json` to `main` and republishes
   `site/` to GitHub Pages. A red run leaves the previous release in place —
   the phone never sees your change at all until a build goes green.
4. `app/version.json` (built from `Package.appxmanifest`'s version + a
   timestamp) is literally the file `Services/UpdateCheckService.cs` polls
   at `https://ro2342.github.io/sorvil/app/version.json` to decide whether
   an update is available — it and `app/app.appxbundle` are the only two
   files that matter for the Lumia to find and offer the new version.
   `CheckAsync()` does a plain version-string compare
   (`CompareVersions(latest, installed) > 0`) against `Package.Current.Id.Version`
   on-device — it does not diff binaries or dates. **Every push that should
   actually reach the phone must bump `<Identity Version="...">` in
   `Package.appxmanifest`.** Skipping the bump still builds and republishes a
   fine `.appxbundle`, but `version.json`'s version string stays identical to
   what's installed, so the device reports "already up to date" even though
   the binary changed — this already happened once (fix landed in `60e4e4c`,
   forgotten bump caught and corrected one commit later in `d1d169b`/`b0a742f`).
   Version format is `Major.Minor.Build.Revision`; bumping the third segment
   per shipped fix is the pattern seen in git history.

## Critical gotcha: `Sorvil.csproj` is old-style, no wildcards

`Sorvil/Sorvil.csproj` lists every `.cs` and `.xaml` file explicitly under
`<Compile Include=.../>` / `<Page Include=.../>` — it does **not** glob the
directory. **Any new `.cs` or `.xaml` file must be added to the `.csproj` by
hand**, or the CI build will silently not compile it (or XAML-codegen will
fail referencing a type that "doesn't exist").

## Architecture

- **Shell is 100% native XAML** — `MainPage.xaml` (SplitView + hamburger,
  fixed header), navigation between `Views/*Page.xaml` happens in a nested
  `ContentFrame`. The reader pages are the one exception: they navigate on
  `App.RootFrame` (the root window frame), not the nested frame, because the
  reader needs to own the whole screen above the shell's header/SplitView —
  see `Services/ReaderNavigation.cs`, which is the single dispatch point
  deciding PDF vs EPUB reader by `BookRecord.Format`.
- **EPUB rendering is fully native — no WebView.** `Services/EpubExtractor.cs`
  unzips the EPUB to app-local storage; `Services/EpubContentParser.cs` reads
  a chapter's XHTML and builds native `Paragraph` objects (no CSS, no
  third-party rendering engine) applied via `ReaderTextStyle`, so formatting
  is consistent across whichever `RichTextBlock`/`RichTextBlockOverflow`
  container native pagination spreads them into. Common semantic tags
  (headings, emphasis, images, line breaks) are supported; anything exotic
  degrades to plain inner text rather than breaking the chapter. PDF rendering
  uses `Windows.Data.Pdf` natively (±1-page render window to limit RAM), also
  no WebView.
- **OPDS is the only server protocol.** `Services/OpdsClient.cs` talks to
  `/opds` (Atom/OPDS feed) — covers via `.../image` and `.../image/thumbnail`
  links, per-format download via acquisition links, pagination via
  `rel="next"`, search via OpenSearch. Every request must send a `User-Agent`
  header (real bug hit against CWA in the past — see git history
  `96e4071`). Credentials live in
  `Windows.Security.Credentials.PasswordVault` (`CredentialService.cs`), never
  in plaintext; server base URL lives in `LocalSettings`
  (`ServerConfigStore.cs`).
- **Network capability**: home Calibre-Web servers are usually on a LAN IP, so
  `Package.appxmanifest` declares both `internetClient` and
  `privateNetworkClientServer` — the first alone is not enough for UWP to
  reach a LAN server.
- **`BookRecord.Id` is composite** (`entryId:extension`) — the same OPDS entry
  can exist as multiple downloaded-format records (e.g. epub + pdf) with
  separate ids. This is intentional, not a bug.
- Local persistence: `LibraryDataStore.cs` (downloaded books),
  `ReaderPreferenceStore.cs` (font/theme/gesture prefs per reader),
  `ThemePreferenceStore.cs` + `ThemeModeService.cs` (app-wide light/dark/auto —
  `ElementTheme.Default` already tracks system theme natively, no manual
  re-apply needed on system theme change).
- `Sorvil/generate_tile_assets.py` regenerates the `Assets/*.png` tile/icon
  set from `logo.svg` — rerun it after changing the logo.

## Repo layout

```
Sorvil/                 C# UWP project (see csproj gotcha above)
  Models/                ServerProfile, OpdsEntry, BookRecord, ...
  Services/              OPDS client, EPUB extraction/parsing, stores, theming
  Views/                 one XAML page per screen
  Assets/                generated tile/icon PNGs
site/                   GitHub Pages download page (app/ subpath is the stable download link, published by CI)
.github/workflows/      01 = one-time cert generation, 02 = build + publish on every push
```

## In-progress reader redesign spec

`sorvil-comportamento.md` and `sorvil-mockup.html` (repo root, untracked)
describe a newer target behavior/interaction spec for the reader screen
(immersive-by-default text view, tap-to-reveal top/bottom dark toolbars, a
side index flyout, font/brightness/gesture panels attached under the top
toolbar, hardware-back priority order). Treat these as the current design
target when working on `ReaderEpubPage`/`ReaderPdfPage` — check whether the
current implementation already matches before assuming it needs a rewrite.
