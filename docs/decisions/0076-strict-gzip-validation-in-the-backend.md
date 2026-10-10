# 0076: The backend decodes gzip with strict validation switched on for every process

- **Status:** Accepted
- **Date:** 2026-10-06
- **Area:** Wire format
- **Source:** Product Manager request: move the backend to .NET 10 with Blazor, keep the agent in Go
- **Supersedes:** —

## Context

Go reports a truncated gzip stream or a bad checksum as an error. .NET's `GZipStream` ends a truncated stream silently and passes it on as
complete unless `System.IO.Compression.UseStrictValidation` is enabled. The decoder and the log import must not store a half-read batch or
file as if it were whole (0044, 0069).

## Options considered

1. **Check the trailer by hand** — duplicated in every reader and easy to forget.
2. **Strict validation switch** (chosen) — one runtime setting; every `GZipStream` in the process throws on truncated or corrupt input.

## Decision

Option 2, set for every project in `Directory.Build.props`. Code that reads gzip calls `StrictGzip.Require()`, so a host without the option
fails at the first read instead of accepting damaged data; a unit test asserts the switch is on. Truncated or corrupt gzip is an error in the
[Wire format](../areas/wire-format.md) area (*Batch rules and trust*) and a failed file in the log import.

## Consequences

- Truncated and corrupt gzip input is an error in the decoder and a failed file in the import.
- A new executable that reads gzip must keep the property; it inherits it from `Directory.Build.props`.
