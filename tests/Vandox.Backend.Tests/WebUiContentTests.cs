using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace Vandox.Backend.Tests;

/// <summary>
/// Contract tests over the files of the web UI: sources, brand copies, style sheet tokens and the vendored chart library.
/// </summary>
[TestClass]
public partial class WebUiContentTests
{
    #region Constants

    private const string BackendRoot = "src/Vandox.Backend";

    #endregion // Constants

    #region Fields

    private static readonly string[] _ownScripts = ["js/app.js", "js/charts.js"];

    private static readonly string[] _requiredStyleSheets = ["app.css", "lib/uplot/uPlot.min.css"];

    private static readonly string[] _brandFiles = ["brand/vandox-favicon.svg", "brand/vandox-logo-horizontal.svg", "brand/vandox-logo-horizontal-dark.svg"];

    #endregion // Fields

    #region Methods

    /// <summary>
    /// Neither the markup nor the code under <c>Components/</c> holds inline code, an external URL or a way to pass text as raw HTML.
    /// </summary>
    [TestMethod]
    public void ComponentsContainNoInlineCodeOrExternalUrls()
    {
        // Arrange
        var root = RepositoryFiles.Path($"{BackendRoot}/Components");
        var razor = Directory.GetFiles(root, "*.razor", SearchOption.AllDirectories);
        var code = Directory.GetFiles(root, "*.cs", SearchOption.AllDirectories);
        var findings = new List<string>();

        // Act
        foreach (var file in razor)
        {
            var text = File.ReadAllText(file);

            findings.AddRange(WebSourceScanner.FindInMarkup(text).Select(rule => $"{Relative(root, file)}: {rule}"));
            findings.AddRange(WebSourceScanner.FindInComponentCode(text).Select(rule => $"{Relative(root, file)}: {rule}"));
        }

        foreach (var file in code)
        {
            findings.AddRange(WebSourceScanner.FindInComponentCode(File.ReadAllText(file)).Select(rule => $"{Relative(root, file)}: {rule}"));
        }

        // Assert
        Assert.IsNotEmpty(razor, "component markup files were found");
        Assert.IsNotEmpty(code, "component code files were found");
        Assert.IsEmpty(findings, $"findings: {string.Join("; ", findings)}");
    }

    /// <summary>
    /// The own scripts hold no external URL, no escape and no dynamic code.
    /// </summary>
    [TestMethod]
    public void OwnScriptsContainNoExternalUrlOrDynamicCode()
    {
        // Arrange
        var root = RepositoryFiles.Path($"{BackendRoot}/wwwroot");
        var scripts = Directory.Exists(root) ? Directory.GetFiles(root, "*.js", SearchOption.AllDirectories).Where(file => IsOwn(root, file)).Select(file => Relative(root, file)).Order().ToArray() : [];
        var findings = new List<string>();

        // Act
        foreach (var script in scripts)
        {
            findings.AddRange(WebSourceScanner.FindInScript(File.ReadAllText(Path.Combine(root, script))).Select(rule => $"{script}: {rule}"));
        }

        // Assert
        CollectionAssert.IsSubsetOf(_ownScripts, scripts, "the own scripts exist");
        Assert.IsEmpty(findings, $"findings: {string.Join("; ", findings)}");
    }

    /// <summary>
    /// No style sheet loads an external resource, and the own one holds no escape.
    /// </summary>
    [TestMethod]
    public void StyleSheetsReferenceNoExternalResources()
    {
        // Arrange
        var root = RepositoryFiles.Path($"{BackendRoot}/wwwroot");
        var sheets = Directory.Exists(root) ? Directory.GetFiles(root, "*.css", SearchOption.AllDirectories).Select(file => Relative(root, file)).Order().ToArray() : [];
        var findings = new List<string>();

        // Act
        foreach (var sheet in sheets)
        {
            var own = IsOwn(root, Path.Combine(root, sheet));

            findings.AddRange(WebSourceScanner.FindInStyleSheet(File.ReadAllText(Path.Combine(root, sheet)), own).Select(rule => $"{sheet}: {rule}"));
        }

        // Assert
        CollectionAssert.IsSubsetOf(_requiredStyleSheets, sheets, "the style sheets exist");
        Assert.IsEmpty(findings, $"findings: {string.Join("; ", findings)}");
    }

    /// <summary>
    /// Every SVG of the web UI is a byte-for-byte copy of the file of the same name in <c>docs/assets/</c>, kept in <c>wwwroot/brand/</c>.
    /// </summary>
    [TestMethod]
    public void BrandFilesMatchDocsAssets()
    {
        // Arrange
        var root = RepositoryFiles.Path($"{BackendRoot}/wwwroot");
        var assets = RepositoryFiles.Path("docs/assets");
        var files = Directory.Exists(root) ? Directory.GetFiles(root, "*.svg", SearchOption.AllDirectories).Select(file => Relative(root, file)).Order().ToArray() : [];

        // Act
        var misplaced = files.Where(file => file.Split('/')[0] != "brand").ToArray();
        var copies = files.Where(file => file.Split('/')[0] == "brand").ToArray();
        var same = copies.Where(file => SameBytes(Path.Combine(root, file), Path.Combine(assets, Path.GetFileName(file)))).ToArray();
        var different = copies.Except(same).ToArray();

        // Assert
        Assert.IsEmpty(misplaced, "SVG files outside wwwroot/brand/");
        Assert.IsEmpty(different, "copies that differ from docs/assets/");
        CollectionAssert.IsSubsetOf(_brandFiles, files, "the three brand files exist");
    }

    /// <summary>
    /// The style sheet defines each color token of <c>docs/BRANDING.md</c> with its light and dark value and the theme selectors.
    /// </summary>
    [TestMethod]
    public void StyleSheetUsesBrandingTokens()
    {
        // Arrange
        var tokens = ReadTokens(File.ReadAllText(RepositoryFiles.Path("docs/BRANDING.md")));
        var path = RepositoryFiles.Path($"{BackendRoot}/wwwroot/app.css");
        var css = File.Exists(path) ? File.ReadAllText(path) : string.Empty;

        // Act
        var wanted = tokens.SelectMany(token => new[] { (token.Name, Value: token.Light), (token.Name, Value: token.Dark) });
        var missing = wanted.Where(item => Regex.Count(css, $"{Regex.Escape(item.Name)}\\s*:\\s*{Regex.Escape(item.Value)}", RegexOptions.IgnoreCase) == 0)
                            .Select(item => $"{item.Name}: {item.Value}")
                            .ToArray();

        // Assert
        Assert.IsNotEmpty(tokens, "the color token table was read");
        Assert.IsEmpty(missing, $"token values missing from app.css: {string.Join("; ", missing)}");
        Assert.Contains("[data-theme=\"dark\"]", css, "dark theme selector");
        Assert.Contains("[data-theme=\"light\"]", css, "light theme selector");
        Assert.Contains("prefers-color-scheme: dark", css, "system dark media query");
    }

    /// <summary>
    /// The vendored chart library is the release it is pinned to (uPlot 1.6.32).
    /// </summary>
    /// <param name="file">The file below <c>wwwroot/lib/uplot/</c></param>
    /// <param name="hash">The expected SHA-256 value</param>
    [TestMethod]
    [DataRow("uPlot.esm.js", "5dd9b3281aa64b461b42d9945f6adb2649d346502b12281a9ae0d46599a80eba")]
    [DataRow("uPlot.min.css", "df630c6a8d6f8eeaff264b50f73ce5b114f646ffd9a0bb74f049b0a00135fa04")]
    [DataRow("LICENSE", "8f989229699b4fe2f1a0432d0e9edc338a8a911e250e2d1b01ecd770a5f5b1bd")]
    public void VendoredUPlotMatchesRelease(string file, string hash)
    {
        // Arrange
        var path = RepositoryFiles.Path($"{BackendRoot}/wwwroot/lib/uplot/{file}");

        // Act
        var actual = File.Exists(path) ? Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path))) : "file is missing";

        // Assert
        Assert.AreEqual(hash, actual, $"SHA-256 of {file}");
    }

    /// <summary>
    /// Returns the path of a file relative to a root with forward slashes.
    /// </summary>
    /// <param name="root">The root</param>
    /// <param name="file">The file</param>
    /// <returns>The relative path</returns>
    private static string Relative(string root, string file)
    {
        return Path.GetRelativePath(root, file).Replace('\\', '/');
    }

    /// <summary>
    /// Tells whether a file is an own file, that is not below the vendored libraries in <c>lib/</c>.
    /// </summary>
    /// <param name="root">The root</param>
    /// <param name="file">The file</param>
    /// <returns><c>true</c> when the file is an own file</returns>
    private static bool IsOwn(string root, string file)
    {
        return Relative(root, file).Split('/')[0] != "lib";
    }

    /// <summary>
    /// Tells whether the second file exists and has the bytes of the first.
    /// </summary>
    /// <param name="first">The first file</param>
    /// <param name="second">The second file</param>
    /// <returns><c>true</c> when the bytes are the same</returns>
    private static bool SameBytes(string first, string second)
    {
        return File.Exists(second) && File.ReadAllBytes(first).AsSpan().SequenceEqual(File.ReadAllBytes(second));
    }

    /// <summary>
    /// Reads the rows of the color token table.
    /// </summary>
    /// <param name="branding">The text of the branding document</param>
    /// <returns>The custom property, the light value and the dark value of each row</returns>
    private static List<(string Name, string Light, string Dark)> ReadTokens(string branding)
    {
        var tokens = new List<(string Name, string Light, string Dark)>();
        var inTable = false;

        foreach (var line in branding.Split('\n').Select(row => row.TrimEnd('\r')))
        {
            if (line.StartsWith("## ", StringComparison.Ordinal))
            {
                inTable = line.StartsWith("## Color tokens", StringComparison.Ordinal);
            }
            else if (inTable && line.StartsWith('|'))
            {
                var cells = line.Trim('|').Split('|').Select(cell => cell.Trim().Trim('`')).ToArray();

                if (cells.Length >= 4 && TokenName().IsMatch(cells[1]))
                {
                    tokens.Add((cells[1], cells[2], cells[3]));
                }
            }
        }

        return tokens;
    }

    #endregion // Methods

    #region Patterns

    [GeneratedRegex("^--[a-z]")]
    private static partial Regex TokenName();

    #endregion // Patterns
}