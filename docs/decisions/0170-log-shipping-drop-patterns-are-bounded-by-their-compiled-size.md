# 0170: Log shipping drop patterns are bounded by their compiled size

- **Status:** Accepted
- **Date:** 2026-10-31
- **Area:** Agent log shipping
- **Source:** Issue #37
- **Supersedes:** —

## Context

The `logs` collector drops lines that match the operator's drop rules (`log_shipping.drop`) before it ships them. Every shipped
line is matched against every pattern, and a local writer chooses the lines, their length and how many arrive. A collector that falls
behind its interval records gaps, so the cost of matching one long line bounds how many lines a flood can push past the collector.
RE2 matches in time linear in the input, but that time still multiplies by the length of the compiled program: about 0.2 ms per
instruction on a 16 KiB line, measured on the development host.

## Options considered

1. **Rely on RE2's linear time and a bound on the pattern's text alone** — no new limit, but a short pattern can compile to a long
   program: at 1,000 instructions a pattern costs up to 300 ms a line, and a flood of long lines pushes the collector into gaps.
2. **Bound each pattern's compiled program and the patterns' programs together** (chosen) — the cost of a line has a fixed ceiling
   whatever the operator writes; the price is that the rules fit about a dozen ordinary patterns.

## Decision

Option 2: a drop pattern is refused at load time when its compiled program, or the programs of all patterns together, exceed a fixed
instruction bound. The bounds and their error texts are in [Configuration options](../areas/configuration-options.md)
(`log_shipping`), the filter in [Agent log shipping](../areas/agent-log-shipping.md).

## Consequences

- A configuration with many or complex drop patterns is refused at start-up instead of slowing the collector at run time.
- Raising the bound needs a new measurement of the cost per instruction on the server.
