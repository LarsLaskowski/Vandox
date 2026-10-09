# 0051: Brand assets live in docs/assets; the web UI and the Telegram bot adopt them with their own issues

- **Status:** Accepted
- **Date:** 2026-10-05
- **Area:** —
- **Source:** Issue #12
- **Supersedes:** —

## Context

Issue #12 adds the Vandox logo as a set of SVG files (stacked and horizontal logo, each for light and dark
backgrounds, an icon and a favicon that switches colors itself) and asks for it to be used in the README,
the web UI (favicon and header logo, embedded with `go:embed`), the Telegram bot (a 512×512 PNG profile
picture) and the image metadata, with the colors documented as design tokens.

When #12 was worked, neither a web UI nor a Telegram bot existed: the web UI foundation is #24 (which
already requires "Logo and favicon from `docs/assets/`" and depends on #12), the bot is #60. The only
Telegram code was the configuration of its token. The issue's acceptance criteria (SVG set and PNG in
`docs/assets/`, README in light and dark mode, color tokens documented, OCI labels set) do not depend on
either.

Two facts constrain how the web UI can embed the files. `go:embed` patterns are relative to the
package's directory and may not contain `.` or `..` elements, so a package under `cmd/` or `internal/`
cannot embed `docs/assets/`; a package at the module root or inside `docs/assets/` could. And the image
build context is an allow-list: `.dockerignore` admits only `go.mod`, `go.sum`, `cmd/`, `internal/` and
`LICENSE`, and the build stage of `deploy/backend/Dockerfile` copies only `cmd/` and `internal/`, so any
embedding from `docs/assets/` also needs the build context and the `COPY` lines extended.

The favicon as given in #12 has a CSS defect. Its `<style>` sets `fill` and `stroke` on the class `.c`,
which both the shield group (`<g class="c" stroke="none">`) and the pulse line
(`<polyline class="c" … fill="none">`) carry. A style-sheet rule overrides SVG presentation attributes, so
the pulse line rendered as filled spikes instead of a line, and the shield segments got an outline. The
other five files have no `<style>` and render as intended.

## Options considered

1. **Implement every requirement of #12 now** — a web package with templates and a favicon handler, and a
   rendered PNG for a bot that does not exist. Speculative code without a consumer, built before the web
   UI's layout, security headers and theme switch are designed in #24; the PNG could not be set anywhere.
2. **Store the assets and document them now; the web UI and the bot adopt them in #24 and #60** — #12
   delivers everything that has a place today (files, README, tokens, image labels); the remaining parts
   go to the issues that create their surfaces, with the specifics added there as comments.
3. **Store the assets inside a Go package (e.g. under `internal/`) so they can be embedded directly** —
   avoids copies later, but puts documentation images into production code before any code uses them,
   and the README would point into a code package.
4. **Prescribe now how #24 embeds the files** — either copies in the web package pinned by a test, or a Go
   package at the module root or in `docs/assets/` that embeds them directly, with `.dockerignore` and the
   Dockerfile's `COPY` lines extended. Not decided here: both are workable, the choice depends on the web
   package layout and build-context policy that #24 designs, and deciding it before that code exists
   would bind #24 without a consumer to judge against.
5. **Keep the favicon byte-exact as given in #12** (and fix it later or leave it to the issue author) —
   keeps every file traceable to the issue text, but commits a favicon that does not render as the issue
   describes ("heavier pulse line") into the single source that #24 will serve. Rejected by the Product
   Manager in favor of correcting it now.
6. **Correct the favicon with an inline `style` attribute on the polyline** — fixes the fill, but a
   `style` attribute cannot switch on `prefers-color-scheme`, so the pulse line would stay light-mode
   green in dark mode. Rejected.

## Decision

Option 2. The six SVG files live in `docs/assets/` and are the single source of the logo. Five are
byte-identical to #12. The favicon is corrected in its `<style>` element and the polyline's `class` only
(Product Manager decision): `.c` sets only `fill`, so the group's `stroke="none"` applies again, and a
new class `.l` (`fill:none` and the stroke color, switched in the dark-mode block) replaces `.c` on the
polyline:
`<style>.c{fill:#086030}.l{fill:none;stroke:#086030}@media (prefers-color-scheme:dark){.c{fill:#3DAA6E}.l{stroke:#3DAA6E}}</style>`.
Everything else in the file is as given. `docs/BRANDING.md` documents the files, their use and the color tokens (`--vandox-color-brand`
`#086030` / dark `#3DAA6E`, `--vandox-color-ink` `#243142` / dark `#E6EAF0`). The README shows the logo
through a `<picture>` element that switches on `prefers-color-scheme`. The web UI favicon and header logo
are done with #24, the Telegram profile picture with #60; both issues receive a comment with the details.
The original PNG is added to `docs/assets/` by the maintainer.

## Consequences

- #24 chooses how the files it serves (`vandox-favicon.svg`, `vandox-logo-horizontal.svg`,
  `vandox-logo-horizontal-dark.svg`) reach the binary (option 4), and records the choice: copies in the
  web package need a unit test that compares each copy byte for byte with `docs/assets/`, so the two
  cannot drift; a root-level or `docs/assets/` package needs `.dockerignore` and the Dockerfile extended,
  which is a change to the release pipeline's build context. Either way `docs/assets/` stays the single
  source. Served as images (`<img>`, `<link rel="icon">`), the SVGs run no script and their inline
  `<style>` needs no CSP exception.
- #60 renders `vandox-icon.svg` to a 512×512 PNG and sets it in BotFather; Telegram's circular crop may
  need padding, because the icon's bar reaches nearly to the edges of its square.
- `docs/assets/` and not the code blocks in #12 is the reference for the favicon; the #24 comment says so.
  Anyone editing an SVG with a `<style>` must not rely on presentation attributes to override a class
  rule.
- A color change means changing the tokens in `docs/BRANDING.md` and every SVG together (and, after #24,
  whatever #24 embeds from them).
- Revisit if the assets need a build step (e.g. generated PNGs): then a generator in the repository would
  replace hand-copied files.
