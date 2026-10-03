# Vandox

Vandox monitors a Linux server and keeps the evidence needed for forensics.
It consists of two Go binaries that share packages for the data model, log
parsing and signatures.

## Binaries

| Binary | Runs on | Purpose |
|---|---|---|
| `vandox-agent` | the monitored Linux server | collects data and ships it to the backend |
| `vandoxd` | Docker container on a Synology NAS in the home network | backend with web UI |

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

Inject version information with `-ldflags`:

```
go build -ldflags "\
  -X github.com/LarsLaskowski/Vandox/internal/version.Version=v0.1.0 \
  -X github.com/LarsLaskowski/Vandox/internal/version.Commit=$(git rev-parse --short HEAD) \
  -X github.com/LarsLaskowski/Vandox/internal/version.Date=$(date -u +%Y-%m-%dT%H:%M:%SZ)" \
  -o bin/ ./cmd/...
```
