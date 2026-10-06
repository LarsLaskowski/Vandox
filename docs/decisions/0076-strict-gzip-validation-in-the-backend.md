# 0076: The backend decodes gzip with strict validation switched on for every process

- **Status:** Accepted
- **Date:** 2026-10-06
- **Source:** Product Manager request: move the backend to .NET 10 with Blazor, keep the agent in Go
- **Supersedes:** —

## Context

Go's `compress/gzip` reports a truncated stream or a bad checksum as an error. .NET's `GZipStream` ends a
truncated stream silently and passes it on as complete unless `System.IO.Compression.UseStrictValidation` is
enabled. The decoder and the log import must not store a half-read batch or file as if it were whole
([0044](0044-batch-validated-as-a-whole-agent-records-only.md), [0069](0069-log-import-idempotent-per-file-content-hash-with-resumable-batches.md)).

## Options considered

1. **Check the trailer by hand** — duplicated in every reader and easy to forget.
2. **Strict validation switch** — one runtime setting; every `GZipStream` in the process throws on truncated or corrupt input.

## Decision

Option 2. `Directory.Build.props` adds the runtime host option to every project, so applications and test
hosts run strict. `StrictGzip.Require()` (`Vandox.Core.IO`) checks the switch and is called wherever the code
reads gzip, so a host that lacks the option fails at the first read instead of accepting damaged data. A unit
test asserts the switch is on.

## Consequences

- Truncated and corrupt gzip input is an error in the decoder and a failed file in the import.
- A new executable that reads gzip must keep the property (it inherits it from `Directory.Build.props`).
