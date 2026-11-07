# Plan: install.sh refuses the malformed IPv6 literals the agent refuses

Source: Issue #385
Tier: standard — `validate_backend_url` gets a real IPv6 grammar check in place of a colon count, a behavior change of input validation that creates, changes or removes no security control (the agent already refuses these values; only where the failure shows moves from the agent's start to the install).
Security areas touched: 10 Parsing of external input (`install.sh` inputs), 11 Outbound calls (`validate_backend_url` in `deploy/agent/install.sh` is named there). Areas 6 and 9 list `install.sh` too, but no right, file write or path changes.

## Problem / root cause

`deploy/agent/install.sh:259` decides that a bracketed host is an IPv6 literal with `[[ $host == *:*:* ]]`: the regex at line 246 only restricts the characters to hex digits and colons, so any such run with two colons passes (`1:::2`, `::1::`, `1:2:3`, `12345::1`, nine groups). The agent's loader (`internal/config/agent.go:537`, `url.Parse`, which in Go 1.27 runs `netip.ParseAddr` on a bracketed host, `net/url/url.go:599`; and the rule at `agent.go:509`) and the sender (`internal/sender/transport.go:100-105`) refuse them, so the agent fails at start.

## Verified facts

| # | Claim | Evidence | Status |
| - | ----- | -------- | ------ |
| 1 | `validate_backend_url` accepts `http://[1:::2]` and `http://[::1::]` | `install.sh:246` (`^\[([0-9A-Fa-f:]+)\](:([0-9]+))?$`) and `:259` (only `*:*:*`), read | confirmed |
| 2 | `netip.ParseAddr` refuses both | GOROOT `net/netip/netip.go:262-264` (no digits after `:::`), `:311-313` (multiple `::`), Go 1.27 | confirmed |
| 3 | The agent's loader and sender use `netip.ParseAddr` on the host | `internal/config/agent.go:509`, `internal/sender/transport.go:105`; `url.Parse` already refuses a bracketed host `netip.ParseAddr` refuses (`net/url/url.go:599-601`) | confirmed |
| 4 | The install then succeeds and the agent fails at start | the URL is written to `agent.yaml` unchanged (`render_config`, `install.sh:837`); `LoadAgent` refuses it via `urlProblem` | confirmed |
| 5 | `netip.ParseAddr`'s IPv6 grammar without zone and dot: eight groups of 1 to 4 hex digits separated by single colons, or exactly one `::` that stands for at least one group (so at most seven explicit groups), no leading or trailing single colon | `netip.go:226-341` (`each group must have 4 or less digits`, `colon must be followed by more characters`, `multiple ::`, `address string too short`, `the :: must expand to at least one field of zeros`) | confirmed |
| 6 | The agent also accepts an embedded IPv4 tail (`::ffff:100.64.0.1`) and a zone; `install.sh` refuses both today because the character class at `:246` has no `.` or `%` | `netip.go:267-289`, `install.sh:246`; zone refused by the agent (`agent.go:509`) | confirmed |
| 7 | `parse_args` calls `validate_backend_url`, so a refused value exits 2 before any host change | `install.sh:178-180`; wiring tested by `runParseArgsCases` (`install_test.go:1436`) | confirmed |
| 8 | Record 0192 is unreleased (its parenthetical states this very gap) | `git cat-file -e v0.1.0:docs/decisions/0192-…` fails; `git ls-remote --tags origin` lists only `v0.1.0` | confirmed |

## Acceptance criteria

Every refused value: exit status 2, standard output empty, standard error exactly `install.sh: --backend-url is not http(s)://ip-address[:port]` and a line feed (the existing `backendURLLine`).

- AC1: `validate_backend_url` refuses a bracketed host that `netip.ParseAddr` refuses, at least: `[1:::2]`, `[:::]`, `[::1::]`, `[1::2::3]`, `[12345::1]`, `[00001::1]`, `[:1::2]`, `[1::2:]`, `[:1:2:3:4:5:6:7:8]`, `[1:2:3:4:5:6:7:8:]`, `[1:2:3]`, `[1:2:3:4:5:6:7]`, `[1:2:3:4:5:6:7:8:9]`, `[1:2:3:4:5:6:7:8::]`, `[::1:2:3:4:5:6:7:8]`, `[1::2:3:4:5:6:7:8]` (each as `http://<host>:8081`).
- AC2: it still accepts every bracketed hex-group literal `netip.ParseAddr` accepts, at least: `[::]`, `[::1]`, `[1::]`, `[fd7a:115c:a1e0::1]`, `[1:2:3:4:5:6:7:8]`, `[FD7A:115C:A1E0:0:0:0:0:1]`, `[0001::00fF]`, `[1:2:3:4:5:6:7::]`, `[::2:3:4:5:6:7:8]`, `[1::3:4:5:6:7:8]`, with and without `:8081` and a trailing `/`.
- AC3: a bracketed literal with an embedded IPv4 tail (`[::ffff:100.64.0.1]`, `[::100.64.0.1]`) stays refused, though the agent accepts it: the documented form for such an address is the IPv4 literal.

## Tests per criterion

| AC | Test(s) | Test file |
| -- | ------- | --------- |
| AC1 | `TestValidateBackendURL_RefusesIPv6TheAgentRefuses`, subtests `AC1 refuses <url>`; each case also asserts that `netip.ParseAddr` refuses its host, so the table cannot drift from the agent's parser | `deploy/agent/install_test.go` |
| AC2 | same test, subtests `AC2 accepts <url>`; each case also asserts that `netip.ParseAddr` accepts its host without a zone | `deploy/agent/install_test.go` |
| AC3 | same test, subtests `AC3 refuses <url>`; each case also asserts that `netip.ParseAddr` accepts it (the deliberate difference) | `deploy/agent/install_test.go` |

AC1 cases fail on today's code (each has at least two colons); AC2 and AC3 pass today and pin the new function against over-refusing.

## Approach

- New helper `_is_ipv6 VALUE` in `install.sh`, next to `_is_ipv4`, status 0 when VALUE (already limited to hex digits and colons by `re_v6`) has the grammar of fact 5: no `:::`; at most one `::`; without `::`, exactly eight groups matching `^[0-9A-Fa-f]{1,4}(:[0-9A-Fa-f]{1,4})*$` as a whole; with `::`, each side empty or matching that pattern, and the group count of both sides (colons plus one per non-empty side) at most seven. Under `LC_ALL=C`, like the caller.
- Line 259 becomes `_is_ipv6 "$host" || host=""`. Nothing else in the function changes; `_split_url` and `tailnet_warning` read only values that passed.
- Pitfall for the Dev: do not split groups with `IFS=: read -a`, which drops a trailing empty field (`1::2:` would pass); match each side with the anchored regex and count colons with a parameter expansion.
- No new user-visible output: the refusal uses the existing message.

## Options considered

1. **A grammar helper in bash** (chosen) — no tool beyond bash, which the script already needs; small and tested against the real parser.
2. **One extended regex with the nine RFC 4291 alternations** — same result, but a line of some 600 characters that no reviewer can check against fact 5; lost on readability.
3. **Ask the agent binary to validate the URL** — the script runs the downloaded binary only after argument parsing and download (`install.sh:939`), and the agent has no check-only mode; adding one is new API for a usage error. Lost.

## Affected projects and types

| Project | Type / file | Change |
| ------- | ----------- | ------ |
| agent installation | `deploy/agent/install.sh` | new `_is_ipv6`, call at line 259 |
| agent installation tests | `deploy/agent/install_test.go` | new test |

## Signatures (for the Dev's skeleton)

None: the change adds a private shell function and no Go or C# API, so step 4 does not run.

## Test files

`deploy/agent/install_test.go` (new test function; `net/netip` import). Existing call sites: none change. `runBackendURLCases` and `TestValidateBackendURL_RefusesHostNames` keep their inputs: each accepted IPv6 value there (`[fd7a:115c:a1e0::1]`, `[::1]`) is valid under the new grammar, and each refused one stays refused. The other bracketed URLs of the file (`install_test.go:459`, `:2149`) are valid literals.

Files no unit test can cover: none (`install.sh` lies outside `COVERAGE_PATHSPECS`; the Go tests run the function).

## Areas

- `docs/areas/agent-installation.md` (`--backend-url` rule, lines 46-49): the IPv6 form is spelled out (below).

## Documentation updates

Owned by the Dev, written after the tests are green:

- `docs/areas/agent-installation.md`: replace "or a bracketed IPv6 literal without a zone" by a phrase stating that the literal is one the agent's parser takes — eight groups of one to four hex digits, or one `::` for at least one group — without a zone or an embedded IPv4 part.
- Sweep hits with no change: `docs/areas/network-and-security-model.md:63-64` (enumerates forms of names and addresses the three refuse; a malformed literal is neither, the sentence stays true), `docs/areas/configuration-options.md:36` (the loader, unchanged), `docs/ARCHITECTURE.md:529`, `README.md:199`, `docs/INSTALL.md:339`, `deploy/agent/README.md:214,240,370` (host names only).
- `CHANGELOG.md`: none — it has no unreleased section; entries are written in the release PR (`docs/CONTRIBUTING.md:127`).
- Record 0192: edited by the Lead in this step (below).

## Architecture check

No guarantee of `.squad/project.md` is touched. Record 0192's rule (the host is an IP literal without a zone, parsed as `netip.ParseAddr` does) is enforced earlier, not changed; the installer stays at least as strict as the agent.

## Security considerations

- [x] No limits: the value is bounded by the command line; the checks are anchored regexes on it.
- [x] No exception reaches a user: the one fixed message, which never echoes the value.

## Decision records

- `docs/decisions/0192-backend-url-names-the-backend-by-ip-address-host-names-are-refused.md` (Accepted, unreleased) edited in place: its *Decision* no longer says a malformed IPv6 literal passes `install.sh`, and names the embedded-IPv4 refusal. No new record: the fix carries out 0192's decision.

## Out of scope / follow-ups

- Accepting an embedded IPv4 tail in `install.sh` (AC3 keeps the refusal; the operator writes the IPv4 literal).
