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

主要閲覧機能（2026-10-03）の追加:

| プロジェクト | 追加内容 |
|---|---|
| `Mavue.Core.Tests` | `ViewerZoomTests`（Fit は画像を拡大しない／PDF は拡大可、幅に合わせる、倍率の上下限と表示上限、段階、ホイール、デコード画素数と予算、ポインター位置を保つスクロール量）、`ViewOrientationTests`（4 回で元に戻る、反転 2 回で元に戻る、上下＋左右＝半回転、画素の行き先）、`AppSettingsTests`（既定値・壊れたファイル・往復・最近使ったファイルの順序と上限・再読み込みしてからの更新・不正値）、`SvgDimensionsTests`（単位、viewBox、既定サイズ、SVG でない、DTD/外部実体を処理しない）、`CameraRawDetectionTests` |
| `Mavue.Image.Tests` | `PixelOrientationTests`（回転 3 方向・左右/上下反転・行パディング・小さいバッファの拒否） |
| `Mavue.Shell.Tests` | `AppRegistrationTests`（全拡張子の ProgID、登録内容、他アプリの既定・OpenWithProgids・動詞を残す、削除、不正な exe の拒否。専用 HKCU サブキー） |
| **全体** | **387（386 成功、1 スキップ: 開発者モードが必要）** |

実機 E2E（Release、2026-10-03）:

- 本体 `--app`: 11/11 PASS。追加 `app-zoom-rotate`（24 MP JPEG: Ctrl+1 → 6000 px で再デコード、Ctrl+-/+、Ctrl+ホイール、Ctrl+R で縦横入れ替え、Ctrl+0、次のファイルで元に戻る）、
  `app-pdf-navigation`（サムネイル 3 件、Ctrl+G→3→Enter、Home/End、ページの回転）、`app-gif-controls`（Space で停止中はコマが出ない、. / , でコマ送り、再開）、
  `app-svg`（画面に描画、Ctrl++）、`app-info-copy-settings`（情報パネル 9 項目、Ctrl+C でクリップボードに CF_HDROP、閉じた後の設定ファイルに最近使ったファイル・ウィンドウ位置・情報パネル）。
  ハーネスは本体に専用の設定ファイル（`--settings-file`）を渡し、利用者の設定・最近使ったファイルは変えない。
- Quick View（既定の全シナリオ）: 21 サンプルすべて OK、シナリオ 21/22 PASS。追加 `quickview-zoom-rotate`（Explorer にキーボードを残したまま Ctrl+ホイール ×2 → 表示幅で再デコード、
  クリックでアクティブ化 → Ctrl+R・Ctrl+-、↓ で SVG を表示、Esc）。`cli-quickview` は 1 回目に前面化の権利が得られず FAIL、単独の再実行で PASS
  （同じ症状は変更前の 2026-10-02 の実行でも多数。ハーネスから起動したクライアントの前面化権の揺らぎ）。
- 手動: Shell の動詞「Mavue で開く」（`InvokeVerbEx`）で Mavue.exe がそのファイルで起動、Quick View の「Mavue で開く」ボタン（UI Automation で押下）で Mavue.exe が起動、
  BMP/TIFF/ICO が本体で表示されること、SVG の `<text>` が描かれないこと（Direct2D の制限）。

PDF ビューア（PDFium、2026-10-03）の追加:

| プロジェクト | 追加内容 |
|---|---|
| `Mavue.Pdf.Tests`（新規） | PDFium の読み込み、日本語パスのファイル、表示中の名前変更（削除共有）、壊れたファイル/存在しないファイルの理由、呼び出し側バッファへの描画（余白・不透明）、文字抽出と位置（往復）、表示回転時の位置、検索（大文字小文字、全ページ、空文字）、リンク（ページ・Web、javascript: は除外）、アウトライン、60 ページの巡回（ページキャッシュ）、並列呼び出しの直列化、破棄後の例外、ストリームから開く。テスト PDF は `TestPdf`（ハーネスと共用） |
| `Mavue.Core.Tests` | `PdfPageLayoutTests`（連続・見開き・単一ページの配置、ページ/幅に合わせる倍率、表示範囲と現在ページ、20,000 ページ、空・不正な倍率） |
| **全体** | **410（409 成功、1 スキップ: 開発者モードが必要）** |

実機 E2E（本体、Release）: 13/13 PASS。追加 `app-pdf-text`（8 ページ: Ctrl+F で 8 件、F3 で 2 ページ目、ドラッグ選択＋Ctrl+C で
「The quick brown fox…」、リンクで 8 ページへ、Web リンクは記録のみ（`--no-launch`）、目次 8 項目・「Chapter 5」→ 5 ページ（UI Automation））、
`app-pdf-layouts`（600 ページ: End → 600、PageUp → 599、30 ページ戻った後もページ要素 5、プライベートメモリ +16 MB、見開きで 1・2 ページが横並び、
PageDown が見開き単位、単一ページ、連続に戻す）。既存 `app-pdf-pages` は単一ページ表示（Ctrl+Shift+1）で実行、`app-pdf-navigation` の回転確認は `pdf-rendered` で判定。
ページ位置はアプリのトレース `pdf-geometry`（描画時・スクロール確定時にウィンドウ内の物理ピクセル）から求める。

E2E で見つけて直した不具合: 600 ページを素早く移動した後の Home で異常終了（0xC000027B / RO_E_CLOSED。サムネイルのキャッシュから追い出した
画像を、まだ表示している項目があるのに破棄していた）、サムネイル一覧・検索結果の選択変更通知が遅れて届きページが戻る（クリックで移動する方式に変更）、
プログラムからのページ移動後に現在ページが前のページに戻る、PDF を開いた直後に暫定の倍率（2%）を通知、設定の同時更新で情報パネルの設定が失われる。

手動: Edge で作成した日本語 PDF で文字抽出・「東京」の検索・リンクを確認。

#### リリース候補（RC）の最終テスト（2026-10-04 19 時）

- Release クリーンビルド: 警告 0・エラー 0。単体 **445（444 成功、1 スキップ、失敗 0）**。整合チェック 501 ファイル（EULA.txt を含む）通過。
- ZIP（日本語とスペースを含む展開先、Web のマーク付き、`Install.cmd`）→ 本体 E2E **15/15**、Quick View E2E **21/23**（既知 2 件）、Explorer E2E 8/10
  （再実行で PDF は PASS。`explorer-monitors` は Explorer のプレビュー ウィンドウ幅 68 px という環境要因）、Explorer 再起動直後の Quick View 6/6、
  更新（PDF 選択・設定を維持）、アンインストール（Mavue の登録 0）、MSIX のインストール → 更新 → 削除。詳細は docs/PACKAGING.md §4.2。
- テスト運用の注意: 実行ログを `tail -f` で監視すると Windows PowerShell の `Add-Content` が書けなくなる（共有違反）。ログは完了後に読む。

#### 配布の最終確認・MSIX の Explorer 連携の調査（2026-10-04 夜）

- Release クリーンビルド（bin\Release の dll/exe 0 を確認後 `--no-incremental`）: エラー 0。単体 **445（444 成功、1 スキップ、失敗 0）**。
- 成果物: `tools/build-release.ps1` の整合チェック（ZIP・MSIX・フォルダーの 500 ファイルの SHA-256 一致、必須ファイル、署名、シンボル、ZIP 項目名）が 0.1.0・0.1.1 とも通過。
  チェックで ZIP 項目名のバックスラッシュ（Windows PowerShell の Compress-Archive / CreateFromDirectory）を検出し修正。署名 Valid 15/15。
- **ZIP 版**（操作のない状態で実行。ハーネスの実入力イベント 0〜9）: インストール 3 秒 → Explorer E2E **9/10**（`explorer-theme` はモニター移動直後の順序の問題。
  ハーネスを修正後 2/2 PASS）→ 本体 E2E **15/15** → Quick View E2E **21/23**（既知 2 件、`cli-quickview` は単独で PASS）→ 0.1.1 へ更新（PDF プレビューの選択・
  設定ファイルの内容を維持、登録は新フォルダー、Quick View 再起動）→ 上段メニュー → アンインストール（キー 0、PDF は Edge に戻る）。
  ※ 利用者の PC 操作が重なった回（実入力 39,089）は画面サンプリング系が失敗したため、操作を止めてもらって再実行した。
- **MSIX**: `msix-probe.ps1` 11 PASS + SVG サムネイル KNOWN。上段メニュー Invoke、パッケージのプレビューは .x3f のみ（Explorer 再起動後も同じ）、
  更新は使用中だと 0x80073D02 / `-ForceApplicationShutdown` で 3 秒、削除後に何も残らない。
- 調査（MSIX）: パッケージのハンドラーは拡張子キーに書かれない。Mavue だけが宣言する種類でのみ使われる。拡張子の既定 ProgID を Mavue にしても SVG は不変、
  HKCU の拡張子キーからパッケージの COM クラスを指すと `REGDB_E_CLASSNOTREG`。既定のアプリにした場合はユーザー操作が必要で未確認。
- 見つけて直した不具合: Explorer 起動/再起動直後の上段メニューの失敗（デスクトップ未登録。Explorer ウィンドウ経由に）、ZIP の項目名、
  インストーラーの表示言語（PowerShell の UI 言語ではなくユーザーの言語設定に合わせる）、MSIX プローブの判定タイミング。
- 試して戻した変更: パッケージの代理プロセスでの動画/音声ストリームを Global Interface Table 経由にする修正（プレビューがハング）。

#### 配布物の完成（2026-10-04）

- Release クリーンビルド（`dotnet clean` 後に bin\Release の dll/exe が 0 であることを確認 → `--no-incremental`）: 警告 0・エラー 0。単体 **445（444 成功、1 スキップ、失敗 0）**。
- リリース成果物（`tools/build-release.ps1`）: 15 ファイルが署名 Valid、未署名の Microsoft ファイル 0、Windows SDK 投影 2 ファイルが REDIST 対象のパッケージ版と SHA-256 一致、
  `licenses\` に Mavue・.NET・Windows App SDK・WebView2・PDFium の全文。
- **ZIP 版**（開発用の登録をすべて外してから）: MSIX がある状態の Install は中止 → インストール → Explorer E2E（インストール先の Mavue.exe、`--explorer --explorer-restart`）**9/10**
  （`explorer-theme` は最初の PNG のみ失敗、単独再実行で PASS）→ Quick View E2E（インストール先のホスト）既知の 2 件以外 PASS（`cli-quickview`・`media-switch` は間欠、
  単独 2 回とも PASS）→ 本体 E2E **15/15** → 設定の「Mavue について」を UI オートメーションで確認 → 0.1.1 への更新（PDF プレビューの選択・設定を維持）→
  上段メニュー（IExplorerCommand::Invoke、コールド 4 回を含め成功。更新直後の 1 回のみ E_FAIL）→ アンインストール（キー 0、使用中ファイルは次回サインインで削除）。
- **MSIX**（同じく登録をすべて外してから）: `tools/Mavue.QuickView.Harness/scripts/msix-probe.ps1` 11/12（起動・self-contained の読み込み元・Quick View・
  パッケージ内の登録ガード・COM 3 クラス・上段メニュー・PDF サムネイル・設定の保存先）。SVG サムネイルとプレビュー ウィンドウ（ハーネスの `explorer-preview-pane`）は
  Mavue が使われない（docs/PACKAGING.md §5.3）。削除後に何も残らないことを確認。
- 最後に開発用の登録（bin\Release、識別パッケージ、サインイン時起動なし、PDF は Edge）と常駐 Quick View を元に戻した。
- ハーネスの注意: Windows PowerShell の `Start-Process -Wait` は子孫プロセスも待つため、常駐 Quick View を起動するインストーラーを包むと終わらない。
  インストーラーのスクリプト自身は `Process.WaitForExit`（そのプロセスのみ）に変更済み。

#### 配布準備（2026-10-03 夜）

- 単体（Release）: **445（444 成功、1 スキップ）**。追加: 暗号化 PDF（`EncryptedPdf_*`。テスト用の暗号化 PDF は RC4-40 を手書き生成、依存追加なし）、PDF プレビューの個別切替・32 ビット ビュー登録、MSIX マニフェスト。
- 実機 E2E（Release）: Explorer **11/11**（`--explorer --explorer-restart --light-theme`: 登録、プレビュー各形式、PDF 切替、サムネイル、キー、DPI 3 台、テーマ暗/明、32 ビット、ショートカット比較、再起動→解除→再登録）。
  本体 **15/15**（追加 `app-pdf-password`・`app-monitors`）。Quick View **21/23**（失敗は既知の 2 件: 注入キーとプレビューの組み合わせによる `tab-switch-follow`、一過性の `cli-quickview`）。
- MSIX: `tools/package-msix.ps1` で作成・署名 → `Add-AppxPackage` → パッケージ版の Mavue と Quick View の起動を確認 → 削除。
- 見つけて直した不具合: Explorer のプレビューが 100 % のモニターで 150 % 分ずれる（prevhost がシステム DPI 対応のため。ウィンドウを per-monitor v2 で作成・配置）、
  暗色モードでプレビューが白（Explorer が白黒を渡す）、32 ビット アプリで `REGDB_E_CLASSNOTREG`（x86 版を 32 ビット ビューに登録）。
  ハーネス: クリップボードの一時的な使用中で異常終了していたのを再試行に、明色テスト後に Quick View の常駐が残るのを解消。

#### Explorer のプレビューウィンドウ・サムネイル（2026-10-03）

| 種類 | 内容 |
|---|---|
| 単体 `Mavue.Shell.Tests` | `ShellHandlerRegistration`（非公開の HKCU サブキー＋擬似の「現在のハンドラー」: 既存が無い拡張子だけ結び付け、PDF の既存プレビューは既定で残す、`--prefer-mavue-preview` で奪って解除で元の値に戻す、効かない場合は元のまま、解除は Mavue の値だけ）。`PreviewNativeTests`（DLL を直接読み込み COM で呼ぶ: PDF/SVG/PNG/EXIF 回転のサムネイル、壊れた・空・巨大寸法・非 SVG のファイルは 3 秒以内に失敗、STA＋メッセージループで画像・GIF アニメ・SVG・600 ページ PDF（近くのページだけ描画）・WAV のプレビュー、壊れたファイルの文言、Unload が待たない（メディアも 100 ms 未満）、DLL がアンロード可能） |
| **全体（Release）** | **438（437 成功、1 スキップ: 開発者モードが必要）** |

実機 E2E（Release、`--explorer --explorer-restart`）: 7/7 PASS（`explorer-register`・`explorer-preview-pane`・`explorer-preview-pdf`・`explorer-thumbnails`・
`explorer-preview-keyboard`・`explorer-shortcuts`・`explorer-lifecycle`）。診断ログ（`HKCU\Software\Mavue\Shell` の `TraceFile`。prevhost は低整合性なので
`%USERPROFILE%\AppData\LocalLow\Mavue`）・画面の画素・プレビュー用プロセスの音量・Explorer への WM_NULL 往復で判定。

- プレビュー: PNG（赤）・24 MP JPEG（3240×2160 にデコード）・GIF（色が変わる）・SVG（円の黄色）・WebP/AVIF/HEIC・MP4（クリックで音と赤い映像）・MP3/M4A/WMA/FLAC/WMV/WAV、
  壊れた JPEG・空の PNG は文言。Explorer の応答は常に 13 ms 以内、プレビュー用プロセスは 1 つのまま。テキスト・PDF は他の previewer のまま（Mavue は呼ばれない）。
- PDF（`--prefer-mavue-preview` のときだけ）: 8 ページ・日本語 PDF、ホイールで後ろのページを描画、600 ページで描画 2〜3 ページ・prevhost 100〜213 MB、壊れた PDF は文言。`--register` で Edge に戻る。
- サムネイル: Windows 経由（`IShellItemImageFactory`、分離プロセスの dllhost）で PDF 181×256・SVG 256×192（19〜34 ms）、壊れた PDF は失敗、PNG は Windows のまま。大アイコン表示で SVG の青・黄が画面に出る。
- 登録 → Explorer 再起動 → 動作 → `--unregister`（Mavue のクラス・結び付けが残らず、PDF の Edge は不変、Explorer が Mavue を呼ばない）→ `--register`。

E2E で見つけて直した不具合: メディアのプレビューを閉じるとき Media Foundation の終了（約 200 ms、COM で待つ）を prevhost の UI スレッドで行っていたため、
その間に Explorer で押された ↓ が失われた（Quick View の `media-formats` が FLAC で失敗。Explorer と prevhost の子ウィンドウは入力を共有）→ 終了を別スレッドへ。
描画先の子ウィンドウをトップレベルにして残すと前面を奪ったため、message-only ウィンドウにした。

既知（テスト環境のみ）: 注入したキー（SendInput）の Ctrl+T・Ctrl+Tab は、プレビューウィンドウに**プロセス外の previewer**（Windows のテキスト・Edge の PDF・Mavue）が
出ている間は効かない（`explorer-shortcuts` で 3 つとも同じことを確認）。**物理キーではタブが開く**（2026-10-03 手動確認）。このため Quick View の
`tab-switch-follow` はプレビューウィンドウを開いたままだと失敗し、閉じた状態・Mavue を登録解除した状態では PASS。

Quick View 全体（Release、プレビューウィンドウを開いたまま）: 21/23 PASS（上記の `tab-switch-follow` と、以前からの一過性の `cli-quickview`）。本体 13/13 PASS。

#### Quick View の PDF を本体と共通化（2026-10-03）

| プロジェクト | 追加したテスト |
|---|---|
| `Mavue.Core.Tests` | `PdfPageLayoutTests`（見開き・表紙単独の配置とページ送り）、`AppSettingsTests.MissingProperties_KeepTheirDefaults` |
| `Mavue.QuickView.Tests` | `QuickViewSettingsTests.MissingPdfLayout_IsContinuous`・`PdfLayout_RoundTrips` |
| **全体（Release）** | **414（413 成功、1 スキップ: 開発者モードが必要）** |

実機 E2E（Release）: Quick View 23 シナリオ中 21 PASS → 失敗 2 件を単独で再実行して PASS（`monitor-open-each` はハーネス側の判定を修正、
`cli-quickview` は前面化が許可されず Esc が届かなかった一過性の失敗）。本体 13/13 PASS。実操作の混入 0。

- `quickview-pdf`（新規）: 8 ページ PDF を Explorer から Space → PDFium 表示・連続スクロール、Explorer 前面のまま PageDown で 2 ページ目（選択は変わらない）、
  ホイールでスクロール、Quick View をクリックして Ctrl+F で 8 件・F3 で次、検索欄の Esc は検索だけ閉じる、Ctrl+G 1 Enter、ドラッグ選択＋Ctrl+C、
  文書内リンクで 8 ページ、Web リンクは記録のみ（`--no-launch`、開かれていないことも確認）、サイドバー（UI Automation）の目次「Chapter 5」→ 5 ページ、
  Ctrl+Shift+4 で 2–3 ページが横並び、↓ で Edge 作成の日本語 PDF へ移動して「東京」2 件・Ctrl+A＋Ctrl+C で 2 ページ分の日本語テキスト、
  F5 プレゼンテーション（→ で次ページ、Esc で終了、Quick View は開いたまま）、Esc で閉じる。
- `app-pdf-layouts` に追加: Ctrl+Shift+4（1 ページ目が単独、2–3 が横並び）、F5 プレゼンテーション（モニター全体を覆う・→ で 2 ページ・Esc で終了・ウィンドウは残る）。
- pdfium.dll を外した Debug ホストで `pdf-pages` PASS（Windows.Data.Pdf へのフォールバック、`decoder=windows-data-pdf`）。

E2E で見つけて直した不具合: 設定ファイルに無い項目が既定値にならない（System.Text.Json のソース生成が init 専用プロパティを常に代入するため、
Quick View の表示方法が「単一ページ」になり、本体の「サムネイル表示」も既定の true にならなかった。`set` に変更し単体テストを追加）。
ハーネス: UI Automation を Quick View のウィンドウハンドルから探す（常駐ホストは複数のトップレベルウィンドウを持つ）、ToggleButton は TogglePattern で操作、
PDFium 表示の「領域に収まる」判定は 1 px の丸めを許容。

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
