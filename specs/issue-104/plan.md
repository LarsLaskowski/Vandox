# Plan: Sign release artifacts and publish provenance

Source: Issue #104
Status: Draft
Tier: security — the change edits `.github/workflows/release.yml` and grants the OIDC signing permission
in it, which is security area 13 (*Release pipeline and published artifacts*) in `.squad/project.md` and
CI configuration.

## Problem / root cause

Not a bug: a feature gap in the release pipeline. Consumers can check a download against `SHA256SUMS`, but
`SHA256SUMS` comes from the same release it describes, so nothing shows that this repository's release
workflow built the binary or the image.

Claims of the issue, checked against the code:

- *"The release pipeline from #9 publishes `vandox-agent-linux-amd64`, `SHA256SUMS` and the image
  `networlddev/vandox`"* — confirmed: `.github/workflows/release.yml` builds both files (lines 88–97),
  pushes `networlddev/vandox:$VERSION` (and `latest`) in `publish-image` (lines 219–230) and uploads the two
  files with `gh release create` (line 268).
- *"Checksums show integrity but not who built the artifacts"* — confirmed: nothing in the workflow signs
  or attests; record 0037 (*Consequences*) says signing, provenance and SBOM are not part of it and need a
  new record.
- *Security invariants "no cache restore, no `${{ }}` in `run:`, SHA-pinned actions"* — confirmed present:
  `cache: false` (line 79), `docker build --no-cache` (line 139), every `uses:` pinned by full SHA (lines 30,
  76, 166, 181, 242), no `${{ }}` inside any `run:` script; workflow-level `permissions: {}` (line 12),
  `publish-image` has `permissions: {}` and `environment: release` (lines 176–177).
- *Attestations for the image need `id-token: write` + `attestations: write` (+ `push-to-registry`)* (from
  the orchestrator's brief) — partly confirmed, from the source of `actions/attest` v4.2.2: the two
  permissions are needed; `push-to-registry` is optional, and `gh attestation verify oci://…` fetches the
  attestation from the GitHub API by digest unless `--bundle-from-oci` is given. `artifact-metadata: write`
  is only needed for storage records, which are created only with `push-to-registry` and only for
  organization-owned repositories (this one is user-owned). Record 0054, option 3, keeps the attestation on
  GitHub only.
- Artifact attestations are available here: the repository is public (`gh api repos/LarsLaskowski/Vandox`:
  `visibility: public`, owner type `User`), and attestations work in public repositories on every plan.

Related observation (not a defect today, but the reason for the in-workflow verification, AC4):
`publish-image` reads the digest as `.RepoDigests[0]` (line 229). If that ever differed from the digest the
published tag resolves to (e.g. an image index vs. a manifest), the attested digest would not match what
users pull. The verify step resolves the published tag in the registry, so a mismatch fails the release
before the GitHub release exists.

## Acceptance criteria

- [ ] AC1: `release.yml` has a job `attest` with `needs: [build, publish-image]`, `runs-on: ubuntu-latest`,
  `permissions:` exactly `id-token: write` and `attestations: write`, no `environment:`, no
  `actions/checkout`, no `secrets.*` reference.
- [ ] AC2: `attest` downloads the `release` artifact with the same pinned `actions/download-artifact` as the
  other jobs, runs `sha256sum -c SHA256SUMS` in `dist/`, then attests `dist/vandox-agent-linux-amd64` with
  `actions/attest` pinned by full commit SHA with a version comment, in provenance mode (only
  `subject-path`; no `sbom-path`, `predicate*` or `push-to-registry` input).
- [ ] AC3: Before attesting the image, a `run:` step checks that `needs.publish-image.outputs.digest`
  (passed via `env:`) matches `^sha256:[0-9a-f]{64}$` and fails otherwise. The image is then attested with
  the same pinned `actions/attest`, `subject-name: docker.io/networlddev/vandox` (no tag), `subject-digest`
  set to that output, no `push-to-registry`.
- [ ] AC4: A final `run:` step in `attest` verifies both attestations with the flags documented in
  `README.md` (see *Approach*): the binary file and `oci://docker.io/networlddev/vandox:$VERSION` (the
  published **tag**, so a tag that does not resolve to the attested digest fails the release), each with
  `--repo`, `--signer-workflow`, `--source-ref refs/tags/$TAG` and `--deny-self-hosted-runners`; all
  values via `env:`; a failure fails the job.
- [ ] AC5: `github-release` has `needs: [build, publish-image, attest]`; its permissions and steps are
  otherwise unchanged.
- [ ] AC6: Invariants unchanged: workflow-level `permissions: {}`; `build` and `publish-image` unchanged
  (permissions, environment, steps); the trigger stays `push` of `v*.*.*` tags only (0053); no `${{ }}` of
  any kind inside any `run:` script; every `uses:` pinned by a 40-hex commit SHA; no cache of any kind
  added; `ci.yml` unchanged (no attestation in pull-request runs).
- [ ] AC7: Documentation updated as listed under *Documentation updates*: the documented commands use the
  same flags as the AC4 step, apart from placeholders; the documented image check names the image **by
  digest** (`oci://docker.io/networlddev/vandox@sha256:<digest>`, digest from the release notes), never by
  tag, and the README tells the user to pull that same digest after verifying it, with one sentence on why
  (a tag is resolved anew on every request and can be re-pointed by anyone with push rights to the
  repository; the digest names exactly the image that was verified).
- [ ] AC8 (issue acceptance): a release tag produces attestations for the binary and the image, and the
  documented verification command succeeds for the released version. Verified on the first tag run (see
  *Verification without tests*).

## Verification without tests

The change touches no production or test code (only `.github/workflows/release.yml`, Markdown
documentation, `.squad/project.md` and this record). Steps 4 (*Skeleton*), 5 (*Tests first*) and the
*Coverage gate* of step 6 are **not applicable** (*Changes without production or test code*,
`.squad/routing.md`). Step 7 still runs *Format check* and the *Analyzer gate*; steps 3 and 8 run as for
any `security` change.

| AC | Verified where | By whom |
| -- | -------------- | ------- |
| AC1–AC3, AC5 | Read-only check of the diff of `release.yml`: job keys, `needs`, permissions, absence of `environment`/checkout/`secrets.`, `actions/attest` inputs; the pinned SHA re-resolved with `git ls-remote --tags https://github.com/actions/attest` (must equal the tag in the version comment) | Dev (step 6), Reviewer and Security (step 8) |
| AC4 | Read-only check that the step's commands use the README's flags (image subject: tag in the step, digest in the README, per AC4/AC7); `gh attestation verify --help` (local `gh` 2.89.0 lists `--repo`, `--signer-workflow`, `--source-ref`, `--deny-self-hosted-runners`) to confirm every flag exists | Dev (step 6), Reviewer (step 8) |
| AC6 | `git diff origin/main -- .github/workflows/` shows only the new job and the changed `needs` of `github-release`; `grep -n '\${{' .github/workflows/release.yml` reviewed for any hit inside a `run:` block; `grep -n 'uses:'` all 40-hex SHAs; YAML parses (`python3 -c 'import yaml,sys; yaml.safe_load(open(sys.argv[1]))' .github/workflows/release.yml`); `actionlint` if available on the PATH | Code Officer (step 7: YAML parse), Reviewer and Security (step 8) |
| AC7 | Read-only review of the listed files against this plan | Reviewer (step 8) |
| AC8 | Cannot run in the pull request: `release.yml` runs only on a version tag (0053), and `ci.yml` must not sign. On the first tag after the merge, the `attest` job's verify step runs the documented flags against the released binary and the image tag; the maintainer then runs the README commands (image by digest from the release notes) once on a workstation for the released version and checks the run's attestation summary. A failure there is a new issue; the release has no GitHub release in that case (AC5) | Workflow on the tag run; maintainer after the first release (named in the PR's *Next Steps*) |

## Approach

New job in `.github/workflows/release.yml`, between `publish-image` and `github-release` (sketch; names
and comments may be adjusted, the guarantees above may not):

```yaml
  # Signs build provenance for the published binary and image (record 0054). Holds the
  # OIDC signing permission and nothing else: no environment, no secret, no checkout.
  attest:
    needs: [build, publish-image]
    runs-on: ubuntu-latest
    permissions:
      id-token: write
      attestations: write
    steps:
      - uses: actions/download-artifact@3e5f45b2cfb9172054b4087a40e8e0b5a5461e7c # v8.0.1
        with:
          name: release
          path: dist

      - name: Check release files
        run: |
          set -euo pipefail
          cd dist && sha256sum -c SHA256SUMS

      - name: Check image digest
        env:
          DIGEST: ${{ needs.publish-image.outputs.digest }}
        run: |
          set -euo pipefail
          if [[ ! $DIGEST =~ ^sha256:[0-9a-f]{64}$ ]]; then
            echo "::error::publish-image digest '$DIGEST' is not sha256:<64 hex>"
            exit 1
          fi

      - name: Attest agent
        uses: actions/attest@1e69f48acb82d1966a394da916b4c1698aa569d6 # v4.2.2
        with:
          subject-path: dist/vandox-agent-linux-amd64

      - name: Attest image
        uses: actions/attest@1e69f48acb82d1966a394da916b4c1698aa569d6 # v4.2.2
        with:
          subject-name: docker.io/networlddev/vandox
          subject-digest: ${{ needs.publish-image.outputs.digest }}

      - name: Verify attestations
        env:
          GH_TOKEN: ${{ github.token }}
          GH_REPO: ${{ github.repository }}
          TAG: ${{ needs.build.outputs.tag }}
          VERSION: ${{ needs.build.outputs.version }}
        run: |
          set -euo pipefail
          flags=(--repo "$GH_REPO"
            --signer-workflow "$GH_REPO/.github/workflows/release.yml"
            --source-ref "refs/tags/$TAG"
            --deny-self-hosted-runners)
          gh attestation verify dist/vandox-agent-linux-amd64 "${flags[@]}"
          gh attestation verify "oci://docker.io/networlddev/vandox:$VERSION" "${flags[@]}"
```

Notes for the Dev:

- The guard is a bash `[[ =~ ]]` test whose pattern is anchored with `^` and `$` on the whole string, so a
  multi-line value is rejected too (`grep -Eqx` checks line by line and would accept a value with an
  extra line). Accepted forms of `subject-digest` in `actions/attest` (`src/subject.ts`, `parseSubjectDigest`):
  `<alg>:<hex>` with `alg` in sha224/sha256/sha384/sha512/sha512_224/sha512_256 and hex of either case at
  the matching length. The guard accepts only the subset `sha256:` + 64 lower-case hex (what Docker writes
  in `RepoDigests`); upper-case hex, other algorithms, an empty output, a value with a tag or a name
  (`networlddev/vandox@sha256:…`), surrounding whitespace or a newline are all rejected (fail closed).
- `${{ … }}` appears only in `env:` and `with:`, never in `run:`.
- The pin `1e69f48acb82d1966a394da916b4c1698aa569d6` is what the lightweight tag `v4.2.2` of
  `actions/attest` pointed to on 2026-10-05 (`git ls-remote`, and a clone of that tag). Re-check it before
  committing; if a newer v4 release exists, pin that one instead and update the comment.
- Do not add `artifact-metadata: write`, `contents: read`, `push-to-registry` or `create-storage-record`:
  not needed (public, user-owned repository; no registry push).
- `github-release`: change only `needs: [build, publish-image]` to `needs: [build, publish-image, attest]`.
- Update the header comment of `release.yml` (line 6) to name record 0054 as well.

## Affected projects and types

| Project | Type / file | Change |
| ------- | ----------- | ------ |
| CI | `.github/workflows/release.yml` | New job `attest`; `github-release` needs it; header comment names 0054 |
| Docs | `README.md`, `docs/CONTRIBUTING.md`, `docs/ARCHITECTURE.md` | Verification commands and the new release step (see below) |
| Squad project knowledge | `.squad/project.md` | Security area 13 extended (see below) |
| Decision record | `docs/decisions/0054-release-provenance-attestations-from-a-secret-free-job.md` | New, `Proposed` (Lead) |

## Signatures (for the Dev's skeleton)

None: no Go code changes.

## Test files

None: no production or test code changes (see *Verification without tests*). No existing test is
affected.

## Documentation updates

Made by the Dev in step 6:

- `README.md`, *Install*:
  - **Agent**: after `sha256sum -c SHA256SUMS`, add the provenance check, with a sentence that it needs
    the GitHub CLI logged in (`gh auth login`) and proves the binary was built by this repository's release
    workflow for that tag:
    ```
    gh attestation verify vandox-agent-linux-amd64 --repo LarsLaskowski/Vandox \
      --signer-workflow LarsLaskowski/Vandox/.github/workflows/release.yml \
      --source-ref refs/tags/vX.Y.Z --deny-self-hosted-runners
    ```
  - **Backend**: the same for the image, **by digest** (the digest from the release notes,
    `Docker image: networlddev/vandox:<X.Y.Z>@sha256:<digest>`, written by `github-release`), followed by
    pulling that same digest:
    ```
    gh attestation verify oci://docker.io/networlddev/vandox@sha256:<digest> --repo LarsLaskowski/Vandox \
      --signer-workflow LarsLaskowski/Vandox/.github/workflows/release.yml \
      --source-ref refs/tags/v<X.Y.Z> --deny-self-hosted-runners
    docker pull networlddev/vandox@sha256:<digest>
    ```
    with one sentence on why the check names the digest and not the tag: a tag is resolved anew on every
    request and anyone with push rights to `networlddev/vandox` could re-point it between the check and the
    pull, while the digest names exactly the verified image. Plus a sentence that the attestation is
    stored on GitHub, not in Docker Hub (so `--bundle-from-oci` and registry-side tools do not find it).
    The existing `docker pull networlddev/vandox:<X.Y.Z>` line may stay as the unverified convenience
    form, but the verification paragraph must not use the tag.
- `docs/CONTRIBUTING.md` (inside `<!-- project:begin releases -->`):
  - intro paragraph: add record 0054 to the list of records;
  - *What the release workflow does*: a new step between 5 and 6 — the `attest` job creates SLSA build
    provenance attestations for the binary and the image digest (GitHub artifact attestations, stored on
    GitHub, not in Docker Hub), in a job with only `id-token: write` and `attestations: write`, no
    environment and no secret, and verifies them with the README commands; the GitHub release is created
    only after that; step 6 renumbered;
  - a short *Verifying a release* subsection pointing to the README commands (or repeating them, then with
    the image by digest exactly as in the README) and stating that `--source-ref` pins the tag,
    `--signer-workflow` pins `release.yml`, and the image is verified and pulled by digest because a tag
    can be re-pointed (immutable tags are optional, see *One-time setup (maintainer)*, item 3); the workflow's own check uses
    the tag on purpose, to catch a tag that does not resolve to the attested digest;
  - *Re-running a failed release*: if `attest` failed, the image is already published and no GitHub release
    exists; "Re-run failed jobs" reruns `attest` and `github-release` within the 7-day artifact window; a
    second attestation for the same digest is harmless; after that, a new patch version.
- `docs/ARCHITECTURE.md` (inside the project block):
  - *Deployment*: a bullet that the binary and the image digest get SLSA build provenance attestations
    (GitHub artifact attestations) from a separate job that holds only the signing permission and no
    secret, verified in the workflow before the GitHub release; add 0054 to that section's *Records* line;
  - security paragraph (the one ending "…limited to pushing `networlddev/vandox` (0039)"): one sentence that
    the OIDC signing permission exists only in the secret-free `attest` job (0054).
- `.squad/project.md`, security area 13: add to the *Goal* that the OIDC signing permission
  (`id-token: write`, `attestations: write`) is granted only to the `attest` job, which declares no
  environment, reads no secret and runs no repository code; every published binary and image digest gets a
  build provenance attestation that the workflow verifies before the GitHub release is created; add 0054 to
  the record list. (A product PR updates `project.md` when the change makes it incomplete, `.squad/routing.md`,
  *Scope of a product PR*.)
- `SECURITY.md`: none (its scope already covers the release pipeline and published artifacts).

## Architecture check

No guarantee from `docs/ARCHITECTURE.md` or *Guarantees* in `.squad/project.md` is touched. The release
flow gains one job; the existing release guarantees stay: built from tags on `main` only, cold build, the
verified image is the pushed image, a published version is never overwritten, the Docker Hub token only in
`publish-image`. A new guarantee is added (provenance for every published binary and image digest) and
documented in `docs/ARCHITECTURE.md` and security area 13.

## Security considerations

- **Least privilege:** the OIDC token (`id-token: write`) can be minted only in `attest`, which runs only
  GitHub's own actions (`download-artifact`, `attest`) and `gh`, no repository code and no build, has no
  environment and so cannot read `DOCKERHUB_TOKEN`, and no `contents` permission. `publish-image` keeps
  `permissions: {}`; the registry credential and the signing permission never share a job.
- **What an attestation proves:** workflow file `release.yml` of this repository at the given tag, on a
  GitHub-hosted runner. It does not prove the tagged commit is on `main`; the tag ruleset `release-tags`
  stays the boundary (0039). Stated in record 0054 and to be stated in the README/CONTRIBUTING text.
- **Check-to-use gap for the image:** the documented consumer check verifies the image by digest and the
  README pulls that digest, so the verified image is the one run; a re-pointed tag cannot slip in between
  (Challenge, objection 1). The workflow's own check uses the tag to detect a tag/digest mismatch at
  release time.
- **Fail closed:** malformed digest output, failed attestation or failed verification stops the run before
  the GitHub release.
- **Script hygiene:** values reach `run:` only through `env:`; the digest from another job is validated
  before use.
- **New action:** `actions/attest` is GitHub-owned and pinned by SHA; Dependabot's `github-actions` entry
  updates it. No Go dependency changes.
- **Residual:** the attestation path first runs on a real tag (0053 forbids other triggers; CI must not
  sign). A failure then leaves an image on Docker Hub without a GitHub release; recovery as documented.

## Decision records

- `docs/decisions/0054-release-provenance-attestations-from-a-secret-free-job.md` (Proposed) — GitHub
  artifact attestations via `actions/attest` instead of cosign or the SLSA generator; a separate secret-free
  `attest` job instead of `build` or `publish-image`; attestation stored on GitHub only, no
  `push-to-registry`; no SBOM in this change (follow-up issue); in-workflow verification gating the GitHub
  release (by tag); documented consumer check and pull by digest. Lead sets it to `Accepted` and adds it to the index at approval.

## Out of scope / follow-ups

- **Follow-up issue (decided):** title `[CI] Publish an SBOM for the release artifacts`; body: "Issue #104
  added build provenance attestations to the release (record 0054) and left the optional SBOM out, because
  every option considered there brings a third-party tool or an image not pinned by digest into the release
  pipeline. Goal: publish an SBOM (SPDX or CycloneDX) for `vandox-agent-linux-amd64` and the image
  `networlddev/vandox`, attested with `actions/attest` (`sbom-path`) in the `attest` job. Requirements: the
  generator pinned by SHA or digest and checksum-verified, no cache, no `${{ }}` in `run:`, the `attest`
  job keeps only `id-token: write` and `attestations: write`; verification documented (`gh attestation
  verify --predicate-type …`). Depends on #104." Labels: `area: repo`, `type: chore`.
- Pushing the image attestation to Docker Hub (`push-to-registry`): not planned; would need a new record
  (0054, *Consequences*).
- cosign signatures, more platforms: not part of this issue.

## Challenge

Devil's Advocate verdict: OBJECTIONS — 0 major, 1 minor.

1. **(minor) The documented image check verifies a tag, not the image the user then pulls.** — **Accepted,
   plan revised.** Confirmed against the code: the planned README command named
   `oci://docker.io/networlddev/vandox:<X.Y.Z>`, while README lines 117–120 offer `docker pull` by tag; a
   tag is resolved anew per request, `release.yml` only refuses to overwrite a version tag itself, and
   immutable tags are only "optional, recommended" (`docs/CONTRIBUTING.md`, *One-time setup (maintainer)*,
   item 3). The release notes already carry the digest (`release.yml` line 260,
   `networlddev/vandox:$VERSION@$DIGEST`). Changes: AC7 and *Documentation updates* now require the README
   and CONTRIBUTING check to name the image by digest (`oci://docker.io/networlddev/vandox@sha256:<digest>`),
   followed by `docker pull networlddev/vandox@sha256:<digest>`, with one sentence on why; AC4 keeps the
   in-workflow check on the tag and states why (it catches a tag that does not resolve to the attested
   digest); *Security considerations* names the gap and its closure. Record 0054 gains option 6 (*What the
   documented image check names*) with the tag form as the rejected option, and its *Decision* and
   *Consequences* are updated accordingly. Tier unchanged (`security`).
