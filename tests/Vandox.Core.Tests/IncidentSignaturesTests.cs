using Vandox.Core.Incidents;
using Vandox.Core.Signatures;

namespace Vandox.Core.Tests;

/// <summary>
/// Tests for <see cref="IncidentSignatures"/>
/// </summary>
[TestClass]
public class IncidentSignaturesTests
{
    #region Methods

    /// <summary>
    /// Every ID that incidents are built from names a signature of the built-in catalog, and no other ID is defined.
    /// </summary>
    [TestMethod]
    public void IncidentSignaturesNameBuiltInSignatures()
    {
        // Arrange
        var ids = BuiltInSignatures.Catalog.Signatures.Select(signature => signature.Id).ToList();

        // Act
        var used = typeof(IncidentSignatures).GetFields()
                                             .Where(field => field.IsLiteral)
                                             .Select(field => (string)field.GetRawConstantValue()!)
                                             .Order(StringComparer.Ordinal)
                                             .ToList();

        // Assert
        Assert.AreSequenceEqual(["boot", "oom_kill", "service_failed", "service_start", "service_stop"], used, "the IDs incidents are built from");

        foreach (var id in used)
        {
            Assert.Contains(id, ids, $"signature {id} is in the built-in catalog");
        }
    }

    #endregion // Methods
}