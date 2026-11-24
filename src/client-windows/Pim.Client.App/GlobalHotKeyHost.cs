using System.Runtime.InteropServices;
using Microsoft.Win32;
using System.Windows.Interop;
using Pim.Client.Core.Services;

namespace Pim.Client.App;

internal sealed class GlobalHotKeyHost : IDisposable
{
    internal const int WmHotKey = 0x0312;

    private readonly IGlobalHotKeyRegistry _registry;
    private readonly GlobalHotKeyGesture _gesture;
    private readonly Action _summon;
    private readonly Action<string> _notifyWarning;
    private readonly Action<string> _log;
    private readonly Func<HwndSource>? _sourceFactory;
    private HwndSource? _source;
    private GlobalHotKeyCoordinator? _coordinator;
    private bool _started;
    private bool _sessionHooksAttached;
    private bool _disposed;

    internal GlobalHotKeyHost(
        IGlobalHotKeyRegistry registry,
        GlobalHotKeyGesture gesture,
        Action summon,
        Action<string> notifyWarning,
        Action<string> log,
        Func<HwndSource>? sourceFactory = null)
    {
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _gesture = gesture ?? throw new ArgumentNullException(nameof(gesture));
        _summon = summon ?? throw new ArgumentNullException(nameof(summon));
        _notifyWarning = notifyWarning ?? throw new ArgumentNullException(nameof(notifyWarning));
        _log = log ?? throw new ArgumentNullException(nameof(log));
        _sourceFactory = sourceFactory;
    }

    internal IntPtr Handle => _source?.Handle ?? IntPtr.Zero;

    public void Start()
    {
        if (_started || _disposed)
        {
            return;
        }

        try
        {
            _source = (_sourceFactory ?? CreateMessageSource)();
            _source.AddHook(WindowMessageHook);
            _coordinator = new GlobalHotKeyCoordinator(
                _registry,
                _gesture,
                _summon,
                failure =>
                {
                    _log($"Global hotkey unavailable ({failure.Failure}, error={failure.ErrorCode}): {failure.Message}");
                    NotifySafely(failure.Message);
                });
            _coordinator.Start(_source.Handle);
            _started = true;

            try
            {
                SystemEvents.SessionSwitch += OnSessionSwitch;
                _sessionHooksAttached = true;
                SystemEvents.SessionEnding += OnSessionEnding;
            }
            catch (Exception ex)
            {
                TryDetachSessionHooks();
                _log($"Global hotkey session hook failed: {ex.Message}");
            }

            _log(_coordinator.IsRegistered
                ? $"Global hotkey registered: {_gesture.DisplayName}"
                : $"Global hotkey not registered: {_gesture.DisplayName}");
        }
        catch (Exception ex)
        {
            _log($"Global hotkey host failed: {ex}");
            NotifySafely(
                $"无法启用全局快捷键 {_gesture.DisplayName}。PIM 仍在运行，可继续从托盘菜单打开笔记。");
            Dispose();
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _started = false;
        TryDetachSessionHooks();

        _coordinator?.Dispose();
        _coordinator = null;

        if (_source is not null)
        {
            try
            {
                _source.RemoveHook(WindowMessageHook);
                _source.Dispose();
            }
            catch (Exception ex)
            {
                _log($"Global hotkey message window disposal failed: {ex.Message}");
            }

            _source = null;
        }
    }

    private void TryDetachSessionHooks()
    {
        if (!_sessionHooksAttached)
        {
            return;
        }

        _sessionHooksAttached = false;
        try
        {
            SystemEvents.SessionSwitch -= OnSessionSwitch;
            SystemEvents.SessionEnding -= OnSessionEnding;
        }
        catch (Exception ex)
        {
            _log($"Global hotkey session unsubscribe failed: {ex.Message}");
        }
    }

    internal static HwndSource CreateMessageSource()
    {
        var parameters = new HwndSourceParameters("PIM.GlobalHotKey")
        {
            ParentWindow = new IntPtr(-3),
            WindowStyle = 0,
            ExtendedWindowStyle = 0,
            Width = 0,
            Height = 0,
        };
        return new HwndSource(parameters);
    }

    private IntPtr WindowMessageHook(
        IntPtr hwnd,
        int message,
        IntPtr wParam,
        IntPtr lParam,
        ref bool handled)
    {
        if (message == WmHotKey)
        {
            _coordinator?.HandleHotKey(wParam.ToInt32());
            handled = true;
        }

        return IntPtr.Zero;
    }

    private void OnSessionSwitch(object sender, SessionSwitchEventArgs e)
        => HandleSessionSwitchReason(e.Reason);

    internal void HandleSessionSwitchReason(SessionSwitchReason reason)
    {
        GlobalHotKeySessionState? state = reason switch
        {
            SessionSwitchReason.SessionLock
                or SessionSwitchReason.ConsoleDisconnect
                or SessionSwitchReason.RemoteDisconnect => GlobalHotKeySessionState.Locked,
            SessionSwitchReason.SessionLogoff => GlobalHotKeySessionState.Logoff,
            SessionSwitchReason.SessionUnlock
                or SessionSwitchReason.SessionLogon
                or SessionSwitchReason.ConsoleConnect
                or SessionSwitchReason.RemoteConnect => GlobalHotKeySessionState.Unlocked,
            _ => null,
        };
        if (state is { } value)
        {
            DispatchSessionState(value);
        }
    }

    private void OnSessionEnding(object sender, SessionEndingEventArgs e) =>
        DispatchSessionState(GlobalHotKeySessionState.Logoff);

    private void DispatchSessionState(GlobalHotKeySessionState state)
    {
        var source = _source;
        if (source is null)
        {
            return;
        }

        try
        {
            if (source.Dispatcher.CheckAccess())
            {
                _coordinator?.HandleSessionState(state);
            }
            else
            {
                source.Dispatcher.BeginInvoke(() => _coordinator?.HandleSessionState(state));
            }
        }
        catch (Exception ex)
        {
            _log($"Global hotkey session state dispatch failed ({state}): {ex.Message}");
        }
    }

    private void NotifySafely(string message)
    {
        try
        {
            _notifyWarning(message);
        }
        catch (Exception ex)
        {
            _log($"Global hotkey warning notification failed: {ex.Message}");
        }
    }
}

internal sealed class Win32GlobalHotKeyRegistry : IGlobalHotKeyRegistry
{
    public GlobalHotKeyRegistrationResult Register(IntPtr window, GlobalHotKeyGesture gesture)
    {
        if (RegisterHotKey(window, gesture.Id, gesture.Modifiers, gesture.VirtualKey))
        {
            return GlobalHotKeyRegistrationResult.Success;
        }

        return GlobalHotKeyRegistrationResult.Failure(Marshal.GetLastWin32Error());
    }

    public void Unregister(IntPtr window, int id) => UnregisterHotKey(window, id);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RegisterHotKey(
        IntPtr window,
        int id,
        uint modifiers,
        uint virtualKey);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnregisterHotKey(IntPtr window, int id);
}
