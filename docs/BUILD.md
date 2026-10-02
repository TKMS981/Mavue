# Mavue ビルド手順

最終更新: 2026-10-02（Quick View PoC 追加）

---

## 1. 必要環境

| 項目 | 要件 | 現在の開発機の状態（2026-10-02 調査） |
|---|---|---|
| OS | Windows 11（x64 / ARM64） | Windows 11 Home 25H2 (10.0.26200.9550), x64 |
| CPU / GPU | 任意（GPU なしでも WARP で動作する設計） | Ryzen 7 7800X3D / RTX 4080 SUPER + Radeon iGPU, RAM 63 GB, NPU なし |
| .NET SDK | **10.0.401**（`global.json` で固定、`latestFeature` へロールフォワード） | **ユーザーローカルに導入済み** (`%LOCALAPPDATA%\Microsoft\dotnet`)。マシン全体 (`C:\Program Files\dotnet`) には SDK なし・ランタイム 6/8/9 のみ |
| Windows App SDK | 2.5.1（NuGet で自動取得。アプリは自己完結でランタイム同梱） | ランタイム 1.1〜1.8 / 2.x がマシンにも導入済み（必須ではない） |
| Windows SDK | C#: NuGet (`Microsoft.Windows.SDK.BuildTools` 10.0.28000.2705) + 投影 TFM 10.0.26100.0 で **VS 不要** | Windows Kits 未導入 |
| Visual Studio 2026 / Build Tools | **C++ コンポーネント（シェル拡張・ネイティブ描画コア・TWAIN ブリッジ）に必須**: 「C++ によるデスクトップ開発」、MSVC v14.5x x64/ARM64、Windows 11 SDK (10.0.26100 以降)、C++ CMake ツール | **導入済み**（2026-10-02、Build Tools 2026 18.10.2、MSVC 19.51、`C:\Program Files (x86)\Microsoft Visual Studio\18\BuildTools`） |
| Git | 2.4x 以降 | 2.55.0.windows.3（リポジトリは未初期化） |
| PowerShell | 5.1 以降 | 5.1 |

> C# 部分（Quick View PoC を含む現在のソリューションすべて）は .NET SDK だけでビルド・テスト・起動できることを確認済み。
> Build Tools が必要なのはネイティブ COM DLL（Windows 11 右クリックメニュー上段 `native/Mavue.Shell.Native`。今後サムネイル・プレビューハンドラー等）だけ。
> ネイティブ DLL は `Mavue.slnx` に含めず `tools/build-native.ps1` でビルドする（DLL がなくてもソリューションはビルド・テストでき、ネイティブのテストはスキップされる）。

## 2. セットアップ

### 2.1 .NET SDK（管理者権限不要の方法 — 開発機で採用）

```powershell
Invoke-WebRequest https://dot.net/v1/dotnet-install.ps1 -OutFile $env:TEMP\dotnet-install.ps1
& $env:TEMP\dotnet-install.ps1 -Channel 10.0 -InstallDir "$env:LOCALAPPDATA\Microsoft\dotnet" -NoPath
```

ユーザーローカル導入の場合、各シェルで以下を設定する（またはユーザー環境変数に永続化）:

```powershell
$env:DOTNET_ROOT = "$env:LOCALAPPDATA\Microsoft\dotnet"
$env:PATH = "$env:DOTNET_ROOT;$env:PATH"
```

`DOTNET_ROOT` が未設定だと、ビルドした `Mavue.exe`（フレームワーク依存）が .NET 10 ランタイムを見つけられない。
（`tools/smoke-test.ps1` は自動で設定する。）

マシン全体に入れる場合（管理者）: `winget install Microsoft.DotNet.SDK.10`

### 2.2 Visual Studio Build Tools（C++。将来の必須項目）

管理者権限が必要。例:

```powershell
winget install Microsoft.VisualStudio.BuildTools --override "--quiet --wait --add Microsoft.VisualStudio.Workload.VCTools --add Microsoft.VisualStudio.Component.VC.Tools.x86.x64 --add Microsoft.VisualStudio.Component.VC.Tools.ARM64 --add Microsoft.VisualStudio.Component.Windows11SDK.26100 --add Microsoft.VisualStudio.Component.VC.CMake.Project --includeRecommended"
```

（パッケージ ID・コンポーネント ID は導入時に `winget show` / VS Installer で最新を確認すること。）

### 2.3 MSIX ローカルインストール用（Explorer 統合の段階で必要）

- 開発者モードを有効化（設定 → システム → 開発者向け）、またはテスト用コード署名証明書を「信頼されたユーザー」に登録。
- 開発機は現在、開発者モード未設定。

## 3. ビルド

```powershell
dotnet build Mavue.slnx -c Debug      # 開発
dotnet build Mavue.slnx -c Release    # リリース構成
dotnet build src/Mavue.App/Mavue.App.csproj -c Release -p:Platform=ARM64   # ARM64 クロスビルド（実行検証は ARM64 実機が必要）
```

- `TreatWarningsAsErrors=true`。警告はビルド失敗になる。
- NuGet パッケージのバージョンは `Directory.Packages.props` で一元管理（理由は `docs/DEPENDENCIES.md`）。
- パッケージソースは `nuget.config` で nuget.org のみに固定。

出力（WinUI の exe は `AppendPlatformToOutputPath=false` で、ビルド方法によらず同じ場所に出る。アーキテクチャは RID フォルダで区別）:
- アプリ (x64): `src/Mavue.App/bin/<Config>/net10.0-windows10.0.26100.0/win-x64/Mavue.exe`
- アプリ (ARM64): `src/Mavue.App/bin/<Config>/net10.0-windows10.0.26100.0/win-arm64/Mavue.exe`
- Quick View 常駐ホスト: `src/Mavue.QuickView.Host/bin/<Config>/net10.0-windows10.0.26100.0/win-x64/Mavue.QuickView.Host.exe`
- E2E ハーネス: `tools/Mavue.QuickView.Harness/bin/<Config>/net10.0-windows10.0.26100.0/Mavue.QuickView.Harness.exe`

> 注意: 以前はソリューションビルドと単体プロジェクトビルドで出力先（`bin\x64\Debug` と `bin\Debug`）が分かれ、
> 古い exe を起動してしまう事故が起きた（2026-10-02）。現在は統一済み。

> §2.2 の Build Tools 導入コマンドと §6 のパッケージングは**未実行**（計画）。それ以外のコマンドは開発機で実行確認済み。

## 4. テスト

```powershell
dotnet test --solution Mavue.slnx -c Debug
powershell -ExecutionPolicy Bypass -File tools/smoke-test.ps1 -Configuration Debug
```

詳細は `docs/TESTING.md`。

## 5. 実行

```powershell
$env:DOTNET_ROOT = "$env:LOCALAPPDATA\Microsoft\dotnet"   # ユーザーローカル SDK の場合
.\src\Mavue.App\bin\Debug\net10.0-windows10.0.26100.0\win-x64\Mavue.exe [ファイル ...]
```

ファイルは引数・「開く」（Ctrl+O）・ウィンドウへのドロップで開ける。1 ファイルならそのフォルダー内の対応ファイルを ←/→ で移動、
複数ならその範囲を移動する。`--trace-file <path>` は自動テスト用の診断ログ（JSON Lines、指定したローカルファイルにのみ書く）。

### 5.1 Quick View 常駐ホスト（PoC）

```powershell
$qv = ".\src\Mavue.QuickView.Host\bin\Debug\net10.0-windows10.0.26100.0\win-x64\Mavue.QuickView.Host.exe"
& $qv                                   # 常駐開始（既定: panel 表示、WinRT デコーダー、プリウォームあり）
& $qv --timing-log "$env:TEMP\qv.jsonl" # 計測ログ（QPC タイムスタンプ、パス・内容は記録しない）
& $qv --shutdown                        # 常駐中のインスタンスを終了
```

オプション: `--activation panel|noactivate|auto|hookgrant|setforeground|appwindow|attach|noactivate-topmost`（比較用）、
`--decoder winrt|xaml`、`--interpolation fant|linear|cubic|nearest`、`--no-prewarm`、`--include-dialogs`、`--trace-shell`、
`--no-wic`（JPEG の WIC 直接デコードを無効化）、`--no-idle-trim`（非表示後の GC を無効化）、
`--settings <path>`（設定ファイル。既定 `%LOCALAPPDATA%\Mavue\QuickView\settings.json`。`{"version": 1, "imageScale": "FitNoUpscale"}` または
`"ActualSize"`。Quick View を開くたびに読み直すので、書き換えはホストの再起動なしで次の Space から反映。設定画面は未実装）、
`--other-quicklook yield`（QuickLook 等の動作中は Space に反応しない）、
`--quickview <file>...`（指定ファイルを表示。常駐プロセスがあれば名前付きパイプで渡してすぐ終了、なければ自分が常駐になって表示。以降の引数はすべてパス）。

Explorer 統合（現在のユーザーのみ、管理者権限不要。登録されるのは実行した exe のパス）:

```powershell
& $qv --register                # 右クリック「Mavue Quick View」（従来メニュー）+ サインイン時の起動
& $qv --register --no-startup   # 右クリックのみ
& $qv --registration-status     # 登録状態の表示
& $qv --unregister              # 登録した項目をすべて削除
```

`--verb <name>` は E2E 用（利用者の登録と別名で登録・解除する）。

ReadyToRun（起動直後の最初の表示が速くなる。実測は `docs/QUICKVIEW-POC.md` §10.4）:

```powershell
dotnet publish src/Mavue.QuickView.Host -c Release -r win-x64 -p:PublishReadyToRun=true
```

NativeAOT（`-p:PublishAot=true`）は MSVC リンカー（§2.2 の Build Tools）が必要で、現状の開発機では「Platform linker not found」で失敗する（未導入のため）。
起動中は Explorer で Space を押すと Quick View が動作する（他の Quick Look 系ツールと同時に動かすと二重表示の可能性あり）。

### 5.2 Windows 11 右クリックメニュー上段（識別パッケージ）

手順と設計は `docs/WINDOWS-INTEGRATION.md` §15。要点:

```powershell
powershell -File tools/build-native.ps1 -Architecture x64,arm64   # artifacts/native/win-<arch>/Mavue.Shell.Native.dll
dotnet build Mavue.slnx -c Release                                 # ホスト出力に DLL とロゴをコピー
powershell -File tools/new-dev-certificate.ps1                     # 開発用の自己署名証明書（初回のみ）
powershell -File tools/package-identity.ps1                        # artifacts/identity/Mavue.QuickView.Identity.msix
& $qv --register-modern-menu artifacts\identity\Mavue.QuickView.Identity.msix
```

開発用証明書は、自己署名の公開部分を **LocalMachine\TrustedPeople**（管理者権限）に入れる必要があった（CurrentUser\TrustedPeople だけでは
`0x800B0109` で登録できない。実測、2026-10-02。§15.3）。ホストの再ビルドで出力フォルダーを消しても、パッケージの登録はそのまま残る（DLL がないと項目が動かない）。

## 6. パッケージング・インストール（計画）

| 段階 | 内容 | 状態 |
|---|---|---|
| 開発実行 | 非パッケージ（`WindowsPackageType=None`）+ WinAppSDK 自己完結 | 実装済み |
| MSIX | `packaging/Mavue.Package`（App + QuickView ホスト + C++ シェル拡張 + マニフェスト拡張）。`dotnet publish` + `MakeAppx`/`SignTool`（Windows SDK BuildTools 同梱） | Planned |
| 署名 | 開発: 自己署名テスト証明書 / 配布: コード署名証明書（LL フックを使うため署名は必須級: `WINDOWS-INTEGRATION.md` §1） | Planned |
| .NET ランタイム | 配布物は自己完結（`SelfContained`）または ReadyToRun/NativeAOT。Quick View ホストは ReadyToRun を計測済み、NativeAOT は Build Tools 導入後に検証 | Planned |
| Windows Search IFilter | MSIX 拡張がないため別コンポーネント（管理者インストール）を調査 | Investigating |

## 7. 既知の注意点

- リポジトリルートの `package.json` / `package-lock.json` / `node_modules`（`claude-code` 依存）は Mavue とは無関係。`.gitignore` で除外している（削除はしていない）。
- Git リポジトリは初期化済み（初回コミット 862d47a）。
- 生成物（`bin/`, `obj/`, `TestResults/`, `AppPackages/`）はコミットしない。
