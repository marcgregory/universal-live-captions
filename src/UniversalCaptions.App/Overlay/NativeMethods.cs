using System.Runtime.InteropServices;

namespace UniversalCaptions.App.Overlay;

/// <summary>
/// Minimal Win32 surface for the overlay's opt-in click-through mode (ADR-0004): the overlay window
/// is made transparent to mouse input by setting WS_EX_TRANSPARENT on its extended style.
/// </summary>
internal static class NativeMethods
{
    internal const int GWL_EXSTYLE = -20;
    internal const long WS_EX_TRANSPARENT = 0x00000020;
    internal const long WS_EX_LAYERED = 0x00080000;

    internal const uint SWP_NOSIZE = 0x0001;
    internal const uint SWP_NOMOVE = 0x0002;
    internal const uint SWP_NOZORDER = 0x0004;
    internal const uint SWP_FRAMECHANGED = 0x0020;

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW", SetLastError = true)]
    internal static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongW", SetLastError = true)]
    internal static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint flags);
}
