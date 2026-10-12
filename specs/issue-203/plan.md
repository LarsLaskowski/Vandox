# Plan: Recognize disk-full and Too many connections lines of PHP, nginx, Apache and Plesk

Source: Issue #203
Status: Draft
Tier: security — the change adds signatures to the detection over stored external text, which is security area 10
(*Parsing of external input*, `Vandox.Core.Signatures`) of `.squad/project.md`, and its whole point is a guard (origin
condition plus `\A` anchor) against text that HTTP clients control.

## Problem / root cause

Not a bug: issue #21 deliberately narrowed the two signatures that read messages with remote text
(`src/Vandox.Core/Signatures/signatures.yaml`, `mariadb_too_many_connections` lines 30-34 and `disk_full` lines 57-61) to
MariaDB, Postfix and Dovecot, and record 0094 (option 16) left the web server and PHP formats to a later change. This is
that change.

Claims of the issue, checked:

- *`disk_full` recognizes a full disk only in MariaDB's own messages* — **confirmed**: `program: [mariadbd, mysqld]`,
  `source: [mariadb]`, pattern `\A(?:[^ \n]+: )?(?:Disk full \(|Disk is full writing ')`.
- *`Too many connections` only in Postfix's and Dovecot's MySQL client errors* — **confirmed**: the program list holds
  six `postfix/*` programs and `dovecot`, source `dovecot`, pattern anchored at `warning: connect to mysql server` or
  `auth-worker(N): Error: mysql(`.
- *Web server and Plesk error logs carry client-supplied text (nginx `request:`, `host:`, `referrer:` and requested
  paths, Apache `referer:`, PHP output in FastCGI stderr over several lines)* — **confirmed** in the sources: nginx's
  `ngx_log_error_core` writes `[level] pid#tid: *conn ` then the message, then `ngx_log_errno`'s ` (N: text)`, then the
  request handler's `, client: …, server: …, request: "…", upstream: "…", host: "…"` and `, referrer: "…"`; the FastCGI
  module logs stderr as `FastCGI sent in stderr: "%*s"` unescaped (line feeds kept); Apache 2.4's default format appends
  `, referer: …` after the message. The error log parser stores as `message` the text after the time stamp
  (`docs/areas/log-import.md`, *Plesk and web server error logs*, *Records*), so the message starts with the level, which
  the server writes. The Plesk panel log's format itself is unverified (record 0092).
- *A pattern that matches anywhere can be triggered by any HTTP client (record 0094)* — **confirmed**, and the existing
  test `BuiltInSignaturesClientTextInWebServerLogsYieldsNothing` already pins the requested-path form
  `open() "…/x" failed (28: No space left on device)" failed (2: …)`.
- *Check each format against the real logs* — the server's logs are not available to the squad. The formats were
  checked against the programs' sources instead (shallow clones in a scratch directory): nginx 1.18.0, Apache httpd
  2.4.52 (`server/log.c`, `mod_proxy_fcgi.c`, `mod_remoteip.c`), `mod_fcgid` (`fcgid_bucket.c`), php-src `PHP-7.4` and
  `PHP-8.3` (`main/main.c`, `main/streams/plain_wrapper.c`, `sapi/fpm/fpm/fpm_main.c`, `sapi/cgi/cgi_main.c`,
  `sapi/apache2handler`, `ext/mysqlnd/mysqlnd_wireprotocol.c`, `Zend/zend_exceptions.c`, `ext/pdo/pdo_dbh.c`). What the
  sources fix:
  - PHP log line: `PHP <type>:  <origin>: <text> in <file> on line <n>` (two spaces after the colon); `<type>` is
    `Warning` (E_WARNING), `Notice` (E_NOTICE), `Fatal error` (E_ERROR and uncaught exceptions); `<origin>` is
    `[class::]function()` of the active function, `Unknown` without one; a docref link is added only when `docref_root`
    is set (default empty, not read).
  - mysqli connect warning: `<origin>: (<sqlstate>/1040): Too many connections`; mysqlnd sets SQL state `08004` for
    1040 in the greeting, `HY000` is its default (`UNKNOWN_SQLSTATE`); PHP 8.1+ throws by default:
    `Uncaught mysqli_sql_exception: Too many connections in <file>:<line>`; PDO: `Uncaught PDOException:
    SQLSTATE[<sqlstate>] [1040] Too many connections`.
  - Write error: PHP 8 `Write of <n> bytes failed with errno=<e> <strerror>`, PHP 7.4 `write of …` (lower case), at
    E_NOTICE.
  - PHP-FPM sends each log message as `PHP message: <line>` on FastCGI stderr, several joined with `; ` (PHP 7.3+);
    php-cgi writes `<line>\n` without the prefix.
  - nginx: FastCGI stderr at level `error` with a connection number; file writes `pwrite()`, `write()`, `pwritev()`,
    `writev()` `"<path>" failed` at `crit` (`ngx_files.c`), `write() "<to>" failed` at `alert` (`ngx_copy_file`, WebDAV
    `COPY`), access log `write() to "<path>" failed` at `alert`; `open()`/`mkdir()` report requested or client paths.
  - Apache: `[<module>:<level>] [pid <p>[:tid <t>]] [client <ip>:<port>] <message>[, referer: <text>]`; the message is
    escaped (`ap_escape_errorlog_item`: a line feed becomes the two characters `\n`); `mod_proxy_fcgi` logs FastCGI
    stderr as `AH01071: Got error '<chunk>'` at `error` in chunks of its read buffer; `mod_fcgid` logs
    `mod_fcgid: stderr: <line>` at `warn`, one entry per line; PHP's Apache handler logs each message at the level of
    its type (`error`, `warn`, `notice`) under the module name `php` (PHP 8), `php7` (PHP 7), or none (`[:error]`)
    without `APLOG_USE_MODULE`; `mod_remoteip` stores the parsed address (`apr_sockaddr_ip_get`), never the header text.
  - Apache's own `(28)No space left on device:` is also printed for semaphore exhaustion (not a full disk).

Related observations (no defect): `sw-cp-server` writing nginx's format and Plesk's panel PHP (`sw-engine`) sending
`PHP message: ` lines are unverified (record 0092 already says so for the format).

## Acceptance criteria

All tests are in `tests/Vandox.Core.Tests/BuiltInSignaturesTests.cs` and use its helpers (`AssertDetected`,
`AssertNothing`, `AssertNoneOf`, `Detect`); messages are given as the error log parser stores them (from the level on).

- [ ] AC1: The catalog lists, in this order: `oom_kill`, `mariadb_crash`, `mariadb_recovery_start`,
  `mariadb_recovery_end`, `mariadb_too_many_connections`, `php_too_many_connections`, `service_start`, `service_stop`,
  `service_failed`, `disk_full`, `php_disk_full`, `nginx_disk_full`, `postfix_fatal`, `dovecot_fatal`, `boot`.
  Test: `BuiltInSignaturesCatalogListsTheSignaturesInOrder` (expected list adapted).
- [ ] AC2: `php_too_many_connections`, `php_disk_full` and `nginx_disk_full` have no event condition, an origin
  condition (sources only: the two PHP signatures `nginx-error`, `sw-cp-server-error`, `apache-error`; `nginx_disk_full`
  `nginx-error`, `sw-cp-server-error`; no program) and a pattern starting with `\A`. Test:
  `BuiltInSignaturesEverySignatureHasAnEventOrOriginCondition` with `_remoteTextSignatures` extended by the three IDs,
  plus `BuiltInSignaturesWebSignaturesReadOnlyTheWebServerSources` (asserts the exact `Sources` lists and empty
  `Programs` of the three).
- [ ] AC3: `php_too_many_connections` yields exactly one event without fields for each of these lines. Test:
  `BuiltInSignaturesPhpTooManyConnectionsMatchesMysqliAndPdoErrors(string source, string message)`, one row each:
  1. `nginx-error`: `[error] 2101#2101: *15 FastCGI sent in stderr: "PHP message: PHP Warning:  mysqli_connect(): (HY000/1040): Too many connections in /var/www/vhosts/example.org/httpdocs/db.php on line 3; PHP message: PHP Warning:  mysqli_query(): Argument #1 must be of type mysqli, bool given in /var/www/vhosts/example.org/httpdocs/db.php on line 5" while reading response header from upstream, client: 198.51.100.7, server: example.org, request: "GET / HTTP/1.1", upstream: "fastcgi://unix:/var/www/vhosts/system/example.org/php-fpm.sock:", host: "example.org", referrer: "https://example.org/"`
  2. `sw-cp-server-error`: `[error] 1201#0: *42 FastCGI sent in stderr: "PHP message: PHP Warning:  mysqli::__construct(): (08004/1040): Too many connections in /opt/psa/admin/plib/Db.php on line 9" while reading response header from upstream, client: 198.51.100.7, server: , request: "GET /admin/ HTTP/1.1", upstream: "fastcgi://unix:/var/run/sw-engine.sock:", host: "panel.example.org:8443"`
  3. `nginx-error`: `[error] 2101#2101: *16 FastCGI sent in stderr: "PHP message: PHP Fatal error:  Uncaught mysqli_sql_exception: Too many connections in /var/www/vhosts/example.org/httpdocs/db.php:3`
  4. `nginx-error`: `[error] 2101#2101: *17 FastCGI sent in stderr: "PHP message: PHP Fatal error:  Uncaught PDOException: SQLSTATE[08004] [1040] Too many connections in /var/www/vhosts/example.org/httpdocs/db.php:4`
  5. `apache-error`: `[proxy_fcgi:error] [pid 3101:tid 140231] [client 198.51.100.7:0] AH01071: Got error 'PHP message: PHP Fatal error:  Uncaught PDOException: SQLSTATE[HY000] [1040] Too many connections in /var/www/vhosts/example.org/httpdocs/wp-db.php:12', referer: https://example.org/`
  6. `apache-error`: `[fcgid:warn] [pid 3101:tid 140231] [client 2001:db8::7:51234] mod_fcgid: stderr: PHP Warning:  mysqli_real_connect(): (HY000/1040): Too many connections in /var/www/vhosts/example.org/httpdocs/db.php on line 4`
  7. `apache-error`: `[php:warn] [pid 901] [client 198.51.100.7:51234] PHP Warning:  mysqli_connect(): (HY000/1040): Too many connections in /var/www/vhosts/example.org/index.php on line 3` (the message of the existing row of `BuiltInSignaturesTooManyConnectionsIgnoresOtherLines`)
  8. `apache-error`: `[:error] [pid 901] [client 198.51.100.7:51234] PHP Fatal error:  Uncaught mysqli_sql_exception: Too many connections in /var/www/vhosts/example.org/index.php:3`
  9. `apache-error`: `[php7:warn] [pid 901:tid 140231] [client 198.51.100.7:51234] PHP Warning:  mysqli::real_connect(): (HY000/1040): Too many connections in /var/www/vhosts/example.org/index.php on line 3`
- [ ] AC4: `php_disk_full` yields exactly one event without fields for each of these lines. Test:
  `BuiltInSignaturesPhpDiskFullMatchesWriteErrors(string source, string message)`:
  1. `nginx-error`: `[error] 2101#2101: *15 FastCGI sent in stderr: "PHP message: PHP Notice:  fwrite(): Write of 8192 bytes failed with errno=28 No space left on device in /var/www/vhosts/example.org/httpdocs/cache.php on line 7" while reading response header from upstream, client: 198.51.100.7, server: example.org, request: "GET / HTTP/1.1", host: "example.org"`
  2. `nginx-error`: `[error] 2101#2101: *16 FastCGI sent in stderr: "PHP message: PHP Notice:  file_put_contents(): write of 4096 bytes failed with errno=28 No space left on device in /var/www/vhosts/example.org/httpdocs/cache.php on line 9"` (PHP 7.4)
  3. `sw-cp-server-error`: `[error] 1201#0: *42 FastCGI sent in stderr: "PHP message: PHP Notice:  SplFileObject::fwrite(): Write of 512 bytes failed with errno=28 No space left on device in /opt/psa/admin/plib/Cache.php on line 20"`
  4. `apache-error`: `[proxy_fcgi:error] [pid 3101:tid 140231] [client 198.51.100.7:0] AH01071: Got error 'PHP message: PHP Notice:  fwrite(): Write of 8192 bytes failed with errno=28 No space left on device in /var/www/vhosts/example.org/httpdocs/cache.php on line 7', referer: https://example.org/`
  5. `apache-error`: `[php:notice] [pid 901] [client 198.51.100.7:51234] PHP Notice:  fflush(): Write of 8192 bytes failed with errno=28 No space left on device in /var/www/vhosts/example.org/index.php on line 5`
  6. `apache-error`: `[fcgid:warn] [pid 3101:tid 140231] [client 198.51.100.7:51234] mod_fcgid: stderr: PHP Notice:  Unknown: Write of 512 bytes failed with errno=28 No space left on device in Unknown on line 0`
- [ ] AC5: `nginx_disk_full` yields exactly one event without fields for each of these lines. Test:
  `BuiltInSignaturesNginxDiskFullMatchesWriteErrors(string source, string message)`:
  1. `nginx-error`: `[crit] 2101#2101: *16 pwrite() "/var/lib/nginx/body/0000000001" failed (28: No space left on device), client: 198.51.100.7, server: example.org, request: "POST /upload.php HTTP/1.1", host: "example.org"` (the message of the existing row of `BuiltInSignaturesDiskFullIgnoresOtherLines`)
  2. `nginx-error`: `[crit] 2101#2101: *17 pwritev() "/var/cache/nginx/fastcgi_temp/3/00/0000000003" failed (28: No space left on device) while reading upstream, client: 198.51.100.7, server: example.org, request: "GET /report HTTP/1.1", upstream: "fastcgi://unix:/run/php/php8.1-fpm.sock:", host: "example.org"`
  3. `nginx-error`: `[crit] 2101#2101: *18 writev() "/var/cache/nginx/proxy_temp/1/00/0000000001" failed (28: No space left on device) while reading upstream, client: 198.51.100.7, server: example.org, request: "GET / HTTP/1.1", host: "example.org"`
  4. `nginx-error`: `[alert] 2101#2101: *19 write() to "/var/log/nginx/access.log" failed (28: No space left on device) while logging request, client: 198.51.100.7, server: example.org, request: "GET / HTTP/1.1", host: "example.org"`
  5. `sw-cp-server-error`: `[crit] 1201#0: pwrite() "/var/cache/sw-cp-server/x" failed (28: No space left on device)` (no connection number)
- [ ] AC6: A trigger text in a client-supplied field or a requested path gives **no event at all**. Test: the existing
  `BuiltInSignaturesClientTextInWebServerLogsYieldsNothing(string source, string message)` (`AssertNothing`), with these
  rows added. `T1` = `PHP message: PHP Warning:  mysqli_connect(): (HY000/1040): Too many connections`, `T2` =
  `PHP message: PHP Notice:  fwrite(): Write of 1 bytes failed with errno=28 No space left on device`, `T3` =
  `pwrite() "/x" failed (28: No space left on device)`; each of `T1`, `T2`, `T3` in each nginx field (rows a to e, so
  15 rows; the Tester may build them from `const` strings):
  - a. nginx `request:` — `[error] 2101#2101: *15 open() "/var/www/vhosts/example.org/httpdocs/x" failed (2: No such file or directory), client: 198.51.100.7, server: example.org, request: "GET /<T> HTTP/1.1", host: "example.org"`
  - b. nginx `host:` — the same line with `host: "<T>"`
  - c. nginx `referrer:` — the same line with `, referrer: "<T>"` appended
  - d. nginx `upstream:` — `[error] 2101#2101: *15 upstream timed out (110: Connection timed out) while reading response header from upstream, client: 198.51.100.7, server: example.org, request: "GET /x HTTP/1.1", upstream: "http://127.0.0.1:7080/<T>", host: "example.org"`
  - e. nginx requested path — `[error] 2101#2101: *15 "/var/www/vhosts/example.org/httpdocs/<T>/index.html" is not found (2: No such file or directory), client: 198.51.100.7, server: example.org, request: "GET /<T>/ HTTP/1.1", host: "example.org"`

  and these single rows:
  - f. requested path closing the quote, at `crit`: `[crit] 2101#2101: *16 stat() "/var/www/vhosts/example.org/httpdocs/x" failed (28: No space left on device)" failed (13: Permission denied), client: 198.51.100.7, server: example.org, request: "GET /x%22%20failed%20(28:%20No%20space%20left%20on%20device) HTTP/1.1"` (the existing `open()` row stays)
  - g. FastCGI stderr, trigger in a **later** PHP message: `[error] 2101#2101: *15 FastCGI sent in stderr: "PHP message: PHP Warning:  Undefined array key "q" in /var/www/vhosts/example.org/httpdocs/index.php on line 2; T1 in /x.php on line 1" while reading response header from upstream, client: 198.51.100.7` (and the same with `T2`)
  - h. FastCGI stderr, trigger **inside** the first PHP message: `[error] 2101#2101: *15 FastCGI sent in stderr: "PHP message: PHP Warning:  Undefined array key "T1" in /var/www/vhosts/example.org/httpdocs/index.php on line 2" while reading response header from upstream, client: 198.51.100.7`
  - i. FastCGI stderr, trigger after a real line feed in the message: `[error] 2101#2101: *15 FastCGI sent in stderr: "PHP message: PHP Warning:  Undefined array key "q` + line feed + `T1 in /x.php on line 1" in /var/www/vhosts/example.org/httpdocs/index.php on line 2"` — the guard is `\A` (a `^` would match after the line feed)
  - j. Apache `referer:` — `[proxy_fcgi:error] [pid 3101:tid 140231] [client 198.51.100.7:0] AH01071: Got error 'PHP message: PHP Warning:  Undefined variable $x in /var/www/vhosts/example.org/httpdocs/index.php on line 12', referer: T1` (and the same with `T2`)
  - k. Apache request path in the message — `[authz_core:error] [pid 3101:tid 140231] [client 198.51.100.7:0] AH01630: client denied by server configuration: /var/www/vhosts/example.org/httpdocs/T1`
  - l. Apache `mod_proxy_fcgi`, trigger in a later message after an earlier one: `[proxy_fcgi:error] [pid 3101:tid 140231] [client 198.51.100.7:0] AH01071: Got error 'PHP message: PHP Warning:  Undefined array key "q" in /x.php on line 2; T1 in /x.php on line 1'`
  - m. Apache client address that is no address: `[proxy_fcgi:error] [pid 3101:tid 140231] [client example.org:0] AH01071: Got error 'T1 in /x.php on line 1'` (the guard is the address class `[0-9A-Fa-f:.]`)
- [ ] AC7: Apache's escaped line feed (the two characters backslash and `n`) does not let a later PHP message count.
  Test: `BuiltInSignaturesApacheEscapedLineFeedReadsOnlyTheFirstPhpMessage(string message)`, rows
  `[proxy_fcgi:error] [pid 3101:tid 140231] [client 198.51.100.7:0] AH01071: Got error 'PHP message: PHP Warning:  x in /x.php on line 1\\nT1 in /x.php on line 2'`
  and the same with `T2`, where `\\n` stands for the C# literal `"\\n"` (backslash, `n`). The test asserts first that
  the message contains a backslash (`Assert.Contains("\\", message, …)`) and then that no event is detected. If the input
  had lost its backslash it would hold a real line feed and test the `\A` anchor of AC6 i instead; the backslash
  assertion makes that fail. The guard tested is the rule that the PHP text must follow `'PHP message: ` directly.
- [ ] AC8: Lines of the right writer with other errors give no event of the new signatures. Tests (`AssertNothing` unless
  noted):
  - `BuiltInSignaturesPhpTooManyConnectionsIgnoresOtherLines(string source, string message)`: the AC3 row 1 line on
    source `plesk-panel` and on `syslog`; `(HY000/1045): Access denied for user 'u'@'localhost' (using password: YES)`
    in place of the 1040 text; `Uncaught Exception: Too many connections` (another exception class);
    `Uncaught PDOException: SQLSTATE[HY000] [2002] Connection refused`; mysqli's `(HY000/1040)` without the text
    `Too many connections` (`(HY000/1040): x`); an nginx FastCGI line at level `warn` instead of `error`.
  - `BuiltInSignaturesPhpDiskFullIgnoresOtherLines(string source, string message)`: `errno=122 Disk quota exceeded`;
    `Read of 8192 bytes failed with errno=28 No space left on device`; `PHP Warning:  file_put_contents(): Only 0 of
    4096 bytes written, possibly out of free disk space`; the AC4 row 1 line on source `plesk-panel`; the existing
    `plesk-panel` row of `BuiltInSignaturesDiskFullIgnoresOtherLines` stays where it is.
  - `BuiltInSignaturesNginxDiskFullIgnoresOtherLines(string source, string message)`: the AC5 row 1 line on source
    `apache-error`; at level `error` (`[error] 2101#2101: *16 pwrite() "/x" failed (28: No space left on device)`);
    `open()` of a temporary file (`[crit] 2101#2101: *16 open() "/var/lib/nginx/body/0000000001" failed (28: No space left on device)`, option 20);
    `mkdir()` (`[emerg] 2101#2101: mkdir() "/var/lib/nginx/body" failed (28: No space left on device)`); another error
    of a write (`(5: Input/output error)`); Apache's own status
    (`apache-error`: `[core:error] [pid 812] (28)No space left on device: AH00023: Couldn't create the ssl-cache mutex`).
- [ ] AC9: Lines parsed by the error log parser give the events with their records, and the copies of one full disk
  within 60 seconds are one event. Test: `BuiltInSignaturesWebServerErrorLogsYieldPhpAndNginxEvents` (async). It parses
  inline content (no new fixture file) with `ErrorLogParser` (nginx and Apache formats, `DateTimeZone.Utc`, as
  `BuiltInSignaturesErrorLogFixturesYieldNothing` does) and runs the catalog over the records of both, with IDs unique
  across the two files (nginx 1 to n, Apache n+1 on), ordered by capture time and ID. nginx content:
  ```
  2026/10/12 10:00:00 [crit] 2101#2101: *16 pwrite() "/var/lib/nginx/body/0000000001" failed (28: No space left on device), client: 198.51.100.7, server: example.org, request: "POST /upload.php HTTP/1.1", host: "example.org"
  2026/10/12 10:00:01 [crit] 2101#2101: *17 pwrite() "/var/lib/nginx/body/0000000002" failed (28: No space left on device), client: 198.51.100.8, server: example.org, request: "POST /upload.php HTTP/1.1", host: "example.org"
  2026/10/12 10:00:02 [error] 2101#2101: *18 FastCGI sent in stderr: "PHP message: PHP Fatal error:  Uncaught mysqli_sql_exception: Too many connections in /var/www/vhosts/example.org/httpdocs/db.php:3
  Stack trace:
  #0 /var/www/vhosts/example.org/httpdocs/db.php(3): mysqli->__construct()
  #1 {main}
    thrown in /var/www/vhosts/example.org/httpdocs/db.php on line 3" while reading response header from upstream, client: 198.51.100.7, server: example.org, request: "GET / HTTP/1.1", host: "example.org"
  ```
  Apache content:
  ```
  [Mon Oct 12 10:00:03.123456 2026] [proxy_fcgi:error] [pid 3101:tid 140231] [client 198.51.100.7:0] AH01071: Got error 'PHP message: PHP Notice:  fwrite(): Write of 8192 bytes failed with errno=28 No space left on device in /var/www/vhosts/example.org/httpdocs/cache.php on line 7', referer: https://example.org/
  ```
  Expected: exactly three events, in this order: `nginx_disk_full` with the record IDs of the two `pwrite()` lines and
  `LineCount` 2; `php_too_many_connections` with the ID of the `Uncaught` line; `php_disk_full` with the ID of the
  Apache line. The raw lines of the stack trace give nothing.
- [ ] AC10: The existing claims about the old signatures still hold where a line now matches a new one:
  `BuiltInSignaturesTooManyConnectionsIgnoresOtherLines` asserts `AssertNoneOf("mariadb_too_many_connections", …)`
  instead of `AssertNothing` (its `apache-error` row now yields `php_too_many_connections`), and
  `BuiltInSignaturesDiskFullIgnoresOtherLines` asserts `AssertNoneOf("disk_full", …)` (its `nginx-error` row now yields
  `nginx_disk_full`); rows unchanged. `BuiltInSignaturesErrorLogFixturesYieldNothing` and
  `BuiltInSignaturesClientTextInWebServerLogsYieldsNothing` keep their rows and pass unchanged (the fixtures hold none
  of the new texts).

Tests first (step 5): AC1, AC2, AC3, AC4, AC5, AC9 and the AC10 assertion changes fail on the current catalog (unknown
IDs, no events). AC6, AC7 and AC8 are guards that pass on the current catalog already; they are written in step 6
(`.squad/routing.md`, step 5), before the Reviewer round.

## Approach

Three new entries in `src/Vandox.Core/Signatures/signatures.yaml` (record 0094, option 18), no code change. The exact
entries (YAML single-quoted scalars, so the quote before `PHP message` in Apache's `Got error '` is doubled):

`php_too_many_connections`, directly after `mariadb_too_many_connections`:

```yaml
  - id: php_too_many_connections
    description: PHP's mysqli or PDO could not connect to MariaDB because of Too many connections (error 1040), as nginx or Apache logged the first PHP message of a request.
    source: [nginx-error, sw-cp-server-error, apache-error]
    message: '\A(?:\[error\] [0-9]+#[0-9]+: \*[0-9]+ FastCGI sent in stderr: "PHP message: |\[proxy_fcgi:error\] \[pid [0-9]+(?::tid [0-9]+)?\] \[client [0-9A-Fa-f:.]+:[0-9]+\] AH01071: Got error ''PHP message: |\[fcgid:warn\] \[pid [0-9]+(?::tid [0-9]+)?\] \[client [0-9A-Fa-f:.]+:[0-9]+\] mod_fcgid: stderr: |\[(?:php[0-9]?)?:(?:error|warn|notice)\] \[pid [0-9]+(?::tid [0-9]+)?\] \[client [0-9A-Fa-f:.]+:[0-9]+\] )PHP (?:Warning:  [^ \n()]+\(\): \([0-9A-Z]{5}/1040\): Too many connections|Fatal error:  Uncaught (?:mysqli_sql_exception: Too many connections|PDOException: SQLSTATE\[[0-9A-Z]{5}\] \[1040\] Too many connections))'
```

`php_disk_full` and `nginx_disk_full`, directly after `disk_full`:

```yaml
  - id: php_disk_full
    description: PHP could not write a file because a file system was full (errno 28), as nginx or Apache logged the first PHP message of a request.
    source: [nginx-error, sw-cp-server-error, apache-error]
    message: '\A(?:\[error\] [0-9]+#[0-9]+: \*[0-9]+ FastCGI sent in stderr: "PHP message: |\[proxy_fcgi:error\] \[pid [0-9]+(?::tid [0-9]+)?\] \[client [0-9A-Fa-f:.]+:[0-9]+\] AH01071: Got error ''PHP message: |\[fcgid:warn\] \[pid [0-9]+(?::tid [0-9]+)?\] \[client [0-9A-Fa-f:.]+:[0-9]+\] mod_fcgid: stderr: |\[(?:php[0-9]?)?:(?:error|warn|notice)\] \[pid [0-9]+(?::tid [0-9]+)?\] \[client [0-9A-Fa-f:.]+:[0-9]+\] )PHP Notice:  (?:[^ \n()]+\(\)|Unknown): [Ww]rite of [0-9]+ bytes failed with errno=28 No space left on device'
  - id: nginx_disk_full
    description: nginx could not write a file because a file system was full (error 28).
    source: [nginx-error, sw-cp-server-error]
    message: '\A\[(?:crit|alert)\] [0-9]+#[0-9]+: (?:\*[0-9]+ )?(?:pwrite|write|pwritev|writev)\(\) (?:to )?"[^"\n]*" failed \(28: No space left on device\)'
```

The existing entries are unchanged. Every negated class excludes `\n`, digits are `[0-9]`, no construct the
non-backtracking engine refuses, each pattern well under 4,096 characters, source lists within 16 entries, IDs within
`^[a-z][a-z0-9_]{0,63}$`, descriptions under 1,024 bytes — all checked when the catalog loads, so a mistake fails every
test that touches `BuiltInSignatures.Catalog`.

### The guard and the forms it meets

The guard is the origin condition plus a pattern anchored with `\A` at the prefix the writer prints, running on .NET's
non-backtracking engine with the catalog's options (Multiline, CultureInvariant, ExplicitCapture; ordinal, case-sensitive,
first match only). Forms that reach it, and its behavior:

| Form | Example | Behavior |
| ---- | ------- | -------- |
| trigger in nginx `request:`, `host:`, `referrer:`, `upstream:` | AC6 a-d | no match: these fields follow the message; the prefix and the error text must sit at `\A` |
| trigger in a requested path inside the message | AC6 e, f; existing `open()` row | no match: only write calls are read (`open()`, `stat()`, `mkdir()` and paths in other texts are not), so the quote-closing path cannot supply the call |
| trigger in a later PHP message (`; `, Apache `\n` escape) | AC6 g, l; AC7 | no match: the PHP text must follow the first `PHP message: ` (or Apache's prefix) directly |
| trigger inside the first PHP message | AC6 h | no match: the first message's head (`PHP <type>:  <origin>: `) is fixed by PHP; an origin holds no space or parenthesis |
| trigger after a real line feed in the same record | AC6 i | no match: `\A`, not `^` |
| trigger in Apache `referer:` or a request path in Apache's message | AC6 j, k | no match: after the message / not after the prefix |
| Apache client address that is not an address | AC6 m | no match: `[0-9A-Fa-f:.]+:[0-9]+` |
| right text, wrong source (`plesk-panel`, `syslog`, `apache-error` for nginx) | AC8 | no match: origin condition |
| right text at a level nginx does not use for it | AC8 | no match: levels are part of the prefix |
| PHP output split by the writer (nginx keeps line feeds; `mod_fcgid` one entry per line; nginx and `mod_proxy_fcgi` log stderr per read-buffer chunk) with client text at the start of the new entry | (documented, not tested: indistinguishable from a real line) | **match**: residual, see *Security considerations* |
| WebDAV `COPY` destination or a variable access log path in a failed nginx write | (documented) | **match** when the write fails with another error: residual |
| raw lines (no time stamp) | the stack trace in AC9 | no match: their message starts with the line itself, never with the level prefix — unless forged, which is the residual of record 0092 |

## Options considered

Recorded in record 0094, options 17 to 22:

1. **Extend `disk_full` and `mariadb_too_many_connections`** (option 17) — lost: one pattern per signature runs every
   alternative on every listed writer; MariaDB's loose `\A(?:[^ \n]+: )?Disk full \(` would read Apache's raw lines
   (stderr of programs PHP starts, `prog: <client-chosen name>: …`) without any line feed. A catalog format with several
   origin/pattern pairs per signature would avoid it but changes the catalog format and detector.
2. **One signature per writer and condition** (option 18, chosen).
3. **Allow the PHP error after earlier PHP messages** (option 19) — lost: earlier messages quote request data.
4. **Read every nginx call with `(28: …)`, including `open()` and `mkdir()`** (option 20) — lost: requested paths close
   the quote.
5. **Read Apache's own `(28)No space left on device:`** (option 21) — lost: also printed for semaphore exhaustion.
6. **Read the Plesk panel log** (option 22) — lost: unverified format, holds login names and request paths.

## Affected projects and types

| Project | Type / file | Change |
| ------- | ----------- | ------ |
| `Vandox.Core` | `src/Vandox.Core/Signatures/signatures.yaml` | three entries (Dev) |
| `Vandox.Core.Tests` | `tests/Vandox.Core.Tests/BuiltInSignaturesTests.cs` | new and adapted tests (Tester) |

## Signatures (for the Dev's skeleton)

None: no C# member is added or changed. Step 4 (skeleton) is not needed; the catalog is data.

## Test files

- `tests/Vandox.Core.Tests/BuiltInSignaturesTests.cs` (the existing test class of `BuiltInSignatures`, which loads the
  embedded catalog).

Existing test code affected by the change (no signature change): `BuiltInSignaturesCatalogListsTheSignaturesInOrder`,
`_remoteTextSignatures` and the summary of `BuiltInSignaturesEverySignatureHasAnEventOrOriginCondition` ("the two
that read messages with remote text" becomes "the signatures that read …"), `BuiltInSignaturesTooManyConnectionsIgnoresOtherLines`
and `BuiltInSignaturesDiskFullIgnoresOtherLines` (AC10) — adapted by the **Tester** in step 5.
`IncidentSignaturesTests` and `IncidentReconstructorTests` use the catalog but no line of their data holds a new
trigger text; they stay unchanged.

## Areas

`docs/areas/detection-and-reports.md` (*Log signatures*), owner **Dev**:

- Table: three rows after `mariadb_too_many_connections` and `disk_full` respectively:
  - `php_too_many_connections` | PHP's `mysqli` or PDO could not connect to MariaDB because of *Too many connections* (error 1040) | sources `nginx-error`, `sw-cp-server-error`, `apache-error`; message anchored with `\A` at the first PHP message after nginx's FastCGI prefix, Apache's `mod_proxy_fcgi` (`AH01071`) or `mod_fcgid` prefix, or a message of PHP's Apache module | none
  - `php_disk_full` | PHP could not write a file because a file system was full (`errno=28`) | as `php_too_many_connections` | none
  - `nginx_disk_full` | nginx could not write a file because a file system was full (`(28: No space left on device)` of `pwrite()`, `write()`, `pwritev()`, `writev()`) | sources `nginx-error`, `sw-cp-server-error`; message anchored with `\A` | none
- Replace the paragraph "`disk_full` reads only MariaDB's own error messages, … their messages carry external text before
  the error." with: the writers each signature reads; that the formats were checked against the sources of nginx
  1.18.0, Apache httpd 2.4.52, `mod_fcgid` and PHP 7.4 and 8.3, not against logs of the server; that only the first PHP
  message after the server's prefix is read (an error after an earlier PHP message of the same request is missed); that
  the Plesk panel log, Apache's own status texts (`(28)…` is also printed for exhausted semaphores), Apache 2.2, a custom
  `ErrorLogFormat`, nginx's `open()`/`mkdir()` and PHP's `Only 0 of N bytes written` are not read; and that one full disk
  seen by MariaDB and nginx gives one event per signature.
- Limitations: replace the bullet "The **writers are narrowed**: …" with one that states the residual of record 0094's
  new consequence: the anchor holds for one line, not for output the writer splits (nginx keeps the line feeds of PHP
  output; `mod_fcgid` logs each line of standard error as an entry; nginx and `mod_proxy_fcgi` log FastCGI standard
  error in pieces of their read buffer), so an HTTP client that makes an application log request data in a long or
  multi-line PHP message can create a PHP event; likewise a WebDAV `COPY` destination or a variable access log path in
  a failed nginx write, and an Apache client address that a reverse-proxy module copies unchecked from a forwarded header
  (`mod_remoteip` stores a parsed address; Plesk's module is not verified).

`docs/areas/log-import.md` (*Plesk and web server error logs*, *Forged lines (limitation)*), owner **Dev**: append
"Forged lines reach the signatures of PHP and nginx (*Detection, alerts and reports*, *Log signatures*)." to that
paragraph.

## Documentation updates

- The area documents above (Dev).
- `.squad/project.md`, security area 10, owner **Dev** (the change makes the entry untrue): replace
  "a signature without an event condition that reads a message which can hold remote text (`disk_full`, `mariadb_too_many_connections`) carries an origin condition (`program` or `source`) and a `\A` anchor at server-written text, so remote text in web server or
  mail lines cannot trigger it;"
  with
  "a signature without an event condition that reads a message which can hold remote text (`disk_full`, `mariadb_too_many_connections`, `php_too_many_connections`, `php_disk_full`, `nginx_disk_full`) carries an origin condition (`program` or `source`) and a `\A` anchor at server-written text, so remote text within one web server or
  mail line cannot trigger it, while PHP output that nginx, `mod_fcgid` or `mod_proxy_fcgi` splits into several entries can (record 0094);"
  keeping the line breaks of the surrounding paragraph as the editor likes.
- `docs/decisions/0094-…md` — extended by the Lead in this step (options 17 to 22, *Decision*, *Consequences*).
- No `README.md`, `docs/ARCHITECTURE.md` or configuration change: no option, no new component.

## Architecture check

No guarantee of `docs/ARCHITECTURE.md` or `.squad/project.md` (*Guarantees*) is touched: detection stays deterministic,
nothing is stored, no record changes, no live/backfill or gap behavior changes. The detector, its bounds (1,024 open
events, 256 IDs) and the linear-time engine are unchanged. Security area 10's statement is narrowed by text, as above,
not by a weaker mechanism for the existing signatures.

## Security considerations

- [x] Limits: none new. Patterns run on the non-backtracking engine in time linear in the length of the decoded
  message, which the parser has already cut to 16,384 UTF-8 bytes; no field is captured, so `NumberGuard` and the
  1,024-byte text cut are not involved.
- [x] Exceptions: none reaches a user. A catalog that breaks a rule throws `SignatureException` when
  `BuiltInSignatures.Catalog` loads, which every catalog test hits.
- Residuals (recorded in record 0094, *Consequences*, and the area document): (1) output that the writer splits into
  entries — nginx (and `sw-cp-server`) keep PHP's line feeds (the forged lines of record 0092), `mod_fcgid` logs one
  entry per line, nginx and `mod_proxy_fcgi` log FastCGI stderr in read-buffer chunks — lets a client whose request data
  an application logs in a long or multi-line PHP message create `php_too_many_connections` or `php_disk_full`;
  (2) a WebDAV `COPY` destination or an access log path with variables in a failed nginx write (needs the write to fail
  with another error) reaches `nginx_disk_full`; (3) an Apache reverse-proxy module that copies `X-Forwarded-For`
  unchecked into the client address would let that header supply the Apache prefix (`mod_remoteip` does not; Plesk's
  module unverified). Mitigation considered and rejected: anchoring the end of the PHP message (the fields after it are
  client text as well). Security reviews whether these residuals are acceptable for a classification that consumers
  weigh by its record IDs.

## Decision records

- `docs/decisions/0094-log-signatures-embedded-yaml-linear-patterns-on-stored-lines.md` — unreleased (no `v*` tag), so
  extended in place: options 17 to 22, *Decision* (options 18, 19, 20 added; the five anchored signatures), *Consequences*
  (what is read and not; the split-output residual), *Source* adds issue #203. Its index row is unchanged (title, status
  `Accepted`).

## Out of scope / follow-ups

- PHP-FPM's own error log, PHP's `error_log` files and syslog lines of PHP are not imported by any parser.
- The Plesk panel log's format (record 0092) — a signature there needs real lines first.
- None of these needs an issue now.
