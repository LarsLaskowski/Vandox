# 0041: Base images pinned by digest through build arguments, tag kept alongside; digests refreshed by hand

- **Status:** Proposed
- **Date:** 2026-10-04
- **Source:** Issue #103 / PR #107
- **Supersedes:** 0036, 0038

## Context

Record 0038 pins both base images of `deploy/backend/Dockerfile` by writing the tag and the multi-arch
index digest into the same `FROM` line (`FROM golang:1.27-trixie@sha256:… AS build`,
`FROM gcr.io/distroless/static-debian13:nonroot@sha256:…`). Record 0036 points Dependabot's `docker`
ecosystem at `/deploy/backend`, and 0038 relies on it to move the digests. Dependabot ignores `golang`
minor and major updates, so the builder stays on `go.mod`'s minor, and the release workflow checks that
minor.

PR #107 (issue #103) changed the builder line. SonarQube Cloud then failed the quality gate with rule
`docker:S8431` ("use either the version tag or the digest, not both") on that line (now new code,
maintainability D). The runtime line has the same finding as an older issue. Docker ignores the tag when a
digest is present, so a tag next to a digest only documents the image; Sonar treats the pair as
misleading.

The Product Manager decided to follow the pattern the maintainer's other repositories use today, even if
0038 has to change. `PlexToJellyfinSync/Dockerfile` declares `ARG BASE_RUNTIME_IMAGE`, `ARG BASE_RUNTIME_TAG`
and `ARG BASE_RUNTIME_DIGEST` with defaults before the first `FROM`. It uses
`FROM ${BASE_RUNTIME_IMAGE}@${BASE_RUNTIME_DIGEST}` (digest only), re-declares the three arguments in the
stage, and sets the labels `org.opencontainers.image.base.name="${BASE_RUNTIME_IMAGE}:${BASE_RUNTIME_TAG}"`
and `org.opencontainers.image.base.digest="${BASE_RUNTIME_DIGEST}"`. Its architecture document describes
this as "pinned by digest, with the tag kept alongside for readability". (Its build stage uses a tag-only
`FROM ${BASE_SDK}`. That part is not adopted here, because 0038 pins the builder too.) `DockerUpdateGuard`
also takes its base images from build arguments.

Dependabot's `docker` file parser (dependabot-core, `docker/lib/dependabot/docker/file_parser.rb`, checked
on 2026-10-04) only recognises a `FROM` line whose image name starts with a lower-case letter or digit,
followed by an optional `:tag` and `@sha256:digest`. It reads neither `ARG` lines nor `${…}` references.
For an ARG-based `FROM`, it finds no dependency at all: it proposes no tag update and no digest update.

## Options considered

1. **Keep tag and digest in the `FROM` line and silence the rule** (mark the Sonar issues as accepted, or
   add a `sonar.issue.ignore.multicriteria` entry for `docker:S8431`).
   - Pros: Dependabot keeps updating the digests, and 0038 stays as it is.
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
5. **What happens to the Dependabot `docker` entry (0036)** once option 4 is in place:
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
   - *Reject every form the simple reader cannot see, then read the simple form*: fails closed on
     anything unusual. Chosen.

## Decision

`deploy/backend/Dockerfile` declares, before the first `FROM` and exactly once each with a default:
`BASE_BUILD_IMAGE="golang"`, `BASE_BUILD_TAG="1.27-trixie"`, `BASE_BUILD_DIGEST="sha256:…"`,
`BASE_RUNTIME_IMAGE="gcr.io/distroless/static-debian13"`, `BASE_RUNTIME_TAG="nonroot"` and
`BASE_RUNTIME_DIGEST="sha256:…"`. The digests are the multi-arch index digests that 0040 and 0038 pinned.
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
- the Dockerfile sets a `syntax` or `escape` parser directive (a `syntax` frontend image would run the
  build without a pin);
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

Carried over from 0038 unchanged, with the reasons given there:

- a multi-stage build from the repository root that copies only `go.mod`, `go.sum`, `cmd/` and
  `internal/`;
- distroless static as the runtime base, with `USER 65532:65532` and `ENTRYPOINT ["/vandoxd"]`;
- the builder's Go minor follows `go.mod`;
- image tags `X.Y.Z` without `v`, and `latest` only for the highest stable tag;
- a pre-release publishes only its own version;
- a published version is never overwritten, and the existence check fails closed.

## Consequences

- SonarQube's `docker:S8431` no longer applies: no `FROM` line names a tag and a digest together. Only the
  CI analysis can confirm this.
- Digest updates are no longer automatic. Between two refreshes, the builder may lag behind the newest Go
  patch, and distroless behind its newest build. The release `govulncheck` scans the source with the
  runner's Go (`setup-go`, newest 1.27.x), not with the builder's. So a standard-library fix that the
  builder lacks is not reported for `vandoxd`. This was already accepted in 0038 for the time between two
  Dependabot pull requests. It now lasts until the next refresh by hand, so `docs/CONTRIBUTING.md` asks for
  a check before every release tag. The follow-up issue automates the refresh.
- Changing the builder minor still changes `go.mod`, `BASE_BUILD_TAG` and `BASE_BUILD_DIGEST` together
  (0040). Nobody checks that a tag and its digest belong together: a tag next to a digest from another tag
  passes the release checks. This was true in 0038 too, because Docker ignored the tag. Whoever refreshes
  a digest reads it for exactly the tag in `BASE_<NAME>_TAG`. An in-build guard (e.g. `RUN go version`
  compared with the builder minor) is left to the follow-up issue.
- Issue #8 asked for Dependabot coverage of `docker`. That coverage has gone: with this Dockerfile it
  covered nothing.
- If the backend gains a compose file (#13), its images are a separate decision (0036 left that to #13).
- To revisit: if Dependabot learns to resolve `ARG` defaults in `FROM` lines, the `docker` entry can return,
  without the `golang` minor/major ignore if it then updates only the digest.
