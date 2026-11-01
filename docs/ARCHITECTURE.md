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
- Quick View は表示面・デコード・再生を共有し、段階表示（シェルのサムネイル → 本画像）・先読みキャッシュ・計測などの Quick View 固有の制御は Quick View ホストに残す（2026-10-06 から形式ごとの表示部品に分割: §4.7）。

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

### 4.7 Quick View の内部構成と Quick Preview 拡張（2026-10-06、Phase 0）

SPEC §3.1（Quick Preview 拡張）に向けて、1 ファイル 2,439 行に集中していた `QuickViewController` を責務ごとに分割した。
**動作は変えていない**（タイミングログの項目名・内容、Space → 表示の順序、選択追従、フック、段階表示、先読み、再生、ズームは同じ）。
例外は §4.7.6 の 4 点（無駄な処理の削除と既存の不具合の修正）。

```
Mavue.QuickView.Host
├─ QuickViewController        セッションの調整役: Space / 右クリックの依頼、選択追従（単一・複数・タブ）、フックで取るキー、
│                             項目の読み込みの流れ（ファイル情報 → 内容へ委譲）、再計画（リサイズ・DPI）、閉じる。形式で分岐しない
├─ Presentation/QuickViewPresence   ウィンドウの配置・前面化（panel 等）・最前面帯からの離脱・非表示、自動クローズの判定材料
├─ QuickViewSettingsStore     設定ファイル（開くたびに更新時刻を見て読み直し、PDF の選択を書き戻す）
├─ QuickViewFileActions       Mavue で開く・プログラムから開く・コピー（将来: 外へのドラッグ、共有）
├─ Content/                   表示部品（IPreviewContent、1 種類 1 インスタンス、重いものは初回使用時に作る）
│   ├─ RasterContent          WIC 画像: 段階表示・キャッシュ・先読み・GIF アニメ・PDF の画像表示（PDFium なし時）・ズーム/回転
│   ├─ SvgContent             SVG（Direct2D）
│   ├─ PdfDocumentContent     PDFium の文書表示（PdfSession・検索・サイドバー・プレゼンテーション）
│   ├─ MediaContent           動画・音声（Media Foundation）
│   ├─ UniversalContent       何でも何かを表示する（Phase 0 は従来のメッセージ。Phase 1 で情報カード）
│   ├─ SurfaceImageView       画像として描く内容の共通状態（ズーム・回転・表示中の大きさ・表示用ビットマップ）
│   ├─ PreviewInfoText        情報欄の文字列（メタデータ表示の境界）
│   ├─ TextContent            テキスト・コード（Phase 4。§4.7.12）。Text/TextPreviewer・TextLinesView・DocumentLineSource
│   ├─ MarkdownContent        Markdown を XAML で描画（Markdig）。Markdown/MarkdownRenderer・MarkdownView・MarkdownImages
│   ├─ HtmlContent            HTML を安全な WebView2 で表示。Html/HtmlBrowser（WebView2 の型に触れる唯一のクラス）
│   └─ PreviewContracts       IPreviewContent・能力インターフェース・IPreviewHost・PreviewServices
└─ Performance/
    ├─ ResidentResources      資源上限（性能段階）、非表示後のメモリ返却、異常終了時の自動再起動
    └─ NeighborPrefetcher     先読み（項目内 → 隣のファイル。`PreviewPrefetchPlan` に従い、それぞれのファイルの表示部品に任せる）

Mavue.QuickView
├─ Preview/ItemDisplayState       項目が画面に出したもの（内容・最終メッセージ・案内）。「読み込み中」・遅れたサムネイル・前の画像の消去の判定
└─ Resident/RestartArguments      異常終了後の再起動の引数（`--quickview` 以降を除く）

Mavue.Core
├─ Viewing/PreviewContentRouting   ファイル情報 → 表示部品の種類（唯一の振り分け。純粋関数）
├─ Viewing/PreviewResourcePolicy   性能段階と資源上限（High = PoC の実測値、Balanced、Low）
├─ Viewing/PreviewPrefetchPlan     先読みの順序（次 → 前）・対象（通常のファイル・段階の上限内）・先読みする部品（画像のみ）
├─ Archives/                        ZIP 等の安全上限（ArchiveLimits）、項目名の検査（ArchiveEntryName）、展開量の計測（DecompressionGuardStream）
└─ Text/                            文字コード判定・行索引・行の読み出し・構文の色付け・上限（Phase 4。§4.7.12。UI 型に依存しない）
```

#### 4.7.1 表示部品の契約

- `IPreviewContent`: `Kind`、`ShowAsync(PreviewItem, CancellationToken)`、`Reset(ContentReset, reason)`、`OnAreaChanged()`、`HasAuxiliaryWindow`。
  - `Reset` は**全部品に毎回**呼ぶ（冪等）。`Reload`（同じ項目の再読み込み: 再生だけ止める）、`NewItem`（ズーム・回転・ページを忘れ、文書を閉じる）、
    `Hidden`（キャッシュと表示用の画素も解放）。前の項目の状態が次の項目に残らないことを構造で保証する。
- 任意の能力はインターフェースで表す: `IViewTransformContent`（ズーム・回転）、`IPagedContent`（ページ）、`IPlaybackContent`（再生中なら一時停止）、
  `IAnimatedContent`（Enter のアニメ切替）、`ISelectionContent`（選択のコピー）、`IPrefetchingContent`（項目内・隣のファイルの先読み）。
  コントローラーは「表示中の部品がその能力を持つか」だけを見る（形式名で分岐しない）。
- 部品固有のウィンドウ操作（PDF のツール、メディアのキー）は部品自身が購読する。
- 部品からコントローラーへの要求は `IPreviewHost`（現在の依頼、段階表示の記録、情報欄、ページ操作、同じ項目の再表示、先読み開始）だけ。

#### 4.7.2 形式を追加する手順（拡張点）

1. 必要なら `FileFormat` と `FileFormatDetector` に形式を追加（先頭バイトで判定）。
2. `PreviewContentKind` に種類を追加し、`PreviewContentRouting.Choose` に 1 行の規則を足す（単体テストで固定）。
3. `IPreviewContent` を実装した部品を 1 つ作り、`PreviewContentSet` に登録する。重い依存（解析ライブラリ、WebView2 等）は
   部品の中で初回使用時に作る。.NET のアセンブリは初めて型が使われたときに読み込まれるので、常駐の起動コストは増えない。
4. 必要な能力インターフェースを実装する（ズーム・ページ・再生・先読み等）。上限は `PreviewResourcePolicy`、アーカイブは `ArchiveLimits` を使う。

| 今後の機能 | 追加する場所 |
|---|---|
| Text / Code / Markdown | `TextContent`（`PreviewContentKind.Text`）。色付けは部品の中で、見えている行から |
| Archive（ZIP） | `ArchiveContent`（`PreviewContentKind.Archive`）。中央ディレクトリの一覧、項目の読み出しは `DecompressionGuardStream`。ZIP 内の画像は `RasterContent` の描画を流用 |
| HTML | `HtmlContent`（WebView2 の安全境界は部品内に閉じる） |
| Universal Preview | `UniversalContent`（情報カード）。どの部品も表示できない場合の受け皿 |
| 動画の軽い操作 UI・ホイールでシーク・波形 | `MediaContent` |
| ピクセル Inspector・Ctrl なしのホイールズーム | `SurfaceImageView` / `RasterContent`（入力の意味は表示中の部品が決める） |
| フィルムストリップ・Pin | コントローラー（ナビゲーション）+ UI 部品。Pin は「選択変更を適用しない」状態をコントローラーが持つ |
| 外へのドラッグ・共有 | `QuickViewFileActions` |
| EXIF Inspector・共有前チェック | `PreviewInfoText` と専用パネル（読み取りは本体の `FileInformation` と共通） |

#### 4.7.3 別プロセス化の境界

- 各部品は「データを作る層」と「表示する層」に分かれている。データ層（`ImageDecoding`・`PdfRendering`・`GifAnimationReader`・`SvgDimensions`・
  `FileFacts`、今後の ZIP 一覧・テキスト索引・波形・知覚ハッシュ）は**パスと上限と CancellationToken を受け取り、画素や一覧などのデータを返す**。UI 型に依存しない。
- 重い処理（類似画像検索、長い音声の波形）や危険度の高い解析は、このデータ層を `Mavue.Worker.exe`（使うときだけ起動、ジョブ オブジェクトでメモリ・CPU 上限、
  将来 AppContainer / 低 IL）に移す。表示部品の `ShowAsync` から見た契約は変わらない。
- 表示の速さが最優先の画像・PDF の 1 枚目は、従来どおり常駐プロセス内で処理する（Space → 表示の遅延を増やさない）。

#### 4.7.4 性能（Always Ready, Never Always Working）

- 非表示中に動くものは増やしていない（タイマーなし）。性能段階は Quick View を開くときに再評価し（システム指標 1 回と保持済みの値）、
  省エネの変化は Windows のイベントで受け取る。
- 性能段階（`PreviewResourcePolicy`）: High（従来値: キャッシュ 192 MB、前後 2 件、64 MB・4,000 万画素まで先読み、GIF アニメ 800 万画素）、
  Balanced（128 MB、2,400 万画素）、Low（64 MB、次の 1 件のみ、32 MB・1,200 万画素、GIF 400 万画素）。
  Low: メモリ 6 GiB 未満・論理プロセッサ 2 以下・省エネ。Balanced: 12 GiB 未満・4 以下・リモート セッション。開発機は High（従来と同じ）。
- 閉じると全部品の `Reset(Hidden)` でキャッシュ・表示用画素・文書・プレーヤーを解放し、1 秒後に GC（従来どおり）。

#### 4.7.5 セキュリティ

- 異常終了・応答なしの後、Windows（`RegisterApplicationRestart`、60 秒以上動いたプロセスのみ。再起動ループを防ぐ）が常駐プロセスを再起動する。
  元の引数は引き継ぐが、表示するファイル（`--quickview` とそれ以降）は引き継がない（落ちたファイルを自動で開き直さない）。引数の組み立てとクォートは
  `Mavue.QuickView.Resident.RestartArguments`（`CommandLineToArgvW` で読み戻して一致することを単体テストで確認。1,023 文字を超える場合は引数なしで再起動）。
- アーカイブ（Phase 2 で使用）: ディスクに展開しない。一覧は中央ディレクトリから（項目数・ディレクトリの大きさに上限）。項目は実際に展開したバイト数を数え、
  項目ごと・アーカイブ全体・圧縮率（4 MiB 以降で 200 倍超）で停止。名前は絶対パス・`..`（と `.. `・`...` など点と空白だけの名前。Windows が末尾の点・空白を落とすため）・ドライブ/ADS の `:`・制御文字・予約デバイス名・長さ・深さを検査。
  入れ子のアーカイブは開かない。上限は `ArchiveLimits`（既定値はコードに記載）。
- 外部の起動・通信・スクリプト・Mark-of-the-Web の扱いは SPEC §3.1 の要件 4 に従う（PDF のリンクは従来どおり http/https/mailto のみ、クリック時のみ）。

#### 4.7.6 分割に伴う変更点（動作が変わるもの）

1. PDFium の文書表示中に、Windows.Data.Pdf で前後ページを先読みしていた（文書表示はその画像を使わない＝無駄な描画とキャッシュ消費）。先読みは表示中の部品が
   自分の分だけ行うようにしたため、文書表示ではこの描画をしない。
2. 隣の SVG を WIC でデコードしようとして失敗していた先読み（SVG は WIC で読めない）をしない。
3. 矢印キー等で次の項目へ移ったとき、表示できない旨のメッセージ（非対応・壊れたファイル・ファイルではない）が 120 ms 以内に出ると、
   「前の画像を消す」処理がそれを「読み込み中」で上書きしていた。さらに（分割前から）画像として扱うファイルでは、デコーダーの「非対応・破損」が
   シェルのサムネイル取得より先に終わると、後から届いた「サムネイルなし」が「読み込み中」で上書きしていた（実機で毎回発生: 2026-10-29 の E2E
   `message-not-replaced`）。クラウドのみのファイルでも同じく「読み込み中」が残った。項目ごとの表示状態を `ItemDisplayState`（`Mavue.QuickView.Preview`）に
   まとめ、最終メッセージを出した時点で記録し、その項目のサムネイル・「読み込み中」・前の画像の消去が上書きしないよう修正（クラウドの案内はサムネイルのみ許可）。
4. 動画・音声・メッセージを表示したとき、前の画像の表示用ビットマップをすぐ解放する（従来は次の画像表示か非表示まで保持）。

#### 4.7.7 Phase の順序（2026-10-06 承認、ZIP の優先度を上げた）

| Phase | 内容 |
|---|---|
| 0 | 本節の分割・振り分け・資源上限と性能段階・自動再起動・アーカイブの安全上限（完了。2026-10-29 にテストと回帰確認: TESTING.md「Quick Preview Phase 0 のテストと回帰確認」） |
| 1 | Ctrl なしのホイールズーム（設定）・吸着、ピクセル Inspector、フィルムストリップ、Pin（現在の Quick View を固定）、外へのドラッグ（既定はコピー）、Universal Preview（2026-10-29: ホイールズーム以外の 5 項目を実装・テスト。§4.7.8） |
| 2 | ウィンドウのデザイン（2026-10-29 利用者の指示で ZIP と入れ替え。前提と改善点は §4.7.9） |
| 3 | ZIP（一覧・ZIP 内画像・前後移動）（2026-10-30 実装。§4.7.10）。利用者の指示で Phase 4 の動画・音声もまとめて実装（§4.7.11） |
| 4 | 動画（軽い操作 UI、ホイール ±5 秒、Shift+ホイール ±30 秒）、音声の波形（2026-10-30 に Phase 3 とまとめて実装。§4.7.11）。2026-11-02: 利用者の指示で、Phase 5 のテキスト・コード・Markdown と Phase 6 の HTML をまとめて Phase 4 として実装（§4.7.12） |
| 5 | （テキスト・コード・Markdown は Phase 4 で実装済み） |
| 6 | EXIF Inspector、共有前チェック・メタデータを除いたコピー（HTML は Phase 4 で実装済み） |
| 7 | 類似画像検索（別プロセス） |
| 8 | 統合テスト・性能テスト・長時間テスト |

#### 4.7.8 Phase 1 の設計（2026-10-29、実装前に整理）

利用者の指示（2026-10-29）で、Phase 1 のうち次の 5 項目を先に実装する: ピクセル Inspector、フィルムストリップ、Pin、外へのドラッグ、Universal Preview の基盤。
Ctrl なしのホイールズーム（F03.31）は今回の対象外（Planned のまま）。既存の Ctrl+ホイール（カーソル中心）は変更しない。

共通の約束（SPEC §3.1 要件 1〜7）:

- 常駐中（非表示）は何もしない。新しい処理はすべて「Quick View が表示中」かつ「その機能が表示中」のときだけ動き、項目の切り替え・非表示で取り消す。
- メインのプレビューを最優先する。フィルムストリップの読み込みは表示中の項目が出た後に始め、メインのサムネイル取得とは別のスレッドで行う。
- 重い処理（原寸タイルのデコード、サムネイル抽出、フォルダー内の件数）は UI スレッドの外で、取り消し可能・上限付き。
- 画像を邪魔しない: フィルムストリップ・Inspector は既定で非表示（ボタンとキーで切替、選択は設定に保存）。Inspector は小さなパネルを画像の隅に重ねる。
- 判断ロジックは純粋関数として Core / Mavue.QuickView に置き、単体テストで固定する。ホストは UI と配線だけ。

| 機能 | 純粋ロジック（テスト対象） | データ層 | ホスト（UI と配線） | 拡張点 |
|---|---|---|---|---|
| ピクセル Inspector（F03.29） | `Core.Viewing.PixelInspection`: 画面上の位置 → 表示中の画像の画素（ズーム・表示回転の逆変換）→ ファイル内の画素（EXIF 回転の逆変換）、タイルの範囲、HEX/RGB/Alpha の表記 | `Image.Wic.WicPixelReader`: 原寸のタイル（既定 256×256）1 枚だけを WIC で読み、BGRA（ストレート α）で返す。スケーラーなし・色変換なし（ファイルの値） | `Content/PixelInspector`: 表示面のポインター追跡、隅のパネル、タイル 1 枚のキャッシュ、移動の間引き。表示中の部品が `IPixelSourceContent` を持つときだけ有効（現在は `RasterContent`。GIF アニメ再生中・PDF・SVG・動画は対象外） | PDF ページ・SVG は `IPixelSourceContent` を実装すれば対応できる。色管理後の値の表示は表記側に追加 |
| フィルムストリップ（F03.28） | `QuickView.Preview.FilmstripWindow`: Explorer のビュー順の範囲（現在の項目の前後 N 件）、再読み込みが要る位置、表示する項目とその番号 | `ExplorerViewSession.ReadItems`（`IFolderView2` の範囲取得）、`SelectItem`。サムネイルは専用の STA スレッドで Windows のキャッシュ → ローカルのファイルだけ抽出（クラウドのみのファイルは抽出しない）→ 種類のアイコン | `Presentation/FilmstripBar`: 情報バーの上の帯（仮想化した横並び）、見えている項目だけサムネイルを要求、件数上限付きのサムネイル キャッシュ（非表示で破棄） | 複数選択時は選択された項目を表示。比較表示・Pin 一覧への発展は同じ帯の項目の種類を増やす |
| Pin（F03.45） | `PreviewNavigator.IsPinned`: 固定中は Explorer の選択変更を適用しない（ナビゲーターの状態の一部） | — | コントローラー: 固定中は他のアプリへの切替・Explorer の終了で閉じない、フックで Explorer のキーを奪わない（←/→・PageUp/PageDown）。Space・Esc・閉じるボタンでは閉じる。フィルムストリップのクリックは固定中も Quick View 内で移動する（Explorer の選択は変えない） | 複数 Pin: 固定したセッション（ナビゲーター＋表示部品＋ウィンドウ）を 1 単位とし、Space で新しい Quick View を開く形へ拡張できる |
| 外へのドラッグ（F03.44） | `Core.Viewing.DragOutPolicy`: ドラッグできる項目（通常のファイルのみ）と許可する操作（コピーのみ。移動は許可しない） | — | `QuickViewFileActions.PrepareDrag`: WinUI の `DragStarting` でファイルを渡す（読み取り専用）。ドラッグ元は情報バーのファイル名の領域（常に）と、パンできないときの画像 | 共有（Windows の共有）・メタデータを除いたコピー（Phase 6）は同じ場所に追加 |
| Universal Preview（F03.40） | `Core.Viewing.UniversalFileInfo`: 種類の分類（アイコン）、MIME、Mark-of-the-Web（`Zone.Identifier` の解析）、表示する項目。振り分け: 内容が判定できず拡張子が文書・アーカイブ・プログラム等なら `PreviewContentRouting` が直接 Universal へ | `UniversalFileInfo.Read`（System.IO と ADS の読み取りのみ。ファイルの内容は読まない）、種類の名前は `AssocQueryString` | `Content/UniversalContent`: 情報カード（種類のアイコン・名前・種類・サイズ・日時・MIME・属性・Web から取得）。画像として読めなかったファイルもこのカードにメッセージ付きで表示。フォルダーは項目数を上限付きで後から数える | テキスト・ZIP 等の部品は、表示できないときにこのカードへ戻す |

実装時の決定と結果（2026-10-29）:

- 表示部品からカードへの切り替えは `IPreviewHost.ShowInfoCardAsync`（`RasterContent` の「非対応・破損」と、コントローラーの例外時）。メッセージは従来どおり即座に出し（`ItemDisplayState.MarkMessage`）、カードが届いたら置き換える。
- Pin 中の Space（Explorer）は従来どおり閉じる（「Space で開閉」を崩さない）。Explorer 上の Esc は Explorer のもの（閉じない）。
- 新しいボタンはキーボード フォーカスを取らない（Space/Esc が Quick View に残る）。キー: F（フィルムストリップ）・I（Inspector）・P（Pin）は Quick View がアクティブなときだけ。
- E2E で見つけた不具合: Quick View のボタン（フィルムストリップ・Pin）をクリックすると、前面ウィンドウとして Quick View の子ウィンドウが通知され、
  「他のアプリに切り替わった」と判定して閉じていた。前面ウィンドウのルートが Quick View 自身か、同じプロセスのウィンドウなら閉じないよう修正。
- 情報カードの値は UI オートメーション名（「項目名: 値」）で読み上げる。後から変わる値（フォルダーの項目数）は名前も更新する。
- 外へのドラッグ（2026-10-29 実機確認で変更）: ドラッグ開始はホストが判定する（左ボタンで押してシステムのドラッグ距離 SM_CXDRAG/SM_CYDRAG を超えたら
  `UIElement.StartDragAsync`）。画像は拡大してパンできるときはパンを優先。データは WinUI の `DataPackage`（読み取り専用の StorageItems、許可はコピーのみ）。
  Explorer と同じシェルのデータ オブジェクト＋`SHDoDragDrop` も試したが、WinUI はプロセス全体で mouse-in-pointer を有効にする（実測 `IsMouseInPointerEnabled = true`）ため
  ドラッグ ループがボタンを離したことを受け取れず使えない。別プロセスで行う案は起動に約 1 秒かかり、ボタンを離した後の次のマウス移動でドロップされる危険があるため不採用。
  結果: Explorer・Edge・WinForms アプリ・Word（Office サインイン時）へはコピーされる。ペイント（Windows 11）は WinUI のドラッグを受け付けない
  （Explorer からのドラッグは受け付ける。コピー/移動/リンクをすべて許可しても受け付けないことを実験で確認）。残課題。

#### 4.7.9 Quick Preview の UI 構成と Phase 2（デザイン）の改善点（2026-10-29 整理）

Phase 2 の前提（利用者の指示）: 画像を主役にする。タイトルバーと背景を macOS Preview のように統一する。Windows 11 の Mica / Acrylic は控えめに使う。
透明感で画像の色を邪魔しない。

現在の構成（Phase 1 時点）:

```
┌ システムのタイトルバー: 「<ファイル名> – Mavue クイックビュー」 ─────────── □ × ┐   OverlappedPresenter（最小化なし）、アプリのテーマに追従
├ Row0 SearchHost（PDF の検索バー。通常は非表示） ─────────────────────────┤
├ Row1 ┌SidebarHost(PDF 232)┐┌ SurfaceHost: ViewerSurface（余白 12、画像/動画/文書/メッセージ） ┐ │   背景 #1E1E1E（ダーク）/#F3F3F3（ライト）、単色
│      │                    ││  + CardHost（情報カード）+ InspectorPanel（左下/右下に重ねる）  │ │
├ Row2 FilmstripHost（88 DIP、既定オフ） ─────────────────────────────────┤   #181818
├ Row3 InfoBar: [ファイル名＋情報（ドラッグ元）]  [PDF ツール][ページ][－ % ＋ 回転][帯 Insp Pin][開く][Mavue で開く] ┤   #181818
└──────────────────────────────────────────────────────────┘
ウィンドウ: 常駐中に 1 枚だけ作成し、非表示は DWM のクローク。Explorer のモニターの作業領域の 70%×75%。Mica なし（最初のフレームの速さと、E2E の「画像が出た」判定の基準色 #1E1E1E のため）。
```

改善点（スクリーンショットと実装からの整理）:

| # | 現状 | 改善の方向 |
|---|---|---|
| 1 | タイトルバー（上）と操作（下）が分かれ、ファイル名が 2 か所に出る | `ExtendsContentIntoTitleBar` でタイトルバーと操作を上部の 1 本のバー（約 40〜48 DIP）に統合（macOS Preview / Quick Look と同じ配置）。ファイル名は 1 か所（ドラッグ元も兼ねる）。キャプション ボタンは背景を透明にして同じ面に置く。ボタン部分は `InputNonClientPointerSource` でクリック可能領域に |
| 2 | 面の色が 3 種類（システムのタイトルバー・#1E1E1E・#181818）で統一感がない | 「操作の面」（上部バー・フィルムストリップ）と「画像の舞台」の 2 層に整理。操作の面だけに Mica（Base Alt を第一候補）を控えめに使い、画像の舞台は不透明なニュートラル色のまま（壁紙の色が画像の周りに混ざらない） |
| 3 | 操作が最大 13 個並ぶ（PDF）。アイコンと文字ボタン（Mavue で開く）が混在。フィルムストリップのアイコン（E786）が動画風で意味が伝わりにくい | 主要操作だけをアイコンで（ズーム・回転・Pin・フィルムストリップ・Inspector・共有/開く）、他は「…」メニューへ。アイコン 16px・当たり判定 32×32・ツールチップにショートカット。アイコンを見直す |
| 4 | 下部の情報行（種類・寸法・サイズ）が常に高さを使う | 上部バーの副題（小さな文字）に統合し、画像の舞台を広げる。ページ番号などは内容に応じて上部バーへ |
| 5 | 透明部分がウィンドウの背景色で塗られる（半透明の青が濃紺に見える） | 画像の範囲内だけに控えめなチェッカーボード（SPEC §5「Transparency checkerboard」と共通）。舞台の色はそのまま |
| 6 | 色をコードで直接指定（ライト/ダーク切替は `ApplyTheme`）。ハイコントラスト未対応 | テーマ リソース（ThemeDictionaries: Default/Light/HighContrast）に移す。ハイコントラストでは背景効果なし・システム色 |
| 7 | Inspector パネル・情報カード・フィルムストリップが個別のスタイル | 共通のカード スタイル（角丸・境界線・文字サイズ）に揃える。画像の上に重ねるもの（Inspector）はぼかしを使わず半透明の単色（画像の見え方を変えない） |
| 8 | ボタンはタブ移動できない（Space/Esc を Quick View に残すため） | F6 で上部バーへフォーカス、Esc で戻る。Space/Esc の意味は変えない |
| 9 | ウィンドウは常に作業領域の 70%×75%。小さい画像は大きな暗い枠の中に浮く | （要判断）画像の縦横比と大きさに合わせてウィンドウを決める（macOS Quick Look 方式、上限・下限あり）か、現状の固定サイズか |

背景効果の扱い（Mica / Acrylic を控えめに）:

- 画像の舞台の後ろには背景効果を置かない（色の判断を妨げない）。Acrylic（ぼかし）は使わないか、メニュー等の一時的な面だけ。
- 透明効果オフ・省エネ・リモート デスクトップ・ハイコントラスト・性能段階 Low では単色に切り替える（Mica は OS 側でも自動で単色になる）。
- 背景効果の準備は事前描画（prewarm）で済ませ、Space → 表示の遅延を増やさない。Phase 2 の受け入れ条件: E2E の表示 21/21 と Space → 表示呼び出し・最初のフレーム・フル品質の時間が現状と同等、常駐時のメモリが同等。
- E2E は画像の舞台の色（#1E1E1E）を基準に「画像が出た」を判定しているため、舞台の色を変える場合はハーネスも同時に更新する。

利用者の判断が必要な点: ウィンドウの大きさ（固定か画像に合わせるか）、チェッカーボードの既定（オン/オフ）、下部情報行を残すか、Mica と Mica Alt のどちらを基調にするか。

決定（2026-10-30）: ウィンドウは**固定サイズ**（作業領域の 70%×75%）、チェッカーボードは**既定オン**（利用者）。下部情報行は上部バーへ統合、背景は **Mica（Base）**
（壁紙の色の影響が Mica Alt より小さい。推奨どおりで進めた）。

Phase 2 の実装（2026-10-30）:

```
┌ Windows のタイトルバー（ファイル名。カスタマイズしない） ─────────────── □ × ┐ ┐ Mica（同じ面に見える）
├ ツールバー（44 DIP）: [📄 種類・寸法・サイズ（ドラッグでコピー）]   [PDF ツール│][◀ 3/10 ▶│][－ 100% ＋ 回転│][帯 スポイト 固定│][Mavue で開く][…] ┤ ┘
├ PDF の検索バー（必要な時だけ） ─────────────────────────────────────────┤
├ 画像の舞台（不透明 #1E1E1E / #F3F3F3）: サイドバー｜表示面＋情報カード＋Inspector（角丸 8 の不透明カード、ぼかしなし） ┤
└ フィルムストリップ（88 DIP、Mica。既定オフ） ─────────────────────────────┘
```

- **タイトルバーは Windows 標準のまま**（ファイル名を表示）。その下のツールバーと Mica で 1 つの帯に見せる。下部の情報行はツールバーの左（ファイルの
  アイコン＋種類・寸法・サイズ。ドラッグ元）へ移し、ファイル名の重複をなくした。
  - 計測（2026-10-30、Release、Space → 表示の内訳の show-call → shown の中央値）: 標準のタイトルバー＋Mica 3.6〜3.8 ms（RC 0.1.0 も 3.7 ms）、
    `ExtendsContentIntoTitleBar` 13.8 ms、キャプション ボタンの色の変更（`AppWindowTitleBar.Button*Color`）13.5 ms、タイトルバーなし
    （`SetBorderAndTitleBar(true, false)`）10.8 ms。フレームを変える方法はどれも表示のたびに約 10 ms 遅くなるため採用しない
    （「Space → 表示の遅延を悪化させない」SPEC §3.1 要件 1）。Mica は遅延に影響しない。
- 画像領域の計算は「クライアント領域 − ツールバー 44 − 検索 − フィルムストリップ − 余白」。E2E の「計算した領域と XAML のレイアウトが一致」で確認。
- 色: ウィンドウのテーマ辞書（Default=ダーク/Light/HighContrast）。コードでの色指定をやめ、`ApplyTheme` はテーマの切替だけ。
  ハイコントラストの切替は `UISettings.ColorValuesChanged` で受ける（`AccessibilitySettings.HighContrastChanged` は CoreWindow のないアプリで購読できず、
  0x80070490 で起動時に例外。実機で発見し修正。未処理例外の記録に HRESULT と呼び出し元のメソッド名を追加）。
- 背景効果: `MicaBackdrop`（Kind=Base）を起動時に設定（事前描画に含まれる）。性能段階 Low・ハイコントラストでは `SystemBackdrop = null` で単色。
  Quick View は多くの場合アクティブにせず表示するため、Windows の仕様で Mica は非アクティブ表示（単色に近い）。壁紙なしの本機ではバーは #202020、
  舞台は #1E1E1E（実測）。計測用に `--no-backdrop`。
- 市松模様: `ViewerSurface` の `TransparencyGrid`（行ごとに繰り返しグラデーションの矩形 1 つ。セル 8 DIP）。透明になり得る形式・明示的な画像の大きさ・画像が
  設定済みのときだけ表示（不透明な画像では作らない、画像より先に模様だけが出ることはない）。「…」メニューで切替、設定 `showTransparencyGrid`（既定 true）。
- キーボード: F6 でツールバーの最初のボタンへ（その間だけタブ移動可、Tab で循環）、Esc で内容へ戻る（閉じない）。Space/Esc の通常の意味は変えない。
- アイコン: フィルムストリップ E7AA（写真の束）、Inspector EF3C（スポイト）、固定 E718 → 固定中 E841（塗り）、「Mavue で開く」E8A7、その他 E712、
  ファイル E7C3。「プログラムから開く」は「…」メニューへ。
- 未確認: ライト テーマ・ハイコントラストの実機表示（利用者のシステム設定を変えないため）、壁紙のある環境での Mica の見え方。

#### 4.7.10 ZIP Preview（Phase 3、2026-10-30）

展開せずに中身を見る。SPEC §3.1 要件 5（中央ディレクトリから一覧、項目はオンデマンド、名前とサイズを信用しない、安全に表示できなければ理由を示して
Quick View は動き続ける）に従う。

```
Mavue.Core.Archives（データ層: パス・上限・CancellationToken → 一覧・バイト列。UI 型に依存しない＝将来 Mavue.Worker.exe へ移せる）
├─ ZipDirectory      末尾 64 KiB から終端レコード → ZIP64 ロケーター/終端 → 中央ディレクトリだけを読む。項目数・ディレクトリの大きさは
│                    読む前に ArchiveLimits と比較。先頭に別データがある ZIP（自己解凍形式）はずれを補正。分割 ZIP は「分割の一部」として拒否
├─ ZipEntryReader    1 項目をメモリへ展開（ディスクに書かない）。暗号化・未対応の圧縮方式（stored/deflate 以外）は理由付きで拒否。申告サイズが
│                    上限超・申告の圧縮率が異常なら読む前に拒否。実際の展開量は DecompressionGuardStream で数え、最後にサイズと CRC-32 を照合
├─ ArchiveTree       フォルダー構造（暗黙のフォルダーを補う）、フォルダー優先・自然順（2 < 10）、ZIP 内画像の列（自然順）、集計（警告用）
├─ ArchiveEntryName  名前の検査（絶対パス・..・ADS・制御文字・予約名・長さ・深さ、**双方向制御文字**（"photo[U+202E]gpj.exe" 型の偽装）を追加）と表示用の置換
└─ Crc32             slice-by-8

Mavue.QuickView.Host/Content/ArchiveContent（表示部品、PreviewContentKind.Archive）
├─ 一覧モード        1 フォルダーずつ ListView（仮想化、行は ContainerContentChanging で埋める）。見出し: 名前・件数・展開後/圧縮時の合計（申告値）、
│                    「画像を表示（N 枚）」。警告: インターネットから取得（Mark-of-the-Web）・安全でない名前・異常な圧縮率・暗号化・未対応方式・入れ子
├─ 画像モード        ZIP 内画像を 1 件ずつ読み、ヘッダーの寸法から表示サイズを決めてデコード（WinRT BitmapDecoder、色管理あり）。ズーム・回転は通常の画像と同じ
│                    （SurfaceImageView を共有）。前後の画像は性能段階の範囲で先読み（読み取り量はアーカイブ単位で MaxTotalBytes まで）、表示済みはキャッシュ
└─ キー              一覧: Enter/ダブルクリックで開く、Backspace で上へ、Space/Esc は閉じる。画像: PageUp/PageDown（Explorer から、フック）・ホイール・
                     ページ ボタン・←/→/Home/End（Quick View がキーボードを持つとき）で前後、Backspace で一覧へ
```

- **対象**: 拡張子 .zip と .cbz（`FileFormat.Zip`）。Office 文書・JAR・EPUB 等の ZIP 形式のファイルは従来どおり情報カード（中身の署名では判定しない）。
  .cbz は最初から画像モード。クラウドのみのファイルは読まない（一覧は末尾にあるため読むとダウンロードされる）→ 情報カード。
  `ViewerFormats` には加えない（ZIP の関連付け・Explorer のプレビュー/サムネイル登録は行わない。Space は全ファイルで動作）。
- **名前の文字コード**: UTF-8 フラグ・Info-ZIP Unicode Path（CRC 一致時）を優先。フラグのない名前は、全件が UTF-8 として正しければ UTF-8
  （macOS 等）、次に OEM コードページ（日本語 Windows は CP932。Explorer と同じ）で全件読めればそれ、最後に CP437。
- **安全**: ディスクに書かないので Zip Slip は起きない（名前は表示にだけ使い、危険な名前は最上位に全体名で警告付き表示、画像列にも入れない）。
  入れ子のアーカイブは開かない。ZIP 内からは実行・外部起動・通信をしない。展開爆弾は申告値で一覧に警告、表示時に読む前に拒否、申告を偽った
  場合も実測で停止（単体テスト）。上限は `ArchiveLimits`（項目 20 万・ディレクトリ 64 MiB・1 項目 256 MiB・圧縮率 200 倍（4 MiB 以降））。
  利用者が開く 1 件ごとに 1 項目の上限、Quick View が自分で行う先読みはアーカイブ単位の合計（1 GiB）で打ち切る。
- **性能**: Space → 表示は「読み込み中」→ 一覧。一覧の読み取りは別スレッド（取り消し可）。行は表示中の分だけ作る。最後に読んだ項目のバイト列
  （64 MiB 以下）だけ保持し、ズーム・回転・リサイズで再展開しない。閉じる・別ファイルでセッション・キャッシュを解放。
- **計測項目**（タイミング ログ）: `archive-listed`（件数・警告数・文字コード・MotW ゾーン・一覧の所要時間）、`archive-list`、`archive-folder`、
  `archive-image`（位置・方式・サイズ）、`full-set`（`archiveIndex`）、失敗は `full-skipped` の reason（`archive-Damaged` 等、`entry-limit-*`、`entry-Encrypted` 等）。
- **未対応**（残課題）: ZIP 内の画像以外の内容（テキスト等は Phase 5 の部品ができたら同じ仕組みで表示可能）、暗号化 ZIP のパスワード入力、
  Deflate64/LZMA/bzip2/Zstandard、7z/RAR/TAR（形式ごとに読み取り部を足す。`ArchiveTree` 以降は共通）、ZIP 内の項目のドラッグ/コピー、
  ZIP 内画像のフィルムストリップ。

#### 4.7.11 動画・音声の軽い操作 UI・ホイールでシーク・波形（Phase 4 分、2026-10-30）

- **軽い操作 UI**（`MediaOverlay`）: Quick View では MediaTransportControls を使わず（`ViewerSurface.UseOwnMediaControls`。本体は従来の操作バーのまま）、
  下部に 1 本のバー（再生/一時停止・現在位置・シーク スライダー・長さ・ミュート）。動画の再生中はポインターが 2.5 秒止まるとバーが消え、
  移動・ホイールで戻る（映像を邪魔しない）。ボタンはキーボード フォーカスを取らない（Enter・Ctrl+矢印・Space・Esc の意味を保つ）。最初のメディアで作る。
- **ホイールでシーク**: 動画・音声の上でホイール 1 段 ±5 秒（上＝進む）、Shift+ホイール ±30 秒、長さの範囲に収める。タッチパッドの細かい量は
  1 段分たまってから（`MediaControlMath.WheelSeek`）。シークは常に 1 件: 実行中の要求は最新だけを保持し、完了（`SeekCompleted`）で次を実行。
  完了が届かない場合も 1 秒で解除（`MediaSession.RequestSeek`）。Ctrl+ホイールは従来どおり（メディアではズームなし）。
- **音声の波形**（`AudioWaveformReader`、Mavue.Viewer）: Media Foundation の Source Reader（再生と同じデコーダー）で float PCM に変換し、
  1,024 区間のピーク値だけを保持（メモリは区間数に比例、ファイルの長さによらない）。20 秒を超える音声はまず 128 か所を少しずつ読む概形
  （1 秒未満）→ 先頭から全体を読んで精密化（0.25 秒ごとに描画を更新）。2 時間を超える音声は概形のみ。再生開始後に優先度の低い専用スレッドで
  実行し、別ファイル・閉じるで取り消し。描画は WriteableBitmap（2 px の棒、再生済み部分は明るい色を位置でクリップ）。クリック/ドラッグでシーク。
  別プロセス化（長い音声）は Mavue.Worker.exe ができた段階で（§4.7.3 の境界: パス・長さ・取り消し → ピーク配列）。
- 位置の表示は再生中のファイルがある間だけ 200 ms ごとに更新（非表示・別ファイルで停止。常駐中は何も動かない）。
- 計測項目: `media-wheel-seek`（stepMs・targetMs）、`media-seek`（slider/waveform）、`media-mute`、`media-command` に source（key/button）、
  `audio-waveform`（exact/coarse/unavailable・所要時間）。

#### 4.7.12 テキスト・コード・Markdown・HTML（Phase 4、2026-11-02）

利用者の指示（2026-11-02）: Markdown（XAML ネイティブ描画）、HTML（安全な WebView2 構成）、コード（色付け）、大きなテキストの負荷対策、
Universal Preview との自然な振り分けをまとめて実装する。HTML は外部通信・外部リソース・危険なスクリプト実行を禁止し、外部アプリの起動や勝手な
ダウンロードをしない。巨大ファイルのメモリ・CPU 消費を制限し、読み取り専用で扱う。Phase 2 の UI を共通利用し、必要になるまで解析・描画しない。

```
Mavue.Core.Text（データ層: パス・上限・CancellationToken → 行・トークン。UI 型に依存しない＝将来 Mavue.Worker.exe へ移せる）
├─ TextEncodingDetector  先頭 64 KiB から: BOM（UTF-8/16/32）→ BOM なし UTF-16（1 バイトおきの 0 と、読んだ結果に制御文字が少ないこと）
│                        → ISO-2022-JP（エスケープ列）→ 正しい UTF-8 → Shift_JIS / EUC-JP（かな・漢字の割合で採点）→ システムの ANSI コード ページ。
│                        0 バイト（UTF-16 以外）や制御文字 2 % 超はバイナリ。LooksLikeText は形式判定と同じ 1 回のヘッダー読み取りで使う
├─ TextLineIndex         行頭のバイト位置の「チェックポイント」だけを持つ（16 行ごと、または前のチェックポイントから 64 KiB 以上離れた行頭）。
│                        12 バイト/チェックポイント。LF・CRLF・単独 CR をコード単位で探す（UTF-16 の文字の中の 0x0A を改行と誤らない）。
│                        1 MiB ずつ読み、1 回目の後と約 4 MiB ごとに進捗を通知。上限（バイト数・チェックポイント数）で打ち切り
├─ TextDocument          読み取り専用・共有モードで開く。任意の行範囲を最寄りのチェックポイントから読み、デコード・タブ展開（4）・制御文字の可視化
│                        （␛ など）・双方向制御文字（U+202A–202E、2066–2069）を <U+202E> と表示（"Trojan Source" 型の見せかけを防ぐ）・
│                        長い行は 10,000 文字で切る。色付けの状態は 128 行ごとに覚え、最寄りの状態から（5,000 行以内）続けて色付け
├─ Syntax/               自作の行単位レキサー（状態を次の行へ渡す）。CodeLanguage（C 系 40 言語余り: 定義データで記述）、MarkupLanguage
│                        （XML/HTML/XAML）、CssLanguage、YamlLanguage、IniLanguage（INI/TOML）、MarkdownSourceLanguage、DiffLanguage、LogLanguage。
│                        SyntaxLanguages: 拡張子・既知のファイル名（Makefile、Dockerfile、.gitignore、CMakeLists.txt…）→ 言語
├─ TextFileTypes         名前からテキスト・Markdown・HTML を判定（拡張子・既知のファイル名・README/LICENSE 等）
├─ HtmlCharset           HTML の文字コードの宣言（BOM → <meta charset>（既知の名前のみ）→ 判定結果）
└─ TextPreviewLimits     性能段階ごとの上限（下表）

Mavue.QuickView.Host/Content
├─ TextContent        テキスト・コード（PreviewContentKind.Text）。TextPreviewer に任せる
├─ MarkdownContent    Markdown（.Markdown）。Markdig で解析 → MarkdownRenderer で XAML。ソース表示は TextPreviewer
├─ HtmlContent        HTML（.Html）。HtmlBrowser（WebView2）。ソース表示・フォールバックは TextPreviewer
├─ Text/TextPreviewer       文書を開く → 索引を低優先度スレッドで作る → 最初の 1 MiB の索引で 1 画面目を表示 → 情報欄・注意書きを更新
├─ Text/TextLinesView       仮想化した行表示（見えている行の TextBlock だけ。行番号の溝・縦横スクロールバー・折り返し・タッチでパン）
├─ Text/DocumentLineSource  行の読み出しを UI スレッドの外で 1 件ずつ（待っている要求は最新だけ）、見えている付近だけを上限付きでキャッシュ
├─ Markdown/MarkdownView    ItemsRepeater（最上位のブロック 1 つ = 要素 1 つ、見えている分だけ作る）＋ ScrollViewer（本文幅 880 DIP、中央寄せ）
├─ Markdown/MarkdownImages  画像の読み込み方針（下記）
└─ Html/HtmlBrowser         WebView2 の安全境界（下記）
```

**振り分け**（`PreviewContentRouting`、単体テストで固定）: `FileFormat.Text/Markdown/Html` は名前（拡張子・既知のファイル名）で決まり、内容が
バイナリでなければその種類（PNG を .txt にしたものは PNG）。テキストの名前のファイルでは、内容を MPEG-TS・SVG・PDF と誤判定しない（"G…G" が
188 バイト離れている文・インライン `<svg>` を含む HTML・`%PDF-` を引用した文書）。`.ts`/`.mts` は内容が MPEG-TS なら動画、テキストなら
TypeScript。種類が分からず先頭がテキストに見えるファイル（拡張子なしの README、独自の設定ファイル）もテキスト表示（画像の拡張子と文書・
アーカイブ等の既知の種類を除く。後者は従来どおり情報カード）。クラウドのみのファイルは読まない（読むとダウンロードされる）→ 情報カード。
テキストの名前でも中身がバイナリなら、情報カードに「表示できるテキストが含まれていません」。関連付け・Explorer のプレビュー登録は変えない。

**上限**（`TextPreviewLimits`、性能段階で自動選択。テキストはファイル全体を読まない）:

| 段階 | 索引するバイト | チェックポイント（≒行数） | 1 行の表示 | Markdown を整形する大きさ | HTML を描画する大きさ | 行キャッシュ |
|---|---|---|---|---|---|---|
| High | 2 GiB | 400 万（約 6,400 万行、48 MB） | 10,000 文字 | 4 MiB | 16 MiB | 6,000 行 |
| Balanced | 1 GiB | 200 万 | 10,000 文字 | 2 MiB | 8 MiB | 4,000 行 |
| Low | 512 MiB | 100 万 | 5,000 文字 | 1 MiB | 4 MiB | 2,000 行 |

上限を超えた部分は表示せず、注意書き（「先頭の N 行だけを表示しています」）を出す。上限を超える Markdown・HTML はソースとして表示する。

**テキスト・コードの表示**（`TextLinesView`）:

- スクロールは「先頭の行 + その行の中のピクセル位置」で持つ（ファイル全体のピクセル位置ではない）。数百万行でも浮動小数点の精度に影響されず、
  折り返しもできる（見えている行だけ測る。まだ測っていない行は 1 行分の高さとして扱う）。縦スクロールバーは行番号に対応。
- ホイールで 3 行、Shift/チルトで横、Ctrl+ホイール・Ctrl+=/−/0 は文字の大きさ（ツールバーの − % ＋ も同じ。50〜400 %、閉じると 100 %）。
  Quick View がキーボードを持つとき ↑/↓・PageUp/PageDown・Home/End でスクロール、←/→ は従来どおりファイルの移動。W で折り返し（ツールバーにも）。
  折り返しの既定はプレーンテキストが**オン**、コードが**オフ**（`wrapPlainText`/`wrapCode` として設定に保存）。
- コードは行番号付き、プレーンテキストは行番号なし。フォントは Cascadia Mono（日本語は既定のフォールバック）。色は VS Code の Dark+/Light+ に
  準じたテーマ辞書（`QvSyntax*Brush`）、ハイコントラストでは色付けなし（システムの文字色）。
- 選択は 1 行の中だけ（マウス）。Ctrl+C は選択があれば選択文字列、なければ従来どおりファイル。
- 情報欄: 「C# · UTF-8 · CRLF · 2,000 行 · 61 KB」（索引中は「…行以上（数えています）」）。

**Markdown**（`MarkdownContent`・`MarkdownRenderer`）:

- Markdig 1.4.2（BSD-2-Clause、依存なし。DEPENDENCIES §2）。拡張: 表（パイプ・グリッド）、タスクリスト、取り消し線等、自動リンク、YAML フロントマター、
  脚注、GitHub のアラート（NOTE/TIP/IMPORTANT/WARNING/CAUTION）、定義リスト、数式（ソースのまま表示）、見出しの ID（GitHub 方式）。
  入れ子の深さは 64、描画側も 24 段で打ち切る。解析は UI スレッドの外。Markdig の型は初めて Markdown を表示したときに読み込まれる。
- XAML: 見出し（H1/H2 は下線。UIA の見出しレベル付き）、段落（RichTextBlock、選択可）、強調、インラインコード、リスト・タスクリスト、引用、アラート、
  コードブロック（info 文字列の言語で色付け。3,000 行まで）、表（1,000 行まで、横スクロール）、区切り線、脚注、定義リスト。
  **HTML はレンダリングしない**: `<br>` は改行、`<img>` は画像（下記の規則）、それ以外のタグは表示せず中の文字だけ。HTML コメントは表示しない。
- リンク: XAML の NavigateUri は使わない（自動で起動されるため）。クリックしたときだけ、http/https/mailto は既定のプログラム（PDF のリンクと同じ規則）、
  `#見出し` は文書内へスクロール、それ以外（他のファイル、javascript:、file: 等）は何もしない。リンク先はツールチップで見える。
- 画像（`MarkdownImages`）: 文書のフォルダーからの相対パスとローカルの絶対パス、data: URI は表示。**インターネット上の画像は読み込まない**（代わりに
  代替テキストとアドレスのツールチップ）。ネットワーク共有（`\\server\…`。資格情報が送られうる）・デバイス パス・クラウドのみのファイルは開かない。
  1 文書 64 枚・1 枚 32 MiB・合計 160 MiB まで、表示幅でデコード、表示されたブロックの画像だけ読む。
- Ctrl+U（ツールバーの「ソース」）で色付きソースに切替（同じファイルの再表示。セッション中は選択を保持、閉じると解除）。

**HTML**（`HtmlContent`・`HtmlBrowser`）。WebView2 の型に触れるのは `HtmlBrowser` だけで、最初の HTML を表示するまでアセンブリもブラウザー プロセスも
読み込まない。閉じると `Close()` でプロセスを終わらせる（常駐中は何も動かない）。安全境界:

1. スクリプト無効（`IsScriptEnabled=false`）。Web メッセージ・ホスト オブジェクト・開発者ツール・既定のコンテキスト メニュー・ブラウザーのショートカット
   （印刷・保存・検索・再読み込み）・自動入力・パスワード保存・ステータス バー・スワイプ移動・**SmartScreen の評判確認（アドレスを外部へ送る）**を無効。
2. 文書はメモリから `https://quickview.mavue.invalid/<n>/document.html`（解決されない予約名）として返す。応答ヘッダーで
   `Content-Security-Policy: sandbox; default-src 'none'; img-src data:; style-src 'unsafe-inline' data:; font-src data:; …; form-action 'none'`、
   `X-Content-Type-Options: nosniff`、`Referrer-Policy: no-referrer`、`Cache-Control: no-store`。
3. それ以外の**すべての要求**（画像・CSS・スクリプト・フォント・iframe・プリフェッチ・favicon・ローカル ファイル・相対パス）は
   `WebResourceRequested`（全コンテキスト・全要求元）で空の 403 を返し、ネットワークに出さない。
4. 文書以外への**すべての移動**（リンクのクリック、meta refresh、フォーム、フレーム）を取り消す。新しいウィンドウ・ダウンロード・外部プロトコル
   （アプリの起動）・権限要求・Basic 認証・証明書エラーはすべて拒否。
5. 二重の防御: ブラウザーを存在しないプロキシ（ループバックも対象）・バックグラウンド通信なし・ping なしで起動。プロファイルは InPrivate
   （ディスクに残さない）。ユーザー データ フォルダーは `%LOCALAPPDATA%\Mavue\QuickView\WebView2`。
6. 文字コード: BOM があれば宣言しない。なければ文書の `<meta charset>`（既知の名前のみ）、なければ判定結果を Content-Type で宣言。

- ページのズームは Ctrl+ホイール（WebView2 自身）。ツールバーのズームは HTML の描画では出さない（ソース表示では文字の大きさ）。
- WebView2 ランタイムがない・作成に失敗した場合はソース表示と注意書き。
- 計測（タイミング ログ）: `html-blocked`（種類と scheme のみ。アドレスは記録しない。1 文書 20 件まで）、`html-blocked-total`（要求・移動・新しい
  ウィンドウ・ダウンロード・外部起動・権限の件数）、`full-set`（decoder=html、loaded、charset、所要時間、ランタイムの版）。

**計測項目**: `text-open`（文字コード・言語・バイナリか・所要時間）、`text-index`（行数・索引したバイト・打ち切り・索引のメモリ・最長の行・所要時間）、
`full-set`（decoder=text/markdown/html。行数・ブロック数・解析時間）、`full-visible`（表示した行数・作成したブロック数）、`text-wrap`、`text-source`、
`markdown-image`（origin=Local/Data/Remote/Invalid と描画できたか。アドレスは記録しない）、`markdown-anchor`、`uri-opened`（scheme のみ）。
先読み（`PreviewPrefetchPlan`）の対象は従来どおり画像だけ（テキストは表示が速いので先読みしない）。

**未対応・残課題**: 複数行にまたがる選択とテキスト内の検索、ZIP 内のテキスト（`ArchiveContent` から `TextPreviewer` を使えば表示できる）、
Explorer のプレビュー ウィンドウでのテキスト表示（Windows 標準のハンドラーを奪わない）、RTF・Jupyter ノートブックの整形表示、Mermaid 図と数式の描画、
HTML 内でフォーカスを持っているときの Space（ページのスクロールに使われる。Esc・Explorer からの Space は閉じる）、HTML の印刷。

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
| ADR-21 | Quick View の内容は形式ごとの表示部品（`IPreviewContent`）に分け、ファイル情報からの振り分けは `PreviewContentRouting` の 1 か所だけにする。コントローラーは形式で分岐しない | 機能追加のたびに 1 つのクラスへ分岐と状態が集中し（2,439 行）、性能・品質を保てなくなるため。部品単位で別プロセス化・資源制限ができる | 採用（2026-10-06、§4.7） |
| ADR-14 | 他の Space プレビューツールとの共存: 既定は Mavue 優先（フック再設置で先頭を維持）、設定で譲る | QuickLook は Space を流すため、後から起動した側が先に呼ばれ二重表示（実測 3/3） | 採用（既定値はユーザー確認事項） |
