# Tasks: Signature detection for known events

Plan: [plan.md](plan.md) — Status: Draft (revised after the challenge and the Security plan review)

Every documentation edit appears in exactly one row; the Lead's approval only touches status, index rows and bookkeeping.

| # | Task | Files | Tests (AC) | Owner | Done |
| - | ---- | ----- | ---------- | ----- | ---- |
| 1 | Skeleton: the new types with the signatures of *Signatures (for the Dev's skeleton)*, bodies `throw new NotImplementedException();` with the skeleton pragma; the catalog file with the exact content of *Approach*; the embedded resource in the project file; *Format*, *Build* | `src/Vandox.Core/Signatures/BuiltInSignatures.cs`, `SignatureCatalog.cs`, `Signature.cs`, `SignatureField.cs`, `SignatureFieldType.cs`, `SignatureException.cs`, `DetectedEvent.cs`, `SignatureDetector.cs`, `signatures.yaml`; `src/Vandox.Core/Vandox.Core.csproj` | — | Dev | [ ] |
| 2 | Tests first: the tests named in AC1 to AC7 (those that can fail on the skeleton); a shared record builder only as `SignatureSamples` | `tests/Vandox.Core.Tests/BuiltInSignaturesTests.cs`, `SignatureCatalogTests.cs`, `SignatureDetectorTests.cs`, optional `SignatureSamples.cs` | AC1–AC7 | Tester | [ ] |
| 3 | Catalog loading and validation (`SignatureCatalog.Parse` with the `source` key, `BuiltInSignatures.Catalog`, read-only lists), keeping each method below cognitive complexity 15 (helpers in their own files) | `src/Vandox.Core/Signatures/*` | AC1, AC5, AC6 | Dev | [ ] |
| 4 | Detector: origin, event and message conditions, field conversion and the number guard (ordinal scan), merge key compared field by field, window by subtraction, emission order, caps, retained state as in plan *Approach* step 5, read-only output collections, errors | `src/Vandox.Core/Signatures/SignatureDetector.cs` and helpers | AC2, AC3, AC4, AC7 | Dev | [ ] |
| 5 | Area document: *Scope*, new section *Log signatures* with the limitations listed in plan *Areas* (DA 1, DA 2, N4, N5, the narrowed writers, MariaDB forged entries), *Related decisions* (0094), *Not here*, *Implementation* as in plan *Areas* | `docs/areas/detection-and-reports.md` | — | Dev | [ ] |
| 6 | Architecture: intro paragraph, `Vandox.Core` component list, stale "signatures" removed from the `internal/` bullet, 0094 in the *Data flow* records | `docs/ARCHITECTURE.md` | — | Dev | [ ] |
| 7 | Layout lines: `src/Vandox.Core` adds "log signatures" | `README.md`, `.squad/stack.md` | — | Dev | [ ] |
| 8 | Security area 10 entry for the signature detection (origin condition and `\A` anchor, number guard, residual reach); area 12 names the text fields of detected events; *Test doubles* row "log parsers (C#)" adds `SignatureSamples` if it exists | `.squad/project.md` | — | Dev | [ ] |
| 9 | Coverage pass: tests only for lines the *Coverage gate* reports uncovered, and the heap bound of AC7 if not yet written | `tests/Vandox.Core.Tests/*Signature*Tests.cs` | AC7 | Tester | [ ] |
| 10 | Decision record 0094 and its index row (Proposed now, revised for the remote-text decision, Accepted at approval) | `docs/decisions/0094-log-signatures-embedded-yaml-linear-patterns-on-stored-lines.md`, `docs/decisions/README.md` | — | Lead | [x] (Proposed) |
