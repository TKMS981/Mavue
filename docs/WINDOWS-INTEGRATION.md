# Mavue Windows 統合設計

> SPEC §3, §22, `CLAUDE.md` §7 に対応。Explorer 統合はオプションではなく第一級機能として扱う。
> すべての項目は実機 Windows 11 での検証が必須（`docs/TESTING.md` §5）。

最終更新: 2026-10-02（Quick View PoC の実測結果と MSIX 整理を反映）
対象 OS: Windows 11（開発機: 25H2 build 26200.9550）。Windows App SDK 2.x の最小要件は Windows 10 1809 だが、
Explorer 統合（タブ、上段コンテキストメニュー）は Windows 11 を主対象とする。Windows 10 での動作範囲は別途記録する。

---

## 0. 配布形態と登録方式の全体像

| 統合ポイント | MSIX（主） | 非パッケージ（開発・代替） |
|---|---|---|
| ファイル関連付け | `uap:FileTypeAssociation` | `HKCU\Software\Classes` + `RegisteredApplications` |
| 上段コンテキストメニュー（Win11） | `desktop4:FileExplorerContextMenus` + `com:ComServer`（`IExplorerCommand`） | **不可**（パッケージ ID 必須）→ スパースパッケージ（外部ロケーション付きパッケージ）で ID を付与 |
| 従来コンテキストメニュー（「その他のオプションを確認」） | 静的 verb (`uap3:Verb`) | `shell\<verb>` レジストリ |
| サムネイルプロバイダー | `desktop2:ThumbnailHandler` + `com:SurrogateServer` | `ShellEx\{E357FCCD-A995-4576-B01F-234630154E96}` |
| プレビューハンドラー | `desktop2:DesktopPreviewHandler` + `com:SurrogateServer` | `ShellEx\{8895b1c6-b41f-4c1c-a562-0d564250836f}` + `PreviewHandlers` 一覧 |
| プロパティハンドラー | `desktop2:DesktopPropertyHandler` | `HKLM\...\PropertySystem\PropertyHandlers\.ext`（**HKLM のみ・管理者権限要**） |
| IFilter（全文検索） | **マニフェスト拡張なし（調査済み: 公式拡張一覧に記載なし）** | `HKLM` 登録（管理者権限要）→ 別インストーラ部品として提供（Investigating） |
| ログオン時起動 | `desktop:StartupTask` | `HKCU\...\Run` |
| 実行エイリアス (`mavue.exe`) | `uap3:AppExecutionAlias` | PATH |
| 共有ターゲット | `uap:ShareTarget` | 不可（ID 必須） |

**結論**: MSIX パッケージで配布し、Windows Search IFilter のみ追加の（任意・管理者）コンポーネントとして扱う案を第一とする。
開発中は非パッケージ実行 + `HKCU` 登録で検証する。**実装済み（第 4 工程）**: `Mavue.QuickView.Host.exe --register [--no-startup]` /
`--unregister` / `--registration-status`（`Mavue.Shell.QuickViewShellRegistration`。従来メニューの「Mavue Quick View」とサインイン時の起動。
作成・削除するのは `Mavue.QuickView` という名前のキーと値のみ）。当初予定の `tools/dev-register.ps1` は、拡張子の一覧をコードと
二重管理しないためにホストのコマンドに置き換えた。

---

## 1. Space キー Quick View

詳細は `ARCHITECTURE.md` §4、実機検証結果は `docs/QUICKVIEW-POC.md`。要点:

- 方式: 常駐 `Mavue.QuickView.Host.exe` が `WH_KEYBOARD_LL` で VK_SPACE（と、非アクティブ表示中の Esc）のみ判定。PoC で実装・実機確認済み。
- 対象判定: 前面ウィンドウが Explorer（`CabinetWClass`）/ デスクトップ（`Progman`/`WorkerW`）/ ファイルダイアログ（`#32770`、設定で有効化）で、
  フォーカスが `SHELLDLL_DefView` 配下の `DirectUIHWND` または `SysListView32` のときのみ（25H2 で `DirectUIHWND`/`SHELLDLL_DefView` を実測）。
- 選択取得: `IShellWindows` → `IServiceProvider::QueryService(SID_STopLevelBrowser)` → `IShellBrowser` → `IShellView` → `IFolderView2`。
  **STA スレッド必須**（MTA からは `IShellBrowser::GetWindow` が `0x8001010D` で失敗することを実測）。
- Windows 11 タブ: タブごとに `IShellWindows` エントリ・`ShellTabWindowClass`・`SHELLDLL_DefView` が存在（実測）。
  フォーカス中アイテムビューの親 DefView と `IShellView::GetWindow` を照合してアクティブタブを特定する。
- **表示方式（実測に基づく決定）**: 前面化（アクティブ化）しない「パネル」表示。表示中だけ Topmost 帯に置き、Explorer がキーボードを保持する。
  他アプリが前面になったら閉じて Topmost を解除する。理由: 実ユーザー入力（物理キー・リモート操作）では、フック経由のプロセスは
  `SetForegroundWindow` も `HWND_TOP` による z 順の引き上げも安定して行えなかった（`docs/QUICKVIEW-POC.md` §3.2）。

### 制約・リスク

| 制約 | 影響 | 対策 |
|---|---|---|
| フックのタイムアウトで無通知解除 | Space が効かなくなる | コールバックは判定のみで即リターン、専用スレッド、定期再インストール |
| UIPI（昇格 Explorer） | 昇格ウィンドウでは動作しない | 仕様として記録。コンテキストメニュー経由は動作 |
| タイプアヘッド検索（ファイル名に空白） | Space を奪うと検索が壊れる | 直近の文字キー入力から一定時間は素通し（閾値は設定可能） |
| 名前変更・アドレスバー・検索ボックス | 入力を妨害 | フォーカスクラス判定で除外。Win11 の XAML 部分（`Microsoft.UI.Content.DesktopChildSiteBridge` 等）は対象外 |
| 他の Quick Look 系ツール（本機に QuickLook 導入済み）| 二重表示 | 起動時に既知のプロセスを検出し警告、Mavue 側のトリガーを無効化/別キーに変更可能 |
| セキュリティ製品の誤検知 | フックがブロック/警告される | コード署名、Space 以外を記録しない、設定で無効化可能、ドキュメント化 |
| Explorer の内部ウィンドウクラスは非公開仕様 | Windows 更新で変わりうる | クラス名判定を 1 か所に集約し、回帰テスト（実機）で毎リリース確認 |
| 前面化の権利（foreground lock） | フック経由ではアクティブ化できない（物理キーで 9/11 失敗、実測） | アクティブ化せずパネル表示（上記）。ユーザーがクリックすれば通常どおりアクティブになる |
| 権利のない z 順引き上げ | `HWND_TOP` では Explorer の後ろに出ることがある（リモート操作で実測） | 表示中のみ Topmost 帯。他アプリ前面化で即クローズ + Topmost 解除 |
| 自動化ハーネスの結果と実環境の乖離 | 注入入力・子プロセス起動では前面化が成功しやすい（実測） | ハーネスは WMI で独立起動したホストに対して実行。最終判断は必ずユーザー実操作で確認 |

代替トリガー（すべて実装対象）: 右クリック「Mavue Quick View」（**従来メニュー版を実装済み**、QUICKVIEW-POC §11）、設定可能なホットキー（例: Ctrl+Space）、
`Mavue.QuickView.Host.exe --quickview <path>`（**実装済み**。将来の `mavue.exe` 実行エイリアスからも同じ経路）。
右クリック経由は Explorer 内で動く `IExplorerCommand::Invoke` から `AllowSetForegroundWindow(Mavue の PID)` を呼べる
（Explorer は前面プロセスなので権利を持つ）ため、こちらの経路ではアクティブ化も正規に可能（未実装・要検証）。
従来メニュー版では、Explorer が起動した `--quickview` プロセスが同じ権利を持つので、そこから常駐プロセスに譲る（実装済み。E2E では
アクティブ化に成功、ユーザー物理操作での確認待ち）。

---

## 2. コンテキストメニュー

- Windows 11 上段メニュー: `IExplorerCommand`（+ サブコマンドは `IEnumExplorerCommand`）を C++ で実装し、
  MSIX の `desktop4:FileExplorerContextMenus` に登録。パッケージ ID がないと上段には表示されない。
- 項目（SPEC §22）: 「Mavue で開く」「Mavue Quick View」、コンテキスト操作「PDF を結合」「PDF を分割」「PDF を圧縮」
  「画像を変換」「画像から PDF を作成」ほか。
- `IExplorerCommand::GetState` は**高速**でなければならない（拡張子判定のみ。ファイル内容を読まない）。
- 実行時は `IShellItemArray` をパイプで `Mavue.App` / `Mavue.QuickView` に渡す。対象プロセス未起動なら起動。
- 複数選択の上限: `MultiSelectModel` を `Player` にすると全選択アイテムが 1 回の呼び出しで渡る。

## 3. ファイル関連付け

- 対象: PDF、全サポート画像形式（SPEC §4）、音声/動画（プレビュー用。既定アプリの奪取はしない）。
- Windows 10 以降、**既定アプリはプログラムから設定できない**（UserChoice はハッシュで保護）。
  Mavue は関連付け候補として登録し、ユーザーを `ms-settings:defaultapps?registeredAppUser=Mavue`（または registeredAUMID）へ誘導する。
- 「プログラムから開く」一覧への登録、ProgID ごとのアイコン・説明（多言語）を提供。

## 4. Preview Handler（Explorer プレビューウィンドウ）

- インターフェース: `IPreviewHandler`, `IInitializeWithStream`（推奨。仮想フォルダ・ZIP 内でも動作）, `IObjectWithSite`, `IOleWindow`,
  `IPreviewHandlerVisuals`（背景色・フォント・テキスト色をテーマに追従）。
- ホスト: 既定は `prevhost.exe`（AppID `{6d2b5079-2f0b-48dd-ab7f-97cec514d30b}`）。他社ハンドラーのクラッシュに巻き込まれないよう
  **専用 AppID + DllSurrogate** を検討（MSIX では `com:SurrogateServer`）。
- 描画: `Mavue.Native.Render`（PDFium / WIC / D2D）でハンドラー内に描画。PDF はページスクロール、画像は Fit 表示。
- 既存ハンドラーとの関係: 本機の `.pdf` には既に `{3A84F9C2-6164-485C-A7D9-4B27F8AC009E}` が登録済み（Edge の PDF プレビュー）。
  **他社登録を黙って上書きしない**。設定画面で「Mavue をプレビューに使用」を選んだ場合のみ HKCU に登録し、元の値を保存・復元可能にする。
  MSIX 登録時の優先順位（パッケージ登録 vs 既存 HKLM/HKCU 登録）は実機で検証する（Investigating）。
- 対象形式: PDF、Windows がプレビュー非対応の画像形式（SVG, JPEG 2000, AVIF/HEIC 拡張なし環境, RAW 一部 等）。

## 5. Thumbnail Provider

- `IThumbnailProvider` + **`IInitializeWithStream`**（Microsoft 推奨。分離プロセスでロードされる場合に使用される唯一の初期化方式）。
  `DisableProcessIsolation` は使用しない（レガシー用）。
- 出力: 要求サイズ `cx` に合わせた 32bpp `HBITMAP` + `WTSAT_ARGB`。
- 対象: PDF（1 ページ目）、SVG、JPEG 2000、OS が対応しない形式。**JPEG/PNG 等 OS が既に提供する形式は上書きしない**。
- サムネイル生成は高速性が求められる: PDF は低解像度レンダリング、画像は縮小デコード。タイムアウトに注意。

## 6. プロパティハンドラー / Windows Search

- `IPropertyStore` + `IInitializeWithStream` + `IPropertyStoreCapabilities`。
  PDF: タイトル・作成者・キーワード・ページ数。画像: OS 対応外形式の寸法・撮影日・GPS。
- IFilter: PDF テキスト、Mavue が付与した OCR テキストを索引化（`SearchFilterHost.exe`、低権限・タイムアウトあり）。
  MSIX 拡張がないため登録方式を調査中（§0）。
- 既存の PDF IFilter（Adobe 等）がある場合は上書きしない方針。

## 7. ドラッグ & ドロップ / クリップボード

- ドロップ受付: ファイル（CF_HDROP / StorageItems）、画像データ（PNG, DIB, CF_DIBV5）、他 PDF のページ。
- ドラッグ元: Quick View / App からファイル、ページ（PDF 間移動）、選択領域（画像）。
- クリップボード: 画像は PNG + CF_DIBV5（透過保持）+ 必要に応じ CF_HDROP。PDF ページはファイルとして。
- 遅延レンダリング（`SetClipboardData(NULL)` 相当 / `DataPackage.SetDataProvider`）で巨大画像のコピーを即時化。

## 8. 共有

- `DataTransferManagerInterop::ShowShareUIForWindow`（HWND 版）で Windows 共有 UI を表示。
- 共有ターゲット（他アプリから Mavue へ）: MSIX の `uap:ShareTarget`。

## 9. 印刷

- 方式 A（主）: Mavue 独自の印刷プレビュー UI → Direct2D `ID2D1PrintControl` → `IPrintDocumentPackageTarget`（XPS 印刷パス）。
  プリンタ能力は `PrintTicket`/`PrintCapabilities`（`Windows.Graphics.Printing.PrintTicket` / Print Schema）。
  ページ範囲・部数・用紙サイズ・向き・拡大縮小・N-up（ページ/枚）・用紙に合わせる・カラー/白黒を SPEC §18 の通り実装。
- 方式 B: Windows 標準印刷ダイアログ（`PrintManagerInterop.GetForWindow` + `Microsoft.UI.Xaml.Printing.PrintDocument`）。
  既知の不具合報告（WindowsAppSDK issue #5725: Print dialog 生成失敗）があるため実機検証が必要。
- **Windows Protected Print Mode (WPP)**: 有効な環境ではサードパーティ v3/v4 ドライバーが無効化され、IPP/Mopria 準拠プリンタのみ。
  XPS 印刷パスは WPP 下でも動作する想定（実機検証）。
- PDF の印刷はベクターのまま（PDFium → D2D）を基本とし、問題のあるプリンタ向けにラスター印刷オプションを用意。
- 本機のプリンタ: 「Microsoft Print to PDF」「OneNote (Desktop)」のみ（物理プリンタなし）→ 物理プリンタでの検証は Blocked。

## 10. スキャン（WIA / TWAIN）

- **WinRT `Windows.Devices.Scanners.ImageScanner`**: フラットベッド/フィーダー、カラー/グレー/白黒、DPI、両面 (`Duplex`)、プレビュー。
- **WIA 2.0 (`IWiaDevMgr2`, `IWiaItem2`, `IWiaTransfer`)**: WinRT で不足する詳細制御・ストリーム転送。
- **TWAIN 2.x**: 64bit `TWAINDSM.dll` が必要。多くの業務用スキャナドライバーは 32bit TWAIN のみ →
  `Mavue.Scan.Twain32.exe`（x86）ブリッジでデータソースを駆動し、画像をパイプ/共有メモリで返す。
- 本機の状態: WIA サービス (`stisvc`) は存在（停止・自動起動）、`twain_32.dll`（レガシー 32bit DSM）のみ存在、
  64bit `TWAINDSM.dll` なし、イメージングデバイス接続なし → **実機スキャン検証は Blocked（ハードウェア待ち）**。
- 後処理: 自動傾き補正（Hough/投影プロファイル法を自前実装）、OCR、複数ページ PDF 化。

## 11. Windows Ink

- 署名作成・フリーハンド注釈に筆圧/傾き/消しゴム端/バレルボタンを使用。
- 現時点: WinUI 3 の `InkCanvas`/`InkPresenter` は Windows App SDK **2.4/2.5 の Experimental のみ**（安定版 2.5.1 には含まれない）。
  → WinUI 3 Pointer API（`PointerPoint.Properties.Pressure/XTilt/YTilt/IsEraser`）+ 独自ストローク平滑化 + Win2D 描画で実装し、
  ストロークモデル（Mavue.Markup）は描画方式非依存にして、InkCanvas 安定版化後に入力層だけ差し替え可能にする。
- 本機にペン/タッチデジタイザなし → **実機ペン検証は Blocked（ハードウェア待ち）**。マウス署名は検証可能。

## 12. 高 DPI / マルチモニタ

- Per-Monitor V2（WinUI 3 既定）。Quick View はカーソルのあるモニタ（または Explorer ウィンドウのモニタ）に表示。
- 本機には物理 GPU 2 基 + 仮想ディスプレイアダプタ（Parsec / Meta Virtual Monitor）があり、マルチアダプタ環境での
  D3D デバイス選択（ウィンドウのあるモニタのアダプタ）とデバイスロストを検証項目に含める。

## 13. 実機検証の前提

- 開発者モード: 本機では未設定（`AllowDevelopmentWithoutDevLicense` 値なし）。非署名 MSIX / スパースパッケージの登録には
  開発者モード有効化、または信頼済みテスト証明書での署名が必要。
- 長いパス: 有効（`LongPathsEnabled=1`）。

## 14. MSIX・パッケージ ID・常駐プロセスの整理（2026-10-02 調査）

### 14.1 パッケージ ID が必要なもの / 不要なもの

| 機能 | パッケージ ID | 根拠・備考 |
|---|---|---|
| Windows 11 の**上段**コンテキストメニュー（「Mavue で開く」「Mavue Quick View」、PDF 結合などの操作） | **必須** | Microsoft Learn「Add a File Explorer context menu command to a packaged desktop app」: `IExplorerCommand` を実装したネイティブ DLL を `windows.comServer`（`com:SurrogateServer`）と `windows.fileExplorerContextMenus`（`desktop4`/`desktop5`）で登録する。MSIX または**スパースパッケージ**（外部の場所を持つパッケージ）で付与 |
| 従来のコンテキストメニュー（「その他のオプションを確認」の中） | 不要 | `IContextMenu` / レジストリの verb。上段には出ない |
| 「プログラムから開く」/ ファイル関連付け | 不要（MSIX では `uap:FileTypeAssociation`） | 関連付けで「プログラムから開く」に載り、編集 verb も追加できる（上段メニューの汎用コマンドとは別の仕組み） |
| サムネイル / プレビューハンドラー / プロパティハンドラー | 不要（非パッケージでもレジストリ登録で可）。MSIX では `desktop2:*` 拡張 | 非パッケージのプロパティハンドラー登録は HKLM（管理者権限）が必要 |
| Space キー Quick View（低レベルキーボードフック） | **不要** | 本 PoC は非パッケージで動作確認済み。参考: 本機の QuickLook は MSIX パッケージで同じ方式のフックを使っている |
| ログオン時起動 | MSIX: `desktop:StartupTask` / 非パッケージ: `HKCU\...\Run` | StartupTask はユーザーが設定アプリで無効化できる |
| 共有ターゲット、一部の Windows AI API | 必須 | |

結論: **上段コンテキストメニューのためにパッケージ ID は本当に必要**（公式文書で確認）。ただし実装はネイティブ DLL なので、
C++ ツールチェーン（Build Tools）が必要になる（§14.4）。

### 14.2 Quick View 常駐プロセスの MSIX 上の設計

- 1 つの MSIX パッケージに `Mavue.App.exe`（エディタ）と `Mavue.QuickView.Host.exe`（常駐）を含め、パッケージ ID を共有する。
  常駐プロセスは `desktop:StartupTask` で起動（2 つ目の `<Application>`、`EntryPoint="Windows.FullTrustApplication"`、`runFullTrust`）。
- 2 プロセス間の通信は **Named Pipe**（同一ユーザー SID のみ許可）。App Services（`windows.appService`）は WinRT アクティベーションを伴い、
  同じパッケージ内の常駐 2 プロセス間の低遅延通信には過剰なので採用しない（必要になるのは他パッケージのアプリへのサービス公開時）。
- コンテキストメニューの `IExplorerCommand` DLL（Explorer 内で動く）から常駐プロセスに選択項目をパイプで渡し、
  同時に `AllowSetForegroundWindow` で前面化の権利を渡す（§1）。
- 非パッケージ開発時は、同じバイナリを `HKCU\...\Run` とスパースパッケージで検証できるようにする。

### 14.3 開発者モードとサイドロード

| 方法 | 要件 | 本機の状態 |
|---|---|---|
| 開発中の未署名パッケージ（レイアウトフォルダを `Add-AppxPackage -Register`） | **開発者モード**が必要 | 未設定 |
| 署名済み MSIX のサイドロード | 署名証明書がローカルで信頼されていること（テスト証明書を「信頼されたユーザー」等に登録）。Windows 11 は既定でサイドロード可能 | 証明書未作成 |
| スパースパッケージ | 署名が必要（開発時はテスト証明書） | 未作成 |
| 配布 | 公的なコード署名証明書（LL フックを使うため署名は必須級）または Microsoft Store | 未定 |

開発者モードの有効化はユーザーの設定変更なので、Explorer 統合の段階でユーザーの判断を仰ぐ（本 PoC では不要）。

### 14.4 Visual Studio Build Tools の要否（調査結果）

| 対象 | Build Tools | 状態 |
|---|---|---|
| 現在のソリューション（C#・WinUI 3、Quick View PoC を含む） | **不要**（.NET SDK + NuGet の Windows SDK BuildTools でビルド・テスト・実行できることを確認済み） | ビルド成功 |
| `IExplorerCommand` / サムネイル / プレビューハンドラー / プロパティハンドラー / IFilter（ネイティブ COM DLL） | **必要** | 未着手 |
| PDFium 等ネイティブライブラリの自前ビルド | 必要（ただし事前ビルド済みバイナリを使う間は不要） | — |
| TWAIN 32bit ブリッジ | 必要 | 未着手 |

必要になった時点でインストールするコンポーネント（最小構成、管理者権限が必要なのでユーザーが実施）:
`Microsoft.VisualStudio.Workload.VCTools`（C++ によるデスクトップ開発）、`Microsoft.VisualStudio.Component.VC.Tools.x86.x64`、
`Microsoft.VisualStudio.Component.VC.Tools.ARM64`、`Microsoft.VisualStudio.Component.Windows11SDK.26100`（以降の SDK でも可）。
CMake（`Microsoft.VisualStudio.Component.VC.CMake.Project`）は vcpkg/CMake を使う場合のみ。

## 15. Windows 11 上段コンテキストメニュー「Mavue Quick View」（2026-10-02 実装）

### 15.1 構成（Microsoft Learn「Add a File Explorer context menu command to a packaged desktop app」の方式）

| 部品 | 内容 |
|---|---|
| `native/Mavue.Shell.Native`（C++20、CMake、MSVC） | `IExplorerCommand` を実装した COM DLL。`GetTitle`「Mavue Quick View」、`GetState` は常に有効（対象拡張子はマニフェストで限定、ファイルは読まない）。`Invoke` は **Explorer に** `Mavue.QuickView.Host.exe --quickview "<path>"…` を起動させるだけ（デスクトップの Shell オートメーション `ShellWindows.FindWindowSW(SWC_DESKTOP)` → `IShellDispatch2::ShellExecute`）。DLL がホストを直接起動することはない |
| 識別パッケージ（スパースパッケージ） | `AppxManifest.xml` だけを含む署名済み MSIX。外部の場所 = ホストの出力フォルダー。`com:SurrogateServer`（DLL は COM の代理プロセス dllhost で動く）と `desktop4:FileExplorerContextMenus`（従来メニューと同じ 18 拡張子の `desktop5:ItemType`）。マニフェストは `Mavue.Shell.IdentityPackageManifest` が生成（拡張子の一覧を従来メニューと共有） |
| ホスト | 変更なし（exe に msix 要素を付けないので、ホストはパッケージ ID なしで今までどおり動く）。`--quickview` → 名前付きパイプ → 常駐ホストの既存経路をそのまま使う |

Explorer に起動させる理由（実測、2026-10-02）: 代理プロセスから直接起動したプロセスは前面に出られず（TeraCopy のパッケージ版項目で確認）、
パッケージのコンテナ内で動く（VS Code issue #334716）。Explorer が起動したプロセスは前面化の権利を持ち、コンテナ外で動く。

### 15.2 登録（現在のユーザーのみ、管理者権限不要）

```powershell
powershell -File tools/build-native.ps1                 # DLL（MSVC Build Tools が必要）→ artifacts/native/win-x64
dotnet build Mavue.slnx -c Release                      # ホストの出力フォルダーに DLL とロゴがコピーされる
powershell -File tools/new-dev-certificate.ps1          # 開発用の自己署名証明書（CN=Mavue Dev、CurrentUser\TrustedPeople で信頼）
powershell -File tools/package-identity.ps1             # マニフェスト生成 → MakeAppx → SignTool（NuGet の Windows SDK BuildTools）
& $qv --register-modern-menu artifacts\identity\Mavue.QuickView.Identity.msix   # 登録し、従来メニューの項目を外す
& $qv --registration-status
& $qv --unregister-modern-menu                         # 外す（従来メニューに戻すなら続けて --register）
```

- 新メニューを登録したときは従来メニューの項目を外す（同じ項目が 2 つ並ぶのを避ける。StartAllBack 環境の従来型メニューにもパッケージの項目は出る）。
  新メニューが登録されている間の `--register` は、従来メニューを追加せず、サインイン時の起動だけを登録する。
- Windows 10、署名証明書がない環境、パッケージを登録しない環境では、従来どおり `--register` の従来メニューを使う。
- 本番配布では、利用者の PC が信頼する証明書（Azure Trusted Signing や CA の証明書）での署名が必要。開発者モードは不要。

### 15.3 実機確認（2026-10-02、Release、この PC は StartAllBack により右クリックで従来型メニューが開く）

| 確認 | 結果 |
|---|---|
| ビルド | DLL は x64・ARM64 とも `/W4 /WX` で警告なし（MSVC 19.51）。静的 CRT（VC++ 再頒布パッケージ不要） |
| 登録 | 自己署名の公開部分を CurrentUser\TrustedPeople に入れただけでは `0x800B0109`（ルート証明書が信頼されていない）。Microsoft Learn「Create a certificate for package signing」のとおり LocalMachine\TrustedPeople に入れて成功（スパースパッケージの手順書の「CurrentUser でよい」という記述はこの PC では通らなかった） |
| 1 ファイル右クリック →「Mavue Quick View」（パッケージの項目） | 約 310 ms で前面表示（アクティブ）。↓・Explorer の選択変更に追従、Esc で Explorer に戻る |
| 3 ファイル選択 | 約 312 ms、「1 / 3」、→ で 2 番目へ。`DllHost.exe /Processid:{3C34DBCC-…}`（親 svchost）で DLL が動き、`--quickview` の 3 パスを持つプロセスが 1 つだけ **explorer.exe を親として**起動された |
| 常駐ホストなし | 約 634 ms で前面表示。そのプロセスが常駐ホストになり、親は explorer.exe、`GetPackageFullName` は APPMODEL_ERROR_NO_PACKAGE（パッケージ ID なし＝コンテナ外） |
| Space | 87 ms で表示（パネル、Explorer が前面のまま）、従来どおり |
| 切り替え | 新メニュー登録中の `--register` は従来メニューを追加しない／`--unregister-modern-menu` → `--register` で従来メニューに戻る／`--register-modern-menu` で従来メニューの 18 項目を外す。メニューの「Mavue Quick View」は 1 つだけ |
| Windows 11 標準の新しいメニュー | StartAllBack を一時的に無効化（`HKCU\Software\StartIsBack` の `Disabled=1`、Explorer 再起動。確認後に 0 へ戻した）して確認。上段にアイコン付きで「Mavue Quick View」が 1 つ表示（Copilot・Malwarebytes 等のアプリ項目の並び）。1 ファイル 429 ms、3 ファイル 417 ms（「1 / 3」、`--quickview` は 3 パスで 1 プロセス、親 explorer.exe）、常駐ホストなし 614 ms、Space 66 ms。いずれも前面表示・選択追従・Esc で Explorer に戻る |

- 自動操作での注意: 常駐ホストの停止直後や Explorer の再起動直後に、合成した右クリックでメニューが開かない・選択の設定に失敗することがあった（計 3 回、再実行で成功）。製品側の動作ではなく、操作スクリプト側のタイミング。
