# 0142: Process and program names follow the legacy derivation, after configurable literal grouping rules

- **Status:** Accepted
- **Date:** 2026-10-17
- **Area:** Agent system metrics
- **Source:** Issue #32
- **Supersedes:** —

## Context

Issue #32 asks the process collector
([0113](0113-process-collector-pss-from-smaps-rollup-top-processes-full-snapshot-under-memory-pressure.md)) to sum every
program under configurable grouping rules. The backend's legacy log parser already names processes and programs, and the
incident and report code reads agent and imported snapshots alike, so the same process must get the same name from both.
Process names and command lines are text any local user chooses; a process can blank its command line and `comm`.

## Options considered

1. **Program names.** A new naming scheme — the agent's and the legacy parser's names for the same process would differ;
   **the legacy parser's derivation, pinned by a fixture both languages read, with configurable rules applied first**
   (chosen). The default rule list is empty, so the names match the legacy log until an operator adds a rule (for example one
   program per PHP-FPM pool).
2. **Rule language.** Regular expressions — more surface in the configuration and in review for the examples of the issue,
   which all fit a literal match; **an exact command or a literal command-line prefix, optionally with the next word
   appended** (chosen).
3. **Configuration list of rules.** A compact string syntax per rule — a second grammar to parse and to document; **a list of
   mappings checked by the strict decoder like every section** (chosen, 0049).
4. **A process without any name** (empty command line and empty `comm`). Leave it out — the model requires a non-empty command
   and program, but leaving the process out would let a local user hide its memory from the program sums by blanking its name;
   **a fixed placeholder as the last fallback** (chosen), which holds no process text.

## Decision

A process's command and program are named by the legacy parser's derivation, after the configured literal grouping rules, and a
process without any name gets a fixed placeholder. The rules are in [Agent system metrics](../areas/agent-system-metrics.md)
(*Processes*, **Names**) and the option in [Configuration options](../areas/configuration-options.md).

## Consequences

- One fixture (`testdata/processes/names.json`) pins the derivation for the agent and the backend's legacy parser.
- A grouping rule with `append_next_word` takes the word from text any local user controls, so it can create programs; their
  number is bounded by the record budget like every other program name.
