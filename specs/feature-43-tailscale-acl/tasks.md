# Tasks: Tailscale on the monitored server and its tailnet policy

Plan: [plan.md](plan.md) — Status: Draft

| # | Group | Task | Files | Criteria | Depends on | Owner | Done |
| - | ----- | ---- | ----- | -------- | ---------- | ----- | ---- |
| 1 | G1 | Skeleton: no new API (no production code) | — | — | — | Dev | [ ] |
| 2 | G1 | Tests first: the tests of *Tests per criterion*, failing on the current tree | `deploy/agent/tailnet_policy_test.go` | AC1–AC7 | 1 | Tester | [ ] |
| 3 | G1 | The example policy, *Approach* 1 | `deploy/agent/tailnet-policy.hujson` | AC1, AC3 | 2 | Dev | [ ] |
| 4 | G1 | README: policy step, routes step, join and settings, checks, reboot step, key expiry, *Checks on the server* rows (*Approach* 2–7) | `deploy/agent/README.md` | AC1–AC7 | 3 | Dev | [ ] |
| 5 | G1 | Area document, INSTALL.md, project.md area 2, stack.md *Layout* (*Documentation updates*) | `docs/areas/network-and-security-model.md`, `docs/INSTALL.md`, `.squad/project.md`, `.squad/stack.md` | AC3, AC7 | 4 | Dev | [ ] |
