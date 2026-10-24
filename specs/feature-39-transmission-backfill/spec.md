# Spec: Transmission and backfill

Source: Issue #39
Status: Approved by Lead

## Problem

`vandox-agent` keeps every record it produces in its on-disk spool (#38), but nothing sends the spool: the spool fills up to
`spool.max_size` and then drops its oldest records with recorded gaps, so no data reaches `vandoxd` and v0.2.0 cannot ship
([Agent](../../docs/areas/agent.md), *Status*, *Spool and sending*). The operator needs the agent to deliver every record to the
backend's ingest endpoint and to survive the backend host being off — typically overnight, 22:00 to 09:00, a few times a year
([ARCHITECTURE](../../docs/ARCHITECTURE.md), *Offline behavior and backfill*) — without a gap in the stored history. After such an
outage the backlog must not push current data back: the backend tells live from backfilled data by how late a record arrives
(record 0022, #41), so current data that waits behind hours of backlog would be classified as backfill and never alert. The monitored
server has 2 GB of RAM and the link ends in a home network, so delivering the backlog must not load either of them beyond a limit the
operator chooses (record 0045, *Consequences*).

## Scenarios

The words below are those of the agent's log (`component=sender`, `key=value` lines in the journal), its configuration file, its
self-metrics and what the backend's ingest endpoint receives. "Acknowledged" means the backend answered a batch with a `2xx` status;
"the backend accepts batches" means it acknowledges them.

- S1: Given a running agent and a backend that accepts batches, when a collector's records are handed to the spool, then the agent
  sends them to the ingest endpoint in a batch with header `mode` `live` at the latest 10 seconds after the hand-over, every batch a
  gzip stream of the wire format with the agent ID, the boot ID and the clock offset under which its records were captured and
  strictly increasing sequence numbers; once the backend acknowledges the batch the agent marks its records acknowledged in the spool,
  never sends them again, and the spool deletes each data file whose records are all acknowledged.
- S2: Given a running agent, when the backend becomes unreachable (the backend host is switched off at 22:00), then the agent logs the
  warning `backend not accepting batches` once with a `reason`, keeps collecting into its spool, and tries again after waits that start
  at about 1 second and double with every failed attempt up to 60 seconds, each wait drawn at random between half and the whole of its
  value, so a backend that is off for hours costs at most about one attempt a minute. The two streams share one episode and one wait:
  a failure of either starts or extends the episode, one warning is logged for it, and while it lasts only one attempt is made at a
  time, a `live` batch.
- S3: Given the backend was unreachable for hours (until 09:00), when it accepts a batch again, then the agent logs the info `backend
  accepting batches again`. Each attempt of the episode is a `live` batch of the records handed to the spool since the previous attempt
  started (up to the batch bound, oldest of them first), so the first batch the backend acknowledges holds only records handed over
  after the start of the last failed attempt, at most one wait (60 seconds) before it was sent. Only after that acknowledgement the agent
  logs the info `backfill started` and sends the backlog — every unacknowledged record below that batch — in `backfill` batches, in the
  order of their sequence numbers, oldest first, while it goes on sending current data in `live` batches in parallel; when the backlog is
  delivered it logs the info `backfill finished` with the number of records it sent, and the backend has received every sequence number
  the spool issued in the outage.
- S4: Given a backfill runs, when new records are handed to the spool at up to the live rate (10 MiB of record lines in any 10 seconds,
  which covers the `logs` collector's largest run of 1,000 lines and 4 MiB of text every 2 seconds only as a burst), then each of them is
  still sent at the latest 10 seconds after its hand-over, whatever the size of the backlog: backfill never delays current data. Above
  the live rate the `live` stream sends full batches back to back; records are then late but in order, stay in the `live` stream and are
  never dropped.
- S5: Given the configuration sets `backend.backfill_bandwidth: 1MiB`, when the agent backfills, then the compressed bytes of its
  `backfill` batches stay within 1 MiB per second on average (with one batch as the allowed burst), while `live` batches are never
  throttled; without the key the limit is `256KiB` per second.
- S6: Given the agent starts (after an update, a crash or a reboot) with unacknowledged records in its spool, then it treats them as
  backlog: current data first, then the backlog as in S3; records the backend acknowledged before the stop are never sent again, and
  records sent without an answer before the stop are sent again and stored once by the backend.
- S7: Given the backend refuses the agent's token (the operator rotated `VANDOX_AGENT_TOKEN` on the backend only), when the agent sends,
  then it logs the warning `backend not accepting batches` with `reason` `token refused` and the `status`, keeps everything in the spool
  and keeps trying as in S2; once the token matches again (the agent restarted with the new one) the backlog is delivered as in S3.
- S8: Given a running agent, when the self-metrics collector takes its sample, then it records `agent_send_lag_seconds`: the sample's
  start minus the capture time of the newest record the backend acknowledged since the agent started — about the send interval while
  sending keeps up, growing while the backend is unreachable and falling back as soon as current data is delivered again, while
  `agent_spool_age_seconds` still shows the backlog.

## Errors and edge cases

- E1: The backend accepts the connection but never answers, or answers slowly: every request ends after at most 30 seconds (connecting,
  sending and the answer together) as a failed attempt with `reason` `timeout`, and the agent waits and tries again as in S2; a hanging
  request never holds up the collectors, the spool, the watchdog or the other stream.
- E2: The backend refuses one batch as invalid (status `400`, `413` or `422` with the header `Vandox-Refused: batch`, which only the
  ingest endpoint sends; the same statuses without it come from something else, such as a TLS listener reached with `http`, and count
  as E3): the stream that sent it splits it into two halves and
  sends them in turn, splitting again on every refusal, so the acceptable records are acknowledged and only the refused ones remain; a
  batch of one record that is refused is given up: the agent marks the record acknowledged in the spool, hands the spool a `gap` with
  cause `unknown`, source `agent`, `first_seq` and `last_seq` the record's number and `from`/`to` its capture time (to one second
  later), which is sent like any record, and logs the warning `record refused` with the `status`, `seq` and `kind`. A refused record
  therefore never stays unacknowledged and never stops other data. At most 10 records are given up per hour (on the monotonic clock);
  when the bound is reached, the stream that met the refusal stops and waits until the oldest give-up of the hour is an hour old, then
  sends the refused batch again; the warning `backend not accepting batches` with `reason` `refused` and the `status` is logged once
  for that wait, no acknowledgement of the other stream ends it, and the other stream goes on. A backend that refuses everything
  therefore costs at most 10 records an hour, each recorded as a gap, and a bounded number of requests; the rest stays in the spool.
  The bound is kept in memory, so each start of the agent allows 10 more.
- E3: Any other answer that is not `2xx` — `401` or `403` (`reason` `token refused`), `400`, `413` or `422` without `Vandox-Refused: batch`, `404` (a backend with an ingest listener but
  without the ingest endpoint, which answers `404` there today), `408`, `415`, `429`, a `3xx` redirect, a `5xx` — and a connection or TLS
  failure (`reason` `connection failed`, `tls failed`) count as the backend not accepting batches: nothing is acknowledged, both streams
  wait as in S2. A redirect is never followed. A backend that does not know the batch's format version must answer with such a status,
  never with `400`, `413` or `422`, so a version mismatch never gives up records (contract for #40).
- E4: The `https` backend presents a certificate the system does not trust: the attempt fails with `reason` `tls failed`; certificate
  verification is never switched off.
- E5: The answer carries a large or hostile body or headers: the agent reads at most 4 KiB of the body and 16 KiB of headers, never
  parses, stores or logs the body, and judges the answer by its status alone.
- E6: The proxy environment variables (`HTTP_PROXY`, `HTTPS_PROXY`, `NO_PROXY`, any case) are set on the server: the agent ignores them
  and connects only to the host and port of `backend.url`.
- E7: The answer of an acknowledged batch is lost (the connection breaks after the backend committed): the records are sent again later
  and the backend stores them once (record 0045); nothing is lost.
- E8: Acknowledged ranges stay bounded. Every reconnect of an episode during a backfill leaves the records between the backfill and the
  new `live` batches as one unacknowledged run (a hole) that the backfill fills oldest first; refused records leave none (E2). A
  backend that flaps many times during a long backfill would open a hole per reconnect, and the spool keeps at most 64 acknowledged
  ranges, after which it refuses acknowledgements and so stops deleting files. The sender therefore keeps at most 32 holes above the
  backfill's position: when a reconnect would open a 33rd, the `live` stream resumes where it stopped instead of jumping to the newest
  records, so current data is late (and classified as backfill by #41) until the backfill closes holes again, but nothing is lost, and
  the spool keeps deleting acknowledged files. Should the spool still refuse an acknowledgement (`spool: more than 64 acknowledged
  ranges`), the agent logs the warning `acknowledgement not kept` with the spool's error once per episode; the records stay
  unacknowledged and are sent again later.
- E9: The outage lasts longer than the spool holds (more than about 7 days at the reference stream): the spool drops its oldest records
  with `spool_dropped` gaps (existing behavior); the backfill delivers those gap records with the rest, so the backend sees the loss as
  recorded, never as normal data.
- E10: The backlog spans several boots or clock offsets: each batch holds the records of one capture context and carries that context's
  boot ID and clock offset, never the current boot's.
- E11: A single record of the largest size the wire format allows (a line of 1 MiB): it is sent alone in a batch, so sending always makes
  progress.
- E12: The memory pressure mode turns on during a backfill: the agent logs the info `backfill paused` and sends no `backfill` batch
  until the mode turns off (info `backfill resumed`); current data goes on as in S4.
- E13: Reading the spool fails (`spool: read: <system error text>`) or meets damaged data: damaged records are handled by the spool as
  today (never handed out, a `spool_dropped` gap); a failed read makes the agent log the warning `spool not readable` with the spool's
  error once per episode and try that stream again after a wait as in S2, while the other stream goes on.
- E14: The agent stops (SIGTERM) during a send: requests in flight are abandoned, their records stay unacknowledged and are sent after the
  next start (S6); the sender delays the stop by at most 2 seconds. The sender has stopped before the spool is closed; an
  acknowledgement the closing spool refuses (`spool: closed`) is part of the stop and logs nothing.
- E15: The wall clock is set back or jumps forward: the waits run on the monotonic clock, and `agent_send_lag_seconds` is never negative
  (0 when the newest acknowledged capture time lies after the sample's start).
- E16: The backend becomes unreachable again during a backfill: the backfill stops with the current batch unacknowledged, both streams
  wait as in S2, and once batches are accepted again current data goes first and the backfill continues from the oldest unacknowledged
  record.
- E17: A configuration value of `backend.backfill_bandwidth` outside its rule (`8KiB`, `2GiB`, `1.5MiB`, `256kb`, `0`, an empty
  string): the agent stops at start-up with exit code 1 and the error naming the key and the line, never the value: `must be a whole
  number of KiB or MiB from 16KiB to 1024MiB, such as 256KiB or 4MiB`.
- E18: The backend is unreachable when the agent starts: the agent starts as usual (`READY=1`), collects into its spool and tries as in
  S2; an unreachable backend is never a start-up error.

## Non-functional requirements

- **Batches.** A batch holds at most 1 MiB (1,048,576 bytes) of record lines counted without their line feeds — what the spool's read
  bound counts, which equals the wire format's line limit, so a single record of the largest size still fits and a read always makes
  progress — at most 20,000 records and the records of one capture context. Its decompressed size stays below the wire format's limits
  (`MaxBatchBytes` 16 MiB) and the backend can bound a request at a little over 1 MiB of compressed bytes. The wire format stays at 1.1;
  no new field or record kind.
- **Timing.** A record handed to the spool is sent at the latest 10 seconds after its hand-over while the backend accepts batches and
  answers each within 0.5 seconds and the input stays within the live rate (S4); retries wait 1 to 60 seconds with jitter (S2); every
  request ends within 30 seconds (E1). These values are fixed, not options.
- **Bounds on unacknowledged runs.** At most 32 holes above the backfill's position (E8) and at most 10 given-up records per hour (E2).
- **Concurrency.** At most two requests are in flight, one `live` and one `backfill`.
- **Memory and load.** The sender's memory does not grow with the backlog: it holds at most one batch per stream, read from the spool
  when it is sent; the plan fixes the bound with a measurement. Backfill pauses under the memory pressure mode (E12) and is throttled
  by `backend.backfill_bandwidth` (S5). The agent's footprint target (under 30 MB, under 1 % CPU on average) is measured in #44.
- **Security.** The agent token is sent only to `backend.url`, with every request, and never logged, written to the spool or put into an
  error text. The agent connects only to the host and port of `backend.url` (no proxy, no redirect), verifies the certificate of an
  `https` URL, reads at most 4 KiB of an answer's body and never parses or logs it. Plain `http` stays allowed because the tailnet
  encrypts and authenticates the transport (Configuration and secrets).
- **New configuration.** `backend.backfill_bandwidth`, default `256KiB` (bytes per second of compressed `backfill` request bodies), a
  whole number of KiB or MiB from `16KiB` to `1024MiB` (E17). Existing configuration files load unchanged.
- **Compatibility.** Today's backend publishes no ingest port (Backend host, *Compose file*), so the agent sees `connection failed`; a
  backend whose ingest listener is reachable but has no endpoint answers `404`. In both cases the agent keeps everything in its spool
  and keeps trying (E3), so installing the agent before the backend's ingest endpoint (#40) loses nothing within the spool's capacity.
- **Logs.** Through the agent's logger with `component=sender`, fixed texts only, never the URL, a host, an address, the token, an
  answer's body or a record: warning `backend not accepting batches` (`reason`: `timeout`, `connection failed`, `tls failed`, `token
  refused`, `refused` or `status`, with `status` for an HTTP answer; once per episode of both streams together and again when the
  reason changes), info `backend accepting batches again` (`seconds`: the episode's length), info `backfill started`, info `backfill
  finished` (`records`), info `backfill paused` and `backfill resumed`, warning `record refused` (`status`, `seq`, `kind`), warning
  `acknowledgement not kept` (`err`), warning `spool not readable` (`err`).
- **Metrics.** `agent_send_lag_seconds` gets its definition (S8, E15); before the first acknowledgement since the start it is the
  sample's start minus the agent's start.

## Acceptance criteria

- [ ] AC1: With a backend that acknowledges every batch at once, every record handed to the spool is received in a `live` batch at the
  latest 10 seconds after its hand-over, the batch decodes with the wire decoder, carries the records' agent ID, boot ID and clock
  offset and strictly increasing sequence numbers, and after the acknowledgement the records are acknowledged in the spool and never
  sent again (S1, E10).
- [ ] AC2: While the backend fails every attempt, the waits between attempts start at 1 second, double per failure up to 60 seconds,
  each lies between half and the whole of its value, only one attempt is in flight, and the warning `backend not accepting batches` is
  logged once per episode of both streams together and per reason, with `reason` `token refused` for `401` and `403`, `tls failed` for
  a TLS failure, `connection failed` for a refused connection, `timeout` for E1 and `status` with the code for every other non-`2xx`
  answer named in E3; a failure of the `backfill` stream during an episode of the `live` stream logs no second warning (S2, S7, E3, E4,
  E18).
- [ ] AC3: After a simulated outage of 13 hours with a varied stream of metric, log line and snapshot records, the backend has received every sequence number the
  spool issued, the spool holds no unacknowledged record, the first batch acknowledged after the outage is a `live` batch holding no
  record handed to the spool before the start of the last failed attempt, no `backfill` batch is sent before it is acknowledged, the
  `backfill` batches of the first backfill pass (no failure during it) carry the backlog in ascending sequence order, and the info lines
  `backend accepting batches again`, `backfill started` and `backfill finished` (with the right `records`) are logged once each; a
  second run with an outage during the backfill ends with the same completeness (S3, E9, E16).
- [ ] AC4: During a backfill of a backlog many times the size of one batch, against a backend that answers every request within 0.5
  seconds, while records are handed to the spool at the live rate (10 MiB of record lines in 10 seconds, in bursts of 1,000 records
  every 2 seconds), every record handed over after the backend accepts batches again is received at the latest 10 seconds after its
  hand-over, also when 10 MiB are handed over right after a send tick; above that rate the `live` batches go back to back, in order,
  and no record is dropped or moved to `backfill` (S4).
- [ ] AC5: With `backend.backfill_bandwidth` set to a value B, the compressed bytes of the `backfill` request bodies sent in any span of
  T seconds are at most B times T plus one batch, `live` batches are not counted and not delayed, and without the key B is `256KiB`
  (S5).
- [ ] AC6: `backend.backfill_bandwidth` accepts `16KiB` to `1024MiB` in whole KiB or MiB, is in `AgentKeys`, the example file and the
  README options table, and a value outside the rule stops the agent with exit code 1 and the error text of E17, without the value (E17).
- [ ] AC7: An agent started on a spool that holds unacknowledged records sends current data first and those records as backlog, never
  sends a record the spool holds acknowledged, and a record whose answer was lost is sent again (S6, E7).
- [ ] AC8: For a batch with one record the backend refuses (`400`, `413` or `422` with `Vandox-Refused: batch` whenever that record is
  in the batch), every other
  record of the batch is acknowledged, the refused record is acknowledged in the spool, a `gap` with cause `unknown` and that record's
  number as `first_seq` and `last_seq` is handed to the spool and received by the backend, `record refused` is logged once with its
  status, number and kind, and later `live` and `backfill` batches go on; the eleventh refused record within an hour is not given up
  and stays in the spool, its stream waits until a give-up slot frees while the other stream's acknowledgements go on; over two hours
  of a backlog holding 20 records the backend always refuses, at most 20 records are given up, the warning with `reason` `refused` is
  logged at most twice and at most 500 requests are sent; a `400` without the header gives nothing up and counts as E3 (E2, E3).
- [ ] AC9: A request that gets no answer ends after at most 30 seconds as a failed attempt, and while it hangs the other stream and a
  new hand-over to the spool go on (E1).
- [ ] AC10: The agent follows no redirect, uses no proxy from the environment, verifies an `https` certificate, sends the token only in
  its requests to `backend.url`, and judges an answer by its status after reading at most 4 KiB of its body; no log line of the sender
  contains the URL, its host, the token or the answer's body (E3, E4, E5, E6).
- [ ] AC11: An acknowledgement the spool refuses logs `acknowledgement not kept` once per episode and leaves the records to be sent
  again — the stream's position does not move, the batch is sent again only after a wait of the backoff schedule (never at once), and
  the backfill never moves past a record it left unacknowledged, so every such record is sent again —, and a failed spool read logs `spool not readable` once per episode and is retried after a wait while the other stream goes on
  (E8, E13).
- [ ] AC12: While the memory pressure mode is on no `backfill` batch is sent and `live` batches go on; `backfill paused` and `backfill
  resumed` are logged at the changes (E12).
- [ ] AC13: A record of the largest allowed size is sent alone in a batch and acknowledged; no batch holds more than 1 MiB of record
  lines (without their line feeds) or 20,000 records or two capture contexts (E10, E11).
- [ ] AC14: A stop during a send ends within 2 seconds of the sender's share, the sender has stopped before the spool is closed, an
  acknowledgement refused with `spool: closed` logs nothing, the batch's records stay unacknowledged, and the next start sends them
  (E14).
- [ ] AC15: `agent_send_lag_seconds` is the sample's start minus the capture time of the newest acknowledged record since the start
  (before the first acknowledgement: minus the agent's start), never negative, and the waits use the monotonic clock (S8, E15).
- [ ] AC16: During one long backfill, against a backend that flaps (fails and accepts again) 100 times and refuses 100 single records
  spread over more than 10 hours, the spool never refuses an acknowledgement, never holds more than 32 holes above the backfill's
  position, deletes every sealed data file once its records are acknowledged, and at the end holds no unacknowledged record and no
  sealed data file (E2, E8).

## Out of scope

- The backend's ingest endpoint, its authentication, limits, deduplication and the acknowledgement after the commit (#40); this
  feature fixes the request and the answers the agent relies on, which #40 implements.
- The backend's live/backfill classification and gap tracking (#41); the header `mode` stays a hint.
- Measuring the clock offset; batches carry it when the spool holds one, which no part of the agent sets yet.
- Honoring a `Retry-After` header, options for the send interval, the waits or the timeout, compression other than gzip.
- The systemd unit and installation (#42), the Tailscale ACL (#43), the 24-hour footprint measurement (#44).
- Commands fetched from the backend (a later feature).

## Open questions

- Does the agent already send batches with sequence numbers and its boot ID (requirement 1)?
  Answered: partly. The spool issues strictly increasing numbers and keeps each record's boot ID and clock offset; `spool.Read` returns
  one capture context per result, and `wire.EncodeBatch` writes the gzip stream with the header's `boot_id` and checks the numbers
  (`internal/spool/read.go`, `internal/wire/encode.go`). Nothing sends yet. The header's boot ID is the boot of the capture (record
  0046), not the boot the agent sends in, so a backlog from an earlier boot keeps that boot's ID (E10).
- Is spooled data deleted only after acknowledgement (requirement 4)?
  Answered: yes for sending — the spool deletes a sealed file only when all its numbers are acknowledged (Agent, *Reading and
  acknowledging*) — except its documented drops for the size bound, the free-space reserve, the file limit and damage, each with a
  `spool_dropped` gap (E9). This feature acknowledges only after a `2xx` answer.
- What is the ingest request and what counts as an acknowledgement, while #40 does not exist yet?
  Answered: this feature fixes the agent's side of the contract — one gzip batch per request to the ingest endpoint under
  `backend.url`, authenticated with the agent token, a `2xx` answer acknowledging every record of the batch (the backend answers only
  after its commit, Storage, *Writing*) — and the plan records the path, the header and the status mapping in a decision record that #40
  implements. Today the backend publishes no ingest port (Backend host, *Compose file*), and its ingest listener answers `404` where it
  is reached (*Listeners and routing*); E3 treats both as "not accepting".
- What happens to a batch the backend refuses as invalid?
  Answered: it is split until only the refused records remain, and each of those is given up with a recorded gap, at most 10 an hour
  (E2; see *Challenge*, M1). The backend's refusal of a single record is its definitive answer, so giving the record up with a gap keeps
  the guarantee *No data gaps unless explicitly recorded*; a version mismatch is kept out of this path by the contract (E3), and the
  hourly bound keeps a backend that refuses everything from emptying the spool.
- What does "chronologically" mean for the backlog?
  Answered: in the order of the sequence numbers, which is the order the records were handed to the spool; capture times within one
  hand-over may differ by up to a collector's run (a log line read late), and the backend sorts by capture time where it matters.
- Which default for the bandwidth limit?
  Answered: `256KiB` per second of compressed bytes. It moves the 13-hour backlog of the reference stream (about 113 MB uncompressed,
  Agent, *Capacity*) in minutes, stays far below a home connection's download rate and keeps the backend's write load below its
  measured HDD throughput of about 8,500 records a second (BENCHMARKS, *Storage write throughput*) at gzip's usual ratio for JSON
  lines; the ratio is not measured, so the plan measures the records per second at the default on the reference stream.
- What is "current data delivered within its normal interval"?
  Answered: a record is sent at the latest 10 seconds after its hand-over to the spool. That is within the interval of every sampling
  collector (`system` 30 seconds, 10 under memory pressure; `processes`, `connections`, `self`, `filesystem` and `clock` 60; `systemd` and `mariadb` 30, 10
  under pressure; `backups` 10) but longer than the 2 seconds of `kernel` and `logs`, whose records are events rather than samples
  (Agent, *Self-metrics*, *System metrics* and the collector sections). The backend's lateness check (#41) therefore has to allow the
  send interval plus a request's time as its tolerance; the plan names this coupling in the area document so #41 builds on it. A
  shorter interval would cost one request and one backend transaction more per interval for little gain, since no alerting exists
  before #41.

## Area documents

- [Agent](../../docs/areas/agent.md): *Spool and sending* gets the sending rules — the two streams, current data first, the batch
  bounds, the send interval and the live rate, the shared waits with jitter, the request time limit, the treatment of each answer
  class, the splitting and giving up of refused records, the bound on holes, the
  memory pressure pause, the throttle, the stop and the log lines; *Status* and *Self-metrics* lose "not implemented yet" and "once the
  sender exists"; *Connection* states that the agent uses no proxy and follows no redirect; *Logging* stays as is.
- [Configuration and secrets](../../docs/areas/configuration-and-secrets.md): the row and the rules of `backend.backfill_bandwidth`
  with its error text.
- [Wire format](../../docs/areas/wire-format.md): the definition of `agent_send_lag_seconds` in *Metric names*; the header `mode` as
  the agent sets it.
- [Backend host](../../docs/areas/backend-host.md): the ingest request and the answers the agent relies on, as the contract #40
  implements (marked planned until then).
- [Network and security model](../../docs/areas/network-and-security-model.md): no change expected; the plan's sweep confirms it.

## Challenge

- M1 (major; refused batches and flapping reconnects exhaust the spool's 64 acknowledged ranges, `internal/spool/ack.go` line 149):
  accepted. Refused records no longer stay unacknowledged: a refused batch is split until only the refused records remain, each is
  given up with a recorded `gap` (cause `unknown`, its number as the sequence range) and acknowledged in the spool, at most 10 an hour,
  beyond which a refusal counts as the backend not accepting batches (E2). Holes from reconnects are bounded at 32 above the
  backfill's position, after which the `live` stream resumes where it stopped (E8). The contract keeps a version mismatch out of the
  refusal statuses (E3). New AC16 drives 100 flaps and 100 refused records and asserts that no acknowledgement is refused and every
  acknowledged sealed file is deleted; AC8 is rewritten. Rejected alternatives: retrying refused records in later passes (the
  objection's cause), and giving up a whole refused batch (up to about 2,700 good records lost for one bad one).
- m1 (contents of the first `live` batch after an outage): accepted. Each attempt of an episode is a `live` batch of the records handed
  over since the previous attempt started, so the first acknowledged batch holds no record handed over before the start of the last
  failed attempt (S3); AC3 asserts it.
- m2 (the 10-second guarantee needs an input rate): accepted. The live rate is 10 MiB of record lines in any 10 seconds against a
  backend that answers within 0.5 seconds, which covers the `logs` collector's largest run as a burst; above it `live` batches go back to
  back, late but in order, never dropped or moved to backfill (S4, *Timing*); AC4 runs at that rate.
- m3 (today's backend publishes no ingest port): accepted. *Compatibility* and the open question name both `connection failed` (no
  published port) and `404` (a reachable listener without the endpoint).
- m4 (ascending order only for the first backfill pass): accepted in AC3, with a second run that interrupts the backfill and asserts
  completeness only.
- m5 (batch bound counted as the spool's read bound counts): accepted. *Batches* states 1,048,576 bytes of record lines without their
  line feeds, equal to the line limit, so a read always makes progress; AC13 says so.
- m6 (one shared wait and episode): accepted. S2 and AC2 state one episode, one wait and one warning for both streams, with one
  attempt in flight during an episode.
- m7 (the sender stops before the spool closes; `spool: closed` is part of the stop): accepted in E14 and AC14.
