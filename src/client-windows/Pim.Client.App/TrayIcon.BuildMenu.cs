using System.Reflection;

namespace Pim.Client.App;

public record TrayMenuItem(string Text);

public class TrayMenu
{
    public List<TrayMenuItem> Items { get; } = new();
}

public partial class TrayIcon
{
    public static TrayMenu BuildMenu(string? version = null, string? serverUrl = null)
    {
        var v = string.IsNullOrWhiteSpace(version) ? GetVersion() : version!.Trim();
        var menu = new TrayMenu();
        menu.Items.Add(new TrayMenuItem($"关于 PIM v{v}"));
        menu.Items.Add(new TrayMenuItem("检查更新"));
        return menu;
    }

    private static string GetVersion()
    {
        try
        {
            var asm = typeof(TrayIcon).Assembly;
            // 显示版本（仓根 VERSION，由 CI 当 -p:DisplayVersion 注入）在前，
            // 机器号在括号里便于对账/排障；更新检查仍然只认机器号。
            var display = asm.GetCustomAttributes<AssemblyMetadataAttribute>()
                .FirstOrDefault(a => a.Key == "DisplayVersion")?.Value;
            var machineAttr = asm.GetCustomAttributes(false).OfType<AssemblyInformationalVersionAttribute>().FirstOrDefault();
            var machine = machineAttr?.InformationalVersion;
            if (!string.IsNullOrWhiteSpace(display))
            {
                return string.IsNullOrWhiteSpace(machine) ? $"v{display}" : $"v{display} ({machine})";
            }
            if (!string.IsNullOrWhiteSpace(machine))
                return machine!;
            var appType = Type.GetType("Pim.Client.App.App, Pim.Client.App");
            if (appType != null)
            {
                var appAttr = appType.Assembly.GetCustomAttributes(false).OfType<AssemblyInformationalVersionAttribute>().FirstOrDefault();
                if (appAttr != null && !string.IsNullOrWhiteSpace(appAttr.InformationalVersion))
                    return appAttr.InformationalVersion;
            }
            System.Diagnostics.Trace.WriteLine("[Warn] TrayIcon.GetVersion: fallback to 0.0.0-local, App type not found or version missing");
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.WriteLine($"[Warn] TrayIcon.GetVersion failed: {ex.Message}");
        }
        return "0.0.0-local";
    }

}
