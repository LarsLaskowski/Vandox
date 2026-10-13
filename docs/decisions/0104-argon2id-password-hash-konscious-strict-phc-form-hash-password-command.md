# 0104: The password hash is argon2id in strict PHC form within bounds, verified with Konscious.Security.Cryptography.Argon2; `vandoxd hash-password` creates it

- **Status:** Proposed
- **Date:** 2026-10-13
- **Area:** Web login
- **Source:** Issue #25
- **Supersedes:** —

## Context

Issue #25 asks for an argon2id password hash supplied as a secret (0032, 0050: printable ASCII, from `VANDOX_WEB_PASSWORD_HASH` or its
`_FILE` form). .NET 10 has no Argon2 implementation, so a dependency is needed (a `security`-tier change). The hash string is an
external format: operators may produce it with the reference `argon2` tool, libsodium, PHP or argon2-cffi, all of which write the
encoding of the reference implementation (P-H-C/phc-winner-argon2, commit f57e61e1, `src/encoding.c`): `$argon2id`, an optional `$v=`,
then `$m=…,t=…,p=…`, salt and hash in base64 without padding; decimals without leading zeros; the decoder refuses a base64 text whose
length is 1 modulo 4 or whose unused trailing bits are not zero. Its test suite (`src/test.c`) holds eight argon2id strings with their
passwords, used here as the real sample. The container is limited to 512 MiB (0060), and every check of a password allocates the
hash's memory parameter. On a NAS the operator often has no `argon2` tool, but the image is always there.

## Options considered

The implementation:

1. **Konscious.Security.Cryptography.Argon2 1.3.1** (chosen) — managed code (with Blake2 1.1.1 from the same author), MIT, no native
   library, so the chiseled image and every architecture work unchanged; it computes the raw hash, so the PHC string is parsed by own
   code and compared in constant time.
2. **Isopoh.Cryptography.Argon2** — parses PHC strings itself, but allocates through its own locked-memory arrays, which depend on
   `mlock` limits of the container.
3. **A libsodium wrapper** — fast native code with PHC verification built in, but a native library per architecture in the image.
4. **An own implementation** — not for cryptography.

Which hashes are accepted:

1. **Whatever the reference decoder accepts** — includes version 16 strings without `v=` and parameters so small (the reference tests use
   256 KiB) or so large that a check is either trivial to brute-force or exhausts the container.
2. **Only `$argon2id$v=19$` with memory 19,456 to 262,144 KiB, 2 to 10 iterations, parallelism 1 to 8, salt 8 to 64 and hash 16 to 64
   bytes** (chosen) — covers the defaults of OWASP (19 MiB, 2 iterations), libsodium's interactive and moderate levels, PHP and
   argon2-cffi; refuses a weak or oversized hash at start with a reason that never shows the value.

How an operator creates the hash:

1. **`vandoxd hash-password`, reading the password from standard input** (chosen) — available wherever the image runs
   (`docker run --rm -i networlddev/vandox hash-password`), with parameters inside the accepted bounds; the smoke test uses it too.
2. **Only documentation of external tools** — not available on a NAS without installing packages.

## Decision

The service accepts only an argon2id hash in the strict form and bounds listed in the [Web login](../areas/web-login.md) area and refuses
to start otherwise; it checks a password with Konscious.Security.Cryptography.Argon2, one check at a time, and compares the result in
constant time. `vandoxd hash-password` creates such a hash with memory 64 MiB, 3 iterations and parallelism 4 (RFC 9106's second
recommended setting).

## Consequences

- A check takes memory of the hash's memory parameter (at most 256 MiB) and, in managed code, noticeably longer than native code; one
  check at a time keeps it within the container limit, and the failed-login limits (0105) keep the number of checks small.
- A hash from a tool that writes another form (a `keyid` or `data` parameter, base64url, padding, version 16) is refused; the operator
  creates a new one with `hash-password`.
- Konscious is updated like any NuGet package (Dependabot, NuGet vulnerability check in the release); its result for the reference
  vectors is pinned by a test, so an update that changes the output fails the build.
