using System.Runtime.InteropServices;
using System.Text;

namespace LiveCaptionViewer.Infrastructure;

/// <summary>Win32 P/Invoke surface used by the window shell, hotkeys and transparency code.</summary>
internal static class NativeMethods
{
    // ─── Messages ────────────────────────────────────────────────────────────
    public const int WM_HOTKEY = 0x0312;
    public const int WM_APPCOMMAND = 0x0319;
    public const int WM_KEYDOWN = 0x0100;
    public const int WM_SYSKEYDOWN = 0x0104;
    public const int WH_KEYBOARD_LL = 13;

    public const int APPCOMMAND_VOLUME_MUTE = 8;
    public const int APPCOMMAND_VOLUME_DOWN = 9;
    public const int APPCOMMAND_VOLUME_UP = 10;

    // ─── Hotkey modifiers / virtual keys ─────────────────────────────────────
    public const uint MOD_NONE = 0x0000;
    public const uint MOD_CTRL = 0x0002;

    public const uint VK_PRIOR = 0x21;   // PageUp
    public const uint VK_NEXT = 0x22;    // PageDown
    public const uint VK_END = 0x23;
    public const uint VK_HOME = 0x24;
    public const uint VK_INSERT = 0x2D;
    public const uint VK_DELETE = 0x2E;
    public const uint VK_W = 0x57;
    public const int VK_SHIFT = 0x10;
    public const int VK_CONTROL = 0x11;
    public const int VK_MENU = 0x12;     // Alt
    public const int VK_LWIN = 0x5B;
    public const int VK_RWIN = 0x5C;
    public const uint VK_VOLUME_MUTE = 0xAD;
    public const uint VK_VOLUME_DOWN = 0xAE;
    public const uint VK_VOLUME_UP = 0xAF;

    // ─── Window styles ───────────────────────────────────────────────────────
    public const int GWL_EXSTYLE = -20;
    public const int WS_EX_LAYERED = 0x00080000;
    public const int WS_EX_TRANSPARENT = 0x00000020;
    public const uint LWA_ALPHA = 0x00000002;

    // ─── DWM ─────────────────────────────────────────────────────────────────
    public const int DWMWA_WINDOW_CORNER_PREF = 33;
    public const int DWMWCP_ROUND = 2;

    [StructLayout(LayoutKind.Sequential)]
    public struct KBDLLHOOKSTRUCT
    {
        public uint vkCode;
        public uint scanCode;
        public uint flags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    public delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);
    public delegate bool EnumChildProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll")]
    public static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll")]
    public static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Auto)]
    public static extern IntPtr GetModuleHandle(string? lpModuleName);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll")]
    public static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    [DllImport("dwmapi.dll")]
    public static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

    [DllImport("user32.dll")]
    public static extern bool EnumChildWindows(IntPtr hwndParent, EnumChildProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    public static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

    [DllImport("user32.dll")]
    public static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll")]
    public static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

    [DllImport("user32.dll")]
    public static extern bool SetLayeredWindowAttributes(IntPtr hwnd, uint crKey, byte bAlpha, uint dwFlags);

    [DllImport("user32.dll")]
    public static extern bool IsWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern short GetAsyncKeyState(int vKey);
}
