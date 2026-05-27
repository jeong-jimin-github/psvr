using System;
using System.Numerics;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace PSVRPlayer.PSVR;

/// <summary>
/// Direct PSVR USB-HID controller using hidapi (same approach as OpenHMD / PSVRFramework).
///
/// USB IDs:  VID=0x054C  PID=0x09AF
///   interface 4 → Sensor HID (reads 64-byte IMU reports)
///   interface 5 → Control HID (writes VR-mode commands)
///
/// Requires hidapi.dll (x64) in the application folder.
/// Download: https://github.com/libusb/hidapi/releases → hidapi-win.zip → x64/hidapi.dll
///
/// Sensor byte layout (64-byte Report 0x01, from PSVRFramework/BMI055Integrator):
///   [0]      Report ID (0x01)
///   [1]      Status flags
///   [2]      Sequence counter
///   [12-23]  Sample 1: Gyro YPR + Accel XYZ (int16 LE × 6)
///   [24-27]  Timestamp1 (uint32 LE, µs)
///   [28-39]  Sample 2: Gyro YPR + Accel XYZ (int16 LE × 6)
///   [40-43]  Timestamp2 (uint32 LE, µs)
///
/// Scale factors (BMI055 at ±2000 DPS / ±2 G):
///   Gyro  = raw × 0.001065 rad/s
///   Accel = raw × 6.1e-5   g
/// </summary>
public sealed class PSVRController : IDisposable
{
    // ── USB identifiers ────────────────────────────────────────────────────
    private const ushort VendorId  = 0x054C;
    private const ushort ProductId = 0x09AF;

    // IMU scale factors
    private const float GyroScale  = 0.001065f;   // rad/s per LSB
    private const float AccelScale = 6.1035e-5f;  // g per LSB

    // ── HID command payloads (prefix 0x00 report-ID is added in TrySendControl) ──
    // Format (from OpenHMD psvr.c / PSVRFramework): [id, 0x00, 0xAA, payloadLen, ...payload]
    //   byte[2] = 0xAA magic marker (required — headset ignores reports without it)
    //   byte[3] = payload length in bytes
    private static readonly byte[] CmdEnterVR = {
        0x23, 0x00, 0xAA, 0x04, 0x01, 0x00, 0x00, 0x00   // 0x01 = VR mode
    };
    private static readonly byte[] CmdExitVR = {
        0x23, 0x00, 0xAA, 0x04, 0x00, 0x00, 0x00, 0x00   // 0x00 = cinema mode
    };
    private static readonly byte[] CmdEnableTracking = {
        0x11, 0x00, 0xAA, 0x08, 0xFF, 0xFF, 0xFF, 0x00,  // flags 0xFFFFFF00
        0x00, 0x00, 0x00, 0x00
    };
    private static readonly byte[] CmdHeadsetOn = {
        0x17, 0x00, 0xAA, 0x04, 0x01, 0x00, 0x00, 0x00
    };

    // ── State ──────────────────────────────────────────────────────────────
    private IntPtr  _sensorHandle  = IntPtr.Zero;
    private IntPtr  _controlHandle = IntPtr.Zero;
    private string? _controlPath;  // raw path always saved for WinHid fallback

    private readonly MadgwickFilter _filter = new();
    private Quaternion _orientation  = Quaternion.Identity;
    private Quaternion _calibOffset  = Quaternion.Identity;
    private bool _isVRMode;
    private CancellationTokenSource? _cts;

    public bool IsConnected { get; private set; }
    public bool IsVRMode => _isVRMode;

    /// <summary>Orientation relative to the last Recenter() call.</summary>
    public Quaternion HeadOrientation
    {
        get { lock (_filter) return _calibOffset * _orientation; }
    }

    public event Action<string>? StatusChanged;
    public event Action? Disconnected;

    // ── Connect / disconnect ───────────────────────────────────────────────

    public bool Connect()
    {
        try
        {
            HidApi.hid_init();

            var devs = HidApi.hid_enumerate(VendorId, ProductId);
            if (devs == IntPtr.Zero)
            {
                StatusChanged?.Invoke(
                    "PSVR未検出 (VID=054C PID=09AF)\n" +
                    "確認事項:\n" +
                    "  • PSVRプロセッサーボックスが電源ON・USB接続されているか\n" +
                    "  • デバイスマネージャーでSONY PSVR (VID_054C&PID_09AF) が見えるか");
                return false;
            }

            // Walk the hid_device_info linked list; open interface 4 (sensor) and 5 (control)
            var sb = new StringBuilder("PSVR インターフェース検出:\n");
            for (var cur = devs; cur != IntPtr.Zero; cur = HidApi.GetNext(cur))
            {
                int   iface = HidApi.GetInterfaceNumber(cur);
                string? path  = HidApi.GetPath(cur);
                sb.AppendLine($"  IF{iface}: {path}");

                if (iface == 4 && path != null && _sensorHandle == IntPtr.Zero)
                {
                    _sensorHandle = HidApi.hid_open_path(path);
                    if (_sensorHandle == IntPtr.Zero)
                        sb.AppendLine($"  ⚠ IF4 open failed: {HidApi.GetError()}");
                }
                else if (iface == 5 && path != null && _controlHandle == IntPtr.Zero)
                {
                    _controlPath   = path; // always save — used by WinHid fallback
                    _controlHandle = HidApi.hid_open_path(path);
                    if (_controlHandle == IntPtr.Zero)
                        sb.AppendLine($"  ⚠ IF5 open failed: {HidApi.GetError()}");
                    // Note: hidapi may return non-null but read-only handle (silent fallback).
                    // TrySendControl will detect write failure and retry via WinHid.
                }
            }
            HidApi.hid_free_enumeration(devs);

            StatusChanged?.Invoke(sb.ToString().TrimEnd());

            if (_sensorHandle == IntPtr.Zero)
            {
                StatusChanged?.Invoke(
                    "センサーインターフェース(IF4)を開けませんでした。\n" +
                    "管理者権限でアプリを再起動してください。");
                return false;
            }

            IsConnected = true;
            StatusChanged?.Invoke("PSVR接続完了");

            _cts = new CancellationTokenSource();
            _ = Task.Run(() => SensorLoop(_cts.Token));

            return true;
        }
        catch (DllNotFoundException)
        {
            StatusChanged?.Invoke(
                "hidapi.dll が見つかりません。\n" +
                "https://github.com/libusb/hidapi/releases から\n" +
                "hidapi-win.zip をダウンロードし、x64/hidapi.dll を\n" +
                "PSVRPlayer.exe と同じフォルダに置いてください。");
            return false;
        }
        catch (Exception ex)
        {
            StatusChanged?.Invoke($"接続エラー: {ex.Message}");
            return false;
        }
    }

    public void Disconnect()
    {
        _cts?.Cancel();
        if (_isVRMode) TrySendControlSilent(CmdExitVR);

        if (_sensorHandle  != IntPtr.Zero) { HidApi.hid_close(_sensorHandle);  _sensorHandle  = IntPtr.Zero; }
        if (_controlHandle != IntPtr.Zero) { HidApi.hid_close(_controlHandle); _controlHandle = IntPtr.Zero; }

        IsConnected = false;
        _isVRMode   = false;
        StatusChanged?.Invoke("切断");
    }

    // ── VR mode commands ──────────────────────────────────────────────────

    public bool EnterVRMode()
    {
        if (!IsConnected) return false;
        string? ctrlError = null;
        if (TrySendControl(CmdHeadsetOn,   ref ctrlError) &&
            TrySendControl(CmdEnableTracking, ref ctrlError) &&
            TrySendControl(CmdEnterVR,      ref ctrlError))
        {
            _isVRMode = true;
            _filter.Reset();
            StatusChanged?.Invoke("VRモード開始");
            return true;
        }
        StatusChanged?.Invoke(
            "⚠ VRモードコマンド送信不可\n" +
            $"詳細: {ctrlError ?? "不明なエラー"}");
        _isVRMode = true;
        return true;
    }

    public bool ExitVRMode()
    {
        if (!IsConnected) return false;
        string? _ = null;
        TrySendControl(CmdExitVR, ref _);
        _isVRMode = false;
        StatusChanged?.Invoke("シネマモードに戻りました");
        return true;
    }

    /// <summary>Sets current orientation as the "forward" reference for subsequent HeadOrientation reads.</summary>
    public void Recenter()
    {
        lock (_filter)
            _calibOffset = Quaternion.Conjugate(_orientation);
    }

    // ── Sensor reading loop ───────────────────────────────────────────────

    private void SensorLoop(CancellationToken ct)
    {
        var buf = new byte[64];
        while (!ct.IsCancellationRequested)
        {
            try
            {
                // 100 ms timeout lets us check cancellation without busy-spinning
                int read = HidApi.hid_read_timeout(_sensorHandle, buf, (UIntPtr)buf.Length, 100);
                if (read > 0 && buf[0] == 0x01 && read >= 44)
                    ParseSensorReport(buf);
                else if (read < 0)          // device error / disconnect
                {
                    IsConnected = false;
                    Disconnected?.Invoke();
                    return;
                }
                // read == 0: timeout, loop and re-check cancellation
            }
            catch (Exception) when (!ct.IsCancellationRequested)
            {
                IsConnected = false;
                Disconnected?.Invoke();
                return;
            }
        }
    }

    private void ParseSensorReport(byte[] d)
    {
        // Two IMU samples per report
        for (int i = 0; i < 2; i++)
        {
            int off = 12 + i * 16;

            float gYaw   = ToInt16(d, off + 0)  * GyroScale;
            float gPitch = ToInt16(d, off + 2)  * GyroScale;
            float gRoll  = ToInt16(d, off + 4)  * GyroScale;
            float aX     = ToInt16(d, off + 6)  * AccelScale;
            float aY     = ToInt16(d, off + 8)  * AccelScale;
            float aZ     = ToInt16(d, off + 10) * AccelScale;

            // PSVR axis → right-hand OpenGL convention
            lock (_filter)
            {
                _filter.Update(gPitch, gYaw, -gRoll, aX, aY, aZ);
                _orientation = _filter.Orientation;
            }
        }
    }

    // ── Helpers ──────────────────────────────────────────────────────────

    private void TrySendControlSilent(byte[] cmd)
    {
        string? _ = null;
        TrySendControl(cmd, ref _);
    }

    private bool TrySendControl(byte[] cmd, ref string? lastError)
    {
        // HID output report: first byte = report ID (0x00 for non-numbered reports)
        var report = new byte[cmd.Length + 1];
        report[0] = 0x00;
        Buffer.BlockCopy(cmd, 0, report, 1, cmd.Length);

        // Primary: hidapi handle (may be read-only — hid_write returns -1 in that case)
        if (_controlHandle != IntPtr.Zero &&
            HidApi.hid_write(_controlHandle, report, (UIntPtr)report.Length) >= 0)
            return true;

        // Fallback: Windows native HidD_SetOutputReport (synchronous, no overlapped IO).
        // Takes a different kernel path and may succeed where hidapi's WriteFile does not.
        if (_controlPath != null)
        {
            var (ok, err) = WinHid.SendOutputReport(_controlPath, report);
            if (ok) return true;
            lastError = err;
        }
        else
        {
            lastError = "コントロールインターフェースが見つかりません";
        }
        return false;
    }

    private static short ToInt16(byte[] d, int offset) =>
        (short)(d[offset] | (d[offset + 1] << 8));

    public void Dispose() => Disconnect();
}
