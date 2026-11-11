using Vandox.Backend.Hosting;

namespace Vandox.Backend.Tests;

/// <summary>
/// Tests for <see cref="BackendApp"/>
/// </summary>
[TestClass]
public class BackendAppTests
{
    #region Methods

    /// <summary>
    /// A cancelled token does not change what the help, the version, a usage error and a refused password print: every line is written as with
    /// <see cref="CancellationToken.None"/>, with the same exit code. The password is read before the interruption arrives.
    /// </summary>
    /// <param name="argument">The command-line argument</param>
    /// <param name="exitCode">The expected exit code</param>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [TestCategory("AC7")]
    [DataRow("-help", 0)]
    [DataRow("-version", 0)]
    [DataRow("-nope", 2)]
    [DataRow("hash-password", 1)]
    public async Task BackendAppWithCancelledTokenPrintsSameOutput(string argument, int exitCode)
    {
        // Arrange
        const string input = "short\n";

        using var plainOut = new StringWriter();
        using var plainError = new StringWriter();
        using var plainInput = new StringReader(input);

        var plainHooks = new ServeHooks
                         {
                             Input = plainInput
                         };
        var plainCode = await BackendApp.RunAsync([argument], [], plainOut, plainError, plainHooks, CancellationToken.None);

        using var source = new CancellationTokenSource();
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        using var reader = new CancellingTextReader(input, source);

        var hooks = new ServeHooks
                    {
                        Input = reader
                    };

        if (argument != "hash-password")
        {
            await source.CancelAsync();
        }

        // Act
        var code = await BackendApp.RunAsync([argument], [], stdout, stderr, hooks, source.Token);

        // Assert
        Assert.AreEqual(exitCode, plainCode, "exit code without cancellation");
        Assert.AreEqual(exitCode, code, "exit code with the cancelled token");
        Assert.IsTrue(source.IsCancellationRequested, "the token was cancelled");
        Assert.IsNotEmpty(plainOut.ToString() + plainError, "the call without cancellation printed something");
        Assert.AreEqual(plainOut.ToString(), stdout.ToString(), "standard output");
        Assert.AreEqual(plainError.ToString(), stderr.ToString(), "standard error");
    }

    #endregion // Methods
}