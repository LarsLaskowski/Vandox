using Vandox.Backend.Reports;

namespace Vandox.Backend.Tests;

/// <summary>
/// Tests for <see cref="ReportAddress"/>
/// </summary>
[TestClass]
public class ReportAddressTests
{
    #region Constants

    private const string Repeated = "The address gives the parameter lang more than once.";

    private const string Invalid = "The address has an invalid value for the parameter lang.";

    #endregion // Constants

    #region Methods

    /// <summary>
    /// An address without the parameter asks for English, with or without a question mark.
    /// </summary>
    /// <param name="query">The query string</param>
    [TestMethod]
    [DataRow("")]
    [DataRow("?")]
    [DataRow("x=1")]
    [DataRow("?language=de")]
    public void ReportAddressParseDefaultsToEnglish(string query)
    {
        // Act
        var address = ReportAddress.Parse(query);

        // Assert
        Assert.AreEqual("en", address.Language, "English without the parameter");
        Assert.AreEqual(string.Empty, address.Error, "no error");
    }

    /// <summary>
    /// The language is read from the key in any letter case, with the key and the value percent-encoded, with or without the question mark.
    /// </summary>
    /// <param name="query">The query string</param>
    /// <param name="language">The language that is asked for</param>
    [TestMethod]
    [DataRow("lang=en", "en")]
    [DataRow("lang=de", "de")]
    [DataRow("?lang=de", "de")]
    [DataRow("LANG=de", "de")]
    [DataRow("Lang=en", "en")]
    [DataRow("%6Cang=de", "de")]
    [DataRow("lang=%64%65", "de")]
    [DataRow("lang=%65%6e", "en")]
    public void ReportAddressParseReadsTheLanguage(string query, string language)
    {
        // Act
        var address = ReportAddress.Parse(query);

        // Assert
        Assert.AreEqual(language, address.Language, "the language of the address");
        Assert.AreEqual(string.Empty, address.Error, "no error");
    }

    /// <summary>
    /// Any value but exactly <c>en</c> or <c>de</c> is refused with the fixed text, in every form that looks like one of them.
    /// </summary>
    /// <param name="query">The query string</param>
    [TestMethod]
    [DataRow("lang=DE")]
    [DataRow("lang=En")]
    [DataRow("lang=de-DE")]
    [DataRow("lang=")]
    [DataRow("lang")]
    [DataRow("?lang=")]
    [DataRow("lang=%20de")]
    [DataRow("lang=de%20")]
    [DataRow("lang=+de")]
    [DataRow("lang=%E2%80%AEde")]
    [DataRow("lang=en%00")]
    [DataRow("lang=fr")]
    [DataRow("lang=deutsch")]
    public void ReportAddressParseRefusesOtherValues(string query)
    {
        // Act
        var address = ReportAddress.Parse(query);

        // Assert
        Assert.AreEqual(Invalid, address.Error, "the invalid-value text");
    }

    /// <summary>
    /// A key given twice, in any letter case or with an empty value, is refused with the fixed text, even when both values are the same.
    /// </summary>
    /// <param name="query">The query string</param>
    [TestMethod]
    [DataRow("lang=en&lang=de")]
    [DataRow("lang=en&LANG=en")]
    [DataRow("lang=de&lang=de")]
    [DataRow("lang=&lang=de")]
    [DataRow("lang=de&lang=")]
    [DataRow("lang=en&%4Cang=en")]
    [DataRow("?lang=de&x=1&Lang=de")]
    public void ReportAddressParseRefusesARepeatedParameter(string query)
    {
        // Act
        var address = ReportAddress.Parse(query);

        // Assert
        Assert.AreEqual(Repeated, address.Error, "the repeat text");
    }

    /// <summary>
    /// Other keys are ignored, whatever their values, and do not make a valid language invalid.
    /// </summary>
    /// <param name="query">The query string</param>
    /// <param name="language">The language that is asked for</param>
    [TestMethod]
    [DataRow("x=1&lang=de&y=2", "de")]
    [DataRow("language=fr&lang=en", "en")]
    [DataRow("a=&b&lang=en", "en")]
    public void ReportAddressParseIgnoresOtherKeys(string query, string language)
    {
        // Act
        var address = ReportAddress.Parse(query);

        // Assert
        Assert.AreEqual(string.Empty, address.Error, "no error");
        Assert.AreEqual($"?lang={language}", address.ToQuery(), "only the language is kept");
    }

    /// <summary>
    /// The query string that is written holds the language and a leading question mark.
    /// </summary>
    /// <param name="query">The query string that is read</param>
    /// <param name="written">The query string that is written</param>
    [TestMethod]
    [DataRow("", "?lang=en")]
    [DataRow("lang=en", "?lang=en")]
    [DataRow("lang=de", "?lang=de")]
    [DataRow("LANG=%64e", "?lang=de")]
    public void ReportAddressToQueryWritesTheLanguage(string query, string written)
    {
        // Act
        var address = ReportAddress.Parse(query);

        // Assert
        Assert.AreEqual(written, address.ToQuery(), "the query string");
    }

    #endregion // Methods
}