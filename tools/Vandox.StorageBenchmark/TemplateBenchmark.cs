using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Runtime.InteropServices;

using Microsoft.Data.Sqlite;

using Vandox.Core.Model;
using Vandox.Core.Templates;
using Vandox.Storage;

namespace Vandox.StorageBenchmark;

/// <summary>
/// Measures writing log lines with their templates and the template queries (issue #22, <c>docs/BENCHMARKS.md</c>). The lines
/// are generated deterministically from the line index, so two runs write the same lines.
/// </summary>
[ExcludeFromCodeCoverage]
internal static class TemplateBenchmark
{
    #region Constants

    private const int ShapeCount = 24;
    private const int HostileLimit = 100000;
    private const int NormalKind = 0;
    private const int HostileKind = 1;
    private const int ConcentratedKind = 2;
    private const int QueryLimit = 10000;
    private const long SecondsInThirtyDays = 30L * 24 * 3600;
    private const double ReferenceCriterionMinutes = 10;
    private const int ReferenceLines = 1000000;
    private const int DefaultBatchSize = 10000;
    private const int RangeHours = 744;
    private const string Lowercase = "abcdefghijklmnopqrstuvwxyz";

    #endregion // Constants

    #region Fields

    private static readonly DateTimeOffset _start = new(2026, 9, 1, 0, 0, 0, TimeSpan.Zero);

    private static readonly string[] _users = [
                                                  "alice",
                                                  "bob",
                                                  "carol",
                                                  "dave",
                                                  "erin",
                                                  "frank",
                                                  "grace",
                                                  "heidi",
                                                  "ivan",
                                                  "judy",
                                                  "mallory",
                                                  "niaj",
                                                  "olivia",
                                                  "peggy",
                                                  "rupert",
                                                  "sybil",
                                                  "trent",
                                                  "victor",
                                                  "walter",
                                                  "zoe",
                                                  "admin",
                                                  "root",
                                                  "www",
                                                  "mail",
                                                  "backup",
                                                  "deploy",
                                                  "git",
                                                  "test",
                                                  "info",
                                                  "sales",
                                                  "support",
                                                  "billing",
                                                  "shop",
                                                  "blog",
                                                  "wiki",
                                                  "forum",
                                                  "news",
                                                  "dev",
                                                  "ops",
                                                  "monitor"
                                              ];

    private static readonly string[] _files = ["wp-login", "xmlrpc", "index", "admin", "config", "setup", "shell", "upload", "backup", "test", "info", "phpinfo", "cron", "api", "login", "readme"];

    private static readonly string[] _words = [
                                                  "alpha",
                                                  "bravo",
                                                  "charlie",
                                                  "delta",
                                                  "echo",
                                                  "foxtrot",
                                                  "golf",
                                                  "hotel",
                                                  "india",
                                                  "juliet",
                                                  "kilo",
                                                  "lima",
                                                  "mike",
                                                  "november",
                                                  "oscar",
                                                  "papa",
                                                  "quebec",
                                                  "romeo",
                                                  "sierra",
                                                  "tango",
                                                  "uniform",
                                                  "victor",
                                                  "whiskey",
                                                  "xray",
                                                  "yankee",
                                                  "zulu",
                                                  "amber",
                                                  "birch",
                                                  "cedar",
                                                  "dune",
                                                  "ember",
                                                  "fjord"
                                              ];

    #endregion // Fields

    #region Methods

    /// <summary>
    /// Runs the measurement.
    /// </summary>
    /// <param name="directory">The directory for the temporary databases</param>
    /// <param name="lines">The number of log lines</param>
    /// <param name="size">The number of lines per batch</param>
    /// <param name="cancellationToken">Cancels the measurement</param>
    /// <returns>A task that completes when the measurement is printed</returns>
    internal static async Task RunAsync(string directory, int lines, int size, CancellationToken cancellationToken)
    {
        var work = Path.Combine(directory, $"vandox-template-benchmark-{Guid.NewGuid():N}");

        Directory.CreateDirectory(work);

        try
        {
            Print($"{RuntimeInformation.OSDescription}; {RuntimeInformation.FrameworkDescription}; {Environment.ProcessorCount} logical CPUs");
            Print($"database below {directory}; {lines} log lines in batches of {size}, generated from the line index over 30 days, one transaction per batch");

            var normalizing = Normalize(lines, size);

            Print($"normalizing {lines} messages in memory: {normalizing:F0} ms");

            var main = Path.Combine(work, "main");

            Directory.CreateDirectory(main);

            var written = await WriteAsync(main, lines, size, NormalKind, cancellationToken).ConfigureAwait(false);

            await QueryAsync(main, lines, cancellationToken).ConfigureAwait(false);

            if (lines == ReferenceLines && size == DefaultBatchSize)
            {
                var verdict = written.TotalMilliseconds < ReferenceCriterionMinutes * 60000 ? "met on this host" : "NOT met on this host";

                Print($"criterion of record 0095 (1,000,000 log lines written with their templates in under {ReferenceCriterionMinutes:F0} minutes): {verdict} ({written.TotalMilliseconds / 60000:F1} minutes); only a run on the reference host (DS918+) counts");
            }
            else
            {
                Print($"no verdict: the criterion is {ReferenceLines} log lines with the default batch size of {DefaultBatchSize}");
            }

            var hostile = Path.Combine(work, "hostile");
            var hostileLines = Math.Min(lines, HostileLimit);

            Directory.CreateDirectory(hostile);
            Print($"hostile case: {hostileLines} lines whose messages hold no digit and differ per line (one template per line, three upserts each)");
            await WriteAsync(hostile, hostileLines, size, HostileKind, cancellationToken).ConfigureAwait(false);
            await QueryAsync(hostile, hostileLines, cancellationToken, true).ConfigureAwait(false);

            var concentrated = Path.Combine(work, "concentrated");
            var concentratedLines = hostileLines + StorageLimits.MaxBaselineHours;

            Directory.CreateDirectory(concentrated);
            Print($"concentrated case: {hostileLines} digitless lines that differ per line, all of one source in one hour, after {StorageLimits.MaxBaselineHours} hourly heartbeat lines of that source (one hour holds {hostileLines} candidates with a baseline of {StorageLimits.MaxBaselineHours} covered hours)");
            await WriteAsync(concentrated, concentratedLines, size, ConcentratedKind, cancellationToken).ConfigureAwait(false);
            await QueryAsync(concentrated, concentratedLines, cancellationToken, true, _start.AddHours(StorageLimits.MaxBaselineHours)).ConfigureAwait(false);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(work, true);
        }
    }

    /// <summary>
    /// Prints a line with the invariant culture.
    /// </summary>
    /// <param name="text">The text</param>
    private static void Print(FormattableString text)
    {
        Console.WriteLine(text.ToString(CultureInfo.InvariantCulture));
    }

    /// <summary>
    /// Measures the normalization of the messages of all lines without the database.
    /// </summary>
    /// <param name="lines">The number of lines</param>
    /// <param name="size">The number of lines generated at a time</param>
    /// <returns>The time spent in the normalizer in milliseconds</returns>
    private static double Normalize(int lines, int size)
    {
        var watch = new Stopwatch();
        var messages = new string[size];
        var checksum = 0;

        for (var first = 0; first < lines; first += size)
        {
            var count = Math.Min(size, lines - first);

            for (var offset = 0; offset < count; offset++)
            {
                messages[offset] = Line(first + offset, lines).Message;
            }

            watch.Start();

            for (var offset = 0; offset < count; offset++)
            {
                checksum += LogTemplateNormalizer.Normalize(messages[offset]).Length;
            }

            watch.Stop();
        }

        Print($"normalized {lines} messages to {checksum} characters of templates");

        return watch.Elapsed.TotalMilliseconds;
    }

    /// <summary>
    /// Writes the lines into a new database.
    /// </summary>
    /// <param name="folder">The folder of the database</param>
    /// <param name="lines">The number of lines</param>
    /// <param name="size">The number of lines per batch</param>
    /// <param name="kind">The kind of lines: normal, hostile or concentrated</param>
    /// <param name="cancellationToken">Cancels the write</param>
    /// <returns>The total write time</returns>
    private static async Task<TimeSpan> WriteAsync(string folder, int lines, int size, int kind, CancellationToken cancellationToken)
    {
        var times = new List<double>();
        var total = Stopwatch.StartNew();

        await using (var store = await SqliteStore.OpenAsync(folder, cancellationToken).ConfigureAwait(false))
        {
            for (var first = 0; first < lines; first += size)
            {
                var batch = Build(first, Math.Min(size, lines - first), lines, kind);
                var watch = Stopwatch.StartNew();

                await store.WriteBatchAsync(batch, cancellationToken).ConfigureAwait(false);
                watch.Stop();
                times.Add(watch.Elapsed.TotalMilliseconds);
            }
        }

        total.Stop();

        var sorted = times.Order().ToList();

        Print($"wrote {lines} log lines in {times.Count} batches: total {total.Elapsed.TotalMilliseconds:F0} ms ({lines / total.Elapsed.TotalSeconds:F0} lines/s, batch times only: {lines / (times.Sum() / 1000):F0} lines/s); median batch {sorted[sorted.Count / 2]:F0} ms, fastest {sorted[0]:F0} ms, slowest {sorted[^1]:F0} ms");

        return total.Elapsed;
    }

    /// <summary>
    /// Counts the rows and times the template queries on a written database.
    /// </summary>
    /// <param name="folder">The folder of the database</param>
    /// <param name="lines">The number of lines that were written</param>
    /// <param name="cancellationToken">Cancels the queries</param>
    /// <param name="hostile">Whether the database holds the hostile lines, for which only the widest query is timed</param>
    /// <param name="rangeEnd">The end of the 30 days, when the lines end elsewhere</param>
    /// <returns>A task that completes when the figures are printed</returns>
    private static async Task QueryAsync(string folder, int lines, CancellationToken cancellationToken, bool hostile = false, DateTimeOffset? rangeEnd = null)
    {
        await using var store = await SqliteStore.OpenAsync(folder, cancellationToken).ConfigureAwait(false);

        var end = rangeEnd ?? _start.AddSeconds(SecondsInThirtyDays);
        var to = end.AddHours(1);
        var from = to.AddHours(-RangeHours);

        Print($"{lines} lines: {Count(folder, "SELECT count(*) FROM log_templates")} templates, {Count(folder, "SELECT count(*) FROM log_template_hours")} template hours, {Count(folder, "SELECT count(*) FROM log_source_hours")} source hours");

        if (hostile)
        {
            await SpikesAsync(store, folder, "usual level, widest parameters (baseline 2160 h, minimum baseline 1 h, factor 1, minimum count 1)", Widest(from, to), cancellationToken).ConfigureAwait(false);

            return;
        }

        var watch = Stopwatch.StartNew();
        var newQuery = new NewTemplateQuery
                       {
                           From = _start.AddDays(15),
                           To = end,
                           Limit = QueryLimit
                       };
        var fresh = await store.NewTemplatesAsync(newQuery, cancellationToken).ConfigureAwait(false);

        watch.Stop();
        Print($"new templates (last 15 days, limit {QueryLimit}): {watch.Elapsed.TotalMilliseconds:F0} ms, {fresh.Count} results");

        await SpikesAsync(store,
                          folder,
                          "usual level, default parameters",
                          new TemplateSpikeQuery
                          {
                              From = from,
                              To = to,
                              Limit = QueryLimit
                          },
                          cancellationToken).ConfigureAwait(false);
        await SpikesAsync(store, folder, "usual level, widest parameters (baseline 2160 h, minimum baseline 1 h, factor 1, minimum count 1)", Widest(from, to), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Creates the usual-level query with the widest parameters.
    /// </summary>
    /// <param name="from">The start of the range</param>
    /// <param name="to">The end of the range</param>
    /// <returns>The query</returns>
    private static TemplateSpikeQuery Widest(DateTimeOffset from, DateTimeOffset to)
    {
        return new TemplateSpikeQuery
               {
                   From = from,
                   To = to,
                   BaselineHours = StorageLimits.MaxBaselineHours,
                   MinBaselineHours = 1,
                   Factor = 1,
                   MinCount = 1,
                   Limit = QueryLimit
               };
    }

    /// <summary>
    /// Times one usual-level query.
    /// </summary>
    /// <param name="store">The store</param>
    /// <param name="folder">The folder of the database</param>
    /// <param name="title">The name of the query in the output</param>
    /// <param name="query">The query</param>
    /// <param name="cancellationToken">Cancels the query</param>
    /// <returns>A task that completes when the time is printed</returns>
    private static async Task SpikesAsync(SqliteStore store, string folder, string title, TemplateSpikeQuery query, CancellationToken cancellationToken)
    {
        var candidates = Count(folder, string.Create(CultureInfo.InvariantCulture, $"SELECT count(*) FROM log_template_hours WHERE hour >= {StorageTimeNanoseconds(query.From)} AND hour < {StorageTimeNanoseconds(query.To)} AND count >= {query.MinCount}"));
        var watch = Stopwatch.StartNew();
        var spikes = await store.TemplateSpikesAsync(query, cancellationToken).ConfigureAwait(false);

        watch.Stop();
        Print($"{title}: {watch.Elapsed.TotalMilliseconds:F0} ms, {candidates} candidates, {spikes.Count} results");
    }

    /// <summary>
    /// Converts an instant to Unix nanoseconds.
    /// </summary>
    /// <param name="instant">The instant</param>
    /// <returns>The nanoseconds</returns>
    private static long StorageTimeNanoseconds(DateTimeOffset instant)
    {
        return (instant.UtcTicks - DateTimeOffset.UnixEpoch.UtcTicks) * 100;
    }

    /// <summary>
    /// Runs a counting query on the database file.
    /// </summary>
    /// <param name="folder">The folder of the database</param>
    /// <param name="sql">The query</param>
    /// <returns>The count</returns>
    private static long Count(string folder, string sql)
    {
        var builder = new SqliteConnectionStringBuilder
                      {
                          DataSource = Path.Combine(folder, StorageLimits.FileName),
                          Mode = SqliteOpenMode.ReadOnly,
                          Pooling = false
                      };

        using var connection = new SqliteConnection(builder.ToString());

        connection.Open();

        using var command = connection.CreateCommand();

        command.CommandText = sql;

        return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Builds a batch of generated log lines.
    /// </summary>
    /// <param name="first">The index of the first line</param>
    /// <param name="count">The number of lines</param>
    /// <param name="lines">The number of lines of the whole run</param>
    /// <param name="kind">The kind of lines: normal, hostile or concentrated</param>
    /// <returns>The batch</returns>
    private static RecordBatch Build(int first, int count, int lines, int kind)
    {
        var records = new DataRecord[count];

        for (var offset = 0; offset < count; offset++)
        {
            var index = first + offset;
            var (line, capturedAt) = Generate(index, lines, kind);

            records[offset] = new DataRecord
                              {
                                  Origin = RecordOrigin.Import,
                                  Source = line.Source,
                                  CapturedAt = capturedAt,
                                  Data = new LogLine
                                         {
                                             Log = line.Source,
                                             Host = "web-1",
                                             Program = line.Program,
                                             Message = line.Message
                                         }
                              };
        }

        return new RecordBatch
               {
                   ReceivedAt = _start.AddDays(31),
                   Records = records
               };
    }

    /// <summary>
    /// Generates a line and its capture time.
    /// </summary>
    /// <param name="index">The index of the line</param>
    /// <param name="lines">The number of lines</param>
    /// <param name="kind">The kind of lines: normal, hostile or concentrated</param>
    /// <returns>The line and its capture time</returns>
    private static ((string Source, string Program, string Message) Line, DateTimeOffset CapturedAt) Generate(int index, int lines, int kind)
    {
        var spread = _start.AddTicks(index * SecondsInThirtyDays / lines * TimeSpan.TicksPerSecond);

        if (kind == ConcentratedKind)
        {
            // One heartbeat per hour makes the baseline hours of the source covered; all other lines fall into the hour after them.
            return index < StorageLimits.MaxBaselineHours
                       ? (("nginx", "nginx", "heartbeat"), _start.AddHours(index))
                       : (Hostile(index - StorageLimits.MaxBaselineHours), _start.AddHours(StorageLimits.MaxBaselineHours).AddMinutes(5));
        }

        return kind == HostileKind ? (Hostile(index), spread) : (Line(index, lines), spread);
    }

    /// <summary>
    /// Generates a hostile line: words without a digit that are different for every index, so every line is a template of its own.
    /// </summary>
    /// <param name="index">The index of the line</param>
    /// <returns>The source, program and message</returns>
    private static (string Source, string Program, string Message) Hostile(int index)
    {
        var rest = index;
        var words = new string[4];

        for (var position = 0; position < words.Length; position++)
        {
            words[position] = _words[rest % _words.Length];
            rest /= _words.Length;
        }

        return ("nginx", "nginx", $"client {string.Join(' ', words)} sent an invalid host header");
    }

    /// <summary>
    /// Generates a line from its index: one of 24 shapes modeled on real log formats, with numbers, addresses, IDs and e-mail
    /// addresses that vary with the index.
    /// </summary>
    /// <param name="index">The index of the line</param>
    /// <param name="lines">The number of lines</param>
    /// <returns>The source, program and message</returns>
    private static (string Source, string Program, string Message) Line(int index, int lines)
    {
        var shape = index % ShapeCount;
        var seed = (ulong)index * 0x9E3779B97F4A7C15UL;
        var number = (int)((seed >> 33) % 100000);
        var other = (int)((seed >> 13) % 60000);
        var ip = string.Create(CultureInfo.InvariantCulture, $"{20 + ((seed >> 56) % 200)}.{(seed >> 48) % 256}.{(seed >> 40) % 256}.{((seed >> 32) % 254) + 1}");
        var id = (seed >> 20).ToString("X10", CultureInfo.InvariantCulture);
        var session = Session(seed);
        var user = _users[(int)((seed >> 24) % (ulong)_users.Length)];
        var file = _files[(int)((seed >> 28) % (ulong)_files.Length)];
        var seconds = index * SecondsInThirtyDays / lines;

        return shape switch
               {
                   0 => ("syslog", "postfix/smtpd", $"connect from unknown[{ip}]"),
                   1 => ("syslog", "postfix/smtpd", $"disconnect from unknown[{ip}] ehlo=1 mail=1 rcpt=0/1 quit=1 commands={number % 5}/4"),
                   2 => ("syslog", "postfix/smtpd", $"{id}: client=unknown[{ip}]"),
                   3 => ("syslog", "postfix/qmgr", $"{id}: from=<{user}@example.org>, size={number}, nrcpt=1 (queue active)"),
                   4 => ("syslog", "postfix/smtp", $"{id}: to=<{user}@example.net>, relay=mx.example.net[{ip}]:25, delay={number % 90}, delays=0.1/0/0.5/0.2, dsn=2.0.0, status=sent (250 2.0.0 Ok: queued as {id})"),
                   5 => ("syslog", "postfix/smtpd", $"warning: unknown[{ip}]: SASL LOGIN authentication failed: UGFzc3dvcmQ6"),
                   6 => ("journal", "dovecot", $"imap-login: Login: user=<{user}>, method=PLAIN, rip={ip}, lip=10.0.0.5, mpid={other}, TLS, session=<{session}>"),
                   7 => ("journal", "dovecot", $"imap({user})<{other}><{session}>: Disconnected: Logged out in={number} out={other}"),
                   8 => ("journal", "kernel", $"Out of memory: Killed process {other} (mariadbd) total-vm:{number}kB, anon-rss:{other}kB, file-rss:0kB, shmem-rss:0kB, UID:{number % 1000} pgtables:{number}kB oom_score_adj:0"),
                   9 => ("journal", "kernel", $"oom-kill:constraint=CONSTRAINT_NONE,nodemask=(null),cpuset=/,mems_allowed=0,global_oom,task_memcg=/system.slice/mariadb.service,task=mariadbd,pid={other},uid={number % 1000}"),
                   10 => ("mariadb", "mariadbd", $"2026-09-{1 + (seconds / 86400 % 30):D2} 10:{seconds / 60 % 60:D2}:{seconds % 60:D2} {other} [Warning] Aborted connection {number} to db: 'wordpress' user: 'wpuser' host: '{ip}' (Got timeout reading communication packets)"),
                   11 => ("mariadb", "mariadbd", $"[Warning] Access denied for user '{user}'@'{ip}' (using password: YES)"),
                   12 => ("nginx", "nginx", $"{other}#{other}: *{number} open() \"/var/www/html/{file}.php\" failed (2: No such file or directory), client: {ip}, server: example.org, request: \"GET /{file}.php HTTP/1.1\", host: \"example.org\""),
                   13 => ("nginx", "nginx", $"{other}#{other}: *{number} upstream timed out (110: Connection timed out) while reading response header from upstream, client: {ip}, server: example.org, request: \"POST /{file}.php HTTP/1.1\", upstream: \"fastcgi://unix:/run/php/php8.2-fpm.sock:\", host: \"example.org\""),
                   14 => ("journal", "systemd", $"Started Session {number} of User {user}."),
                   15 => ("journal", "systemd", $"Stopping User Manager for UID {number % 1000}..."),
                   16 => ("journal", "systemd", $"session-{number}.scope: Deactivated successfully."),
                   17 => ("syslog", "sshd", $"Failed password for invalid user {user} from {ip} port {other} ssh2"),
                   18 => ("syslog", "sshd", $"Accepted publickey for root from {ip} port {other} ssh2: RSA SHA256:{session}{id}"),
                   19 => ("syslog", "CRON", "(root) CMD (/usr/lib/php/sessionclean)"),
                   20 => ("journal", "systemd", "Starting Cleanup of Temporary Directories..."),
                   21 => ("syslog", "kernel", $"[UFW BLOCK] IN=eth0 OUT= MAC=00:1a:2b:3c:4d:5e SRC={ip} DST=10.0.0.5 LEN={number % 1500} TOS=0x00 PREC=0x00 TTL={number % 64} ID={other} DF PROTO=TCP SPT={other} DPT=22"),
                   22 => ("journal", "systemd-logind", $"New session {number} of user {user}."),
                   _ => ("syslog", "plesk", $"Disk usage warning: /var/lib/psa/dumps is {number % 100}% full")
               };
    }

    /// <summary>
    /// Builds an eleven-character session ID of letters and digits.
    /// </summary>
    /// <param name="seed">The seed</param>
    /// <returns>The ID</returns>
    private static string Session(ulong seed)
    {
        var characters = new char[11];
        var rest = seed;

        for (var position = 0; position < characters.Length; position++)
        {
            var value = (int)(rest % 36);

            characters[position] = value < 26 ? Lowercase[value] : (char)('0' + value - 26);
            rest = (rest / 36) + (seed >> 7);
        }

        return new string(characters);
    }

    #endregion // Methods
}