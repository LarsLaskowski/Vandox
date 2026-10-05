# Plan: Add logo and branding

Source: Issue #12
Status: Draft
Tier: security — the change edits `deploy/backend/Dockerfile` (an OCI label), which is Docker
configuration and *Security areas* item 13 (release pipeline and published artifacts) in
`.squad/project.md`; the rest (SVG files, Markdown) alone would be `trivial` (SVG files are not Markdown,
so not `docs`), and two decision records are needed.

## Problem / root cause

Feature-like documentation issue: the repository has no logo. The issue asks to store an SVG set under
`docs/assets/`, show the logo in the README in light and dark mode, use it in the web UI and the Telegram
bot, set OCI labels on the image and document the colors as design tokens.

Claims of the issue, checked against the repository (`main` at `56c78f6`, branch at `2eca8ae`):

- *"Depends on #5"* — confirmed satisfied: #5 ("Adopt the Squad-Spec-Repository-Template") is closed.
  It is not the dependency that matters, though; see the next two points.
- *"Web UI: favicon, horizontal variant in the header, embedded via `go:embed`"* — **cannot be implemented
  now**: no web UI exists. The only Go code is `cmd/vandox-agent`, `cmd/vandoxd`, `internal/cli`,
  `internal/config`, `internal/model`, `internal/version`, `internal/wire`; no `go:embed`, no HTML, no
  HTTP server. The web UI foundation is issue #24 (open), which already requires "Logo and favicon from
  `docs/assets/`" and lists "Depends on #12".
- *"Telegram bot profile picture: PNG rendered from `vandox-icon.svg` (512×512)"* — **cannot be applied
  now**: no Telegram bot exists (only the secret `VANDOX_TELEGRAM_BOT_TOKEN` in
  `internal/config/config.go:35`, marked "#60" in `internal/config/backend.go:29`). The bot is #60 (open).
  A bot's profile picture is set by its owner in BotFather, not by code.
- *"OCI labels on the image: title, description, source, licenses, plus the logo URL if supported"* —
  **partly already true**: `deploy/backend/Dockerfile` exists (`LABEL` block of the runtime stage at
  lines 43–50 on the branch) and already sets
  `org.opencontainers.image.title="vandox"`, `.source="https://github.com/LarsLaskowski/Vandox"` and
  `.licenses="MIT"` (matches `LICENSE`). `.description` is missing. **A logo URL is not supported**: the
  OCI image annotation keys (`created`, `authors`, `url`, `documentation`, `source`, `version`,
  `revision`, `vendor`, `licenses`, `ref.name`, `title`, `description`, `base.digest`, `base.name`)
  define no logo key, and Docker Hub (where the image is published, record 0027) reads no logo label.
- *"The SVG set ... no font is needed"* — confirmed: the six files given (copied byte-exact to the
  scratchpad by the orchestrator) are well-formed XML, contain only `path`, `polyline`, `g`, `title` and,
  in the favicon, one `<style>`; no `<text>`, no font, no `<script>`, no external reference (`href`).
  Each file matches the issue's code block exactly (compared programmatically, LF line endings, trailing
  newline).
- *Colors* — confirmed: `vandox-logo.svg` and `vandox-logo-horizontal.svg` use exactly `#086030` and
  `#243142`; the `-dark` variants exactly `#3DAA6E` and `#E6EAF0`; `vandox-icon.svg` only `#086030`;
  `vandox-favicon.svg` switches `#086030` → `#3DAA6E` itself through
  `@media (prefers-color-scheme:dark)` in its `<style>`.
- *"The original PNG is added by the maintainer"* — not part of the squad's diff (see *Out of scope*).

Related constraint found on the way (not a bug today, a trap for #24), stated precisely:

- `go:embed` patterns are relative to the embedding package's directory and may not contain `.` or `..`
  elements, so a package under `cmd/` or `internal/` cannot embed `docs/assets/`. A package whose
  directory contains `docs/assets/` (the module root) or a package inside `docs/assets/` itself could.
- The image build is the real obstacle: `.dockerignore` is an allow-list (`*`, then `!go.mod`, `!go.sum`,
  `!cmd/`, `!internal/`, `!LICENSE`), and the build stage of `deploy/backend/Dockerfile` copies only
  `cmd/` and `internal/` (lines 20–21). Any embedding of `docs/assets/` therefore needs either copies
  inside `cmd/`/`internal/` or a change to `.dockerignore` and the Dockerfile's `COPY` lines.

How #24 resolves this (copies pinned by a test, or a Go package at the root or in `docs/assets/` plus
build-context changes) is #24's decision; record 0051 names both paths and decides neither (see
*Challenge*, objection 2).

## Acceptance criteria

Taken from the issue, narrowed to what exists (record 0051):

- [ ] AC1: `docs/assets/` contains exactly the six SVG files `vandox-logo.svg`, `vandox-logo-dark.svg`,
  `vandox-logo-horizontal.svg`, `vandox-logo-horizontal-dark.svg`, `vandox-icon.svg`,
  `vandox-favicon.svg`, each byte-identical to the issue's code block (SHA-256 below). (The original PNG
  is the maintainer's part of the issue's first criterion.)
- [ ] AC2: `README.md` starts with a `<picture>` element that shows `docs/assets/vandox-logo-dark.svg`
  under `(prefers-color-scheme: dark)` and `docs/assets/vandox-logo.svg` otherwise, with `alt="Vandox"`.
- [ ] AC3: `docs/BRANDING.md` documents the color tokens with light and dark values (`#086030`/`#3DAA6E`,
  `#243142`/`#E6EAF0`), their CSS custom property names, which logo file to use where, and the hand-over
  to the web UI (#24) and the Telegram bot (#60); the README layout block points to it.
- [ ] AC4: the runtime stage of `deploy/backend/Dockerfile` sets `org.opencontainers.image.title`,
  `.description`, `.source` and `.licenses`; no logo label; every other line of the Dockerfile unchanged.
- [ ] AC5: decision records 0051 and 0052 exist and match what was built.

SHA-256 of the six files (from the scratchpad copies, which equal the issue text):

```
586ced3831f01b5aee4daf31ed690ee0083ce0586007d6475296812de93242a5  vandox-logo.svg
2369cf27cd98dd5d459967833c7a3de4e6583cf39fa70d43d3c9dddbc4630b0a  vandox-logo-dark.svg
2baa235d55653dcf77a38427c993511000ecafa32fa8e669495f6c46aed75ecd  vandox-logo-horizontal.svg
783f550c8ab08df05b5a26b50970cf17caeef1f3eb876c778bb3a86fcc3bcd1a  vandox-logo-horizontal-dark.svg
2c21472869c1c44e8390b48cb94e200d06cd476c6f9d6fb53d94c256c7b6a85f  vandox-icon.svg
292d0a9c021e1064ef07825b8bd186851b36b5bca42e3fc8ba77aa75b6b028f5  vandox-favicon.svg
```

## Verification without tests

The change touches no production or test code (no `.go` file): steps 4 (*Skeleton*), 5 (*Tests first*)
and the *Coverage gate* of step 6 are **not applicable** (*Changes without production or test code* in
`.squad/routing.md`). Step 7 still runs *Format check* and the *Analyzer gate* (both must stay clean;
no Go file changes). Per criterion:

| AC | Verified where | By whom |
| -- | -------------- | ------- |
| AC1 | `cd docs/assets && sha256sum -c` against the six lines above, and `ls docs/assets` shows exactly those six files (plus the maintainer's PNG if it was added meanwhile); `python3 -c "import xml.dom.minidom,sys;[xml.dom.minidom.parse(f) for f in sys.argv[1:]]" docs/assets/*.svg` parses all | Code Officer in step 7 (read-only), Reviewer in step 8 |
| AC2 | Read-only check of the `README.md` diff; after the push, the README on the branch viewed on GitHub in light and in dark theme | Reviewer in step 8; orchestrator after the push (logged) |
| AC3 | `docs/BRANDING.md` values compared with the issue and with `grep -o '#[0-9A-Fa-f]\{6\}' docs/assets/*.svg` | Reviewer in step 8 |
| AC4 | Read-only diff of `deploy/backend/Dockerfile`: exactly one added line in the `LABEL` block; the release workflow's pull-request dry run (`.github/workflows/release.yml`, triggered by the Dockerfile path) builds and verifies the image in step 11. Where a Docker daemon is available: `docker build -f deploy/backend/Dockerfile -t vandox:issue12 .` and `docker image inspect --format '{{json .Config.Labels}}' vandox:issue12` lists the four keys (no daemon in the planning session) | Security and Reviewer in step 8; orchestrator in step 11 |
| AC5 | Records read against the diff | Lead in step 9 |

## Approach

1. Copy the six scratchpad files byte-exact into `docs/assets/` (`cp`, not retyped).
2. Edit `README.md` (exact edits below).
3. Add `docs/BRANDING.md` (content below).
4. Add the description label to `deploy/backend/Dockerfile`.
5. Web UI and Telegram parts move to #24 and #60 (record 0051); the orchestrator comments on both issues
   (texts under *Out of scope / follow-ups*).

## Affected projects and types

| Project | Type / file | Change |
| ------- | ----------- | ------ |
| docs | `docs/assets/vandox-logo.svg` | new, byte-exact copy |
| docs | `docs/assets/vandox-logo-dark.svg` | new, byte-exact copy |
| docs | `docs/assets/vandox-logo-horizontal.svg` | new, byte-exact copy |
| docs | `docs/assets/vandox-logo-horizontal-dark.svg` | new, byte-exact copy |
| docs | `docs/assets/vandox-icon.svg` | new, byte-exact copy |
| docs | `docs/assets/vandox-favicon.svg` | new, byte-exact copy |
| docs | `docs/BRANDING.md` | new |
| repo | `README.md` | logo header, layout line |
| deploy | `deploy/backend/Dockerfile` | one `LABEL` line added |
| docs | `docs/decisions/0051-…`, `0052-…` | new (Lead, done) |

### `README.md`, exact edits

Replace line 1 (`# Vandox`) with:

```html
<h1 align="center">
  <picture>
    <source media="(prefers-color-scheme: dark)" srcset="docs/assets/vandox-logo-dark.svg">
    <img src="docs/assets/vandox-logo.svg" alt="Vandox" width="280">
  </picture>
</h1>
```

(The logo carries the wordmark, so it replaces the text heading instead of duplicating it; the `alt`
keeps "Vandox" as the heading's text for screen readers and when images do not load.) Lines 3–5 (the
introduction) stay as they are, after one blank line.

In the *Layout* code block, insert after `docs/               documentation` (column alignment as the
other lines, 20 characters before the description):

```
docs/assets/        logo, icon and favicon, see docs/BRANDING.md
```

### `docs/BRANDING.md`, content

Sections, in this order (wording is the Dev's; the facts are fixed):

1. `# Branding` — one paragraph: the logo is a segmented shield with a pulse line (green) and the wordmark
   VANDOX (dark blue, Montserrat Bold converted to paths, so no font is needed). The files live in
   `docs/assets/` and are the single source; the original PNG is kept there by the maintainer.
2. `## Files` — table *File | Shows | Use*:
   - `vandox-logo.svg` — stacked logo, light backgrounds — README (light), documents
   - `vandox-logo-dark.svg` — stacked logo, dark backgrounds — README (dark)
   - `vandox-logo-horizontal.svg` — shield and wordmark side by side, light backgrounds — web UI header,
     light theme
   - `vandox-logo-horizontal-dark.svg` — same, dark backgrounds — web UI header, dark theme
   - `vandox-icon.svg` — shield only, green — source of the Telegram bot's profile picture (rendered as a
     512×512 PNG)
   - `vandox-favicon.svg` — shield only, heavier pulse line; switches to the dark-mode green itself via
     `prefers-color-scheme` — web UI favicon
3. `## Color tokens` — table *Token | CSS custom property | Light | Dark | Used for*:
   - `brand` — `--vandox-color-brand` — `#086030` — `#3DAA6E` — shield, pulse line, accents
   - `ink` — `--vandox-color-ink` — `#243142` — `#E6EAF0` — wordmark, primary text
   followed by this CSS block as the reference definition:
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
   and one sentence: a manual theme switch in the web UI sets the same dark values; the files in
   `docs/assets/` use exactly these values, so a color change means changing the tokens and every SVG
   together.
4. `## Where the logo is used` — README (`<picture>` switching via `prefers-color-scheme`, done); web UI
   (favicon and header logo, with #24; `docs/assets/` stays the single source, and the image build
   context currently contains only `go.mod`, `go.sum`, `cmd/`, `internal/` and `LICENSE`, so #24 decides
   how the served files reach the binary, record 0051); Telegram bot (profile picture rendered from
   `vandox-icon.svg` at 512×512 and set in BotFather, with #60); container image (OCI labels title,
   description, source and licenses; no logo label, record 0052).

### `deploy/backend/Dockerfile`, exact edit

In the `LABEL` block of the runtime stage, insert directly after the line
`LABEL org.opencontainers.image.title="vandox" \` (and before
`      org.opencontainers.image.source="https://github.com/LarsLaskowski/Vandox" \`):

```
      org.opencontainers.image.description="Vandox backend: lean monitoring for a Plesk-managed Linux server that reconstructs outages from logs and metrics" \
```

Indentation as the neighbouring lines (6 spaces), ending with ` \`. No `$`, backquote or double quote
inside the value; ASCII only. Nothing else in the file changes (the release workflow checks the `ARG` and
`FROM` lines, which stay untouched).

## Signatures (for the Dev's skeleton)

None — no Go code changes; step 4 does not apply.

## Test files

None — no production or test code (see *Verification without tests*). No existing test calls a changed
signature.

## Documentation updates

`README.md` and the new `docs/BRANDING.md` as above. `docs/ARCHITECTURE.md`: none (no guarantee or flow
changes; the *Deployment* section does not list labels other than the base-image ones, which are
unchanged). `README.md` configuration table and environment variables: none (no option added).
`.squad/project.md`, `.squad/stack.md`: none (no new security area, command or coupling point in code yet;
#24 adds a coupling point if its chosen way of embedding the assets creates one).

## Architecture check

No guarantee from `docs/ARCHITECTURE.md` or `.squad/project.md` is touched. The image keeps its user,
base images, digests and build; a label is metadata only. `.dockerignore` keeps excluding `docs/`, so the
assets do not enter the image.

## Security considerations

- Dockerfile (*Security areas* 13): only an added `LABEL` key with a constant value; no `ARG`, `FROM`,
  `RUN`, `COPY`, `USER` or parser-directive change; no build-argument interpolation in the new value. The
  release workflow's form checks (`ARG` one per line, no continuation on `ARG`, no BOM, no
  `syntax`/`escape` directive, `FROM ${BASE_*_IMAGE}@${BASE_*_DIGEST}`) are unaffected.
- SVG content: no `<script>`, no event attributes, no `href`/external reference, no `<foreignObject>`
  (checked). The favicon's `<style>` is self-contained. When #24 serves them, an SVG loaded as an image
  (`<img>`, `<link rel="icon">`) runs no script; the page's CSP (#24) does not need `style-src
  'unsafe-inline'` for a style inside an image-context SVG. Named for #24 in record 0051.
- No secret, no input parsing, no network.

## Decision records

- `docs/decisions/0051-brand-assets-in-docs-assets-web-ui-and-telegram-with-their-features.md` (Proposed)
  — the SVG set lives in `docs/assets/` as the single source; web UI and Telegram parts of #12 are done
  with #24 and #60; the embedding constraint (no `..` in `go:embed` patterns, allow-list build context)
  is recorded and the way to embed (pinned copies or a root/`docs/assets/` package with build-context
  changes) is left to #24.
- `docs/decisions/0052-image-labels-description-added-no-logo-label.md` (Proposed) — description label
  added; no logo label, because OCI defines none and Docker Hub reads none.

## Challenge

Devil's Advocate, round 1: 0 major, 2 minor.

1. **minor — Dockerfile line numbers wrong** (the `LABEL` block is lines 43–50, line 41 is
   `ARG DATE=unknown`). **Accepted.** Verified with `cat -n deploy/backend/Dockerfile`. The *Problem* section
   now says lines 43–50; the exact edit no longer uses line numbers and anchors only on the quoted
   `title` line and the following `source` line.
2. **minor — "`go:embed` cannot reach `docs/assets/`" overstated.** **Accepted.** The `..` restriction only
   rules out packages under `cmd/` and `internal/`; a package at the module root or inside `docs/assets/`
   could embed the files. The binding obstacle is the image build: `.dockerignore` is an allow-list and
   the Dockerfile copies only `cmd/` and `internal/` (lines 20–21). Changed: the constraint is stated
   precisely in *Problem / root cause*, in the `docs/BRANDING.md` content (section 4, which no longer
   prescribes copies) and in the #24 comment, which now offers both paths. Record 0051 lists the
   root/`docs/assets/` embed package as option 4 and leaves the choice between it and pinned copies to
   #24 instead of requiring copies; its context section is corrected the same way. No change to tier,
   acceptance criteria or the diff of this issue beyond the `docs/BRANDING.md` wording.

## Out of scope / follow-ups

- **Original PNG**: added by the maintainer (issue text). The PR body says so; the first acceptance
  criterion of the issue is complete only with it. Nothing in this change references the PNG.
- **Web UI favicon and header logo** → existing issue #24 (already requires "Logo and favicon from
  `docs/assets/`" and depends on #12). The orchestrator posts this comment on #24 (no new issue):
  > From #12: the logo files are in `docs/assets/` and the color tokens in `docs/BRANDING.md`. Please use
  > `vandox-favicon.svg` as the favicon (it switches colors itself) and `vandox-logo-horizontal.svg` /
  > `vandox-logo-horizontal-dark.svg` in the header per theme, with the `--vandox-color-*` tokens.
  > Note for embedding: a package under `cmd/` or `internal/` cannot embed `docs/assets/` (`go:embed`
  > patterns may not contain `..`), and the image build context is an allow-list (`.dockerignore`) of
  > `go.mod`, `go.sum`, `cmd/`, `internal/` and `LICENSE`, with the Dockerfile copying only `cmd/` and
  > `internal/`. Either keep copies in the web package with a test that compares them byte for byte with
  > `docs/assets/`, or embed from a package at the module root or in `docs/assets/` and extend
  > `.dockerignore` and the Dockerfile accordingly — your call, see record 0051.
- **Telegram profile picture** → existing issue #60. The orchestrator posts this comment on #60:
  > From #12: when the bot exists, render `docs/assets/vandox-icon.svg` to a 512×512 PNG (e.g.
  > `rsvg-convert -w 512 -h 512 docs/assets/vandox-icon.svg -o vandox-icon-512.png`) and set it as the
  > bot's profile picture in BotFather (`/setuserpic`). Telegram crops profile pictures to a circle and the
  > icon's horizontal bar reaches nearly to the edges of its square, so check the crop and add padding if
  > needed (record 0051).
- **Issue #12 itself** stays open after this PR only if the maintainer wants it to track the PNG; the PR
  uses "Closes #12" otherwise — the maintainer's choice at merge time, named in the PR body.
- Not done: `org.opencontainers.image.url`, `.documentation`, `.vendor`, `.authors` (not requested).
- Licensing of the logo: the repository is MIT-licensed (`LICENSE`), so committed logo files fall under
  it unless the maintainer states otherwise; no statement is added here (maintainer's call, not a code
  decision).
