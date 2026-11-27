using System.Text.Json;

namespace Pim.Client.Core.Services.Notes;

/// <summary>
/// Account-independent device preference for the next handwriting brush. The
/// per-stroke style remains in the immutable ink source; this store only remembers
/// the last small-popover choice on this device.
/// </summary>
public sealed class LocalInkPreferenceStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    public LocalInkPreferenceStore(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new ArgumentException("preferences path is required", nameof(path));
        }

        Path = System.IO.Path.GetFullPath(path);
    }

    public string Path { get; }

    public static string DefaultPath => System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "PIM",
        "Notes",
        "ink-preferences.json");

    public LocalInkStyle Load()
    {
        try
        {
            if (!File.Exists(Path))
            {
                return LocalInkStyle.Default;
            }

            using var stream = new FileStream(
                Path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 4096,
                FileOptions.SequentialScan);
            var saved = JsonSerializer.Deserialize<Preference>(stream, JsonOptions);
            if (saved is null
                || !LocalInkPalette.IsBrushColor(saved.Color)
                || !LocalInkPalette.IsBrushWidth(saved.Width))
            {
                return LocalInkStyle.Default;
            }

            return new LocalInkStyle(saved.Color, saved.Width);
        }
        catch (Exception ex) when (
            ex is IOException
                or UnauthorizedAccessException
                or JsonException
                or NotSupportedException)
        {
            return LocalInkStyle.Default;
        }
    }

    public void Save(LocalInkStyle style)
    {
        ArgumentNullException.ThrowIfNull(style);
        if (!LocalInkPalette.IsBrushColor(style.Color)
            || !LocalInkPalette.IsBrushWidth(style.Width))
        {
            throw new ArgumentException("手写画笔偏好不在支持的颜色或笔宽档位内。", nameof(style));
        }

        var temporary = Path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)
                ?? throw new InvalidOperationException("preferences path has no directory"));
            File.WriteAllText(
                temporary,
                JsonSerializer.Serialize(new Preference(style.Color, style.Width), JsonOptions));
            File.Move(temporary, Path, overwrite: true);
        }
        catch (Exception ex) when (
            ex is IOException
                or UnauthorizedAccessException
                or NotSupportedException)
        {
            try
            {
                if (File.Exists(temporary))
                {
                    File.Delete(temporary);
                }
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    private sealed record Preference(string Color, double Width);
}
