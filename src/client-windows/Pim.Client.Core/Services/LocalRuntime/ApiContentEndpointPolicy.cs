namespace Pim.Client.Core.Services.LocalRuntime;

/// <summary>
/// Resolves server-provided ASR content endpoints without allowing an API token to
/// cross origins. Relative paths and same-origin redirects are accepted; absolute
/// external URLs, protocol-relative URLs, downgrades and fragments are rejected.
/// </summary>
public sealed class ApiContentEndpointPolicy
{
    private const int MaximumDecodePasses = 4;
    private readonly Uri _origin;
    private readonly string _originText;

    public ApiContentEndpointPolicy(string apiOrigin)
    {
        if (!Uri.TryCreate(apiOrigin, UriKind.Absolute, out var origin)
            || (origin.Scheme != Uri.UriSchemeHttps && origin.Scheme != Uri.UriSchemeHttp)
            || !string.IsNullOrEmpty(origin.UserInfo)
            || !string.IsNullOrEmpty(origin.Query)
            || !string.IsNullOrEmpty(origin.Fragment)
            || origin.AbsolutePath.Length > 1)
        {
            throw new ArgumentException(
                "API origin must be an absolute http(s) origin without path, query or userinfo",
                nameof(apiOrigin));
        }

        _originText = origin.GetLeftPart(UriPartial.Authority).TrimEnd('/');
        _origin = new Uri(_originText + "/", UriKind.Absolute);
    }

    public bool TryResolveContentEndpoint(
        string? endpoint,
        out Uri resolved,
        out string error)
    {
        resolved = _origin;
        if (string.IsNullOrWhiteSpace(endpoint)
            || !endpoint.StartsWith("/", StringComparison.Ordinal)
            || endpoint.StartsWith("//", StringComparison.Ordinal)
            || !IsSafePath(endpoint))
        {
            error = "content-endpoint-invalid";
            return false;
        }

        if (!Uri.TryCreate(_origin, endpoint, out var candidate)
            || !IsSameOrigin(candidate))
        {
            error = "content-endpoint-origin-mismatch";
            return false;
        }

        resolved = candidate;
        error = string.Empty;
        return true;
    }

    public bool TryResolveRedirect(
        Uri current,
        Uri? location,
        out Uri next,
        out string error)
    {
        next = _origin;
        if (!IsSameOrigin(current)
            || location is null)
        {
            error = "content-redirect-invalid";
            return false;
        }

        Uri candidate;
        try
        {
            if (!IsSafePath(location.OriginalString))
            {
                error = "content-redirect-invalid";
                return false;
            }

            candidate = location.IsAbsoluteUri
                ? location
                : new Uri(current, location);
        }
        catch (UriFormatException)
        {
            error = "content-redirect-invalid";
            return false;
        }

        if (!IsSameOrigin(candidate)
            || !string.IsNullOrEmpty(candidate.Query)
            || !string.IsNullOrEmpty(candidate.UserInfo)
            || !string.IsNullOrEmpty(candidate.Fragment)
            || !IsSafePath(candidate.AbsolutePath))
        {
            error = "content-redirect-origin-mismatch";
            return false;
        }

        next = candidate;
        error = string.Empty;
        return true;
    }

    public bool IsSameOrigin(Uri uri) =>
        uri.IsAbsoluteUri
        && uri.Scheme is "http" or "https"
        && string.Equals(
            uri.GetLeftPart(UriPartial.Authority).TrimEnd('/'),
            _originText,
            StringComparison.Ordinal);

    private static bool IsSafePath(string path)
    {
        var current = path;
        for (var pass = 0; pass < MaximumDecodePasses; pass++)
        {
            if (ContainsUnsafeCharactersOrDotSegment(current))
            {
                return false;
            }

            string decoded;
            try
            {
                decoded = Uri.UnescapeDataString(current);
            }
            catch (ArgumentException)
            {
                return false;
            }

            if (string.Equals(decoded, current, StringComparison.Ordinal))
            {
                return true;
            }

            current = decoded;
        }

        // More encoding layers than the policy accepts are treated as adversarial.
        return false;
    }

    private static bool ContainsUnsafeCharactersOrDotSegment(string path) =>
        path.IndexOf('\\') >= 0
        || path.IndexOf('\0') >= 0
        || path.IndexOf('#') >= 0
        || path.IndexOf('?') >= 0
        || path.Any(char.IsControl)
        || path.Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Any(segment => segment is "." or "..");
}
