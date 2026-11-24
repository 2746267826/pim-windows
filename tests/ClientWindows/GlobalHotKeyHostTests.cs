using System.Runtime.InteropServices;
using Microsoft.Win32;
using Pim.Client.App;
using Pim.Client.Core.Services;
using Xunit;

namespace Pim.Client.App.Tests;

[Collection(WpfApplicationCollection.Name)]
public sealed class GlobalHotKeyHostTests(WpfApplicationFixture wpf)
{
    [Fact]
    public void WmHotKeyFromTheHiddenMessageWindowSummonsNotes()
    {
        wpf.Run(() =>
        {
            var registry = new FakeRegistry(GlobalHotKeyRegistrationResult.Success);
            var summoned = 0;
            var notices = new List<string>();
            var host = new GlobalHotKeyHost(
                registry,
                GlobalHotKeyGesture.NativeNotes,
                () => summoned++,
                notices.Add,
                _ => { });
            try
            {
                host.Start();

                Assert.NotEqual(IntPtr.Zero, host.Handle);
                Assert.Equal(1, registry.Registrations);
                SendMessage(
                    host.Handle,
                    GlobalHotKeyHost.WmHotKey,
                    new IntPtr(GlobalHotKeyGesture.NativeNotes.Id),
                    IntPtr.Zero);

                Assert.Equal(1, summoned);
                Assert.Empty(notices);
            }
            finally
            {
                host.Dispose();
            }

            Assert.Equal(1, registry.Unregistrations);
        });
    }

    [Fact]
    public void RegistrationConflictShowsAWarningButKeepsTheMessageHostSafe()
    {
        wpf.Run(() =>
        {
            var registry = new FakeRegistry(GlobalHotKeyRegistrationResult.Failure(1409));
            var summoned = 0;
            var notices = new List<string>();
            var host = new GlobalHotKeyHost(
                registry,
                GlobalHotKeyGesture.NativeNotes,
                () => summoned++,
                notices.Add,
                _ => { });
            try
            {
                var exception = Record.Exception(host.Start);

                Assert.Null(exception);
                Assert.NotEqual(IntPtr.Zero, host.Handle);
                var notice = Assert.Single(notices);
                Assert.Contains("Ctrl+Alt+N", notice);
                Assert.Contains("已被其他程序占用", notice);
                SendMessage(
                    host.Handle,
                    GlobalHotKeyHost.WmHotKey,
                    new IntPtr(GlobalHotKeyGesture.NativeNotes.Id),
                    IntPtr.Zero);
                Assert.Equal(0, summoned);
            }
            finally
            {
                host.Dispose();
            }
        });
    }

    [Fact]
    public void MessageWindowCreationFailureIsReportedWithoutCrashingStartup()
    {
        wpf.Run(() =>
        {
            var notices = new List<string>();
            var host = new GlobalHotKeyHost(
                new FakeRegistry(GlobalHotKeyRegistrationResult.Success),
                GlobalHotKeyGesture.NativeNotes,
                () => { },
                notices.Add,
                _ => { },
                () => throw new InvalidOperationException("desktop unavailable"));

            var exception = Record.Exception(host.Start);

            Assert.Null(exception);
            var notice = Assert.Single(notices);
            Assert.Contains("无法", notice);
            Assert.Contains("托盘", notice);
            host.Dispose();
        });
    }

    [Fact]
    public void ActualWin32RegistrationRoutesWmHotKeyAndReleasesOnDispose()
    {
        wpf.Run(() =>
        {
            var gesture = new GlobalHotKeyGesture(
                "Ctrl+Alt+Shift+F24",
                0x4D99,
                0x0001u | 0x0002u | 0x0004u,
                0x87u);
            var notices = new List<string>();
            var logs = new List<string>();
            var summoned = 0;

            var first = new GlobalHotKeyHost(
                new Win32GlobalHotKeyRegistry(),
                gesture,
                () => summoned++,
                notices.Add,
                logs.Add);
            try
            {
                first.Start();

                Assert.NotEqual(IntPtr.Zero, first.Handle);
                Assert.Empty(notices);
                Assert.Equal(
                    1,
                    logs.Count(line => line.Contains($"registered: {gesture.DisplayName}", StringComparison.Ordinal)));
                SendMessage(
                    first.Handle,
                    GlobalHotKeyHost.WmHotKey,
                    new IntPtr(gesture.Id),
                    IntPtr.Zero);
                Assert.Equal(1, summoned);
            }
            finally
            {
                first.Dispose();
            }

            Assert.DoesNotContain(
                logs,
                line => line.Contains("disposal failed", StringComparison.Ordinal));

            var second = new GlobalHotKeyHost(
                new Win32GlobalHotKeyRegistry(),
                gesture,
                () => summoned++,
                notices.Add,
                logs.Add);
            try
            {
                second.Start();

                Assert.Empty(notices);
                Assert.Equal(
                    2,
                    logs.Count(line => line.Contains($"registered: {gesture.DisplayName}", StringComparison.Ordinal)));
            }
            finally
            {
                second.Dispose();
            }
        });
    }

    [Fact]
    public void LockUnregistersTheHotKeyAndUnlockRestoresIt()
    {
        wpf.Run(() =>
        {
            var registry = new FakeRegistry(GlobalHotKeyRegistrationResult.Success);
            var host = new GlobalHotKeyHost(
                registry,
                GlobalHotKeyGesture.NativeNotes,
                () => { },
                _ => { },
                _ => { });
            try
            {
                host.Start();
                Assert.Equal(1, registry.Registrations);

                host.HandleSessionSwitchReason(SessionSwitchReason.SessionLock);
                Assert.Equal(1, registry.Unregistrations);

                host.HandleSessionSwitchReason(SessionSwitchReason.SessionUnlock);
                Assert.Equal(2, registry.Registrations);

                host.HandleSessionSwitchReason(SessionSwitchReason.SessionLogoff);
                Assert.Equal(2, registry.Unregistrations);

                host.HandleSessionSwitchReason(SessionSwitchReason.SessionUnlock);
                Assert.Equal(2, registry.Registrations);
            }
            finally
            {
                host.Dispose();
            }

            Assert.Equal(2, registry.Unregistrations);
        });
    }

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

    private sealed class FakeRegistry(params GlobalHotKeyRegistrationResult[] results)
        : IGlobalHotKeyRegistry
    {
        private readonly Queue<GlobalHotKeyRegistrationResult> _results = new(results);

        public int Registrations { get; private set; }

        public int Unregistrations { get; private set; }

        public GlobalHotKeyRegistrationResult Register(IntPtr window, GlobalHotKeyGesture gesture)
        {
            Registrations++;
            return _results.Count == 0 ? GlobalHotKeyRegistrationResult.Success : _results.Dequeue();
        }

        public void Unregister(IntPtr window, int id)
        {
            Unregistrations++;
        }
    }
}
