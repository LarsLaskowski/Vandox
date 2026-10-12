# Tasks: Web UI foundation

Plan: [plan.md](plan.md) — Status: Approved

Every documentation edit appears in exactly one row; the Lead's approval only touches status, index rows and bookkeeping.

| # | Task | Files | Tests (AC) | Owner | Done |
| - | ---- | ----- | ---------- | ----- | ---- |
| 1 | Skeleton: `SecurityHeadersMiddleware` (constants final), `ChartSeries`, `ChartData`, `ChartInterop`, `TimeSeriesChart` (final markup, code-behind throws); `_Imports.razor` usings; *Build* passes | `src/Vandox.Backend/Hosting/SecurityHeadersMiddleware.cs`, `src/Vandox.Backend/Components/Charts/*`, `src/Vandox.Backend/Components/_Imports.razor` | — | Dev | [x] |
| 2 | Tests first (fail on the skeleton), the four test doubles, the source scanner with its tests (plan *Source scanner*), and the header assertions added to `ServeUsesListenHookAndReportsUnavailableDatabase` | `tests/Vandox.Backend.Tests/{SecurityHeadersMiddlewareTests,ChartDataTests,ChartInteropTests,TimeSeriesChartTests,MainLayoutTests,WebUiContentTests,WebSourceScannerTests,ServeTests}.cs`, `WebSourceScanner.cs`, `RecordingJsRuntime.cs`, `RecordingJsObjectReference.cs`, `TestNavigationManager.cs`, `ComponentTestRenderer.cs` | AC2–AC12 | Tester | [x] |
| 3 | Serving and headers: `UseStaticWebAssets`, `MapStaticAssets`, `SecurityHeadersMiddleware` after port routing, framework frame-ancestors off | `src/Vandox.Backend/Hosting/ServeCommand.cs`, `SecurityHeadersMiddleware.cs` | AC1, AC7, AC8, AC9 | Dev | [x] |
| 4 | Page, layout, style sheet, scripts | `src/Vandox.Backend/Components/App.razor`, `Components/Layout/MainLayout.razor`, `wwwroot/app.css`, `wwwroot/js/app.js`, `wwwroot/js/charts.js` | AC2–AC6 | Dev | [x] |
| 5 | Brand copies (byte for byte), delete `wwwroot/favicon.svg`, vendor uPlot 1.6.32 (commit and SHA-256 checked) | `src/Vandox.Backend/wwwroot/brand/*.svg`, `wwwroot/lib/uplot/{uPlot.esm.js,uPlot.min.css,LICENSE}` | AC6, AC9 | Dev | [x] |
| 6 | Charts C# side | `src/Vandox.Backend/Components/Charts/*` | AC10–AC12 | Dev | [x] |
| 7 | CI: Sonar exclusion of the vendored code; smoke script checks assets and the policy (read from the `ContentSecurityPolicy` constant) from the image | `.github/workflows/ci.yml`, `.github/scripts/smoke-test-backend.sh` | CI *Release build check* | Dev | [x] |
| 8 | Backend host area | `docs/areas/backend-host.md` | — | Dev | [x] |
| 9 | Branding: Web UI bullet | `docs/BRANDING.md` | — | Dev | [x] |
| 10 | Project knowledge: security areas 12 and 13, integration surface item | `.squad/project.md` | — | Dev | [x] |
| 11 | Stack: row for browser scripts and style sheets under *Not checkable in a cloud session* | `.squad/stack.md` | — | Dev | [x] |
| 12 | Test doubles row "Blazor components (C#)" | `.squad/project.md` | — | Tester | [x] |
| 13 | AC1 pin and coverage pass (*Coverage gate*) | tests as needed | AC1 | Tester | [x] |
| 14 | Web UI area (revised after the plan review), its index row, ARCHITECTURE link, records 0098/0099 and index rows (Proposed) | `docs/areas/web-ui.md`, `docs/areas/README.md`, `docs/ARCHITECTURE.md`, `docs/decisions/0098-…`, `0099-…`, `docs/decisions/README.md` | — | Lead | [x] |
| 15 | Browser check: layout desktop/360 px, theme, chosen theme survives a navigation link and a reconnect, console without CSP violations, network panel own origin only, reconnect dialog | — | *Verification without tests* | Product Manager | [ ] |
