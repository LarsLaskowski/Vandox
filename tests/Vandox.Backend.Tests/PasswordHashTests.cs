using System.Text;
using System.Text.RegularExpressions;

using Vandox.Backend.Security;

namespace Vandox.Backend.Tests;

/// <summary>
/// Tests for <see cref="PasswordHash"/>
/// </summary>
[TestClass]
public partial class PasswordHashTests
{
    #region Constants

    private const string Valid = "$argon2id$v=19$m=65536,t=2,p=1$c29tZXNhbHQ$CTFhFdXPJO1aFaMaO6Mm5c8y7cJHAph8ArZWb2GRPPc";
    private const string Salt = "c29tZXNhbHQ";
    private const string HashPart = "CTFhFdXPJO1aFaMaO6Mm5c8y7cJHAph8ArZWb2GRPPc";

    #endregion // Constants

    #region Properties

    /// <summary>
    /// Gets the reference vectors inside the accepted bounds.
    /// </summary>
    public static IEnumerable<(string Password, string Salt, string Hash)> InBoundsVectors => ReferenceVectors.InBounds();

    /// <summary>
    /// Gets the reference vectors outside the accepted bounds with their reasons.
    /// </summary>
    public static IEnumerable<(string Hash, string Reason)> OutOfBoundsVectors => ReferenceVectors.OutOfBounds();

    /// <summary>
    /// Gets the refused forms of the hash with the exact reason.
    /// </summary>
    public static IEnumerable<(string Text, string Reason)> RefusedForms
    {
        get
        {
            var salt65 = Convert.ToBase64String(new byte[65]).TrimEnd('=');
            var hash15 = Convert.ToBase64String(new byte[15]).TrimEnd('=');
            var hash65 = Convert.ToBase64String(new byte[65]).TrimEnd('=');
            var differentPassword = ReferenceVectors.All()[6].Hash.Replace('+', '-');

            return
            [
                ("$argon2i$v=19$m=65536,t=2,p=1$c29tZXNhbHQ$CTFhFdXPJO1aFaMaO6Mm5c8y7cJHAph8ArZWb2GRPPc", PasswordHash.ReasonForm),
                ("$argon2d$v=19$m=65536,t=2,p=1$c29tZXNhbHQ$CTFhFdXPJO1aFaMaO6Mm5c8y7cJHAph8ArZWb2GRPPc", PasswordHash.ReasonForm),
                ("$Argon2id$v=19$m=65536,t=2,p=1$c29tZXNhbHQ$CTFhFdXPJO1aFaMaO6Mm5c8y7cJHAph8ArZWb2GRPPc", PasswordHash.ReasonForm),
                ("$argon2id$m=65536,t=2,p=1$c29tZXNhbHQ$CTFhFdXPJO1aFaMaO6Mm5c8y7cJHAph8ArZWb2GRPPc", PasswordHash.ReasonVersion),
                ("$argon2id$v=16$m=65536,t=2,p=1$c29tZXNhbHQ$CTFhFdXPJO1aFaMaO6Mm5c8y7cJHAph8ArZWb2GRPPc", PasswordHash.ReasonVersion),
                ("$argon2id$v=16$m=100,t=1,p=0$c29tZXNhbHQ$CTFhFdXPJO1aFaMaO6Mm5c8y7cJHAph8ArZWb2GRPPc", PasswordHash.ReasonVersion),
                ("$argon2id$v=019$m=65536,t=2,p=1$c29tZXNhbHQ$CTFhFdXPJO1aFaMaO6Mm5c8y7cJHAph8ArZWb2GRPPc", PasswordHash.ReasonForm),
                ("$argon2id$v=19$m=065536,t=2,p=1$c29tZXNhbHQ$CTFhFdXPJO1aFaMaO6Mm5c8y7cJHAph8ArZWb2GRPPc", PasswordHash.ReasonForm),
                ("$argon2id$v=19$m=+65536,t=2,p=1$c29tZXNhbHQ$CTFhFdXPJO1aFaMaO6Mm5c8y7cJHAph8ArZWb2GRPPc", PasswordHash.ReasonForm),
                ("$argon2id$v=19$m= 65536,t=2,p=1$c29tZXNhbHQ$CTFhFdXPJO1aFaMaO6Mm5c8y7cJHAph8ArZWb2GRPPc", PasswordHash.ReasonForm),
                ("$argon2id$v=19$m=\uFF16\uFF15\uFF15\uFF13\uFF16,t=2,p=1$c29tZXNhbHQ$CTFhFdXPJO1aFaMaO6Mm5c8y7cJHAph8ArZWb2GRPPc", PasswordHash.ReasonForm),
                ("$argon2id$v=19$m=65536,p=1,t=2$c29tZXNhbHQ$CTFhFdXPJO1aFaMaO6Mm5c8y7cJHAph8ArZWb2GRPPc", PasswordHash.ReasonForm),
                ("$argon2id$v=19$m=65536,t=2,p=1,data=AAAA$c29tZXNhbHQ$CTFhFdXPJO1aFaMaO6Mm5c8y7cJHAph8ArZWb2GRPPc", PasswordHash.ReasonForm),
                ("$argon2id$v=19$m=65536,t=2,p=1,keyid=AAAA$c29tZXNhbHQ$CTFhFdXPJO1aFaMaO6Mm5c8y7cJHAph8ArZWb2GRPPc", PasswordHash.ReasonForm),
                ("$argon2id$v=19$m=12345678901,t=2,p=1$c29tZXNhbHQ$CTFhFdXPJO1aFaMaO6Mm5c8y7cJHAph8ArZWb2GRPPc", PasswordHash.ReasonForm),
                ("$argon2id$v=19$m=19455,t=2,p=1$c29tZXNhbHQ$CTFhFdXPJO1aFaMaO6Mm5c8y7cJHAph8ArZWb2GRPPc", PasswordHash.ReasonMemory),
                ("$argon2id$v=19$m=262145,t=2,p=1$c29tZXNhbHQ$CTFhFdXPJO1aFaMaO6Mm5c8y7cJHAph8ArZWb2GRPPc", PasswordHash.ReasonMemory),
                ("$argon2id$v=19$m=4294967296,t=2,p=1$c29tZXNhbHQ$CTFhFdXPJO1aFaMaO6Mm5c8y7cJHAph8ArZWb2GRPPc", PasswordHash.ReasonMemory),
                ("$argon2id$v=19$m=100,t=1,p=0$c29tZXNhbHQ$CTFhFdXPJO1aFaMaO6Mm5c8y7cJHAph8ArZWb2GRPPc", PasswordHash.ReasonMemory),
                ("$argon2id$v=19$m=65536,t=1,p=1$c29tZXNhbHQ$CTFhFdXPJO1aFaMaO6Mm5c8y7cJHAph8ArZWb2GRPPc", PasswordHash.ReasonIterations),
                ("$argon2id$v=19$m=65536,t=11,p=1$c29tZXNhbHQ$CTFhFdXPJO1aFaMaO6Mm5c8y7cJHAph8ArZWb2GRPPc", PasswordHash.ReasonIterations),
                ("$argon2id$v=19$m=65536,t=1,p=0$c29tZXNhbHQ$CTFhFdXPJO1aFaMaO6Mm5c8y7cJHAph8ArZWb2GRPPc", PasswordHash.ReasonIterations),
                ("$argon2id$v=19$m=65536,t=2,p=0$c29tZXNhbHQ$CTFhFdXPJO1aFaMaO6Mm5c8y7cJHAph8ArZWb2GRPPc", PasswordHash.ReasonParallelism),
                ("$argon2id$v=19$m=65536,t=2,p=9$c29tZXNhbHQ$CTFhFdXPJO1aFaMaO6Mm5c8y7cJHAph8ArZWb2GRPPc", PasswordHash.ReasonParallelism),
                ("$argon2id$v=19$m=65536,t=2,p=1$c29tZXNhbHQ=$CTFhFdXPJO1aFaMaO6Mm5c8y7cJHAph8ArZWb2GRPPc", PasswordHash.ReasonForm),
                ("$argon2id$v=19$m=65536,t=2,p=1$c29tZXNhbHR$CTFhFdXPJO1aFaMaO6Mm5c8y7cJHAph8ArZWb2GRPPc", PasswordHash.ReasonForm),
                ("$argon2id$v=19$m=65536,t=2,p=1$c29tZXNhbHQxx$CTFhFdXPJO1aFaMaO6Mm5c8y7cJHAph8ArZWb2GRPPc", PasswordHash.ReasonForm),
                ("$argon2id$v=19$m=65536,t=2,p=1$c29tZXNhb+$CTFhFdXPJO1aFaMaO6Mm5c8y7cJHAph8ArZWb2GRPPc", PasswordHash.ReasonForm),
                ("$argon2id$v=19$m=65536,t=2,p=1$c29tZXNhbG/$CTFhFdXPJO1aFaMaO6Mm5c8y7cJHAph8ArZWb2GRPPc", PasswordHash.ReasonForm),
                (differentPassword, PasswordHash.ReasonForm),
                ("$argon2id$v=19$m=65536,t=2,p=1$c29tZXNhbA$CTFhFdXPJO1aFaMaO6Mm5c8y7cJHAph8ArZWb2GRPPc", PasswordHash.ReasonSalt),
                ($"$argon2id$v=19$m=65536,t=2,p=1${salt65}$CTFhFdXPJO1aFaMaO6Mm5c8y7cJHAph8ArZWb2GRPPc", PasswordHash.ReasonSalt),
                ("$argon2id$v=19$m=65536,t=2,p=1$$CTFhFdXPJO1aFaMaO6Mm5c8y7cJHAph8ArZWb2GRPPc", PasswordHash.ReasonSalt),
                ($"$argon2id$v=19$m=65536,t=2,p=1$c29tZXNhbA${hash15}", PasswordHash.ReasonSalt),
                ($"$argon2id$v=19$m=65536,t=2,p=1${Salt}${hash15}", PasswordHash.ReasonHash),
                ($"$argon2id$v=19$m=65536,t=2,p=1${Salt}${hash65}", PasswordHash.ReasonHash),
                ($"$argon2id$v=19$m=65536,t=2,p=1${Salt}", PasswordHash.ReasonForm),
                ($"{Valid}$", PasswordHash.ReasonForm),
                ($"{Valid} ", PasswordHash.ReasonForm),
                ($" {Valid}", PasswordHash.ReasonForm),
                (string.Empty, PasswordHash.ReasonForm)
            ];
        }
    }

    #endregion // Properties

    #region Methods

    /// <summary>
    /// The five reference vectors inside the bounds parse and verify their password, and each refuses a wrong password and the empty one.
    /// </summary>
    /// <param name="password">The password</param>
    /// <param name="salt">The salt as text</param>
    /// <param name="hash">The encoded hash</param>
    [TestMethod]
    [DynamicData(nameof(InBoundsVectors))]
    public void PasswordHashVerifiesReferenceVectors(string password, string salt, string hash)
    {
        // Arrange
        var parameters = Parameters().Match(hash);

        // Act
        var parsed = PasswordHash.Parse(hash);

        // Assert
        Assert.IsTrue(parameters.Success, "the vector has parameters");
        Assert.AreEqual(int.Parse(parameters.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture), parsed.MemoryKiB, "memory of the vector");
        Assert.AreEqual(int.Parse(parameters.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture), parsed.Iterations, "iterations of the vector");
        Assert.AreEqual(int.Parse(parameters.Groups[3].Value, System.Globalization.CultureInfo.InvariantCulture), parsed.Parallelism, "parallelism of the vector");
        Assert.AreEqual(Encoding.ASCII.GetByteCount(salt), parsed.SaltLength, "salt length of the vector");
        Assert.AreEqual(32, parsed.HashLength, "hash length of the vector");
        Assert.IsTrue(parsed.Verify(password), "the right password verifies");
        Assert.IsFalse(parsed.Verify(password + "x"), "a wrong password does not verify");
        Assert.IsFalse(parsed.Verify(string.Empty), "the empty password does not verify");
    }

    /// <summary>
    /// The three reference vectors outside the bounds are refused with their reason.
    /// </summary>
    /// <param name="hash">The encoded hash</param>
    /// <param name="reason">The expected reason</param>
    [TestMethod]
    [DynamicData(nameof(OutOfBoundsVectors))]
    public void PasswordHashRefusesWeakReferenceVectors(string hash, string reason)
    {
        // Act
        var exception = Assert.ThrowsExactly<PasswordHashException>(() => PasswordHash.Parse(hash), "weak vector");

        // Assert
        Assert.AreEqual(reason, exception.Message, "reason");
    }

    /// <summary>
    /// Every refused form throws with exactly its reason.
    /// </summary>
    /// <param name="text">The text</param>
    /// <param name="reason">The expected reason</param>
    [TestMethod]
    [DynamicData(nameof(RefusedForms))]
    public void PasswordHashParseRefusesInvalidForms(string text, string reason)
    {
        // Act
        var exception = Assert.ThrowsExactly<PasswordHashException>(() => PasswordHash.Parse(text), "invalid form");

        // Assert
        Assert.AreEqual(reason, exception.Message, "reason");
        Assert.DoesNotContain(HashPart, exception.Message, "the reason does not show the hash");
    }

    /// <summary>
    /// A created hash has the default form and parameters, a new salt on every call, and verifies its password.
    /// </summary>
    [TestMethod]
    public void PasswordHashCreateRoundTrips()
    {
        // Arrange
        const string password = "correct horse battery staple";

        // Act
        var first = PasswordHash.Create(password);
        var second = PasswordHash.Create(password);
        var parsed = PasswordHash.Parse(first);

        // Assert
        Assert.IsTrue(DefaultForm().IsMatch(first), $"the form of the created hash: {first}");
        Assert.AreNotEqual(first, second, "two calls give different salts");
        Assert.AreEqual(PasswordHash.DefaultMemoryKiB, parsed.MemoryKiB, "memory");
        Assert.AreEqual(PasswordHash.DefaultIterations, parsed.Iterations, "iterations");
        Assert.AreEqual(PasswordHash.DefaultParallelism, parsed.Parallelism, "parallelism");
        Assert.AreEqual(PasswordHash.DefaultSaltBytes, parsed.SaltLength, "salt length");
        Assert.AreEqual(PasswordHash.DefaultHashBytes, parsed.HashLength, "hash length");
        Assert.IsTrue(parsed.Verify(password), "the password verifies");
        Assert.IsFalse(parsed.Verify(password + "!"), "another password does not verify");
    }

    /// <summary>
    /// Creating with the explicit salt and parameters reproduces a reference vector byte for byte.
    /// </summary>
    /// <param name="index">The index of the vector in the file</param>
    [TestMethod]
    [DataRow(0)]
    [DataRow(3)]
    public void PasswordHashCreateReproducesReferenceVector(int index)
    {
        // Arrange
        var vector = ReferenceVectors.All()[index];
        var parameters = Parameters().Match(vector.Hash);
        var memory = int.Parse(parameters.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
        var iterations = int.Parse(parameters.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture);
        var parallelism = int.Parse(parameters.Groups[3].Value, System.Globalization.CultureInfo.InvariantCulture);

        // Act
        var created = PasswordHash.Create(vector.Password, Encoding.ASCII.GetBytes(vector.Salt), memory, iterations, parallelism, 32);

        // Assert
        Assert.AreEqual(vector.Hash, created, "the created hash equals the reference vector");
    }

    /// <summary>
    /// A password of 1,025 characters never verifies, the one of 1,024 characters does.
    /// </summary>
    [TestMethod]
    public void PasswordHashVerifyRejectsOverlongPassword()
    {
        // Arrange
        var password = new string('p', PasswordHash.MaxPasswordLength);
        var hash = PasswordHash.Parse(PasswordHash.Create(password, Encoding.ASCII.GetBytes("somesalt"), PasswordHash.MinMemoryKiB, PasswordHash.MinIterations, PasswordHash.MinParallelism, 32));

        // Act
        var exact = hash.Verify(password);
        var longer = hash.Verify(password + "p");

        // Assert
        Assert.IsTrue(exact, "1,024 characters verify");
        Assert.IsFalse(longer, "1,025 characters do not verify");
    }

    #endregion // Methods

    #region Patterns

    [GeneratedRegex("m=([0-9]+),t=([0-9]+),p=([0-9]+)")]
    private static partial Regex Parameters();

    [GeneratedRegex("^\\$argon2id\\$v=19\\$m=65536,t=3,p=4\\$[A-Za-z0-9+/]{22}\\$[A-Za-z0-9+/]{43}$")]
    private static partial Regex DefaultForm();

    #endregion // Patterns
}