# Log: Issue #115

One line per pipeline step: date, step, member, result. Lead decisions and escalations are quoted in full.

| Date | Step | Member | Result |
| ---- | ---- | ------ | ------ |
| 2026-10-05 | 1 Intake | Orchestrator | Issue #115 read (open, label squad, no comments); branch fix-issue-115-integration-surface-config off main cb14020 |
| 2026-10-05 | 2 Plan | Lead | RESULT: DONE, tier docs. Changes: (1) README.md Agent options and Backend options tables: drop the always-"—" Environment variable column, header becomes Key / Default / Description, all 8 rows keep key, default, description verbatim, Secrets table unchanged. (2) .squad/project.md Integration surface, under "A new or changed configuration option": example-file bullet states the option must be set explicitly (optional at its default) and that TestLoadAgent_Example and TestLoadBackend_Example load the files and fail on a missing key from AgentKeys/BackendKeys (0049); README bullet becomes "the Agent options or Backend options table in README.md (key, default, description); a non-secret option has no environment variable (0049), a secret is added as a new secret instead". AC1 project.md lists no env var for a config option; AC2 example-file bullet names both tests (they exist); AC3 README tables exactly Key/Default/Description, rows verbatim; AC4 diff only README.md, .squad/project.md, specs/issue-115/; AC5 config-check.py passes and Format check clean. Out of scope: docs/ARCHITECTURE.md line 186 ("and environment variables"), noted only. No decision record needed (0049 covers it). |
