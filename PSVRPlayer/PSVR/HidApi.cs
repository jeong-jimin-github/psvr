using System;
using System.Runtime.InteropServices;

namespace PSVRPlayer.PSVR;

/// <summary>
/// P/Invoke bindings for hidapi.dll (Windows x64).
/// hidapi is the same native library used by OpenHMD and PSVRFramework.
/// Download: https://github.com/libusb/hidapi/releases  → hidapi-win.zip → x64/hidapi.dll
/// </summary>
internal static class HidApi
{
    private const string Dll = "hidapi";

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    public static extern int hid_init();

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    public static extern int hid_exit();

    /// <summary>
    /// Enumerate HID devices. Returns head of a linked list; free with hid_free_enumeration().
    /// Returns IntPtr.Zero if no devices are found or on error.
    /// </summary>
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr hid_enumerate(ushort vendor_id, ushort product_id);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    public static extern void hid_free_enumeration(IntPtr devs);

    /// <summary>Open device by path (returned by hid_enumerate). Returns device handle or IntPtr.Zero on error.</summary>
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi, BestFitMapping = false)]
    public static extern IntPtr hid_open_path(string path);

    /// <summary>
    /// Read an Input report with timeout. Returns bytes read, 0 on timeout, -1 on error.
    /// Buffer must include space for the report ID byte.
    /// </summary>
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    public static extern int hid_read_timeout(IntPtr device, byte[] data, UIntPtr length, int milliseconds);

    /// <summary>
    /// Write an Output report. First byte must be the report ID (0x00 for non-numbered reports).
    /// Returns bytes written (>= 0) or -1 on error.
    /// </summary>
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    public static extern int hid_write(IntPtr device, byte[] data, UIntPtr length);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    public static extern void hid_close(IntPtr device);

    // ── hid_device_info struct field accessors ─────────────────────────────
    // Layout for Windows x64 (verified against hidapi.h + MSVC ABI):
    //   offset  0  : path (char*)           8 bytes
    //   offset  8  : vendor_id (u16)        2 bytes
    //   offset 10  : product_id (u16)       2 bytes
    //   offset 12  : [4 bytes padding]
    //   offset 16  : serial_number (wchar*) 8 bytes
    //   offset 24  : release_number (u16)   2 bytes
    //   offset 26  : [6 bytes padding]
    //   offset 32  : manufacturer_str (wchar*)  8 bytes
    //   offset 40  : product_str (wchar*)        8 bytes
    //   offset 48  : usage_page (u16)       2 bytes
    //   offset 50  : usage (u16)            2 bytes
    //   offset 52  : interface_number (i32) 4 bytes
    //   offset 56  : next (struct*)         8 bytes

    public static string? GetPath(IntPtr dev)
    {
        if (dev == IntPtr.Zero) return null;
        var ptr = Marshal.ReadIntPtr(dev, 0);
        return ptr == IntPtr.Zero ? null : Marshal.PtrToStringAnsi(ptr);
    }

    public static int GetInterfaceNumber(IntPtr dev) =>
        dev == IntPtr.Zero ? -1 : Marshal.ReadInt32(dev, 52);

    public static IntPtr GetNext(IntPtr dev) =>
        dev == IntPtr.Zero ? IntPtr.Zero : Marshal.ReadIntPtr(dev, 56);
}
