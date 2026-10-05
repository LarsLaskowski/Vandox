# 0051: Brand assets live in docs/assets; the web UI and the Telegram bot adopt them with their own issues

- **Status:** Proposed
- **Date:** 2026-10-05
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

Two facts constrain how the web UI can use the files: `go:embed` patterns may not contain `..`, so a
package under `internal/` or `cmd/` cannot embed `docs/assets/`; and `.dockerignore` keeps `docs/` out of
the image build context.

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

## Decision

Option 2. The six SVG files live in `docs/assets/` exactly as given in #12 and are the single source of
the logo; `docs/BRANDING.md` documents the files, their use and the color tokens (`--vandox-color-brand`
`#086030` / dark `#3DAA6E`, `--vandox-color-ink` `#243142` / dark `#E6EAF0`). The README shows the logo
through a `<picture>` element that switches on `prefers-color-scheme`. The web UI favicon and header logo
are done with #24, the Telegram profile picture with #60; both issues receive a comment with the details.
The original PNG is added to `docs/assets/` by the maintainer.

## Consequences

- #24 keeps copies of the files it serves (`vandox-favicon.svg`, `vandox-logo-horizontal.svg`,
  `vandox-logo-horizontal-dark.svg`) in its web package for `go:embed`, with a unit test that compares
  each copy byte for byte with `docs/assets/`, so the two cannot drift. Served as images (`<img>`,
  `<link rel="icon">`), the SVGs run no script and their inline `<style>` needs no CSP exception.
- #60 renders `vandox-icon.svg` to a 512×512 PNG and sets it in BotFather; Telegram's circular crop may
  need padding, because the icon's bar reaches nearly to the edges of its square.
- A color change means changing the tokens in `docs/BRANDING.md` and every SVG together (and, after #24,
  the copies through the pinning test).
- Revisit if the assets need a build step (e.g. generated PNGs): then a generator in the repository would
  replace hand-copied files.
