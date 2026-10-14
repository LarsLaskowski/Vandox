using System.Text.RegularExpressions;

using Vandox.Backend.Cli;
using Vandox.Backend.Hosting;
using Vandox.Backend.Security;

namespace Vandox.Backend.Tests;

/// <summary>
/// Tests for <see cref="HashPasswordCommand"/>
/// </summary>
[TestClass]
public partial class HashPasswordCommandTests
{
    #region Properties

    /// <summary>
    /// Gets or sets the context of the running test.
    /// </summary>
    public TestContext TestContext { get; set; } = null!;

    #endregion // Properties

    #region Methods

    /// <summary>
    /// A first line of 12 to 1,024 characters prints one hash line that verifies the password.
    /// </summary>
    /// <param name="kind">The kind of input</param>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [DataRow("twelve characters")]
    [DataRow("1024 characters")]
    [DataRow("carriage return and line feed")]
    [DataRow("no final line feed")]
    [DataRow("non-ASCII letters")]
    public async Task HashPasswordPrintsVerifiableHash(string kind)
    {
        // Arrange
        var (password, input) = AcceptedInput(kind);

        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        using var stdin = new StringReader(input);

        // Act
        var code = await HashPasswordCommand.RunAsync(stdin, stdout, stderr, TestContext.CancellationToken);

        // Assert
        var output = stdout.ToString();

        Assert.AreEqual(0, code, "exit code");
        Assert.AreEqual(string.Empty, stderr.ToString(), "nothing on standard error");
        Assert.IsTrue(HashLine().IsMatch(output), $"one hash line of the default form: {output}");
        Assert.IsTrue(PasswordHash.Parse(output.TrimEnd('\n')).Verify(password), "the hash verifies the password");
    }

    /// <summary>
    /// A password that is too short, too long, empty or has a control character is refused with the one line, the password appears nowhere, and no more than 1,026 characters are read.
    /// </summary>
    /// <param name="kind">The kind of input</param>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [DataRow("eleven characters")]
    [DataRow("1025 characters")]
    [DataRow("endless first line")]
    [DataRow("empty input")]
    [DataRow("line feed only")]
    [DataRow("tab")]
    [DataRow("bell")]
    [DataRow("next line control character")]
    public async Task HashPasswordRefusesInvalidPasswords(string kind)
    {
        // Arrange
        var input = RefusedInput(kind);

        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        var stdin = new CountingTextReader(input);
        var password = input.Split('\n')[0].TrimEnd('\r');

        // Act
        var code = await HashPasswordCommand.RunAsync(stdin, stdout, stderr, TestContext.CancellationToken);

        // Assert
        Assert.AreEqual(1, code, "exit code");
        Assert.AreEqual(HashPasswordCommand.RefusedText + "\n", stderr.ToString(), "the refusal on standard error");
        Assert.AreEqual(string.Empty, stdout.ToString(), "nothing on standard output");
        Assert.IsLessThanOrEqualTo(1026, stdin.Consumed, "characters read from standard input");

        if (password.Length > 0)
        {
            Assert.DoesNotContain(password, stderr.ToString() + stdout, "the password is not printed");
        }
    }

    /// <summary>
    /// The sub-command runs through the application entry point, reads the hook's input and needs no configuration.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task HashPasswordRunsThroughBackendApp()
    {
        // Arrange
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        using var input = new StringReader("a-password-of-length\n");
        var hooks = new ServeHooks
                    {
                        Input = input
                    };

        // Act
        var code = await BackendApp.RunAsync(["hash-password"], [], stdout, stderr, hooks, TestContext.CancellationToken);

        // Assert
        Assert.AreEqual(0, code, "exit code");
        Assert.AreEqual(string.Empty, stderr.ToString(), "nothing on standard error");
        Assert.IsTrue(PasswordHash.Parse(stdout.ToString().TrimEnd('\n')).Verify("a-password-of-length"), "the printed hash verifies the password");
    }

    /// <summary>
    /// Builds an accepted input.
    /// </summary>
    /// <param name="kind">The kind of input</param>
    /// <returns>The password and the whole input</returns>
    private static (string Password, string Input) AcceptedInput(string kind)
    {
        return kind switch
               {
                   "twelve characters" => ("abcdefghijkl", "abcdefghijkl\nsecond line\n"),
                   "1024 characters" => (new string('p', 1024), new string('p', 1024) + "\nsecond line\n"),
                   "carriage return and line feed" => ("abcdefghijkl", "abcdefghijkl\r\nsecond line\n"),
                   "no final line feed" => ("abcdefghijkl", "abcdefghijkl"),
                   "non-ASCII letters" => ("p\u00e4ssw\u00f6rd-\u00fcn\u00efcode", "p\u00e4ssw\u00f6rd-\u00fcn\u00efcode\nsecond line\n"),
                   _ => throw new InvalidOperationException($"unknown kind {kind}")
               };
    }

    /// <summary>
    /// Builds a refused input.
    /// </summary>
    /// <param name="kind">The kind of input</param>
    /// <returns>The whole input</returns>
    private static string RefusedInput(string kind)
    {
        return kind switch
               {
                   "eleven characters" => "zzzzzzzzzzz\n",
                   "1025 characters" => new string('z', 1025) + "\n",
                   "endless first line" => new string('z', 100000),
                   "empty input" => string.Empty,
                   "line feed only" => "\n",
                   "tab" => "zzzzzz\tzzzzzzzz\n",
                   "bell" => "zzzzzz\u0007zzzzzzzz\n",
                   "next line control character" => "zzzzzz\u0085zzzzzzzz\n",
                   _ => throw new InvalidOperationException($"unknown kind {kind}")
               };
    }

    #endregion // Methods

    #region Patterns

    [GeneratedRegex("^\\$argon2id\\$v=19\\$m=65536,t=3,p=4\\$[A-Za-z0-9+/]{22}\\$[A-Za-z0-9+/]{43}\\n$")]
    private static partial Regex HashLine();

    #endregion // Patterns
}