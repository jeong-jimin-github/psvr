using System;
using System.Runtime.InteropServices;

namespace PSVRPlayer.PSVR;

/// <summary>
/// Sends HID Output Reports via Windows native API as a fallback when hidapi
/// opens the control interface read-only (silently, due to its own fallback logic).
///
/// Uses synchronous CreateFile (no FILE_FLAG_OVERLAPPED) + HidD_SetOutputReport,
/// which takes a different kernel path than hidapi's overlapped WriteFile.
/// </summary>
internal static class WinHid
{
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateFile(string path, uint access, uint share,
        IntPtr security, uint creation, uint flags, IntPtr template);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("hid.dll", SetLastError = true)]
    private static extern bool HidD_SetOutputReport(IntPtr device, byte[] buffer, uint length);

    private static readonly IntPtr Invalid = new(-1);
    private const uint GenericRW   = 0xC0000000u; // GENERIC_READ | GENERIC_WRITE
    private const uint ShareRW     = 0x00000003u; // FILE_SHARE_READ | FILE_SHARE_WRITE
    private const uint OpenExisting = 3u;

    /// <summary>
    /// Opens <paramref name="path"/>, sends the output report, then closes.
    /// Returns (true, null) on success or (false, "description of WIN32 error").
    /// </summary>
    public static (bool ok, string? error) SendOutputReport(string path, byte[] report)
    {
        var h = CreateFile(path, GenericRW, ShareRW, IntPtr.Zero, OpenExisting, 0, IntPtr.Zero);
        if (h == Invalid)
        {
            int e = Marshal.GetLastWin32Error();
            return (false, ErrorHint(e));
        }

        try
        {
            if (HidD_SetOutputReport(h, report, (uint)report.Length))
                return (true, null);
            int e = Marshal.GetLastWin32Error();
            return (false, $"HidD_SetOutputReport WIN32 {e} (0x{e:X4}): {ErrorHint(e)}");
        }
        finally { CloseHandle(h); }
    }

    private static string ErrorHint(int code) => code switch
    {
        5  => $"WIN32 {code} ACCESS_DENIED — PlayStationサービスがIF5を占有している可能性あり。タスクマネージャーでPlayStation/Sonyサービスを終了後に再試行してください。",
        32 => $"WIN32 {code} SHARING_VIOLATION — 別プロセスが排他オープン中です。",
        _  => $"WIN32 {code} (0x{code:X4})"
    };
}
