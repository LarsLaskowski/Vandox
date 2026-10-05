# 0057: modernc.org/sqlite as the SQLite driver: pure Go, no cgo, FTS5 included

- **Status:** Proposed
- **Date:** 2026-10-05
- **Source:** Issue #13
- **Supersedes:** —

## Context

Record 0007 puts all backend data into SQLite with FTS5, in WAL mode, and notes that "the SQLite driver
must support FTS5". Issue #13 needs the database for the first time: `GET /healthz` returns 200 only when
the database is reachable. `go.mod` has no SQLite driver yet.

The image is built with `CGO_ENABLED=0` and runs on `gcr.io/distroless/static-debian13` (0041), which has
no C library. The release checks that the agent binary is statically linked, and the Dockerfile builds
`vandoxd` the same way.

## Options considered

1. **`github.com/mattn/go-sqlite3`** — the most widely used driver, wraps the C SQLite.
   - Pros: the reference SQLite build, fast.
   - Cons: it needs cgo. That means `CGO_ENABLED=1`, a C toolchain in the build stage, and either a libc in
     the runtime image (no longer distroless static) or a fully static cgo link with extra flags. FTS5 needs
     the build tag `sqlite_fts5`.
2. **`modernc.org/sqlite`** — SQLite's C source translated to Go.
   - Pros: no cgo, so the build and runtime images stay as they are. FTS5 is compiled in. It uses
     `database/sql` with the driver name `sqlite`, and connection pragmas go into the DSN (`_pragma=`).
     Checked on 2026-10-05 with v1.60.1 (SQLite 3.53.4): builds with `CGO_ENABLED=0`, WAL mode and FTS5
     virtual tables work, and a database path given through `url.URL` keeps `?`, `#` and `%` in the file
     name.
   - Cons: slower than C SQLite. It brings about nine indirect modules (`modernc.org/libc`, `mathutil`,
     `memory`, `github.com/dustin/go-humanize`, `github.com/google/uuid`, `github.com/mattn/go-isatty`,
     `github.com/ncruces/go-strftime`, `github.com/remyoudompheng/bigfft`, `golang.org/x/sys`), and its
     page cache is allocated outside the Go heap, so `GOMEMLIMIT` does not see it.
3. **`github.com/ncruces/go-sqlite3`** — SQLite compiled to WebAssembly, run by the `wazero` runtime.
   - Pros: no cgo, FTS5 included, close to the C build.
   - Cons: a WebAssembly runtime in the backend process, with more memory per connection and a smaller user
     base than option 2.

## Decision

Option 2: `vandoxd` uses `modernc.org/sqlite` through `database/sql`, driver name `sqlite`. The store opens
`<storage.directory>/vandox.db` with the DSN built by `url.URL{Scheme: "file", Path: <path>}` and the
query `_pragma=journal_mode(WAL)&_pragma=busy_timeout(5000)&_pragma=foreign_keys(1)`, and fails when
`PRAGMA journal_mode` does not report `wal`. A unit test creates an FTS5 table so a driver without FTS5
fails the build.

Before handing the path to the driver, the store checks `vandox.db`, `vandox.db-wal` and `vandox.db-shm`
with `os.Lstat` and refuses a symbolic link or any other non-regular file (a missing `vandox.db` is
created with `O_CREATE|O_EXCL`, mode `0600`). SQLite follows a symbolic link of the main file and puts the
database and its `-wal`/`-shm` files next to the resolved target, so an `os.Stat` check would let the
database be written outside `storage.directory` (plan security review of #13). The storage directory
itself may be a link.

## Consequences

- The Dockerfile stays `CGO_ENABLED=0` on distroless static; the binary stays static.
- `govulncheck` and Dependabot's `gomod` entry now cover the driver and its indirect modules.
- Heavy queries will be slower than with C SQLite. If later analysis features measure that as a problem,
  option 1 or 3 can be revisited in a new record (which would have to change the runtime image).
- The SQLite page cache does not count against `GOMEMLIMIT`; the compose file leaves a margin between
  `GOMEMLIMIT` and `mem_limit` (0060).
