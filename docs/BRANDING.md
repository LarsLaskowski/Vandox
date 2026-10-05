# Branding

The Vandox logo is a segmented shield with a pulse line (green) and the wordmark VANDOX (dark blue,
Montserrat Bold converted to paths, so no font is needed). The files live in `docs/assets/` and are the
single source; the original PNG is kept there by the maintainer.

## Files

| File | Shows | Use |
|---|---|---|
| `vandox-logo.svg` | stacked logo, light backgrounds | README (light), documents |
| `vandox-logo-dark.svg` | stacked logo, dark backgrounds | README (dark) |
| `vandox-logo-horizontal.svg` | shield and wordmark side by side, light backgrounds | web UI header, light theme |
| `vandox-logo-horizontal-dark.svg` | shield and wordmark side by side, dark backgrounds | web UI header, dark theme |
| `vandox-icon.svg` | shield only, green | source of the Telegram bot's profile picture (rendered as a 512×512 PNG) |
| `vandox-favicon.svg` | shield only, heavier pulse line; switches to the dark-mode green itself via `prefers-color-scheme` | web UI favicon |

## Color tokens

| Token | CSS custom property | Light | Dark | Used for |
|---|---|---|---|---|
| `brand` | `--vandox-color-brand` | `#086030` | `#3DAA6E` | shield, pulse line, accents |
| `ink` | `--vandox-color-ink` | `#243142` | `#E6EAF0` | wordmark, primary text |

Reference definition:

```css
:root {
  --vandox-color-brand: #086030;
  --vandox-color-ink: #243142;
}
@media (prefers-color-scheme: dark) {
  :root {
    --vandox-color-brand: #3DAA6E;
    --vandox-color-ink: #E6EAF0;
  }
}
```

A manual theme switch in the web UI sets the same dark values. The files in `docs/assets/` use exactly
these values, so a color change means changing the tokens and every SVG together.

## Where the logo is used

- **README**: a `<picture>` element switches between the light and dark stacked logo via
  `prefers-color-scheme`.
- **Web UI** (with #24): favicon and header logo. `docs/assets/` stays the single source. The image build
  context currently contains only `go.mod`, `go.sum`, `cmd/`, `internal/` and `LICENSE`, so #24 decides how
  the served files reach the binary (decision record 0051).
- **Telegram bot** (with #60): profile picture rendered from `vandox-icon.svg` at 512×512 and set in
  BotFather.
- **Container image**: OCI labels title, description, source and licenses. There is no logo label, because
  OCI defines none (decision record 0052).
