# 0051: Brand assets live in docs/assets; the web UI and the Telegram bot adopt them with their own issues

- **Status:** Accepted
- **Date:** 2026-10-05
- **Area:** —
- **Source:** Issue #12
- **Supersedes:** —

## Context

Issue #12 adds the Vandox logo as SVG files (stacked and horizontal logo for light and dark backgrounds, an icon and a self-switching
favicon) and asks for it in the README, the web UI, the Telegram bot (a 512×512 PNG profile picture) and the image metadata, with the
colors documented as design tokens. When it was worked, neither a web UI nor a Telegram bot existed; the acceptance criteria that had a
place already (SVG set in `docs/assets/`, README in light and dark mode, tokens, image labels) did not depend on either. The favicon
as given had a CSS defect: its `<style>` set `fill` and `stroke` on a class carried by both the shield group and the pulse line, and a
style-sheet rule overrides SVG presentation attributes, so the pulse line rendered as filled spikes and the shield got an outline.

## Options considered

- **Implement every requirement of #12 now** — rejected: speculative code for a web UI and a bot that do not exist, before their layout
  and theme switch are designed.
- **Store the assets and document them now; the web UI and the bot adopt them with their own issues** — chosen.
- **Store the assets in a code package so they can be embedded directly** — rejected: documentation images in production code before
  anything uses them, and the README would point into a code package.
- **Prescribe now how the web UI includes the files** — rejected: the choice depends on the web layout and build-context policy decided
  there.
- **Keep the favicon byte-exact as given** — rejected by the Product Manager: it would commit a favicon that does not render as the
  issue describes into the single source. **Fix it with an inline `style` attribute** — rejected: it cannot follow
  `prefers-color-scheme`, so the pulse line would stay light-mode green in dark mode.

## Decision

The six SVG files live in `docs/assets/` and are the single source of the logo. Five are byte-identical to the issue; the favicon is
corrected in its `<style>` element and the polyline's class only (Product Manager decision): the shared class sets only `fill`, and a
new class for the line sets `fill:none` and the stroke color, switched in the dark-mode block. `docs/BRANDING.md` documents the files,
their use and the color tokens. The README shows the logo through a `<picture>` element that switches on `prefers-color-scheme`. The
web UI favicon and header logo are done with the web UI, the Telegram profile picture with the bot.

## Consequences

- The web UI decides how the files it serves reach the application and records the choice; `docs/assets/` stays the single source, so
  any copy needs a test that compares it byte for byte. Served as images, the SVGs run no script and their inline `<style>` needs no CSP
  exception.
- The bot renders `vandox-icon.svg` to a 512×512 PNG and sets it in BotFather; Telegram's circular crop may need padding.
- `docs/assets/`, not the code blocks in the issue, is the reference for the favicon. Anyone editing an SVG with a `<style>` must not rely on
  presentation attributes to override a class rule.
- A color change means changing the tokens in `docs/BRANDING.md` and every SVG together.
