using System.Runtime.InteropServices;
using System.Text;
using SharpHook;
using SharpHook.Data;

namespace PrimeDictate;

internal sealed record ForegroundInputTarget(IntPtr WindowHandle, uint ProcessId, string? Title)
{
    public string DisplayName =>
        string.IsNullOrWhiteSpace(this.Title)
            ? $"window 0x{this.WindowHandle.ToInt64():X}"
            : $"{this.Title} (0x{this.WindowHandle.ToInt64():X})";

    public bool IsStillForeground()
    {
        var current = Capture();
        return current is not null &&
            current.WindowHandle == this.WindowHandle &&
            current.ProcessId == this.ProcessId;
    }

    public static ForegroundInputTarget? Capture()
    {
        var handle = NativeMethods.GetForegroundWindow();
        if (handle == IntPtr.Zero)
        {
            return null;
        }

        _ = NativeMethods.GetWindowThreadProcessId(handle, out var processId);
        return new ForegroundInputTarget(handle, processId, GetWindowTitle(handle));
    }

    private static string? GetWindowTitle(IntPtr handle)
    {
        var length = NativeMethods.GetWindowTextLength(handle);
        if (length <= 0)
        {
            return null;
        }

        var title = new StringBuilder(length + 1);
        return NativeMethods.GetWindowText(handle, title, title.Capacity) > 0
            ? title.ToString()
            : null;
    }
}

internal static class WindowsMousePointerIndicator
{
    private const uint SpiGetMouseSonar = 0x101C;

    private static readonly EventSimulator EventSimulator = new();

    public static void PulseIfMouseSonarEnabled()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var enabled = false;
        if (!NativeMethods.SystemParametersInfo(SpiGetMouseSonar, 0, ref enabled, 0) || !enabled)
        {
            return;
        }

        _ = EventSimulator.SimulateKeyStroke(new[] { KeyCode.VcLeftControl });
    }
}

internal static partial class NativeMethods
{
    internal const int GwlExStyle = -20;
    internal const int WsExTransparent = 0x00000020;
    internal const int WsExToolWindow = 0x00000080;
    internal const int WsExNoActivate = 0x08000000;

    [DllImport("user32.dll")]
    internal static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern int GetWindowText(IntPtr hWnd, StringBuilder text, int count);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern int GetWindowTextLength(IntPtr hWnd);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SystemParametersInfo(uint uiAction, uint uiParam, ref bool pvParam, uint fWinIni);

    internal static IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex) =>
        IntPtr.Size == 8
            ? GetWindowLongPtr64(hWnd, nIndex)
            : new IntPtr(GetWindowLong32(hWnd, nIndex));

    internal static IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong) =>
        IntPtr.Size == 8
            ? SetWindowLongPtr64(hWnd, nIndex, dwNewLong)
            : new IntPtr(SetWindowLong32(hWnd, nIndex, dwNewLong.ToInt32()));

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
    private static extern IntPtr GetWindowLongPtr64(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW", SetLastError = true)]
    private static extern int GetWindowLong32(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    private static extern IntPtr SetWindowLongPtr64(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongW", SetLastError = true)]
    private static extern int SetWindowLong32(IntPtr hWnd, int nIndex, int dwNewLong);
}
