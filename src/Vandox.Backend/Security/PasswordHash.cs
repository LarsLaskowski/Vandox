namespace Vandox.Backend.Security;

#pragma warning disable RH2003, S2325

/// <summary>
/// An argon2id password hash in the PHC string form: it parses and checks the hash and creates new ones.
/// </summary>
internal sealed class PasswordHash : IPasswordVerifier
{
    #region Constants

    /// <summary>
    /// The least accepted memory in KiB.
    /// </summary>
    internal const int MinMemoryKiB = 19456;

    /// <summary>
    /// The most accepted memory in KiB.
    /// </summary>
    internal const int MaxMemoryKiB = 262144;

    /// <summary>
    /// The least accepted number of iterations.
    /// </summary>
    internal const int MinIterations = 2;

    /// <summary>
    /// The most accepted number of iterations.
    /// </summary>
    internal const int MaxIterations = 10;

    /// <summary>
    /// The least accepted parallelism.
    /// </summary>
    internal const int MinParallelism = 1;

    /// <summary>
    /// The most accepted parallelism.
    /// </summary>
    internal const int MaxParallelism = 8;

    /// <summary>
    /// The least accepted salt length in bytes.
    /// </summary>
    internal const int MinSaltBytes = 8;

    /// <summary>
    /// The most accepted salt length in bytes.
    /// </summary>
    internal const int MaxSaltBytes = 64;

    /// <summary>
    /// The least accepted hash length in bytes.
    /// </summary>
    internal const int MinHashBytes = 16;

    /// <summary>
    /// The most accepted hash length in bytes.
    /// </summary>
    internal const int MaxHashBytes = 64;

    /// <summary>
    /// The longest password that is checked, in characters.
    /// </summary>
    internal const int MaxPasswordLength = 1024;

    /// <summary>
    /// The memory in KiB of a newly created hash.
    /// </summary>
    internal const int DefaultMemoryKiB = 65536;

    /// <summary>
    /// The iterations of a newly created hash.
    /// </summary>
    internal const int DefaultIterations = 3;

    /// <summary>
    /// The parallelism of a newly created hash.
    /// </summary>
    internal const int DefaultParallelism = 4;

    /// <summary>
    /// The salt length in bytes of a newly created hash.
    /// </summary>
    internal const int DefaultSaltBytes = 16;

    /// <summary>
    /// The hash length in bytes of a newly created hash.
    /// </summary>
    internal const int DefaultHashBytes = 32;

    /// <summary>
    /// The reason for a text that is not in the accepted form.
    /// </summary>
    internal const string ReasonForm = "must be an argon2id hash in the form $argon2id$v=19$m=<memory>,t=<iterations>,p=<parallelism>$<salt>$<hash>";

    /// <summary>
    /// The reason for another version than 19.
    /// </summary>
    internal const string ReasonVersion = "version must be 19";

    /// <summary>
    /// The reason for memory out of bounds.
    /// </summary>
    internal const string ReasonMemory = "memory must be 19456 to 262144 KiB";

    /// <summary>
    /// The reason for iterations out of bounds.
    /// </summary>
    internal const string ReasonIterations = "iterations must be 2 to 10";

    /// <summary>
    /// The reason for parallelism out of bounds.
    /// </summary>
    internal const string ReasonParallelism = "parallelism must be 1 to 8";

    /// <summary>
    /// The reason for a salt length out of bounds.
    /// </summary>
    internal const string ReasonSalt = "salt must be 8 to 64 bytes";

    /// <summary>
    /// The reason for a hash length out of bounds.
    /// </summary>
    internal const string ReasonHash = "hash must be 16 to 64 bytes";

    #endregion // Constants

    #region Properties

    /// <summary>
    /// Gets the memory in KiB.
    /// </summary>
    internal int MemoryKiB { get; }

    /// <summary>
    /// Gets the number of iterations.
    /// </summary>
    internal int Iterations { get; }

    /// <summary>
    /// Gets the parallelism.
    /// </summary>
    internal int Parallelism { get; }

    /// <summary>
    /// Gets the salt length in bytes.
    /// </summary>
    internal int SaltLength { get; }

    /// <summary>
    /// Gets the hash length in bytes.
    /// </summary>
    internal int HashLength { get; }

    #endregion // Properties

    #region Methods

    /// <summary>
    /// Parses a hash text, enforcing the accepted form and bounds.
    /// </summary>
    /// <param name="text">The text</param>
    /// <returns>The hash</returns>
    /// <exception cref="PasswordHashException">The text is not an accepted hash; the message is one of the Reason texts</exception>
    internal static PasswordHash Parse(string text)
    {
        throw new NotImplementedException();
    }

    /// <summary>
    /// Creates the hash of a password with a random salt and the default parameters.
    /// </summary>
    /// <param name="password">The password</param>
    /// <returns>The hash in the PHC string form</returns>
    internal static string Create(string password)
    {
        throw new NotImplementedException();
    }

    /// <summary>
    /// Creates the hash of a password with the given salt and parameters; it checks no bounds.
    /// </summary>
    /// <param name="password">The password</param>
    /// <param name="salt">The salt</param>
    /// <param name="memoryKiB">The memory in KiB</param>
    /// <param name="iterations">The iterations</param>
    /// <param name="parallelism">The parallelism</param>
    /// <param name="hashBytes">The hash length in bytes</param>
    /// <returns>The hash in the PHC string form</returns>
    internal static string Create(string password, ReadOnlySpan<byte> salt, int memoryKiB, int iterations, int parallelism, int hashBytes)
    {
        throw new NotImplementedException();
    }

    #endregion // Methods

    #region IPasswordVerifier

    /// <inheritdoc/>
    public bool Verify(string password)
    {
        throw new NotImplementedException();
    }

    #endregion // IPasswordVerifier
}