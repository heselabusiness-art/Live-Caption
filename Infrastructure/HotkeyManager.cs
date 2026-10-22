using System.Runtime.InteropServices;
using System.Windows.Interop;
using System.Windows.Threading;
using Serilog;
using static LiveCaptionViewer.Infrastructure.NativeMethods;

namespace LiveCaptionViewer.Infrastructure;

public enum HotkeyAction
{
    /// <summary>Home / Vol Up: capture the segment and send it as an interview question.</summary>
    CaptureAndSend,
    /// <summary>End / Vol Down: capture the segment and send it as a pushback.</summary>
    CaptureAndSendPushback,
    /// <summary>PageUp / Mute: capture the segment, display only.</summary>
    CaptureOnly,
    /// <summary>PageDown: send Reminder.txt.</summary>
    SendReminder,
    /// <summary>Insert: capture the selected window's screenshot (Tech Interview tab).</summary>
    TechCapture,
    /// <summary>Delete: capture the segment and send it as a coding question.</summary>
    TechSend,
    /// <summary>Ctrl+W: toggle click-through mode.</summary>
    ToggleClickThrough
}

/// <summary>
/// Owns every global input hook of the app: <c>RegisterHotKey</c> keys, the
/// <c>WM_APPCOMMAND</c> headset buttons and the low-level keyboard hook that catches
/// headset volume keys system-wide. Raises <see cref="Triggered"/> on the UI thread.
/// </summary>
internal sealed class HotkeyManager : IDisposable
{
    private readonly record struct Binding(int Id, HotkeyAction Action, uint Modifiers, uint Key, string Label);

    private static readonly Binding[] Bindings =
    [
        new(0xC001, HotkeyAction.CaptureAndSend,         MOD_NONE, VK_HOME,   "Home"),
        new(0xC002, HotkeyAction.CaptureOnly,            MOD_NONE, VK_PRIOR,  "PageUp"),
        new(0xC006, HotkeyAction.SendReminder,           MOD_NONE, VK_NEXT,   "PageDown"),
        new(0xC003, HotkeyAction.CaptureAndSendPushback, MOD_NONE, VK_END,    "End"),
        new(0xC004, HotkeyAction.TechCapture,            MOD_NONE, VK_INSERT, "Insert"),
        new(0xC005, HotkeyAction.TechSend,               MOD_NONE, VK_DELETE, "Delete"),
        new(0xC007, HotkeyAction.ToggleClickThrough,     MOD_CTRL, VK_W,      "Ctrl+W")
    ];

    private static readonly ILogger Log = AppLog.For(nameof(HotkeyManager));

    private readonly IntPtr _hwnd;
    private readonly HwndSource _source;
    private readonly Dispatcher _dispatcher;
    private readonly Dictionary<int, HotkeyAction> _registered = new();
    private readonly List<Binding> _unregistered = new();
    private readonly List<string> _failedKeys = new();
    private readonly List<string> _fallbackKeys = new();
    private readonly List<string> _failureDetails = new();

    // Kept in a field so the delegate is not garbage-collected while the hook is installed.
    private LowLevelKeyboardProc? _hookProc;
    private IntPtr _hookHandle = IntPtr.Zero;
    private bool _disposed;

    public HotkeyManager(IntPtr hwnd, HwndSource source, Dispatcher dispatcher)
    {
        _hwnd = hwnd;
        _source = source;
        _dispatcher = dispatcher;
    }

    public event Action<HotkeyAction>? Triggered;

    /// <summary>Labels of keys that could not be hooked at all (they do nothing).</summary>
    public IReadOnlyList<string> FailedKeys => _failedKeys;

    /// <summary>
    /// Keys another app (or another running copy of this one) already owns, now served by the
    /// low-level keyboard hook instead. They work, but the other app no longer sees them.
    /// </summary>
    public IReadOnlyList<string> FallbackKeys => _fallbackKeys;

    /// <summary>Human-readable reasons, e.g. "Home (error 1409: hotkey already registered)".</summary>
    public IReadOnlyList<string> FailureDetails => _failureDetails;

    public void Start()
    {
        _source.AddHook(WndProc);

        foreach (Binding b in Bindings)
        {
            if (RegisterHotKey(_hwnd, b.Id, b.Modifiers, b.Key))
            {
                _registered[b.Id] = b.Action;
            }
            else
            {
                // RegisterHotKey fails when another app owns the key (error 1409).
                int error = Marshal.GetLastWin32Error();
                _unregistered.Add(b);
                _failureDetails.Add($"{b.Label} (error {error}{(error == 1409 ? ": already registered by another app or another copy of this one" : "")})");
                Log.Warning("Could not register {Key} (Win32 error {Error})", b.Label, error);
            }
        }

        InstallKeyboardHook();

        // Keys that RegisterHotKey refused are still caught by the low-level hook, if it is up.
        foreach (Binding b in _unregistered)
            (_hookHandle != IntPtr.Zero ? _fallbackKeys : _failedKeys).Add(b.Label);
    }

    private static bool IsDown(int vk) => (GetAsyncKeyState(vk) & 0x8000) != 0;

    /// <summary>Does the current modifier state match what the binding expects?</summary>
    private static bool ModifiersMatch(uint required)
    {
        bool ctrl = IsDown(VK_CONTROL), alt = IsDown(VK_MENU), shift = IsDown(VK_SHIFT),
             win = IsDown(VK_LWIN) || IsDown(VK_RWIN);

        if (alt || shift || win) return false;
        return required == MOD_CTRL ? ctrl : !ctrl;
    }

    private void InstallKeyboardHook()
    {
        try
        {
            _hookProc = LowLevelKeyboardCallback;
            using var proc = System.Diagnostics.Process.GetCurrentProcess();
            using var mod = proc.MainModule!;
            _hookHandle = SetWindowsHookEx(WH_KEYBOARD_LL, _hookProc, GetModuleHandle(mod.ModuleName), 0);
            if (_hookHandle == IntPtr.Zero)
                Log.Warning("Low-level keyboard hook not installed (Win32 error {Error}); headset volume keys disabled",
                    Marshal.GetLastWin32Error());
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Low-level keyboard hook failed");
        }
    }

    private void Raise(HotkeyAction action)
    {
        Log.Debug("Hotkey {Action}", action);
        // Never run the handler inside the hook / WndProc: queue it so input is not blocked.
        _dispatcher.BeginInvoke(() =>
        {
            try { Triggered?.Invoke(action); }
            catch (Exception ex) { Log.Error(ex, "Hotkey handler for {Action} failed", action); }
        });
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        // Headset volume buttons arrive as WM_APPCOMMAND while the app window is focused.
        if (msg == WM_APPCOMMAND)
        {
            int cmd = (int)((lParam.ToInt64() >> 16) & 0xFFF);
            HotkeyAction? action = cmd switch
            {
                APPCOMMAND_VOLUME_UP => HotkeyAction.CaptureAndSend,
                APPCOMMAND_VOLUME_DOWN => HotkeyAction.CaptureAndSendPushback,
                APPCOMMAND_VOLUME_MUTE => HotkeyAction.CaptureOnly,
                _ => null
            };
            if (action is { } a)
            {
                Raise(a);
                handled = true;
                return (IntPtr)1;
            }
        }

        if (msg == WM_HOTKEY && _registered.TryGetValue(wParam.ToInt32(), out HotkeyAction hotkey))
        {
            Raise(hotkey);
            handled = true;
        }

        return IntPtr.Zero;
    }

    // Called for every key event system-wide: keep it tiny. Intercepts the headset volume keys,
    // maps them to the same actions as Home / End / PageUp and suppresses the volume change.
    private IntPtr LowLevelKeyboardCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 && (wParam == (IntPtr)WM_KEYDOWN || wParam == (IntPtr)WM_SYSKEYDOWN))
        {
            var kb = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
            HotkeyAction? action = kb.vkCode switch
            {
                VK_VOLUME_UP => HotkeyAction.CaptureAndSend,
                VK_VOLUME_DOWN => HotkeyAction.CaptureAndSendPushback,
                VK_VOLUME_MUTE => HotkeyAction.CaptureOnly,
                _ => null
            };
            if (action is { } a)
            {
                Raise(a);
                return (IntPtr)1;
            }

            // Fallback for keys RegisterHotKey could not get.
            foreach (Binding b in _unregistered)
            {
                if (kb.vkCode == b.Key && ModifiersMatch(b.Modifiers))
                {
                    Raise(b.Action);
                    return (IntPtr)1;
                }
            }
        }

        return CallNextHookEx(_hookHandle, nCode, wParam, lParam);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        foreach (int id in _registered.Keys)
            UnregisterHotKey(_hwnd, id);
        _registered.Clear();

        if (_hookHandle != IntPtr.Zero)
        {
            UnhookWindowsHookEx(_hookHandle);
            _hookHandle = IntPtr.Zero;
        }

        _source.RemoveHook(WndProc);
    }
}
