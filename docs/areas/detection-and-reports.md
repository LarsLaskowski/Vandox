# Detection, alerts and reports

## Scope

How the backend turns stored records into findings: that detection is deterministic, which data may raise an alert, when the nightly
report is sent, and what the first release does and does not do. The rules, thresholds and signatures themselves are **not
implemented yet**; this document fixes the principles they are held to. How records get stored is in the storage area; how gaps are
recorded is in the agent area.

## Principles

- **Detection, incident reconstruction and alerting are deterministic**: rules, thresholds and log signatures. Every alert can be
  explained from rules and values and is covered by unit tests. A new failure pattern needs a new rule or signature.
- **AI is optional and only writes the nightly report.** It never decides whether an alert fires. Vandox works fully without any AI
  service.
- **Analysis comes before alerting.** The first release (v0.1.0) is the forensics release: collection, log import, spool and backfill,
  storage and the historical views needed to reconstruct outages. Alerting, the nightly report and remote actions build on it in later
  releases, with thresholds based on what the analysis showed. The data model must serve later rules without migration surprises.

## Live data and backfill

- The backend classifies **every record as live or backfilled** from its capture time (corrected by the batch's clock offset), its
  receive time and gaps in the sequence numbers. It does not trust a flag from the agent; the batch header's live/backfill mode is only
  a hint.
- **Alert rules are evaluated on live data only.** Backfilled data is stored and analyzed, appears in incidents and in the report, and
  never alerts, so there is no alert storm after the backend host was off.
- Imported data (origin `import`) is historical and never alerts.
- The classification depends on the agent's clock being reasonably correct; the threshold between live and backfilled is a tested
  constant.
- A recorded gap is *unknown*, never *normal*, in analysis, reports and the web UI.

## Nightly report

- The report is sent at **06:00**. If the backend host was off at that time, it is sent as soon as the backfill has completed, so it is
  always based on complete data. The backend therefore needs to know when a backfill is complete.
- The backend sends the report through the Telegram bot (see the notification area); writing it may use the optional AI.

## Service inventory

The inventory of running services on the monitored server may suggest services that are not needed. A service is only ever **disabled
in a reversible, documented way** (for example `systemctl disable` or `mask`; packages stay installed). Agents of the hosting provider
are **never** disabled or changed, and the inventory excludes them from every action, including remote actions. Mail is monitored at the
level of the mail services (process state, ports, logs), never individual mail accounts, so no mailbox credentials exist anywhere.

## Related decisions

- [0008](../decisions/0008-deterministic-detection-and-alerting.md) — why detection is deterministic and AI only writes the report.
- [0020](../decisions/0020-analysis-before-alerting-forensics-release.md) — why the first release is the forensics release.
- [0022](../decisions/0022-backfill-detection-and-live-only-alerts.md) — why the backend classifies live and backfilled data itself.
- [0024](../decisions/0024-nightly-report-timing.md) — why 06:00 or after the backfill.
- [0026](../decisions/0026-services-disabled-reversibly-only.md) — why services are only disabled reversibly.
- [0015](../decisions/0015-mail-services-checked-not-mail-accounts.md) — why mail services are checked, not mail accounts.

## Not here

- Telegram delivery and who may receive messages: the notification area.
- How gaps are recorded and how batches are identified: the agent area and the wire format.
- Where records are stored: the storage area.
