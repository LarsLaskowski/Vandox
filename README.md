<h1 align="center">
  <picture>
    <source media="(prefers-color-scheme: dark)" srcset="docs/assets/vandox-logo-dark.svg">
    <img src="docs/assets/vandox-logo.svg" alt="Vandox" width="280">
  </picture>
</h1>

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
internal/config/    configuration loading of both binaries, see Configuration below
internal/model/     shared record types and their validation
internal/wire/      versioned batch format, see docs/WIRE_FORMAT.md
internal/           further shared packages (log parsing, signatures, ...)
deploy/agent/       deployment files for the agent
deploy/backend/     deployment files for the backend
docs/               documentation
docs/assets/        logo, icon and favicon, see docs/BRANDING.md
testdata/           fixtures for tests
```

## Configuration

Each binary reads one strict YAML file for its options. The secrets come only from the environment (or
from files named by `*_FILE` variables), never from the file
([0032](docs/decisions/0032-secrets-only-from-environment-or-docker-secrets.md)).

| Binary | File | Commented example |
| ------ | ---- | ----------------- |
| `vandox-agent` | `/etc/vandox/agent.yaml` | [`deploy/agent/agent.yaml`](deploy/agent/agent.yaml) |
| `vandoxd` | `/etc/vandox/vandoxd.yaml` (mounted into the container) | [`deploy/backend/vandoxd.yaml`](deploy/backend/vandoxd.yaml) |

An unknown key, an unknown `VANDOX_` variable, a duplicate key, a second document, an anchor, a custom
tag or an invalid value is an error at start-up. Errors name the file, the line and the key, and never
the value.

**Agent options**

| Key | Environment variable | Default | Description |
| --- | -------------------- | ------- | ----------- |
| `agent_id` | — | required | Name of the server, 1 to 64 characters of `[A-Za-z0-9._-]`, starting with a letter or digit. |
| `backend.url` | — | required | Base URL of the backend ingest endpoint: `http` or `https`, a host and an optional port, no user info, path or query. |
| `spool.directory` | — | `/var/lib/vandox/spool` | Directory of the local spool, an absolute and clean path. |
| `log.level` | — | `info` | `debug`, `info`, `warn` or `error`. |

**Backend options**

| Key | Environment variable | Default | Description |
| --- | -------------------- | ------- | ----------- |
| `web.listen` | — | `:8080` | Address of the web UI as `[host]:port`. |
| `ingest.listen` | — | `:8081` | Address of the ingest endpoint, on a port other than `web.listen`. |
| `storage.directory` | — | `/data` | Directory of the backend's data, an absolute and clean path. |
| `log.level` | — | `info` | `debug`, `info`, `warn` or `error`. |

**Secrets**

| Variable | File variant | Binary | Required | Rule |
| -------- | ------------ | ------ | -------- | ---- |
| `VANDOX_AGENT_TOKEN` | `VANDOX_AGENT_TOKEN_FILE` | agent, backend | agent: yes, backend: no | At least 32 printable ASCII characters without spaces, e.g. `openssl rand -hex 32`. |
| `VANDOX_WEB_PASSWORD_HASH` | `VANDOX_WEB_PASSWORD_HASH_FILE` | backend | no | Printable ASCII without spaces, 1 to 4096 characters. |
| `VANDOX_TELEGRAM_BOT_TOKEN` | `VANDOX_TELEGRAM_BOT_TOKEN_FILE` | backend | no | Printable ASCII without spaces, 1 to 4096 characters. |

Set either the variable or its `_FILE` form, not both. The `_FILE` value is an absolute path to a regular
file (for example a Docker secret) whose content is the secret, with at most one trailing line ending.

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
