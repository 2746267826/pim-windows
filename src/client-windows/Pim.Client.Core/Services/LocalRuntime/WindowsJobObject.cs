using System.Runtime.InteropServices;

namespace Pim.Client.Core.Services.LocalRuntime;

/// <summary>
/// Minimal kill-on-close Job Object. Closing the handle (including process crash)
/// terminates every process still assigned to the job.
/// </summary>
internal sealed class WindowsJobObject : IDisposable
{
    private const int JobObjectExtendedLimitInformationClass = 9;
    private const uint JobObjectLimitKillOnJobClose = 0x2000;

    private IntPtr _handle;

    private WindowsJobObject(IntPtr handle)
    {
        _handle = handle;
    }

    public static bool TryCreate(out WindowsJobObject? job, out string? error)
    {
        job = null;
        error = null;
        if (!OperatingSystem.IsWindows())
        {
            error = "not-windows";
            return false;
        }

        var handle = CreateJobObjectW(IntPtr.Zero, null);
        if (handle == IntPtr.Zero)
        {
            error = $"create-job-object-{Marshal.GetLastWin32Error()}";
            return false;
        }

        var information = new JobObjectExtendedLimitInformation
        {
            BasicLimitInformation = new JobObjectBasicLimitInformation
            {
                LimitFlags = JobObjectLimitKillOnJobClose,
            },
        };
        if (!SetInformationJobObject(
                handle,
                JobObjectExtendedLimitInformationClass,
                ref information,
                (uint)Marshal.SizeOf<JobObjectExtendedLimitInformation>()))
        {
            error = $"set-job-information-{Marshal.GetLastWin32Error()}";
            CloseHandle(handle);
            return false;
        }

        job = new WindowsJobObject(handle);
        return true;
    }

    public bool Assign(IntPtr processHandle, out string? error)
    {
        error = null;
        if (_handle == IntPtr.Zero)
        {
            error = "job-closed";
            return false;
        }

        if (!AssignProcessToJobObject(_handle, processHandle))
        {
            error = $"assign-process-{Marshal.GetLastWin32Error()}";
            return false;
        }

        return true;
    }

    public void Terminate(uint exitCode)
    {
        if (_handle != IntPtr.Zero)
        {
            _ = TerminateJobObject(_handle, exitCode);
        }
    }

    public void Dispose()
    {
        if (_handle == IntPtr.Zero)
        {
            return;
        }

        var handle = _handle;
        _handle = IntPtr.Zero;
        CloseHandle(handle);
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateJobObjectW(IntPtr securityAttributes, string? name);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetInformationJobObject(
        IntPtr job,
        int informationClass,
        ref JobObjectExtendedLimitInformation information,
        uint informationLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool TerminateJobObject(IntPtr job, uint exitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectBasicLimitInformation
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize;
        public UIntPtr MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectExtendedLimitInformation
    {
        public JobObjectBasicLimitInformation BasicLimitInformation;
        public IoCounters IoInfo;
        public UIntPtr ProcessMemoryLimit;
        public UIntPtr JobMemoryLimit;
        public UIntPtr PeakProcessMemoryUsed;
        public UIntPtr PeakJobMemoryUsed;
    }
}
