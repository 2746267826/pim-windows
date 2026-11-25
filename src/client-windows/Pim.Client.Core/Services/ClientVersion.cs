using System.Reflection;
using System.Text.RegularExpressions;

namespace Pim.Client.Core.Services;

/// <summary>
/// 双轨版本号的读取与格式化。
///
/// 机器版本（CalVer）来自 <see cref="AssemblyInformationalVersionAttribute"/>，更新比较、
/// 心跳上报和 version.json 只认它；显示版本来自编译期注入的 AssemblyMetadata("DisplayVersion")，
/// 只用于界面。两者绝不能拼成同一个字符串再交给 <see cref="UpdateChecker"/>。
/// </summary>
public static class ClientVersion
{
    public const string DisplayVersionMetadataKey = "DisplayVersion";
    private static readonly Regex DisplayVersionPattern = new(
        @"^[0-9]+\.[0-9]+\.[0-9]+(?:[-+][A-Za-z0-9._-]+)?$",
        RegexOptions.CultureInvariant);

    public static string? ReadMachineVersion(Assembly assembly)
    {
        ArgumentNullException.ThrowIfNull(assembly);
        var value = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    public static string? ReadDisplayVersion(Assembly assembly)
    {
        ArgumentNullException.ThrowIfNull(assembly);
        var value = assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(attribute => attribute.Key == DisplayVersionMetadataKey)?
            .Value?
            .Trim();
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        if (value.StartsWith('v') || value.StartsWith('V'))
        {
            value = value[1..].TrimStart();
        }

        return DisplayVersionPattern.IsMatch(value) ? value : null;
    }

    public static string FormatDisplayLabel(string? displayVersion, string? machineVersion)
    {
        var display = NormalizeDisplay(displayVersion);
        var machine = string.IsNullOrWhiteSpace(machineVersion) ? null : machineVersion.Trim();

        if (display is null)
        {
            return machine ?? "0.0.0-local";
        }

        return machine is null ? $"v{display}" : $"v{display} ({machine})";
    }

    private static string? NormalizeDisplay(string? value)
    {
        var display = value?.Trim();
        if (string.IsNullOrWhiteSpace(display))
        {
            return null;
        }

        if (display.StartsWith('v') || display.StartsWith('V'))
        {
            display = display[1..].TrimStart();
        }

        return DisplayVersionPattern.IsMatch(display) ? display : null;
    }
}
