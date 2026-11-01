using System.Diagnostics;
using System.Text.Json;
using Pim.Client.Core.Models.LocalRuntime;

namespace Pim.Client.Core.Services.LocalRuntime;

public sealed record OwnedProcessCleanupResult(
    int ProcessId,
    bool Killed,
    bool IdentityVerified,
    string? Error);

/// <summary>
/// Durable record of processes this tool spawned. Cleanup only touches an entry
/// whose pid, start time, session and executable path still match the launch record.
/// It never enumerates or kills processes by name.
/// </summary>
public sealed class OwnedProcessRegistry
{
    private readonly string _path;
    private readonly object _sync = new();

    public OwnedProcessRegistry(string path)
    {
        _path = Path.GetFullPath(path);
    }

    public OwnedProcessIdentity Register(Process process, string executablePath)
    {
        int sessionId;
        try
        {
            sessionId = process.SessionId;
        }
        catch (Exception ex) when (ex is PlatformNotSupportedException or InvalidOperationException)
        {
            sessionId = 0;
        }

        var identity = new OwnedProcessIdentity(
            process.Id,
            process.StartTime.ToUniversalTime().Ticks,
            sessionId,
            Path.GetFullPath(executablePath));
        lock (_sync)
        {
            var entries = LoadUnsafe();
            entries.RemoveAll(entry => entry.ProcessId == identity.ProcessId);
            entries.Add(identity);
            SaveUnsafe(entries);
        }

        return identity;
    }

    public void Unregister(int processId)
    {
        lock (_sync)
        {
            var entries = LoadUnsafe();
            if (entries.RemoveAll(entry => entry.ProcessId == processId) > 0)
            {
                SaveUnsafe(entries);
            }
        }
    }

    public IReadOnlyList<OwnedProcessIdentity> Snapshot()
    {
        lock (_sync)
        {
            return LoadUnsafe().ToArray();
        }
    }

    /// <summary>
    /// Removes dead or identity-mismatched entries and returns still-live processes
    /// that are proven to be owned. Startup cleanup kills only this returned set.
    /// </summary>
    public IReadOnlyList<OwnedProcessIdentity> Reconcile()
    {
        var live = new List<OwnedProcessIdentity>();
        lock (_sync)
        {
            var entries = LoadUnsafe();
            var keep = new List<OwnedProcessIdentity>();
            foreach (var entry in entries)
            {
                if (OwnedProcessGuard.TryVerify(entry, out _))
                {
                    live.Add(entry);
                    keep.Add(entry);
                }
            }

            if (keep.Count != entries.Count)
            {
                SaveUnsafe(keep);
            }
        }

        return live;
    }

    private List<OwnedProcessIdentity> LoadUnsafe()
    {
        try
        {
            if (!File.Exists(_path))
            {
                return new List<OwnedProcessIdentity>();
            }

            var json = File.ReadAllText(_path);
            return JsonSerializer.Deserialize<List<OwnedProcessIdentity>>(json)
                   ?? new List<OwnedProcessIdentity>();
        }
        catch (JsonException)
        {
            return new List<OwnedProcessIdentity>();
        }
        catch (IOException)
        {
            return new List<OwnedProcessIdentity>();
        }
    }

    private void SaveUnsafe(List<OwnedProcessIdentity> entries)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var temp = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(entries));
        File.Move(temp, _path, overwrite: true);
    }
}

public static class OwnedProcessGuard
{
    public static bool TryVerify(OwnedProcessIdentity owned, out Process? process)
    {
        process = null;
        try
        {
            var candidate = Process.GetProcessById(owned.ProcessId);
            var matches = OwnedProcessIdentity.Matches(
                owned,
                candidate.Id,
                candidate.StartTime.ToUniversalTime().Ticks,
                candidate.SessionId,
                candidate.MainModule?.FileName ?? string.Empty);
            if (!matches)
            {
                candidate.Dispose();
                return false;
            }

            process = candidate;
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException
                                       or InvalidOperationException
                                       or System.ComponentModel.Win32Exception
                                       or NotSupportedException)
        {
            return false;
        }
    }

    public static OwnedProcessCleanupResult TryKillOwned(OwnedProcessIdentity owned)
    {
        if (!TryVerify(owned, out var process))
        {
            return new OwnedProcessCleanupResult(owned.ProcessId, false, false, "identity-mismatch-or-exited");
        }

        using (process)
        {
            try
            {
                process!.Kill(entireProcessTree: true);
                if (!process.WaitForExit(5000))
                {
                    return new OwnedProcessCleanupResult(owned.ProcessId, false, true, "timeout");
                }

                return new OwnedProcessCleanupResult(owned.ProcessId, true, true, null);
            }
            catch (Exception ex) when (ex is InvalidOperationException
                                           or System.ComponentModel.Win32Exception
                                           or NotSupportedException)
            {
                return new OwnedProcessCleanupResult(owned.ProcessId, false, true, ex.GetType().Name);
            }
        }
    }
}
