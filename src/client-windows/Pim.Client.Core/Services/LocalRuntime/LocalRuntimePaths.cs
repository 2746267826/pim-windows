namespace Pim.Client.Core.Services.LocalRuntime;

/// <summary>Default on-disk layout for the local runtime feature.</summary>
public static class LocalRuntimePaths
{
    public static string DefaultRoot { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "PIM",
        "local-runtime");

    public static string CatalogFile(string? root = null) =>
        Path.Combine(root ?? DefaultRoot, "catalog.json");

    public static string OwnedProcessesFile(string? root = null) =>
        Path.Combine(root ?? DefaultRoot, "owned-processes.json");
}

/// <summary>Default disk-space probe backed by DriveInfo.</summary>
public sealed class DriveInfoDiskSpaceProbe : IDiskSpaceProbe
{
    public long GetAvailableFreeBytes(string directoryPath)
    {
        var root = Path.GetPathRoot(Path.GetFullPath(directoryPath));
        if (string.IsNullOrEmpty(root))
        {
            return 0;
        }

        var drive = new DriveInfo(root);
        return drive.IsReady ? drive.AvailableFreeSpace : 0;
    }
}
