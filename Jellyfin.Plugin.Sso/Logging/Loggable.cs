namespace Jellyfin.Plugin.Sso.Logging;

/// <summary>
/// Values that arrived from outside, in the only form they may be written to the log.
///
/// A provider id, an OAuth error and a <c>Sec-Fetch-Site</c> header all reach the plugin
/// as text the caller chose, and nothing stops any of them from carrying a newline and a
/// line of the caller's own invention after it. Written straight into the log, that line
/// reads as if the server had written it — a forged entry in the one record an operator
/// consults when they want to know what happened.
///
/// Every method here answers the same way: match the value against the closed set the
/// specification defines, and return a literal of this file's own per arm. No path exists
/// from the argument to the returned string, so there is nothing to forge with.
/// </summary>
internal static class Loggable
{
    /// <summary>
    /// The id of the configured provider the request refers to, or "unknown". Logging the
    /// configured value rather than the query string keeps caller-controlled text — and
    /// the forged log lines it could carry — out of the log entirely.
    /// </summary>
    internal static string KnownProviderId(string providerId)
        => Plugin.Instance?.Configuration.Providers
            .FirstOrDefault(p => string.Equals(p.Id, providerId, StringComparison.Ordinal))
            ?.Id
            ?? "unknown";

    /// <summary>
    /// The name of a <c>Sec-Fetch-Site</c> value, for the log line that explains a refused
    /// logout. Matched against the four names the Fetch specification defines; anything
    /// else is "an unrecognised value", because the decision the log line explains does not
    /// depend on which word it was.
    /// </summary>
    internal static string NamedFetchSite(string? value) => value switch
    {
        "same-origin" => "same-origin",
        "same-site" => "same-site",
        "cross-site" => "cross-site",
        "none" => "none",
        _ => "an unrecognised value",
    };

    /// <summary>
    /// A value from a token, on one line and bounded in length.
    ///
    /// The three above belong to closed sets, so they are answered with literals. A
    /// username and a group name are not: they are whatever the provider puts in the
    /// token, and at a provider that lets people choose their own display name they are
    /// whatever that person typed. Written straight into the log, a name carrying a
    /// newline and a plausible-looking line after it reads as a second entry the server
    /// wrote — "an administrator logged in" is two lines of typing.
    ///
    /// These values have to be in the log: an operator working out why somebody landed in
    /// the wrong libraries needs the name the provider sent, not a literal. So they are
    /// written, with every line break and control character replaced and the length cut to
    /// something a log line can hold. One entry per event, whatever arrives.
    /// </summary>
    internal static string OneLine(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        var safe = string.Concat(value.Select(
            c => char.IsControl(c) || char.IsSeparator(c) && c != ' ' ? '\uFFFD' : c));

        return safe.Length <= MaxLoggedLength ? safe : safe[..MaxLoggedLength] + "…";
    }

    /// <summary>
    /// How much of a provider-supplied value is written. Long enough for any real name or
    /// group, short enough that a token full of them cannot fill a disk one line at a time.
    /// </summary>
    internal const int MaxLoggedLength = 200;

    /// <summary>Every name on one line, for the log line that lists what arrived.</summary>
    internal static string OneLine(IEnumerable<string> values)
        => OneLine(string.Join(", ", values));

    /// <summary>
    /// Maps the IdP's error to the fixed set from RFC 6749 §4.1.2.1, so neither the log
    /// nor the response ever carries a string the caller chose.
    /// </summary>
    internal static string OAuthErrorCode(string? error) => error switch
    {
        "invalid_request" => "invalid_request",
        "unauthorized_client" => "unauthorized_client",
        "access_denied" => "access_denied",
        "unsupported_response_type" => "unsupported_response_type",
        "invalid_scope" => "invalid_scope",
        "server_error" => "server_error",
        "temporarily_unavailable" => "temporarily_unavailable",
        _ => "unrecognised_error",
    };
}
