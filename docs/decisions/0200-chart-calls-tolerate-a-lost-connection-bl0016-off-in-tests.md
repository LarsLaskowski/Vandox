# 0200: A chart draw on a lost connection ends quietly and is drawn again; BL0016 is off for test code

- **Status:** Proposed
- **Date:** 2026-11-11
- **Area:** Web UI
- **Source:** Product Manager request: move the backend to .NET 11 (feature dotnet-11-upgrade)
- **Supersedes:** —

## Context

The 11 SDK's rule BL0016 reports a JavaScript interop call outside a `try`/`catch`. `ChartInterop.RenderAsync` makes three
(import the module, create the chart, update it) and catches nothing, so a `JSDisconnectedException` escapes
`TimeSeriesChart.OnAfterRenderAsync` when the browser connection is gone; only the disposal tolerates it today. In the test
project the rule reports every method call on the hand-written doubles of the JavaScript runtime (`RecordingJsRuntime`,
`RecordingJsObjectReference`), which implement the runtime's interfaces and make no interop call. `tests/**` is outside the
cloud analysis (`ci.yml`, `sonar.exclusions`), as for S107 in the same `.editorconfig` section.

## Options considered

1. **Catch only `JSDisconnectedException` in `RenderAsync`, report whether the chart was drawn, and let the component mark
   the data drawn only then** (chosen) — the one failure that means "page gone" ends quietly; every other failure still reaches
   the caller; a draw that did not happen is retried on the next render.
2. **Catch in the component** — the rule reports the calls in `ChartInterop`, where they are made.
3. **Catch every exception** — would hide a script error with the connection still open.

For the test code:

4. **`BL0016` set to `none` in the `[tests/**.cs]` section** (chosen) — one line for doubles that make no interop call.
5. **`#pragma` around each call in five files** — the same effect, scattered; or `try`/`catch` around calls on a test double,
   which tests nothing.

## Decision

`ChartInterop.RenderAsync` returns whether it drew; a `JSDisconnectedException` from one of its calls ends it without an
exception and without a draw, and `TimeSeriesChart` remembers the data as drawn only after a draw. `BL0016` is off for
`tests/**.cs` only. The chart's behavior is in [web-ui](../areas/web-ui.md), *Charts*.

## Consequences

- Production code keeps BL0016 as an error; a new interop call outside a guard fails the build.
- The only behavior change is the quiet end of a draw on a lost connection.
