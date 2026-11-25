using Pim.Client.Core.Services;

namespace Pim.Client.App;

public record TrayMenuItem(string Text);

public class TrayMenu
{
    public List<TrayMenuItem> Items { get; } = new();
}

public partial class TrayIcon
{
    public static TrayMenu BuildMenu(string? version = null, string? serverUrl = null, string? displayVersion = null)
    {
        var machine = string.IsNullOrWhiteSpace(version) ? GetVersion() : version!.Trim();
        var display = string.IsNullOrWhiteSpace(displayVersion) ? GetDisplayVersion() : displayVersion!.Trim();
        var label = ClientVersion.FormatDisplayLabel(display, machine);
        var menu = new TrayMenu();
        menu.Items.Add(new TrayMenuItem($"关于 PIM {label}"));
        menu.Items.Add(new TrayMenuItem("检查更新"));
        return menu;
    }

    /// <summary>
    /// 机器版本（CalVer）。只返回 <see cref="System.Reflection.AssemblyInformationalVersionAttribute"/> 的原始值，
    /// 不附加显示版本、v 前缀或括号：它会被 <see cref="UpdateChecker"/> 解析最后一段数字。
    /// </summary>
    internal static string GetVersion()
    {
        try
        {
            var asm = typeof(TrayIcon).Assembly;
            var machine = ClientVersion.ReadMachineVersion(asm);
            if (!string.IsNullOrWhiteSpace(machine))
                return machine!;

            var appType = Type.GetType("Pim.Client.App.App, Pim.Client.App");
            if (appType != null)
            {
                machine = ClientVersion.ReadMachineVersion(appType.Assembly);
                if (!string.IsNullOrWhiteSpace(machine))
                    return machine!;
            }
            System.Diagnostics.Trace.WriteLine("[Warn] TrayIcon.GetVersion: fallback to 0.0.0-local, App type not found or version missing");
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.WriteLine($"[Warn] TrayIcon.GetVersion failed: {ex.Message}");
        }
        return "0.0.0-local";
    }

    /// <summary>
    /// 显示版本（仓根 VERSION，由 CI 当 -p:DisplayVersion 注入），仅供界面展示。
    /// </summary>
    internal static string GetDisplayVersion()
    {
        try
        {
            var asm = typeof(TrayIcon).Assembly;
            var display = ClientVersion.ReadDisplayVersion(asm);
            if (!string.IsNullOrWhiteSpace(display))
                return display!;

            var appType = Type.GetType("Pim.Client.App.App, Pim.Client.App");
            if (appType != null)
            {
                display = ClientVersion.ReadDisplayVersion(appType.Assembly);
                if (!string.IsNullOrWhiteSpace(display))
                    return display!;
            }
            System.Diagnostics.Trace.WriteLine("[Warn] TrayIcon.GetDisplayVersion: fallback to 0.0.0-local, App type not found or DisplayVersion missing");
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.WriteLine($"[Warn] TrayIcon.GetDisplayVersion failed: {ex.Message}");
        }
        return "0.0.0-local";
    }

}
