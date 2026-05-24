/**
 * The Handy API v2 client.
 * API docs: https://www.handyfeeling.com/api/handy/v2/docs
 *
 * Authentication: X-Connection-Key header
 * HSSP mode (mode=4): upload script → setup → play/stop with server-time sync
 */

const BASE_URL = 'https://www.handyfeeling.com/api/handy/v2';
const SCRIPT_UPLOAD_URL = 'https://www.handyfeeling.com/api/script/v0/upload';

// Mode enum (Handy API v2)
const Mode = {
  HAMP: 0,
  HSSP: 4,
};

export class HandyClient {
  constructor() {
    this.connectionKey = '';
    this.serverTimeOffset = 0; // local + offset = server time
    this.scriptUrl = null;
    this.isConnected = false;
    this.isReady = false; // true after sync + script setup
    this._onStatusChange = null;
  }

  setConnectionKey(key) {
    this.connectionKey = key.trim();
  }

  onStatusChange(cb) {
    this._onStatusChange = cb;
  }

  _emit(status) {
    this._onStatusChange?.(status);
  }

  get _headers() {
    return {
      'X-Connection-Key': this.connectionKey,
      'Accept': 'application/json',
      'Content-Type': 'application/json',
    };
  }

  async checkConnection() {
    if (!this.connectionKey) throw new Error('接続キーが入力されていません');

    const res = await fetch(`${BASE_URL}/connected`, { headers: this._headers });
    if (!res.ok) throw new Error(`HTTP ${res.status}`);
    const data = await res.json();
    this.isConnected = data.connected === true;
    return this.isConnected;
  }

  /**
   * Sync local clock to Handy server clock.
   * Sends N requests, trims outliers, averages the offset.
   */
  async syncTime(iterations = 30, onProgress = null) {
    const offsets = [];

    for (let i = 0; i < iterations; i++) {
      const t0 = Date.now();
      try {
        const res = await fetch(`${BASE_URL}/servertime`, {
          headers: { 'X-Connection-Key': this.connectionKey, Accept: 'application/json' },
        });
        const t1 = Date.now();
        const { serverTime } = await res.json();
        const rtt = t1 - t0;
        // Estimated server time at receipt = serverTime (already the server's current time)
        // Local clock at receipt = t1
        // Offset = serverTime - t1 + rtt/2  (account for one-way latency)
        offsets.push(serverTime - t1 + rtt / 2);
      } catch (_) { /* skip failed requests */ }

      onProgress?.(i + 1, iterations);
    }

    if (offsets.length < 5) throw new Error('タイムシンクに失敗しました');

    offsets.sort((a, b) => a - b);
    const trim = Math.max(1, Math.floor(offsets.length * 0.1));
    const trimmed = offsets.slice(trim, offsets.length - trim);
    this.serverTimeOffset = trimmed.reduce((s, v) => s + v, 0) / trimmed.length;
    return this.serverTimeOffset;
  }

  serverNow() {
    return Date.now() + this.serverTimeOffset;
  }

  async _setMode(mode) {
    const res = await fetch(`${BASE_URL}/mode`, {
      method: 'PUT',
      headers: this._headers,
      body: JSON.stringify({ mode }),
    });
    if (!res.ok) throw new Error(`モード設定失敗 HTTP ${res.status}`);
  }

  /**
   * Upload funscript JSON to Handy's CDN. Returns hosted URL.
   */
  async uploadScript(funscriptJson) {
    const json = typeof funscriptJson === 'string' ? funscriptJson : JSON.stringify(funscriptJson);
    const blob = new Blob([json], { type: 'application/json' });
    const form = new FormData();
    form.append('file', blob, 'script.funscript');

    const res = await fetch(SCRIPT_UPLOAD_URL, { method: 'POST', body: form });
    if (!res.ok) throw new Error(`アップロード失敗 HTTP ${res.status}`);
    const data = await res.json();
    if (!data.url) throw new Error('アップロードレスポンスにURLがありません');
    this.scriptUrl = data.url;
    return data.url;
  }

  /**
   * Configure HSSP with the uploaded script URL.
   */
  async setupHSSP(url = this.scriptUrl) {
    if (!url) throw new Error('スクリプトURLがありません。先にアップロードしてください。');

    await this._setMode(Mode.HSSP);

    const res = await fetch(`${BASE_URL}/hssp/setup`, {
      method: 'PUT',
      headers: this._headers,
      body: JSON.stringify({ url, timeout: 10000 }),
    });
    if (!res.ok) throw new Error(`HSSP設定失敗 HTTP ${res.status}`);
    this.isReady = true;
  }

  /**
   * Start HSSP playback. videoTimeSec is the current video playback position in seconds.
   * The Handy will sync its script to match the video's current time.
   */
  async play(videoTimeSec) {
    if (!this.isReady) return;
    // estimatedDelay: time between sending API request and device acting on it (~150ms)
    const estimatedDelay = 150;
    const serverStart = this.serverNow() + estimatedDelay - videoTimeSec * 1000;

    const res = await fetch(`${BASE_URL}/hssp/play`, {
      method: 'PUT',
      headers: this._headers,
      body: JSON.stringify({ serverTime: Math.round(serverStart), playbackRate: 1.0 }),
    });
    if (!res.ok) throw new Error(`再生失敗 HTTP ${res.status}`);
  }

  async stop() {
    if (!this.isReady) return;
    const res = await fetch(`${BASE_URL}/hssp/stop`, {
      method: 'PUT',
      headers: this._headers,
    });
    if (!res.ok) throw new Error(`停止失敗 HTTP ${res.status}`);
  }

  async seek(videoTimeSec, isPlaying) {
    if (!this.isReady) return;
    await this.stop();
    if (isPlaying) await this.play(videoTimeSec);
  }
}
