# Plan: config: open the configuration file without a stat-then-open race

Source: Issue #263
Status: Draft
Tier: security — the fix changes the control flow of `readFile` and `readSecretFile` in `internal/config`, which `.squad/project.md` lists under *Security areas* 8 (secrets) and 10 (configuration input must never hang).

## Problem / root cause

`internal/config/config.go:75-82` (`readFile`) calls `os.Stat(path)`, rejects a non-regular file, and then calls
`os.Open(path)`. A FIFO renamed into place between the two calls passes the check and `open(2)` blocks without a
writer, so `vandox-agent` hangs at start-up instead of failing.

Claims of the issue, checked against the code:

- "`config.go:75-82` calls `os.Stat` and then `os.Open`": **confirmed** (line 75 `os.Stat`, line 82 `os.Open`).
- "a FIFO swapped in between blocks start-up": **confirmed** by reading: `os.Open` is `O_RDONLY` without
  `O_NONBLOCK`, and `open(2)` of a FIFO for reading blocks until a writer opens it (fifo(7)).
- "apply the same fix to the backend's loader if it checks in the same order": **refuted for Linux**.
  `src/Vandox.Core/Configuration/BackendConfigLoader.cs:123` does a `FileProbe.GetKind` pre-check, but the read
  goes through `FileProbe.OpenRegular` (`src/Vandox.Core/IO/FileProbe.cs`), which on Linux opens with
  `O_RDONLY|O_NONBLOCK|O_CLOEXEC` and checks the type with `statx(AT_EMPTY_PATH)` on the opened handle, throwing
  `SafeIoException` (an `IOException`, caught at line 143 as "cannot read the file") for anything not regular. A
  swapped-in FIFO is opened without blocking and refused, so the backend cannot hang; the pre-check only yields the
  distinct "no such file or directory" / "not a regular file" texts. The same holds for
  `SecretReader.ReadFile` (`SecretReader.cs:200-216`). The portable branch (non-Linux) still checks then opens, but
  the backend ships only as a Linux container image and .NET there has no FIFO case to block on in practice; out
  of scope. The only defect on the backend side is the doc comment of `BackendConfigLoader.ReadFile`
  (lines 114-115): "The type is checked before the file is opened, so a FIFO cannot block the read" names the
  wrong mechanism; it is corrected (comment only).
- "only root can trigger the race": **confirmed** for the default `/etc/vandox/agent.yaml`; for `-config` and
  `_FILE` paths it is whoever controls the directory the operator names.

Related defect found on the way: `internal/config/secret.go:175-182` (`readSecretFile`, the agent's `_FILE` read)
has the identical `os.Stat` then `os.Open` sequence and the same hang. It is fixed in this change with the same
helper.

## Acceptance criteria

- [ ] AC1: `openRegular` opens a regular file for reading and returns it; reading it yields the content.
  Test: `TestOpenRegular` subtest "regular file is opened".
- [ ] AC2: `openRegular` on a FIFO that has no writer returns `errNotRegular` (checked with `errors.Is`), returns a
  nil file and does not block (the call returns at all; a blocking open hangs the test, as the backend's
  `FileProbeOpenRegular` FIFO test does). Test: `TestOpenRegular` subtest "FIFO without a writer is refused without blocking"
  (FIFO made with `syscall.Mkfifo` in `t.TempDir()`).
- [ ] AC3: `openRegular` on a directory and on a character device (`os.DevNull`) returns `errNotRegular` and a nil
  file. Tests: `TestOpenRegular` subtests "directory is refused", "device file is refused".
- [ ] AC4: `openRegular` on a missing path returns an error matching `fs.ErrNotExist` that is not `errNotRegular`.
  Test: `TestOpenRegular` subtest "missing file".
- [ ] AC5: `readFile` refuses a FIFO without blocking with the error `config: <path>: not a regular file` (exact
  text), a plain error, not a `*KeyError`. Test: `TestReadFile` failure case "FIFO" (through
  `requireReadFileFails`, plus a comparison of the full text). The existing `TestReadFile` cases (missing file
  with `fs.ErrNotExist` and the path in the text, directory, device, oversize, regular, empty) keep passing unchanged.
- [ ] AC6: the agent's `_FILE` secret read refuses a FIFO without blocking: `readSecret` returns a `*SecretError`
  for `VANDOX_AGENT_TOKEN_FILE` with Reason `must name a regular file`, and neither the path nor the file content
  appears in the error. Test: `TestReadSecret_FileVariable` subtest "FIFO is refused without blocking". The
  existing subtests (missing file without the path, directory, device file, symlink followed) keep passing unchanged.
- [ ] AC7: neither `readFile` nor `readSecretFile` checks the path's type before opening it: no `os.Stat` /
  `os.Lstat` remains in `internal/config` outside tests, and both call `openRegular`. Verified by the Reviewer and
  Security reading the diff (`grep -n "os.Stat\|os.Lstat" internal/config/*.go` outside `_test.go` is empty); a
  race window cannot be pinned deterministically without a seam, and AC2 proves the open itself cannot block.
- [ ] AC8: the backend's behavior is unchanged; only the doc comment of `BackendConfigLoader.ReadFile` changes.
  Verified by the existing `BackendConfigLoaderTests` and `FileProbeTests` staying green (*Test with coverage*).

## Approach

1. In `internal/config/config.go` add the sentinel `errNotRegular` and the helper `openRegular`:
   `os.OpenFile(path, os.O_RDONLY|syscall.O_NONBLOCK|syscall.O_NOCTTY, 0)`; on error return it unchanged (a
   `*fs.PathError`); then `f.Stat()` (fstat on the descriptor); on error close and return that error; when
   `!info.Mode().IsRegular()` close and return `nil, errNotRegular`; else return `f`. `O_NONBLOCK` keeps the open
   of a FIFO from blocking and has no effect on reads of a regular file (Go's `os` keeps regular files out of the
   poller); `O_NOCTTY` keeps a swapped-in terminal from becoming the agent's controlling terminal (systemd
   starts the service as a session leader). Go adds `O_CLOEXEC` itself. The file is closed with `f.Close()`, the
   error deliberately ignored as today (read-only file).
2. `readFile`: drop the `os.Stat` block; call `openRegular`; `errors.Is(err, errNotRegular)` gives
   `config: <path>: not a regular file`; any other error `config: %w` as today (the `*PathError` names the path,
   so the missing-file text keeps the path and `fs.ErrNotExist`). Rewrite the doc comment: the file is opened
   without blocking and its type checked on the opened descriptor, so a FIFO, also one swapped in, cannot block.
3. `readSecretFile` (`secret.go`): drop the `os.Stat` block; call `openRegular`; `errNotRegular` gives
   `&SecretError{Var: fileVar, Reason: "must name a regular file"}`; any other error `fileError(fileVar,
   reasonUnreadable, err)` as today (errno only, no path).
4. `BackendConfigLoader.ReadFile` doc comment: "The type is checked on the opened file (FileProbe.OpenRegular opens
   without blocking), so a FIFO cannot block the read; the check before the open only gives a missing or
   non-regular path its own message." No code change.

## Options considered

1. **`O_NONBLOCK|O_NOCTTY` open, then `Fstat` on the descriptor** (chosen) — one open, no window, the same pattern
   `FileProbe.OpenRegular` already uses in the backend; a swapped-in device is opened (without blocking) before it
   is refused.
2. **`O_PATH` open, `Fstat`, then a second open through `/proc/self/fd/<n>`** (the issue's alternative) — never
   opens a device for I/O, but `O_PATH` descriptors cannot be read, so it needs `/proc` (which the agent unit may
   restrict) and two opens, Linux-only code, for a race only root can trigger in `/etc/vandox`. Lost on complexity.
3. **Keep `os.Stat` and add an `Fstat` after `os.Open`** — still blocks inside `open(2)` on a FIFO; does not fix the issue.
4. **Keep `os.Stat` as a message-only pre-check in front of option 1** (as the backend does) — the Go errors already
   distinguish a missing file (`fs.ErrNotExist` from the open) from a non-regular one (`errNotRegular`), so the
   pre-check would be dead weight. Lost; the backend keeps its pre-check because its texts come from it and it is
   harmless there.

## Affected projects and types

| Project | Type / file | Change |
| ------- | ----------- | ------ |
| Go `internal/config` | `config.go` | new `errNotRegular`, new `openRegular`; `readFile` uses it, doc comment rewritten; imports `errors`, `syscall` |
| Go `internal/config` | `secret.go` | `readSecretFile` uses `openRegular` instead of `os.Stat` + `os.Open` |
| .NET `Vandox.Core` | `Configuration/BackendConfigLoader.cs` | doc comment of `ReadFile` only |

## Signatures (for the Dev's skeleton)

```go
// errNotRegular reports that an opened file is not a regular file.
var errNotRegular = errors.New("not a regular file")

// openRegular opens the file at path for reading without blocking on a FIFO and without making a terminal
// the controlling terminal, and checks on the opened descriptor that it is a regular file. A file that is not
// regular is closed and errNotRegular returned.
func openRegular(path string) (*os.File, error)
```

Unchanged signatures: `readFile(path string, limit int64) ([]byte, error)`,
`readSecretFile(fileVar, path string) (Secret, error)`. Files the skeleton rewrites: `internal/config/config.go`
(add the two members; the skeleton's `openRegular` may return `nil, errors.New("not implemented")`). `secret.go`
and `BackendConfigLoader.cs` are changed by the Dev in the implementation step.

## Test files

- `internal/config/config_test.go` — new `TestOpenRegular` (AC1-AC4), new failure case "FIFO" in `TestReadFile` (AC5).
- `internal/config/secret_test.go` — new subtest in `TestReadSecret_FileVariable` (AC6).

A small helper `makeFifo(t *testing.T) string` (`t.Helper()`, `syscall.Mkfifo` with mode 0o600 in `t.TempDir()`,
`t.Fatalf` on error) lives in `config_test.go` and is used by both files. The agent and its tests are Linux-only
(CI builds and tests with `GOOS=linux`), so no build tag is needed.

Existing test code that calls a changed signature: none (no signature changes).

Files of the change no unit test can cover: none. The `f.Stat()` failure branch of `openRegular` cannot be
provoked on a valid descriptor; it is two lines and stays within the 80 % threshold.

## Areas

- `docs/areas/configuration-and-secrets.md`, *Configuration file* (the "regular file of at most 1 MiB" bullet) and
  *Secrets* (the `_FILE` bullet): the type is now checked on the opened file, not before the open. Written by the
  Lead with this plan (the text holds for both binaries: the backend already behaves this way).

## Documentation updates

- `docs/areas/configuration-and-secrets.md` — done by the Lead (see *Areas*).
- `docs/decisions/0049-strict-configuration-file-schema-and-errors.md` — one *Consequences* bullet, done by the Lead.
- Doc comments in `config.go`, `secret.go` (if it states the order) and `BackendConfigLoader.cs` — Dev.

## Architecture check

The guarantee touched is *Security area* 10 of `.squad/project.md`: malformed or hostile configuration input never
hangs. The change closes the remaining window in the agent and does not weaken it; the backend already met it.
Secret handling (area 8) is unchanged: error texts still carry neither the `_FILE` path nor the content.
`docs/ARCHITECTURE.md` needs no change.

## Security considerations

- [ ] Limits: `MaxFileBytes` and `MaxSecretBytes + 3` are byte limits on the raw file before any decoding, unchanged.
- [ ] Exceptions reaching a user-visible message: `readFile` — `config: <path>: not a regular file`, or
  `config: ` + the `*fs.PathError` text of the open (`open <path>: <errno text>`) or of `File.Stat`
  (`stat <path>: ...`), as today for the open; the read error text is unchanged. `readSecretFile` —
  `must name a regular file` or `reasonUnreadable` with the errno only (`fileError`), as today.
- [ ] Residual risk accepted: a character or block device swapped in is opened (non-blocking, no controlling tty)
  before it is refused; an open with side effects on such a device would need root in `/etc/vandox` or control
  of an operator-chosen path. Recorded in record 0049.
- [ ] Kernel behavior relied on (`open(2)` of a FIFO with `O_RDONLY|O_NONBLOCK` returns immediately without a
  writer, fifo(7)) is not taken as given: AC2 and AC6 exercise it on a real FIFO.

## Decision records

- `docs/decisions/0049-strict-configuration-file-schema-and-errors.md` (unreleased, edited in place; status stays
  Accepted): one *Consequences* bullet on the open-then-check rule and why `O_PATH` lost. No new record: the
  choice is small and the topic is the configuration file this record covers.

## Out of scope / follow-ups

- The backend's non-Linux branch of `FileProbe.OpenRegular` (check, then `FileStream` open): the backend ships only
  as a Linux image; no follow-up.

## Challenge notes

Verdict: OBJECTIONS 0 major, 3 minor (Devil's Advocate, verbatim).

1. (minor) In `TestOpenRegular`, "FIFO without a writer" and in `TestReadFile`, "FIFO", a regression back to a blocking open would hang the test until the Go test timeout and give no failure message. Run the call in a goroutine and fail via `select` with a short `time.After`, or open it from a helper goroutine. Do not use a real-clock sleep as the assertion. Keep any time bound to what `docs/UNIT_TESTS.md` allows.
2. (minor) The `readFile` error text for an I/O failure on the descriptor (`f.Stat()`) is `stat <path>: ...`, which is still a path-bearing `*PathError`. For `readSecretFile`, `fileError` must keep passing only the errno, never the `*PathError` text. The Dev should check that `fileError` strips the path for errors that come from `openRegular`, as it does for `os.Open` errors today. AC6 covers the FIFO case but not this one.
3. (minor) The new doc comments on `readFile` and `readSecretFile` should state the order as "open without blocking, then check the type on the descriptor". This applies to `secret.go` too, even though the plan marks that comment "if it states the order".
