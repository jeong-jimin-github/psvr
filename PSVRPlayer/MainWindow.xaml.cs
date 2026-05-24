using System;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using PSVRPlayer.Handy;
using PSVRPlayer.PSVR;
using PSVRPlayer.Video;

namespace PSVRPlayer;

public partial class MainWindow : Window
{
    private readonly PSVRController  _psvr      = new();
    private readonly VideoPlayer     _video     = new();
    private readonly HandyClient     _handy     = new();
    private readonly FunscriptPlayer _funscript = new();
    private readonly VRWindow        _vrWindow  = new();

    private readonly DispatcherTimer _timer     = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private bool _seeking;
    private bool _keyVisible;

    private static readonly string ConfigPath =
        Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "psvr_config.json");

    public MainWindow()
    {
        InitializeComponent();
        WireEvents();
        _timer.Tick += Timer_Tick;

        // Initialize LibVLC (must be done before Load)
        _video.Init();

        // Restore saved Handy key from simple JSON config
        try
        {
            if (File.Exists(ConfigPath))
            {
                var cfg = System.Text.Json.JsonDocument.Parse(File.ReadAllText(ConfigPath));
                if (cfg.RootElement.TryGetProperty("handyKey", out var kv))
                    HandyKeyBox.Password = kv.GetString() ?? "";
            }
        }
        catch { /* first run */ }
    }

    // ── PSVR ──────────────────────────────────────────────────────────────

    private void PSVRConnect_Click(object s, RoutedEventArgs e)
    {
        bool ok = _psvr.Connect();
        SetPSVRUI(ok ? "connected" : "error");
        if (ok) { EnterVRBtn.IsEnabled = true; PSVRDisconnectBtn.IsEnabled = true; PSVRConnectBtn.IsEnabled = false; }
    }

    private void PSVRDisconnect_Click(object s, RoutedEventArgs e)
    {
        _psvr.Disconnect();
        SetPSVRUI("disconnected");
        EnterVRBtn.IsEnabled = false; ExitVRBtn.IsEnabled = false; RecenterBtn.IsEnabled = false;
        PSVRDisconnectBtn.IsEnabled = false; PSVRConnectBtn.IsEnabled = true;
    }

    private void EnterVR_Click(object s, RoutedEventArgs e)
    {
        if (!int.TryParse(MonitorIndexBox.Text, out int mi)) mi = -1;
        _vrWindow.PreferredMonitorIndex = mi;

        _psvr.EnterVRMode();
        ExitVRBtn.IsEnabled = true; RecenterBtn.IsEnabled = true;
    }

    private void ExitVR_Click(object s, RoutedEventArgs e) { _psvr.ExitVRMode(); ExitVRBtn.IsEnabled = false; }
    private void Recenter_Click(object s, RoutedEventArgs e) => _psvr.Recenter();

    private void SetPSVRUI(string state)
    {
        PSVRDot.Fill    = state == "connected" ? (Brush)FindResource("Green") : state == "error" ? (Brush)FindResource("Red") : (Brush)FindResource("TextDim");
        PSVRStatus.Text = state == "connected" ? "接続済み" : state == "error" ? "エラー" : "未接続";
        PSVRStatus.Foreground = state == "connected" ? (Brush)FindResource("Green") : (Brush)FindResource("TextDim");
    }

    // ── Video ──────────────────────────────────────────────────────────────

    private void VideoBrowse_Click(object s, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog { Filter = "動画|*.mp4;*.mkv;*.webm;*.avi;*.mov;*.m4v|すべて|*.*" };
        if (dlg.ShowDialog() != true) return;

        _video.Load(dlg.FileName);
        VideoPathBox.Text = Path.GetFileName(dlg.FileName);

        // Auto-detect format from filename
        var name = dlg.FileName.ToLowerInvariant();
        if      (name.Contains("_sbs") || name.Contains("-sbs")) FormatBox.SelectedIndex = name.Contains("180") ? 3 : 1;
        else if (name.Contains("_tb")  || name.Contains("-tb"))  FormatBox.SelectedIndex = 2;
        else if (name.Contains("180"))                            FormatBox.SelectedIndex = 3;

        PlayBtn.IsEnabled = PauseBtn.IsEnabled = SeekSlider.IsEnabled = true;
        OpenVRBtn.IsEnabled = true;
        _timer.Start();
    }

    private void Format_Changed(object s, SelectionChangedEventArgs e)
    {
        if (FormatBox.SelectedItem is ComboBoxItem item)
        {
            string fmt = item.Tag?.ToString() ?? "mono360";
            _vrWindow.PreferredFormat = fmt;
        }
    }

    // ── VR window ─────────────────────────────────────────────────────────

    private void OpenVRWindow_Click(object s, RoutedEventArgs e)
    {
        _vrWindow.SetVideoPlayer(_video);
        _vrWindow.SetOrientationSource(() => _psvr.IsConnected ? _psvr.HeadOrientation : System.Numerics.Quaternion.Identity);

        if (FormatBox.SelectedItem is ComboBoxItem item)
            _vrWindow.PreferredFormat = item.Tag?.ToString() ?? "mono360";

        if (!int.TryParse(MonitorIndexBox.Text, out int mi)) mi = -1;
        _vrWindow.PreferredMonitorIndex = mi;

        _vrWindow.Open();
        OpenVRBtn.IsEnabled = false;
        CloseVRBtn.IsEnabled = true;
        Log("VRウィンドウを開きました");
    }

    private void CloseVRWindow_Click(object s, RoutedEventArgs e)
    {
        _vrWindow.Close();
        OpenVRBtn.IsEnabled = true;
        CloseVRBtn.IsEnabled = false;
    }

    // ── Playback ──────────────────────────────────────────────────────────

    private void Play_Click(object s, RoutedEventArgs e)
    {
        _video.Play();
        _ = _handy.PlayAsync(_video.CurrentTime);
    }

    private void Pause_Click(object s, RoutedEventArgs e)
    {
        _video.Pause();
        _ = _handy.StopAsync();
    }

    private void Seek_MouseDown(object s, System.Windows.Input.MouseButtonEventArgs e) => _seeking = true;
    private void Seek_MouseUp(object s, System.Windows.Input.MouseButtonEventArgs e)
    {
        _seeking = false;
        double t = (SeekSlider.Value / 1000.0) * _video.Duration;
        _video.CurrentTime = t;
        _ = _handy.SeekAsync(t, _video.IsPlaying);
    }
    private void Seek_ValueChanged(object s, RoutedPropertyChangedEventArgs<double> e) { /* display updated in timer */ }

    private void Volume_Changed(object s, RoutedPropertyChangedEventArgs<double> e) =>
        _video.SetVolume((int)VolumeSlider.Value);

    private void Timer_Tick(object? s, EventArgs e)
    {
        if (_seeking || !_video.HasVideo) return;
        double cur = _video.CurrentTime, dur = _video.Duration;
        if (!_seeking && dur > 0)
            SeekSlider.Value = (cur / dur) * 1000;
        TimeLabel.Text = $"{Fmt(cur)} / {Fmt(dur)}";
    }

    private static string Fmt(double sec)
    {
        if (!double.IsFinite(sec)) return "0:00";
        int m = (int)(sec / 60), s = (int)(sec % 60);
        return $"{m}:{s:D2}";
    }

    // ── The Handy ──────────────────────────────────────────────────────────

    private void ToggleKey_Click(object s, RoutedEventArgs e)
    {
        // We'd need a separate TextBox to show the key; toggle a flag for now
        _keyVisible = !_keyVisible;
    }

    private async void HandyConnect_Click(object s, RoutedEventArgs e)
    {
        string key = HandyKeyBox.Password.Trim();
        if (string.IsNullOrEmpty(key)) { MessageBox.Show("接続キーを入力してください"); return; }

        HandyConnectBtn.IsEnabled = false;
        SetHandyUI("syncing", "接続確認中...", "");
        _handy.SetKey(key);

        try
        {
            File.WriteAllText(ConfigPath, $"{{\"handyKey\":\"{key}\"}}");
        }
        catch { /* ignore save errors */ }

        try
        {
            bool connected = await _handy.CheckConnectionAsync();
            if (!connected) { SetHandyUI("error", "デバイスがオフライン", ""); return; }

            SetHandyUI("syncing", "タイムシンク中...", "");
            await Task.Run(() => _handy.SyncTimeAsync(30, (done, total) =>
                Dispatcher.InvokeAsync(() => HandySyncLabel.Text = $"タイムシンク ({done}/{total})…")));

            SetHandyUI("connected", "接続済み", $"同期完了 (オフセット {_handy.TimeOffsetMs:F0} ms)");

            if (_funscript.Loaded) HandyUploadBtn.IsEnabled = true;
        }
        catch (Exception ex)
        {
            SetHandyUI("error", $"エラー: {ex.Message}", "");
        }
        finally
        {
            HandyConnectBtn.IsEnabled = true;
        }
    }

    private void SetHandyUI(string state, string status, string sync)
    {
        var green   = (Brush)FindResource("Green");
        var red     = (Brush)FindResource("Red");
        var yellow  = (Brush)FindResource("Yellow");
        var textDim = (Brush)FindResource("TextDim");

        HandyDot.Fill = state switch {
            "connected" => green, "syncing" => yellow, "error" => red, _ => textDim
        };
        HandyStatus.Text       = status;
        HandyStatus.Foreground = state == "connected" ? green : state == "error" ? red : textDim;
        HandySyncLabel.Text    = sync;
    }

    // ── Funscript ──────────────────────────────────────────────────────────

    private void ScriptBrowse_Click(object s, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog { Filter = "Funscript|*.funscript;*.json" };
        if (dlg.ShowDialog() != true) return;
        try
        {
            _funscript.Load(File.ReadAllText(dlg.FileName));
            ScriptPathBox.Text = Path.GetFileName(dlg.FileName);
            ScriptInfo.Text    = $"{_funscript.Actions.Count} アクション / {Fmt(_funscript.DurationMs / 1000.0)}";
            Log($"Funscript読み込み: {_funscript.Actions.Count} アクション");
            if (_handy.TimeOffsetMs != 0) HandyUploadBtn.IsEnabled = true;
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Funscript読み込みエラー:\n{ex.Message}");
        }
    }

    private async void HandyUpload_Click(object s, RoutedEventArgs e)
    {
        HandyUploadBtn.IsEnabled = false;
        HandyUploadStatus.Text   = "アップロード中...";

        try
        {
            string url = await _handy.UploadScriptAsync(_funscript);
            HandyUploadStatus.Text = "セットアップ中...";
            await _handy.SetupHSSPAsync(url);
            HandyUploadStatus.Text = "準備完了 — 再生でThe Handyが動作します ✓";
            Log("Handy HSSP準備完了");
        }
        catch (Exception ex)
        {
            HandyUploadStatus.Text = $"失敗: {ex.Message}";
            Log($"Handyエラー: {ex.Message}");
        }
        finally
        {
            HandyUploadBtn.IsEnabled = true;
        }
    }

    // ── Helpers ────────────────────────────────────────────────────────────

    private void WireEvents()
    {
        _psvr.StatusChanged += msg => Dispatcher.InvokeAsync(() => Log(msg));
        _psvr.Disconnected  += ()  => Dispatcher.InvokeAsync(() => { SetPSVRUI("error"); Log("PSVRが切断されました"); });
        _vrWindow.Log       += msg => Dispatcher.InvokeAsync(() => Log(msg));
        _video.PlaybackEnded += () => Dispatcher.InvokeAsync(() => _ = _handy.StopAsync());
    }

    private void Log(string msg)
    {
        LogBox.AppendText($"[{DateTime.Now:HH:mm:ss}] {msg}\n");
        LogBox.ScrollToEnd();
    }

    protected override void OnClosed(EventArgs e)
    {
        _timer.Stop();
        _vrWindow.Close();
        _psvr.Dispose();
        _video.Dispose();
        _handy.Dispose();
        base.OnClosed(e);
    }
}
