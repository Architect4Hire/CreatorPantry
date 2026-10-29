namespace CreatorPantry.Domain.Modules.Billing.Managers;

/// <summary>
/// The web origins a provider page may send a creator back to (AP.6.5), and the URLs built on them.
/// </summary>
/// <remarks>
/// A return URL is where a payment provider sends a browser after it has taken money, so it is never what a request
/// says it is: the browser names its origin, and that is used only when <c>Billing:ReturnOrigins</c> lists it — the
/// same origins the gateway's CORS policy allows. Anything else falls to nothing, which the caller refuses.
/// </remarks>
public static class BillingAccountOrigins
{
    /// <summary>Whether <paramref name="value"/> is an http(s) origin and nothing more: no path, query or fragment.</summary>
    public static bool IsOrigin(string? value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri)
        && uri.Scheme is "https" or "http"
        && uri.AbsolutePath == "/"
        && string.IsNullOrEmpty(uri.Query)
        && string.IsNullOrEmpty(uri.Fragment)
        && string.IsNullOrEmpty(uri.UserInfo);

    /// <summary>
    /// The configured origin <paramref name="asked"/> names, or the first configured when it names none; null when
    /// it names one not configured, or nothing is.
    /// </summary>
    public static Uri? Resolve(IEnumerable<string> configured, string? asked)
    {
        var allowed = configured.Where(IsOrigin).Select(origin => new Uri(origin)).ToList();

        if (string.IsNullOrWhiteSpace(asked))
        {
            return allowed.FirstOrDefault();
        }

        return IsOrigin(asked) && new Uri(asked) is var wanted
            ? allowed.FirstOrDefault(origin => Uri.Compare(
                origin, wanted, UriComponents.SchemeAndServer, UriFormat.Unescaped, StringComparison.OrdinalIgnoreCase) == 0)
            : null;
    }

    /// <summary>The billing page on <paramref name="origin"/>, with <paramref name="query"/> when there is one.</summary>
    public static Uri Page(Uri origin, string? query = null) =>
        new(origin, BillingAccountPolicy.PagePath + (query is null ? string.Empty : "?" + query));
}
