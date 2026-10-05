# 0054: Release artifacts get GitHub build provenance attestations from a separate, secret-free job; no SBOM yet

- **Status:** Proposed
- **Date:** 2026-10-05
- **Source:** Issue #104
- **Supersedes:** — (adds the signing and provenance that record 0037, *Consequences*, left to a new record)

## Context

The release workflow `.github/workflows/release.yml` (records 0037, 0039, 0041, 0053) publishes
`vandox-agent-linux-amd64` and `SHA256SUMS` as GitHub release assets and the image `networlddev/vandox` on
Docker Hub. `SHA256SUMS` shows that a download is intact, but it is published by the same release it
describes, so it does not show who built the files. Issue #104 asks that consumers can verify that the
binary and the image were built by this repository's release workflow, using GitHub artifact attestations
or cosign, optionally with an SBOM, while keeping the release workflow's permissions minimal and its
invariants (no restored cache, no `${{ }}` in `run:`, every action pinned by SHA) intact.

Facts this decision rests on (checked on 2026-10-05):

- The repository is public and owned by a user account. Artifact attestations are available for public
  repositories on every GitHub plan. In a public repository they are signed with a short-lived certificate
  from the Sigstore public-good instance, issued for the workflow's OIDC token, and stored through
  GitHub's attestations API.
- `actions/attest` (GitHub's own action, v4) creates a SLSA v1 build provenance attestation when no SBOM or
  predicate input is given. It needs the job permissions `id-token: write` and `attestations: write`.
  `actions/attest-build-provenance` v4 is only a wrapper around it, and its README tells new users to call
  `actions/attest` directly. `artifact-metadata: write` is only needed for storage records, which are only
  created with `push-to-registry: true` and only for organization-owned repositories.
- `push-to-registry: true` additionally writes the attestation into the image registry. That needs
  registry credentials in the same job as the OIDC permission.
- `gh attestation verify oci://<image>` resolves the image reference to its digest in the registry and, by
  default, fetches the attestation from the GitHub API, not from the registry (`--bundle-from-oci` reads
  the registry instead).
- In `release.yml`, the Docker Hub token is readable only by `publish-image`, which declares the environment
  `release` and has `permissions: {}` (0039). The pushed image's digest is that job's output `digest`.

## Options considered

1. **Signing mechanism**
   - *cosign, keyless*: signs the image in the registry and the binary as a blob with `.sig`/`.pem` or bundle
     release assets. Cons: one more tool to install and pin (a third-party installer action or a downloaded
     binary), more release assets, and image signing needs the registry credentials and the OIDC permission
     in the same job.
   - *SLSA generator reusable workflows* (`slsa-framework/slsa-github-generator`): stronger isolation of the
     signing step. Cons: a third-party reusable workflow; for the image it needs the Docker Hub credentials
     passed into it, which contradicts 0039 (the token never leaves `publish-image`).
   - *GitHub artifact attestations with `actions/attest`*: GitHub's own action, like `upload-artifact` and
     `download-artifact` (0037); verification with `gh attestation verify`, which many users already have.

   Chosen: GitHub artifact attestations with `actions/attest`.
2. **Which job holds `id-token: write` and `attestations: write`**
   - *The `build` job*: the binary is at hand. Cons: the job also runs `govulncheck`, the Go build and
     `docker build`, so code from dependencies and base images runs in the job that can mint a signing
     token; and the image digest does not exist yet in that job.
   - *The `publish-image` job*: the image digest is at hand, and `push-to-registry` would work. Cons: the
     Docker Hub token and the signing permission end up in the same job, so a compromise of one step there
     gets both.
   - *A new job `attest` after `publish-image`*: downloads the verified files, takes the image digest from
     `publish-image`'s output, holds only the two attestation permissions, declares no environment, reads no
     secret, checks nothing out and builds nothing.

   Chosen: the new job `attest`.
3. **Where the image attestation is stored**
   - *GitHub only*: `gh attestation verify oci://…` finds it by digest through the GitHub API.
   - *Also in Docker Hub (`push-to-registry: true`)*: tools that read attestations from the registry
     (`cosign verify-attestation`, Docker Scout) find it too. Cons: needs the Docker Hub credentials in the
     job with the signing permission (option 2), and adds attestation manifests to `networlddev/vandox`.

   Chosen: GitHub only.
4. **SBOM**
   - *Generate one now* (syft as an action or binary, or BuildKit's `--sbom`, which pulls a scanner image
     by tag): adds a third-party tool or an image not pinned by digest to the release pipeline, against the
     pinning rule of security area 13.
   - *Leave it out of this change and track it in a follow-up issue*: the issue marks it optional; the
     provenance attestation is the part that answers "who built this".

   Chosen: leave it out, with a follow-up issue.
5. **Whether the workflow checks its own attestations**
   - *No*: the first real check happens when a consumer runs the documented command.
   - *Yes, with the documented command, before the GitHub release is created*: a wrong digest (for example
     if the digest read from `docker image inspect` is not the one the published tag resolves to), a wrong
     subject or a broken verification command stops the release instead of reaching users.

   Chosen: yes.

## Decision

`release.yml` gets a job `attest` with `needs: [build, publish-image]`, `runs-on: ubuntu-latest`,
`permissions: {id-token: write, attestations: write}`, no `environment`, no checkout and no secret. It:

1. downloads the `release` artifact and runs `sha256sum -c SHA256SUMS` in `dist/`;
2. checks that `publish-image`'s digest output matches `^sha256:[0-9a-f]{64}$` (passed through `env:`);
3. attests `dist/vandox-agent-linux-amd64` with `actions/attest` (pinned by full commit SHA, provenance
   mode: no SBOM or predicate input);
4. attests the image with `actions/attest`, `subject-name: docker.io/networlddev/vandox` and
   `subject-digest` from `publish-image`; `push-to-registry` is not enabled;
5. verifies both with the commands documented in `README.md`: `gh attestation verify` with `--repo`,
   `--signer-workflow <owner>/<repo>/.github/workflows/release.yml`, `--source-ref refs/tags/<tag>` and
   `--deny-self-hosted-runners`, for the binary file and for `oci://docker.io/networlddev/vandox:<version>`.

`github-release` gets `needs: [build, publish-image, attest]`, so no GitHub release exists without verified
attestations. The workflow-level `permissions: {}`, the other jobs' permissions, the trigger (0053), the
cold build (0037) and the Docker Hub token's confinement to `publish-image` (0039) do not change. No SBOM is
published.

## Consequences

- Consumers can check that a binary or an image digest was built by `release.yml` of this repository for a
  given tag on a GitHub-hosted runner, with `gh` logged in to GitHub. `SHA256SUMS` stays for a plain
  integrity check.
- An attestation shows which workflow file at which tag built the artifact. It does not show that the
  tagged commit is on `main`: whoever may create a `v*` tag controls the workflow file that runs, and so
  what gets attested. The tag ruleset `release-tags` stays the boundary (0039).
- The signing token can be minted only in `attest`, which runs only GitHub's own actions and `gh`, no
  repository code, and holds no registry credential.
- The image attestation is not in Docker Hub. Registry-side tools do not see it; adding
  `push-to-registry` later needs a new record, because it puts the token and the signing permission in one
  job.
- If `attest` fails (for example a Sigstore or GitHub API outage), the image is already published but no
  GitHub release exists. "Re-run failed jobs" reruns `attest` and `github-release` while the run's
  artifact exists (7 days, 0037); attesting the same digest again only adds a second attestation. After
  that, the release gets a new patch version, as for any other failure after the push.
- The `attest` job downloads the whole `release` artifact, including the saved image, although it attests
  only the binary from it.
- The attestation path is first exercised by a real tag: `release.yml` runs only on tags (0053), and
  `ci.yml`'s `Release build check` deliberately signs nothing.
- Follow-up: an issue to publish an SBOM for the agent binary and the image with a pinned tool.
- Dependabot's `github-actions` entry keeps the `actions/attest` pin current.
