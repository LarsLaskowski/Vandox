using Vandox.Backend.Reports;

namespace Vandox.Backend.Tests;

/// <summary>
/// Tests for <see cref="ReportText"/>
/// </summary>
[TestClass]
public class ReportTextTests
{
    #region Methods

    /// <summary>
    /// The language names en and de return the English and the German table, and every label is set in both.
    /// </summary>
    [TestMethod]
    public void ReportTextForReturnsEnglishAndGerman()
    {
        // Arrange
        var labels = typeof(ReportText).GetProperties().Where(property => property.PropertyType == typeof(string) && property.GetMethod is { IsStatic: false }).ToList();

        // Act
        var english = ReportText.For("en");
        var german = ReportText.For("de");

        // Assert
        Assert.AreSame(ReportText.English, english, "English table");
        Assert.AreSame(ReportText.German, german, "German table");
        Assert.AreEqual("en", ReportText.English.Language, "English language name");
        Assert.AreEqual("de", ReportText.German.Language, "German language name");
        Assert.AreEqual('.', ReportText.English.DecimalSeparator, "English decimal separator");
        Assert.AreEqual(',', ReportText.German.DecimalSeparator, "German decimal separator");
        Assert.AreSequenceEqual(["en", "de"], ReportText.Languages, "the languages");
        Assert.IsNotEmpty(labels, "the table has labels");

        foreach (var label in labels)
        {
            Assert.IsFalse(string.IsNullOrEmpty((string?)label.GetValue(ReportText.English)), $"English {label.Name} is set");
            Assert.IsFalse(string.IsNullOrEmpty((string?)label.GetValue(ReportText.German)), $"German {label.Name} is set");
        }

        Assert.AreNotEqual(ReportText.English.Title, ReportText.German.Title, "the titles differ");
        Assert.AreNotEqual(ReportText.English.SummaryHeading, ReportText.German.SummaryHeading, "the summary headings differ");
    }

    /// <summary>
    /// Any other value than the two names, also another case or a region, has no table.
    /// </summary>
    /// <param name="language">The language</param>
    [TestMethod]
    [DataRow("fr")]
    [DataRow("EN")]
    [DataRow("De")]
    [DataRow("de-DE")]
    [DataRow("de_DE")]
    [DataRow("")]
    [DataRow(" en")]
    [DataRow("en ")]
    [DataRow("de\n")]
    public void ReportTextForRefusesOtherLanguages(string language)
    {
        // Act
        var text = ReportText.For(language);

        // Assert
        Assert.IsNull(text, $"language {language}");
    }

    /// <summary>
    /// The consumer templates of both tables start with the program, so that the program can be set as code in front of the rest.
    /// </summary>
    /// <param name="language">The language</param>
    [TestMethod]
    [DataRow("en")]
    [DataRow("de")]
    public void ReportTextConsumerLinesStartWithTheProgram(string language)
    {
        // Arrange
        var text = ReportText.For(language);

        // Act
        var permanent = text?.ConsumerPermanent ?? string.Empty;
        var transient = text?.ConsumerTransient ?? string.Empty;

        // Assert
        Assert.StartsWith("{0}", permanent, $"the permanent consumer template of {language} starts with the program");
        Assert.StartsWith("{0}", transient, $"the transient consumer template of {language} starts with the program");
        Assert.DoesNotContain("{0}", permanent[3..], $"the program appears once in the permanent template of {language}");
        Assert.DoesNotContain("{0}", transient[3..], $"the program appears once in the transient template of {language}");
    }

    #endregion // Methods
}