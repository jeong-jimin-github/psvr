using System;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace PSVRPlayer.Handy;

/// <summary>
/// The Handy API v2 client.
/// HSSP (server-sync) mode: upload funscript → sync time → play/stop with timestamp.
/// </summary>
public sealed class HandyClient : IDisposable
{
    private const string Base   = "https://www.handyfeeling.com/api/handy/v2";
    private const string Upload = "https://www.handyfeeling.com/api/script/v0/upload";

    private readonly HttpClient _http = new();
    private string _key = string.Empty;

    // HSSP mode value = 4 (PSVRFramework / Handy API v2)
    private const int ModeHSSP = 4;

    public string? ScriptUrl     { get; private set; }
    public double  TimeOffsetMs  { get; private set; }  // local+offset = server time
    public bool    IsReady       { get; private set; }

    public void SetKey(string key) => _key = key.Trim();

    private HttpRequestMessage Req(HttpMethod m, string path)
    {
        var r = new HttpRequestMessage(m, Base + path);
        r.Headers.Add("X-Connection-Key", _key);
        r.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        return r;
    }

    // ── Connection ────────────────────────────────────────────────────────

    public async Task<bool> CheckConnectionAsync()
    {
        using var res = await _http.SendAsync(Req(HttpMethod.Get, "/connected"));
        res.EnsureSuccessStatusCode();
        using var doc = await JsonDocument.ParseAsync(await res.Content.ReadAsStreamAsync());
        return doc.RootElement.GetProperty("connected").GetBoolean();
    }

    // ── Time sync (Handy server-clock alignment) ──────────────────────────

    public async Task SyncTimeAsync(int iterations = 30, Action<int, int>? progress = null)
    {
        var offsets = new double[iterations];
        int valid   = 0;

        for (int i = 0; i < iterations; i++)
        {
            long t0 = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            try
            {
                using var res = await _http.SendAsync(Req(HttpMethod.Get, "/servertime"));
                long t1 = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                using var doc = await JsonDocument.ParseAsync(await res.Content.ReadAsStreamAsync());
                long sv = doc.RootElement.GetProperty("serverTime").GetInt64();
                offsets[valid++] = sv - t1 + (t1 - t0) / 2.0;
            }
            catch { /* skip */ }
            progress?.Invoke(i + 1, iterations);
        }

        if (valid < 5) throw new Exception("タイムシンク失敗 (5回未満成功)");

        Array.Sort(offsets, 0, valid);
        int trim = Math.Max(1, valid / 10);
        double sum = 0;
        for (int i = trim; i < valid - trim; i++) sum += offsets[i];
        TimeOffsetMs = sum / (valid - 2 * trim);
    }

    private long ServerNow() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + (long)TimeOffsetMs;

    // ── Script upload ─────────────────────────────────────────────────────

    public async Task<string> UploadScriptAsync(FunscriptPlayer funscript)
    {
        var json = JsonSerializer.Serialize(new { version = "1.0", actions = funscript.Actions });
        using var content = new MultipartFormDataContent();
        content.Add(new ByteArrayContent(Encoding.UTF8.GetBytes(json))
        {
            Headers = { ContentType = new MediaTypeHeaderValue("application/json") }
        }, "file", "script.funscript");

        using var res = await _http.PostAsync(Upload, content);
        res.EnsureSuccessStatusCode();
        using var doc = await JsonDocument.ParseAsync(await res.Content.ReadAsStreamAsync());
        ScriptUrl = doc.RootElement.GetProperty("url").GetString()
            ?? throw new Exception("アップロードレスポンスにURLがありません");
        return ScriptUrl;
    }

    // ── HSSP setup ────────────────────────────────────────────────────────

    public async Task SetupHSSPAsync(string? url = null)
    {
        url ??= ScriptUrl ?? throw new InvalidOperationException("先にアップロードしてください");

        // Set mode to HSSP
        var modeReq = Req(HttpMethod.Put, "/mode");
        modeReq.Content = new StringContent(JsonSerializer.Serialize(new { mode = ModeHSSP }),
            Encoding.UTF8, "application/json");
        (await _http.SendAsync(modeReq)).EnsureSuccessStatusCode();

        // Configure script URL + timeout
        var setupReq = Req(HttpMethod.Put, "/hssp/setup");
        setupReq.Content = new StringContent(JsonSerializer.Serialize(new { url, timeout = 10000 }),
            Encoding.UTF8, "application/json");
        (await _http.SendAsync(setupReq)).EnsureSuccessStatusCode();

        IsReady = true;
    }

    // ── Playback control ──────────────────────────────────────────────────

    public async Task PlayAsync(double videoTimeSec)
    {
        if (!IsReady) return;
        const int estimatedDelay = 150; // ms latency between API call and device acting
        long startAt = ServerNow() + estimatedDelay - (long)(videoTimeSec * 1000);

        var req = Req(HttpMethod.Put, "/hssp/play");
        req.Content = new StringContent(
            JsonSerializer.Serialize(new { serverTime = startAt, playbackRate = 1.0 }),
            Encoding.UTF8, "application/json");
        await _http.SendAsync(req);
    }

    public async Task StopAsync()
    {
        if (!IsReady) return;
        await _http.SendAsync(Req(HttpMethod.Put, "/hssp/stop"));
    }

    public async Task SeekAsync(double videoTimeSec, bool isPlaying)
    {
        await StopAsync();
        if (isPlaying) await PlayAsync(videoTimeSec);
    }

    public void Dispose() => _http.Dispose();
}
