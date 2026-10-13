# 0100: The report language comes from the flag `-lang` (`en` by default, or `de`); the texts are tables in code, not .NET resources or cultures

- **Status:** Accepted
- **Date:** 2026-10-13
- **Area:** Detection, alerts and reports
- **Source:** Issue #28
- **Supersedes:** —

## Context

The Product Manager wants the forensics report ([0098](0098-forensics-report-as-markdown-from-vandoxd-report.md)) in the user's
language, German or English to begin with. The repository has no localization: every output of `vandoxd` is English, the web UI
declares `lang="en"`, and the build sets `InvariantGlobalization` (all projects, `Directory.Build.props`) and
`SatelliteResourceLanguages` to `en` (`src/Vandox.Backend/Vandox.Backend.csproj`). In globalization-invariant mode .NET 8 and later
refuse to create any culture but the invariant one (`PredefinedCulturesOnly`, according to the .NET 8 breaking-change notes; not
run here). The web page of the report comes later and will choose the language per user.

## Options considered

Where the texts live:

1. **Two hand-written text tables in code, one per language, selected by the language code** (chosen) — no culture needed, every
   label checked by the compiler, a missing German label fails the build, numbers formatted by fixed rules.
2. **`.resx` resources with satellite assemblies and `CultureInfo`** — the .NET standard; but needs cultures (refused in invariant
   mode, so `InvariantGlobalization` would have to go for the whole backend), a satellite assembly per language that
   `SatelliteResourceLanguages` currently drops, and culture-dependent number and date formats nobody asked for.
3. **Markdown templates as embedded files** — wording editable without code, but placeholders become a format of their own to parse.

How the language is chosen:

4. **A flag of the sub-command, `-lang en|de`, default `en`** (chosen) — explicit per run, no configuration change; English like
   every other output of `vandoxd`; the web page later passes the user's choice to the same tables.
5. **A configuration option `report.language`** — one fixed choice per installation; a new option touches the strict schema, the
   example file and the README (integration surface) for something the web page will choose per user anyway.
6. **The environment (`LANG`)** — in the container usually unset or `C.UTF-8`, so it would select English by accident and German
   only by surprise.
7. **German by default** — the current operator reads German, but every other output and the documentation are English.

## Decision

Options 1 and 4: `vandoxd report` takes `-lang en` (default) or `-lang de`; any other value is a usage error. The texts are two tables
in code; only labels change with the language, never the rules, the external text or the digits. Times are written in UTC in one
format for both languages, numbers without thousands separators and with the language's decimal separator. The details are in
[Detection, alerts and reports](../areas/detection-and-reports.md) (*Forensics report*, *Language*) and
[Backend host](../areas/backend-host.md).

## Consequences

- A third language is a third table and one more accepted value.
- The rest of `vandoxd` (logs, usage, error texts) stays English.
- If the web UI later needs cultures for other reasons, option 2 can replace the tables without changing the report's output.
