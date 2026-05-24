import { VRPlayer } from './VRPlayer.js';
import { HandyClient } from './HandyClient.js';
import { FunscriptPlayer } from './FunscriptPlayer.js';

// ── Instances ────────────────────────────────────────────────────────────────

const player = new VRPlayer(
  document.getElementById('canvas-container'),
  document.getElementById('vr-btn-overlay'),
);
const handy = new HandyClient();
const funscript = new FunscriptPlayer();

// ── DOM refs ─────────────────────────────────────────────────────────────────

const video = document.getElementById('video');
const videoInput = document.getElementById('video-input');
const videoDrop = document.getElementById('video-drop');
const videoFileInfo = document.getElementById('video-file-info');
const videoFilename = document.getElementById('video-filename');
const videoClear = document.getElementById('video-clear');
const formatSelect = document.getElementById('format-select');

const scriptInput = document.getElementById('script-input');
const scriptDrop = document.getElementById('script-drop');
const scriptFileInfo = document.getElementById('script-file-info');
const scriptFilename = document.getElementById('script-filename');
const scriptClear = document.getElementById('script-clear');
const scriptStatus = document.getElementById('script-status');
const scriptStatusDot = document.getElementById('script-status-dot');
const scriptStatusText = document.getElementById('script-status-text');

const handyKeyInput = document.getElementById('handy-key');
const handyToggleKey = document.getElementById('handy-toggle-key');
const handyDot = document.getElementById('handy-dot');
const handyStatus = document.getElementById('handy-status');
const handyConnectBtn = document.getElementById('handy-connect-btn');
const handySyncRow = document.getElementById('handy-sync-row');
const handySyncDot = document.getElementById('handy-sync-dot');
const handySyncStatus = document.getElementById('handy-sync-status');
const handyUploadRow = document.getElementById('handy-upload-row');
const handyUploadBtn = document.getElementById('handy-upload-btn');

const playBtn = document.getElementById('play-btn');
const seekBar = document.getElementById('seek-bar');
const timeCurrent = document.getElementById('time-current');
const timeTotal = document.getElementById('time-total');
const muteBtn = document.getElementById('mute-btn');
const volumeBar = document.getElementById('volume-bar');

const ctrlHandyDot = document.getElementById('ctrl-handy-dot');
const ctrlHandyLabel = document.getElementById('ctrl-handy-label');

// ── Helpers ───────────────────────────────────────────────────────────────────

function fmt(sec) {
  if (!isFinite(sec)) return '0:00';
  const m = Math.floor(sec / 60);
  const s = Math.floor(sec % 60);
  return `${m}:${s.toString().padStart(2, '0')}`;
}

let _seekDragging = false;

function updateSeekBar() {
  if (_seekDragging) return;
  const dur = video.duration;
  if (!isFinite(dur) || dur === 0) return;
  seekBar.value = Math.round((video.currentTime / dur) * 1000);
  timeCurrent.textContent = fmt(video.currentTime);
}

function setHandyUI(state) {
  // state: 'disconnected' | 'connected' | 'syncing' | 'ready' | 'error'
  const colorMap = {
    disconnected: '',
    connected: 'connected',
    syncing: 'syncing',
    ready: 'connected',
    error: 'error',
  };
  const textMap = {
    disconnected: '未接続',
    connected: '接続済み',
    syncing: 'タイムシンク中...',
    ready: '準備完了',
    error: 'エラー',
  };
  handyDot.className = `status-dot ${colorMap[state] || ''}`;
  handyStatus.textContent = textMap[state] || state;
  handyStatus.className = `status-text ${state === 'ready' || state === 'connected' ? 'ok' : state === 'error' ? 'err' : ''}`;

  ctrlHandyDot.className = `status-dot ${colorMap[state] || ''}`;
  ctrlHandyLabel.textContent = `The Handy: ${textMap[state] || state}`;
}

function setScriptStatus(state, text) {
  scriptStatus.style.display = 'flex';
  const colorMap = { ok: 'connected', error: 'error', loading: 'syncing' };
  scriptStatusDot.className = `status-dot ${colorMap[state] || ''}`;
  scriptStatusText.textContent = text;
  scriptStatusText.className = `status-text ${state === 'ok' ? 'ok' : state === 'error' ? 'err' : ''}`;
}

// ── Video ─────────────────────────────────────────────────────────────────────

function loadVideoFile(file) {
  player.loadVideo(file);
  videoFilename.textContent = file.name;
  videoFileInfo.style.display = 'flex';
  videoDrop.style.display = 'none';
  playBtn.disabled = false;
  seekBar.disabled = false;

  // Auto-detect format from filename
  const name = file.name.toLowerCase();
  if (name.includes('_sbs') || name.includes('-sbs') || name.includes('sbs_')) {
    if (name.includes('180')) { formatSelect.value = 'sbs180'; }
    else { formatSelect.value = 'sbs360'; }
  } else if (name.includes('_lr') || name.includes('-lr')) {
    formatSelect.value = 'sbs360';
  } else if (name.includes('_tb') || name.includes('-tb') || name.includes('tb_')) {
    formatSelect.value = 'tb360';
  } else if (name.includes('180')) {
    formatSelect.value = 'mono180';
  }
  player.setFormat(formatSelect.value);
}

videoDrop.addEventListener('click', () => videoInput.click());
videoDrop.addEventListener('keydown', (e) => { if (e.key === 'Enter') videoInput.click(); });

videoInput.addEventListener('change', (e) => {
  if (e.target.files[0]) loadVideoFile(e.target.files[0]);
});

videoClear.addEventListener('click', () => {
  video.src = '';
  videoFileInfo.style.display = 'none';
  videoDrop.style.display = 'block';
  playBtn.disabled = true;
  seekBar.disabled = true;
  document.getElementById('no-video-overlay').style.display = 'flex';
});

// Drag and drop on videoDrop
['dragenter', 'dragover'].forEach(ev => {
  videoDrop.addEventListener(ev, (e) => { e.preventDefault(); videoDrop.classList.add('over'); });
});
['dragleave', 'drop'].forEach(ev => {
  videoDrop.addEventListener(ev, () => videoDrop.classList.remove('over'));
});
videoDrop.addEventListener('drop', (e) => {
  e.preventDefault();
  const file = e.dataTransfer.files[0];
  if (file && file.type.startsWith('video/')) loadVideoFile(file);
});

// Global drag-and-drop (on canvas area too)
document.addEventListener('dragover', (e) => e.preventDefault());
document.addEventListener('drop', (e) => {
  e.preventDefault();
  const files = [...e.dataTransfer.files];
  const vid = files.find(f => f.type.startsWith('video/'));
  const scr = files.find(f => f.name.endsWith('.funscript') || f.name.endsWith('.json'));
  if (vid) loadVideoFile(vid);
  if (scr) loadScriptFile(scr);
});

// ── Funscript ─────────────────────────────────────────────────────────────────

function loadScriptFile(file) {
  const reader = new FileReader();
  reader.onload = (e) => {
    try {
      const info = funscript.load(e.target.result);
      scriptFilename.textContent = file.name;
      scriptFileInfo.style.display = 'flex';
      scriptDrop.style.display = 'none';
      setScriptStatus('ok', `${info.actionCount} アクション / ${fmt(info.duration / 1000)}`);

      // Show upload button if handy is connected
      if (handy.isConnected) handyUploadRow.style.display = 'block';
    } catch (err) {
      setScriptStatus('error', err.message);
    }
  };
  reader.readAsText(file);
}

scriptDrop.addEventListener('click', () => scriptInput.click());
scriptDrop.addEventListener('keydown', (e) => { if (e.key === 'Enter') scriptInput.click(); });

scriptInput.addEventListener('change', (e) => {
  if (e.target.files[0]) loadScriptFile(e.target.files[0]);
});

scriptClear.addEventListener('click', () => {
  funscript.clear();
  scriptFileInfo.style.display = 'none';
  scriptDrop.style.display = 'block';
  scriptStatus.style.display = 'none';
  handyUploadRow.style.display = 'none';
  handy.isReady = false;
});

['dragenter', 'dragover'].forEach(ev => {
  scriptDrop.addEventListener(ev, (e) => { e.preventDefault(); scriptDrop.classList.add('over'); });
});
['dragleave', 'drop'].forEach(ev => {
  scriptDrop.addEventListener(ev, () => scriptDrop.classList.remove('over'));
});
scriptDrop.addEventListener('drop', (e) => {
  e.preventDefault();
  const file = e.dataTransfer.files[0];
  if (file) loadScriptFile(file);
});

// ── Format ────────────────────────────────────────────────────────────────────

formatSelect.addEventListener('change', () => player.setFormat(formatSelect.value));

// ── Video controls ────────────────────────────────────────────────────────────

video.addEventListener('loadedmetadata', () => {
  timeTotal.textContent = fmt(video.duration);
  seekBar.max = 1000;
});

video.addEventListener('timeupdate', updateSeekBar);

video.addEventListener('play', () => {
  playBtn.textContent = '⏸';
  if (handy.isReady) handy.play(video.currentTime).catch(console.warn);
});

video.addEventListener('pause', () => {
  playBtn.textContent = '▶';
  if (handy.isReady) handy.stop().catch(console.warn);
});

video.addEventListener('ended', () => {
  playBtn.textContent = '▶';
  if (handy.isReady) handy.stop().catch(console.warn);
});

playBtn.addEventListener('click', () => {
  if (video.paused) player.play().catch(console.warn);
  else player.pause();
});

seekBar.addEventListener('mousedown', () => { _seekDragging = true; });
seekBar.addEventListener('touchstart', () => { _seekDragging = true; }, { passive: true });

seekBar.addEventListener('input', () => {
  const t = (seekBar.value / 1000) * video.duration;
  timeCurrent.textContent = fmt(t);
});

seekBar.addEventListener('change', () => {
  _seekDragging = false;
  const t = (seekBar.value / 1000) * video.duration;
  video.currentTime = t;
  if (handy.isReady) handy.seek(t, !video.paused).catch(console.warn);
});

muteBtn.addEventListener('click', () => {
  video.muted = !video.muted;
  muteBtn.textContent = video.muted ? '🔇' : '🔊';
});

volumeBar.addEventListener('input', () => {
  video.volume = volumeBar.value / 100;
  video.muted = video.volume === 0;
  muteBtn.textContent = video.muted ? '🔇' : '🔊';
});

// Keyboard shortcuts
document.addEventListener('keydown', (e) => {
  if (e.target.tagName === 'INPUT') return;
  if (e.key === ' ' || e.key === 'k') {
    e.preventDefault();
    if (video.paused) player.play().catch(console.warn);
    else player.pause();
  } else if (e.key === 'ArrowRight') {
    video.currentTime = Math.min(video.duration, video.currentTime + 10);
  } else if (e.key === 'ArrowLeft') {
    video.currentTime = Math.max(0, video.currentTime - 10);
  } else if (e.key === 'm') {
    video.muted = !video.muted;
    muteBtn.textContent = video.muted ? '🔇' : '🔊';
  }
});

// ── The Handy ────────────────────────────────────────────────────────────────

// Restore saved key
const savedKey = localStorage.getItem('handyConnectionKey') || '';
if (savedKey) handyKeyInput.value = savedKey;

handyToggleKey.addEventListener('click', () => {
  handyKeyInput.type = handyKeyInput.type === 'password' ? 'text' : 'password';
});

handyConnectBtn.addEventListener('click', async () => {
  const key = handyKeyInput.value.trim();
  if (!key) { alert('接続キーを入力してください'); return; }

  localStorage.setItem('handyConnectionKey', key);
  handy.setConnectionKey(key);
  handyConnectBtn.disabled = true;
  setHandyUI('syncing');
  handySyncRow.style.display = 'flex';
  handySyncDot.style.display = 'block';
  handySyncStatus.textContent = '接続確認中...';

  try {
    const connected = await handy.checkConnection();
    if (!connected) {
      setHandyUI('error');
      handyStatus.textContent = 'デバイスがオンラインではありません';
      handySyncStatus.textContent = '';
      handySyncDot.style.display = 'none';
      return;
    }

    setHandyUI('connected');
    handySyncStatus.textContent = 'タイムシンク中 (0/30)...';

    await handy.syncTime(30, (done, total) => {
      handySyncStatus.textContent = `タイムシンク中 (${done}/${total})...`;
    });

    handySyncDot.style.display = 'none';
    handySyncStatus.textContent = `同期完了 (オフセット: ${handy.serverTimeOffset.toFixed(0)} ms)`;
    setHandyUI('connected');

    if (funscript.loaded) handyUploadRow.style.display = 'block';

  } catch (err) {
    setHandyUI('error');
    handyStatus.textContent = `エラー: ${err.message}`;
    handySyncDot.style.display = 'none';
  } finally {
    handyConnectBtn.disabled = false;
  }
});

handyUploadBtn.addEventListener('click', async () => {
  if (!funscript.loaded) { alert('先にFunscriptを読み込んでください'); return; }

  handyUploadBtn.disabled = true;
  handyUploadBtn.textContent = 'アップロード中...';
  setScriptStatus('loading', 'アップロード中...');

  try {
    const scriptData = {
      version: '1.0',
      actions: funscript.actions,
    };

    const url = await handy.uploadScript(scriptData);
    setScriptStatus('ok', 'アップロード完了');

    handyUploadBtn.textContent = 'HSSP セットアップ中...';
    await handy.setupHSSP(url);

    setHandyUI('ready');
    handySyncStatus.textContent = 'スクリプト準備完了 — 再生でThe Handyが動作します';
    setScriptStatus('ok', 'Handyと同期済み ✓');
    handyUploadBtn.textContent = '再セットアップ';
  } catch (err) {
    setScriptStatus('error', `失敗: ${err.message}`);
    handyUploadBtn.textContent = 'Funscriptをアップロード & 準備';
    console.error(err);
  } finally {
    handyUploadBtn.disabled = false;
  }
});
