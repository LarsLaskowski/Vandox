# Tasks: Show the socket's UID and user in the new-listener findings

Plan: [plan.md](plan.md) — Status: Draft

| # | Group | Task | Files | Criteria | Depends on | Owner | Done |
| - | ----- | ---- | ----- | -------- | ---------- | ----- | ---- |
| 1 | G1 | Skeleton: `Utf8Text` and `Cut` public, `ReportLimits.MaxShownUserBytes`, the five `ReportText` properties (empty texts), `ReportComposer.UidPieces` returning no pieces | `src/Vandox.Core/LogParsing/Utf8Text.cs`, `src/Vandox.Core/Reports/ReportLimits.cs`, `src/Vandox.Backend/Reports/ReportText.cs`, `src/Vandox.Backend/Reports/ReportComposer.cs` | AC1–AC10 | — | Dev | [ ] |
| 2 | G1 | Tests first: `ReportSamples.Entry` parameters and `RichListeners`, golden files, the new and adapted tests of *Tests per criterion* | `tests/Vandox.Backend.Tests/ReportSamples.cs`, `MarkdownReportWriterTests.cs`, `ReportContentTests.cs`, `ForensicsReporterTests.cs`, `ReportDownloadEndpointTests.cs`, `testdata/report/forensics-en.md`, `testdata/report/forensics-de.md` | AC1–AC10 | 1 | Tester | [ ] |
| 3 | G1 | Implement the UID part, the texts in both languages and the cut | `src/Vandox.Backend/Reports/ReportComposer.cs`, `src/Vandox.Backend/Reports/ReportText.cs` | AC1–AC10 | 2 | Dev | [ ] |
| 4 | G1 | Measure the page's per-circuit figure (plan, fact 11); report to the Lead above 20 MB | scratch only | AC7 | 3 | Dev | [ ] |
| 5 | G1 | Documentation updates of the plan | `docs/areas/detection-and-reports.md`, `docs/areas/web-ui.md`, `.squad/project.md` | AC1–AC10 | 4 | Dev | [ ] |
