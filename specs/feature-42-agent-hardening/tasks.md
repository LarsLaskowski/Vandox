# Tasks: Agent hardening and installation

Plan: [plan.md](plan.md) — Status: Draft

| # | Group | Task | Files | Criteria | Depends on | Owner | Done |
| - | ----- | ---- | ----- | -------- | ---------- | ----- | ---- |
| 1 | G1 | The unit file: no new API | — | AC6, AC8 | — | Dev | [ ] |
| 2 | G1 | Unit tests first (keys of systemd 249 read from tag `v249` first) | `deploy/agent/service_test.go` | AC6, AC8, AC11 | — | Tester | [ ] |
| 3 | G1 | Write the unit | `deploy/agent/vandox-agent.service` | AC6, AC8 | 2 | Dev | [ ] |
| 4 | G2 | Skeleton: `read_spool_directory`, `check_platform`, `run_mariadb_sql`, `parse_args`, `validate_version`, `validate_agent_id`, `validate_backend_url`, `tailnet_warning`, `needs_token`, `read_token`, `validate_token`, `fetch_release`, `verify_checksum`, `check_binary_version`, `checkout_warning`, `render_config`, `name_server_warning`, `write_token`, `dropin_warning`, `ensure_user`, `ensure_spool_directory`, `check_journal_group`, `check_journal_clock`, `check_ipc_filter`, `check_capabilities`, `main` | `deploy/agent/common.sh`, `deploy/agent/install.sh` | AC1, AC2, AC3, AC4, AC5, AC10 | 3 | Dev | [ ] |
| 5 | G2 | Install tests first | `deploy/agent/common_test.go`, `deploy/agent/install_test.go` | AC1, AC2, AC3, AC4, AC5, AC10 | 4 | Tester | [ ] |
| 6 | G2 | Implement the install script and the shared functions | `deploy/agent/common.sh`, `deploy/agent/install.sh` | AC1, AC2, AC3, AC4, AC5, AC10 | 5 | Dev | [ ] |
| 7 | G3 | Skeleton: `parse_args`, `main` of `uninstall.sh` | `deploy/agent/uninstall.sh` | AC7 | 6 | Dev | [ ] |
| 8 | G3 | Uninstall tests first | `deploy/agent/uninstall_test.go` | AC7 | 7 | Tester | [ ] |
| 9 | G3 | Implement uninstall and purge, the removal SQL | `deploy/agent/uninstall.sh`, `deploy/agent/mariadb-user-remove.sql` | AC7 | 8 | Dev | [ ] |
| 10 | G4 | End-to-end check on systemd 249: no new API | — | AC1, AC2, AC3, AC6, AC7, AC8, AC9, AC10 | — | Dev | [ ] |
| 11 | G4 | CI script with every case of *Approach* | `.github/scripts/check-agent-install.sh` | AC1, AC2, AC3, AC6, AC7, AC8, AC9, AC10 | 9 | Tester | [ ] |
| 12 | G4 | CI job `agent-install` | `.github/workflows/ci.yml` | AC8, AC9 | 11 | Dev | [ ] |
| 13 | G5 | Area documents, README files, INSTALL, ARCHITECTURE, project and stack entries | `docs/areas/agent.md`, `docs/areas/configuration-and-secrets.md`, `docs/areas/network-and-security-model.md`, `deploy/agent/README.md`, `README.md`, `docs/INSTALL.md`, `docs/ARCHITECTURE.md`, `.squad/project.md`, `.squad/stack.md` | AC9, AC10, AC11 | 12 | Dev | [ ] |
| 14 | G5 | Test documentation | `docs/UNIT_TESTS.md` | AC11 | 12 | Tester | [ ] |
