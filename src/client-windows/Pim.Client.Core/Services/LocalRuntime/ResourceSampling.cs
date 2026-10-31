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
            CpuLoadPercent: null,
            AvailableMemoryBytes: availableMemory,
            VramAvailableBytes: null,
            IsOnAcPower: acPower,
            BatteryPercent: batteryPercent));
    }

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

    [DllImport("kernel32.dll", SetLastError = false)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx buffer);

    [DllImport("kernel32.dll", SetLastError = false)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSystemPowerStatus(out SystemPowerStatus systemPowerStatus);
}
