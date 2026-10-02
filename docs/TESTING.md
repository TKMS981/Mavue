# Mavue テスト戦略

> `CLAUDE.md` §16 / SPEC §31（Definition of Done）に対応。

最終更新: 2026-10-02（Quick View PoC の E2E と計測を追加）

---

## 1. テスト階層

| 層 | プロジェクト | 内容 | 実行環境 |
|---|---|---|---|
| 単体 | `tests/Mavue.*.Tests` | 純粋ロジック（形式判定、安全保存、Undo/Redo、レイアウト計算、IPC シリアライズ等） | CI / ローカル |
| ファイル形式 | `tests/Mavue.Codecs.Tests`, `Mavue.Pdf.Tests` | 各形式のデコード/エンコード、破損ファイル、巨大寸法、境界値 | CI / ローカル |
| 統合 | `tests/Mavue.Integration.Tests` | 複数モジュール連携（PDF 編集→保存→再読込、変換パイプライン、OCR→埋め込み→検索） | ローカル / CI（Windows ランナー） |
| シェル統合 | `tests/Mavue.Shell.Tests`（+ 手動チェックリスト） | COM 登録、`IThumbnailProvider`/`IPreviewHandler` を自前ホストで呼び出し、Explorer 実機操作 | **実機必須** |
| Quick View | `tests/Mavue.QuickView.Tests` + UI 自動化 | トリガー判定ロジック（クラス名・フォーカス状態の表駆動テスト）、ナビゲーション、実機エンドツーエンド | 単体: CI / E2E: 実機 |
| 回帰 | 各プロジェクト内 `Regression/` | 不具合ごとに再現ファイル/手順をテスト化 | CI |
| 性能 | `tests/Mavue.Perf` | BenchmarkDotNet + 起動遅延ハーネス | 実機（本機を基準機） |

テストフレームワーク: xUnit v3（`xunit.v3` 4.0.1）。`dotnet test` で実行。

## 2. 実行コマンド

`global.json` で Microsoft.Testing.Platform (MTP) ランナーを選択している（.NET 10 SDK の `dotnet test` は VSTest 方式を xUnit v3 で使えないため）。

```powershell
# すべてのテスト（ビルド含む）
dotnet test --solution Mavue.slnx -c Debug

# 実機専用テストを除外（CI 用）
dotnet test --solution Mavue.slnx --filter-not-trait "Category=RealWindows"

# カテゴリ指定
dotnet test --solution Mavue.slnx --filter-trait "Category=QuickView"

# WinUI 3 アプリ起動スモークテスト（対話デスクトップが必要）
powershell -ExecutionPolicy Bypass -File tools/smoke-test.ps1 -Configuration Debug
```

### 現在のテスト（2026-10-02）

| プロジェクト | 内容 | 件数 |
|---|---|---|
| `Mavue.Core.Tests` | 形式判定（シグネチャ・拡張子フォールバック・Unicode パス・巨大ストリーム）、安全保存（失敗/検証失敗/キャンセル時に元ファイル不変、属性維持、バックアップ、日本語ファイル名） | 45 |
| `Mavue.QuickView.Tests` | Space トリガー判定（既存 18 件・無変更）、入力追跡（タイプアヘッド・IME 推定・ショートカット除外）、縮小デコードサイズ、ファイル安全ポリシー（属性・寸法・UNC・デバイスパス）、計測タイムライン（JSON Lines） | 73 |
| `Mavue.Repository.Tests` | ja-JP/en-US リソースのキー一致（App と Quick View Host）、**SPEC の全機能が FEATURES.md に存在すること**、SPEC §29 以外を Excluded にしていないこと、状態語彙 | 6 |
| **合計** | | **142（141 成功、1 スキップ: シンボリックリンク作成に開発者モードが必要）** |

第 2 工程で `Mavue.QuickView.Tests` に選択追従ロジック（`PreviewNavigator`）の 17 件を追加（同プロジェクト計 90 件）。

`Mavue.Core.Tests` は 46 件（シンボリックリンク経由の保存テストを追加）。

第 3 工程（2026-10-02）の追加:

| プロジェクト | 追加内容 | 件数（計） |
|---|---|---|
| `Mavue.QuickView.Tests` | タブ切替判定（`ShellViewFocus`）6、先読みキャッシュ（`PreviewCache`: LRU・予算超過・置換・破棄）12、表示サイズ（`DisplaySizing`: 拡大しない・原寸・上限・DPI）と設定（`QuickViewSettings`: 既定値・壊れたファイル・未知の値・保存往復） | 125 |
| `Mavue.Image.Tests`（新規） | WIC 直接デコード: EXIF 回転 1〜8 を Windows のデコーダーと画素比較、縮小・拡大しない・PNG・破損ファイル・画素数上限・寸法読み取り・呼び出し側バッファへの書き込み（行パディングあり）・バッファ不足と破棄後の拒否 | 28 |
| **全体** | Core 46 + Image 28 + QuickView 125 + Repository 6 | **205（204 成功、1 スキップ: 開発者モードが必要）** |
| `tools/smoke-test.ps1` | Mavue.exe 起動 → 最初のフレーム描画 → リソース解決確認 → 終了コード 0 | — |

第 4 工程（2026-10-02）の追加:

| プロジェクト | 追加内容 | 件数（計） |
|---|---|---|
| `Mavue.Core.Tests` | IPC の枠組み（`IpcFraming`・`QuickViewRequest`）: 往復、不正な長さ（確保前に拒否）、壊れた JSON、途中で切れたフレーム、パスの検証 | 62 |
| `Mavue.QuickView.Tests` | 名前付きパイプ（`QuickViewPipe`）: 名前の分離、送受信、同時 20 クライアント、無効な依頼、不正データの後も継続、サーバーなし | 131 |
| `Mavue.Shell.Tests`（新規） | 右クリック・サインイン登録（`QuickViewShellRegistration`）: 専用の HKCU サブキーで実行。全拡張子への登録、他アプリの項目を消さない、空キーの片付け、再登録、別名の動詞、不正なパス・名前の拒否 | 12 |
| **全体** | Core 62 + Image 28 + QuickView 131 + Repository 6 + Shell 12 | **239（238 成功、1 スキップ: 開発者モードが必要）** |

Quick View の GIF アニメーション（2026-10-02）の追加: `Mavue.Image.Tests` に `GifComposer`（遅延の正規化・ループ回数の解析・オフセット/透過/はみ出し・
破棄方法 2/3・リセット）と `GifAnimationReader`（Windows のエンコーダーで作った GIF のフレーム数・遅延・ループ・各フレームの色、ループなし、GIF 以外の拒否）の 14 件（計 42）。
全体 276（275 成功、1 スキップ）。E2E `gif-animation`: 3 フレーム 100 ms の無限ループ GIF で、画面中央の色が変わること、ループすること、フレーム間隔、
↓ で静止画に切り替えると停止し以後フレームが出ないこと、↑ で戻ると新しい再生が 1 つだけ（約 10 フレーム/秒）であること、Esc で停止すること。

Quick View の PDF ページ送り（2026-10-02）の追加: `Mavue.QuickView.Tests` に `PdfPageCursor`（未確定・1 ページ・先頭/末尾で止まる・0 は移動でない・ページ数の変化・リセット）と
ページ別のキャッシュキーの 7 件（計 138）。全体 262（261 成功、1 スキップ）。E2E `pdf-pages`: 3 ページの PDF で PageDown×3（末尾で止まる）・PageUp・ホイール（拡大しないモードのみ）、
Explorer の選択が変わらないこと、↓↑ でファイル移動して戻ると 1 ページ目から表示されること。

Quick View の動画・音声（2026-10-02）の追加: `Mavue.Core.Tests` に形式判定（MP4/MOV/M4A のブランド、汎用ブランドでの拡張子による判定、MKV/WebM の DocType、
WAV/AVI、ASF、FLAC、Ogg（Opus/Theora）、MPEG-PS/TS、MP3（ID3・フレーム同期）、AAC、画像と誤判定しないこと、拡張子フォールバック、種類の判定）、
`Mavue.QuickView.Tests` に `MediaControlMath`（シークの範囲、音量の範囲と丸め、時間表示）。全体 319（318 成功、1 スキップ）。
E2E（生成したファイルのみ使用。30 秒の MP4 は MediaComposition で赤→緑→青の各 10 秒＋正弦波、MP3/M4A/WMA/FLAC/WMV は Windows のエンコーダー）:
`media-video`（画面上の色・ホストの音声セッションのピーク値・Enter で一時停止〔停止中は無音〕・Ctrl+→ で +10 秒〔緑になる〕・Ctrl+↓/↑ の音量・
Ctrl+矢印でファイルが移動しないこと・Quick View 上の →/← と Explorer の ↓/↑ でのファイル移動と停止・Quick View 上の Esc で閉じて解放）、
`media-switch`（動画→画像→MP3→PDF→壊れた MP4→WAV→逆順で戻る。毎回前の再生が止まること、音声は音が出ること、壊れたファイルはメッセージで Quick View は継続、
同時に動く再生は 1 つ、Space で閉じて解放）、`media-formats`（M4A・WMA・FLAC・WMV の再生）。
ハーネスに `--only <名前の前方一致,...>`（指定したシナリオだけ実行）を追加。

本体の閲覧基盤と共通ビューア（2026-10-02）の追加:

- 表示サイズ・PDF のページ・動画/音声の計算・安全ポリシーを `Mavue.Core.Viewing` に移したため、対応するテスト（`MediaControlMathTests`・`PdfPageCursorTests`・`PreviewPolicyTests`）は
  `Mavue.Core.Tests` に移動（内容は同じ。ページ別キャッシュキーのテストは `Mavue.QuickView.Tests` の `PreviewCacheTests` に残す）。
- `Mavue.Core.Tests` に `ViewerFileListTests`（開いた順・重複除去・両端で止まる・フォルダー内の絞り込みと名前順・未知の拡張子のファイルも残す・自然順の比較・開ける形式・サイズ表記）。
- 全体 339（338 成功、1 スキップ）。
- 実機 E2E（本体）: `dotnet run --project tools/Mavue.QuickView.Harness -c Release --no-build -- --configuration Release --app`。
  `Mavue.exe --trace-file <log> <file>` を起動し、ログ・画面の画素・アプリの音声セッションのピーク値で確認する。
  - `app-formats`: JPEG・PNG・WebP・AVIF・HEIF・GIF（アニメーション）・PDF・MP4・WMV・MP3・WAV・M4A・WMA・FLAC と、壊れた MP4・JPEG（メッセージ）。閉じると再生停止・プロセス終了。
  - `app-folder-navigation`: 1 ファイルを開いてフォルダー内を →/← で 10 回移動（動画→画像→MP3→PDF→壊れた MP4→WAV→戻る）。毎回前の再生が止まり、同時に存在するプレーヤーは 1 つ、音は現在のファイルからだけ。
  - `app-pdf-pages`: PageDown×3（末尾で止まる）・PageUp・ホイール、隣の画像へ移って戻ると 1 ページ目。
  - `app-media-keys`: Space で一時停止（無音）、Ctrl+→ で +10 秒（画面が緑）、Ctrl+↓/↑ で音量、Space で再開、再生中に閉じても解放。
  - `app-multiple-files`: 引数の 3 ファイルだけを順に移動（フォルダーは使わない）。
  - `app-open-dialog`: Ctrl+O で Windows のファイル選択（アンパッケージアプリでは PickerHost.exe が表示）、Esc で取り消し、アプリは応答を続ける。
- ドラッグ＆ドロップは E2E 化していない。Explorer の項目を UI Automation で探して合成マウス入力でドラッグし、開くことを実機で確認した
  （`SetCursorPos` だけではドラッグが始まらず、`mouse_event` の移動が必要）。
- ハーネスの追加: `--only <シナリオ名の前方一致,...>`、`--app`、`--app-path <Mavue.exe>`（「他のアプリ」として起動する Mavue.exe を差し替える）、
  `media-esc-repeat`（Quick View をアクティブにした後の Esc を 20 回）。
- ハーネスの修正: 計測ログの読み取り（`TimingLog.Poll`）が、読み取り中にホストが追記した行を読み飛ばすことがあった（読んだ後にファイル長へ位置を合わせていた）。
  読んだバイト数だけ進めるよう修正（`media-video` の失敗の 1 つはこれ。QUICKVIEW-POC §15）。

Windows 11 上段メニュー対応（2026-10-02）の追加:

| プロジェクト | 追加内容 | 件数（計） |
|---|---|---|
| `Mavue.Shell.Tests` | 識別パッケージのマニフェスト（識別・スパース設定・COM サーバーとメニューの CLSID 一致・全拡張子・C++ ヘッダーの CLSID との一致・不正な発行者/拡張子/バージョンの拒否）、従来メニューだけを外す／サインイン登録だけを追加、ネイティブ DLL を直接読み込んで COM の vtable 経由で呼ぶテスト（表示名・アイコン・状態・フラグ・正規名、項目なしの Invoke は何も起動しない、未知のクラスの拒否、アンロード可能）。ネイティブのテストは DLL がない環境ではスキップ | 28 |
| **全体** | Core 62 + Image 28 + QuickView 131 + Repository 6 + Shell 28 | **255（254 成功、1 スキップ: 開発者モードが必要）** |

新メニューの実機確認は E2E ハーネスではなく、実際の Explorer メニューを合成入力で操作するスクリプトで行った（WINDOWS-INTEGRATION §15.3）。

E2E シナリオの追加: `cli-quickview`（`--quickview` の転送・アクティブ表示・選択追従）、`context-menu-verb`（テスト用の名前で動詞を登録し、
Explorer に 1 ファイル・3 ファイルで実行させる。終了後に登録を削除）。

`Mavue.Repository.Tests` は、FEATURES.md から行を削除する・SPEC 非除外項目を Excluded にする変更で失敗することを確認済み（ミューテーション確認）。

### 2.1 Quick View 実機 E2E ハーネス（`tools/Mavue.QuickView.Harness`）

**実際のデスクトップを操作する**（Explorer を開き、SendInput で Space/Esc/Alt+Tab を送り、前面ウィンドウと画面ピクセルを検査する）。
ロックされていない対話デスクトップで、操作していない時に実行すること。キーは「送信直前に想定ウィンドウが前面であること」を確認してから送る。

```powershell
$h = ".\tools\Mavue.QuickView.Harness\bin\Debug\net10.0-windows10.0.26100.0\Mavue.QuickView.Harness.exe"
& $h                                   # 既定: panel、7 形式 × 3 回 + シナリオ 15 件（fit。actual は 13 件）
& $h --huge                            # 192 MP JPEG / 100 MP PNG を追加（初回生成に時間がかかる）
& $h --activation hookgrant --cases small-jpeg --iterations 5 --no-scenarios   # 表示方式の比較
& $h --attach-host <timing.jsonl>      # 独立に起動済みのホストに接続して計測
& $h --quicklook-after-host 12         # QuickLook を Mavue の後に起動して共存を検証（二重表示の検出）
& $h --no-navigation                   # 選択追従シナリオ（nav-single-follow / nav-multi-step）を省略
& $h --configuration Release           # Release ビルドのホストで計測
& $h --scale actual                    # 原寸表示モードで計測（既定は fit = 拡大しない）
& $h --pause 2500                      # 各サンプル後に待つ（ホストの非表示後メモリ記録 idle-memory を取るため）
& $h --host-args "--no-wic"            # ホストに追加引数を渡す（例: WIC 直接デコードを無効化して比較）
& $h --list-explorer                   # 診断: Shell COM が見ている Explorer ビューと選択数
& $h --thumb <file>                    # 診断: サムネイルキャッシュ取得（MTA/STA 比較）
```

- 結果は表形式で表示し、`%TEMP%\Mavue.QuickView.E2E\report-*.json` に保存する。テスト画像は同フォルダに生成（日本語フォルダ名）。
- 計測項目: hook / selection / shown / first-frame / thumbnail-visible / full-visible（QPC）、画面ピクセル検出時刻、Quick View が前面か、
  Explorer がフォーカスを保持したか、Quick View が Explorer より上か、デコード時間・サイズ、ホストのピーク WS、Esc で Explorer に戻ったか、
  計測中の**実ユーザー入力の有無**（ハーネス自身の LL フックで注入でない入力を数える）。
- ホストは既定で **WMI 経由で独立に起動**する（ハーネスの子プロセスにすると前面化の結果が有利に歪むことを実測したため）。
- ホストには作業フォルダの `settings-e2e.json` を `--settings` で渡す。**利用者の Quick View 設定は読み書きしない**。
- シナリオ（第 3 工程時点）: `nav-single-follow`、`nav-rapid`（↓×4・↑×4 を 40 ms 間隔、キャッシュ再表示でエラーがないこと）、
  `nav-multi-step`、`tab-switch-follow`（Ctrl+T・Ctrl+Tab）、`small-image-not-enlarged`（240×180 の画像が画面上で 240×180 物理ピクセルか、
  ウィンドウを画面キャプチャして測定。キャプチャは `display-capture.bmp` に保存）、`settings-reload`（非表示中に `settings-e2e.json` の
  `imageScale` を書き換え、次の Space から反映されるか）、`space-then-space`、`activate-explorer-then-space`、`switch-to-other-app`、`alt-tab`。
- 表示領域のシナリオ（第 3 工程の追加分、fit・actual の両モードで実行）: `monitor-open-each`（Explorer を各モニターへ移して画像と PDF を開き、
  そのモニターの DPI で配置され、計算した画像領域が XAML のレイアウトと一致し、画像がその領域に収まるか）、`monitor-move-while-shown`
  （表示中のウィンドウを別モニターへ移し、領域が変われば作り直されるか）、`resize-while-shown`（表示中に縮小・復元して作り直されるか）。
  **テスト中に Explorer と Quick View のウィンドウを各モニターへ動かす**。モニターが 1 台の環境では monitor 系は不合格（対象外）と表示される。選択追従系は「フル品質の画像が画面に出た」ことまで確認する（エラーを切替完了と数えない）。
- **注意（実測）**: 注入入力での成功は実ユーザー入力での成功を保証しない（`docs/QUICKVIEW-POC.md` §3.2）。表示方式に関わる変更は、
  必ずユーザーの実操作（物理キーボード / リモート操作）で確認し、ホストの計測ログ（`--timing-log`）で裏付ける。

テストの Trait 規約:
- `Category=Unit` / `Integration` / `FileFormat` / `Pdf` / `Image` / `Ocr` / `Shell` / `QuickView` / `Perf`
- `Category=RealWindows` — 実機 Explorer・プリンタ・スキャナ・ペン等が必要。CI では除外。
- `Requires=WicHeif` 等 — OS 拡張が必要なテストは、拡張未導入時に**スキップ（理由付き）**し、失敗扱いにしない。

## 3. テストアセット

- 原則として**テスト内で生成**する（WIC で画像生成、最小 PDF をバイト列で生成）。著作権・個人情報の混入を防ぐ。
- 外部コーパス（PDF 互換性: pdf.js テストスイート、PDFium テストファイル等）を使う場合は、ライセンスを
  `tests/assets/LICENSES.md` に記録し、取得スクリプト経由で取得（リポジトリに大容量バイナリをコミットしない）。
- EXIF/GPS テスト用画像は合成データのみ。実在の位置情報を含めない。
- 悪意あるファイル（ファジング由来の破損 PDF・画像）は隔離ディレクトリで扱い、クラッシュしないこと・タイムアウトすることを検証。

## 4. 機能別テスト観点（抜粋）

| 領域 | 必須観点 |
|---|---|
| 安全保存 | 書き込み途中の例外で元ファイルが不変、置換後の属性維持、読み取り専用/ロック中ファイル、ディスクフル、別ボリューム |
| PDF 墨消し | **墨消し後に PDFium・QPDF・外部ツールでテキスト抽出し、対象文字列が存在しないこと**。画像 XObject の画素が消去されていること。注釈・メタデータ・しおり・フォーム値に残存しないこと。増分更新の旧リビジョンが残らないこと（完全書き換え） |
| PDF 暗号化 | 暗号化→QPDF/PDFium で開けること、権限フラグの確認、誤パスワード |
| OCR | 日本語（横/縦）、英語、混在、回転、低解像度、10000px 超の分割、テキスト層の埋め込み→検索 |
| 画像変換 | 各形式往復、JPEG 品質、PNG 圧縮、メタデータ保持/削除（GPS 除去を明示検証）、ICC 保持 |
| Unicode | 日本語・サロゲートペア・結合文字・長いパス（>260）を含むファイル名で開く/保存/Quick View |
| 大容量 | 1 億画素超画像、1000 ページ超 PDF でメモリ上限内・UI 非ブロック（UI スレッド応答時間を計測） |
| Undo/Redo | 全編集コマンドで Apply→Revert→Apply がビット一致 |
| 多言語 | ハードコード文字列検出テスト（XAML / C# の UI 文字列が resw 参照であること） |

## 5. 実機 Windows 検証（手動 + 半自動チェックリスト）

各リリース前に本機（Windows 11 25H2）で実施し、結果を `docs/test-reports/YYYY-MM-DD.md` に記録する。

| 項目 | 現在の可否（本機） |
|---|---|
| Explorer Space Quick View（単一/複数/タブ/デスクトップ/ファイルダイアログ/名前変更中・検索中に誤動作しない） | 可能。単一選択・panel 表示は E2E 27/27 + ユーザー実操作（リモート）で確認済み。複数選択・デスクトップ・ダイアログ・IME は未確認 |
| QuickLook (PaddyXu) 共存時の挙動 | 可能（本機に導入済み）。**未確認**（計測時は QuickLook が起動していなかった） |
| 上段コンテキストメニュー（MSIX 登録後） | 可能（開発者モード or 署名が必要） |
| Preview Pane / Thumbnail / プロパティ / Windows Search | 可能（登録後） |
| 高 DPI（100/125/150/200%）、マルチモニタ間移動 | 要確認（接続モニタ構成次第。仮想ディスプレイで代替可） |
| 印刷 | Microsoft Print to PDF のみ可能。**物理プリンタ: Blocked** |
| スキャン（WIA/TWAIN, ADF, 両面） | **Blocked（スキャナ未接続）** |
| Windows Ink（筆圧・消しゴム） | **Blocked（ペンデバイスなし）** |
| GPU なし / WARP フォールバック | 可能（`D3D_DRIVER_TYPE_WARP` 強制フラグで検証） |

## 6. 性能計測

- **Quick View エンドツーエンド**: テストハーネスが Explorer ウィンドウを開き対象ファイルを選択 → `SendInput` で Space →
  Quick View プロセスが ETW (`EventSource "Mavue-QuickView"`) で各段階のタイムスタンプを出力 → 集計。
  指標: p50 / p95 / 最大、コールド（常駐なし）/ ウォーム。ARCHITECTURE.md §4.4 の目標と比較。
- **起動遅延**: プロセス作成 → 最初のフレーム提示。
- **大容量**: ページ送り遅延、ズーム応答、ピークメモリ（`PROCESS_MEMORY_COUNTERS_EX`）。
- 計測値は `docs/perf/` に日付付きで記録し、回帰を検出する（閾値超過で失敗する Perf テストは基準機でのみ有効）。

### 既存の実測値

| 日付 | 項目 | 値 | 条件 |
|---|---|---|---|
| 2026-10-02 | 空の WinUI 3 アプリ起動→OnLaunched→終了 | 約 730 ms | Debug, JIT, フレームワーク依存, 本機 |
| 2026-10-02 | Mavue.App 起動→最初のフレーム描画→終了（smoke-test） | 595 ms（初回）/ 381 ms（2 回目） | Debug, JIT, WinAppSDK 自己完結, 本機 |
| 2026-10-02 | 同上 | 563 ms（初回）/ 386 ms（2 回目） | Release, JIT（ReadyToRun/NativeAOT なし） |
| 2026-10-02 | Quick View: Space → 小さい JPEG のフル品質表示（panel, 常駐・warm） | 中央値 20.7 ms（アプリ内）/ 画面ピクセル検出 51.9 ms | Debug, 注入入力, 本機 |
| 2026-10-02 | Quick View: Space → 表示呼び出し完了（全形式） | 7〜12 ms | 同上 |
| 2026-10-02 | Quick View: 192 MP JPEG / 100 MP PNG のフル品質表示 | 451 ms / 755 ms（その前に 18〜22 ms でサムネイル表示） | 同上 |
| 2026-10-02 | Quick View 常駐ホスト: 起動 → 準備完了 | 約 160〜170 ms（プリウォーム込み） | Debug |
| 2026-10-02 | Quick View 常駐ホストのワーキングセット | 約 140 MB（小画像表示後）、192 MP JPEG で 176 MB | Debug, 画像ごとの新規プロセス |

| 2026-10-02 | Quick View: Space → 24MP JPEG フル品質表示 | 約 87 ms（WIC 直接）| Release, 注入入力 |
| 2026-10-02 | Quick View: ↓ で先読み済みの次ファイル | 約 30 ms（先読みなし 104 ms） | Release |
| 2026-10-02 | Quick View: Ctrl+Tab で別タブへ追従 | 196〜217 ms（戻り 79〜106 ms） | Release |
| 2026-10-02 | Quick View: E2E 全形式（巨大画像含む）のピーク WS | 462 MB | Release, fit |
| 2026-10-02 | Quick View: ReadyToRun で起動直後の初回フル表示 | 59〜60 ms（JIT 74〜84 ms） | Release |

詳細と条件は `docs/QUICKVIEW-POC.md`。

## 7. CI（予定）

- GitHub Actions `windows-latest`（または自己ホスト Windows 11）で `dotnet build` + `dotnet test --filter Category!=RealWindows`。
- ネイティブ（C++）ビルドは VS Build Tools を持つランナーで。
- リポジトリは現時点で Git 未初期化（BUILD.md 参照）。
