# 0041: Base images pinned by digest through build arguments, tag kept alongside; digests refreshed by hand

- **Status:** Accepted
- **Date:** 2026-10-04
- **Source:** Issue #103 / PR #107
- **Supersedes:** —

## Context

Issue #9 requires the backend image `networlddev/vandox` for linux/amd64 on a distroless or similarly minimal
base, running as non-root, with the base image pinned by digest, tagged with the version and `latest`.
Issue #8 requires a Dependabot configuration that covers `docker`, so `.github/dependabot.yml` first pointed
its `docker` ecosystem at `/deploy/backend` (weekly, at most 10 open pull requests, grouped as
`docker-images`) before the Dockerfile existed, accepting a "no Dockerfile found" status until issue #13
added it. The first form of the Dockerfile pinned both base images by writing the tag and the multi-arch index
digest into the same `FROM` line (`FROM golang:1.27-trixie@sha256:… AS build`,
`FROM gcr.io/distroless/static-debian13:nonroot@sha256:…`) and relied on Dependabot to move the digests.
Dependabot ignored `golang` minor and major updates, so the builder stayed on `go.mod`'s minor, and the
release workflow checks that minor.

PR #107 (issue #103) changed the builder line. SonarQube Cloud then failed the quality gate with rule
`docker:S8431` ("use either the version tag or the digest, not both") on that line (now new code,
maintainability D). The runtime line has the same finding as an older issue. Docker ignores the tag when a
digest is present, so a tag next to a digest only documents the image; Sonar treats the pair as
misleading.

The Product Manager decided to follow the pattern the maintainer's other repositories use today, even if
the earlier in-line pinning has to change. `PlexToJellyfinSync/Dockerfile` declares `ARG BASE_RUNTIME_IMAGE`, `ARG BASE_RUNTIME_TAG`
and `ARG BASE_RUNTIME_DIGEST` with defaults before the first `FROM`. It uses
`FROM ${BASE_RUNTIME_IMAGE}@${BASE_RUNTIME_DIGEST}` (digest only), re-declares the three arguments in the
stage, and sets the labels `org.opencontainers.image.base.name="${BASE_RUNTIME_IMAGE}:${BASE_RUNTIME_TAG}"`
and `org.opencontainers.image.base.digest="${BASE_RUNTIME_DIGEST}"`. Its architecture document describes
this as "pinned by digest, with the tag kept alongside for readability". (Its build stage uses a tag-only
`FROM ${BASE_SDK}`. That part is not adopted here, because this record pins the builder too.) `DockerUpdateGuard`
also takes its base images from build arguments.

Dependabot's `docker` file parser (dependabot-core, `docker/lib/dependabot/docker/file_parser.rb`, checked
on 2026-10-04) only recognises a `FROM` line whose image name starts with a lower-case letter or digit,
followed by an optional `:tag` and `@sha256:digest`. It reads neither `ARG` lines nor `${…}` references.
For an ARG-based `FROM`, it finds no dependency at all: it proposes no tag update and no digest update.

## Options considered

Image and release (issues #8, #9; unchanged by the pinning change below):

- **Runtime base:** `gcr.io/distroless/static-debian13:nonroot` (no shell or package manager, CA certificates
  and `/etc/passwd` included, user 65532; needs a static `CGO_ENABLED=0` binary) over `scratch` (no CA bundle
  and no user database) and `alpine` (a shell and a package manager, a larger attack surface).
- **Dockerfile timing:** created with the release pipeline in `deploy/backend/`, not postponed to #13, so the
  pipeline can be verified.
- **Version tag:** `X.Y.Z` without `v` (the Docker Hub convention; `--version` still prints the Git tag with
  `v`). **`latest`** moves only for the highest stable tag (`sort -V`), never for a pre-release or a patch for
  an older line.
- **Same Go toolchain for both binaries:** a multi-stage build whose builder minor is checked against `go.mod`
  by the release workflow, not a single-stage image built on the runner (issue #13 asks for a multi-stage
  Dockerfile that `docker build` alone can rebuild from the tag).
- **Existing version check:** only an explicit not-found (`no such manifest` / `manifest unknown`) from
  `docker manifest inspect` counts as absent and any other error stops the job (fails closed), instead of
  failing only when the inspect succeeds (a rate limit or 5xx would count as "absent").
- **Dependabot `docker` entry before the Dockerfile existed:** added at once for `/deploy/backend` rather than
  with #13 (which would miss #8's acceptance criterion) or for several directories (one reports a missing file
  forever).

Pinning form:

1. **Keep tag and digest in the `FROM` line and silence the rule** (mark the Sonar issues as accepted, or
   add a `sonar.issue.ignore.multicriteria` entry for `docker:S8431`).
   - Pros: Dependabot keeps updating the digests, and the in-line pinning stays as it is.
   - Cons: this is against the Product Manager's decision. It also hides a rule the other repositories
     satisfy.

   Rejected.
2. **Literal digest-only `FROM` (`FROM golang@sha256:… AS build`), with the tag in a comment.**
   - Pros: Sonar is satisfied, and Dependabot still parses the line.
   - Cons: for a digest-only reference, Dependabot has no tag to follow, so it cannot tell which tag the
     digest belongs to. A proposed update would not stay within `1.27-trixie`. The builder minor is no
     longer machine-readable, only from a comment. It also does not follow the other repositories.

   Rejected.
3. **Tag-only `FROM` (`FROM golang:1.27-trixie`), as in PlexToJellyfinSync's build stage.**
   - Pros: Sonar and Dependabot both work.
   - Cons: it drops digest pinning. That is a guarantee of `docs/ARCHITECTURE.md` and of security area 13
     in `.squad/project.md`.

   Rejected.
4. **PlexToJellyfinSync's pattern for both stages**: `BASE_<NAME>_IMAGE`, `BASE_<NAME>_TAG` and
   `BASE_<NAME>_DIGEST` build arguments with defaults, `FROM ${…_IMAGE}@${…_DIGEST}`, and OCI base-image
   labels in the runtime stage. The release workflow checks the build arguments instead of the `FROM`
   text.
   - Pros: the digest pinning stays, and the tag stays visible next to it. The builder minor stays
     machine-checkable. The literal tag-and-digest pair is gone. The published image says in its labels
     which base it was built on. It matches the maintainer's other repositories.
   - Cons: Dependabot can no longer update the digests. The base images can also be overridden with
     `--build-arg`. The release `docker build` passes no `BASE_*` argument, and the release check pins the
     form of every `FROM`.

   Chosen.
5. **What happens to the Dependabot `docker` entry** once option 4 is in place:
   - *Keep it unchanged, as PlexToJellyfinSync does*: it finds nothing to update. Its comment ("digest
     updates of the same tag continue") and its `golang` ignore rule would then be false and dead.
   - *Remove it*: the configuration then says what actually happens. A later literal `FROM` would fail the
     release check anyway.

   Chosen: remove it.
6. **How the digests are refreshed without Dependabot**
   - *By hand, documented in `docs/CONTRIBUTING.md`*: the maintainer checks the digests before every
     release tag and when a Go patch release or a distroless update appears. The digest is read with
     `docker buildx imagetools inspect <image>:<tag>` and written to `BASE_<NAME>_DIGEST`.
   - *Automated* (a scheduled workflow that compares each `BASE_<NAME>_TAG`'s current index digest with
     `BASE_<NAME>_DIGEST`, Renovate with a custom manager, or a release-time `govulncheck -mode=binary` on
     the built `vandoxd`): this is new tooling or a new release gate, beyond fixing a quality-gate finding.

   Chosen: by hand now. Automation is a follow-up issue.
7. **How strict the release pinning check is**
   - *Read only upper-case, unindented `FROM` and `ARG NAME=` lines*: simple, but a lower-case or
     indented `FROM`, or a global default re-declared on a lower-case, indented, multi-name or continued
     `ARG` line, passes unseen (found in the Security plan review). Rejected.
   - *Parse the Dockerfile fully* (case-insensitive instructions, continuations, multi-name `ARG`):
     exact, but a hand-written parser in a workflow step is easy to get wrong. Rejected.
   - *Allow only a fixed set of line forms before the first `FROM`* (a whitelist): closes unknown
     directive forms too, but it is a larger new check for a repository-owned Dockerfile that every pull
     request review already covers. Rejected as out of proportion for a defence-in-depth check.
   - *Reject every form the simple reader cannot see, then read the simple form*: fails closed on
     anything unusual. Chosen. The parser-directive forms rejected are the ones BuildKit reads: `#` and
     `//` comments, indented or not, after an optional byte order mark (found in the Security plan
     review, round 3). Its JSON form needs the whole file to be a JSON object, which a file with
     `FROM` lines cannot be.

## Decision

`deploy/backend/Dockerfile` declares, before the first `FROM` and exactly once each with a default:
`BASE_BUILD_IMAGE="golang"`, `BASE_BUILD_TAG="1.27-trixie"`, `BASE_BUILD_DIGEST="sha256:…"`,
`BASE_RUNTIME_IMAGE="gcr.io/distroless/static-debian13"`, `BASE_RUNTIME_TAG="nonroot"` and
`BASE_RUNTIME_DIGEST="sha256:…"`. The digests are the multi-arch index digests that 0040 pinned.
The stages are `FROM ${BASE_BUILD_IMAGE}@${BASE_BUILD_DIGEST} AS build` and
`FROM ${BASE_RUNTIME_IMAGE}@${BASE_RUNTIME_DIGEST}`. No line pairs a tag with a digest. The runtime stage
re-declares the three `BASE_RUNTIME_*` arguments without defaults. It adds
`org.opencontainers.image.base.name="${BASE_RUNTIME_IMAGE}:${BASE_RUNTIME_TAG}"` and
`org.opencontainers.image.base.digest="${BASE_RUNTIME_DIGEST}"` to its labels.

The release workflow fails in these cases:

- a line starts with `from` or `arg` in any case, with or without leading white space, and is not an
  upper-case `FROM` or `ARG` at the very start of the line;
- an `ARG` line declares more than one argument, or ends with `\` or `` ` `` (continues onto the next
  line);
- the Dockerfile contains a UTF-8 byte order mark, or sets a `syntax` or `escape` parser directive in
  `#` or `//` comment form, indented or not (a `syntax` frontend image would run the build without a pin;
  BuildKit strips a leading byte order mark and accepts both comment forms);
- a `FROM` line is not `FROM ${BASE_<NAME>_IMAGE}@${BASE_<NAME>_DIGEST}` (optionally `AS <stage>`) with the
  same `<NAME>` twice;
- one of `BASE_<NAME>_IMAGE`, `_TAG` or `_DIGEST` is not declared exactly once, with a default, before
  the first `FROM`;
- a `_DIGEST` default is not `sha256:` followed by 64 hex digits;
- the build stage is not `FROM ${BASE_BUILD_IMAGE}@${BASE_BUILD_DIGEST} AS build` with
  `BASE_BUILD_IMAGE` `golang`;
- the `<major>.<minor>` at the start of `BASE_BUILD_TAG` differs from `go.mod`'s `go` directive.

The first three cases exist because the other checks read the Dockerfile with line-anchored, upper-case
patterns. Without them, a lower-case or indented `FROM`, or a global default re-declared on a lower-case,
indented, multi-name or continued `ARG` line, would pass unseen. They are deliberately conservative and
also reject legal but unusual forms, which fails closed. The checks cover the base image of every stage,
not an image named in `COPY --from=` or `RUN --mount=…,from=`; the Dockerfile has none.

The release `docker build` passes only `VERSION`, `COMMIT` and `DATE`.

`.github/dependabot.yml` has no `docker` entry. The base image digests are refreshed by hand, as
`docs/CONTRIBUTING.md` (*Base image digests*) describes. A tag and its digest are always changed together.

Image and release rules, unchanged by the pinning change:

- a multi-stage build from the repository root that copies only `go.mod`, `go.sum`, `cmd/` and
  `internal/`;
- distroless static as the runtime base, with `USER 65532:65532` and `ENTRYPOINT ["/vandoxd"]`;
- the builder's Go minor follows `go.mod`;
- image tags `X.Y.Z` without `v`, and `latest` only for the highest stable tag;
- a pre-release publishes only its own version;
- a published version is never overwritten, and the existence check fails closed (the publish job pushes only
  after `docker manifest inspect` reported an explicit not-found; a Docker Hub outage or rate limit stops it
  before the push and a re-run continues from there).

## Consequences

- SonarQube's `docker:S8431` no longer applies: no `FROM` line names a tag and a digest together. Only the
  CI analysis can confirm this.
- Digest updates are no longer automatic. Between two refreshes, the builder may lag behind the newest Go
  patch, and distroless behind its newest build. The release `govulncheck` scans the source with the
  runner's Go (`setup-go`, newest 1.27.x), not with the builder's. So a standard-library fix that the
  builder lacks is not reported for `vandoxd`. This was already accepted for the in-line pinning for the time between two
  Dependabot pull requests. It now lasts until the next refresh by hand, so `docs/CONTRIBUTING.md` asks for
  a check before every release tag. The follow-up issue automates the refresh.
- Changing the builder minor still changes `go.mod`, `BASE_BUILD_TAG` and `BASE_BUILD_DIGEST` together
  (0040). Nobody checks that a tag and its digest belong together: a tag next to a digest from another tag
  passes the release checks. This was true for the in-line pinning too, because Docker ignored the tag. Whoever refreshes
  a digest reads it for exactly the tag in `BASE_<NAME>_TAG`. An in-build guard (e.g. `RUN go version`
  compared with the builder minor) is left to the follow-up issue.
- Issue #8 asked for Dependabot coverage of `docker`. That coverage has gone: with this Dockerfile it
  covered nothing. A `docker-compose` entry was never part of it; the compose file's images are a separate
  decision (0060).
- The backend must stay buildable with `CGO_ENABLED=0`; a cgo SQLite driver would need a different runtime base.
  Volumes mounted into the container must be writable by UID/GID 65532. Without a shell or `curl` in the image,
  the `HEALTHCHECK` is an exec-form command built into `vandoxd` (0059).
- The two binaries share the Go minor version but may differ in patch version (`setup-go` takes the latest
  patch, the builder moves only with the pinned digest). A Docker Hub outage during a release stops the publish
  job before the push.
- To revisit: if Dependabot learns to resolve `ARG` defaults in `FROM` lines, the `docker` entry can return,
  without the `golang` minor/major ignore if it then updates only the digest.
