using System.Runtime.InteropServices;
using System.Text;

namespace MultiKilo.Terminal;

internal static class NativeTerminalClipboard
{
    private const int GwlpUserData = -21;
    private const int WmRightButtonDown = 0x0204;
    private const uint CfUnicodeText = 13;
    private const string TerminalWindowClass = "HwndTerminalClass";

    public static bool IsTerminalWindow(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero)
        {
            return false;
        }

        var className = new StringBuilder(64);
        return GetClassNameW(hwnd, className, className.Capacity) > 0 &&
               string.Equals(className.ToString(), TerminalWindowClass, StringComparison.Ordinal);
    }

    public static bool HasUnicodeText() => IsClipboardFormatAvailable(CfUnicodeText);

    public static bool HasSelection(IntPtr hwnd)
    {
        if (!IsTerminalWindow(hwnd))
        {
            return false;
        }

        var terminal = GetWindowLongPtrW(hwnd, GwlpUserData);
        return terminal != IntPtr.Zero && TerminalIsSelectionActive(terminal);
    }

    public static void InvokeNativeCopyOrPaste(IntPtr hwnd)
    {
        if (!IsTerminalWindow(hwnd))
        {
            return;
        }

        SendMessageW(hwnd, WmRightButtonDown, IntPtr.Zero, IntPtr.Zero);
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassNameW(IntPtr hwnd, StringBuilder className, int maxCount);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtrW(IntPtr hwnd, int index);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SendMessageW(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsClipboardFormatAvailable(uint format);

    [DllImport(
        "Microsoft.Terminal.Control.dll",
        CharSet = CharSet.Unicode,
        CallingConvention = CallingConvention.StdCall,
        PreserveSig = true)]
    [return: MarshalAs(UnmanagedType.I1)]
    private static extern bool TerminalIsSelectionActive(IntPtr terminal);
}
