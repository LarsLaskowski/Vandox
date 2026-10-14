using Vandox.Backend.Security;

namespace Vandox.Backend.Tests;

/// <summary>
/// The argon2id vectors of the Argon2 reference implementation in <c>testdata/argon2/reference-argon2id.txt</c>.
/// </summary>
internal static class ReferenceVectors
{
    #region Constants

    /// <summary>
    /// The path of the vectors file in the repository.
    /// </summary>
    private const string FilePath = "testdata/argon2/reference-argon2id.txt";

    #endregion // Constants

    #region Fields

    private static readonly int[] _inBoundsIndexes = [0, 1, 5, 6, 7];

    #endregion // Fields

    #region Methods

    /// <summary>
    /// Reads all vectors of the file in file order.
    /// </summary>
    /// <returns>The vectors: password, salt as text and the encoded hash</returns>
    internal static IReadOnlyList<(string Password, string Salt, string Hash)> All()
    {
        var vectors = new List<(string Password, string Salt, string Hash)>();

        foreach (var line in File.ReadAllLines(RepositoryFiles.Path(FilePath)))
        {
            if (line.Length == 0 || line[0] == '#')
            {
                continue;
            }

            var fields = line.Split(' ');

            if (fields.Length != 3)
            {
                throw new InvalidOperationException($"{FilePath}: a vector line has {fields.Length} fields, expected 3");
            }

            vectors.Add((fields[0], fields[1], fields[2]));
        }

        return vectors;
    }

    /// <summary>
    /// Returns the vectors inside the accepted bounds (all but lines 3 to 5 of the vectors).
    /// </summary>
    /// <returns>The vectors</returns>
    internal static IEnumerable<(string Password, string Salt, string Hash)> InBounds()
    {
        var all = All();

        return _inBoundsIndexes.Select(index => all[index]);
    }

    /// <summary>
    /// Returns the vectors outside the accepted bounds (lines 3 to 5 of the vectors) with the reason the guard gives.
    /// </summary>
    /// <returns>The encoded hash and the reason</returns>
    internal static IEnumerable<(string Hash, string Reason)> OutOfBounds()
    {
        var all = All();

        yield return (all[2].Hash, PasswordHash.ReasonMemory);
        yield return (all[3].Hash, PasswordHash.ReasonMemory);
        yield return (all[4].Hash, PasswordHash.ReasonIterations);
    }

    #endregion // Methods
}