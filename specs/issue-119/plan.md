# Plan: Publish an SBOM for the release artifacts

Source: Issue #119
Status: Draft
Tier: security — the change edits `.github/workflows/release.yml`, `.github/workflows/ci.yml` and
`.github/scripts/` (security area 13, *Release pipeline and published artifacts*) and brings a third-party
tool into the release pipeline.

## Problem / root cause

Not a bug. The release (`.github/workflows/release.yml`) attests build provenance for
`vandox-agent-linux-amd64` and the image digest in the `attest` job (lines 545–598, record 0054) and publishes
no SBOM. Record 0054, option 4, left the SBOM out because every generator considered there was a
third-party tool or an image not pinned by digest. Issue #119 asks for SBOMs for both artifacts, attested
with `actions/attest` (`sbom-path`) in `attest`.

Claims of the issue, checked:

- "#104 added build provenance attestations (record 0054) and left the optional SBOM out, because every
  option considered there brings a third-party tool or an image not pinned by digest" — **confirmed**
  (0054, option 4 and *Decision*, "No SBOM is published"; *Consequences* names this follow-up).
- "attested with `actions/attest` (`sbom-path`) in the `attest` job" — **confirmed feasible**: the pinned
  `actions/attest@1e69f48acb82d1966a394da916b4c1698aa569d6` (v4.2.2) has the input `sbom-path` (SPDX or
  CycloneDX JSON, ≤ 16 MB, not combinable with `predicate*`); for SPDX it derives the predicate type
  `https://spdx.dev/Document/v<spdxVersion>` (read in `action.yml` and `src/sbom.ts` at that commit).
- "The `attest` job keeps only `id-token: write` and `attestations: write`" — **confirmed** as the current
  state (release.yml lines 550–552); the plan keeps it.
- "Verification is documented (`gh attestation verify --predicate-type …`)" — **confirmed feasible**:
  `gh attestation verify` (2.89) enforces `https://slsa.dev/provenance/v1` by default and has
  `--predicate-type`, `--format json` and `--jq`.

Related defect found on the way: none. (Cosmetic only: `README.md` writes the tag placeholder as
`vX.Y.Z` on line 114 and `v<X.Y.Z>` on line 136; the Dev aligns the new SBOM commands with the existing
style of the block they extend and leaves the old lines alone.)

## Acceptance criteria

- [ ] AC1 — **Generator pin.** `.github/scripts/generate-sbom.sh` holds exactly one generator reference, a
  constant of the form `ghcr.io/anchore/syft:vX.Y.Z@sha256:<64 hex>`: `vX.Y.Z` is the newest syft release at
  implementation time (never `latest`), the digest is that tag's multi-arch **index** digest, read with
  `docker buildx imagetools inspect ghcr.io/anchore/syft:vX.Y.Z` (the `Digest:` line) and found identical for
  `docker.io/anchore/syft:vX.Y.Z`. Before any `docker` call the script checks the constant as a whole string
  against `^ghcr\.io/anchore/syft:v[0-9]+\.[0-9]+\.[0-9]+@sha256:[0-9a-f]{64}$` (`[[ =~ ]]`) and exits 1
  otherwise. No other image, download or installer is used.
- [ ] AC2 — **Isolation.** Each `docker run` of the generator uses `--rm --network none --read-only
  --tmpfs /tmp --cap-drop ALL --security-opt no-new-privileges --user "$(id -u):$(id -g)"`, bind-mounts the
  input file read-only (`:ro`), and has exactly one writable bind mount: a directory freshly created with
  `mktemp -d` that is not inside `dist/`. Environment passed: only `SYFT_CHECK_FOR_APP_UPDATE=false` and, if
  syft needs a writable home or cache, variables pointing into `/tmp` (the tmpfs) — no host path, no token,
  no Docker socket, no cache volume.
- [ ] AC3 — **Output.** The script runs `scan file:<agent>` and `scan docker-archive:<image tar>` with output
  `spdx-json@2.3` and `--source-name` (`vandox-agent-linux-amd64`, `networlddev/vandox`) /
  `--source-version` (the version argument). It copies to `<dest-dir>` only
  `vandox-agent-linux-amd64.spdx.json` and `vandox-image.spdx.json`, each only if it is a regular file and
  not a symlink (`[[ -f … && ! -L … ]]`), and exits 1 if either is missing or of another kind.
- [ ] AC4 — **Content check** (with `jq` on the copied files): `.spdxVersion == "SPDX-2.3"`; each document
  has a package named `stdlib` and a package for the main module `github.com/LarsLaskowski/Vandox`; the image
  document has at least one package with an external reference whose locator starts with `pkg:deb/`; each
  file is at most 16 MiB (`actions/attest` limit). Any failure exits 1 with an `::error::` line. (If syft
  names the main-module package differently, the Dev reports the observed name from the CI dry run to the
  Lead instead of weakening the check.)
- [ ] AC5 — **Arguments.** Usage `generate-sbom.sh <agent-binary> <image-tar> <version> <dest-dir>`; wrong
  argument count, a missing input file or a version not matching
  `^v(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)(-[0-9A-Za-z.-]+)?$` exits 1 before any `docker` call
  (`v0.0.0-dryrun` passes).
- [ ] AC6 — **Release build job.** In `release.yml`, job `build`, a step `Generate SBOMs` between `Save image`
  and `Upload release files` runs the script with `dist/vandox-agent-linux-amd64`, `dist/vandox-image.tar`,
  the tag and `dist`; the tag reaches the script only through `env:`. The `release` artifact then contains
  both SBOM files. `SHA256SUMS` still holds exactly the binary's line (the `Verify agent` check is
  unchanged). The job's `permissions: contents: read` is unchanged.
- [ ] AC7 — **Attest job.** `permissions` stays exactly `id-token: write` and `attestations: write`; still no
  `environment`, no secret, no checkout. Two new steps, both `actions/attest@1e69f48acb82d1966a394da916b4c1698aa569d6 # v4.2.2`:
  `Attest agent SBOM` (`subject-path: dist/vandox-agent-linux-amd64`,
  `sbom-path: dist/vandox-agent-linux-amd64.spdx.json`) and `Attest image SBOM`
  (`subject-name: docker.io/networlddev/vandox`, `subject-digest` from `publish-image`,
  `sbom-path: dist/vandox-image.spdx.json`); neither sets `push-to-registry`. `Verify attestations` additionally
  runs both existing `gh attestation verify` calls with the same flags plus
  `--predicate-type https://spdx.dev/Document/v2.3`. `github-release` keeps `needs: [build, publish-image, attest]`.
- [ ] AC8 — **CI dry run.** In `ci.yml`, job `Release build check`, after `Build and verify image`: save the
  image to a file outside the checkout's tracked paths and run the script with the agent binary, that tar,
  `v0.0.0-dryrun` and a scratch directory. Nothing is uploaded; no secret, no new permission.
- [ ] AC9 — **Workflow invariants.** No `${{ }}` inside any `run:` of either workflow; no `actions/cache`, no
  change to `setup-go` `cache:` settings, no cache backend; every `uses:` still pinned by full commit SHA;
  workflow-level `permissions: {}` in `release.yml` unchanged; trigger unchanged (0053); `publish-image`
  unchanged.
- [ ] AC10 — **Documentation** as listed under *Documentation updates*, consistent with record 0056.

## Verification without tests

The change touches no production or test code (only `.github/workflows/*.yml`, `.github/scripts/*.sh`,
Markdown and `.squad/project.md`), so steps 4 (*Skeleton*), 5 (*Tests first*) and the *Coverage gate* of
step 6 are **not applicable** (`.squad/routing.md`, *Changes without production or test code*). The tier
stays `security`: plan challenge, steps 3 and 8 and every other step run. Step 7 runs *Format check* and the
*Analyzer gate* (Go only; both must stay clean).

| AC | Verified where | By whom |
| -- | -------------- | ------- |
| AC1 | Read-only check of the script; `docker buildx imagetools inspect` of the tag on both registries, digests compared and quoted in the Dev's report; the PR's `Release build check` pulls by that digest | Dev (lookup), Reviewer and Security (diff), orchestrator (CI result) |
| AC2 | Read-only check of every `docker run` line in the diff | Reviewer, Security |
| AC3, AC4 | `Release build check` on the PR runs the script end to end on a real agent binary and image tar; the Dev runs the script once locally if Docker can pull the pinned image (`ghcr.io` blobs may be blocked by the session proxy — then CI is the run) and attaches the `jq` summary (package count, `stdlib` versions, number of `pkg:deb/` packages) to the report; additionally the Dev exercises the failure paths in a scratch copy (wrong form of the constant, symlink in the output dir, missing file) and reports the exit codes | Dev, orchestrator (CI result), Reviewer |
| AC5 | Local run of the script with wrong argument count, missing file and bad version (no Docker needed: these exit before any `docker` call) | Dev (report), Reviewer |
| AC6 | Read-only check of `release.yml`; the step order and artifact path match AC6 | Reviewer, Security |
| AC7 | Read-only check of the `attest` job against AC7 and against `actions/attest` `action.yml` at the pinned SHA. The attestation calls themselves can only run on a real tag (0053, 0054); the first release tag is their first run, and `github-release` does not run if they fail | Reviewer, Security; first tag by the maintainer |
| AC8 | `Release build check` green on the PR, its log shows both SBOM files written and checked | Orchestrator (CI result), Reviewer |
| AC9 | `grep -n '\${{' ` over `run:` blocks of both workflows (none), `grep -n 'cache'`, `grep -n 'uses:'` (all SHA-pinned) | Reviewer, Security |
| AC10 | Read-only check of the docs against record 0056 and the workflow | Reviewer |

## Approach

1. Look up the newest syft release tag and its index digest on `ghcr.io` (and confirm the same digest on
   Docker Hub); write the constant into the new script.
2. Write `.github/scripts/generate-sbom.sh` (bash, `set -euo pipefail`, `LC_ALL=C`, header comment naming
   its callers and record 0056, like the existing scripts): argument checks (AC5), constant check (AC1),
   `mktemp -d` output directory with an `EXIT` trap that removes it, two hardened `docker run` calls (AC2,
   AC3), copy with file-kind checks (AC3), `jq` content checks (AC4). Executable bit set (`git` mode 100755,
   like the existing scripts).
3. `release.yml`: `Generate SBOMs` step in `build` (AC6); two attest steps and two more verify calls in
   `attest` (AC7); update the header comment's record list (`… 0054, 0056`) and the `attest` job comment.
4. `ci.yml`: `docker save` plus the script in `Release build check` (AC8); extend the job comment.
5. Documentation (below).

## Affected projects and types

| Project | Type / file | Change |
| ------- | ----------- | ------ |
| CI | `.github/scripts/generate-sbom.sh` | new: pinned, isolated syft run, copy and content check |
| CI | `.github/workflows/release.yml` | `build`: step `Generate SBOMs`; `attest`: 2 SBOM attest steps, 2 verify calls; comments |
| CI | `.github/workflows/ci.yml` | `Release build check`: save image, run the script |
| Docs | `README.md`, `docs/CONTRIBUTING.md`, `docs/ARCHITECTURE.md`, `.squad/project.md` | see *Documentation updates* |
| Records | `docs/decisions/0056-release-sboms-from-a-digest-pinned-syft-container.md` | new (Proposed), Lead |

## Signatures (for the Dev's skeleton)

None in Go. Script interface (for the Dev, not a skeleton step):

```
.github/scripts/generate-sbom.sh <agent-binary> <image-tar> <version> <dest-dir>
  exit 0: both SBOMs written to <dest-dir> and checked
  exit 1: any argument, pin, generator, file-kind or content error (message as ::error:: on stderr)
  writes: <dest-dir>/vandox-agent-linux-amd64.spdx.json, <dest-dir>/vandox-image.spdx.json
```

Existing files the change must rewrite: `release.yml` (`build`, `attest` jobs, header comment), `ci.yml`
(`release-build` job). No existing file is final as is.

## Test files

None: no production or test code changes (see *Verification without tests*). No existing test calls a
changed signature.

## Documentation updates

Made by the Dev:

- `README.md`, *Install*: after each existing `gh attestation verify` block, the SBOM check and extraction,
  for the binary by file and for the image by digest (never by tag, as 0054 option 6), e.g.
  `gh attestation verify <artifact> <same flags> --predicate-type https://spdx.dev/Document/v2.3 --format json --jq '.[0].verificationResult.statement.predicate' > <name>.spdx.json`
  (the Dev checks the exact `--format`/`--jq` behavior against `gh attestation verify --help`), and one
  sentence that the SBOM is SPDX 2.3 JSON, generated by syft at release time, stored on GitHub with the
  provenance and not published as a release asset.
- `docs/CONTRIBUTING.md`, *What the release workflow does*: step 3/4 mention SBOM generation and its check
  (pinned syft image, no network); step 6 adds the SBOM attestations and their verification; step 7 "only
  after the attestations exist and verify" covers them. *Verifying a release*: the `--predicate-type`
  check. *Release build check on pull requests*: it also generates and checks the SBOMs. New subsection
  *SBOM generator* (after *Base image digests*): where the pin lives, how to refresh it (newest syft release
  tag, `docker buildx imagetools inspect ghcr.io/anchore/syft:vX.Y.Z` `Digest:` line, same digest on Docker
  Hub, never `latest`, in a pull request whose `Release build check` proves it), that Dependabot does not
  update it. *Re-running a failed release*: a re-run of `attest` also adds second SBOM attestations, which is
  harmless. Add 0056 to the record list of *Versioning and releases*.
- `docs/ARCHITECTURE.md`, *Deployment*: the attestation bullet also names SBOM attestations (SPDX, generated
  by a digest-pinned, network-less syft container in the build job); add 0056 to the records line.
  *Security model*: unchanged (no new credential or permission).
- `.squad/project.md`, security area 13 (made untrue otherwise): every published binary and image digest
  also gets an SBOM attestation verified before the GitHub release; the SBOM generator is a container image
  pinned by index digest, run without network, capabilities, token or writable access to `dist/`; add 0056 to
  the record list.
- `SECURITY.md`: none (its scope already names the release pipeline and published artifacts).

## Architecture check

- 0037 (cold build, no restored cache, no `${{ }}` in `run:`, verified image is the pushed image): kept —
  the generator runs after the build, reads copies, writes only a new directory; `docker run` of a
  digest-pinned image is not a restored cache.
- 0039 (Docker Hub token only in `publish-image`): unchanged; no job gains a secret.
- 0041 / area 13 pinning rule: extended to the generator image (index digest, form-checked).
- 0053 (only a tag triggers `release.yml`): unchanged; the PR dry run lives in `ci.yml`.
- 0054 (signing only in the secret-free `attest` job that runs no repository code): kept; `attest` gets only
  more `actions/attest` steps and `gh` calls. 0054's "No SBOM is published" is amended by 0056 (0054 is not
  edited; 0056 names it under *Supersedes*).
- No guarantee from `docs/ARCHITECTURE.md` is weakened.

## Security considerations

- **Trust in the generator.** Pinned by index digest; the daemon verifies content against it. Container
  restrictions (AC2) keep a malicious build of that digest away from the job token, the artifact service,
  `dist/`, the Docker socket and the network. Residual: it can write false SBOM content; the content check
  (AC4) catches only gross failures. Accepted in 0056.
- **Output handling.** The writable mount is a fresh directory outside `dist/`; only two named regular,
  non-symlink files are copied (a symlink planted by the container could otherwise copy a host file into the
  public artifact).
- **Guards and the forms they accept:**
  - *Generator reference* (consumer: Docker's reference parser, which accepts `name`, `name:tag`,
    `name@digest`, `name:tag@digest`, an optional `host[:port]/` prefix, implicit `docker.io/library/`, and
    digests of other algorithms): the guard accepts exactly one form — `ghcr.io/anchore/syft:vMAJOR.MINOR.PATCH@sha256:<64 lowercase hex>` —
    matched as a whole string with `[[ =~ ]]` (bash ERE anchors at string start and end, so an embedded newline
    or trailing text fails). Rejected: no digest, tag only, `latest`, digest only (no readable version),
    another registry or repository, upper-case hex, `sha512:`, extra whitespace. With `tag@digest`, Docker
    uses the digest and ignores the tag.
  - *Version argument*: the release's strict SemVer pattern (same as `Resolve version`), whole-string match;
    it reaches only `--source-version`.
  - *Copied files*: regular file and not a symlink accepted; missing, symlink (to file or directory),
    directory, FIFO, device rejected.
- **Signing job.** No change to its permissions; `sbom-path` reads a file from the downloaded artifact.
- **Supply-chain freshness.** The pin ages until refreshed by hand (follow-up below).

## Decision records

- `docs/decisions/0056-release-sboms-from-a-digest-pinned-syft-container.md` (Proposed) — generator choice
  (syft container by digest over `sbom-action`, BuildKit, Go tool dependency, hand-written, host binary),
  SPDX 2.3, generation in `build` inside the container, publication as attestations only, content check,
  manual pin refresh with follow-up issue; amends 0054.

## Out of scope / follow-ups

Follow-up issue for the orchestrator to create:

- **Title:** `[CI] Report a stale syft image pin in the weekly digest check`
- **Body:** Record 0056 (#119) pins the SBOM generator in `.github/scripts/generate-sbom.sh` as
  `ghcr.io/anchore/syft:vX.Y.Z@sha256:…`. Dependabot cannot read that reference, so it is refreshed by hand
  (`docs/CONTRIBUTING.md`, *SBOM generator*). Extend `.github/workflows/base-image-digests.yml` (record 0055)
  so the weekly run also reports when a newer syft release exists or the pinned tag's index digest moved,
  in the same issue or a sibling issue, with the same restrictions (no token in the lookup step, only
  regex-checked values written). Labels: `area: repo`, `type: chore`, `dependencies`.

Not planned: SBOMs as release assets, SBOM attestations in Docker Hub (`push-to-registry`), CycloneDX in
addition to SPDX (0056, options 2 and 4).
