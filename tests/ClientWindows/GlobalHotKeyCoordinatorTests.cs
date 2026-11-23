using Pim.Client.Core.Services;
using Xunit;

namespace Pim.UnitTests.ClientWindows;

public sealed class GlobalHotKeyCoordinatorTests
{
    private static readonly IntPtr MessageWindow = new(0x4321);

    [Fact]
    public void NativeNotesGesture_UsesARegisterHotKeyCompatibleIdAndChord()
    {
        var gesture = GlobalHotKeyGesture.NativeNotes;

        Assert.InRange(gesture.Id, 1, 0xBFFF);
        Assert.Equal(0x0003u, gesture.Modifiers);
        Assert.Equal(0x4Eu, gesture.VirtualKey);
    }

    [Fact]
    public void Start_RegistersCtrlAltNAndDisposeUnregistersIt()
    {
        var registry = new FakeRegistry();
        var activated = 0;
        var coordinator = new GlobalHotKeyCoordinator(
            registry,
            GlobalHotKeyGesture.NativeNotes,
            () => activated++,
            _ => { });

        coordinator.Start(MessageWindow);

        Assert.Equal(GlobalHotKeyGesture.NativeNotes, Assert.Single(registry.Registrations).Gesture);
        Assert.Equal("Ctrl+Alt+N", GlobalHotKeyGesture.NativeNotes.DisplayName);
        Assert.True(coordinator.IsRegistered);

        coordinator.HandleHotKey(GlobalHotKeyGesture.NativeNotes.Id);
        Assert.Equal(1, activated);

        coordinator.Dispose();

        Assert.Equal(1, registry.Unregistrations);
        Assert.False(coordinator.IsRegistered);
    }

    [Fact]
    public void HotKeyMessage_IsIgnoredWhenItTargetsAnotherIdOrRegistrationFailed()
    {
        var registry = new FakeRegistry();
        var activated = 0;
        var coordinator = new GlobalHotKeyCoordinator(
            registry,
            GlobalHotKeyGesture.NativeNotes,
            () => activated++,
            _ => { });

        coordinator.Start(MessageWindow);
        coordinator.HandleHotKey(GlobalHotKeyGesture.NativeNotes.Id + 1);
        Assert.Equal(0, activated);

        var failedRegistry = new FakeRegistry(GlobalHotKeyRegistrationResult.Failure(1409));
        var failedCoordinator = new GlobalHotKeyCoordinator(
            failedRegistry,
            GlobalHotKeyGesture.NativeNotes,
            () => activated++,
            _ => { });
        failedCoordinator.Start(MessageWindow);
        failedCoordinator.HandleHotKey(GlobalHotKeyGesture.NativeNotes.Id);

        Assert.Equal(0, activated);
        Assert.False(failedCoordinator.IsRegistered);
    }

    [Fact]
    public void Conflict_IsReportedAsUserVisibleFallbackAndDoesNotThrow()
    {
        var registry = new FakeRegistry(GlobalHotKeyRegistrationResult.Failure(1409));
        var notices = new List<GlobalHotKeyFailureNotice>();
        var coordinator = new GlobalHotKeyCoordinator(
            registry,
            GlobalHotKeyGesture.NativeNotes,
            () => { },
            notices.Add);

        var exception = Record.Exception(() => coordinator.Start(MessageWindow));

        Assert.Null(exception);
        var notice = Assert.Single(notices);
        Assert.Equal(GlobalHotKeyRegistrationFailure.AlreadyRegistered, notice.Failure);
        Assert.Contains("Ctrl+Alt+N", notice.Message);
        Assert.Contains("已被其他程序占用", notice.Message);
        Assert.Contains("托盘", notice.Message);
    }

    [Fact]
    public void PermissionDenied_IsReportedWithPermissionGuidance()
    {
        var registry = new FakeRegistry(GlobalHotKeyRegistrationResult.Failure(5));
        var notices = new List<GlobalHotKeyFailureNotice>();
        var coordinator = new GlobalHotKeyCoordinator(
            registry,
            GlobalHotKeyGesture.NativeNotes,
            () => { },
            notices.Add);

        coordinator.Start(MessageWindow);

        var notice = Assert.Single(notices);
        Assert.Equal(GlobalHotKeyRegistrationFailure.AccessDenied, notice.Failure);
        Assert.Contains("权限", notice.Message);
        Assert.Contains("托盘", notice.Message);
    }

    [Fact]
    public void RegistrarException_IsContainedAndReportedAsRegistrationFailure()
    {
        var registry = new FakeRegistry { RegisterException = new UnauthorizedAccessException("denied") };
        var notices = new List<GlobalHotKeyFailureNotice>();
        var coordinator = new GlobalHotKeyCoordinator(
            registry,
            GlobalHotKeyGesture.NativeNotes,
            () => { },
            notices.Add);

        var exception = Record.Exception(() => coordinator.Start(MessageWindow));

        Assert.Null(exception);
        var notice = Assert.Single(notices);
        Assert.Equal(GlobalHotKeyRegistrationFailure.Failed, notice.Failure);
        Assert.Contains("无法注册", notice.Message);
    }

    [Fact]
    public void LockSuspendsShortcut_AndUnlockRegistersAgain()
    {
        var registry = new FakeRegistry();
        var activated = 0;
        var coordinator = new GlobalHotKeyCoordinator(
            registry,
            GlobalHotKeyGesture.NativeNotes,
            () => activated++,
            _ => { });

        coordinator.Start(MessageWindow);
        coordinator.HandleSessionState(GlobalHotKeySessionState.Locked);
        coordinator.HandleSessionState(GlobalHotKeySessionState.Locked);

        Assert.False(coordinator.IsRegistered);
        Assert.Equal(1, registry.Unregistrations);
        Assert.Single(registry.Registrations);

        coordinator.HandleHotKey(GlobalHotKeyGesture.NativeNotes.Id);
        Assert.Equal(0, activated);

        coordinator.HandleSessionState(GlobalHotKeySessionState.Unlocked);
        coordinator.HandleHotKey(GlobalHotKeyGesture.NativeNotes.Id);

        Assert.True(coordinator.IsRegistered);
        Assert.Equal(2, registry.Registrations.Count);
        Assert.Equal(1, activated);

        coordinator.Dispose();
        Assert.Equal(2, registry.Unregistrations);
    }

    [Fact]
    public void UnlockRetry_RecoversAfterInitialConflictWasCleared()
    {
        var registry = new FakeRegistry(
            GlobalHotKeyRegistrationResult.Failure(1409),
            GlobalHotKeyRegistrationResult.Success);
        var notices = new List<GlobalHotKeyFailureNotice>();
        var coordinator = new GlobalHotKeyCoordinator(
            registry,
            GlobalHotKeyGesture.NativeNotes,
            () => { },
            notices.Add);

        coordinator.Start(MessageWindow);
        Assert.Single(notices);
        Assert.False(coordinator.IsRegistered);

        coordinator.HandleSessionState(GlobalHotKeySessionState.Locked);
        coordinator.HandleSessionState(GlobalHotKeySessionState.Unlocked);

        Assert.True(coordinator.IsRegistered);
        Assert.Equal(2, registry.Registrations.Count);
        Assert.Single(notices);
    }

    [Fact]
    public void Logoff_UnregistersPermanentlyAndDoesNotReactivateOnUnlock()
    {
        var registry = new FakeRegistry();
        var activated = 0;
        var coordinator = new GlobalHotKeyCoordinator(
            registry,
            GlobalHotKeyGesture.NativeNotes,
            () => activated++,
            _ => { });

        coordinator.Start(MessageWindow);
        coordinator.HandleSessionState(GlobalHotKeySessionState.Locked);
        coordinator.HandleSessionState(GlobalHotKeySessionState.Logoff);
        coordinator.HandleSessionState(GlobalHotKeySessionState.Unlocked);
        coordinator.HandleHotKey(GlobalHotKeyGesture.NativeNotes.Id);

        Assert.False(coordinator.IsRegistered);
        Assert.Single(registry.Registrations);
        Assert.Equal(1, registry.Unregistrations);
        Assert.Equal(0, activated);

        coordinator.Dispose();
        Assert.Equal(1, registry.Unregistrations);
    }

    [Fact]
    public void Stop_UnregistersAndIgnoresFurtherHotKeyMessages()
    {
        var registry = new FakeRegistry();
        var activated = 0;
        var coordinator = new GlobalHotKeyCoordinator(
            registry,
            GlobalHotKeyGesture.NativeNotes,
            () => activated++,
            _ => { });

        coordinator.Start(MessageWindow);
        coordinator.Stop();
        coordinator.HandleHotKey(GlobalHotKeyGesture.NativeNotes.Id);

        Assert.False(coordinator.IsRegistered);
        Assert.Equal(1, registry.Unregistrations);
        Assert.Equal(0, activated);
    }

    private sealed class FakeRegistry : IGlobalHotKeyRegistry
    {
        private readonly Queue<GlobalHotKeyRegistrationResult> _results;

        public FakeRegistry(params GlobalHotKeyRegistrationResult[] results)
        {
            _results = new Queue<GlobalHotKeyRegistrationResult>(results);
        }

        public Exception? RegisterException { get; init; }

        public List<(IntPtr Window, GlobalHotKeyGesture Gesture)> Registrations { get; } = [];

        public int Unregistrations { get; private set; }

        public GlobalHotKeyRegistrationResult Register(IntPtr window, GlobalHotKeyGesture gesture)
        {
            if (RegisterException is not null)
            {
                throw RegisterException;
            }

            Registrations.Add((window, gesture));
            return _results.Count == 0 ? GlobalHotKeyRegistrationResult.Success : _results.Dequeue();
        }

        public void Unregister(IntPtr window, int id)
        {
            Unregistrations++;
        }
    }
}
