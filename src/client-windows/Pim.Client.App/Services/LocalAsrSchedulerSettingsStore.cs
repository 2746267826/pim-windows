using System.IO;
using System.Text.Json;

namespace Pim.Client.App.Services;

internal sealed record LocalAsrSchedulerSettings(
    bool AutomaticProcessingEnabled,
    int PollIntervalSeconds,
    int MaxConsecutiveFailures,
    DateTimeOffset UpdatedAtUtc)
{
    public const int MinimumPollIntervalSeconds = 5;
    public const int MaximumPollIntervalSeconds = 3600;
    public const int MinimumFailures = 1;
    public const int MaximumFailures = 20;

    public static LocalAsrSchedulerSettings Defaults { get; } =
        new(
            AutomaticProcessingEnabled: false,
            PollIntervalSeconds: 30,
            MaxConsecutiveFailures: 3,
            UpdatedAtUtc: DateTimeOffset.MinValue);

    public bool IsValid =>
        PollIntervalSeconds is >= MinimumPollIntervalSeconds and <= MaximumPollIntervalSeconds
        && MaxConsecutiveFailures is >= MinimumFailures and <= MaximumFailures;
}

internal sealed record LocalAsrSchedulerSettingsLoadResult(
    LocalAsrSchedulerSettings Settings,
    bool ResetToSafeDefaults,
    string? Detail);

internal interface ILocalAsrSchedulerSettingsStore
{
    Task<LocalAsrSchedulerSettingsLoadResult> LoadAsync(
        CancellationToken cancellationToken = default);

    Task SaveAsync(
        LocalAsrSchedulerSettings settings,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Small account-independent preference store for the WPF scheduler. Invalid or
/// corrupted input always disables automatic processing; it never enables a run.
/// </summary>
internal sealed class LocalAsrSchedulerSettingsStore : ILocalAsrSchedulerSettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    private readonly string _path;
    private readonly Func<DateTimeOffset> _now;

    public LocalAsrSchedulerSettingsStore(
        string path,
        Func<DateTimeOffset>? now = null)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new ArgumentException("settings path is required", nameof(path));
        }

        _path = Path.GetFullPath(path);
        _now = now ?? (() => DateTimeOffset.UtcNow);
    }

    public async Task<LocalAsrSchedulerSettingsLoadResult> LoadAsync(
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(_path))
        {
            return new LocalAsrSchedulerSettingsLoadResult(
                LocalAsrSchedulerSettings.Defaults with { UpdatedAtUtc = _now() },
                ResetToSafeDefaults: false,
                Detail: null);
        }

        try
        {
            await using var stream = new FileStream(
                _path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 4096,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            var settings = await JsonSerializer.DeserializeAsync<LocalAsrSchedulerSettings>(
                stream,
                JsonOptions,
                cancellationToken);
            if (settings is null || !settings.IsValid)
            {
                return Reset("settings-invalid");
            }

            return new LocalAsrSchedulerSettingsLoadResult(
                settings,
                ResetToSafeDefaults: false,
                Detail: null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (
            ex is IOException
                or UnauthorizedAccessException
                or JsonException
                or NotSupportedException)
        {
            return Reset(ex.GetType().Name);
        }
    }

    public async Task SaveAsync(
        LocalAsrSchedulerSettings settings,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (!settings.IsValid)
        {
            throw new ArgumentException("scheduler settings are outside safe bounds", nameof(settings));
        }

        var normalized = settings with { UpdatedAtUtc = _now() };
        var directory = Path.GetDirectoryName(_path)
            ?? throw new InvalidOperationException("settings path has no directory");
        Directory.CreateDirectory(directory);

        var temporary = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var stream = new FileStream(
                temporary,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 4096,
                FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(
                    stream,
                    normalized,
                    JsonOptions,
                    cancellationToken);
                await stream.FlushAsync(cancellationToken);
                stream.Flush(flushToDisk: true);
            }

            File.Move(temporary, _path, overwrite: true);
        }
        finally
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

    private LocalAsrSchedulerSettingsLoadResult Reset(string detail) =>
        new(
            LocalAsrSchedulerSettings.Defaults with
            {
                AutomaticProcessingEnabled = false,
                UpdatedAtUtc = _now(),
            },
            ResetToSafeDefaults: true,
            Detail: detail);
}
