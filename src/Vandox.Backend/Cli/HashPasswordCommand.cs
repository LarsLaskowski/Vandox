using System.Text;

using Vandox.Backend.Security;

namespace Vandox.Backend.Cli;

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

    /// <summary>
    /// The most characters read from standard input: the longest password, a carriage return and a line feed.
    /// </summary>
    private const int MaxLineLength = PasswordHash.MaxPasswordLength + 2;

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
    internal static async Task<int> RunAsync(TextReader stdin, TextWriter stdout, TextWriter stderr, CancellationToken cancellationToken)
    {
        var password = ReadFirstLine(stdin, cancellationToken);

        if (IsAccepted(password))
        {
            await stdout.WriteLineAsync(PasswordHash.Create(password)).ConfigureAwait(false);

            return 0;
        }

        await stderr.WriteLineAsync(RefusedText).ConfigureAwait(false);

        return 1;
    }

    /// <summary>
    /// Reads the first line of the input, at most as many characters as the longest line that can still hold an accepted password.
    /// </summary>
    /// <param name="stdin">The standard input</param>
    /// <param name="cancellationToken">Interrupts the read</param>
    /// <returns>The line without its line end</returns>
    private static string ReadFirstLine(TextReader stdin, CancellationToken cancellationToken)
    {
        var line = new StringBuilder();

        while (line.Length < MaxLineLength)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var value = stdin.Read();

            if (value < 0 || value == '\n')
            {
                break;
            }

            line.Append((char)value);
        }

        if (line.Length > 0 && line[^1] == '\r')
        {
            line.Length--;
        }

        return line.ToString();
    }

    /// <summary>
    /// Tells whether a password is accepted: 12 to 1,024 characters and no control character.
    /// </summary>
    /// <param name="password">The password</param>
    /// <returns><c>true</c> when it is accepted</returns>
    private static bool IsAccepted(string password)
    {
        if (password.Length is < MinPasswordLength or > PasswordHash.MaxPasswordLength)
        {
            return false;
        }

        if (password.Any(char.IsControl))
        {
            return false;
        }

        return true;
    }

    #endregion // Methods
}