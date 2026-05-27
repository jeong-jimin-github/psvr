using System;
using System.Runtime.InteropServices;

namespace PSVRPlayer.PSVR;

/// <summary>
/// Sends HID Output Reports via Windows native API as a fallback when hidapi's
/// hid_write fails on the PSVR control interface (IF5).
///
/// Critically, Windows requires the output buffer to be EXACTLY the device's
/// declared OutputReportByteLength (HIDP_CAPS). Sending a short buffer makes
/// HidD_SetOutputReport fail with ERROR_INVALID_FUNCTION (WIN32 1). We query the
/// caps and pad accordingly, then try HidD_SetOutputReport and WriteFile in turn.
/// </summary>
internal static class WinHid
{
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateFile(string path, uint access, uint share,
        IntPtr security, uint creation, uint flags, IntPtr template);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool WriteFile(IntPtr handle, byte[] buffer, uint count,
        out uint written, IntPtr overlapped);

    [DllImport("hid.dll", SetLastError = true)]
    private static extern bool HidD_SetOutputReport(IntPtr device, byte[] buffer, uint length);

    [DllImport("hid.dll", SetLastError = true)]
    private static extern bool HidD_GetPreparsedData(IntPtr device, out IntPtr preparsed);

    [DllImport("hid.dll", SetLastError = true)]
    private static extern bool HidD_FreePreparsedData(IntPtr preparsed);

    [DllImport("hid.dll", SetLastError = true)]
    private static extern int HidP_GetCaps(IntPtr preparsed, IntPtr caps);

    private static readonly IntPtr Invalid = new(-1);
    private const uint GenericRW    = 0xC0000000u; // GENERIC_READ | GENERIC_WRITE
    private const uint ShareRW      = 0x00000003u; // FILE_SHARE_READ | FILE_SHARE_WRITE
    private const uint OpenExisting = 3u;
    private const int  HidpStatusSuccess = unchecked((int)0x00110000);

    /// <summary>
    /// Opens <paramref name="path"/>, pads <paramref name="report"/> to the device's
    /// OutputReportByteLength, sends it, then closes the handle.
    /// Returns (true, null) on success or (false, "WIN32 error description").
    /// </summary>
    public static (bool ok, string? error) SendOutputReport(string path, byte[] report)
    {
        var h = CreateFile(path, GenericRW, ShareRW, IntPtr.Zero, OpenExisting, 0, IntPtr.Zero);
        if (h == Invalid)
        {
            int e = Marshal.GetLastWin32Error();
            return (false, $"CreateFile {ErrorHint(e)}");
        }

        try
        {
            int outLen = GetOutputReportByteLength(h);
            byte[] buf = report;
            if (outLen > 0 && outLen != report.Length)
            {
                buf = new byte[outLen];
                Array.Copy(report, buf, Math.Min(report.Length, outLen));
            }

            // Primary: HidD_SetOutputReport (uses IOCTL_HID_SET_OUTPUT_REPORT)
            if (HidD_SetOutputReport(h, buf, (uint)buf.Length))
                return (true, null);
            int setErr = Marshal.GetLastWin32Error();

            // Fallback: WriteFile (uses the interrupt OUT pipe)
            if (WriteFile(h, buf, (uint)buf.Length, out _, IntPtr.Zero))
                return (true, null);
            int writeErr = Marshal.GetLastWin32Error();

            return (false,
                $"SetOutputReport {ErrorHint(setErr)} / WriteFile {ErrorHint(writeErr)} " +
                $"(outLen={outLen}, sent={buf.Length})");
        }
        finally { CloseHandle(h); }
    }

    /// <summary>Returns the device's OutputReportByteLength (incl. report-ID byte), or 0 on failure.</summary>
    private static int GetOutputReportByteLength(IntPtr device)
    {
        if (!HidD_GetPreparsedData(device, out var preparsed) || preparsed == IntPtr.Zero)
            return 0;

        // HIDP_CAPS is ~68 bytes; allocate generously. OutputReportByteLength is a
        // USHORT at offset 6 (Usage, UsagePage, InputReportByteLength precede it).
        IntPtr caps = Marshal.AllocHGlobal(256);
        try
        {
            if (HidP_GetCaps(preparsed, caps) != HidpStatusSuccess)
                return 0;
            return (ushort)Marshal.ReadInt16(caps, 6);
        }
        finally
        {
            Marshal.FreeHGlobal(caps);
            HidD_FreePreparsedData(preparsed);
        }
    }

    private static string ErrorHint(int code) => code switch
    {
        0  => "OK",
        1  => $"WIN32 1 INVALID_FUNCTION — レポート長/フォーマット不一致の可能性",
        5  => $"WIN32 5 ACCESS_DENIED — 別プロセス(Sony/PlayStationサービス等)がIF5を占有中の可能性",
        32 => $"WIN32 32 SHARING_VIOLATION — 別プロセスが排他オープン中",
        _  => $"WIN32 {code} (0x{code:X4})"
    };
}
