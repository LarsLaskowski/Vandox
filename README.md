# Vandox

Vandox monitors a Linux server and keeps the evidence needed for forensics.
It consists of two Go binaries that share packages for the data model, log
parsing and signatures.

## Binaries

| Binary | Runs on | Purpose |
|---|---|---|
| `vandox-agent` | the monitored Linux server | collects data and ships it to the backend |
| `vandoxd` | Docker container on any Docker host in the home network (NAS, mini PC, server) | backend with web UI |

Both support `--version`, which prints version, commit and build date.

## Layout

```
cmd/vandox-agent/   entry point of the agent
cmd/vandoxd/        entry point of the backend
internal/           shared packages (data model, log parsing, signatures, ...)
deploy/agent/       deployment files for the agent
deploy/backend/     deployment files for the backend
docs/               documentation
testdata/           fixtures for tests
```

## Build

```
go build ./...
```

Inject version information with `-ldflags`. This is the form the release uses: the full commit SHA, the
commit time in UTC, `-trimpath` and a static binary.

```
go build -trimpath -buildvcs=false -ldflags "\
  -s -w \
  -X github.com/LarsLaskowski/Vandox/internal/version.Version=v0.1.0 \
  -X github.com/LarsLaskowski/Vandox/internal/version.Commit=$(git rev-parse HEAD) \
  -X github.com/LarsLaskowski/Vandox/internal/version.Date=$(TZ=UTC git log -1 --format=%cd --date=format-local:%Y-%m-%dT%H:%M:%SZ)" \
  -o bin/ ./cmd/...
```

Set `CGO_ENABLED=0` for a statically linked binary, as the release does.

## Install

Releases are published from `v<major>.<minor>.<patch>` tags on `main`
(see [`docs/CONTRIBUTING.md`](docs/CONTRIBUTING.md#versioning-and-releases)).

**Agent.** Download `vandox-agent-linux-amd64` and `SHA256SUMS` from the
[GitHub release](https://github.com/LarsLaskowski/Vandox/releases), verify and install the binary (the full
agent installation, with user and systemd unit, is described under `deploy/agent/` once it exists):

```
sha256sum -c SHA256SUMS
sudo install -m 0755 vandox-agent-linux-amd64 /usr/local/bin/vandox-agent
vandox-agent --version
```

**Backend.** Pull the image from Docker Hub, either by version or by the digest given in the release notes:

```
docker pull networlddev/vandox:<X.Y.Z>
docker pull networlddev/vandox@sha256:<digest>
```

Image tags: `X.Y.Z` for the release `vX.Y.Z`; `latest` is the highest stable release; a pre-release
(`vX.Y.Z-rc.N`) is published only under its own tag `X.Y.Z-rc.N`. A published version tag is never
overwritten. The image runs as UID/GID 65532 (non-root) on a distroless static base.
