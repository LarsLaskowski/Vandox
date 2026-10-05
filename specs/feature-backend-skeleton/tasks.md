# Tasks: Backend skeleton and container

Plan: [plan.md](plan.md) — Status: Draft (revised after the plan challenge)

| # | Task | Files | Tests (AC) | Owner | Done |
| - | ---- | ----- | ---------- | ----- | ---- |
| 1 | Add `modernc.org/sqlite` (`go get`, `go mod tidy`), check `go tool govulncheck ./...` with the current Go patch | `go.mod`, `go.sum` | AC-D1, AC-D6 | Dev | [ ] |
| 2 | Skeleton: signatures of `cmd/vandoxd` (`listenFunc`, `run`, `serve`, `newLogger`, `healthcheck`, `healthURL`, `newHealthClient`, `probe`), `cmd/vandoxd/internal/server` (`Pinger`, `Options`, `Run`, `NewWebHandler`, `NewIngestHandler`, `newHTTPServer`), `cmd/vandoxd/internal/store` (`FileName`, `SchemaVersion`, `Store`, `Open`, `Created`, `Ping`, `Close`); rewrite `main.go`; adapt the two `run` call sites in `main_test.go` mechanically; *Build* passes | `cmd/vandoxd/main.go`, `serve.go`, `logger.go`, `healthcheck.go`, `internal/server/server.go`, `internal/server/health.go`, `internal/store/store.go`, `cmd/vandoxd/main_test.go` (call sites only) | — | Dev | [ ] |
| 3 | Tests first for the command line, service, logger and health check; they compile and fail | `cmd/vandoxd/main_test.go`, `serve_test.go`, `logger_test.go`, `healthcheck_test.go` | AC-R1–AC-R15 | Tester | [ ] |
| 4 | Tests first for the server package (AC-S2 in a `synctest` bubble; AC-S6/S7 under the real-timer rules of record 0062) | `cmd/vandoxd/internal/server/server_test.go`, `health_test.go` | AC-S1–AC-S10 | Tester | [ ] |
| 5 | Tests first for the store package | `cmd/vandoxd/internal/store/store_test.go` | AC-D1–AC-D6 | Tester | [ ] |
| 6 | Implement the store | `cmd/vandoxd/internal/store/store.go` | AC-D1–AC-D6 | Dev | [ ] |
| 7 | Implement the server (handlers, single-flight ping, `Run`, shutdown deadline) | `cmd/vandoxd/internal/server/server.go`, `health.go` | AC-S1–AC-S10 | Dev | [ ] |
| 8 | Implement `run`, `serve`, logger and health check; correct the `internal/cli` package comment | `cmd/vandoxd/main.go`, `serve.go`, `logger.go`, `healthcheck.go`, `internal/cli/cli.go` | AC-R1–AC-R15 | Dev | [ ] |
| 9 | Dockerfile: `/data` (65532, 0700, comment on why `--chown`, record 0059), `EXPOSE`, `STOPSIGNAL`, `HEALTHCHECK`; pinning check unchanged | `deploy/backend/Dockerfile` | AC-C1 | Dev | [ ] |
| 10 | Compose file (web port only; ingest port not published, comment naming #40), `.gitignore` entries, example-config comment | `deploy/backend/docker-compose.yml`, `.gitignore`, `deploy/backend/vandoxd.yaml` | AC-C2, AC-C5 | Dev | [ ] |
| 11 | CI smoke test script and step (no 8081 binding; stop, then `down` without `-v` and `up -d` for persistence) | `.github/scripts/smoke-test-backend.sh`, `.github/workflows/ci.yml` | AC-C1, AC-C3–AC-C5 | Dev | [ ] |
| 12 | Coverage ≥ 80 % on new/changed code and overall | — | all AC-R/S/D | Tester, Dev | [ ] |
| 13 | Documentation updates from the plan | `README.md`, `docs/ARCHITECTURE.md`, `docs/UNIT_TESTS.md`, `docs/CONTRIBUTING.md`, `SECURITY.md`, `.squad/project.md` | — | Dev | [ ] |
| 14 | *Format* and *Analyzer gate* (including `gocognit` on `run`, `serve`, `Run`) | changed Go files | — | Code Officer | [ ] |
| 15 | Decision records 0057–0062 match the build; 0034 → `Superseded by 0058`; index | `docs/decisions/` | — | Lead | [ ] |
