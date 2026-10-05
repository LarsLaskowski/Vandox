# Plan: Backend skeleton and container

Source: Issue #13 | [spec.md](spec.md)
Status: Draft
Tier: security — the change adds a dependency (`modernc.org/sqlite`), changes `deploy/backend/Dockerfile`
and `ci.yml`, and touches security areas 2 (port binding in `deploy/backend/`), 8 (secrets in the compose
file), 9 (database file created under `storage.directory`), 10 (configuration and CLI input), 11 (the
health-check HTTP call), 12 (log output) and 13 (Dockerfile, `.github/scripts/`).

## Problem / root cause

Feature, summarised from `spec.md`: turn `vandoxd` from a version-printing stub into a runnable service
(configuration, `slog`, SQLite, web and ingest listeners, `/healthz`, graceful shutdown) and ship it as a
container with a health check and a compose file for a NAS.

Claims of the issue, checked against the code (`main` at 938de24):

- *"configuration loading from the shared configuration package"* — confirmed: `internal/config.LoadBackend`
  (`internal/config/backend.go:50`) exists with `web.listen`, `ingest.listen`, `storage.directory`,
  `log.level` and three optional secrets; no binary calls it yet (`cmd/vandoxd/main.go:20` calls only
  `cli.Run`).
- *"Separate listeners ... (ports configurable)"* — the configuration part is already there:
  `web.listen` `:8080`, `ingest.listen` `:8081`, different ports enforced (`backend.go:91`). Nothing listens.
- *"/healthz returning 200 when the database is reachable"* — no database exists yet. The architecture
  prescribes SQLite with FTS5 in WAL mode, no external database (record 0007, `docs/ARCHITECTURE.md`
  *Storage and retention*). `go.mod` has no SQLite driver, so this feature adds one (record 0057).
- *"Dockerfile: multi-stage build, minimal non-root runtime image, pinned base image"* — already true:
  `deploy/backend/Dockerfile` exists (records 0038 → 0041, 0055): multi-stage, distroless static `nonroot`,
  `USER 65532:65532`, both bases pinned by digest through build arguments. Missing: `HEALTHCHECK`, a
  `/data` directory, `EXPOSE`.
- Issue comment *"The Dependabot `docker` entry (PR #102) watches /deploy/backend (decision 0036). Place the
  Dockerfile there, or move the directory"* — refuted / outdated: 0036 is `Superseded by 0041`, and
  `.github/dependabot.yml` has no `docker` entry any more (0041, option 5: Dependabot cannot read the
  `ARG`-based `FROM` lines). The Dockerfile already is in `deploy/backend/`. 0036 left a `docker-compose`
  Dependabot entry to #13; record 0060 decides against it.
- *"deploy/backend/docker-compose.yml for Container Manager"* — confirmed missing; `deploy/backend/` holds
  `Dockerfile` and `vandoxd.yaml`.

Related defect found on the way: the runtime image has no `/data` directory. A named volume mounted on a
path that does not exist in the image is created owned by root, so the non-root UID 65532 could not create
the database there (`storage.directory` defaults to `/data`, `internal/config/backend.go:41`). Nothing
writes there yet, so it is latent; this plan fixes it (Dockerfile ships `/data` owned by 65532, 0700, and
the smoke test proves a fresh named volume is writable).

Also stale and updated by this change: `docs/ARCHITECTURE.md` says "The binaries do not call it yet" about
`internal/config`; record 0034 and `docs/UNIT_TESTS.md` fix `vandoxd`'s `run(args, stdout, stderr)`
signature and "no arguments print the usage", which 0034 itself left "to the first feature that gives a
binary a real default action" — this one (record 0058 supersedes 0034).

## Acceptance criteria

Numbering: AC-R* `cmd/vandoxd` (run, serve, health check, logger), AC-S* server package, AC-D* store
package, AC-C* container and compose (verified without unit tests, see *Verification of container
criteria*). Every AC-R/S/D criterion gets at least one unit test.

**Command line (`cmd/vandoxd/main.go`)**

- [ ] AC-R1: `run(ctx, []string{"-version"}, …)` prints exactly `version.String("vandoxd") + "\n"` to
  stdout, nothing to stderr, exit 0 (existing `TestRun_Version`, call site adapted). `--version` likewise.
  `-version` wins over every other flag (`-version -healthcheck` prints the version, exit 0, no listener,
  no file read).
- [ ] AC-R2: `-h`, `-help` and `--help` print a usage that starts with `Usage of vandoxd:` and lists
  `-config`, `-healthcheck` and `-version` to stderr, exit 0, without reading a file or listening.
- [ ] AC-R3: an undefined flag (`-bogus`) prints the flag error and the usage to stderr, exit 2 (existing
  `TestRun_UndefinedFlag`, call site adapted). A positional argument (`extra`) prints
  `vandoxd: unexpected argument` and the usage to stderr, exit 2. Neither listens nor reads a file.
- [ ] AC-R4: when writing the version line fails (failing writer), exit 1.

**Service (`cmd/vandoxd/serve.go`)**

- [ ] AC-R5: without `-version`/`-healthcheck`, `run` loads the configuration from `-config` (default
  `config.DefaultBackendFile`) with the given `environ`, opens the store in `storage.directory`, calls the
  injected `listen` exactly twice — with `("tcp", web.listen)` and `("tcp", ingest.listen)` from the file —
  and serves: `GET /healthz` on the web listener returns 200 with body `ok\n`; `GET /healthz` on the
  ingest listener returns 404.
- [ ] AC-R6: cancelling `ctx` while serving makes `run` return 0; stderr then holds JSON log lines (each
  line parses as a JSON object) including one with `"msg":"vandoxd stopped"`; the database file
  `<storage.directory>/vandox.db` exists afterwards.
- [ ] AC-R7: a real `SIGTERM` sent to the test process (`syscall.Kill(os.Getpid(), syscall.SIGTERM)`) while
  `run` serves (sent only after `/healthz` answered 200) makes `run` return 0. Not `t.Parallel`.
- [ ] AC-R8: a second `run` on the same storage directory reopens the database: its log has
  `"msg":"database opened"` with `"created":false`, the first run's `"created":true`.
- [ ] AC-R9: start-up failures return 1 and log one JSON error line, without listening: configuration file
  missing; configuration invalid (e.g. `log.level: verbose`); an unknown `VANDOX_` variable in `environ`;
  storage directory missing; `listen` failing for the web address; `listen` failing for the ingest address
  (the web listener opened before is closed: the fake records `Close`).
- [ ] AC-R10: the secret is never logged: with `VANDOX_AGENT_TOKEN=<64-character sentinel>` in `environ`, a
  full start/stop cycle's stderr does not contain the sentinel (sentinel not in a subtest name, see
  `docs/UNIT_TESTS.md`).
- [ ] AC-R11: the start log line (`"msg":"vandoxd starting"`) carries `version`, `commit`, `config` (the
  path), `web`, `ingest` and `storage` attributes; once the configuration is loaded, log lines below the
  configured level are not written (with `log.level: warn`, a full start/stop cycle writes no line with
  `"level":"INFO"` to stderr).

**Logger (`cmd/vandoxd/logger.go`)**

- [ ] AC-R12: `newLogger(w, level)` returns a JSON `slog` logger writing to `w` at `debug`, `info`, `warn`,
  `error` (table test: a record one level below is dropped, one at the level is written); an unknown level
  returns an error.

**Health check (`cmd/vandoxd/healthcheck.go`)**

- [ ] AC-R13: `run(ctx, []string{"-healthcheck", "-config", f}, …)` against an `httptest` server whose
  address is written into `web.listen` of `f`: status 200 → exit 0 and empty stdout/stderr; status 503 →
  exit 1 and stderr `vandoxd: health check failed: unexpected status 503\n`; a 302 redirect → exit 1 (not
  followed: the redirect target handler records no request); configuration invalid → exit 1 with the
  configuration error on stderr. The injected `listen` is never called and no database file is created.
- [ ] AC-R14: `healthURL(listen)` (table test) maps `:8080` → `http://127.0.0.1:8080/healthz`,
  `0.0.0.0:8080` → `http://127.0.0.1:8080/healthz`, `[::]:8080` → `http://[::1]:8080/healthz`,
  `[::ffff:0.0.0.0]:8080` → `http://127.0.0.1:8080/healthz`, `127.0.0.1:9000` → `http://127.0.0.1:9000/healthz`,
  `192.0.2.10:8080` → `http://192.0.2.10:8080/healthz`, `[::1]:8080` → `http://[::1]:8080/healthz`,
  `[fe80::1%eth0]:8080` → `http://[fe80::1%25eth0]:8080/healthz`; a value without a port returns an error.
- [ ] AC-R15: `newHealthClient()` has `Timeout` 4 s, a transport whose `Proxy` is nil, and does not follow
  redirects (`CheckRedirect` returns `http.ErrUseLastResponse`).

**Server package (`cmd/vandoxd/internal/server`)**

- [ ] AC-S1: `NewWebHandler`: `GET /healthz` with a pinger returning nil → 200, body `ok\n`,
  `Content-Type: text/plain; charset=utf-8`, `Cache-Control: no-store`, `X-Content-Type-Options: nosniff`;
  `HEAD /healthz` → 200, empty body; pinger error → 503, body `unavailable\n` (the error text, e.g. a path,
  is not in the body) and one log record at `WARN` with the error as attribute `error`; `POST /healthz` →
  405; `/`, `/healthz/`, `/other` → 404.
- [ ] AC-S2: a pinger that blocks until released: the request returns 503 after `pingTimeout`; while that
  ping is still blocked, further concurrent requests start no second `Ping` (call count stays 1) and also
  answer 503 after their timeout; after the pinger is released with nil, the next request starts a new
  `Ping` and answers 200. The pinger receives a context with a deadline.
- [ ] AC-S3: `NewIngestHandler` answers 404 to `GET /`, `GET /healthz` and `POST /v1/batches`.
- [ ] AC-S4: `newHTTPServer` sets `ReadHeaderTimeout` 5 s, `ReadTimeout` 30 s, `WriteTimeout` 30 s,
  `IdleTimeout` 120 s, `MaxHeaderBytes` 16 KiB and a non-nil `ErrorLog`.
- [ ] AC-S5: `Run` serves both listeners (`/healthz` 200 on web, 404 on ingest); cancelling `ctx` returns
  nil and both listeners are closed (a new dial fails).
- [ ] AC-S6: graceful shutdown: a `/healthz` request in flight (pinger blocked, `PingTimeout` 1 min) when
  `ctx` is cancelled keeps `Run` waiting; releasing the pinger lets the request complete with 200 and `Run`
  return nil.
- [ ] AC-S7: shutdown deadline: same setup with `ShutdownTimeout` 50 ms and the pinger never released
  before `Run` returns: `Run` returns an error for which `errors.Is(err, context.DeadlineExceeded)` holds,
  and the client's request fails (connection closed). The pinger is released in `t.Cleanup`.
- [ ] AC-S8: when one listener fails (a fake `net.Listener` whose `Accept` returns a non-temporary error),
  `Run` shuts the other down and returns an error wrapping that `Accept` error.
- [ ] AC-S9: `Run` returns an error without serving when `Web`, `Ingest`, `DB` or `Logger` is nil or
  `PingTimeout`/`ShutdownTimeout` is not positive (table test).
- [ ] AC-S10: `Run` with an already cancelled `ctx` returns nil.

**Store package (`cmd/vandoxd/internal/store`)**

- [ ] AC-D1: `Open` in an empty `t.TempDir()` creates `vandox.db` with permissions `0600` (mode & 0o777,
  umask 022 in the test or checked as "no group/other bits"), `Created()` is true, `PRAGMA journal_mode`
  is `wal`, and `meta` holds `schema_version` = `1`.
- [ ] AC-D2: data survives a restart: after `Open`, an insert through the store's handle (white-box), `Close`
  and a second `Open` of the same directory, the row is still there and `Created()` is false.
- [ ] AC-D3: `Ping` returns nil on an open store, and an error after `Close`; `Ping` with a cancelled
  context returns an error.
- [ ] AC-D4: `Open` fails (error, no panic, no file created) when the directory does not exist, when it is a
  regular file, and when `vandox.db` is a directory; it fails when `vandox.db` exists with non-SQLite
  content (e.g. 4 KiB of `x`), and when `meta.schema_version` is `2` (a database of a newer version is
  refused, not modified: the value is still `2` afterwards).
- [ ] AC-D5: a storage directory whose name contains `?`, `#`, `%20` and a space opens, and the database
  file is created inside that directory (not in a truncated path).
- [ ] AC-D6: the driver supports FTS5: `CREATE VIRTUAL TABLE t USING fts5(body)`, an insert and a
  `MATCH` query succeed on an open store (white-box) — pins 0007's "the SQLite driver must support FTS5".

**Container and compose** (verified as described in *Verification of container criteria*)

- [ ] AC-C1: `deploy/backend/Dockerfile` keeps the 0041 pattern (the pinning check passes unchanged), adds
  `EXPOSE 8080 8081`, ships `/data` (owned `65532:65532`, mode `0700`) and
  `HEALTHCHECK --interval=30s --timeout=5s --start-period=30s --start-interval=2s --retries=3 CMD ["/vandoxd", "-healthcheck"]`;
  `docker run --rm vandox:local --version` still prints the version line.
- [ ] AC-C2: `deploy/backend/docker-compose.yml` has the content listed under *Approach / Compose file*:
  data volume, read-only import mount, read-only configuration file, the agent-token secret, `mem_limit`,
  restart policy, `stop_grace_period`, read-only root file system, port bindings.
- [ ] AC-C3: started from the compose file on a Docker host, the container becomes `healthy`, `/healthz` on
  the published web port answers 200, the published ingest port answers 404 to `/healthz`.
- [ ] AC-C4: `docker compose stop` ends the container with exit code 0 and the log line `vandoxd stopped`;
  after `docker compose start` it becomes `healthy` again and logs `"created":false`.
- [ ] AC-C5: the running container has `HostConfig.Memory` 536870912 (512 MiB), restart policy
  `unless-stopped`, a read-only root file system and user `65532:65532`.

## Verification of container criteria

Code changes, so steps 4–6 and the *Coverage gate* apply. AC-C1–AC-C5 concern the Dockerfile, the compose
file and CI, which a Go unit test cannot exercise; they are verified as follows:

| AC | Where | By whom |
| -- | ----- | ------- |
| AC-C1 | `ci.yml` `Release build check`: *Check base image pinning* (unchanged script) and *Build and verify image* (`--version`, user); the new smoke test for the health check and `/data` | CI on the PR; Dev builds the image locally once (`docker build -f deploy/backend/Dockerfile .`) if Docker is available, else CI |
| AC-C2 | Read-only review of the file against *Approach / Compose file*; `docker compose config --quiet` inside the smoke test | Reviewer, Security; CI |
| AC-C3, AC-C4, AC-C5 | New step *Smoke test backend container* in `ci.yml` (`Release build check` job, after *Build and verify image*) running `.github/scripts/smoke-test-backend.sh` | CI on the PR; the orchestrator links the green run in `log.md` before step 9 |

## Approach

### Dependency

`modernc.org/sqlite` (v1.60.1 checked locally on 2026-10-05: builds with `CGO_ENABLED=0`, `PRAGMA
journal_mode` returns `wal`, FTS5 virtual tables work, SQLite 3.53.4). Added with `go get` and
`go mod tidy`; its transitive modules (`modernc.org/libc`, `mathutil`, `memory`, `dustin/go-humanize`,
`google/uuid`, `mattn/go-isatty`, `ncruces/go-strftime`, `remyoudompheng/bigfft`) arrive as indirect
requirements. `go tool govulncheck ./...` must stay clean (Dev runs it with the current Go patch, see
*Known pitfalls* in `.squad/stack.md`). Record 0057.

### Package layout

Backend-only code goes into `cmd/vandoxd/internal/server` and `cmd/vandoxd/internal/store`; `internal/`
stays "packages shared by both binaries" (record 0061). `cmd/vandoxd` (package `main`) holds the flag
handling, the service wiring, the logger and the health-check client.

### `cmd/vandoxd`

- `main.go`: `main()` is one statement (0034 → 0058):
  `os.Exit(run(context.Background(), os.Args[1:], os.Environ(), os.Stdout, os.Stderr, (&net.ListenConfig{}).Listen))`.
  `run` parses its own `flag.FlagSet` (`ContinueOnError`, output to stderr, usage header
  `Usage of vandoxd:`) with `-config` (default `config.DefaultBackendFile`), `-healthcheck` and `-version`.
  Order: parse error → 2; `-version` → print (1 on write error) → 0; positional argument → 2; `-healthcheck`
  → `healthcheck`; otherwise `serve`. `vandoxd` no longer calls `internal/cli`; `internal/cli` stays for
  `vandox-agent` (its package comment is corrected).
- `serve.go`: registers `signal.NotifyContext(ctx, syscall.SIGTERM, os.Interrupt)` first, and
  `context.AfterFunc(sigCtx, stop)` so that after the first signal the default handling is restored and a
  second signal terminates at once. Bootstrap logger `newLogger(stderr, "info")`; `config.LoadBackend` →
  on error log `"configuration invalid"` with attribute `error` and return 1; logger at `cfg.Log.Level`;
  log `"vandoxd starting"` (`version`, `commit`, `config`, `web`, `ingest`, `storage`; never a secret);
  `store.Open(ctx, cfg.Storage.Directory)` → log `"database opened"` (`path`, `created`); `listen` web, then
  ingest (on ingest failure close the web listener); `server.Run` with `PingTimeout` 2 s and
  `ShutdownTimeout` 10 s; then close the store; log `"vandoxd stopped"`; exit 0, or 1 when `server.Run` or
  `Close` returned an error (logged at `ERROR`). The constants are unexported in `serve.go`.
- `logger.go`: `slog.NewJSONHandler(w, &slog.HandlerOptions{Level: lvl})`, level parsed with
  `slog.Level.UnmarshalText` after checking the value is one of the four names `internal/config` accepts.
  JSON is chosen because it escapes control characters and is machine-readable in `docker logs` (record
  0058).
- `healthcheck.go`: loads the configuration like `serve` (same `-config`, same `environ`), builds the URL with
  `healthURL(cfg.Web.Listen)`, `probe`s it with `newHealthClient()` and a 4 s context; prints
  `vandoxd: health check failed: <reason>` to stderr on failure. No logger, no store, no listener.

### `cmd/vandoxd/internal/server`

- `health.go`: `Pinger`, `NewWebHandler`. Routes on an `http.ServeMux` with the pattern `GET /healthz`
  (Go's mux then also answers `HEAD` and replies 405 to other methods). The handler waits for the shared
  in-flight ping (single-flight: at most one `Ping` runs at a time; a request arriving while one runs waits
  for that result) or for its own `pingTimeout`, whichever is first; a ping that never returns is
  abandoned, never stacked (same idea as record 0029 on the agent). A failed or timed-out ping logs at
  `WARN` with `slog.Any("error", err)` (never concatenated into the message) and answers 503.
- `server.go`: `Options`, `Run`, `NewIngestHandler` (an empty `ServeMux`: 404 for everything),
  `newHTTPServer` (timeouts of AC-S4, `ErrorLog: slog.NewLogLogger(logger.Handler(), slog.LevelWarn)`).
  `Run` serves both listeners in goroutines, waits for `ctx.Done()` or the first serve error
  (`http.ErrServerClosed` is not an error), then calls `Shutdown` on both with one context of
  `ShutdownTimeout`; on deadline it calls `Close` on both and returns an error wrapping
  `context.DeadlineExceeded`; it returns the serve error (wrapped) if a listener failed, else nil. It logs
  `"listening"` (`listener`: `web`/`ingest`, `address`) at start and `"shutting down"` when `ctx` is done.

### `cmd/vandoxd/internal/store`

`Open(ctx, dir)`: `os.Stat(dir)` must be a directory (not created: the volume or the operator provides it,
and creating it would hide a missing mount); `path = filepath.Join(dir, FileName)`; if absent, create it
with `os.OpenFile(path, O_RDWR|O_CREATE|O_EXCL, 0o600)` and close it (`created = true`) — SQLite gives the
`-wal` and `-shm` files the main file's mode (checked locally: all three `0600`); if present it must be a
regular file. DSN: `(&url.URL{Scheme: "file", Path: path, RawQuery: "_pragma=journal_mode(WAL)&_pragma=busy_timeout(5000)&_pragma=foreign_keys(1)"}).String()`
with driver name `sqlite` (escaping checked locally: a directory `we?ird#d%20ir` resolves to exactly that
path). Then verify `PRAGMA journal_mode` = `wal` (else error: WAL needs shared memory, which some network
file systems lack), `CREATE TABLE IF NOT EXISTS meta (key TEXT PRIMARY KEY, value TEXT NOT NULL) STRICT`,
`INSERT OR IGNORE INTO meta(key, value) VALUES ('schema_version', '1')`, read it back and refuse any value
other than `1` (no migration framework yet; a newer database is never modified). On any error after
`sql.Open` the handle is closed. `Ping(ctx)` reads `schema_version` with `QueryRowContext`. Errors are
wrapped as `store: <step>: %w`.

### Dockerfile (`deploy/backend/Dockerfile`)

- Build stage: `RUN mkdir -p -m 0700 /out/data` (anywhere after `WORKDIR`).
- Runtime stage: `COPY --from=build --chown=65532:65532 /out/data /data`; `EXPOSE 8080 8081`;
  `STOPSIGNAL SIGTERM`; the `HEALTHCHECK` line of AC-C1; `USER` and `ENTRYPOINT` unchanged; no `CMD`
  (no arguments = serve).
- No `FROM`/`ARG` change; header comment extended by one line about the health check. No configuration
  file is baked into the image (record 0059): the file is mounted.

### Compose file (`deploy/backend/docker-compose.yml`)

Commented for Synology Container Manager / QNAP / `docker compose` (record 0060):

- `name: vandox`; service `vandoxd`, `image: networlddev/vandox:latest` (comment: pin `X.Y.Z` or a digest
  for controlled upgrades), `container_name: vandoxd`, `restart: unless-stopped`.
- `mem_limit: 512m`; `environment: GOMEMLIMIT: 400MiB` (the Go runtime collects before the cgroup limit is
  hit) and `VANDOX_AGENT_TOKEN_FILE: /run/secrets/vandox_agent_token`.
- `stop_grace_period: 30s` (longer than the 10 s shutdown deadline, so Docker's SIGKILL never cuts it).
- `ports`: `"${WEB_BIND_ADDRESS:-127.0.0.1}:8080:8080"` (web/health port on loopback for the reverse proxy,
  0023; override for a proxy on another host) and
  `"${TAILNET_ADDRESS:?set TAILNET_ADDRESS to the backend host's tailnet IP address}:8081:8081"` (ingest
  only on the tailnet address, 0017; unset or empty stops `docker compose` before anything starts).
- `volumes`: `vandox-data:/data` (named volume; Docker copies the image's `/data` ownership into a new
  volume), `./import:/import:ro`, `./vandoxd.yaml:/etc/vandox/vandoxd.yaml:ro`.
- `secrets: [vandox_agent_token]`, top-level `secrets.vandox_agent_token.file: ./secrets/vandox_agent_token`;
  the web password hash and Telegram token as commented-out entries for #25 / #60.
- Hardening: `read_only: true`, `tmpfs: [/tmp]` (SQLite's temporary files), `cap_drop: [ALL]`,
  `security_opt: ["no-new-privileges:true"]`, `logging` with the `json-file` driver, `max-size: 10m`,
  `max-file: "3"`.
- Header comment: the files the operator creates (`vandoxd.yaml` from the example, `secrets/` readable by
  UID 65532, `import/`, `.env` with `TAILNET_ADDRESS`), that the container ports must match `web.listen`
  and `ingest.listen`, and that a bind-mounted data directory must be owned by 65532.
- No Dependabot `docker-compose` entry (record 0060).

`.gitignore` gains `deploy/backend/secrets/` and `deploy/backend/.env` so an operator working in a clone
does not commit them.

### CI smoke test

`.github/scripts/smoke-test-backend.sh <image>` (bash, `set -euo pipefail`), called from a new step
*Smoke test backend container* in `ci.yml`, job `release-build`, right after *Build and verify image*,
with `vandox:local`. It copies `deploy/backend/docker-compose.yml` and `deploy/backend/vandoxd.yaml` into a
`mktemp -d` project directory, creates `secrets/vandox_agent_token` (`openssl rand -hex 32`, mode `0644` so
UID 65532 can read it — CI only), `import/`, and an override file setting `image: <image>` and
`pull_policy: never`; runs with `TAILNET_ADDRESS=127.0.0.1` and `WEB_BIND_ADDRESS=127.0.0.1` and a unique
project name; `trap` runs `docker compose down -v` on exit. Checks, each failing with `::error::`:
`docker compose config --quiet`; `up -d`; health `healthy` within 60 s; `curl -fsS
http://127.0.0.1:8080/healthz` is `ok`; `curl` on `127.0.0.1:8081/healthz` is 404; `docker inspect`
memory 536870912, restart policy `unless-stopped`, `ReadonlyRootfs` true, `Config.User` `65532:65532`;
`docker compose stop` → `State.ExitCode` 0 and logs contain `vandoxd stopped`; `docker compose start` →
`healthy` within 60 s and logs contain `"created":false`. No secret of the repository, no network beyond
loopback, no push. All values reach `run:` through the script arguments, no `${{ }}` in the script.

## Affected projects and types

| Project | Type / file | Change |
| ------- | ----------- | ------ |
| `cmd/vandoxd` | `main.go` | rewrite: new `run` signature, own flag set, `listenFunc`; no longer calls `cli.Run` |
| `cmd/vandoxd` | `serve.go`, `logger.go`, `healthcheck.go` | new |
| `cmd/vandoxd/internal/server` | `server.go`, `health.go` | new package |
| `cmd/vandoxd/internal/store` | `store.go` | new package |
| `internal/cli` | `cli.go` | package comment only: used by `vandox-agent` (no behavior change) |
| module | `go.mod`, `go.sum` | `modernc.org/sqlite` and its indirect requirements |
| deploy | `deploy/backend/Dockerfile` | `/data`, `EXPOSE`, `STOPSIGNAL`, `HEALTHCHECK` |
| deploy | `deploy/backend/docker-compose.yml` | new |
| deploy | `deploy/backend/vandoxd.yaml` | comment only: the listen ports must match the compose port mappings |
| CI | `.github/workflows/ci.yml`, `.github/scripts/smoke-test-backend.sh` | new smoke-test step and script |
| repo | `.gitignore` | `deploy/backend/secrets/`, `deploy/backend/.env` |

The existing file the skeleton must rewrite: `cmd/vandoxd/main.go` (still holds the 0034 wiring through
`cli.Run`); nothing else of the old logic remains.

## Signatures (for the Dev's skeleton)

`cmd/vandoxd` (package `main`):

```go
// listenFunc opens a listener for network and address; main passes (&net.ListenConfig{}).Listen.
type listenFunc func(ctx context.Context, network, address string) (net.Listener, error)

func main() // os.Exit(run(context.Background(), os.Args[1:], os.Environ(), os.Stdout, os.Stderr, (&net.ListenConfig{}).Listen))

// run executes vandoxd with args (without the program name) and returns the process exit code: 0 on success,
// 1 on a runtime or start-up failure, 2 on a usage error.
func run(ctx context.Context, args, environ []string, stdout, stderr io.Writer, listen listenFunc) int

// serve runs the backend service until ctx is done or SIGTERM/SIGINT arrives and returns the exit code.
func serve(ctx context.Context, configPath string, environ []string, stderr io.Writer, listen listenFunc) int

// newLogger returns a JSON slog logger writing to w at level (debug, info, warn or error).
func newLogger(w io.Writer, level string) (*slog.Logger, error)

// healthcheck probes /healthz of the web listener configured in configPath and returns the exit code.
func healthcheck(ctx context.Context, configPath string, environ []string, stderr io.Writer) int

// healthURL returns the /healthz URL for the web listen address listen, using loopback for an empty or
// unspecified host.
func healthURL(listen string) (string, error)

// newHealthClient returns the HTTP client of the health check: 4 s timeout, no proxy, no redirects.
func newHealthClient() *http.Client

// probe sends GET url with client and returns nil only for status 200.
func probe(ctx context.Context, client *http.Client, url string) error
```

`cmd/vandoxd/internal/server`:

```go
// Pinger reports whether the database is reachable.
type Pinger interface {
	Ping(ctx context.Context) error
}

// Options configures Run.
type Options struct {
	Web             net.Listener  // listener of the web UI; serves /healthz
	Ingest          net.Listener  // listener of the ingest API; no routes yet
	DB              Pinger        // checked by /healthz
	Logger          *slog.Logger
	PingTimeout     time.Duration // limit of one /healthz database check
	ShutdownTimeout time.Duration // deadline of the graceful shutdown of both listeners
}

// Run serves Options.Web and Options.Ingest until ctx is done or a listener fails, then shuts both down
// gracefully within ShutdownTimeout. It returns nil after a clean shutdown, an error wrapping
// context.DeadlineExceeded when requests outlast the deadline (their connections are then closed), or the
// serve error of a failed listener.
func Run(ctx context.Context, opts Options) error

// NewWebHandler returns the handler of the web listener: GET and HEAD /healthz, 200 when db answers within
// pingTimeout, 503 otherwise.
func NewWebHandler(db Pinger, pingTimeout time.Duration, logger *slog.Logger) http.Handler

// NewIngestHandler returns the handler of the ingest listener; it answers 404 until the ingest API exists.
func NewIngestHandler() http.Handler

func newHTTPServer(h http.Handler, logger *slog.Logger) *http.Server // unexported, AC-S4
```

`cmd/vandoxd/internal/store`:

```go
// FileName is the name of the database file in the storage directory.
const FileName = "vandox.db"

// SchemaVersion is the database schema version this build reads and writes.
const SchemaVersion = 1

// Store is the backend's SQLite database.
type Store struct {
	db      *sql.DB
	created bool
}

// Open opens the database FileName in the existing directory dir, creating the file (mode 0600) if it does
// not exist, in WAL mode, and checks its schema version.
func Open(ctx context.Context, dir string) (*Store, error)

// Created reports whether Open created the database file.
func (s *Store) Created() bool

// Ping reads the schema version and returns an error when the database cannot be read.
func (s *Store) Ping(ctx context.Context) error

// Close closes the database.
func (s *Store) Close() error
```

Unexported helpers in `health.go` (the single-flight checker) are the Dev's choice; their behavior is fixed
by AC-S2.

## Test files

Per *Layout* in `.squad/stack.md` (`foo.go` → `foo_test.go`):

- `cmd/vandoxd/main_test.go` (existing; AC-R1–R4)
- `cmd/vandoxd/serve_test.go` (AC-R5–R11)
- `cmd/vandoxd/logger_test.go` (AC-R12)
- `cmd/vandoxd/healthcheck_test.go` (AC-R13–R15)
- `cmd/vandoxd/internal/server/server_test.go` (AC-S3–S10)
- `cmd/vandoxd/internal/server/health_test.go` (AC-S1, AC-S2)
- `cmd/vandoxd/internal/store/store_test.go` (AC-D1–D6)

Test doubles (no mocking library): a fake `Pinger` (scripted error, call counter, blocks until released,
released in `t.Cleanup`), a fake `listenFunc` that hands out pre-opened `127.0.0.1:0` listeners and records
the addresses it was asked for and whether they were closed, a fake `net.Listener` whose `Accept` fails
(AC-S8), and `httptest` servers for the health check. Loopback listeners are what `httptest` itself uses
(`docs/UNIT_TESTS.md`, *Test stack*); no other network. Configuration files are written into `t.TempDir()`;
`web.listen` in serve tests may name any free-looking port, since the fake `listen` ignores it.

Existing test code that calls a changed signature: `cmd/vandoxd/main_test.go`, `TestRun_Version` (the call
`run([]string{"-version"}, &stdout, &stderr)`) and `TestRun_UndefinedFlag` (`run([]string{"-bogus"}, &stdout, &stderr)`).
The old signature goes away, so the **Dev** adapts both call sites in step 4 to
`run(context.Background(), args, nil, &stdout, &stderr, <listen func that fails the test>)` — mechanically,
no assertion touched. `internal/cli/cli_test.go` is unaffected.

## Documentation updates

The Dev makes these:

- `README.md`: *Binaries* — `vandoxd` without arguments runs the service, flags `-config`, `-healthcheck`;
  *Layout* — `cmd/vandoxd/internal/`; *Install / Backend* — a "Run with Docker Compose" part: the files to
  create (`vandoxd.yaml`, `secrets/vandox_agent_token` readable by UID 65532, `import/`, `.env` with
  `TAILNET_ADDRESS`, optional `WEB_BIND_ADDRESS`), `docker compose up -d`, the reverse proxy pointing at
  `127.0.0.1:8080`, `/healthz` for monitoring (through the proxy), and that a bind-mounted data directory
  must be owned by 65532. No configuration-table change (no new option, no new secret).
- `docs/ARCHITECTURE.md`: intro (configuration loading now used by `vandoxd`; the backend service, its
  `/healthz` and the database file exist); *Components* — `cmd/vandoxd` with `cmd/vandoxd/internal/`
  (0061); *Network* — the compose port bindings (web on loopback for the proxy, ingest on the tailnet
  address); *Storage and retention* — the database file `vandox.db` in `storage.directory`, mode 0600,
  `modernc.org/sqlite` (0057); *Deployment* — `HEALTHCHECK` through `vandoxd -healthcheck`, `/data` owned by
  65532, the compose file (0059, 0060); links to the new records.
- `docs/UNIT_TESTS.md` (*Code coverage*): `main` only calls `os.Exit(run(...))` with the process boundaries
  as arguments; link 0058 instead of 0034.
- `docs/CONTRIBUTING.md` (*Release build check on pull requests*): the job also starts the image with the
  compose file and checks health, restart persistence and graceful stop.
- `SECURITY.md` (*Deployment Security Considerations*): keep the web port on loopback (or bound to the proxy
  host) and the ingest port on the tailnet address as in the compose file.
- `.squad/project.md` (product facts this change makes untrue or incomplete): area 2 names
  `deploy/backend/docker-compose.yml` and its port bindings; area 9 names the database file
  (`cmd/vandoxd/internal/store`, mode 0600, no directory creation); area 11 names the loopback health-check
  call (`healthcheck.go`: timeout, no proxy, no redirects); area 12 names the JSON `slog` handler of
  `vandoxd`; area 13 names the smoke script `.github/scripts/smoke-test-backend.sh` (runs on pull
  requests, reads no secret, pushes nothing); *Test doubles* gains the
  database row (fake `Pinger` in `cmd/vandoxd/internal/server`, status implemented) and the `time` row
  stays planned.
- `deploy/backend/vandoxd.yaml`: comment that `web.listen`/`ingest.listen` ports must match the compose
  port mappings.
- `internal/cli/cli.go`: package comment (see *Affected*).

## Architecture check

- Guarantees in `.squad/project.md` (*Guarantees*): none is touched — no agent code, no backfill or gap
  logic. The single-flight health ping follows the spirit of 0029 (a hung database cannot pile up
  goroutines) without being bound by it.
- 0007 (SQLite, FTS5, WAL, one container plus one data volume): kept — pure-Go driver with FTS5, WAL
  verified at open, the database in the data volume.
- 0016/0023 (UI in the LAN behind a TLS proxy; "the UI port must only be reachable by the reverse proxy"):
  the compose file publishes the web port on `127.0.0.1` by default; the issue's "home-network monitoring"
  reaches `/healthz` through the proxy. `/healthz` is unauthenticated by requirement; #25 must keep it
  outside the login (named in 0059).
- 0010/0017 (ingest only on the tailnet address): the compose file refuses to start without
  `TAILNET_ADDRESS`; the ingest listener serves no health or UI route.
- 0032/0050 (secrets): the compose file passes the token only as a Docker secret through
  `VANDOX_AGENT_TOKEN_FILE`; `GOMEMLIMIT` and the compose interpolation variables (`TAILNET_ADDRESS`,
  `WEB_BIND_ADDRESS`) carry no `VANDOX_` prefix and do not reach `checkEnviron` (interpolation variables are
  not passed into the container). Secrets are never logged (AC-R10).
- 0041/0055 (base image pinning): no `FROM`/`ARG` change; the pinning script stays as it is.
- 0049 (no environment overrides, strict file): no new option; the config path is a flag, not a
  `VANDOX_` variable.
- 0034: superseded by 0058 (`run` gains context, environment and listen function; no arguments serve).

## Security considerations

Guards and the inputs they see, with the accepted forms:

- **`healthURL` (input: `web.listen`, already validated by `checkListen`: empty host or an IP literal
  accepted by `netip.ParseAddr`, port 1–65535).** Forms and behavior: empty host → `127.0.0.1`; `0.0.0.0` →
  `127.0.0.1`; `::` → `::1`; an IPv4-mapped unspecified `::ffff:0.0.0.0` → unmapped first, then
  `127.0.0.1`; any other IPv4 or IPv6 literal (including a zoned `fe80::1%eth0`) → used as is, bracketed and
  the zone percent-encoded by `net.JoinHostPort` + `url.URL`; anything `net.SplitHostPort` rejects → error
  (exit 1). The client only ever dials the configured web listener; it has a timeout, ignores
  `HTTP_PROXY`/`HTTPS_PROXY` (`Proxy: nil`), follows no redirect and sends no credentials (area 11).
- **Database path (input: `storage.directory`, validated by `checkDirectory` as absolute and clean, free of
  Cc/Cf/Zl/Zp by 0049).** The file name is the constant `vandox.db`; the path goes into the SQLite URI only
  through `url.URL.Path`, so `?`, `#`, `%` and spaces cannot add URI parameters (`_pragma`, `mode`,
  `vfs`, `immutable`) or cut the path (AC-D5); the only query parameters are the three constant pragmas.
  The file is created `0600` with `O_EXCL`; an existing non-regular file is refused; the directory is not
  created (area 9).
- **Compose port interpolation.** `${TAILNET_ADDRESS:?…}`: unset and empty both stop `docker compose`
  (`:?` semantics), so the ingest port is never published on all interfaces by accident;
  `${WEB_BIND_ADDRESS:-127.0.0.1}`: unset or empty → loopback. A value that is not an address of the host
  makes the container fail to start (bind error), i.e. fails closed (area 2).
- **Command line.** Flags only (`-config`, `-healthcheck`, `-version`); positional arguments are rejected;
  no secret is accepted on the command line (0032).
- **HTTP server.** Header, read, write and idle timeouts and a 16 KiB header limit on both listeners, so a
  slow client cannot hold connections forever; `/healthz` returns only `ok`/`unavailable`, never an error
  text (a SQLite error can contain the path).
- **Logs (area 12).** JSON handler: control characters in attribute values are escaped; error values are
  attributes, not message text; `config.Secret` logs as `[redacted]`; the start line logs no secret.
- **Container.** Non-root 65532, read-only root file system, `cap_drop: ALL`, `no-new-privileges`,
  configuration and import mounted read-only, the token as a Docker secret; the CI smoke test reads no
  repository secret and pushes nothing (area 13).

Accepted residual: `/healthz` is unauthenticated and reveals only whether the database answers (issue
requirement). The secret file on the host must be readable by UID 65532, which on a NAS usually means
`chown 65532`; documented, not enforced.

## Decision records

All `Proposed`, written with this plan:

- [`docs/decisions/0057-sqlite-driver-modernc-pure-go.md`](../../docs/decisions/0057-sqlite-driver-modernc-pure-go.md) —
  `modernc.org/sqlite` as the SQLite driver.
- [`docs/decisions/0058-vandoxd-runs-the-service-by-default-with-a-shutdown-deadline.md`](../../docs/decisions/0058-vandoxd-runs-the-service-by-default-with-a-shutdown-deadline.md) —
  `vandoxd`'s flags, default action, JSON logging, signal handling and shutdown deadline; supersedes 0034
  (0034's status becomes `Superseded by 0058` with the approval).
- [`docs/decisions/0059-healthz-checks-the-database-and-the-binary-is-the-health-probe.md`](../../docs/decisions/0059-healthz-checks-the-database-and-the-binary-is-the-health-probe.md) —
  `/healthz` semantics, its listener, the single-flight ping, `vandoxd -healthcheck` as the image's
  `HEALTHCHECK`, `/data` in the image, no baked configuration.
- [`docs/decisions/0060-compose-file-port-bindings-volumes-and-memory-limit.md`](../../docs/decisions/0060-compose-file-port-bindings-volumes-and-memory-limit.md) —
  compose port bindings, volumes, secret, memory limit, hardening, no Dependabot `docker-compose` entry,
  CI smoke test.
- [`docs/decisions/0061-backend-only-packages-under-cmd-vandoxd-internal.md`](../../docs/decisions/0061-backend-only-packages-under-cmd-vandoxd-internal.md) —
  backend-only packages under `cmd/vandoxd/internal/`.

## Out of scope / follow-ups

- **Tailscale in userspace-networking mode on the backend host.** The Tailscale package on Synology DSM 7
  runs in userspace-networking mode unless TUN is set up; then the tailnet address is not a host
  interface, binding the ingest port to it fails (fails closed), and inbound tailnet connections are
  delivered to the host's loopback instead. How to bind the ingest port there without offering the web
  port to the tailnet is a deployment question for the ingest feature (#40), not for this skeleton.
  Proposed follow-up issue — title: `[Docs] Ingest port binding when Tailscale runs in userspace networking
  on the backend host`; body: "`deploy/backend/docker-compose.yml` (#13, record 0060) publishes the ingest
  port on `${TAILNET_ADDRESS}`. On a Synology NAS the Tailscale package runs in userspace-networking mode by
  default, where the tailnet address is not a host interface, so the bind fails, and inbound tailnet
  connections are forwarded to 127.0.0.1, where the web port is published. Decide and document the
  supported setup (TUN mode required, or a different binding with an ACL argument), and check it against
  security area 2 and records 0010, 0016, 0017."
- Schema migrations beyond version 1, retention, rollups: with the storage features.
- `/healthz` staying outside the login: #25 (named in 0059).
