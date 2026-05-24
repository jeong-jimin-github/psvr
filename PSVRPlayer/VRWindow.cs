using System;
using System.Numerics;
using System.Threading;
using OpenTK.Windowing.Common;
using OpenTK.Windowing.Desktop;
using OpenTK.Windowing.GraphicsLibraryFramework;
using OpenTK.Graphics.OpenGL4;
using PSVRPlayer.Rendering;
using PSVRPlayer.Video;

namespace PSVRPlayer;

/// <summary>
/// OpenTK native window that renders VR video on the PSVR display.
/// Runs on a dedicated render thread.  Matches the engine.cpp / vrdevice.cpp role
/// from PSVRFramework's VRVideoPlayer.
///
/// Display handling:
///   - Enumerates monitors via GLFW and finds the PSVR (1920×1080 non-primary, or user-chosen).
///   - Opens a borderless fullscreen window on that monitor.
///   - SphereRenderer handles the two-pass render + barrel distortion.
/// </summary>
public sealed class VRWindow : IDisposable
{
    private NativeWindow?   _window;
    private SphereRenderer  _renderer = new();
    private VideoPlayer?    _video;
    private Func<Quaternion>? _getOrientation;

    private int _monitorIndex = -1; // -1 = auto-select non-primary
    private bool _running;
    private Thread? _thread;

    public bool IsOpen => _running;

    public event Action<string>? Log;

    // ── Public API (called from WPF thread) ──────────────────────────────

    public void SetVideoPlayer(VideoPlayer video) => _video = video;
    public void SetOrientationSource(Func<Quaternion> fn) => _getOrientation = fn;
    public void SetFormat(string fmt) { PreferredFormat = fmt; }
    public int  PreferredMonitorIndex { get; set; } = -1;

    public void Open()
    {
        if (_running) return;
        _running = true;
        _thread = new Thread(RenderLoop) { IsBackground = true, Name = "VRRender" };
        _thread.Start();
    }

    public void Close()
    {
        _running = false;
        _thread?.Join(2000);
    }

    // ── Render thread ─────────────────────────────────────────────────────

    private void RenderLoop()
    {
        try
        {
            var (monitor, x, y, w, h) = PickMonitor();
            Log?.Invoke($"VRウィンドウ → モニター [{monitor}] {w}×{h} @ ({x},{y})");

            var nws = new NativeWindowSettings
            {
                Title            = "PSVR VR View",
                ClientSize       = new OpenTK.Mathematics.Vector2i(w, h),
                Location         = new OpenTK.Mathematics.Vector2i(x, y),
                WindowBorder     = WindowBorder.Hidden,
                Flags            = ContextFlags.ForwardCompatible,
                APIVersion       = new Version(3, 3),
                NumberOfSamples  = 0,
            };

            _window = new NativeWindow(nws);
            _window.Context.MakeCurrent();

            GL.Enable(EnableCap.DepthTest);
            GL.ClearColor(0f, 0f, 0f, 1f);

            _renderer.Init(w / 2, h);
            string currentFmt = _renderer.Format;

            Log?.Invoke("OpenGL " + GL.GetString(StringName.Version));

            while (_running && !_window.IsExiting)
            {
                _window.ProcessEvents(0); // non-blocking

                // Check for format change
                if (PreferredFormat != currentFmt)
                {
                    _renderer.SetFormat(PreferredFormat);
                    currentFmt = PreferredFormat;
                }

                // Upload latest video frame if available
                if (_video is not null && _video.TryGetFrame(out var frameData, out int fw, out int fh))
                    _renderer.UploadVideoFrame(frameData, fw, fh);

                var orientation = _getOrientation?.Invoke() ?? Quaternion.Identity;
                _renderer.Render(orientation, w, h);

                _window.Context.SwapBuffers();
            }
        }
        catch (Exception ex)
        {
            Log?.Invoke($"VRウィンドウエラー: {ex.Message}");
        }
        finally
        {
            _renderer.Dispose();
            _window?.Dispose();
            _running = false;
        }
    }

    public string PreferredFormat { get; set; } = "mono360";

    // ── Monitor selection ─────────────────────────────────────────────────

    private unsafe (int idx, int x, int y, int w, int h) PickMonitor()
    {
        GLFW.Init();
        var monitors = GLFW.GetMonitors(out int count);

        // Use user-specified index if valid
        int chosen = PreferredMonitorIndex >= 0 && PreferredMonitorIndex < count
            ? PreferredMonitorIndex
            : -1;

        // Auto-select: prefer a 1920×1080 non-primary monitor (likely PSVR)
        if (chosen < 0)
        {
            for (int i = 0; i < count; i++)
            {
                var mode = GLFW.GetVideoMode(monitors[i]);
                GLFW.GetMonitorPos(monitors[i], out int mx, out int my);
                // Non-primary monitor at resolution PSVR uses
                if (i != 0 && mode->Width == 1920 && mode->Height == 1080)
                { chosen = i; break; }
            }
        }

        // Fall back to last monitor, then primary
        if (chosen < 0) chosen = count > 1 ? count - 1 : 0;

        {
            var mode = GLFW.GetVideoMode(monitors[chosen]);
            GLFW.GetMonitorPos(monitors[chosen], out int mx, out int my);
            return (chosen, mx, my, mode->Width, mode->Height);
        }
    }

    public void Dispose() => Close();
}
