using System.Net;

namespace Pim.Client.Core.Services.LocalRuntime;

/// <summary>
/// URL policy for model artifacts. Every hop (initial URL and each redirect) must
/// stay on HTTPS and must not target loopback, link-local, private or special-use
/// addresses. This prevents an initial public HTTPS URL from being redirected to
/// HTTP or an internal service.
/// </summary>
public static class ArtifactUrlPolicy
{
    public const int MaxRedirects = 5;

    public static bool TryValidate(Uri uri, out string error)
    {
        if (!uri.IsAbsoluteUri)
        {
            error = "url-not-absolute";
            return false;
        }

        if (!string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            error = "scheme-not-https";
            return false;
        }

        if (!string.IsNullOrEmpty(uri.UserInfo))
        {
            error = "userinfo-not-allowed";
            return false;
        }

        var host = uri.IdnHost.TrimEnd('.').ToLowerInvariant();
        if (host.Length == 0)
        {
            error = "host-missing";
            return false;
        }

        if (host == "localhost"
            || host.EndsWith(".localhost", StringComparison.Ordinal)
            || host.EndsWith(".local", StringComparison.Ordinal)
            || host is "metadata" or "metadata.google.internal")
        {
            error = "local-or-private-host";
            return false;
        }

        if (IPAddress.TryParse(host, out var address)
            && IsPrivateOrLocalAddress(address))
        {
            error = "private-address";
            return false;
        }

        error = string.Empty;
        return true;
    }

    public static bool TryResolveRedirect(
        Uri current,
        Uri? location,
        out Uri next,
        out string error)
    {
        if (location is null)
        {
            next = current;
            error = "redirect-without-location";
            return false;
        }

        if (!location.IsAbsoluteUri)
        {
            location = new Uri(current, location);
        }

        if (!TryValidate(location, out error))
        {
            next = location;
            return false;
        }

        next = location;
        return true;
    }

    public static bool IsPrivateOrLocalAddress(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        if (address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
        {
            var bytes = address.GetAddressBytes();
            return bytes[0] == 0
                   || bytes[0] == 10
                   || bytes[0] == 127
                   || (bytes[0] == 100 && bytes[1] is >= 64 and <= 127)
                   || (bytes[0] == 169 && bytes[1] == 254)
                   || (bytes[0] == 172 && bytes[1] is >= 16 and <= 31)
                   || (bytes[0] == 192 && bytes[1] == 168)
                   || (bytes[0] == 198 && bytes[1] is 18 or 19)
                   || bytes[0] >= 224;
        }

        if (address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6)
        {
            if (address.Equals(IPAddress.IPv6Loopback) || address.Equals(IPAddress.IPv6Any))
            {
                return true;
            }

            if (address.IsIPv6LinkLocal || address.IsIPv6SiteLocal || address.IsIPv6Multicast)
            {
                return true;
            }

            var bytes = address.GetAddressBytes();
            return (bytes[0] & 0xFE) == 0xFC; // fc00::/7 unique local
        }

        return true;
    }
}
