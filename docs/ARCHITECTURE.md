# Mavue アーキテクチャ

> 本書は `CLAUDE.md`（開発ルール）と `docs/SPEC.md`（製品仕様）に従属する技術設計書である。
> 仕様と本書が矛盾する場合は SPEC が優先される。本書は機能を削除・省略しない。
> 「目標値」と書かれた性能数値は**未実測の設計目標**であり、実測値は「実測」と明記する。

最終更新: 2026-10-02（Quick View PoC の実測結果を反映。詳細は docs/QUICKVIEW-POC.md）

---

## 1. 設計原則

1. **ネイティブ優先・適材適所** — UI とアプリロジックは C# / .NET 10 / WinUI 3 (Windows App SDK 2.x)。
   Explorer・prevhost・SearchFilterHost など**他プロセスにロードされるコード**は C++ (Win32/COM) で書き、CLR を持ち込まない。
2. **Quick View は独立した常駐プロセス** — エディタ本体 (Mavue.App) の起動コストを Quick View に負わせない。
3. **置換可能なエンジン** — PDF・画像コーデック・OCR・スキャン・印刷はインターフェース越しに利用し、実装を差し替え可能にする。
4. **UI スレッドを塞がない** — デコード・レンダリング・I/O・OCR はすべて非同期 + `CancellationToken`。
5. **元ファイルを壊さない** — 保存は一時ファイル → 検証 → アトミック置換。編集は内部的に非破壊（操作履歴）で保持。
6. **ローカル完結** — 外部送信なし。テレメトリなし（将来入れる場合も既定オフ・明示同意）。
7. **計測して判断する** — 性能に関する設計判断は計測ハーネス（`docs/TESTING.md` §6）の数値で検証する。

---

## 2. プロセス構成

```
┌────────────────────────── ユーザーセッション ───────────────────────────┐
│                                                                          │
│  explorer.exe ──(Space keydown: WH_KEYBOARD_LL)──┐                       │
│     │  ▲ Shell COM (IShellWindows/IFolderView2)  │                       │
│     │  └──────────────────────────────┐          ▼                       │
│     │                         ┌──────────────────────────────┐           │
│     │ IExplorerCommand        │ Mavue.QuickView.Host.exe(常駐)│           │
│     │ (Mavue Quick View) ────►│  ・キーボードフックスレッド     │           │
│     │       named pipe        │  ・選択取得 (STA)              │           │
│     │                         │  ・事前生成済みプレビューWindow │           │
│     │                         │  ・D3D11/D2D デバイス保持       │           │
│     │                         │  ・プレビューキャッシュ          │           │
│     │                         └────────────┬─────────────────┘           │
│     │                                      │ named pipe ("Open in Mavue",│
│     │                                      ▼  Markup from Quick View)    │
│     │                         ┌──────────────────────────────┐           │
│     │ shell\open / 関連付け ──►│ Mavue.App.exe (エディタ)      │           │
│     │                         │  タブ / 複数ウィンドウ / 編集   │           │
│     │                         └────────────┬─────────────────┘           │
│     │                                      │ (将来) 分離デコード          │
│     │                                      ▼                             │
│     │                         ┌──────────────────────────────┐           │
│     │                         │ Mavue.Worker.exe (低権限)     │ Investigating
│     │                         └──────────────────────────────┘           │
│     │                         ┌──────────────────────────────┐           │
│     │                         │ Mavue.Scan.Twain32.exe (x86)  │ 32bit TWAIN DS 用ブリッジ
│     │                         └──────────────────────────────┘           │
│     ▼                                                                    │
│  Mavue.Shell.Preview.dll (C++ COM, in-proc)                              │
│   ・IThumbnailProvider  → Explorer の分離プロセス (dllhost)  [実装済み]   │
│   ・IPreviewHandler     → 専用 AppID の prevhost.exe       [実装済み]   │
│  Mavue.Shell.Native.dll (C++ COM, パッケージのサロゲート)                │
│   ・IExplorerCommand    → dllhost (パッケージ ID 必須: §8) [実装済み]   │
│   ・IPropertyStore      → explorer / SearchProtocolHost 等 (計画)       │
│  Mavue.Search.Filter.dll (C++ IFilter) → SearchFilterHost.exe             │
└──────────────────────────────────────────────────────────────────────────┘
```

| プロセス / バイナリ | 言語 | 役割 | ライフタイム |
|---|---|---|---|
| `Mavue.App.exe` | C# WinUI 3 | エディタ本体（閲覧・編集・変換・印刷・スキャン・OCR） | 通常アプリ。単一インスタンス + 複数ウィンドウ（AppInstance リダイレクト） |
| `Mavue.QuickView.Host.exe` | C# WinUI 3（NativeAOT 対象） | Space キー Quick View。常駐（ログオン時起動、設定で無効化可）。PoC 実装済み | 常駐 / オンデマンド |
| `Mavue.Shell.Native.dll` | C++20 COM | Windows 11 上段メニュー「Mavue Quick View」（`IExplorerCommand`）。将来プロパティハンドラー | 識別パッケージの COM サロゲート |
| `Mavue.Shell.Preview.dll` | C++20 COM | プレビューハンドラー・サムネイルプロバイダー（WIC / Direct2D / PDFium / Media Foundation）。`Mavue.exe --register` で HKCU に登録 | 専用 prevhost.exe・サムネイル用 dllhost にロード |
| `Mavue.Search.Filter.dll` | C++20 COM | Windows Search 用 IFilter（PDF テキスト、OCR テキスト） | SearchFilterHost にロード |
| `Mavue.Native.Render` (静的ライブラリ) | C++20 | PDFium / WIC / D2D による描画の共通コア。Shell DLL と（P/Invoke で）マネージド側から共用 | — |
| `Mavue.Scan.Twain32.exe` | C++ x86 | 64bit アプリから 32bit TWAIN データソースを使うためのブリッジ | スキャン時のみ |
| `Mavue.Worker.exe` | C# / C++ | 信頼できないファイルの解析を低権限プロセスに隔離（Investigating） | オンデマンド |

### 2.1 Explorer 内にマネージドコードを入れない理由

- in-proc の .NET COM サーバーは 1 プロセス 1 ランタイムの制約があり、他社の .NET 製シェル拡張と衝突しうる。
- Explorer / prevhost / dllhost の起動遅延・メモリ増加・クラッシュ時の巻き込み。
- Microsoft はマネージドコードによる in-proc シェル拡張を推奨していない。
- よってシェル拡張は C++ で実装する。
- 共有の実態（2026-10-03）: 対応拡張子の一覧と登録は C#（`ViewerFormats`・`ShellHandlerRegistration`）を正とし、ネイティブ側は内容で形式を判定する。
  デコーダー（WIC と Windows のコーデック拡張、Media Foundation）と `pdfium.dll` の実体は本体と同じもの。表示規則（拡大しない、EXIF の向き、
  GIF の最短遅延、PDF の表示範囲付近だけ描画）は本体に合わせてネイティブで再実装した（WinUI の共通ビューアは Explorer のプロセスに載せられない）。
  計画の `Mavue.Native.Render`（本体と共有する C++ 描画コア）はまだ作っていない（本体は C# から WIC/PDFium を直接使う）。

---

## 3. ソリューション / モジュール構成

2026-10-02 時点で作成済みなのは `src/Mavue.*`（`Mavue.QuickView.Host` を含む）、`tests/Mavue.Core.Tests`,
`Mavue.QuickView.Tests`, `Mavue.Repository.Tests`, `tests/assets/quickview/`, `tools/`（`Mavue.QuickView.Harness` を含む）。
`native/`, `packaging/`, その他のテストプロジェクトは予定。

```
Mavue/
├─ Mavue.slnx
├─ global.json                     .NET SDK ピン留め
├─ Directory.Build.props           共通ビルド設定
├─ Directory.Packages.props        NuGet 中央管理
├─ src/
│  ├─ Mavue.Core/        net10.0           ファイル識別・安全保存・キャッシュキー・IPC 契約・共通抽象
│  ├─ Mavue.Pdf/         net10.0           IPdfEngine 抽象（実装: PDFium / QPDF ラッパー）
│  ├─ Mavue.Image/       net10.0-windows   画像モデル・調整パイプライン・WIC ラッパー
│  ├─ Mavue.Codecs/      net10.0           IImageCodec 抽象・形式レジストリ（WIC 優先 + フォールバック）
│  ├─ Mavue.Metadata/    net10.0           EXIF/XMP/IPTC/ICC/GPS 読み書き抽象
│  ├─ Mavue.Ocr/         net10.0           IOcrEngine 抽象（Windows.Media.Ocr / Tesseract / Windows AI）
│  ├─ Mavue.Markup/      net10.0           注釈・図形・署名のドキュメントモデル（描画非依存）
│  ├─ Mavue.Print/       net10.0-windows   印刷ジョブモデル・N-up レイアウト
│  ├─ Mavue.Scan/        net10.0-windows   IScannerService 抽象（WIA / WinRT / TWAIN）
│  ├─ Mavue.Shell/       net10.0-windows   マネージド側シェル連携（関連付け登録補助、Explorer 選択取得）
│  ├─ Mavue.QuickView/   net10.0-windows   Quick View のロジック（トリガー判定、ナビゲーション、プレビュー選択）
│  ├─ Mavue.Viewer/      WinUI 3 ライブラリ 共通ビューア（表示面・画像デコード・PDF 描画・GIF・動画/音声。App と Quick View で共有）
│  ├─ Mavue.App/         WinUI 3           エディタ UI
│  └─ Mavue.QuickView.Host/ WinUI 3        常駐 Quick View プロセス（Mavue.QuickView.Host.exe。ライブラリ名との衝突を避けるため名称変更）
├─ native/
│  ├─ Mavue.Native.Render/   C++ 静的ライブラリ
│  ├─ Mavue.Shell.Native/    C++ COM DLL
│  ├─ Mavue.Search.Filter/   C++ IFilter DLL
│  └─ Mavue.Scan.Twain32/    C++ x86 EXE
├─ packaging/Mavue.Package/  MSIX（Package.appxmanifest）
└─ tests/
   ├─ Mavue.Core.Tests/ ...  単体テスト（xUnit v3）
   ├─ Mavue.Integration.Tests/
   ├─ Mavue.Perf/            BenchmarkDotNet / 起動遅延ハーネス
   └─ assets/                テスト用ファイル（ライセンス明記・生成スクリプト）
```

### 3.2 共通ビューア（2026-10-02）

- `Mavue.Core.Viewing`（BCL のみ）: 表示サイズ（`DisplaySizing`・`PreviewSizing`）、PDF の現在ページ（`PdfPageCursor`）、動画/音声のシーク・音量（`MediaControlMath`）、
  ファイルの安全ポリシーと事前情報（`PreviewSafetyPolicy`・`FileFacts`）、開ける形式（`ViewerFormats`）、前後移動の一覧（`ViewerFileList`、Explorer に近い名前順）。
- `Mavue.Viewer`（WinUI 3 ライブラリ。XAML ページを持たずコードで UI を組む＝ライブラリの PRI 統合に依存しない）:
  - `Controls.ViewerSurface`: 画像（プレースホルダー付き）・動画/音声（`MediaPlayerElement`）・メッセージの表示面。描くだけで、何をどの大きさでデコードするかは呼び出し側が決める。
  - `Rendering`: `ImageDecoding`（WIC 直接 / WinRT）、`PdfRendering`（Windows.Data.Pdf）、`DecodedImage`。
  - `Playback`: `GifPlayer`、`MediaSession`（1 ファイル 1 セッション、UI スレッドにイベント、破棄後のイベントは捨てる）。
  - `DocumentViewer`: App 用のまとめ役。1 度に 1 ファイル。開く・切替・閉じる・破棄で、デコードの取り消し、GIF/動画の停止、ビットマップ・プレーヤー・ファイルの解放を行う。
    表示面の大きさとスケールモードから計画し、リサイズ・モニター変更で作り直す。ズーム・サムネイル・ページ先読みはここに足す。
- Quick View は表示面・デコード・再生を共有し、段階表示（シェルのサムネイル → 本画像）・先読みキャッシュ・計測などの Quick View 固有の制御は `QuickViewController` に残す。

#### 拡大縮小・回転（2026-10-03）

- 計算は `Mavue.Core.Viewing`（BCL のみ、単体テスト）: `ViewerZoom`（Fit／幅に合わせる／倍率、段階、ホイール、デコードする画素数、ポインター位置を保つスクロール量）、
  `ViewOrientation`（90° 単位の回転と左右反転を「画面上の操作」として合成）。100 % は画像なら 1 画素 = 1 物理画素、PDF・SVG は 96 dpi × モニター倍率。
- 表示は 2 段階: 操作した瞬間は表示中のビットマップを要素の大きさだけ変えて伸縮（`ViewerSurface.ZoomImage`、アンカー位置を保つ）、
  止まった後（約 180 ms）に必要な画素数で再デコード / 再描画。ラスター画像は原画素を超えてデコードしない（拡大は引き伸ばし）、上限 5,000 万画素。GIF・SVG は再デコード不要。
- 回転・反転はデコード後の画素に適用（`Mavue.Image.PixelOrientation`、表示サイズの画素だけ）。ファイルは変更しない。別ファイルに移ると元に戻る。
- PDF は本体では `PdfDocumentSession` で開いたままにする（ページ送り・サムネイルごとに解析し直さない）。ファイルは読み取り・書き込み・削除の共有付きで開き、
  表示中も他のアプリが変更・名前変更・削除できる。描画は 1 つずつ（ゲート）。Quick View は従来どおり描画ごとに開く（常駐プロセスがファイルを掴まない）。
- SVG は XAML の `SvgImageSource`（Direct2D）。大きさは `SvgDimensions`（DTD・外部実体を処理しない）。Direct2D の SVG は `<text>` 等を描かないため、resvg（DEPENDENCIES §4）で置き換える予定。

#### PDF ビューア（2026-10-03、PDFium）

- `Mavue.Pdf`（BCL のみ・WinRT なし）: `PdfiumDocument`（`IPdfDocument`）が PDFium の C API を包む。
  - **スレッド**: PDFium はスレッドセーフでないため、全呼び出しを `PdfiumLibrary.Gate` で直列化。ライブラリは初回使用時に初期化し、プロセス終了まで保持。
  - **ファイル**: `FPDF_LoadCustomDocument` の読み取りコールバックで必要な部分だけ読む（全体をメモリに載せない）。`FileShare.ReadWrite | Delete` で開くので、表示中も他のアプリが変更・名前変更・削除できる。
  - **ページ**: 必要時に読み込み、直近 4 ページ（ページ＋テキスト）だけ保持。
  - **座標**: 「表示ポイント」= 表示時のページ左上原点のポイント（ページの /Rotate と表示の回転を適用）。変換は PDFium の `FPDF_PageToDevice/DeviceToPage`。
  - **リンク**: リンク注釈（ページ内移動・URI）と本文中の URL。http/https/mailto 以外と起動・JavaScript・他ファイルへのリンクは返さない。
  - pdfium.dll が読み込めない場合 `PdfiumLibrary.IsAvailable` が false になり、表示は Windows.Data.Pdf にフォールバック。
- `Mavue.Core.Viewing.PdfPageLayout`（純粋計算）: 単一ページ / 連続 / 見開きの配置、ページ・幅に合わせる倍率、表示範囲のページ（二分探索）、現在ページ。
- `Mavue.Viewer.Controls.PdfDocumentView`（本体）: スクロールビューアー上の Canvas に、**表示範囲の前後 1 画面分のページだけ**要素とビットマップを作る（それ以外は解放・再利用）。
  描画は見えているページを優先してバックグラウンドで 1 枚ずつ（`PdfiumRendering.Render` が PDFium から SoftwareBitmap のメモリへ直接描く）、1 ページ最大 2,400 万画素。
  **UI スレッドは PDFium を直接呼ばない**（大きなページの描画中にロック待ちで固まらないよう、文字位置・選択範囲・検索強調・リンクも別スレッドで計算して反映）。
  ページキー・目次・リンク・検索で移動したときは、スクロールが目的位置に着くまで目的ページを「現在ページ」に固定する（途中のスクロール位置から再計算すると前のページに戻った、実測）。
- `Mavue.Viewer.PdfSession`（本体と Quick View で共通）: 開いている PDF 1 つ分の状態と操作。`PdfDocumentView` を持ち、開く/閉じる（文書の解放は別スレッド）、
  ページ移動・表示方法・倍率、検索（16 ページずつ別スレッド、最大 10,000 件、取り消し可）、目次、選択文字のコピー、リンク要求（`UriRequested`。開くかどうかはアプリ側が決める）を公開する。
- 共通の UI 部品（`Mavue.Viewer.Controls`）: `PdfSearchBar`（Ctrl+F の検索バー、Enter/F3 で次・前）、`PdfSidebar`（「ページ」サムネイル・「目次・しおり」・「検索結果」のタブ）、
  `PdfPresentationWindow`（F5 の全画面表示。ページを画面に収めて描画し、前後ページを先読み）。文字列は `Func<string, string>` で各アプリのリソース（`Pdf_*` キー）から受け取る
  （ライブラリは独自の PRI を持たない）。部品はセッションのイベントだけを見るので、本体と Quick View で同じものを載せる。
- `DocumentViewer`（本体）は PDF を `PdfSession` で開いて表示面（`ViewerSurface.ShowDocument`）に載せる。
- Quick View も同じ `PdfSession`・部品を使う（`QuickViewController.ShowPdfSessionAsync`）。表示中だけ文書を開き、閉じる・次のファイルへ移るとすぐ解放（常駐プロセスがファイルを掴み続けない）。
  Explorer が前面のままの PageUp/PageDown はフック経由でセッションのページ送りに渡す。検索欄・ページ番号欄に入力中は Space/Esc/文字キーを Quick View の操作に使わない。
  表示方法とサイドバーの開閉は `QuickViewSettings` に保存。pdfium.dll が無いときは従来の 1 ページ画像表示（Windows.Data.Pdf）にフォールバック。
- 設定レコード（`AppSettings`・`QuickViewSettings`）のプロパティは `init` ではなく `set`: System.Text.Json のソース生成は init 専用プロパティを常に代入するため、
  ファイルに無い項目が初期値ではなく既定値（例: 表示方法が「単一ページ」）になった（単体テストで再現・回帰テスト追加）。

#### 本体のアプリ層（`Mavue.App`）

- 設定 `Mavue.Core.Settings.AppSettings`（`%LOCALAPPDATA%\Mavue\settings.json`、SafeFileWriter で原子的に保存、複数ウィンドウを想定して変更のたびに読み直す）:
  外観、開いたときの表示サイズ、情報パネル・サムネイルの表示、最近使ったファイル（15 件）、ウィンドウ位置。Quick View の設定（`QuickViewSettings`）も Core に移し、本体の設定画面から書く。
- ファイル操作は `Mavue.Shell.ShellActions`（Windows の「プログラムから開く」・フォルダーに表示・プロパティ・クリップボード・Mavue の起動）を本体と Quick View で共用。
- Windows への登録は `Mavue.Shell.AppRegistration`（HKCU のみ。WINDOWS-INTEGRATION §3）。

#### パッケージ版（2026-10-03）

- `Mavue.Shell.PackageInfo.IsPackaged`（`GetCurrentPackageFullName`）で MSIX 内かを判定。パッケージ内では Windows 統合をマニフェスト（`MsixPackageManifest`）が担い、
  HKCU への登録（`--register` と設定画面）は行わない。パッケージの配置は `Mavue\`（本体と常駐ホストが同じフォルダー。2026-10-04 から）。`docs/PACKAGING.md`。
- 配布物（2026-10-04）: .NET と Windows App SDK を self-contained で同梱し、本体と Quick View を 1 フォルダーに同居（共通ファイルはバイト一致）。
  ZIP 版（`Install.cmd`、HKCU 登録、版ごとのフォルダー）と MSIX 版は同じフォルダーから作る（`tools/build-release.ps1`）。
- 暗号化 PDF: `PdfSession.OpenAsync(..., password)`。`DocumentViewer.PasswordProvider`（アプリが設定）が入力を求め、誤りなら再入力。パスワードは保存・記録しない。

### 3.1 依存方向（循環禁止）

```
Mavue.App ─┬─► Mavue.Viewer ─► Mavue.Image / Core
           ├─► Mavue.Pdf / Image / Ocr / Markup / Print / Scan / Metadata / Shell / QuickView（予定）
           └─► Mavue.Core
Mavue.QuickView.Host ─┬─► Mavue.QuickView ─► Mavue.Codecs / Pdf(読み取りのみ) / Core
                      ├─► Mavue.Viewer（App と同じ表示面・デコード・再生）
                      └─► Mavue.Image（WIC 直接デコード）
各ドメインモジュール ─► Mavue.Core（のみ）
Mavue.Core ─► BCL のみ（Windows 非依存。将来のテスト容易性のため）
```

- Windows 固有 API（WinRT/Win32/COM）を使うモジュールは TFM を `net10.0-windows10.0.26100.0` とし、
  `Mavue.Core` / `Mavue.Markup` などの純粋ロジックは `net10.0` に保つ（`CLAUDE.md` §5「Windows 固有コードの分離」）。
- ネイティブ呼び出しは各モジュール内の `Interop/` に集約し、`LibraryImport`（ソース生成 P/Invoke）を使う（NativeAOT 互換）。
- COM は `GeneratedComInterface`（ComWrappers ソース生成）を使い、NativeAOT・トリミング互換にする。

---

## 4. Quick View アーキテクチャ（最重要）

### 4.1 要求

SPEC §3：Explorer でファイル選択 → Space → 即時プレビュー。複数選択、前後移動、Esc で閉じる、
全画面、ズーム、回転、インデックスシート、ファイル名/情報、他アプリで開く、コピー、共有、印刷、
Quick View からのマークアップ、GIF/音声/動画、HDR（可能な範囲）。

### 4.2 Windows 上の制約（調査結果）

- Explorer には **Space キーでプレビューを出すための公式拡張ポイントは存在しない**。
  公開されている実装（QuickLook (GPL-3.0)、PowerToys Peek (MIT)）はいずれも
  「グローバルなキー入力監視 + Shell COM で選択取得」方式である（コードは流用しない。方式のみ参考）。
- 低レベルキーボードフック (`WH_KEYBOARD_LL`) は
  - フック手続きが `LowLevelHooksTimeout`（既定約 1 秒）を超えると Windows 7 以降は**通知なしにフックが外される**。
  - フックはインストールしたスレッドのメッセージループで呼ばれる。
  - UIPI により、昇格した（管理者）Explorer へのキー入力は非昇格プロセスから監視できない。
  - セキュリティ製品からキーロガー類似と判定されうる → 署名必須、Space 以外のキー内容は保持・記録しない。
- `RegisterHotKey` で Space を登録すると全アプリで Space が奪われるため不可。
- Explorer へ DLL を注入する `WH_KEYBOARD`（スレッドフック）は侵襲的で不安定なため採用しない。
- 本機には **QuickLook (21090PaddyXu.QuickLook) がインストール済み**（調査で確認）。両方が Space を処理すると二重表示になるため、
  競合検出と設定による無効化が必要。

### 4.3 採用方式

```
[Space keydown]
   │ (1) WH_KEYBOARD_LL コールバック（専用スレッド, < 1ms 目標）
   │     ・修飾キーなしの VK_SPACE のみ対象。それ以外は即 CallNextHookEx
   │     ・GetForegroundWindow → クラス名判定 (CabinetWClass / Progman / WorkerW / #32770 のうちファイルビューを持つもの)
   │     ・GetGUIThreadInfo で hwndFocus を取得し、フォーカスが
   │         DirectUIHWND (親 SHELLDLL_DefView) または SysListView32 (親 SHELLDLL_DefView) の時のみ「対象」
   │       → 名前変更中の Edit、アドレスバー、検索ボックス (XAML island) では素通し
   │     ・直前 ~1 秒以内に文字キー入力があればタイプアヘッド検索中とみなし素通し（設定可能）
   │     ・IME 変換中（フォーカススレッドの IME がコンポジション中）は素通し
   │     ・対象なら選択取得スレッドのキューへ投入し、キーを握りつぶす (return 1)。キーリピートと対応する keyup も握りつぶす
   ▼
   (2) 選択取得スレッド（専用 STA、IShellWindows をキャッシュ。MTA では 0x8001010D で失敗することを実測）
   │     ・前面 HWND に対応する IShellBrowser を特定
   │       Windows 11 のタブ: 各タブが IShellWindows の個別エントリ・ShellTabWindowClass・SHELLDLL_DefView を持つ（実測）。
   │       フォーカス中アイテムビューの親 DefView と IShellView::GetWindow を照合してアクティブタブを特定
   │     ・IShellView → IFolderView2 → GetSelection / Items(SVGIO_SELECTION | SVGIO_FLAG_VIEWORDER)
   │     ・単一選択時は表示順の全アイテムも遅延取得（前後ナビゲーション用）
   │     ・デスクトップ: IShellWindows::FindWindowSW(CSIDL_DESKTOP, SWFO_NEEDDISPATCH) 経由
   ▼
   (3) 事前生成済み Quick View ウィンドウを「パネル」として表示（アクティブ化しない）
   │     ・【R2 検証済み】フック経由のプロセスは実ユーザー入力では前面化の権利を安定して得られない
   │       （物理キーで SetForegroundWindow 系 9/11 失敗、リモート操作では HWND_TOP でも Explorer の後ろ）。
   │     ・採用: SetWindowPos(HWND_TOPMOST, SWP_NOACTIVATE | SWP_SHOWWINDOW)。表示中だけ Topmost 帯、
   │       Explorer がキーボードを保持。他アプリが前面になったら閉じ、閉じる前に Topmost を解除。
   │       Esc / Space はフックで処理。ユーザーがパネルをクリックすると通常のアクティブウィンドウになる。
   ▼
   (4) 段階的表示 (progressive)
         a. Mavue プレビューキャッシュにヒット → 即表示
         b. IShellItemImageFactory::GetImage(SIIGBF_THUMBNAILONLY | SIIGBF_INCACHEONLY) で
            Windows サムネイルキャッシュから即時プレースホルダ（専用 STA スレッド、warm 時 4〜7 ms を実測）
         c. 形式別のフル品質描画をバックグラウンドで開始（キャンセル可能）
            - 画像: PoC は WinRT BitmapDecoder + BitmapTransform で「画面サイズ」へ縮小デコード（Fant）。
              巨大 JPEG では DCT 縮小が効いていないと推測されるため、WIC 直接（IWICBitmapSourceTransform）経路を次に検証
            - PDF: PoC は Windows.Data.Pdf（OS 内蔵）で 1 ページ目を画面解像度にレンダリング。製品は PDFium
            - 動画/音声: MediaPlayerElement (Media Foundation)
         d. 次/前のアイテムを先読み（隣接 1〜2 件、メモリ上限付き）
```

### 4.4 遅延予算（目標値と PoC 実測）

| 区間 | 目標 | 計測方法 |
|---|---|---|
| 区間 | 目標 | PoC 実測（Debug, 中央値） |
|---|---|---|
| フックコールバック処理 | < 1 ms | 0.0〜0.6 ms |
| 選択取得（Shell COM） | < 15 ms | 3〜5 ms |
| ウィンドウ表示呼び出し完了 | < 30 ms | 7〜12 ms |
| 最初のフレーム | — | 10〜17 ms |
| **Space → 最初の画像（キャッシュ済みサムネイル or 小画像のフル品質）** | **< 80 ms** | 17〜29 ms（アプリ内）、画面ピクセル検出で 24〜71 ms |
| Space → フル品質（24MP JPEG / PDF 1 ページ目） | < 250 ms | 103 ms / 34 ms（192MP JPEG 451 ms、100MP PNG 755 ms） |
| 常駐していない場合のコールド起動 | 計測して改善 | プロセス起動 → 準備完了 約 160 ms（プリウォーム込み、Debug） |

計測方法: QPC タイムスタンプ（ETW EventSource "Mavue-QuickView" + JSON Lines）と、E2E ハーネスの画面ピクセル検出。詳細は `docs/QUICKVIEW-POC.md`。

**実測（2026-10-02, 本機）**: 空の WinUI 3 アプリ（Debug・JIT・フレームワーク依存）の起動 → `OnLaunched` → 終了まで **約 730 ms**。
これはコールド起動では上記目標を満たせないことを示しており、**常駐プロセス + 事前生成ウィンドウ**方式の根拠とする。
NativeAOT / ReadyToRun / Release 構成でどこまで縮むかは未計測（次の課題）。

### 4.5 常駐プロセスの設計

- ログオン時起動: MSIX の `desktop:StartupTask`（ユーザーが設定アプリで無効化可能）。非パッケージ時は `HKCU\...\Run`。
- 常駐時のメモリ目標: アイドル時ワーキングセット最小化（デコーダーは遅延ロード、キャッシュはメモリ圧迫通知
  `CreateMemoryResourceNotification` で縮退）。数値目標は計測後に設定。
- 事前初期化するもの（PoC 実装済み）: プレビューウィンドウ（クローク状態で 1 フレーム描画して非表示）、IShellWindows 接続、
  シェルサムネイル経路（初回 60 ms を除去）。予定: D3D11/D2D デバイス、WIC ファクトリ、パイプサーバー。
  実測効果: 起動直後の最初の Space のフル表示が 102〜116 ms → 85〜89 ms。
- 遅延初期化するもの（重量）: PDFium、メディアパイプライン、フォールバックコーデック (libheif 等)、OCR。
- ウォッチドッグ: フックが外れた場合（タイムアウト）を検知できないため、定期的（例: 30 秒）にフックを再インストールする設計を検討。
- 常駐を無効にした場合も、コンテキストメニュー「Mavue Quick View」からコールド起動で利用可能。

### 4.6 Quick View の機能配置

| 機能 | 実装方針 |
|---|---|
| 複数選択 / 前後移動 | **実装済み**: `DShellFolderViewEvents.SelectionChanged` / `DWebBrowserEvents2` を購読して Explorer の選択変更に追従（ポーリングなし）。複数選択時は ←/→ をフックで横取りして選択内を移動（詳細は QUICKVIEW-POC §7） |
| Esc / Space で閉じる | パネル表示中はフックで処理（Explorer が前面のときのみ Esc を横取り）、アクティブ化後はウィンドウのキーハンドラ。Space はトグル（PoC 実装・実操作で確認済み） |
| 表示サイズ | **実装済み**: 既定は「拡大しない」（画像領域の物理ピクセルに合わせてデコードし 1:1 表示、小さい画像は元のサイズ）。設定 `imageScale=ActualSize` で 1 画素 = 画面の 1 画素 + スクロール（5,000 万画素まで）。設定は `%LOCALAPPDATA%\Mavue\QuickView\settings.json`（`DisplaySizing` / `QuickViewSettings`）で、Quick View を開くたびに更新時刻を見て読み直す（ホストの再起動不要）。ウィンドウ内の切替ボタンはユーザー要望で廃止し、将来の設定画面（F23.03）で切り替える。画像領域はウィンドウの実際のクライアントサイズと `GetDpiForWindow` から計算し、表示中のリサイズ・別モニターへの移動で変わったら作り直す（QUICKVIEW-POC §10.6, §10.8） |
| 先読み | **実装済み**: 表示中の前後の項目をバックグラウンドでデコードし LRU キャッシュ（192 MB）に保持（QUICKVIEW-POC §10.2） |
| タブ切替の追従 | **実装済み**: 持ち主の Explorer に限定した `EVENT_OBJECT_FOCUS` でタブの切替を検知（QUICKVIEW-POC §10.1） |
| 全画面 / ズーム / 回転 | 共通ビューア（`Mavue.Viewer.Controls.ViewerSurface`。App と共有、§3.2）に追加する。ズーム・パン・回転は未実装 |
| インデックスシート | 複数選択時のグリッド表示（サムネイルは Windows サムネイルキャッシュ + Mavue キャッシュ） |
| 他アプリで開く | `SHAssocEnumHandlers` / `IAssocHandler::Invoke`（「プログラムから開く」相当） |
| コピー / ドラッグ | `IDataObject`（CF_HDROP + 画像形式）|
| 共有 | `DataTransferManagerInterop.ShowShareUIForWindow`（Windows 共有 UI） |
| 印刷 | Mavue.Print 経由（§7） |
| マークアップ | Mavue.App へ named pipe でハンドオフし、マークアップモードで開く |
| クラウドプレースホルダ (OneDrive 等) | フル読み込みはハイドレーション（ダウンロード）を誘発するため、`FILE_ATTRIBUTE_RECALL_ON_DATA_ACCESS` を確認し、サムネイル表示 + 「ダウンロードして表示」を提示 |
| ネットワーク / 低速ドライブ | タイムアウト付き非同期 I/O、プレースホルダ表示を維持 |
| 仮想フォルダ（ZIP 内・ライブラリ・ごみ箱） | `IShellItem` → `BindToHandler(BHID_Stream)` によるストリームで読み込み（ファイルパスに依存しない） |

---

## 5. 描画パイプライン

- **GPU**: D3D11 デバイス（`D3D11_CREATE_DEVICE_BGRA_SUPPORT`）+ Direct2D 1.x デバイスコンテキスト。WinUI 3 側は
  Win2D (`Microsoft.Graphics.Win2D`) の `CanvasVirtualControl`/`CanvasSwapChainPanel` で表示。
- **フォールバック**: デバイス作成失敗・デバイスロスト・ブロックリスト該当時は WARP（ソフトウェアラスタライザ）へ。
  CPU のみの縮小デコード経路も保持（`CLAUDE.md` §8）。
- **大画像**: タイル（例: 512px）+ ミップレベル。表示領域と倍率に応じて必要タイルのみデコード。
  WIC の `IWICBitmapSourceTransform` / `IWICPlanarBitmapSourceTransform`、ブロック単位デコード可能な形式（TIFF タイル、JPEG2000 解像度レベル）を活用。
- **大 PDF**: ページ単位・タイル単位レンダリング、表示ページ ±N の先読み、ズーム中は低解像度を先に出し高解像度で置換。
- **HDR**: 画像は scRGB (FP16) スワップチェーン + `DXGI_COLOR_SPACE_RGB_FULL_G10_NONE_P709`、
  JPEG XR / AVIF / HEIF の HDR メタデータは「可能な範囲」で対応（SPEC §3）。SDR ディスプレイではトーンマッピング。
- **High-DPI**: Per-Monitor V2。ラスタライズは常に実ピクセル密度で行う。モニタ間移動で再ラスタライズ。
- **カラーマネジメント**: 画像の ICC プロファイル → ディスプレイプロファイル変換（D2D ColorManagement 効果）。
  ColorSync / Soft Proof は SPEC で除外されており実装しない。

---

## 6. ドキュメントモデル・編集・保存

- **非破壊編集**: 元ファイル + 操作リスト（Crop/Resize/Adjust/Markup/ページ操作…）。表示は操作を適用した結果をキャッシュ。
- **Undo/Redo**: コマンドパターン（`IEditCommand { Apply; Revert; MergeWith }`）。大きなビットマップ差分はディスク退避。
- **安全な保存** (`Mavue.Core.IO.SafeFileWriter`):
  1. 同一ボリュームの一時ファイルに書き込み（`FILE_FLAG_WRITE_THROUGH` 相当でフラッシュ）
  2. 書き込み結果を検証（再オープンしてヘッダ/ページ数等を検証可能な形式は検証）
  3. `ReplaceFileW`（.NET: `File.Replace`）でアトミック置換。属性・ACL・作成日時・代替データストリームを維持
  4. 失敗時は元ファイルを変更しない
- **自動保存 / クラッシュ復旧**: `%LOCALAPPDATA%\Mavue\Recovery\` に操作ジャーナル（操作リスト + 必要なバイナリ）を定期保存。
  起動時に未完了ジャーナルを検出して復旧を提案。
- **ロック**: SPEC §21「Lock」→ 読み取り専用属性 + Mavue 内の編集ロック（macOS Preview の「ロック」相当）。
- **バージョン履歴**: 保存前のスナップショットを Recovery 領域に保持（世代数・容量上限は設定可能）。Windows の「以前のバージョン」(VSS) は参照のみ。

---

## 7. ドメイン別スタック（詳細は DEPENDENCIES.md）

| ドメイン | 第一選択 | 補完 / フォールバック |
|---|---|---|
| PDF 描画・テキスト・フォーム・注釈・ページ操作 | **PDFium** (BSD-3) | Windows.Data.Pdf（描画のみ、Quick View の非常用） |
| PDF 暗号化・権限・線形化・オブジェクトストリーム圧縮・構造操作 | **QPDF** (Apache-2.0) | — |
| 真の墨消し (Redaction) | **Mavue 独自実装**（PDFium のページオブジェクト API + コンテンツストリーム書き換え + 画像 XObject の画素消去 + 注釈/メタデータ/構造の除去 + 検証） | — |
| 画像デコード/エンコード | **WIC**（OS 内蔵 + Store 拡張） | libwebp, libavif+dav1d, OpenJPEG, LibRaw, resvg（SVG）, libheif（HEVC 特許の法的確認後） |
| SVG | **resvg** (Apache-2.0/MIT) | Direct2D SVG（テキスト非対応等の制約あり） |
| 画像調整 | Direct2D エフェクト（Exposure / Brightness / Contrast / Saturation / TemperatureAndTint / Gamma / Sharpen） | CPU 実装（テスト用リファレンス兼フォールバック） |
| 背景除去 | Windows AI ImageObjectExtractor（Copilot+ PC のみ） | ONNX モデル（**商用可ライセンスのもの**）+ Windows ML / ONNX Runtime |
| OCR | **Windows.Media.Ocr**（本機 ja/en 導入済み） | Tesseract 5（Apache-2.0）、Windows AI TextRecognizer（NPU 必須）、PaddleOCR (ONNX) を調査 |
| 音声 / 動画 | Media Foundation（MediaPlayerElement） | FFmpeg は特許・ライセンス上の検討後 |
| 印刷 | Direct2D `ID2D1PrintControl` + `IPrintDocumentPackageTarget`（XPS 印刷パス）+ Mavue 独自プレビュー | `Microsoft.UI.Xaml.Printing.PrintDocument` + `PrintManagerInterop`（Windows 標準印刷 UI） |
| スキャン | `Windows.Devices.Scanners`（WinRT, WIA ベース） | WIA 2.0 COM（`IWiaDevMgr2`）、TWAIN 2.x（64bit DSM + x86 ブリッジ） |
| ペン入力 / 署名 | WinUI 3 Pointer API（筆圧・傾き・消しゴム）+ Win2D 描画 + 独自ストロークモデル | WinUI 3 `InkCanvas`（WinAppSDK 2.4 以降 Experimental。安定版化後に移行検討） |
| メタデータ | WIC メタデータクエリ（EXIF/XMP/IPTC/GPS 読み書き）+ Windows プロパティシステム | MetadataExtractor (Apache-2.0) は読み取り専用の補助 |

---

## 8. Explorer 統合とパッケージ ID

Windows 11 で以下を実現するには**パッケージ ID**が必要:
- 新しい（上段の）コンテキストメニューへの項目追加（`IExplorerCommand` + `desktop4:FileExplorerContextMenus`）
- 共有ターゲット、StartupTask、一部 Windows AI API

よって**配布形態は MSIX を第一とする**。開発時は非パッケージで実行可能に保つ。詳細は `docs/WINDOWS-INTEGRATION.md`。

**2026-10-04 追記**: 上段メニューのパッケージ ID は非パッケージ版でも識別パッケージ（スパース）で得られる。一方、MSIX の
プレビュー/サムネイル ハンドラーは、他のパッケージ（フォト等）も宣言する種類では Explorer に使われないことを実測した（原因と検証は PACKAGING §5.3）。MSIX を第一とする方針（ADR-8）は
再検討中で、ユーザーの判断待ち（`docs/PACKAGING.md` §1・§5.3）。

---

## 9. IPC

- **Named Pipe**（`\\.\pipe\Mavue.<SessionId>.<UserSid ハッシュ>.<用途>`）。DACL は現在のユーザー SID のみ許可。
  `PIPE_REJECT_REMOTE_CLIENTS` を指定。
- メッセージ: 長さプレフィックス付き UTF-8 JSON（ソース生成 `System.Text.Json`、NativeAOT 互換）。
  契約は `Mavue.Core.Ipc` に定義し、バージョンフィールドを持つ。
- 大きなビットマップの受け渡し（例: Quick View → App のマークアップハンドオフ時のデコード済み画像）は
  名前付き共有メモリ（`CreateFileMapping`、同 DACL）を使用（Investigating: 再デコードとの比較を計測して決定）。
- 単一インスタンス: Windows App SDK `AppInstance.FindOrRegisterForKey` + `RedirectActivationToAsync`（`Mavue.App`。予定）。
- **実装済み（Quick View、第 4 工程）**: `Mavue.QuickView.Host.exe --quickview <file>` → パイプ `Mavue.QuickView.<SessionId>.<SID ハッシュ>`。
  DACL は現在のユーザーのみ・NETWORK 拒否、クライアントは `PipeOptions.CurrentUserOnly` でサーバーの所有者を確認。契約は `Mavue.Core.Ipc.QuickViewRequest`
  （バージョン、完全修飾パス、依頼時の前面ウィンドウ）。常駐プロセスの単一インスタンスは `Local\` ミューテックス。
  クライアントは送信前に `AllowSetForegroundWindow` で前面化の権利を常駐プロセスに譲る（QUICKVIEW-POC §11）。

---

## 10. スレッドとキャンセル

- UI スレッド: 描画指示とバインディングのみ。
- デコード / レンダリング: 専用の制限付きスケジューラ（同時実行数 = 論理コア数を上限、Quick View は優先度高）。
- すべての重い API は `CancellationToken` を受け取る。ナビゲーション時は前のアイテムの処理を即キャンセル。
- COM: Shell COM は STA スレッド専用。WIC/D2D はマルチスレッドファクトリを使用。

---

## 11. 多言語化・アクセシビリティ

- 文字列は MRT Core (`.resw`)、`ja-JP` と `en-US`。コードへのハードコード禁止（テストで検出: `docs/TESTING.md`）。
- UI Automation: カスタム描画コントロールには AutomationPeer を実装（ページ・注釈・サムネイルの読み上げ）。
- キーボードのみで全機能に到達可能。ハイコントラストテーマ対応。

---

## 12. セキュリティ・プライバシー

- 解析対象ファイルは信頼できない入力として扱う。PDFium / コーデックの更新を追随（セキュリティ修正）。
- **ファイル取り扱いの規則**（Quick View PoC で一部実装: `PreviewSafetyPolicy`、テスト済み）:
  | 対象 | 規則 |
  |---|---|
  | プレビュー全般 | 読み取り専用・共有モード（他プロセスの書き込みを妨げない）。Quick View は**決して書き込まない** |
  | 悪意ある/壊れたファイル | デコード例外はすべて捕捉してエラー表示（常駐プロセスを落とさない）。将来は低権限 Worker に隔離 |
  | 巨大ファイル・巨大寸法 | ヘッダーの寸法で事前判定（1 ギガピクセル超は拒否）。画面サイズへの縮小デコード（全展開しない、実測で確認） |
  | 異常なメタデータ | メタデータは表示用に上限付きで読む（実装予定）。ログに値を出さない |
  | シンボリックリンク / ジャンクション | プレビューはリンク先を読み取り専用で開く。保存時は `SafeFileWriter` がリンク自体を置き換えないよう、実体パスを解決してから書く（実装予定・要テスト） |
  | ネットワークパス / UNC | 許可。非同期・キャンセル可能・タイムアウト付き。UI スレッドで I/O しない |
  | デバイスパス（`\\.\`、`\\?\GLOBALROOT`） | 拒否（ファイルではない） |
  | クラウドプレースホルダー | 内容を読まない（読むとダウンロードが発生）。キャッシュ済みサムネイルのみ表示 |
  | 圧縮ファイル（ZIP 内など） | パスを持たない項目は `IShellItem` のストリーム経由で扱う（PoC は未対応で件数のみ記録） |
  | 特殊なファイル名（日本語、サロゲートペア、長いパス、末尾の空白・ドット） | `\\?\` 形式を含め .NET の Unicode パス API で扱う（日本語パスはテスト済み） |
  | 権限のないファイル | アクセス拒否を捕捉してエラー表示。昇格はしない |
  | 書き込み | すべて `SafeFileWriter`（一時ファイル → 検証 → アトミック置換）。墨消しなどは別名保存が既定 |
- 将来、解析を低権限 `Mavue.Worker` に隔離（AppContainer / 低 IL）。シェル拡張は既に OS 側でプロセス分離される。
- ログに文書内容・ファイルパス全体・EXIF/GPS・OCR テキスト・署名を出力しない（ファイル名はハッシュ化、またはオプトイン）。
- 署名画像は `%LOCALAPPDATA%\Mavue\Signatures\` に DPAPI (`ProtectedData`, CurrentUser) で暗号化保存。
- フォーム AutoFill 用プロフィール（氏名・住所等）も同様に DPAPI で暗号化保存。
- GPS 地図表示（SPEC §8）は外部地図サービスへ座標を送る可能性があるため、明示操作時のみ・送信内容を開示・オプトイン。オフライン地図も調査。
- キャッシュ: `%LOCALAPPDATA%\Mavue\Cache`。キー = ボリュームシリアル + FileId + サイズ + 更新時刻 + 描画設定。LRU + 容量上限（設定可能）。設定から全消去可能。
- 墨消しは「視覚的マスク」と明確に区別し、UI でも用語を分ける（`CLAUDE.md` §10）。

---

## 13. 重要な設計判断の記録 (ADR 要約)

| # | 判断 | 理由 | 状態 |
|---|---|---|---|
| ADR-1 | UI は WinUI 3 / Windows App SDK 2.5.1 / .NET 10 LTS | CLAUDE.md の既定スタック。.NET 10 は LTS (2028-11 まで) | 採用 |
| ADR-2 | Quick View は常駐の独立プロセス | 実測コールド起動 ~730ms（Debug）では即時表示にならない | 採用（プロトタイプで再計測） |
| ADR-3 | Space 検出は WH_KEYBOARD_LL + フォーカス判定 | 公式拡張ポイントが存在しない。代替手段なし | 採用（リスク: §4.2） |
| ADR-4 | シェル拡張は C++、CLR を持ち込まない | §2.1 | 採用 |
| ADR-5 | PDF は PDFium + QPDF + 独自墨消し | MuPDF は AGPL（組み込むと Mavue 全体が AGPL の条件に縛られ Apache-2.0 で提供できない）。PDFium 単体では暗号化書き込み・線形化不可 | 採用（ライセンス最終確認: DEPENDENCIES.md） |
| ADR-6 | 画像は WIC 優先 + 許容ライセンスのフォールバック | OS 拡張の有無が環境依存のため | 採用 |
| ADR-7 | HEIC のフォールバックデコーダー同梱は保留 | HEVC 特許ライセンスの法的確認が必要 | Investigating（機能自体は削除しない: OS 拡張経由で対応） |
| ADR-8 | 配布は MSIX、開発は非パッケージ実行 | Windows 11 上段コンテキストメニュー等にパッケージ ID が必要 | 再検討中（2026-10-04: MSIX のプレビュー ハンドラーの制約。§8） |
| ADR-9 | ペン入力は独自実装、InkCanvas は安定版待ち | WinUI 3 InkCanvas は 2.4/2.5 で Experimental | 採用（再評価予定） |
| ADR-10 | Quick View は「パネル」表示（非アクティブ、表示中のみ Topmost、他アプリ前面化で自動クローズ） | 実ユーザー入力でフック経由の前面化・z 順引き上げが不安定（QUICKVIEW-POC §3.2）。Topmost は権利不要の正規の仕組み | 採用（**ユーザーの最終承認待ち**: 「強制 Topmost は避ける」要件との関係） |
| ADR-11 | Shell COM（選択取得・サムネイル）は専用 STA スレッド | MTA では RPC_E_CANTCALLOUT_ININPUTSYNCCALL（実測） | 採用 |
| ADR-12 | E2E はホストを独立起動して計測し、最終判断はユーザー実操作で行う | ハーネスの子プロセスとして起動すると前面化が有利に歪む（実測） | 採用 |
| ADR-13 | 選択追従は Shell のイベント（DShellFolderViewEvents / DWebBrowserEvents2）を、メッセージループ付き専用 STA で受信 | ポーリング不要、Explorer 発行後 約 2 ms で切替（実測）。STA がメッセージを処理しないと Explorer を待たせるため専用ループ | 採用 |
| ADR-15 | Quick View の JPEG は WIC を直接使ってデコード（Mavue.Image）。ICC プロファイル付き・非 sRGB は WinRT の色管理付き経路 | WinRT 経路より 192 MP で 449 → 251 ms（実測）。EXIF 回転は Windows のデコーダーと画素一致をテストで保証 | 採用 |
| ADR-16 | Quick View は画像を拡大しない（既定）。表示領域の物理ピクセルでデコードし 1:1 表示。原寸表示は設定で選択 | 拡大表示で画質が悪いとのユーザー報告。デコードを画面ピクセルに合わせると縮小以外の再サンプリングが発生しない | 採用（ユーザー要望） |
| ADR-17 | 表示する画像は常に専用の複製（キャッシュの画像を XAML に直接渡さない） | XAML の画像ソース破棄でキャッシュ側の画像まで閉じられ、再表示で失敗（実測、ユーザー報告の不具合） | 採用 |
| ADR-18 | Quick View の画像領域は XAML のレイアウトではなく `AppWindow.ClientSize` と `GetDpiForWindow` から計算。表示サイズの設定は開くたびに設定ファイルを読み直す（その場の切替 UI は置かない） | 拡大率の違うモニターへ移るとき、配置後に Windows が DPI 変更でウィンドウを再リサイズし、レイアウトが古いままの値でデコードしていた（3 モニターで実測）。切替 UI はユーザー要望で設定画面に移す | 採用 |
| ADR-20 | Windows 11 上段メニューは `IExplorerCommand`（C++ の最小 DLL）+ 署名済み識別パッケージ。DLL は Explorer に `Mavue.QuickView.Host.exe --quickview` を起動させるだけで、ホストにはパッケージ ID を付けない | 代理プロセスから起動すると前面に出られず（TeraCopy で実測）、パッケージのコンテナ内で動く（VS Code issue）。Explorer に起動させると前面化の権利を持ちコンテナ外で動く（実測）。既存の `--quickview` → パイプ経路と表示・追従ロジックをそのまま使える | 採用（実装・実機確認済み） |
| ADR-19 | 右クリック「Mavue Quick View」は、まず HKCU の従来メニュー（SystemFileAssociations の動詞 → `--quickview` → 名前付きパイプ）で提供。常駐プロセスはそのファイルを選択中の Explorer ビューを読み、Space と同じ表示・追従に合流する | 上段メニューはパッケージ ID とネイティブ DLL が必要で、Build Tools（管理者インストール）待ち。動詞は 1 ファイル 1 プロセスで起動されるが、選択を読めば最初の 1 件で全体を表示できる（実測） | 採用（上段メニューは後続） |
| ADR-14 | 他の Space プレビューツールとの共存: 既定は Mavue 優先（フック再設置で先頭を維持）、設定で譲る | QuickLook は Space を流すため、後から起動した側が先に呼ばれ二重表示（実測 3/3） | 採用（既定値はユーザー確認事項） |
