namespace Vandox.Storage.Tests;

/// <summary>
/// Tests for <see cref="TemplateQueries"/>
/// </summary>
[TestClass]
public class TemplateQueriesTests
{
    #region Methods

    /// <summary>
    /// A missing query, or a missing source in a query, is refused with the name of the argument.
    /// </summary>
    /// <param name="scenario">The scenario</param>
    /// <param name="parameter">The expected parameter name</param>
    [TestMethod]
    [DataRow("hours query null", "query")]
    [DataRow("new query null", "query")]
    [DataRow("new query source null", "query.Source")]
    [DataRow("spike query null", "query")]
    [DataRow("spike query source null", "query.Source")]
    [DataRow("message null", "message")]
    public void TemplateQueriesValidateRefusesMissingArguments(string scenario, string parameter)
    {
        // Act
        var exception = Assert.ThrowsExactly<ArgumentNullException>(() => Validate(scenario), scenario);

        // Assert
        Assert.AreEqual(parameter, exception.ParamName, $"parameter name for {scenario}");
    }

    /// <summary>
    /// Validates the argument of one scenario.
    /// </summary>
    /// <param name="scenario">The scenario</param>
    private static void Validate(string scenario)
    {
        switch (scenario)
        {
            case "hours query null":
                {
                    TemplateQueries.Validate((TemplateHourQuery)null!);
                }
                break;

            case "new query null":
                {
                    TemplateQueries.Validate((NewTemplateQuery)null!);
                }
                break;

            case "new query source null":
                {
                    TemplateQueries.Validate(new NewTemplateQuery
                                             {
                                                 Source = null!
                                             });
                }
                break;

            case "spike query null":
                {
                    TemplateQueries.Validate((TemplateSpikeQuery)null!);
                }
                break;

            case "spike query source null":
                {
                    TemplateQueries.Validate(new TemplateSpikeQuery
                                             {
                                                 Source = null!
                                             });
                }
                break;

            default:
                {
                    TemplateQueries.ValidateMessage(null!);
                }
                break;
        }
    }

    #endregion // Methods
}