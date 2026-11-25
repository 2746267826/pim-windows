using System.Diagnostics;
using System.Windows.Forms;
using Pim.Client.Core;
using Pim.Client.Core.Services;

namespace Pim.Client.App;

/// <summary>
/// Explicit system-browser links for web-only compatibility pages.
/// These links never carry credentials and are never hosted inside the daemon UI.
/// </summary>
internal static class WebCompatibilityLinks
{
    internal static string BuildUrl(string? serverUrl, string route)
    {
        var requested = string.IsNullOrWhiteSpace(serverUrl)
            ? ClientDefaults.DefaultServerUrl
            : serverUrl;
        var root = ServerUrlPolicy.TryNormalize(requested, out var normalized, out _)
            ? normalized
            : ServerUrlPolicy.LocalDevelopmentUrl;
        root = root.TrimEnd('/');
        if (root.EndsWith("/api/v1", StringComparison.OrdinalIgnoreCase))
        {
            root = root[..^"/api/v1".Length].TrimEnd('/');
        }

        if (!Uri.TryCreate(root, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            root = ClientDefaults.DefaultServerUrl;
        }

        var normalizedRoute = string.IsNullOrWhiteSpace(route)
            ? "/today"
            : "/" + route.TrimStart('/');
        return root + normalizedRoute;
    }

    internal static void Open(string? serverUrl, string route)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = BuildUrl(serverUrl, route),
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                $"打开 Web 兼容页面失败：{ex.Message}",
                "PIM",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        }
    }
}
