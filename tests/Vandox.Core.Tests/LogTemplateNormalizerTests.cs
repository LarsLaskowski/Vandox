using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;

using Vandox.Core.Templates;

namespace Vandox.Core.Tests;

/// <summary>
/// Tests for <see cref="LogTemplateNormalizer"/>
/// </summary>
[TestClass]
public class LogTemplateNormalizerTests
{
    #region Constants

    private const string Delimiters = "\"'()[]{}<>,;=#!?*&|^$\\`";

    #endregion // Constants

    #region Properties

    /// <summary>
    /// Gets or sets the context of the running test.
    /// </summary>
    public TestContext TestContext { get; set; } = null!;

    #endregion // Properties

    #region Methods

    /// <summary>
    /// Lines that differ only in numbers, IDs, addresses, paths, hex values or white space give the same template.
    /// </summary>
    /// <param name="first">The first line</param>
    /// <param name="second">The second line</param>
    /// <param name="expected">The expected template</param>
    [TestMethod]
    [DataRow("connect from unknown[203.0.113.7]", "connect from unknown[198.51.100.22]", "connect from unknown[<ip>]")]
    [DataRow("4F2A31C0B2: from=<info@example.com>, size=1234", "9AB0C1D2E3: from=<root@localhost>, size=87", "<id>: from=<<email>>, size=<num>")]
    [DataRow("imap-login: Login: user=<alice>, method=PLAIN, rip=203.0.113.9, lip=10.0.0.5, mpid=2345, TLS, session=<aB3dE5fG7hJ>", "imap-login: Login: user=<alice>, method=PLAIN, rip=198.51.100.4, lip=10.0.0.5, mpid=99, TLS, session=<Zk9Qw2Er4Ty>", "imap-login: Login: user=<alice>, method=PLAIN, rip=<ip>, lip=<ip>, mpid=<num>, TLS, session=<<id>>")]
    [DataRow("Out of memory: Killed process 1234 (mariadbd) total-vm:1234567kB, anon-rss:654321kB", "Out of memory: Killed process 77 (mariadbd) total-vm:9876kB, anon-rss:123kB", "Out of memory: Killed process <num> (mariadbd) total-vm:<num>kB, anon-rss:<num>kB")]
    [DataRow("Aborted connection 12345 to db: 'wordpress' user: 'wpuser' host: '203.0.113.7' (Got timeout reading communication packets)", "Aborted connection 678 to db: 'wordpress' user: 'wpuser' host: '198.51.100.9' (Got timeout reading communication packets)", "Aborted connection <num> to db: 'wordpress' user: 'wpuser' host: '<ip>' (Got timeout reading communication packets)")]
    [DataRow("1234#1234: *5678 open() \"/var/www/html/missing.php\" failed (2: No such file or directory), client: 203.0.113.7, request: \"GET /missing.php HTTP/1.1\"", "4321#4321: *99 open() \"/srv/site/other/x.css\" failed (2: No such file or directory), client: 198.51.100.4, request: \"GET /x.css HTTP/1.1\"", "<num>#<num>: *<num> open() \"<path>\" failed (<num>: No such file or directory), client: <ip>, request: \"GET <path> <id>\"")]
    [DataRow("New session 4711 of user root.", "New session 12 of user root.", "New session <num> of user root.")]
    [DataRow("disk  full\ton /dev/sda1", "disk full on /dev/sdb2", "disk full on <path>")]
    [DataRow("page fault at 0x7f3a12", "page fault at 0XDEADBEEF", "page fault at <hex>")]
    public void LogTemplateNormalizerMapsSimilarLinesToOneTemplate(string first, string second, string expected)
    {
        // Act
        var firstTemplate = LogTemplateNormalizer.Normalize(first);
        var secondTemplate = LogTemplateNormalizer.Normalize(second);

        // Assert
        Assert.AreNotEqual(first, second, "the two lines differ");
        Assert.AreEqual(expected, firstTemplate, "template of the first line");
        Assert.AreEqual(expected, secondTemplate, "template of the second line");
    }

    /// <summary>
    /// Lines that differ in literal words give different templates.
    /// </summary>
    /// <param name="first">The first line</param>
    /// <param name="second">The second line</param>
    [TestMethod]
    [DataRow("connect from unknown[1.2.3.4]", "disconnect from unknown[1.2.3.4]")]
    [DataRow("user alice logged in", "user bob logged in")]
    [DataRow("error 12 in module a", "error 12 in module b")]
    [DataRow("open /tmp/a failed", "close /tmp/a failed")]
    [DataRow("mail to a@b.c", "mail from a@b.c")]
    public void LogTemplateNormalizerKeepsLinesWithDifferentTextApart(string first, string second)
    {
        // Act
        var firstTemplate = LogTemplateNormalizer.Normalize(first);
        var secondTemplate = LogTemplateNormalizer.Normalize(second);

        // Assert
        Assert.AreNotEqual(firstTemplate, secondTemplate, $"templates of '{first}' and '{second}'");
    }

    /// <summary>
    /// Every example of every token rule gives its placeholder.
    /// </summary>
    /// <param name="token">The token</param>
    /// <param name="expected">The expected template</param>
    [TestMethod]
    [DataRow("/var/lib/mysql/ibdata1", "<path>")]
    [DataRow("./run.sh", "<path>")]
    [DataRow("../up/x", "<path>")]
    [DataRow("~/x", "<path>")]
    [DataRow("info@example.com", "<email>")]
    [DataRow("root@localhost", "<email>")]
    [DataRow("first.last+tag_1%x-y@mail-1.example.org", "<email>")]
    [DataRow("10.0.0.1", "<ip>")]
    [DataRow("192.168.1.20:25", "<ip>")]
    [DataRow("10.0.0.0/8", "<ip>")]
    [DataRow("255.255.255.255:65535", "<ip>")]
    [DataRow("::1", "<ip>")]
    [DataRow("fe80::1", "<ip>")]
    [DataRow("2001:db8:0:0:0:0:0:1", "<ip>")]
    [DataRow("::ffff:10.0.0.1", "<ip>")]
    [DataRow("0x7f3a", "<hex>")]
    [DataRow("0X1F", "<hex>")]
    [DataRow("0123abcd-0123-4567-89ab-0123456789ab", "<id>")]
    [DataRow("42", "<num>")]
    [DataRow("-5", "<num>")]
    [DataRow("2.0.0", "<num>")]
    [DataRow("10:15:32", "<num>")]
    [DataRow("2026-10-12", "<num>")]
    [DataRow("50%", "<num>")]
    [DataRow("256.1.1.1", "<num>")]
    [DataRow("123kB", "<num>kB")]
    [DataRow("5s", "<num>s")]
    [DataRow("1.5ms", "<num>ms")]
    [DataRow("total-vm:123kB", "total-vm:<num>kB")]
    [DataRow("mail.example.com:25", "mail.example.com:<num>")]
    [DataRow("http://x/y", "http:<path>")]
    [DataRow("eth0", "<id>")]
    [DataRow("sda1", "<id>")]
    [DataRow("4F2A31C0B2", "<id>")]
    [DataRow("4Xb2Kq1Zz0z9", "<id>")]
    [DataRow("v2", "<id>")]
    public void LogTemplateNormalizerReplacesEachTokenForm(string token, string expected)
    {
        // Act
        var alone = LogTemplateNormalizer.Normalize(token);
        var inLine = LogTemplateNormalizer.Normalize($"before {token} after");

        // Assert
        Assert.AreEqual(expected, alone, $"template of '{token}'");
        Assert.AreEqual($"before {expected} after", inLine, $"template of '{token}' between two words");
    }

    /// <summary>
    /// Tokens that almost match a rule fall to the later rules.
    /// </summary>
    /// <param name="token">The token</param>
    /// <param name="expected">The expected template</param>
    [TestMethod]
    [DataRow("/", "/")]
    [DataRow("a@", "a@")]
    [DataRow("a@b@c", "a@b@c")]
    [DataRow("1.2.3.4:123456", "<num>")]
    [DataRow("1.2.3.4/33", "<num>")]
    [DataRow("256.1.1.1", "<num>")]
    [DataRow("10:15:32", "<num>")]
    [DataRow(":::1", "<num>")]
    [DataRow("fe80:1", "<id>:<num>")]
    [DataRow("fe80::", "<id>::")]
    [DataRow("0x", "<num>x")]
    [DataRow("deadbeef", "deadbeef")]
    [DataRow("abcdef12", "<id>")]
    [DataRow("0123abcd-0123-4567-89ab-01234567890", "<id>")]
    [DataRow("12345kBytes", "<id>")]
    [DataRow("00:1a:2b:3c:4d:5e", "<num>:<num>a:<num>b:<num>c:<num>d:<num>e")]
    [DataRow("\u0661\u0662\u0663", "\u0661\u0662\u0663")]
    [DataRow("\uFF11\uFF12\uFF13", "\uFF11\uFF12\uFF13")]
    [DataRow("0123abcd-0123-4567-89ab-0123456789ag", "<id>")]
    [DataRow("5.ms", "<id>")]
    [DataRow("a@.example.com", "a@.example.com")]
    [DataRow("a@b..c", "a@b..c")]
    [DataRow("a@b_c", "a@b_c")]
    [DataRow("1.2.3.4:x", "<ip>:x")]
    [DataRow("1.2.3.4:", "<ip>:")]
    [DataRow("1.2.3.4x5", "<id>")]
    [DataRow("1.2.3.4/123", "<num>")]
    [DataRow("1.2.3.4/", "<num>")]
    [DataRow("1:2:3:4:5:6:7:", "<num>:")]
    [DataRow("fe80::12345", "<id>::<num>")]
    public void LogTemplateNormalizerLeavesNearMissesToLaterRules(string token, string expected)
    {
        // Act
        var template = LogTemplateNormalizer.Normalize(token);

        // Assert
        Assert.AreEqual(expected, template, $"template of '{token}'");
    }

    /// <summary>
    /// Runs of white space and control characters become one space, trimmed at both ends, and format characters stay inside their token.
    /// </summary>
    /// <param name="message">The message</param>
    /// <param name="expected">The expected template</param>
    [TestMethod]
    [DataRow("a\tb\nc\rd\0e\u0085f\u00A0g", "a b c d e f g")]
    [DataRow("  \t\n\r\0\u0085\u00A0abc  \t\n\r\0\u0085\u00A0", "abc")]
    [DataRow("a \t\r\n \0 \u0085 \u00A0 b", "a b")]
    [DataRow("ab\u200Bcd", "ab\u200Bcd")]
    [DataRow("x\u200B7", "<id>")]
    [DataRow("1\u200B2", "<id>")]
    public void LogTemplateNormalizerCollapsesSeparators(string message, string expected)
    {
        // Act
        var template = LogTemplateNormalizer.Normalize(message);

        // Assert
        Assert.AreEqual(expected, template, "template");
    }

    /// <summary>
    /// Every delimiter is kept and ends a token, and trailing dots and colons are kept after the replacement.
    /// </summary>
    [TestMethod]
    public void LogTemplateNormalizerKeepsDelimitersAndTrailingPunctuation()
    {
        // Act and Assert
        foreach (var delimiter in Delimiters)
        {
            Assert.AreEqual($"<num>{delimiter}<num>", LogTemplateNormalizer.Normalize($"1{delimiter}2"), $"delimiter '{delimiter}' between two numbers");
        }

        Assert.AreEqual("port <num>.", LogTemplateNormalizer.Normalize("port 25."), "trailing dot after a number");
        Assert.AreEqual("<id>:", LogTemplateNormalizer.Normalize("4F2A31C0B2:"), "trailing colon after an ID");
        Assert.AreEqual("memory:", LogTemplateNormalizer.Normalize("memory:"), "trailing colon after a word");
        Assert.AreEqual("a...", LogTemplateNormalizer.Normalize("a..."), "several trailing dots");
        Assert.AreEqual("see <ip>: failed", LogTemplateNormalizer.Normalize("see 10.0.0.1: failed"), "trailing colon after an address");
        Assert.AreEqual("<ip>.", LogTemplateNormalizer.Normalize("1.2.3.4."), "trailing dot after an address");
        Assert.AreEqual("... x", LogTemplateNormalizer.Normalize("... x"), "a token of dots only");
        Assert.AreEqual(":", LogTemplateNormalizer.Normalize(":"), "a lone colon");
    }

    /// <summary>
    /// A lone surrogate becomes U+FFFD and a valid pair stays.
    /// </summary>
    /// <param name="message">The message</param>
    /// <param name="expected">The expected template</param>
    /// <param name="loneSurrogates">The number of lone surrogates the message must hold</param>
    [TestMethod]
    [DynamicData(nameof(LoneSurrogateCases))]
    public void LogTemplateNormalizerReplacesLoneSurrogates(string message, string expected, int loneSurrogates)
    {
        // Act
        var template = LogTemplateNormalizer.Normalize(message);
        var roundTrip = Encoding.UTF8.GetString(Encoding.UTF8.GetBytes(template));

        // Assert
        Assert.AreEqual(loneSurrogates, CountLoneSurrogates(message), "the input still holds the intended lone surrogates");
        Assert.AreEqual(0, CountLoneSurrogates(template), "the template holds no lone surrogate");
        Assert.AreEqual(expected, template, "template");
        Assert.AreEqual(template, roundTrip, "the template is valid Unicode and survives UTF-8 encoding unchanged");
    }

    /// <summary>
    /// The template is cut at a character boundary to at most 1,024 UTF-8 bytes, counted on the finished template.
    /// </summary>
    /// <param name="scenario">The scenario</param>
    [TestMethod]
    [DataRow("ASCII over the limit")]
    [DataRow("three-byte character over the limit")]
    [DataRow("three-byte character ending at the limit")]
    [DataRow("digit run that normalizes short")]
    [DataRow("white space run that normalizes short")]
    [DataRow("cut template ending in a space")]
    [DataRow("lone surrogate over the limit")]
    [DataRow("lone surrogate at the limit")]
    public void LogTemplateNormalizerCutsTemplatesAtTheByteLimit(string scenario)
    {
        // Arrange
        var (message, expected, bytes) = scenario switch
                                         {
                                             "ASCII over the limit" => (new string('a', 2000), new string('a', 1024), 1024),
                                             "three-byte character over the limit" => (string.Concat(new string('a', 1022), "\u20AC", "b"), new string('a', 1022), 1022),
                                             "three-byte character ending at the limit" => (string.Concat(new string('a', 1021), "\u20AC", "b"), string.Concat(new string('a', 1021), "\u20AC"), 1024),
                                             "digit run that normalizes short" => (new string('7', 3000), "<num>", 5),
                                             "white space run that normalizes short" => (string.Concat("head", new string(' ', 3000), "tail"), "head tail", 9),
                                             "cut template ending in a space" => (string.Concat(new string('a', 1023), " ", new string('b', 10)), string.Concat(new string('a', 1023), " "), 1024),
                                             "lone surrogate over the limit" => (string.Concat(new string('a', 1022), "\uD800"), new string('a', 1022), 1022),
                                             _ => (string.Concat(new string('a', 1021), "\uD800"), string.Concat(new string('a', 1021), "\uFFFD"), 1024)
                                         };

        // Act
        var template = LogTemplateNormalizer.Normalize(message);

        // Assert
        Assert.AreEqual(expected, template, "template text");
        Assert.AreEqual(bytes, Encoding.UTF8.GetByteCount(template), "template length in UTF-8 bytes");
    }

    /// <summary>
    /// An empty message, or one of separators only, gives the empty template.
    /// </summary>
    /// <param name="message">The message</param>
    [TestMethod]
    [DataRow("")]
    [DataRow(" ")]
    [DataRow(" \t\r\n ")]
    [DataRow("\0\u0085\u00A0")]
    public void LogTemplateNormalizerGivesTheEmptyTemplateForAnEmptyMessage(string message)
    {
        // Act
        var template = LogTemplateNormalizer.Normalize(message);

        // Assert
        Assert.AreEqual(string.Empty, template, "template");
    }

    /// <summary>
    /// A null message is refused.
    /// </summary>
    [TestMethod]
    public void LogTemplateNormalizerRefusesNull()
    {
        // Act
        var exception = Assert.ThrowsExactly<ArgumentNullException>(() => LogTemplateNormalizer.Normalize(null!), "null message");

        // Assert
        Assert.AreEqual("message", exception.ParamName, "parameter name");
    }

    /// <summary>
    /// Concurrent calls give the same templates as single-threaded calls.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task LogTemplateNormalizerGivesTheSameTemplatesUnderConcurrentCalls()
    {
        // Arrange
        const int workers = 8;
        const int rounds = 200;

        var inputs = BuildConcurrentInputs();
        var expected = inputs.Select(LogTemplateNormalizer.Normalize).ToArray();
        var schedule = Enumerable.Range(0, inputs.Length * rounds).Select(position => (int)(position * 7919L % inputs.Length)).ToArray();
        var mismatches = new ConcurrentBag<string>();
        using var start = new Barrier(workers);

        // Act
        var tasks = Enumerable.Range(0, workers).Select(worker => Task.Factory.StartNew(() => RunWorker(worker, workers, schedule, inputs, expected, start, mismatches, TestContext.CancellationToken), TestContext.CancellationToken, TaskCreationOptions.LongRunning, TaskScheduler.Default)).ToArray();

        await Task.WhenAll(tasks);

        // Assert
        Assert.AreEqual(0, schedule.Distinct().Count(index => schedule.Count(other => other == index) != rounds), "every input is normalized the same number of times");
        Assert.AreEqual(1024, expected[inputs.Length - 2].Length, "the 16 KiB number message fills the template to the limit");
        Assert.IsEmpty(mismatches, "concurrent results that differ from the single-threaded one");
    }

    /// <summary>
    /// An output far larger than the input and than any stack buffer is produced without failure and cut at the limit.
    /// </summary>
    [TestMethod]
    public void LogTemplateNormalizerHandlesOutputLargerThanTheInput()
    {
        // Arrange
        var message = string.Concat(Enumerable.Repeat("1 ", 512 * 1024));
        var expected = string.Concat(Enumerable.Repeat("<num> ", 171))[..1024];

        // Act
        var template = LogTemplateNormalizer.Normalize(message);

        // Assert
        Assert.AreEqual(1024 * 1024, message.Length, "the message is 1 MiB");
        Assert.AreEqual(expected, template, "template text");
        Assert.AreEqual(1024, Encoding.UTF8.GetByteCount(template), "template length in UTF-8 bytes");
    }

    /// <summary>
    /// The time to normalize a message grows linearly with its length: a message eight times as long takes about eight times as long, not sixty-four times.
    /// The messages are single long tokens or runs whose template stays short, so that the whole input is read.
    /// </summary>
    /// <param name="scenario">The shape of the message</param>
    [TestMethod]
    [DataRow("digits with a unit")]
    [DataRow("email with dotted domain")]
    [DataRow("white space run")]
    [DataRow("dotted letters")]
    [DataRow("hex digits")]
    public void LogTemplateNormalizerTimeGrowsLinearly(string scenario)
    {
        // Arrange
        const int baseLength = 50_000;
        const int factor = 8;
        const int repetitions = 5;

        var baseMessage = BuildLongMessage(scenario, baseLength);
        var longMessage = BuildLongMessage(scenario, factor * baseLength);

        LogTemplateNormalizer.Normalize(baseMessage);
        LogTemplateNormalizer.Normalize(longMessage);

        // Act
        var baseTicks = BestTicks(baseMessage, repetitions);
        var longTicks = BestTicks(longMessage, repetitions);
        var ratio = (double)longTicks / Math.Max(1, baseTicks);

        // Assert
        Assert.IsGreaterThanOrEqualTo(baseLength, baseMessage.Length, "the base message has the intended length");
        Assert.IsLessThan(30.0, ratio, $"{scenario}: the time of the message {factor} times as long over the time of the base message is {ratio:F2}; linear growth is about {factor}, quadratic about {factor * factor}");
    }

    /// <summary>
    /// Builds a long message of one shape.
    /// </summary>
    /// <param name="scenario">The shape</param>
    /// <param name="length">The approximate length in characters</param>
    /// <returns>The message</returns>
    private static string BuildLongMessage(string scenario, int length)
    {
        return scenario switch
               {
                   "digits with a unit" => string.Concat(new string('7', length), "kB"),
                   "email with dotted domain" => string.Concat("user@", string.Concat(Enumerable.Repeat("a.", length / 2)), "b"),
                   "white space run" => string.Concat("head", new string(' ', length), "tail"),
                   "dotted letters" => string.Concat(string.Concat(Enumerable.Repeat("a.", length / 2)), "b"),
                   _ => string.Concat(string.Concat(Enumerable.Repeat("dead", length / 4)), "1")
               };
    }

    /// <summary>
    /// Measures the best of several runs of one normalization.
    /// </summary>
    /// <param name="message">The message</param>
    /// <param name="repetitions">The number of runs</param>
    /// <returns>The shortest duration in stopwatch ticks</returns>
    private static long BestTicks(string message, int repetitions)
    {
        var best = long.MaxValue;

        for (var run = 0; run < repetitions; run++)
        {
            var start = Stopwatch.GetTimestamp();

            LogTemplateNormalizer.Normalize(message);

            best = Math.Min(best, Stopwatch.GetTimestamp() - start);
        }

        return best;
    }

    /// <summary>
    /// Builds the lone-surrogate cases at runtime, because a lone surrogate in an attribute literal is corrupted in the assembly metadata.
    /// </summary>
    /// <returns>The messages, the expected templates and the number of lone surrogates of each message</returns>
    private static IEnumerable<(string Message, string Expected, int LoneSurrogates)> LoneSurrogateCases()
    {
        const char highSurrogate = (char)0xD800;
        const char lowSurrogate = (char)0xDC00;
        const char emojiHigh = (char)0xD83D;
        const char emojiLow = (char)0xDE00;
        const char replacement = (char)0xFFFD;

        yield return (string.Concat("a", highSurrogate, "b"), string.Concat("a", replacement, "b"), 1);
        yield return (string.Concat("a", lowSurrogate), string.Concat("a", replacement), 1);
        yield return (string.Concat(highSurrogate, highSurrogate), string.Concat(replacement, replacement), 2);
        yield return (string.Concat(lowSurrogate, highSurrogate), string.Concat(replacement, replacement), 2);
        yield return (string.Concat("x", emojiHigh), string.Concat("x", replacement), 1);
        yield return (string.Concat("x", emojiHigh, emojiLow, "y"), string.Concat("x", emojiHigh, emojiLow, "y"), 0);
    }

    /// <summary>
    /// Counts the surrogates of a text that are not part of a valid pair.
    /// </summary>
    /// <param name="text">The text</param>
    /// <returns>The number of lone surrogates</returns>
    private static int CountLoneSurrogates(string text)
    {
        var count = 0;
        var pairOpen = false;

        foreach (var character in text)
        {
            if (pairOpen && char.IsLowSurrogate(character))
            {
                pairOpen = false;

                continue;
            }

            count += pairOpen ? 1 : 0;
            pairOpen = char.IsHighSurrogate(character);
            count += char.IsLowSurrogate(character) ? 1 : 0;
        }

        count += pairOpen ? 1 : 0;

        return count;
    }

    /// <summary>
    /// Normalizes the inputs of one worker's share of the schedule and records every result that differs from the single-threaded one.
    /// </summary>
    /// <param name="worker">The number of the worker</param>
    /// <param name="workers">The number of workers</param>
    /// <param name="schedule">The input indexes in the order they are normalized</param>
    /// <param name="inputs">The inputs</param>
    /// <param name="expected">The single-threaded templates of the inputs</param>
    /// <param name="start">The barrier that releases all workers together</param>
    /// <param name="mismatches">Receives the inputs whose template differs</param>
    /// <param name="cancellationToken">Cancels the wait for the other workers</param>
    private static void RunWorker(int worker, int workers, int[] schedule, string[] inputs, string[] expected, Barrier start, ConcurrentBag<string> mismatches, CancellationToken cancellationToken)
    {
        start.SignalAndWait(cancellationToken);

        for (var position = worker; position < schedule.Length; position += workers)
        {
            var index = schedule[position];

            if (string.Equals(expected[index], LogTemplateNormalizer.Normalize(inputs[index]), StringComparison.Ordinal))
            {
                continue;
            }

            mismatches.Add($"input {index}");
        }
    }

    /// <summary>
    /// Builds the mixed inputs of the concurrency test; the last three are the large ones.
    /// </summary>
    /// <returns>The inputs</returns>
    private static string[] BuildConcurrentInputs()
    {
        return [
                   "connect from unknown[203.0.113.7]",
                   "4F2A31C0B2: from=<info@example.com>, size=1234",
                   "imap-login: Login: user=<alice>, rip=203.0.113.9, lip=10.0.0.5, mpid=2345, session=<aB3dE5fG7hJ>",
                   "Out of memory: Killed process 1234 (mariadbd) total-vm:1234567kB, anon-rss:654321kB",
                   "Aborted connection 12345 to db: 'wordpress' user: 'wpuser' host: '203.0.113.7'",
                   "1234#1234: *5678 open() \"/var/www/html/missing.php\" failed (2: No such file or directory), client: 203.0.113.7",
                   "New session 4711 of user root.",
                   "a\uD800b\uDC00c",
                   "\uDBFF",
                   "x\uD83D",
                   "\uDE00y 12 z\uD83D\uDE00",
                   "page fault at 0x7f3a12 on fe80::1 and 00:1a:2b:3c:4d:5e",
                   "tab\tnewline\ncarriage\rreturn\0null 42",
                   string.Concat(Enumerable.Repeat("\uD800 7 ", 4096)),
                   string.Concat(Enumerable.Repeat("1 ", 8192)),
                   string.Concat(Enumerable.Range(0, 16384).Select(index => (char)('a' + (index % 26))))
               ];
    }

    #endregion // Methods
}