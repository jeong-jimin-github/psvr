using System;
using System.Linq;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using HidSharp;

namespace PSVRPlayer.PSVR;

/// <summary>
/// Direct PSVR USB-HID controller, equivalent to PSVRFramework's PSVR.cs.
///
/// USB IDs (Sony PSVR):  VID=0x054C  PID=0x09AF
///   mi_04 → Sensor HID (reads 64-byte IMU reports)
///   mi_05 → Control HID (writes VR-mode commands)
///
/// No SteamVR, no WinUSB driver required — Windows HID class driver is enough.
///
/// Sensor byte layout (64-byte Report 0x01, from PSVRFramework/BMI055Integrator):
///   [0]      Report ID (0x01)
///   [1]      Status flags
///   [2]      Sequence counter
///   [3-11]   Button / proximity bytes  (proximity uint16 at [8-9])
///   [12-13]  Gyro Yaw 1   (int16 LE)
///   [14-15]  Gyro Pitch 1 (int16 LE)
///   [16-17]  Gyro Roll 1  (int16 LE)
///   [18-19]  Accel X 1    (int16 LE)
///   [20-21]  Accel Y 1    (int16 LE)
///   [22-23]  Accel Z 1    (int16 LE)
///   [24-27]  Timestamp1   (uint32 LE, µs)
///   [28-29]  Gyro Yaw 2   (int16 LE)
///   [30-31]  Gyro Pitch 2 (int16 LE)
///   [32-33]  Gyro Roll 2  (int16 LE)
///   [34-35]  Accel X 2    (int16 LE)
///   [36-37]  Accel Y 2    (int16 LE)
///   [38-39]  Accel Z 2    (int16 LE)
///   [40-43]  Timestamp2   (uint32 LE, µs)
///
/// Scale factors (BMI055 at ±2000 DPS / ±2 G):
///   Gyro  = raw × 0.001065 rad/s
///   Accel = raw × 6.1e-5   g
/// </summary>
public sealed class PSVRController : IDisposable
{
    // ── USB identifiers ────────────────────────────────────────────────────
    private const int VendorId  = 0x054C;
    private const int ProductId = 0x09AF;

    // IMU scale factors
    private const float GyroScale  = 0.001065f;   // rad/s per LSB (2000 DPS / 32768 * π/180)
    private const float AccelScale = 6.1035e-5f;  // g per LSB (2G / 32768)

    // ── HID command IDs (from PSVRFramework wiki / source) ─────────────────
    // Report 0x11: Enable VR Tracking  (flags 0xFFFFFF00)
    // Report 0x23: Enter/Exit VR Mode  (0x01 = VR, 0x00 = Cinema)
    // Report 0x17: Headset power       (0x01 = on)
    private static readonly byte[] CmdEnterVR = {
        0x23,                                       // Command ID
        0x00, 0x00, 0x00,
        0x01,                                       // 1 = VR mode, 0 = cinema mode
        0x00, 0x00, 0x00
    };
    private static readonly byte[] CmdExitVR = {
        0x23, 0x00, 0x00, 0x00,
        0x00,                                       // 0 = cinema mode
        0x00, 0x00, 0x00
    };
    private static readonly byte[] CmdEnableTracking = {
        0x11, 0x00, 0x00, 0x00,
        0xFF, 0xFF, 0xFF, 0x00                      // flags 0xFFFFFF00 LE
    };
    private static readonly byte[] CmdHeadsetOn = {
        0x17, 0x00, 0x00, 0x00,
        0x01, 0x00, 0x00, 0x00
    };

    // ── State ──────────────────────────────────────────────────────────────
    private HidDevice?  _sensorDevice;
    private HidDevice?  _controlDevice;
    private HidStream?  _sensorStream;
    private HidStream?  _controlStream;

    private readonly MadgwickFilter _filter = new();
    private Quaternion _orientation = Quaternion.Identity;
    private Quaternion _calibOffset = Quaternion.Identity; // set by Recenter()
    private bool _isVRMode;
    private CancellationTokenSource? _cts;

    public bool IsConnected { get; private set; }
    public bool IsVRMode => _isVRMode;

    // Orientation relative to recenter position (Conjugate(savedOrientation) * currentOrientation)
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
            var devices = DeviceList.Local.GetHidDevices(VendorId, ProductId).ToList();
            if (devices.Count == 0)
            {
                StatusChanged?.Invoke("PSVRが見つかりません (VID=054C PID=09AF)");
                return false;
            }

            // Interface 4 = sensor (large input reports, no output)
            // Interface 5 = control (accepts output reports)
            // Identify by device path containing MI_04 / MI_05
            _sensorDevice  = devices.FirstOrDefault(d =>
                d.DevicePath?.IndexOf("MI_04", StringComparison.OrdinalIgnoreCase) >= 0)
                ?? devices.FirstOrDefault(d => d.GetMaxInputReportLength() >= 64);

            _controlDevice = devices.FirstOrDefault(d =>
                d.DevicePath?.IndexOf("MI_05", StringComparison.OrdinalIgnoreCase) >= 0)
                ?? devices.FirstOrDefault(d => d.GetMaxOutputReportLength() >= 8);

            if (_sensorDevice is null)
            {
                StatusChanged?.Invoke("センサーインターフェースが見つかりません");
                return false;
            }

            if (_sensorDevice.TryOpen(out _sensorStream))
            {
                _sensorStream.ReadTimeout  = Timeout.Infinite;
                _sensorStream.WriteTimeout = 1000;
            }
            else
            {
                StatusChanged?.Invoke("センサーストリームを開けません (管理者権限が必要な場合あり)");
                return false;
            }

            if (_controlDevice is not null)
                _controlDevice.TryOpen(out _controlStream);

            IsConnected = true;
            StatusChanged?.Invoke("PSVR接続完了");

            _cts = new CancellationTokenSource();
            _ = Task.Run(() => SensorLoop(_cts.Token));

            return true;
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
        if (_isVRMode) TrySendControl(CmdExitVR);

        _sensorStream?.Dispose();  _sensorStream  = null;
        _controlStream?.Dispose(); _controlStream = null;
        IsConnected = false;
        _isVRMode = false;
        StatusChanged?.Invoke("切断");
    }

    // ── VR mode commands ──────────────────────────────────────────────────

    public bool EnterVRMode()
    {
        if (!IsConnected) return false;
        if (TrySendControl(CmdHeadsetOn) && TrySendControl(CmdEnableTracking) && TrySendControl(CmdEnterVR))
        {
            _isVRMode = true;
            _filter.Reset();
            StatusChanged?.Invoke("VRモード開始");
            return true;
        }
        // Control interface unavailable – video will still render, but no mode switch.
        StatusChanged?.Invoke("⚠ VRモードコマンド未送信 (コントロールインターフェース不可)。PSVRToolboxで手動でVRモードにしてください。");
        _isVRMode = true; // attempt anyway
        return true;
    }

    public bool ExitVRMode()
    {
        if (!IsConnected) return false;
        TrySendControl(CmdExitVR);
        _isVRMode = false;
        StatusChanged?.Invoke("シネマモードに戻りました");
        return true;
    }

    /// <summary>Sets current orientation as the new "forward" reference.</summary>
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
                int read = _sensorStream!.Read(buf, 0, buf.Length);
                if (read >= 44 && buf[0] == 0x01)
                    ParseSensorReport(buf);
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
        // Two IMU samples per report — process both sequentially
        for (int i = 0; i < 2; i++)
        {
            int off = 12 + i * 16; // 12 = start of sample1, each sample = 6×int16 + uint32 = 16 bytes

            float gYaw   = ToInt16(d, off + 0)  * GyroScale;
            float gPitch = ToInt16(d, off + 2)  * GyroScale;
            float gRoll  = ToInt16(d, off + 4)  * GyroScale;
            float aX     = ToInt16(d, off + 6)  * AccelScale;
            float aY     = ToInt16(d, off + 8)  * AccelScale;
            float aZ     = ToInt16(d, off + 10) * AccelScale;

            // PSVR axis convention → OpenGL/right-hand convention
            // X = pitch (nod), Y = yaw (shake head), Z = roll (tilt)
            lock (_filter)
            {
                _filter.Update(gPitch, gYaw, -gRoll, aX, aY, aZ);
                _orientation = _filter.Orientation;
            }
        }
    }

    // ── Helpers ──────────────────────────────────────────────────────────

    private bool TrySendControl(byte[] cmd)
    {
        if (_controlStream is null) return false;
        try
        {
            // HID output report: prefix with 0x00 (report ID for non-numbered reports)
            var report = new byte[cmd.Length + 1];
            report[0] = 0x00;
            Buffer.BlockCopy(cmd, 0, report, 1, cmd.Length);
            _controlStream.Write(report);
            return true;
        }
        catch { return false; }
    }

    private static short ToInt16(byte[] d, int offset) =>
        (short)(d[offset] | (d[offset + 1] << 8));

    public void Dispose() => Disconnect();
}
