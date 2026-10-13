namespace Vandox.Backend.Cli;

#pragma warning disable RH2003, S2325

/// <summary>
/// The sub-command <c>hash-password</c>: reads a password from the first line of standard input and prints its argon2id hash.
/// </summary>
internal static class HashPasswordCommand
{
    #region Constants

    /// <summary>
    /// The shortest accepted password in characters.
    /// </summary>
    internal const int MinPasswordLength = 12;

    /// <summary>
    /// The text printed for a refused password.
    /// </summary>
    internal const string RefusedText = "vandoxd hash-password: the password must have 12 to 1024 characters and no control characters";

    #endregion // Constants

    #region Methods

    /// <summary>
    /// Runs the command.
    /// </summary>
    /// <param name="stdin">The standard input</param>
    /// <param name="stdout">The standard output</param>
    /// <param name="stderr">The standard error</param>
    /// <param name="cancellationToken">Interrupts the command</param>
    /// <returns>A task that returns the exit code</returns>
    internal static Task<int> RunAsync(TextReader stdin, TextWriter stdout, TextWriter stderr, CancellationToken cancellationToken)
    {
        throw new NotImplementedException();
    }

    #endregion // Methods
}