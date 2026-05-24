# PSVR PC Video Player

SteamVR不要でPSVRをPCに直接接続して使うVRビデオプレイヤー。  
The Handyデバイスへの Funscript 同期再生に対応。

---

## 特徴

- **SteamVR不要** — PSVRFramework を参考にUSB HIDで直接通信
- **IMUヘッドトラッキング** — Madgwick フィルタで 1000Hz 処理
- **レンズ歪み補正** — バレル歪み (K1=0.22, K2=0.24) をGPUシェーダーで補正
- **多フォーマット対応** — 360°/180° Mono・SBS・TB、フラット 2D シアター
- **The Handy 連携** — API v2 タイムシンク + HSSP Funscript 同期

## 動作環境

| 項目 | 要件 |
|------|------|
| OS | Windows 10/11 (x64) |
| ランタイム | 同梱（self-contained） |
| PSVR | PlayStation VR 第1世代 |
| 接続 | USB + HDMI（プロセッサーユニット経由） |

---

## インストール

[Releases](../../releases) から最新の `PSVRPlayer-Windows-vYYYY.MM.DD.zip` をダウンロードして展開してください。

```
PSVRPlayer-Windows-vXXXX.zip
└── PSVRPlayer.exe     ← これを実行
    libvlc/            ← VLC ネイティブDLL（自動参照）
    ...
```

---

## セットアップ手順

### 1. PSVR を PC に接続

```
PSVR ヘッドセット
    ├── USB  ─────┐
    └── HDMI ─────┤── プロセッサーユニット ── PC
                  └── 電源アダプター
```

Windows の「ディスプレイ設定」に **PlayStation VR (1920×1080)** が表示されれば接続完了。

### 2. アプリを起動

1. `PSVRPlayer.exe` を実行
2. **PSVR接続** をクリック
3. **VRモード開始** → プロセッサーユニットがVRモードに切り替わる
4. 動画ファイルを開く
5. **VRウィンドウを開く** → PSVR ディスプレイにフルスクリーン表示

> **コントロールインターフェースが届かない場合**  
> [PSVRToolbox](https://github.com/gusmanb/PSVRFramework) で手動でVRモードに切り替えてから `PSVRPlayer.exe` を起動してください。

### 3. センタリング（視点リセット）

ヘッドセットを正面に向けた状態で **センター** ボタンを押す。

---

## The Handy + Funscript

1. [The Handy](https://www.thehandy.com/) の接続キーを入力して **接続**  
   → 30回タイムシンクが自動実行される
2. `.funscript` ファイルを読み込む
3. **Funscriptをアップロード & 準備** をクリック  
   → Handy CDN にアップロード後 HSSP セットアップ
4. 動画を再生すると The Handy が自動で同期動作

---

## 対応フォーマット

| 選択肢 | 説明 |
|--------|------|
| 360° Mono | 等長方形 360° 単眼 |
| 360° SBS | 360° 左右分割ステレオ |
| 360° TB | 360° 上下分割ステレオ |
| 180° Mono | 前半球 180° 単眼 |
| 180° SBS | 180° 左右分割ステレオ |
| フラット | 仮想スクリーンで 2D 視聴 |

ファイル名に `_sbs` `_tb` `180` が含まれる場合は自動判定。

---

## ビルド（開発者向け）

### 要件
- Visual Studio 2022 または .NET 8 SDK
- Windows (WPF)

### 手順

```bash
cd PSVRPlayer
dotnet restore -r win-x64
dotnet build -c Release
# または
dotnet publish -c Release -r win-x64 --self-contained true
```

---

## アーキテクチャ

```
PSVRPlayer/
├── PSVR/
│   ├── PSVRController.cs    USB HID通信 (mi_04センサー / mi_05コントロール)
│   └── MadgwickFilter.cs    IMU融合フィルタ (1000 Hz)
├── Rendering/
│   └── SphereRenderer.cs    OpenGL 2パスレンダリング + バレル歪み補正
├── Video/
│   └── VideoPlayer.cs       LibVLCSharp ソフトデコード → RGBA フレーム
├── Handy/
│   ├── HandyClient.cs       The Handy API v2 (タイムシンク / HSSP)
│   └── FunscriptPlayer.cs   Funscript パーサー + 線形補間
└── VRWindow.cs              OpenTK NativeWindow (PSVRモニター自動選択)
```

### PSVR USB プロトコル（[PSVRFramework](https://github.com/gusmanb/PSVRFramework) 参考）

| インターフェース | 用途 |
|-----------------|------|
| mi_04 (EP03) | センサーデータ読み取り (64 byte / 1000 Hz) |
| mi_05 (EP04) | VRモードコマンド送信 |

主なコマンド:
- `0x23` — VRモード切替 (0x01=VR / 0x00=シネマ)
- `0x11` — トラッキング有効化 (flags `0xFFFFFF00`)
- `0x17` — ヘッドセット電源

---

## ライセンス

MIT
