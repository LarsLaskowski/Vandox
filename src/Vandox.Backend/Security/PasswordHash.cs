using System.Globalization;
using System.Security.Cryptography;
using System.Text;

using Konscious.Security.Cryptography;

namespace Vandox.Backend.Security;

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

    private const string Identifier = "argon2id";

    private const int Version = 19;

    private const int MaxDigits = 10;

    #endregion // Constants

    #region Fields

    private readonly byte[] _salt;
    private readonly byte[] _hash;

    #endregion // Fields

    #region Constructors

    /// <summary>
    /// Initializes a new instance of the <see cref="PasswordHash"/> class.
    /// </summary>
    /// <param name="memoryKiB">The memory in KiB</param>
    /// <param name="iterations">The iterations</param>
    /// <param name="parallelism">The parallelism</param>
    /// <param name="salt">The salt</param>
    /// <param name="hash">The expected hash</param>
    private PasswordHash(int memoryKiB, int iterations, int parallelism, byte[] salt, byte[] hash)
    {
        MemoryKiB = memoryKiB;
        Iterations = iterations;
        Parallelism = parallelism;
        _salt = salt;
        _hash = hash;
    }

    #endregion // Constructors

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
    internal int SaltLength => _salt.Length;

    /// <summary>
    /// Gets the hash length in bytes.
    /// </summary>
    internal int HashLength => _hash.Length;

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
        var parts = text.Split('$');

        Require(HasShape(parts), ReasonForm);

        var hasVersion = parts.Length == 6;

        var offset = hasVersion ? 3 : 2;
        long? version = hasVersion ? ParseDecimal(parts[2].AsSpan(2)) : null;
        var (memory, iterations, parallelism) = ParseParameters(parts[offset]);

        var salt = DecodeBase64(parts[offset + 1]);
        var hash = DecodeBase64(parts[offset + 2]);

        if (version != Version)
        {
            throw new PasswordHashException(ReasonVersion);
        }

        Require(memory is >= MinMemoryKiB and <= MaxMemoryKiB, ReasonMemory);
        Require(iterations is >= MinIterations and <= MaxIterations, ReasonIterations);
        Require(parallelism is >= MinParallelism and <= MaxParallelism, ReasonParallelism);
        Require(salt.Length is >= MinSaltBytes and <= MaxSaltBytes, ReasonSalt);
        Require(hash.Length is >= MinHashBytes and <= MaxHashBytes, ReasonHash);

        return new PasswordHash((int)memory, (int)iterations, (int)parallelism, salt, hash);
    }

    /// <summary>
    /// Creates the hash of a password with a random salt and the default parameters.
    /// </summary>
    /// <param name="password">The password</param>
    /// <returns>The hash in the PHC string form</returns>
    internal static string Create(string password)
    {
        var salt = RandomNumberGenerator.GetBytes(DefaultSaltBytes);

        return Create(password, salt, DefaultMemoryKiB, DefaultIterations, DefaultParallelism, DefaultHashBytes);
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
        var saltBytes = salt.ToArray();
        var hash = Compute(password, saltBytes, memoryKiB, iterations, parallelism, hashBytes);

        return string.Create(CultureInfo.InvariantCulture, $"$argon2id$v={Version}$m={memoryKiB},t={iterations},p={parallelism}${EncodeBase64(saltBytes)}${EncodeBase64(hash)}");
    }

    /// <summary>
    /// Computes the argon2id hash of a password.
    /// </summary>
    /// <param name="password">The password; not empty</param>
    /// <param name="salt">The salt</param>
    /// <param name="memoryKiB">The memory in KiB</param>
    /// <param name="iterations">The iterations</param>
    /// <param name="parallelism">The parallelism</param>
    /// <param name="hashBytes">The hash length in bytes</param>
    /// <returns>The hash</returns>
    private static byte[] Compute(string password, byte[] salt, int memoryKiB, int iterations, int parallelism, int hashBytes)
    {
        var bytes = Encoding.UTF8.GetBytes(password);

        try
        {
            using var argon2 = new Argon2id(bytes)
                               {
                                   Salt = salt,
                                   MemorySize = memoryKiB,
                                   Iterations = iterations,
                                   DegreeOfParallelism = parallelism
                               };

            return argon2.GetBytes(hashBytes);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    /// <summary>
    /// Tells whether the parts of a hash text have the shape <c>$argon2id[$v=n]$params$salt$hash</c>.
    /// </summary>
    /// <param name="parts">The text split at <c>$</c></param>
    /// <returns><c>true</c> when they have</returns>
    private static bool HasShape(string[] parts)
    {
        var count = parts.Length;

        return parts is ["", Identifier, ..] && (count == 5 || (count == 6 && parts[2].StartsWith("v=", StringComparison.Ordinal)));
    }

    /// <summary>
    /// Throws the reason when a condition does not hold.
    /// </summary>
    /// <param name="condition">The condition</param>
    /// <param name="reason">The reason</param>
    /// <exception cref="PasswordHashException">The condition does not hold</exception>
    private static void Require(bool condition, string reason)
    {
        if (condition)
        {
            return;
        }

        throw new PasswordHashException(reason);
    }

    /// <summary>
    /// Parses <c>m=&lt;memory&gt;,t=&lt;iterations&gt;,p=&lt;parallelism&gt;</c>.
    /// </summary>
    /// <param name="text">The parameters</param>
    /// <returns>The three values</returns>
    /// <exception cref="PasswordHashException">The text is not in the form</exception>
    private static (long Memory, long Iterations, long Parallelism) ParseParameters(string text)
    {
        var parts = text.Split(',');

        Require(parts is [['m', '=', ..], ['t', '=', ..], ['p', '=', ..]], ReasonForm);

        return (ParseDecimal(parts[0].AsSpan(2)), ParseDecimal(parts[1].AsSpan(2)), ParseDecimal(parts[2].AsSpan(2)));
    }

    /// <summary>
    /// Parses an ASCII decimal of at most 10 digits without a leading zero.
    /// </summary>
    /// <param name="text">The digits</param>
    /// <returns>The value</returns>
    /// <exception cref="PasswordHashException">The text is not such a decimal</exception>
    private static long ParseDecimal(ReadOnlySpan<char> text)
    {
        if (text.Length is 0 or > MaxDigits || (text.Length > 1 && text[0] == '0'))
        {
            throw new PasswordHashException(ReasonForm);
        }

        long value = 0;

        foreach (var digit in text)
        {
            if (digit is < '0' or > '9')
            {
                throw new PasswordHashException(ReasonForm);
            }

            value = (value * 10) + (digit - '0');
        }

        return value;
    }

    /// <summary>
    /// Decodes standard base64 without padding, whose length is not 1 modulo 4 and whose unused trailing bits are zero.
    /// </summary>
    /// <param name="text">The text</param>
    /// <returns>The bytes</returns>
    /// <exception cref="PasswordHashException">The text is not in that form</exception>
    private static byte[] DecodeBase64(string text)
    {
        var remainder = text.Length % 4;

        Require(remainder != 1 && text.All(IsBase64Character), ReasonForm);

        if (remainder != 0)
        {
            var unusedBits = remainder == 2 ? 0b1111 : 0b11;

            Require((Base64Value(text[^1]) & unusedBits) == 0, ReasonForm);
        }

        return Convert.FromBase64String(text + new string('=', (4 - remainder) % 4));
    }

    /// <summary>
    /// Encodes bytes as standard base64 without padding.
    /// </summary>
    /// <param name="bytes">The bytes</param>
    /// <returns>The text</returns>
    private static string EncodeBase64(byte[] bytes)
    {
        return Convert.ToBase64String(bytes).TrimEnd('=');
    }

    /// <summary>
    /// Tells whether a character belongs to the standard base64 alphabet.
    /// </summary>
    /// <param name="character">The character</param>
    /// <returns><c>true</c> when it does</returns>
    private static bool IsBase64Character(char character)
    {
        return character is (>= 'A' and <= 'Z') or (>= 'a' and <= 'z') or (>= '0' and <= '9') or '+' or '/';
    }

    /// <summary>
    /// Returns the value of a base64 character.
    /// </summary>
    /// <param name="character">The character, from the alphabet</param>
    /// <returns>The value 0 to 63</returns>
    private static int Base64Value(char character)
    {
        return character switch
               {
                   >= 'A' and <= 'Z' => character - 'A',
                   >= 'a' and <= 'z' => character - 'a' + 26,
                   >= '0' and <= '9' => character - '0' + 52,
                   '+' => 62,
                   _ => 63
               };
    }

    #endregion // Methods

    #region IPasswordVerifier

    /// <inheritdoc/>
    public bool Verify(string password)
    {
        if (password.Length is 0 or > MaxPasswordLength)
        {
            return false;
        }

        var computed = Compute(password, _salt, MemoryKiB, Iterations, Parallelism, _hash.Length);

        return CryptographicOperations.FixedTimeEquals(computed, _hash);
    }

    #endregion // IPasswordVerifier
}