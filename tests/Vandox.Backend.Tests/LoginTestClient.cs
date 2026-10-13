using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;

namespace Vandox.Backend.Tests;

/// <summary>
/// An <see cref="HttpClient"/> for the web port of a running service: no automatic redirects, and its own cookie jar that, unlike a
/// <see cref="CookieContainer"/>, sends a <c>Secure</c> cookie over plain HTTP too, so the answers of a service behind a proxy can be followed.
/// It reads the antiforgery token and handler value from the login form and posts forms with them.
/// </summary>
internal sealed partial class LoginTestClient : IDisposable
{
    #region Constants

    /// <summary>
    /// The name of the session cookie.
    /// </summary>
    internal const string SessionCookie = "vandox_session";

    /// <summary>
    /// The name of the antiforgery cookie.
    /// </summary>
    internal const string AntiforgeryCookie = "vandox_csrf";

    /// <summary>
    /// The name of the antiforgery form field.
    /// </summary>
    internal const string TokenField = "__RequestVerificationToken";

    /// <summary>
    /// The name of the form field that selects the form handler.
    /// </summary>
    internal const string HandlerField = "_handler";

    /// <summary>
    /// The name of the password field of the login form.
    /// </summary>
    internal const string PasswordField = "password";

    #endregion // Constants

    #region Fields

    private readonly HttpClient _client;
    private readonly Dictionary<string, string> _cookies = new(StringComparer.Ordinal);
    private readonly string _baseAddress;

    #endregion // Fields

    #region Constructors

    /// <summary>
    /// Initializes a new instance of the <see cref="LoginTestClient"/> class.
    /// </summary>
    /// <param name="port">The web port on the loopback address</param>
    internal LoginTestClient(int port)
    {
        _baseAddress = $"http://127.0.0.1:{port}";
        _client = new HttpClient(new HttpClientHandler
                                 {
                                     AllowAutoRedirect = false,
                                     UseCookies = false
                                 })
                  {
                      Timeout = TimeSpan.FromSeconds(30)
                  };
    }

    #endregion // Constructors

    #region Properties

    /// <summary>
    /// Gets a value indicating whether the cookie jar holds a session cookie.
    /// </summary>
    internal bool HasSession => _cookies.ContainsKey(SessionCookie);

    #endregion // Properties

    #region Methods

    /// <summary>
    /// Reads the values of the <c>Set-Cookie</c> headers of a response.
    /// </summary>
    /// <param name="response">The response</param>
    /// <returns>The header values, empty when there are none</returns>
    internal static IReadOnlyList<string> SetCookies(HttpResponseMessage response)
    {
        return response.Headers.TryGetValues("Set-Cookie", out var values) ? values.ToArray() : [];
    }

    /// <summary>
    /// Reads the target of a redirect as path and query: the framework redirects of a page answer an absolute address on the host of the request, the redirects of the authentication a relative one.
    /// </summary>
    /// <param name="response">The response</param>
    /// <returns>The path and query of the <c>Location</c> header, <c>null</c> when there is none</returns>
    internal static string? LocationOf(HttpResponseMessage response)
    {
        var location = response.Headers.Location;

        if (location is null)
        {
            return null;
        }

        return location.IsAbsoluteUri ? location.PathAndQuery : location.OriginalString;
    }

    /// <summary>
    /// Finds the <c>Set-Cookie</c> header of one cookie.
    /// </summary>
    /// <param name="response">The response</param>
    /// <param name="name">The name of the cookie</param>
    /// <returns>The header value, <c>null</c> when the response does not set the cookie</returns>
    internal static string? SetCookie(HttpResponseMessage response, string name)
    {
        return SetCookies(response).FirstOrDefault(value => value.StartsWith(name + "=", StringComparison.Ordinal));
    }

    /// <summary>
    /// Splits a <c>Set-Cookie</c> header value into its attributes, lower case, without the name and value.
    /// </summary>
    /// <param name="setCookie">The header value</param>
    /// <returns>The attributes such as <c>httponly</c>, <c>samesite=lax</c> and <c>path=/</c></returns>
    internal static IReadOnlyList<string> CookieAttributes(string setCookie)
    {
        return setCookie.Split(';').Skip(1).Select(attribute => attribute.Trim().ToLowerInvariant()).ToArray();
    }

    /// <summary>
    /// Reads the hidden fields of the first form of a page.
    /// </summary>
    /// <param name="html">The page</param>
    /// <returns>The values of the hidden inputs by name, HTML decoded</returns>
    internal static Dictionary<string, string> HiddenFields(string html)
    {
        var fields = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (Match input in InputElement().Matches(html))
        {
            var attributes = Attribute().Matches(input.Value).ToDictionary(match => match.Groups[1].Value.ToLowerInvariant(), match => WebUtility.HtmlDecode(match.Groups[2].Value));

            if (attributes.GetValueOrDefault("type") == "hidden" && attributes.TryGetValue("name", out var name))
            {
                fields.TryAdd(name, attributes.GetValueOrDefault("value", string.Empty));
            }
        }

        return fields;
    }

    /// <summary>
    /// Copies the cookies of another client into this one.
    /// </summary>
    /// <param name="other">The other client</param>
    internal void CopyCookiesFrom(LoginTestClient other)
    {
        foreach (var (name, value) in other._cookies)
        {
            _cookies[name] = value;
        }
    }

    /// <summary>
    /// Sends a request and takes the cookies of the answer into the jar.
    /// </summary>
    /// <param name="method">The method</param>
    /// <param name="path">The path and query</param>
    /// <param name="content">The body, <c>null</c> for none</param>
    /// <param name="headers">Further request headers, <c>null</c> for none</param>
    /// <param name="cancellationToken">Cancels the request</param>
    /// <returns>A task that returns the response; the caller disposes it</returns>
    internal async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, HttpContent? content, IReadOnlyDictionary<string, string>? headers, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(method, _baseAddress + path)
                            {
                                Content = content
                            };

        if (_cookies.Count > 0)
        {
            request.Headers.TryAddWithoutValidation("Cookie", string.Join("; ", _cookies.Select(cookie => $"{cookie.Key}={cookie.Value}")));
        }

        foreach (var (name, value) in headers ?? new Dictionary<string, string>())
        {
            request.Headers.TryAddWithoutValidation(name, value);
        }

        var response = await _client.SendAsync(request, cancellationToken).ConfigureAwait(false);

        foreach (var setCookie in SetCookies(response))
        {
            Absorb(setCookie);
        }

        return response;
    }

    /// <summary>
    /// Sends a <c>GET</c> request.
    /// </summary>
    /// <param name="path">The path and query</param>
    /// <param name="cancellationToken">Cancels the request</param>
    /// <param name="headers">Further request headers, <c>null</c> for none</param>
    /// <returns>A task that returns the response; the caller disposes it</returns>
    internal Task<HttpResponseMessage> GetAsync(string path, CancellationToken cancellationToken, IReadOnlyDictionary<string, string>? headers = null)
    {
        return SendAsync(HttpMethod.Get, path, null, headers, cancellationToken);
    }

    /// <summary>
    /// Reads the hidden fields of the first form of a page.
    /// </summary>
    /// <param name="path">The path of the page</param>
    /// <param name="cancellationToken">Cancels the request</param>
    /// <param name="headers">Further request headers, <c>null</c> for none</param>
    /// <returns>A task that returns the hidden fields; they hold the antiforgery token</returns>
    internal async Task<Dictionary<string, string>> ReadFormFieldsAsync(string path, CancellationToken cancellationToken, IReadOnlyDictionary<string, string>? headers = null)
    {
        using var response = await GetAsync(path, cancellationToken, headers).ConfigureAwait(false);

        var html = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        var fields = HiddenFields(html);

        if (fields.ContainsKey(TokenField))
        {
            return fields;
        }

        throw new InvalidOperationException($"GET {path} answered {(int)response.StatusCode} and its page has no form with an antiforgery token");
    }

    /// <summary>
    /// Posts a form of simple fields.
    /// </summary>
    /// <param name="path">The path</param>
    /// <param name="fields">The fields</param>
    /// <param name="cancellationToken">Cancels the request</param>
    /// <param name="headers">Further request headers, <c>null</c> for none</param>
    /// <returns>A task that returns the response; the caller disposes it</returns>
    internal async Task<HttpResponseMessage> PostFormAsync(string path, IEnumerable<KeyValuePair<string, string>> fields, CancellationToken cancellationToken, IReadOnlyDictionary<string, string>? headers = null)
    {
        using var content = new FormUrlEncodedContent(fields);

        return await SendAsync(HttpMethod.Post, path, content, headers, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Posts a <c>multipart/form-data</c> form with simple fields and one file part.
    /// </summary>
    /// <param name="path">The path</param>
    /// <param name="fields">The fields</param>
    /// <param name="fileLength">The length of the file part in bytes</param>
    /// <param name="cancellationToken">Cancels the request</param>
    /// <returns>A task that returns the response; the caller disposes it</returns>
    internal async Task<HttpResponseMessage> PostMultipartAsync(string path, IEnumerable<KeyValuePair<string, string>> fields, int fileLength, CancellationToken cancellationToken)
    {
        using var content = new MultipartFormDataContent();

        foreach (var (name, value) in fields)
        {
            content.Add(new StringContent(value), name);
        }

        content.Add(new ByteArrayContent(new byte[fileLength]), "upload", "upload.bin");

        return await SendAsync(HttpMethod.Post, path, content, null, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Opens the login page and posts the login form with a password.
    /// </summary>
    /// <param name="password">The password</param>
    /// <param name="cancellationToken">Cancels the requests</param>
    /// <param name="headers">Further request headers for both requests, <c>null</c> for none</param>
    /// <returns>A task that returns the response of the post; the caller disposes it</returns>
    internal async Task<HttpResponseMessage> PostLoginAsync(string password, CancellationToken cancellationToken, IReadOnlyDictionary<string, string>? headers = null)
    {
        var fields = await ReadFormFieldsAsync("/login", cancellationToken, headers).ConfigureAwait(false);

        fields[PasswordField] = password;

        return await PostFormAsync("/login", fields, cancellationToken, headers).ConfigureAwait(false);
    }

    /// <summary>
    /// Opens the home page of the signed-in user and posts the sign-out form with the token of that page.
    /// </summary>
    /// <param name="cancellationToken">Cancels the requests</param>
    /// <returns>A task that returns the response of the post; the caller disposes it</returns>
    internal async Task<HttpResponseMessage> PostLogoutAsync(CancellationToken cancellationToken)
    {
        var fields = await ReadFormFieldsAsync("/", cancellationToken).ConfigureAwait(false);

        return await PostFormAsync("/logout", fields, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Takes one <c>Set-Cookie</c> header into the jar; a cookie that is empty, expired or has a non-positive age is removed.
    /// </summary>
    /// <param name="setCookie">The header value</param>
    private void Absorb(string setCookie)
    {
        var parts = setCookie.Split(';');
        var pair = parts[0].Split('=', 2);

        if (pair.Length < 2)
        {
            return;
        }

        var name = pair[0].Trim();
        var value = pair[1].Trim();
        var ended = value.Length == 0;

        foreach (var attribute in parts.Skip(1).Select(part => part.Trim()))
        {
            if (attribute.StartsWith("expires=", StringComparison.OrdinalIgnoreCase) && DateTimeOffset.TryParse(attribute["expires=".Length..], CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var expires) && expires <= DateTimeOffset.UtcNow)
            {
                ended = true;
            }

            if (attribute.StartsWith("max-age=", StringComparison.OrdinalIgnoreCase) && int.TryParse(attribute["max-age=".Length..], CultureInfo.InvariantCulture, out var age) && age <= 0)
            {
                ended = true;
            }
        }

        if (ended)
        {
            _cookies.Remove(name);
        }
        else
        {
            _cookies[name] = value;
        }
    }

    #endregion // Methods

    #region Patterns

    [GeneratedRegex("<input\\b[^>]*>", RegexOptions.IgnoreCase)]
    private static partial Regex InputElement();

    [GeneratedRegex("([A-Za-z][A-Za-z0-9_-]*)=\"([^\"]*)\"")]
    private static partial Regex Attribute();

    #endregion // Patterns

    #region IDisposable

    /// <inheritdoc />
    public void Dispose()
    {
        _client.Dispose();
    }

    #endregion // IDisposable
}