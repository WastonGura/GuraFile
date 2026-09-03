namespace GuraFile.Storage;

public static class GraphSecurityPolicy
{
    public const string VirtualHostName = "graph.gurafile.local";
    public const string VirtualHostOrigin = "https://graph.gurafile.local";
    public const string EntryUrl = "https://graph.gurafile.local/index.html";

    public const string ExpectedCsp =
        "default-src 'none'; script-src 'self' 'unsafe-inline'; style-src 'self' 'unsafe-inline'; img-src 'self' data:; connect-src 'none'; frame-src 'none'; object-src 'none';";

    public static bool IsAllowedUri(string? uriString)
    {
        if (string.IsNullOrWhiteSpace(uriString))
        {
            return false;
        }

        if (!Uri.TryCreate(uriString, UriKind.Absolute, out var uri))
        {
            return false;
        }

        return IsAllowedUri(uri);
    }

    public static bool IsAllowedUri(Uri uri)
    {
        ArgumentNullException.ThrowIfNull(uri);

        if (!string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return string.Equals(uri.Host, VirtualHostName, StringComparison.OrdinalIgnoreCase);
    }
}
