namespace Pim.Client.Core.Services;

public enum GlobalHotKeyRegistrationFailure
{
    None,
    AlreadyRegistered,
    AccessDenied,
    Failed,
}

public enum GlobalHotKeySessionState
{
    Unlocked,
    Locked,
    Logoff,
}

public readonly record struct GlobalHotKeyRegistrationResult(bool Succeeded, int ErrorCode)
{
    public static GlobalHotKeyRegistrationResult Success { get; } = new(true, 0);

    public static GlobalHotKeyRegistrationResult Failure(int errorCode) => new(false, errorCode);

    public GlobalHotKeyRegistrationFailure Classify()
    {
        if (Succeeded)
        {
            return GlobalHotKeyRegistrationFailure.None;
        }

        return ErrorCode switch
        {
            1409 => GlobalHotKeyRegistrationFailure.AlreadyRegistered,
            5 => GlobalHotKeyRegistrationFailure.AccessDenied,
            _ => GlobalHotKeyRegistrationFailure.Failed,
        };
    }
}

public sealed record GlobalHotKeyGesture(string DisplayName, int Id, uint Modifiers, uint VirtualKey)
{
    public static GlobalHotKeyGesture NativeNotes { get; } =
        new("Ctrl+Alt+N", 0x4D06, 0x0003, 0x4E);
}

public sealed record GlobalHotKeyFailureNotice(
    GlobalHotKeyRegistrationFailure Failure,
    int ErrorCode,
    string Message);

public interface IGlobalHotKeyRegistry
{
    GlobalHotKeyRegistrationResult Register(IntPtr window, GlobalHotKeyGesture gesture);

    void Unregister(IntPtr window, int id);
}

public sealed class GlobalHotKeyCoordinator : IDisposable
{
    private readonly IGlobalHotKeyRegistry _registry;
    private readonly GlobalHotKeyGesture _gesture;
    private readonly Action _summon;
    private readonly Action<GlobalHotKeyFailureNotice> _notifyFailure;
    private readonly object _gate = new();
    private IntPtr _window;
    private bool _started;
    private bool _suspended;
    private bool _terminal;
    private bool _registered;
    private bool _disposed;

    public GlobalHotKeyCoordinator(
        IGlobalHotKeyRegistry registry,
        GlobalHotKeyGesture gesture,
        Action summon,
        Action<GlobalHotKeyFailureNotice> notifyFailure)
    {
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _gesture = gesture ?? throw new ArgumentNullException(nameof(gesture));
        _summon = summon ?? throw new ArgumentNullException(nameof(summon));
        _notifyFailure = notifyFailure ?? throw new ArgumentNullException(nameof(notifyFailure));
    }

    public bool IsRegistered
    {
        get
        {
            lock (_gate)
            {
                return _registered;
            }
        }
    }

    public void Start(IntPtr window)
    {
        lock (_gate)
        {
            if (_disposed || _terminal)
            {
                return;
            }

            _window = window;
            _started = true;
            _suspended = false;
        }

        TryRegister();
    }

    public void HandleHotKey(int id)
    {
        Action? summon = null;
        lock (_gate)
        {
            if (_disposed
                || !_started
                || _suspended
                || !_registered
                || id != _gesture.Id)
            {
                return;
            }

            summon = _summon;
        }

        summon?.Invoke();
    }

    public void HandleSessionState(GlobalHotKeySessionState state)
    {
        if (state == GlobalHotKeySessionState.Unlocked)
        {
            lock (_gate)
            {
                if (_disposed || _terminal)
                {
                    return;
                }

                _suspended = false;
                if (!_started)
                {
                    return;
                }
            }

            TryRegister();
            return;
        }

        lock (_gate)
        {
            if (_disposed || (_suspended && state != GlobalHotKeySessionState.Logoff))
            {
                return;
            }

            _suspended = true;
            if (state == GlobalHotKeySessionState.Logoff)
            {
                _terminal = true;
                _started = false;
            }

            UnregisterLocked();
        }
    }

    public void Stop()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _started = false;
            _suspended = true;
            UnregisterLocked();
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _started = false;
            _suspended = true;
            UnregisterLocked();
        }
    }

    private void TryRegister()
    {
        GlobalHotKeyFailureNotice? notice = null;
        lock (_gate)
        {
            if (_disposed
                || _terminal
                || !_started
                || _suspended
                || _registered
                || _window == IntPtr.Zero)
            {
                return;
            }

            GlobalHotKeyRegistrationResult result;
            try
            {
                result = _registry.Register(_window, _gesture);
            }
            catch
            {
                result = GlobalHotKeyRegistrationResult.Failure(0);
            }

            if (result.Succeeded)
            {
                _registered = true;
                return;
            }

            var failure = result.Classify();
            notice = new GlobalHotKeyFailureNotice(
                failure,
                result.ErrorCode,
                GlobalHotKeyMessages.ForFailure(_gesture, failure));
        }

        if (notice is not null)
        {
            _notifyFailure(notice);
        }
    }

    private void UnregisterLocked()
    {
        if (!_registered)
        {
            return;
        }

        _registered = false;
        try
        {
            _registry.Unregister(_window, _gesture.Id);
        }
        catch
        {
            // 注销失败不能阻塞退出、锁屏或注销；状态已先置为未注册，后续消息也不会触发窗口。
        }
    }
}

public static class GlobalHotKeyMessages
{
    public static string ForFailure(GlobalHotKeyGesture gesture, GlobalHotKeyRegistrationFailure failure) =>
        failure switch
        {
            GlobalHotKeyRegistrationFailure.AlreadyRegistered =>
                $"全局快捷键 {gesture.DisplayName} 已被其他程序占用。PIM 仍在运行，可继续从托盘菜单打开笔记；关闭占用该快捷键的程序后重启 PIM 可恢复。",
            GlobalHotKeyRegistrationFailure.AccessDenied =>
                $"当前账户权限不足，无法注册全局快捷键 {gesture.DisplayName}。PIM 仍在运行，可继续从托盘菜单打开笔记。",
            _ =>
                $"无法注册全局快捷键 {gesture.DisplayName}。PIM 仍在运行，可继续从托盘菜单打开笔记。",
        };
}
