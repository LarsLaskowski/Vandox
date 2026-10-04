# Tasks: Configuration loading for agent and backend

Plan: [plan.md](plan.md) — Status: Approved by Lead

| # | Task | Files | Tests (AC) | Owner | Done |
| - | ---- | ----- | ---------- | ----- | ---- |
| 1 | Skeleton: add the dependency (`go get go.yaml.in/yaml/v3@v3.0.5`, `go mod tidy`); compile-only signatures of `internal/config` per *Signatures*; stub `wire.ValidateAgentID` (leave `Header.Validate` untouched); *Build* passes | `go.mod`, `go.sum`, `internal/config/{config,decode,secret,agent,backend}.go`, `internal/wire/wire.go` | — | Dev | [x] |
| 2 | Tests for `KeyError`, `readFile`, value checks | `internal/config/config_test.go` | AC6, AC8, AC13 | Tester | [x] |
| 3 | Tests for the strict decoder, one case per row of the YAML accepted-forms table (including the safe unknown-key-name rule, parser errors as `*KeyError` without library text, a second document after a null first one, the `Node.Tag` allowlist with its `%TAG`/verbatim/`!!set`/`!!omap` forms, `!!null` with content, Cc/Cf/Zl/Zp in values), with a white-box test struct | `internal/config/decode_test.go` | AC5, AC6, AC7 | Tester | [x] |
| 4 | Tests for `Secret` redaction, `SecretError`, `checkEnviron`, `readSecret` (env and `_FILE` forms; no `_FILE` value or path and no unsafe variable name in any error) | `internal/config/secret_test.go` | AC9, AC11, AC12 | Tester | [x] |
| 5 | Tests for `LoadAgent`, including the example file, defaults, required options and secrets, environment strictness, redaction of the loaded struct, error order | `internal/config/agent_test.go` | AC1, AC3, AC4, AC5, AC6, AC10, AC11, AC12, AC13 | Tester | [x] |
| 6 | Tests for `LoadBackend`, likewise | `internal/config/backend_test.go` | AC2, AC3, AC4, AC6, AC10, AC11, AC12 | Tester | [x] |
| 7 | Test `TestValidateAgentID` | `internal/wire/wire_test.go` | AC14 | Tester | [x] |
| 8 | Implement `internal/config` (gocognit ≤ 15 per function, no `//nolint`) | `internal/config/*.go` | AC1–AC13 | Dev | [x] |
| 9 | Implement `wire.ValidateAgentID` and make `Header.Validate` call it (behavior unchanged) | `internal/wire/wire.go` | AC14 + existing wire tests | Dev | [x] |
| 10 | Commented example files covering every option with its default; delete `.gitkeep` | `deploy/agent/agent.yaml`, `deploy/backend/vandoxd.yaml`, `deploy/agent/.gitkeep` | AC1, AC2, AC3 | Dev | [x] |
| 11 | Documentation: README *Configuration* section and *Layout*; ARCHITECTURE *Configuration*; `.squad/project.md` integration surface and security areas 8, 10 | `README.md`, `docs/ARCHITECTURE.md`, `.squad/project.md` | — | Dev | [x] |
| 12 | Coverage ≥ 80 % on new/changed code and overall; `go tool govulncheck ./...` clean | — | — | Dev / Tester | [x] coverage (changed 96.1 %, overall 96.8 %); govulncheck not runnable locally (vuln.go.dev 403), to be confirmed by CI |
| 13 | *Format* and *Analyzer gate* | changed files | — | Code Officer | [x] |
