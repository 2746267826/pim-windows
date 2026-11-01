using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;
using Pim.Client.Core.Models.LocalRuntime;

namespace Pim.Client.Core.Services.LocalRuntime;

/// <summary>
/// Windows launcher that creates the target suspended, so the caller can assign it
/// to a Job Object before a single instruction of user code runs.
/// </summary>
internal static class WindowsSuspendedProcessLauncher
{
    private const uint CreateSuspended = 0x00000004;
    private const uint CreateNoWindow = 0x08000000;
    private const uint ExtendedStartupInfoPresent = 0x00080000;
    private const int StartfUseStdHandles = 0x00000100;
    private const uint HandleFlagInherit = 0x00000001;
    private const uint GenericRead = 0x80000000;
    private const uint GenericWrite = 0x40000000;
    private const uint FileShareRead = 0x00000001;
    private const uint FileShareWrite = 0x00000002;
    private const uint OpenExisting = 3;
    private const int InsufficientBuffer = 122;
    private const int ErrorDirectory = 267;
    private const int ProcThreadAttributeHandleList = 0x00020002;

    public static bool TryStart(
        RuntimeLaunchSpec spec,
        out SuspendedLaunch? launch,
        out string? error)
    {
        launch = null;
        error = null;
        if (!OperatingSystem.IsWindows())
        {
            error = "not-windows";
            return false;
        }

        var security = new SecurityAttributes
        {
            Length = Marshal.SizeOf<SecurityAttributes>(),
            InheritHandle = true,
        };

        IntPtr stdoutRead = IntPtr.Zero;
        IntPtr stdoutWrite = IntPtr.Zero;
        IntPtr stderrRead = IntPtr.Zero;
        IntPtr stderrWrite = IntPtr.Zero;
        var nul = IntPtr.Zero;
        IntPtr attributeList = IntPtr.Zero;
        GCHandle handleListPin = default;
        Process? process = null;
        StreamReader? stdoutReader = null;
        StreamReader? stderrReader = null;
        ProcessInformation processInformation = default;
        try
        {
            if (!CreatePipe(out stdoutRead, out stdoutWrite, ref security, 0)
                || !CreatePipe(out stderrRead, out stderrWrite, ref security, 0))
            {
                error = $"create-pipe-{Marshal.GetLastWin32Error()}";
                return false;
            }

            SetHandleInformation(stdoutRead, HandleFlagInherit, 0);
            SetHandleInformation(stderrRead, HandleFlagInherit, 0);

            // Always create our own inheritable NUL handle. Reusing the caller's
            // console handle would make the child inherit a handle outside the
            // explicit list and can leak unrelated pipes.
            nul = CreateFileW(
                "NUL",
                GenericRead | GenericWrite,
                FileShareRead | FileShareWrite,
                ref security,
                OpenExisting,
                0,
                IntPtr.Zero);
            if (nul == IntPtr.Zero || nul == new IntPtr(-1))
            {
                error = $"open-nul-{Marshal.GetLastWin32Error()}";
                return false;
            }

            var handles = new[] { nul, stdoutWrite, stderrWrite };
            if (!TryCreateHandleListAttribute(handles, out attributeList, out error))
            {
                return false;
            }

            handleListPin = GCHandle.Alloc(handles, GCHandleType.Pinned);
            if (!UpdateProcThreadAttribute(
                    attributeList,
                    0,
                    ProcThreadAttributeHandleList,
                    handleListPin.AddrOfPinnedObject(),
                    (IntPtr)(IntPtr.Size * handles.Length),
                    IntPtr.Zero,
                    IntPtr.Zero))
            {
                error = $"handle-list-attribute-{Marshal.GetLastWin32Error()}";
                return false;
            }

            var startup = new StartupInfoEx
            {
                StartupInfo = new StartupInfo
                {
                    Size = Marshal.SizeOf<StartupInfoEx>(),
                    Flags = StartfUseStdHandles,
                    StandardInput = nul,
                    StandardOutput = stdoutWrite,
                    StandardError = stderrWrite,
                },
                AttributeList = attributeList,
            };

            var commandLine = BuildCommandLine(spec.ExecutablePath, spec.Arguments);
            var nativeExecutablePath = ToNativeApplicationPath(spec.ExecutablePath);
            var created = CreateProcessW(
                nativeExecutablePath,
                commandLine,
                IntPtr.Zero,
                IntPtr.Zero,
                inheritHandles: true,
                CreateSuspended | CreateNoWindow | ExtendedStartupInfoPresent,
                IntPtr.Zero,
                spec.WorkingDirectory,
                ref startup,
                out processInformation);
            if (!created)
            {
                var firstError = Marshal.GetLastWin32Error();
                if (firstError != ErrorDirectory)
                {
                    error = $"create-process-{firstError};cwd={spec.WorkingDirectory}";
                    return false;
                }

                // CreateProcessW still enforces the legacy current-directory path limit
                // even when long-path process/image paths are otherwise available.
                // The executable is already an absolute, verified path and all asset
                // arguments are absolute, so retry without an invalid CWD rather than
                // failing a runtime solely because its install path is deep.
                commandLine = BuildCommandLine(spec.ExecutablePath, spec.Arguments);
                created = CreateProcessW(
                    nativeExecutablePath,
                    commandLine,
                    IntPtr.Zero,
                    IntPtr.Zero,
                    inheritHandles: true,
                    CreateSuspended | CreateNoWindow | ExtendedStartupInfoPresent,
                    IntPtr.Zero,
                    null,
                    ref startup,
                    out processInformation);
                if (!created)
                {
                    error = $"create-process-{Marshal.GetLastWin32Error()};"
                            + $"cwd={spec.WorkingDirectory};cwd-fallback";
                    return false;
                }
            }

            process = Process.GetProcessById(processInformation.ProcessId);
            stdoutReader = CreateReader(stdoutRead);
            stdoutRead = IntPtr.Zero;
            stderrReader = CreateReader(stderrRead);
            stderrRead = IntPtr.Zero;

            launch = new SuspendedLaunch(
                process,
                processInformation.ProcessHandle,
                processInformation.ThreadHandle,
                stdoutReader,
                stderrReader);
            processInformation = default;
            return true;
        }
        finally
        {
            if (launch is null && processInformation.ProcessHandle != IntPtr.Zero)
            {
                // Creation succeeded but handle/stream setup failed: the process is
                // still suspended, so terminate it rather than leaking an orphan.
                _ = TerminateProcess(processInformation.ProcessHandle, 1);
            }

            if (launch is null)
            {
                stdoutReader?.Dispose();
                stderrReader?.Dispose();
                process?.Dispose();
            }

            CloseIfValid(stdoutWrite);
            CloseIfValid(stderrWrite);
            CloseIfValid(stdoutRead);
            CloseIfValid(stderrRead);
            if (processInformation.ProcessHandle != IntPtr.Zero)
            {
                CloseHandle(processInformation.ProcessHandle);
            }

            if (processInformation.ThreadHandle != IntPtr.Zero)
            {
                CloseHandle(processInformation.ThreadHandle);
            }

            if (nul != IntPtr.Zero && nul != new IntPtr(-1))
            {
                CloseHandle(nul);
            }

            if (handleListPin.IsAllocated)
            {
                handleListPin.Free();
            }

            if (attributeList != IntPtr.Zero)
            {
                DeleteProcThreadAttributeList(attributeList);
                Marshal.FreeHGlobal(attributeList);
            }
        }
    }

    private static bool TryCreateHandleListAttribute(
        IntPtr[] handles,
        out IntPtr attributeList,
        out string? error)
    {
        attributeList = IntPtr.Zero;
        error = null;
        var size = IntPtr.Zero;
        _ = InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref size);
        if (size == IntPtr.Zero || Marshal.GetLastWin32Error() != InsufficientBuffer)
        {
            error = $"initialize-handle-list-{Marshal.GetLastWin32Error()}";
            return false;
        }

        var buffer = Marshal.AllocHGlobal(size);
        if (!InitializeProcThreadAttributeList(buffer, 1, 0, ref size))
        {
            error = $"initialize-handle-list-{Marshal.GetLastWin32Error()}";
            Marshal.FreeHGlobal(buffer);
            return false;
        }

        attributeList = buffer;
        return true;
    }

    private static StreamReader CreateReader(IntPtr readHandle)
    {
        var safe = new SafeFileHandle(readHandle, ownsHandle: true);
        var stream = new FileStream(safe, FileAccess.Read, bufferSize: 4096, isAsync: false);
        return new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
    }

    private static string BuildCommandLine(string executablePath, IReadOnlyList<string> arguments)
    {
        var builder = new StringBuilder();
        builder.Append(Quote(executablePath));
        foreach (var argument in arguments)
        {
            builder.Append(' ').Append(Quote(argument));
        }

        return builder.ToString();
    }

    private static string ToNativeApplicationPath(string path)
    {
        if (path.Length < 248
            || path.StartsWith(@"\\?\", StringComparison.Ordinal)
            || !Path.IsPathFullyQualified(path))
        {
            return path;
        }

        return path.StartsWith(@"\\", StringComparison.Ordinal)
            ? @"\\?\UNC\" + path[2..]
            : @"\\?\" + path;
    }

    private static string Quote(string value)
    {
        if (value.Length > 0 && value.IndexOfAny(new[] { ' ', '\t', '"' }) < 0)
        {
            return value;
        }

        var builder = new StringBuilder("\"");
        var backslashes = 0;
        foreach (var character in value)
        {
            if (character == '\\')
            {
                backslashes++;
                continue;
            }

            if (character == '"')
            {
                builder.Append('\\', backslashes * 2 + 1);
                builder.Append('"');
                backslashes = 0;
                continue;
            }

            builder.Append('\\', backslashes);
            builder.Append(character);
            backslashes = 0;
        }

        builder.Append('\\', backslashes * 2);
        builder.Append('"');
        return builder.ToString();
    }

    private static void CloseIfValid(IntPtr handle)
    {
        if (handle != IntPtr.Zero && handle != new IntPtr(-1))
        {
            CloseHandle(handle);
        }
    }

    public sealed class SuspendedLaunch : IDisposable
    {
        private IntPtr _processHandle;
        private IntPtr _threadHandle;
        private StreamReader? _standardOutput;
        private StreamReader? _standardError;

        internal SuspendedLaunch(
            Process process,
            IntPtr processHandle,
            IntPtr threadHandle,
            StreamReader standardOutput,
            StreamReader standardError)
        {
            Process = process;
            _processHandle = processHandle;
            _threadHandle = threadHandle;
            _standardOutput = standardOutput;
            _standardError = standardError;
        }

        public Process Process { get; }

        public IntPtr ProcessHandle => _processHandle;

        public bool Resume(out string? error)
        {
            error = null;
            if (_threadHandle == IntPtr.Zero)
            {
                error = "thread-handle-closed";
                return false;
            }

            var result = ResumeThread(_threadHandle);
            if (result == uint.MaxValue)
            {
                error = $"resume-thread-{Marshal.GetLastWin32Error()}";
                return false;
            }

            return true;
        }

        public void Terminate(uint exitCode)
        {
            if (_processHandle != IntPtr.Zero)
            {
                _ = TerminateProcess(_processHandle, exitCode);
            }
        }

        public (StreamReader StandardOutput, StreamReader StandardError) DetachStreams()
        {
            var stdout = _standardOutput
                         ?? throw new InvalidOperationException("stdout stream already detached");
            var stderr = _standardError
                         ?? throw new InvalidOperationException("stderr stream already detached");
            _standardOutput = null;
            _standardError = null;
            return (stdout, stderr);
        }

        public void ReleaseProcessHandles()
        {
            if (_threadHandle != IntPtr.Zero)
            {
                CloseHandle(_threadHandle);
                _threadHandle = IntPtr.Zero;
            }

            if (_processHandle != IntPtr.Zero)
            {
                CloseHandle(_processHandle);
                _processHandle = IntPtr.Zero;
            }
        }

        public void Dispose()
        {
            ReleaseProcessHandles();
            _standardOutput?.Dispose();
            _standardError?.Dispose();
            _standardOutput = null;
            _standardError = null;
            Process.Dispose();
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SecurityAttributes
    {
        public int Length;
        public IntPtr SecurityDescriptor;
        [MarshalAs(UnmanagedType.Bool)]
        public bool InheritHandle;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct StartupInfo
    {
        public int Size;
        public string? Reserved;
        public string? Desktop;
        public string? Title;
        public int X;
        public int Y;
        public int XSize;
        public int YSize;
        public int XCountChars;
        public int YCountChars;
        public int FillAttribute;
        public int Flags;
        public short ShowWindow;
        public short Reserved2;
        public IntPtr Reserved2Pointer;
        public IntPtr StandardInput;
        public IntPtr StandardOutput;
        public IntPtr StandardError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct StartupInfoEx
    {
        public StartupInfo StartupInfo;
        public IntPtr AttributeList;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformation
    {
        public IntPtr ProcessHandle;
        public IntPtr ThreadHandle;
        public int ProcessId;
        public int ThreadId;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CreatePipe(
        out IntPtr readPipe,
        out IntPtr writePipe,
        ref SecurityAttributes securityAttributes,
        uint size);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetHandleInformation(IntPtr handle, uint mask, uint flags);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateFileW(
        string fileName,
        uint desiredAccess,
        uint shareMode,
        ref SecurityAttributes securityAttributes,
        uint creationDisposition,
        uint flagsAndAttributes,
        IntPtr templateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool InitializeProcThreadAttributeList(
        IntPtr attributeList,
        int attributeCount,
        uint flags,
        ref IntPtr size);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool UpdateProcThreadAttribute(
        IntPtr attributeList,
        uint flags,
        IntPtr attribute,
        IntPtr value,
        IntPtr size,
        IntPtr previousValue,
        IntPtr returnSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern void DeleteProcThreadAttributeList(IntPtr attributeList);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CreateProcessW(
        string applicationName,
        string commandLine,
        IntPtr processAttributes,
        IntPtr threadAttributes,
        bool inheritHandles,
        uint creationFlags,
        IntPtr environment,
        string? currentDirectory,
        ref StartupInfoEx startupInfo,
        out ProcessInformation processInformation);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint ResumeThread(IntPtr thread);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool TerminateProcess(IntPtr process, uint exitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);
}
