# Log templates

## Scope

A **template** is the message of a log line with its variable parts replaced by placeholders, so that lines which differ only in
numbers, IDs, addresses or paths share one template. The backend keeps, per template, when it was first and last seen and how many
lines it had in each hour, and answers two questions from that: which templates are **new** in a time range, and which hours of a
template are **above its usual level**. The logs view shows them ([Logs view](web-ui-logs-view.md), *Template page*, *Search page*), the
incidents list the new templates and spikes of their window ([Incidents](incidents.md)); no command shows the new
templates or the usual-level hours yet.

## Normalization

The template of a message is built in one forward pass over the message, the same way for every source; the same message always gives
the same template, whatever was seen before. The pass takes time linear in the message, compares characters ordinally as ASCII (no
regular expression, no culture), keeps no state between messages, and builds its output in a buffer of a fixed 1,024 characters, so no
input can make it grow. The normalizer is a grouping, not a guard.

1. **Separators:** every white-space character and every other control character (Unicode category Cc) is a separator. A run of
   separators becomes one ASCII space; separators at the start and the end are dropped. A template therefore holds no line feed and no
   control character of category Cc. Format characters (category Cf, such as U+202E and the other bidirectional controls, or U+200B)
   are no separators and stay inside their token.
2. **Delimiters:** the ASCII characters `"` `'` `(` `)` `[` `]` `{` `}` `<` `>` `,` `;` `=` `#` `!` `?` `*` `&` `|` `^` `$` `\` and
   the backtick are kept as they are and end a token.
3. **Tokens:** a token is a run of characters that are neither separators nor delimiters. Trailing `.` and `:` characters are split off
   a token and kept as they are after its replacement; a token that is empty then stays empty.
4. **Rules:** each token is replaced by the first rule that applies; a token no rule applies to is kept as it is. "Digit" means an
   ASCII digit `0`–`9` throughout (a digit of another script is a letter here), "hex digit" `0`–`9`, `a`–`f`, `A`–`F`.

| # | Token | Becomes | Examples |
| - | ----- | ------- | -------- |
| a | a path: starts with `/` and is at least two characters long, or starts with `./`, `../` or `~/` | `<path>` | `/var/lib/mysql/ibdata1`, `./run.sh`, `~/x` |
| b | an e-mail address: exactly one `@`; before it one or more ASCII letters, digits or `.` `_` `%` `+` `-`; after it one or more labels of ASCII letters, digits and `-`, separated by single dots | `<email>` | `info@example.com`, `root@localhost` |
| c | an IPv4 address: four groups of one to three digits separated by dots, each 0 to 255, optionally followed by `/` and a prefix length of 0 to 32, or by `:` and a port of one to five digits | `<ip>` | `10.0.0.1`, `192.168.1.20:25`, `10.0.0.0/8` |
| d | an IPv6 address: only hex digits, `:` and `.`; at most seven `:` in total, and either `::` exactly once (never three colons in a row) or exactly seven `:`; every group between colons one to four hex digits (the groups at a `::` may be empty); the last group may instead be an IPv4 address of rule c without prefix or port | `<ip>` | `::1`, `fe80::1`, `2001:db8:0:0:0:0:0:1`, `::ffff:10.0.0.1` |
| e | a hex number with prefix: `0x` or `0X` followed by one or more hex digits | `<hex>` | `0x7f3a`, `0X1F` |
| f | a UUID: hex digits in groups of 8, 4, 4, 4 and 12, separated by `-` | `<id>` | `0123abcd-0123-4567-89ab-0123456789ab` |
| g | a number: at least one digit, and only digits and `.` `:` `/` `-` `+` `%` | `<num>` | `42`, `-5`, `2.0.0`, `10:15:32`, `2026-10-12`, `50%`, `256.1.1.1` |
| h | a number with a unit: one or more digits, optionally `.` and one or more digits, then one to four ASCII letters up to the end | `<num>` and the letters | `123kB`, `5s`, `1.5ms` |
| i | any other token that holds a `:`: it is split at every `:`, each part is replaced by rules a to h and j (an empty part stays empty), and the parts are joined with `:` again | the parts | `total-vm:123kB` → `total-vm:<num>kB`, `mail.example.com:25` → `mail.example.com:<num>`, `http://x/y` → `http:<path>` |
| j | any other token that holds a digit (hex strings such as queue IDs and hashes included) | `<id>` | `eth0`, `sda1`, `4F2A31C0B2`, `4Xb2Kq1Zz0z9`, `v2` |

5. **Text:** a lone surrogate (UTF-16 text that is not valid Unicode) becomes U+FFFD. The template is cut at a character boundary to at
   most **1,024 UTF-8 bytes** (counted on the finished template; the cut is the last step, so a cut template may end in a space); an
   empty message has the empty template.

For example `connect from unknown[203.0.113.7]` becomes `connect from unknown[<ip>]`, `4F2A31C0B2: from=<info@example.com>, size=1234`
becomes `<id>: from=<<email>>, size=<num>`, and `Out of memory: Killed process 1234 (mariadbd) total-vm:1234567kB, anon-rss:654321kB`
becomes `Out of memory: Killed process <num> (mariadbd) total-vm:<num>kB, anon-rss:<num>kB`. A literal `<num>` in a message cannot be
told from a placeholder; a template is a grouping key, not a reversible form of the message.

## Statistics

- A template is identified by the record's **source**, the line's **program** and the template text. The same text from two programs,
  or from the journal and `syslog`, is two templates, so copies of one line in several sources are counted per source.
- Every log line that is **stored** is counted once, in the transaction that stores it: its template's count, its first-seen time (the
  earliest capture time of its lines), its last-seen time (the latest) and the count of the UTC hour its capture time falls in (the hour
  starts at a full hour of Unix time; a capture time in the first, partial hour of the storable range belongs to the hour that starts at
  the beginning of that range). A record that is not stored (a resent agent record, a batch that fails or is rolled back) counts
  nothing. Lines arrive in any order: an older log imported later moves a template's first-seen time earlier.
- The times come from the records' capture times, which an agent or a log file supplies: they are **not trusted**. Any time within
  the storable range is counted as given, so a wrong or forged capture time puts a line into a wrong hour and can move a template's
  first or last seen time.
- Per source and hour the number of stored lines is kept as well; an hour in which a source has at least one line is **covered** for
  that source.
- The statistics are history: they are not changed when log lines are deleted. Retention keeps them as hourly rollups: the hours of a
  source and its templates are removed only past `retention.rollups_1h` (1,095 days by default) and only where no stored log line of
  that source lies in the hour (an imported line, or a line of a kept window, keeps its hour), and a template with no hour left is
  removed ([Retention and rollups](retention.md)). Lines stored before the statistics existed (a database of schema version 7) are not counted until `vandoxd templates`
  rebuilds the statistics.

## Queries

Every query except the template of a line takes a time range `from` to `to` (both required, within the storable range, `from` before
`to`) and a limit of 1 to 10,000 results (1 to 1,000 for the templates of a range and the lines of a template); a broken rule is an
error that names the rule. Every value reaches the database as a bound parameter.

- **Template of a line:** given a source, a program and a message of at most 16,384 UTF-8 bytes (the longest message a log line
  can have; a longer one is an error), the template the message normalizes to, with its first and last seen time and count, or none.
- **Hourly counts** of one template: the hours that start in the range (from the hour that holds `from`, before `to`) in which the
  template has lines, with their counts, in time order. Hours without a line are not returned.
- **New templates:** the templates whose first-seen time lies in the range and whose source has a template first seen before `from`,
  optionally of one source, ordered by first-seen time, then by identity. A template of a source without earlier lines is not new but
  unknown, and is not returned: the first import of a source makes nothing "new".
- **Templates of a range:** the templates with at least one line in the hours that start in the range, optionally of one source and one
  program, each with its number of lines in those hours, most lines first, then by identity, at most 1,000 (the logs view asks for 200).
  The count is kept per UTC hour, so a range that starts or ends inside an hour counts that **whole hour**. A template is fetched by its
  number. The rules and error texts are in [Storage reading and log exploration](storage-reading.md), *Log exploration*.
- **Lines of a template:** the stored log lines of the template's source and program whose message normalizes to the template's text,
  found by normalizing candidates that the template's literal words select ([Storage reading and log exploration](storage-reading.md),
  *Log exploration*), so the result is exactly the lines the statistics counted as long as the normalization rules have not changed since
  they were counted; after a change of the rules without a rebuild, the lines of an old template are those that normalize to its text
  under the **current** rules, and may differ from its count.
- **Templates above their usual level:** for every template and hour that starts in the range (at most **744 hours**, 31 days,
  from `from` to `to`; the first hour is the one that holds `from`, so the hours counted can reach 745), optionally of one source:
  - the **baseline window** is the `baseline hours` before the hour (default **168**, one week; 1 to 2,160), clipped to the storable
    range; the hour itself is not part of it;
  - the **covered hours** are the hours of the window that are covered for the template's source; the template's **usual level** is its
    total count in the covered hours divided by their number (an hour without the template counts as zero);
  - the hour is reported when the source has at least `minimum baseline hours` covered hours (default **24**; 1 to `baseline hours`),
    the hour's count is at least `minimum count` (default **10**; at least 1), and the count is more than `factor` (default **3**; 1 to
    1,000) times the usual level. A template absent from every covered hour has the usual level 0 and is reported once it reaches the
    minimum count.
  - Each result has the template, the hour, the count, the usual level and the number of covered hours; results are ordered by hour,
    then by count (highest first), then by template identity, and the limit applies after these rules.

An hour of the window in which the source has no line at all is **unknown** and is left out, never counted as a quiet hour; with fewer
covered hours than the minimum no usual level is known and nothing is reported.

## Limits and errors

- Names without a digit (host, user, database and unit names, quoted values) stay literal, so lines that differ only in such a name
  are separate templates; random IDs without a digit are not recognized.
- A random ID is `<id>` only when it holds a digit; one that happens to have none stays literal, so the same message shape can give a
  second template for such lines.
- A hex value split by colons that is no IPv6 address (a MAC address) is replaced part by part, and a part such as `1a` keeps its
  letters (rule h), so such values are not one placeholder.
- Rule a takes any token that starts with `/`, rule g any digit-only shape (dates and times, versions, out-of-range addresses such as
  `256.1.1.1`); the placeholders are a grouping, not a validation of the value.
- Text without digits that varies per line (random words, as any local process or remote client can write into some logs) creates one
  template per line. The statistics grow by at most one template and one hourly count per stored line.
- Templates are **external text**: the normalizer removes control characters of category Cc, but keeps format characters (Cf, such as
  U+202E, which reverses the display direction, and U+200B), `<`, `&` and any other markup; whoever shows a template, or the template of
  a spike, escapes it like any log line and handles format characters (security area 12 of `.squad/project.md`).
- With retention ([Retention and rollups](retention.md)), the template statistics are kept as hourly rollups (1,095 days by default) and
  outlive the log lines (90 days by default): after the lines have expired, a template's text is the only trace of them.
- `vandoxd templates` recounts only the hours that begin at or after the start of the log period (`now − retention.logs`, rounded up to
  a full hour) and keeps the statistics of every earlier hour, so a rebuild after a change of the rules leaves the old templates in
  expired hours; with `retention.logs` `unlimited` it recounts every stored line ([Storage writing](storage-writing.md), *Template
  rebuild*).
- A change of these rules changes only the templates of lines stored afterwards until the statistics are rebuilt: after a rule change
  `vandoxd templates` recounts every stored line under the current rules, then `vandoxd incidents` is run again.
- While a rebuild runs and after an interrupted one (the `template_rebuild` row in `meta`, [Storage writing](storage-writing.md),
  *Template rebuild*), hours may hold **partial counts**: they count as covered with counts too low, so new-template and usual-level
  results are wrong, not unknown, until a run completes (the "unknown" rule of *Queries* covers only hours with no line of the source,
  as after the schema-7 upgrade).
- The memory of a rebuild does not grow with the stored lines: a batch of at most 10,000 lines holds a tally of at most that many keys
  of source (at most 128 UTF-8 bytes), program (at most 1,024) and template (at most 1,024), plus as many source hours, each with its own
  source text (about 46 MB of UTF-16 strings at 10,000 lines, plus the overhead of the maps), and one message (at most 16 KiB) is read at
  a time.
- The cost of writing log lines grows by the normalization and one statement per template and hour a batch touches; text without
  digits that varies per line costs three statements per line. The measurement for one million lines, and for such a hostile case, is
  in `docs/BENCHMARKS.md`.
- The **cost of the usual-level query is not bounded by its range or its limit.** It evaluates every template hour of the range whose
  count reaches the minimum count, all of them before the limit applies, and reads every template hour of the range and of the
  `baseline hours` before it once (the baseline sums are a window function per template); the covered hours are computed once per
  source and hour (the statement materializes them). Text without digits that varies per line makes one template per line, so ten copies
  of such a line in an hour make a candidate at the default minimum count. A deadline or cancellation stops a running query
  ([Storage](storage.md), *Database file and connections*) with no partial result: a caller that serves a user bounds it with a deadline
  and narrows the range and the source so that it can finish within it. The measured times at the default and the widest parameters are
  in `docs/BENCHMARKS.md`.

## Related decisions

- [0095](../decisions/0095-log-templates-token-rules-statistics-in-the-write-path-usual-level-from-covered-hours.md) — why templates
  come from fixed token rules, why the write path keeps their statistics per source and program, and why the usual level uses only the
  hours the source logged.
- [0097](../decisions/0097-reader-statements-interrupted-by-a-progress-handler-bound-to-the-token.md) — why a deadline or cancellation
  interrupts a running reader statement such as the usual-level query.

## Not here

- The tables of the statistics: [Storage](storage.md), *Schema*; how the write path counts a batch's lines and the rebuild by
  `vandoxd templates`: [Storage writing](storage-writing.md), *Template statistics* and *Template rebuild*; the command line of `vandoxd
  templates`: [Backend host](backend-host.md), *Command line and exit codes*.
- The search for the lines of a template and the rules of the logs view's queries: [Storage reading and log
  exploration](storage-reading.md), *Log exploration*; their display: [Logs view](web-ui-logs-view.md).
- Recognizing known kinds of events: [Log signatures](log-signatures.md).

## Implementation

`Vandox.Core.Templates` (`LogTemplateNormalizer`, with `TokenRules` and `TemplateWriter`) for the normalization; `Vandox.Storage`
(`BatchWriter`, `TemplateTally`, `TemplateQueries`, `SqliteStore` as `ILogTemplateReader`) for the statistics and the queries;
`Vandox.Storage` (`TemplateRebuilder`, `SqliteStore.RebuildTemplatesAsync`) for the template rebuild.
