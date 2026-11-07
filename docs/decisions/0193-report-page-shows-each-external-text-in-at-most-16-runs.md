# 0193: The report page shows each external text in at most 16 runs

- **Status:** Proposed
- **Date:** 2026-11-07
- **Area:** Report page
- **Source:** Issue #356
- **Supersedes:** —

## Context

The report page keeps the document of one run per circuit, and every external text of it (listener commands, socket users, agent
IDs, the victims and programs of the outages) is shown through the runs of the shared external-text display
([Web UI](../areas/web-ui.md), *External text*), up to 256 runs a field. Each run is a string and a run object of its own, so a text
whose plain and escaped characters alternate costs far more than its characters: about 22.9 KiB for a command of 1,024 bytes
(measured in the plan review of #304), about 60 MB for the commands of the *New listeners* section alone, which
[0149](0149-new-listeners-section-capped-per-finding-kind-changed-endpoints-without-command.md) left as a follow-up. The number of
circuits is not bounded and the container has 512 MB.

## Options considered

1. **A lower cap of runs for the report page's external text** (chosen) — the shown characters stay the same; the text past the last
   run is written as one run, so only its styling is coarser. It bounds every external text of the page, the outage texts
   included, and the render tree of each text with it.
2. **A shorter shown length of a command on the page** — the run count, not the characters, is the cost: an alternating command cut to
   1,024 shown characters still fills about 146 runs, and the page would cut commands that the Markdown download shows whole.
3. **Lower caps of findings** — already weighed and rejected in
   [0149](0149-new-listeners-section-capped-per-finding-kind-changed-endpoints-without-command.md): it shortens the forensic window of
   the section; the outage texts would stay unbounded by it.
4. **A cut of the command at composition, like the user name** — changes the Markdown report and the download as well and drops
   forensic content to bound a page cost.

## Decision

The report page builds the runs of every external text with a cap of its own, `ReportsView.MaxTextRuns` (16), instead of the shared 256;
the text, its character cap and its escapes are unchanged, and the last run, which holds the rest, is in the escape style whenever it
differs from the text it stands for. The resulting per-text bounds and the page's memory per circuit are in
[Report page](../areas/web-ui-report-page.md), *Limits and errors*.

## Consequences

- A text with more than 15 changes between plain and escaped characters shows its rest as one run, in the escape style whenever it
  differs from the text it stands for, plain characters included; what is shown stays the same characters as before. Inside that rest a
  literal backslash sequence cannot be told apart from a real escape by its style; an external text reaches this with 8 escaped
  characters at its start, and the characters themselves are still escaped and written as text.
- The rest run is marked escaped by its content, no longer by its length, which could mark a rest plain when its escapes and its cut
  cancelled out; this corrects every view that shows external text, not only the report page.
- The per-text cost is now set mostly by the characters (the stored text and up to 2,048 shown characters); lowering the page further
  would need a shorter shown length, which this record does not take.
- The logs and incidents views keep 256 runs a field; their memory figures count characters only and are restated in their own issue.
