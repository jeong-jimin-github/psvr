using System;
using System.Runtime.InteropServices;
using LibVLCSharp.Shared;

namespace PSVRPlayer.Video;

/// <summary>
/// Software-decoded video player using LibVLCSharp.
/// Delivers RGBA frames to a pinned buffer which the OpenGL thread uploads
/// as a texture each frame.  Matches the VLC integration in PSVRFramework's VRVideoPlayer.
/// </summary>
public sealed class VideoPlayer : IDisposable
{
    private LibVLC?        _vlc;
    private MediaPlayer?   _mp;
    private Media?         _media;

    private byte[]  _writeBuffer = Array.Empty<byte>();
    private byte[]  _readBuffer  = Array.Empty<byte>();
    private GCHandle _gcHandle;
    private readonly object _frameLock = new();
    private volatile bool _newFrame;

    public int  Width     { get; private set; }
    public int  Height    { get; private set; }
    public bool HasVideo  => _media is not null;
    public bool IsPlaying => _mp?.IsPlaying == true;
    public double Duration => (_mp?.Length ?? 0) / 1000.0;   // seconds
    public double CurrentTime
    {
        get  => (_mp?.Time ?? 0) / 1000.0;
        set  { if (_mp is not null) _mp.Time = (long)(value * 1000); }
    }

    public event Action? PlaybackEnded;

    public void Init()
    {
        Core.Initialize();
        _vlc = new LibVLC("--no-osd", "--no-video-title");
        _mp  = new MediaPlayer(_vlc);
        _mp.EndReached += (_, _) => PlaybackEnded?.Invoke();
    }

    public void Load(string path)
    {
        _media?.Dispose();
        _media = new Media(_vlc!, path);
        _mp!.Media = _media;

        // Hook callbacks before Play so format callback fires
        _mp.SetVideoFormatCallbacks(OnVideoFormat, OnVideoCleanup);
        _mp.SetVideoCallbacks(OnLock, null, OnDisplay);
    }

    public void Play()  => _mp?.Play();
    public void Pause() => _mp?.Pause();
    public void Stop()  => _mp?.Stop();

    public void SetVolume(int percent) { if (_mp is not null) _mp.Volume = percent; }

    // ── Frame access (called from OpenGL render thread) ──────────────────

    /// <summary>
    /// Returns true and copies latest decoded frame into <paramref name="data"/>
    /// if a new frame has arrived since the last call.
    /// </summary>
    public bool TryGetFrame(out byte[] data, out int w, out int h)
    {
        lock (_frameLock)
        {
            w = Width; h = Height;
            data = _readBuffer;
            if (!_newFrame || _readBuffer.Length == 0) return false;
            _newFrame = false;
            return true;
        }
    }

    // ── LibVLC callbacks ─────────────────────────────────────────────────

    private uint OnVideoFormat(ref IntPtr opaque, IntPtr chroma,
        ref uint width, ref uint height, IntPtr pitches, IntPtr lines)
    {
        Width  = (int)width;
        Height = (int)height;

        // RGBA: 4 bytes per pixel
        var chromaBytes = "RGBA"u8.ToArray();
        Marshal.Copy(chromaBytes, 0, chroma, 4);

        int stride = (int)width * 4;
        Marshal.WriteInt32(pitches, stride);
        Marshal.WriteInt32(lines,  (int)height);

        lock (_frameLock)
        {
            if (_gcHandle.IsAllocated) _gcHandle.Free();
            _writeBuffer = new byte[stride * (int)height];
            _readBuffer  = new byte[_writeBuffer.Length];
            _gcHandle = GCHandle.Alloc(_writeBuffer, GCHandleType.Pinned);
        }
        return 1; // 1 plane
    }

    private void OnVideoCleanup(ref IntPtr opaque)
    {
        lock (_frameLock)
            if (_gcHandle.IsAllocated) _gcHandle.Free();
    }

    private IntPtr OnLock(IntPtr opaque, IntPtr planes)
    {
        lock (_frameLock)
            if (_gcHandle.IsAllocated)
                Marshal.WriteIntPtr(planes, _gcHandle.AddrOfPinnedObject());
        return IntPtr.Zero;
    }

    private void OnDisplay(IntPtr opaque, IntPtr picture)
    {
        lock (_frameLock)
        {
            if (_writeBuffer.Length > 0)
            {
                Buffer.BlockCopy(_writeBuffer, 0, _readBuffer, 0, _writeBuffer.Length);
                _newFrame = true;
            }
        }
    }

    // ── Dispose ──────────────────────────────────────────────────────────

    public void Dispose()
    {
        _mp?.Stop();
        _mp?.Dispose();
        _media?.Dispose();
        _vlc?.Dispose();
        lock (_frameLock)
            if (_gcHandle.IsAllocated) _gcHandle.Free();
    }
}
