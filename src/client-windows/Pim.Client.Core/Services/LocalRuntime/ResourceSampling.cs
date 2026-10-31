using System.Runtime.InteropServices;
using Pim.Client.Core.Models.LocalRuntime;

namespace Pim.Client.Core.Services.LocalRuntime;

/// <summary>Produces a resource snapshot. Unmeasurable fields stay null and the
/// evaluator fails closed for requirements that depend on them.</summary>
public interface IResourceSnapshotProvider
{
    Task<ResourceSnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default);
}

/// <summary>Deterministic snapshot source for tests and controlled demos.</summary>
public sealed class MockResourceSnapshotProvider : IResourceSnapshotProvider
{
    private readonly Func<ResourceSnapshot> _factory;

    public MockResourceSnapshotProvider(Func<ResourceSnapshot> factory)
    {
        _factory = factory;
    }

    public Task<ResourceSnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(_factory());
}

/// <summary>
/// Windows sampling via kernel32 only. CPU load and available VRAM are not measured
/// here yet, so they stay null: GPU-required models fail closed with
/// resource-vram-unknown until real VRAM sampling is wired.
/// </summary>
public sealed class WindowsResourceSnapshotProvider : IResourceSnapshotProvider
{
    private readonly CpuLoadSampler _cpuLoadSampler = new(ReadSystemTimes);

    public Task<ResourceSnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!OperatingSystem.IsWindows())
        {
            return Task.FromResult(ResourceSnapshot.Unknown(DateTimeOffset.UtcNow));
        }

        var memory = default(MemoryStatusEx);
        memory.Length = (uint)Marshal.SizeOf<MemoryStatusEx>();
        var hasMemory = GlobalMemoryStatusEx(ref memory);

        var power = default(SystemPowerStatus);
        var hasPower = GetSystemPowerStatus(out power);

        long? availableMemory = hasMemory ? unchecked((long)memory.AvailPhys) : null;
        bool? acPower = hasPower && power.AcLineStatus != 255
            ? power.AcLineStatus == 1
            : null;
        int? batteryPercent = hasPower && power.BatteryLifePercent is >= 0 and <= 100
            ? power.BatteryLifePercent
            : null;

        return Task.FromResult(new ResourceSnapshot(
            DateTimeOffset.UtcNow,
            LogicalProcessorCount: Environment.ProcessorCount,
            CpuLoadPercent: _cpuLoadSampler.Sample(),
            AvailableMemoryBytes: availableMemory,
            VramAvailableBytes: null,
            IsOnAcPower: acPower,
            BatteryPercent: batteryPercent));
    }

    private static CpuTimes ReadSystemTimes()
    {
        if (!GetSystemTimes(out var idle, out var kernel, out var user))
        {
            throw new InvalidOperationException("GetSystemTimes failed");
        }

        return new CpuTimes(ToUInt64(idle), ToUInt64(kernel), ToUInt64(user));
    }

    private static ulong ToUInt64(FileTime value) =>
        ((ulong)value.HighDateTime << 32) | value.LowDateTime;

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatusEx
    {
        public uint Length;
        public uint MemoryLoad;
        public ulong TotalPhys;
        public ulong AvailPhys;
        public ulong TotalPageFile;
        public ulong AvailPageFile;
        public ulong TotalVirtual;
        public ulong AvailVirtual;
        public ulong AvailExtendedVirtual;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SystemPowerStatus
    {
        public byte AcLineStatus;
        public byte BatteryFlag;
        public byte BatteryLifePercent;
        public byte Reserved1;
        public uint BatteryLifeTime;
        public uint BatteryFullLifeTime;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileTime
    {
        public uint LowDateTime;
        public uint HighDateTime;
    }

    [DllImport("kernel32.dll", SetLastError = false)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx buffer);

    [DllImport("kernel32.dll", SetLastError = false)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSystemPowerStatus(out SystemPowerStatus systemPowerStatus);

    [DllImport("kernel32.dll", SetLastError = false)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSystemTimes(
        out FileTime idleTime,
        out FileTime kernelTime,
        out FileTime userTime);
}

public readonly record struct CpuTimes(ulong Idle, ulong Kernel, ulong User);

/// <summary>
/// Converts two cumulative GetSystemTimes readings into a busy ratio. The first
/// reading has no delta and therefore stays unknown; the next non-zero delta yields
/// a real percentage. Zero-delta samples stay unknown without discarding the last
/// baseline.
/// </summary>
public sealed class CpuLoadSampler
{
    private readonly Func<CpuTimes> _read;
    private readonly object _sync = new();
    private CpuTimes? _previous;

    public CpuLoadSampler(Func<CpuTimes> read)
    {
        _read = read;
    }

    public double? Sample()
    {
        var current = _read();
        lock (_sync)
        {
            if (_previous is not { } previous)
            {
                _previous = current;
                return null;
            }

            var idleDelta = current.Idle >= previous.Idle ? current.Idle - previous.Idle : 0;
            var kernelDelta = current.Kernel >= previous.Kernel ? current.Kernel - previous.Kernel : 0;
            var userDelta = current.User >= previous.User ? current.User - previous.User : 0;
            var totalDelta = kernelDelta + userDelta;
            if (totalDelta == 0)
            {
                return null;
            }

            _previous = current;
            var boundedIdle = Math.Min(idleDelta, totalDelta);
            var busy = totalDelta - boundedIdle;
            return Math.Clamp(busy * 100.0 / totalDelta, 0, 100);
        }
    }
}
