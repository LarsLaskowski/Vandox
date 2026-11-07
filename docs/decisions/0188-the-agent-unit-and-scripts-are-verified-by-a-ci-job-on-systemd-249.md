# 0188: The agent unit and scripts are verified by a CI job on systemd 249

- **Status:** Accepted
- **Date:** 2026-10-27
- **Area:** Agent installation
- **Source:** Issue #42
- **Supersedes:** —

## Context

The target is Ubuntu 22.04 with systemd 249, which ignores a unit key it does not know with only a log line, so a hardening setting
can silently vanish. The cloud sessions have no systemd as PID 1. The CI code analysis (SonarQube Cloud) fails the quality gate on a
clear-text URL in a shell script (`shell:S5332`), following it through variables and command output and exempting loopback.

## Options considered

1. **The extracted Ubuntu 22.04 systemd package in the session** — its library path and the proxy make it fragile; unmeasured.
2. **A CI job on GitHub's `ubuntu-22.04` runner** (chosen), which boots systemd 249, installs a locally built agent from the bundle
   it packages, and runs `systemd-analyze verify` and `security` there; Go tests in `deploy/agent` pin the unit and the scripts'
   functions locally.

## Decision

Option 2. The job installs from the bundle it packages exactly as the release does, so the documented path is the tested one.

## Consequences

- The job depends on GitHub keeping an `ubuntu-22.04` runner; it fails when the runner's systemd is not 249.
- The job's script holds no clear-text URL. Suppressing S5332 (by the scanner's ignore criteria, which hide every later finding in the
  file and change the workflow, or by `# NOSONAR`, whose effect on the shell analyzer is unmeasured) and building the URL from pieces
  (evasion) were rejected: the backend is unreachable in CI and only rendered and validated, so an `https://` URL tests the same, and
  the address allow-list check takes address and port and opens a socket.
- The job does not exercise an `http://` backend URL; the Go tests of the scripts' functions cover both schemes.
