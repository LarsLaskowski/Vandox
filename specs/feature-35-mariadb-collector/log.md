# Log: feature 35-mariadb-collector

One line per pipeline step: date and time of day (UTC), step, member, result. Lead decisions and escalations
are quoted in full.

| Date | Step | Member | Result |
| ---- | ---- | ------ | ------ |
| 2026-10-20 20:39 | 1 Intake | Orchestrator | feature: user request (issue #35, label type: feature); branch feature-35-mariadb-collector off main |
| 2026-10-20 20:48 | 2a Spec | squad-lead | DONE: spec.md, 11 scenarios, 15 criteria, plan-check --spec PASS (opus/medium · 180,381 tokens · 33 tool uses · 493 s) |
| 2026-10-20 20:51 | 2a Spec challenge | squad-devils-advocate | OBJECTIONS 3 major (M1 refused vs record 0043, M2 pre-greeting error packet, M3 protocol client/capture), 4 minor (opus/medium · 69,988 tokens · 21 tool uses · 209 s) |
| 2026-10-20 20:57 | 2a Spec revise | squad-lead | ESCALATE: source of MariaDB protocol sample; all 7 objections accepted, plan-check --spec PASS (opus/medium · 28,102 tokens · 27 tool uses · 124 s · continued) |
| 2026-10-20 20:57 | 2a Escalation | Product Manager | Protocol sample: (a) the user captures it on MariaDB 10.6 / Ubuntu 22.04; plan waits for the capture |
| 2026-10-21 05:27 | 2b Escalation follow-up | Orchestrator | Capture 1 received: server MariaDB 12.3.3 (ubu2204), but client switched to TLS after greeting, so the rest is encrypted and unusable; user asked to repeat with --skip-ssl |
| 2026-10-21 05:35 | 2b Escalation answer | Product Manager | Captures supplied (MariaDB 12.3.3-ubu2204, plaintext, --skip-ssl): status+variables+processlist, account check; hostname anonymised same-length; no captures of error cases 1698/1045/1040 available. Stored in testdata/mariadb/ |
| 2026-10-21 05:50 | 2b Plan | squad-lead | DONE: plan.md, tasks.md (7 groups), record 0118 Proposed; tier security; areas 6,7,10,11,12; spec closed (opus/medium · 139,852 tokens · 53 tool uses · 884 s · continued) |
| 2026-10-21 05:50 | 2b Capture fix | Orchestrator | Capture: replaced leaked file-name prefix 144 and server_uid by same-length placeholders (hex+text); remaining 144 are numeric values |
