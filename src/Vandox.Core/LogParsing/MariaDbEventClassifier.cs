namespace Vandox.Core.LogParsing;

#pragma warning disable RH2003, S2325

/// <summary>
/// Classifies the headers of the MariaDB error log into events and tracks an open crash recovery.
/// </summary>
internal sealed class MariaDbEventClassifier
{
    #region Methods

    /// <summary>
    /// Classifies a header; the open recovery is tracked in call order.
    /// </summary>
    /// <param name="line">The header</param>
    /// <returns>A value of <see cref="MariaDbEvents"/>, or an empty string for none</returns>
    internal string Classify(MariaDbLine line)
    {
        throw new NotImplementedException();
    }

    #endregion // Methods
}