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

#### Quick Preview Phase 0（2026-10-06、Quick View の内部分割）

- 変更前の基準: 単体テスト 445 件（成功 444・スキップ 1・失敗 0）。E2E（Release）: 7 形式 × 3 回の表示 21/21 PASS
  （例: Space → 表示呼び出し 10〜13 ms、小 JPEG のフル品質 33 ms、24 MP JPEG 98 ms、PDF 41 ms）。同じ実行のシナリオは、計測中に実ユーザー入力
  （約 3.7 万件）が重なったため失敗が多く、基準として使わない。
- Phase 0 の実装後: ソリューション全体のビルド成功（警告 0）。テストの実行と新規テストの作成は利用者の指示で後回しにし、2026-10-29 に実施（下記）。

#### Quick Preview Phase 0 のテストと回帰確認（2026-10-29）

- 修正前（Phase 0 の実装そのまま）: 単体 **445（444 成功、1 スキップ、失敗 0）** で基準と一致。E2E（Release）: 表示 **21/21**、シナリオ 23/24（`cli-quickview` のみ。既知の前面化の揺らぎで単独再実行は PASS）、実ユーザー入力 0。
  Space → 表示呼び出し 8.6〜10.9 ms、小 JPEG のフル品質 25 ms、24 MP JPEG 91 ms、PDF 32 ms（基準と同等以上）。
- 見つけて直した不具合（ARCHITECTURE §4.7.6）:
  1. 画像として扱うファイル（データ・テキスト・壊れた JPEG 等）へ移ると、「非対応」「プレビューできません」が出た後に、遅れて終わったシェルのサムネイル取得が
     「読み込み中」で上書きしていた（分割前から。実機で 3 ファイルとも発生を確認）。クラウドのみの画像でも「読み込み中」が残った。→ `ItemDisplayState`。
  2. 異常終了後の再起動で、`--quickview` 付きで起動した常駐プロセスは他の引数（`--settings` 等）も失っていた。→ `RestartArguments`（`--quickview` 以降だけを除く。クォートも Windows の規則どおり）。
  3. ZIP の項目名で `.. `（末尾の空白）・`...` など点と空白だけの名前が安全と判定されていた（Windows が末尾の点・空白を落とすため `..` になり得る）。`./` は空の名前として扱う。
- 先読みの判断を `PreviewPrefetchPlan`（Core、純粋関数）に切り出し、`NeighborPrefetcher` はそれに従う。先読みの記録 `prefetched` に形式を追加、
  診断用に `thumbnail-suppressed`（メッセージの後に届いたサムネイル結果を捨てた）を追加。

| プロジェクト | 追加したテスト | 件数 |
|---|---|---|
| `Mavue.Core.Tests` | `PreviewContentRoutingTests`（全形式 × 全アクセス状態 × PDFium 有無、メディアのクラウドは再生しない、SVG は WIC に回さない、PDF は PDFium 有無で文書表示/ページ画像、拡張子ではなく中身で判定） | 145 |
| | `PreviewResourcePolicyTests`（段階の境界値 6/12 GiB・2/4 コア・省エネ・リモート、不明な値で下げない、High = PoC の値、下位の段階は上限が大きくならない） | 32 |
| | `PreviewPrefetchPlanTests`（次 → 前、Low は次だけ、上限・クラウド・フォルダーは先読みしない、先読みするのは画像だけ = PDFium 表示中の PDF と SVG は先読みしない） | 26 |
| | `ArchiveEntryNameTests`（絶対パス・UNC・ドライブ相対・`..`・点と空白だけ・ADS の `:`・制御文字・予約デバイス名・長さと深さの境界・正規化） | 71 |
| | `DecompressionGuardStreamTests`（項目・全体・圧縮率の上限と境界、実際の ZIP 爆弾〔64 MiB のゼロ〕を圧縮サイズの 200 倍で停止、通常の ZIP は全部読める、キャンセル、非同期、スレッド安全） | 20 |
| `Mavue.QuickView.Tests` | `ItemDisplayStateTests`（メッセージの後に「読み込み中」・サムネイル・前の画像の消去が上書きしない、クラウドはサムネイルのみ、次の項目に持ち越さない） | 12 |
| | `RestartArgumentsTests`（`--quickview` 以降を除く、大文字小文字、1,023 文字の境界、`CommandLineToArgvW` で読み戻して一致） | 19 |
| | `PreviewCacheTests`（`BudgetBytes`: 下げると古い順に即解放、上げても消さない、以後の追加に適用、0 以下は拒否） | 6 |
| **全体（Release）** | **776（775 成功、1 スキップ: 開発者モードが必要、失敗 0）**。追加 331 件（Theory のデータ行を含む） | |

- 修正後の E2E（Release）: 表示 **21/21**、シナリオ **24/25**（`cli-quickview` のみ同じ揺らぎ。単独再実行で PASS）、実ユーザー入力 0。
  新シナリオ `message-not-replaced`: 画像 → 乱数データ（.bin）→ テキスト → 壊れた JPEG を ↓ で移動し、14〜19 ms でメッセージが出て 0.9 秒後もそのまま
  （UI Automation で画面の文字を確認。3 件とも遅れたサムネイル結果を抑止）、↑ で画像に戻る。
- 実機ログで確認: 段階 High（16 論理プロセッサ・63 GB）、再起動の登録 HRESULT 0、PDF ページ（Windows.Data.Pdf）の先読み 0・先読みエラー 0
  （先読みは JPEG/PNG/WebP/AVIF のみ）、閉じた後のメモリ返却（`idle-memory`）、再生の解放（`media-*`）。
- 再起動の登録内容: `--settings <パス> --timing-log <パス> --quickview <ファイル>` で起動した常駐プロセスから `GetApplicationRestartSettings` で読み出し、
  `--settings "…" --timing-log "…"`（ファイルなし、フラグ 0xC = 更新・再起動時は対象外）であることを確認。
- 未確認: Windows による実際の再起動（60 秒以上動いた後の異常終了が必要）、Low/Balanced 段階での実動作（開発機は High。段階の選択と上限は単体テストのみ）、
  ZIP の表示（Phase 2。安全上限は単体テストのみ）。

#### Quick Preview Phase 1（2026-10-29: ピクセル Inspector・フィルムストリップ・Pin・外へのドラッグ・Universal Preview）

| プロジェクト | 追加したテスト | 件数 |
|---|---|---|
| `Mavue.Core.Tests` | `PixelInspectionTests`（表示回転 8 通りの逆変換、要素上の位置 → 画素（拡大・縮小・端・範囲外）、EXIF 1〜8 → ファイル内の画素（範囲内・1 対 1・左上の行き先）、タイルの整列と切り詰め、HEX/RGB/Alpha の表記） | 46 |
| | `UniversalFileInfoTests`（内容を読まない事実、Mark-of-the-Web: ゾーンとホスト名のみ・URL の個人情報を残さない・不正な値・巨大なストリーム、フォルダー・存在しない・デバイス パス、種類と MIME、項目数の上限と取り消し、ドラッグはファイルのみ・移動しない） | 50 |
| | `PreviewContentRoutingTests` 追加（内容が不明で拡張子が文書・アーカイブ等 → 情報カード、それ以外はデコーダーを試す、内容が拡張子に優先） | 17 |
| `Mavue.QuickView.Tests` | `PreviewPinTests`（固定中は Explorer の選択・空の選択に追従しない、解除で追従、閉じる/新しい Space で解除、固定中の矢印は Explorer を動かさない、`GoTo`） | 10 |
| | `FilmstripModelTests`（範囲の上限と切り詰め、端に近づいたら読み直し・ビューの端では読まない、範囲外、パスのない項目の欠け、ビュー順、複数選択、前後） | 18 |
| | `QuickViewSettingsTests` 追加（フィルムストリップ・Inspector は既定でオフ、往復で他の設定を壊さない） | 2 |
| `Mavue.Image.Tests` | `WicPixelReaderTests`（PNG の正確な値とストレート α、端のタイル、範囲外・画像でない・存在しない、大きすぎる範囲の拒否、**EXIF 1〜8 の各方向で Windows のデコーダーと同じ色**） | 13 |
| **全体（Release）** | **932（931 成功、1 スキップ: 開発者モードが必要、失敗 0）**。Phase 0 時点の 776 件・基準 445 件はすべて成功のまま。追加 156 件 | |

実機 E2E（Release）:

- 新シナリオ 5 件すべて PASS: `pixel-inspector`（400×300 PNG 上で赤・#123456・半透明の青の X/Y・RGB・HEX・Alpha、画像の外で消える、タイル 3 枚）、
  `filmstrip`（6 枚中 3 枚目で Space → プレビュー表示の後に 6 件を Explorer の順で表示、5 枚目のクリックで Explorer が選択し Quick View が追従、複数選択は選択した 3 件のみ）、
  `pin`（固定中は Explorer の ↓ で変わらない・他のアプリがアクティブでも閉じない・Explorer の Esc で閉じない、解除で Explorer の選択に追従）、
  `drag-out`（ファイル名と画像の 2 経路で、同じドライブの別フォルダーへドロップ → コピーされ元は残る。操作は Copy のみ）、
  `info-card`（.docx・.zip はデコーダーを試さず即カード、ZIP は入手元のホスト名、乱数データはメッセージ付きカード、フォルダーは「3 個の項目」）。
- 全体: 表示 **21/21**、シナリオ **28/29**（`cli-quickview` のみ既知の前面化の揺らぎ、単独再実行で PASS）。Space → 表示呼び出し 10.8〜12.6 ms、
  小 JPEG のフル品質 28 ms、24 MP JPEG 89 ms、PDF 37 ms（基準と同等以上）。常駐時のメモリ（非表示後）: プライベート 163.5 MB（Phase 0 修正後の実行 161.4 MB）、
  起動から準備完了 297 ms（同 287 ms）。新機能はオフのとき何も動かない。
- 本体（`--app`）: 14/15、`app-multiple-files` は全体実行で 1 回だけ矢印の 1 ステップが重複し、単独 3 回とも PASS（共通の表示面 `ViewerSurface` の変更は
  Quick View が有効にするドラッグだけで、本体では無効）。
- 既存シナリオの変更: `message-not-replaced` のテキスト ファイルは Phase 1 から即情報カード（メッセージなし）になるため期待値を更新。
- E2E で見つけて直した不具合: フィルムストリップ・Pin のボタンのクリックで Quick View が閉じる（前面ウィンドウが Quick View の子ウィンドウとして通知され、
  他のアプリへの切り替えと判定していた）。フォルダーの項目数が UI オートメーション名に反映されない（読み上げが「数えています…」のまま）。
- 目視（スクリーンショット）: フィルムストリップ（現在の項目に枠）、隅の Inspector パネル、情報カード（入手元を含む）の配置。
- 未確認: Ctrl なしのホイールズーム（F03.31、今回対象外）、色管理後の値（Inspector はファイルの値）、クラウドのみのファイルでのフィルムストリップの実機確認、
  巨大フォルダー（数万件）でのフィルムストリップの体感、他のアプリ（メール・Office）へのドロップ、タッチ・ペンでのドラッグ。

#### Phase 1 の実機確認（2026-10-29、`--phase1-real`）

追加シナリオ（既定の実行には含めない。`--phase1-real` で実行。Office は `MAVUE_E2E_OFFICE=1` のときだけ）:

| シナリオ | 結果 | 内容 |
|---|---|---|
| `filmstrip-cloud` | PASS | **テスト用の同期ルート**（Cloud Files API、%TEMP% に作成し終了時に登録解除。OneDrive には触れない）にクラウドのみのファイル 5 件（jpg/png/mp4/pdf/heic）。データ要求（FETCH_DATA）を要求元プロセスごとに記録し即拒否する。フィルムストリップに 6 件表示、↓ で各ファイルへ（画像・PDF はクラウドの案内、動画は情報カード）。**Quick View からのデータ要求 0 件**、終了後もすべてクラウドのみのまま |
| `filmstrip-large` | PASS | 30,000 ファイル（ハード リンク）のフォルダーの中央で Space → フル表示 32〜38 ms（通常と同じ）。範囲 121/30,000 件の読み取り 169〜185 ms（表示の後）。↓×50 で 1 回だけ読み直し、エラー 0。End で末尾 61 件。常駐プロセスのメモリ +10〜12 MB |
| `inspector-transforms` | PASS | 位置を色に符号化した TIFF（EXIF 6）で、100 %・Ctrl+R で回転後・Ctrl+ホイールで 2.07 倍の各 3 点: 報告された X/Y の画素の色＝報告された色（完全一致）、画面上の色とも一致。JPEG（EXIF 8）の 4 象限で報告色＝画面の色 |
| `pin-real` | PASS | A: P で固定 → Explorer で別ファイルをマウスでクリック（Quick View は変わらない）、Alt+Tab、他のアプリ、Explorer のウィンドウを閉じる → いずれも閉じない、Quick View 上の Esc で閉じる。B: ボタンで固定 → Explorer で Space → 閉じる。C: P で固定 → Explorer で ↓↓ → P で解除 → Explorer の選択（04）に追従 |
| `drag-out` | PASS | ファイル名・画像から同じドライブの別フォルダー（Explorer）へ → コピー、元は残る（3 回連続） |
| `drag-probe` | PASS | 別プロセスの WinForms のドロップ先: 許可はコピーのみ、形式 Shell IDList Array・FileDrop・FileContents・FileGroupDescriptorW・Preferred DropEffect、ファイルのパスを受け取る |
| `drag-apps` | Edge PASS（3/3）・ペイント FAIL・Word 対象外 | Edge（別プロファイル）でファイルが開く。ペイントは開かない（下記）。Word は 1 回目に挿入を確認したが、Office 未サインイン（ライセンス ダイアログ）で自動操作が不安定なため利用者の指示で対象外 |
| `drag-paint-control` | 参考 | 同じファイルを Explorer からペイントへドラッグすると開く |

ドラッグについて分かったこと（ARCHITECTURE §4.7.8）:

- ペイントは WinUI のドラッグ（`DataPackage`）を受け付けない。許可する操作をコピー/移動/リンクすべてにしても同じ（実験。元に戻した）。
- Explorer と同じシェルのデータ オブジェクト＋`SHDoDragDrop` を試したが、WinUI がプロセス全体で mouse-in-pointer を有効にしている（`IsMouseInPointerEnabled = true` を実測）ため、
  ドラッグ ループがボタンを離したことを受け取れない（7〜13 秒続き Esc で終了）。別プロセスで行う案は起動に約 1 秒かかり、ボタンを離した直後に起動した場合に
  次のマウス移動でドロップされる危険があるため不採用。WinUI のドラッグに戻した（開始判定はホストで行い `StartDragAsync`）。ペイントへのドラッグは残課題。

その他:

- ハーネスの修正: 画像の範囲の検出から Inspector パネルを除外、ドロップ先・クリック対象の Explorer を Quick View と重ならない位置へ移動、Explorer のキーボード フォーカスを
  クリックで戻してから Space/↓、ハード リンクは 1 ファイル 1,023 個までのため元ファイルを分散、Word の COM 呼び出しの再試行。
- 全体の実行（`--phase1-real` 込み）: 表示 21/21。実行中に実ユーザー入力が 1,450 件あった回は `pin-real`・`media-video` が失敗し、単独再実行で PASS
  （`media-video` は重い準備の直後だと「開いた直後に再生中」の判定が間に合わない。単独では 113 ms で再生）。
- 単体テスト: 932（931 成功、1 スキップ、失敗 0）。
- 実行後に残るもの: %TEMP%\Mavue.QuickView.E2E\…「Phase1 大量」（30,000 個のハード リンク。実データは 32 ファイル分）、%TEMP%\Mavue.E2E.EdgeProfile。
  同期ルートはテスト終了時に登録解除・削除。

#### Quick Preview Phase 2（2026-10-30: ウィンドウのデザイン・市松模様）

| プロジェクト | 追加したテスト | 件数 |
|---|---|---|
| `Mavue.Core.Tests` | `TransparencyRuleTests`（市松模様を出す形式・出さない形式、設定の既定オン・古い設定ファイルでもオン・オフの保存） | 18 |
| **全体（Release）** | **950（949 成功、1 スキップ、失敗 0）** | |

実機 E2E（Release）:

- 新シナリオ: `checkerboard`（半透明の象限は 2 色が交互、不透明の象限は 1 色。「…」メニューでオフにすると 1 色）、`toolbar-keyboard`（F6 でツールバーのボタン、
  そこで Esc → 閉じずに内容へ、もう一度 Esc で閉じる）。どちらも PASS。
- 全体: 表示 **21/21**、シナリオ 29/31 → 失敗 2 件を確認: `quickview-zoom-rotate` はハーネスの判定点がツールバー移動で SVG の外に出ていたため（判定点を追加して PASS。
  Ctrl+ホイールのカーソル中心ズーム自体は PASS）、`cli-quickview` は既知の揺らぎ（単独で PASS）。
- **性能の回帰を発見して修正**: 最初の実装（コンテンツをタイトルバーへ拡張）で Space → 表示が約 10 ms 遅くなった（show-call → shown 3.7 → 13.7 ms）。
  同じ日の RC 0.1.0 のホストとの比較と組み合わせ試験で原因を特定: `ExtendsContentIntoTitleBar`（13.8 ms）とキャプション ボタンの色の変更（13.5 ms）が
  それぞれ単独で遅くする。タイトルバーなし（`SetBorderAndTitleBar`）も 10.8 ms。Mica は影響なし（3.6 ms）。→ Windows 標準のタイトルバーのまま
  ツールバーと Mica で一体に見せる設計に変更し、3.8 ms（RC と同等）。Space → 表示呼び出し 10.3〜13.5 ms、24 MP JPEG のフル品質 83〜92 ms。
- 起動時の例外を発見して修正: `AccessibilitySettings.HighContrastChanged` の購読が CoreWindow のないアプリで 0x80070490（未処理例外の記録に HRESULT と
  メソッド名を追加して特定）。
- 目視（スクリーンショット、ダーク）: タイトルバー＋ツールバーが同色の帯、画像の舞台は #1E1E1E、市松模様は半透明部分にだけ見える、Inspector は隅。
- 未確認: ライト テーマ・ハイコントラストの実機表示（利用者のシステム設定を変えないため）、壁紙のある環境での Mica の見え方。

#### Quick Preview Phase 3（2026-10-30: ZIP Preview・動画/音声の軽い操作・ホイールシーク・波形）

利用者の方針（2026-10-30）: 機能ごとにテストを回さず、Phase 3 の実装（ZIP と動画・音声）がまとまってから一括で実行した。

| プロジェクト | 追加したテスト | 件数 |
|---|---|---|
| `Mavue.Core.Tests` | `ZipArchiveTests`（CRC-32、中央ディレクトリの一覧、CP932/フラグなし UTF-8 の名前、自己解凍形式の先頭データ、ZIP64、ZIP でない・壊れた・分割、項目数/ディレクトリの上限、危険な名前、stored/deflate の往復、フォルダー/暗号化/未対応方式、CRC 不一致・範囲外、申告の展開爆弾は読む前に拒否、申告を偽った爆弾は実測で停止、項目/合計の上限、取り消し、フォルダー構造と自然順、画像列の除外条件） | 28 |
| `Mavue.Core.Tests` | `Phase3RulesTests`（.zip/.cbz の判定と振り分け、Office/JAR/EPUB は従来どおり、クラウドのみは読まない、双方向制御文字の検出と表示用の置換、ホイールシーク ±5/±30 秒とタッチパッドの累積、波形の列の最大値） | 17 |
| `Mavue.Core.Tests`（既存） | 全形式を回す理論テスト 4 件に `FileFormat.Zip` が加わった分 | 4 |
| **全体（Release）** | **999（998 成功、1 スキップ、失敗 0）** | |

実機 E2E（Release、`--iterations 1`）: 新シナリオ（`RunnerPhase3.cs`）

- `archive-list`: ダウンロード扱い（MotW ゾーン 3）の ZIP。一覧 16.6 ms（中央ディレクトリのみ）、ファイル 8・フォルダー 3・画像 4・危険な名前 1・暗号化 1・入れ子 1、名前は shift_jis で「日本語」を表示、
  「インターネットから取得（example.com）」と警告を表示。PASS。
- `archive-images`: 「画像を表示」→ 1/4（640×480）、PageDown/PageUp/→ で前後（戻りは cache から）、Explorer の選択は変わらない、Backspace で一覧。PASS。
- `archive-cbz`: .cbz は 1 ページ目から（一覧なし）、ホイールで 2 ページ目、Explorer からの PageDown（フック）で 3 ページ目（先読み済みで 6 ms）。PASS。
- `archive-safety`: 60 MB の展開爆弾は一覧で警告、表示時は `entry-limit-MaxCompressionRatio` で読まずに拒否、次の画像は表示。壊れた ZIP・ZIP でないファイル・分割の一部は
  それぞれの理由を情報カードに表示。常駐プロセスはそのまま。PASS。
- `media-wheel`: 軽い操作バーを表示、ホイール +5 秒・Shift+ホイール +30 秒（長さで止まる）・2 段下げ −10 秒、ファイルは移動しない。PASS。
- `audio-waveform`: 20 秒の MP3 の波形を 205 ms で計算（exact）、波形の中央クリックで 10.0 秒へ。PASS。

全体の一括実行で見つかったもの（いずれもハーネス側。製品の変更なし）:

- `info-card` が空の .zip を情報カードの例に使っていた → ZIP は専用の表示になったため .7z に変更（PASS）。
- `pixel-inspector`: 画像探索が Phase 1 の「下 260 px は情報バー」の前提のままで、低い画面にウィンドウが出ると画像の下端が切れた → 枠だけ除くように修正（PASS）。
- `archive-images`/`archive-safety` の初版: UI Automation で「画像を表示」を押すと（クリックと同じく）Quick View がアクティブになり、その後の Explorer への PageDown は
  既存の仕様どおり Explorer が受け取る → Quick View に送るよう修正。Explorer からのフック経由のページ送りは `archive-cbz` で確認。
- `media-wheel` の初版: バーの外枠（Border）は UI Automation の要素にならない → 再生ボタンとスライダーで確認。
- 本体 `app-multiple-files` が 3 回中 2 回失敗: PDF はページ数が分かった時点で状態をもう一度通知し、ハーネスがそれを次のキーの結果と取り違えていた（実際の移動は正しい）
  → キーの前に 0.6 秒待つよう修正し 3/3 PASS。
- `cli-quickview` は全体実行で 1 回失敗（既知の揺らぎ）、単独で PASS。

結果: Quick View 表示 7/7（各 1 回）、シナリオは上記の修正後すべて PASS、本体 15/15。Space → 表示・24 MP JPEG のフル品質（81.6 ms）は Phase 2 と同等。

- 未確認: ZIP 内の大量画像（数千枚）・数 GB の ZIP での体感、長い音声（1 時間超）の概形→精密化の実機表示、ライト テーマでの ZIP 一覧・操作バーの見え方。

#### Quick Preview Phase 4（2026-11-02: テキスト・コード・Markdown・HTML）

利用者の方針（2026-11-02）: 細かいテストは回さず、Markdown / HTML / Code / Universal の振り分けまで実装がまとまってから一括で実行した。

| プロジェクト | 追加・変更したテスト | 件数 |
|---|---|---|
| `Mavue.Core.Tests` | `TextPreviewTests`: 文字コード判定（BOM 付き UTF-8/UTF-16 LE・BE、BOM なし UTF-16、UTF-8、Shift_JIS、EUC-JP、ISO-2022-JP、末尾で切れた UTF-8、欧文の ANSI、バイナリ・制御文字）、行の区切り（LF/CRLF/CR/混在・末尾の改行・空ファイル）、UTF-16 で 0x0A を含む文字を改行と誤らない、Shift_JIS の行、20 万行の任意の行の直接読み出しと索引メモリ、1 MiB の境界にまたがる CRLF、30 万文字の行の切り詰めと次の行、バイト数・チェックポイント数の上限、取り消し、バイナリは開かない、表示用の変換（タブ・制御文字・双方向制御文字）、途中から読んだときの複数行コメントの色、C#/Python/JSON/XAML/YAML/INI/diff/ログ/Markdown/CSS の色付け、名前と拡張子からの言語、全言語に乱数入力（例外・不正なトークンなし）、名前によるテキスト形式の判定、テキストを MPEG-TS・SVG・PDF と誤判定しない、.ts（TypeScript と MPEG-TS）、PNG を .txt にしたもの、振り分け（Text/Markdown/Html、クラウドのみは情報カード、種類不明でテキストならテキスト）、カテゴリーと MIME、HTML の文字コード宣言、段階別の上限、折り返し設定の既定値と保存 | 88 |
| `Mavue.Core.Tests`（既存の変更） | `FileFormatDetectorTests`（.txt → Text、.md → Markdown、.html → Html、.xyz → Unknown）、`PreviewContentRoutingTests`（実ファイルの .txt はテキスト表示へ） | 4 |
| **全体（Debug）** | **1,091（1,090 成功、1 スキップ: 既存のシンボリックリンクのテスト、失敗 0）** | |

実機 E2E（Debug、`--iterations 1`。新シナリオは `RunnerPhase4.cs`）:

- `text-plain`: Shift_JIS の日本語テキスト（CRLF・41 行）。情報欄の後 85〜93 ms で表示、文字コード Shift_JIS、プレーンテキストは折り返し・行番号なし、日本語が画面上にある。PASS。
- `text-code`: 2,005 行の C#。言語 csharp・行番号あり・折り返しなし、ホイール 10 段で 30 行、End で最終行・Home で先頭、W で折り返しの切替（保存）。PASS。
- `text-large`: 200 万行・約 122 MB のログ。1 画面目は情報欄の後 144〜167 ms（その時点で約 2.6 万行を索引済み）、全体の索引は 234〜250 ms で 200 万行、索引のメモリ 1.4 MB、
  End で最終行、ホストのワーキング セットの増加は約 4 MB。PASS。
- `text-routing`: 拡張子なしの README・未知の拡張子のテキストはテキスト表示、`module.ts` は TypeScript（動画ではない）、中身がバイナリの .txt は情報カードに
  「表示できるテキストが含まれていません」。PASS。
- `markdown-render`: 12 ブロック（解析 73〜79 ms）、見出し・表（日本語）・アラート・ローカル画像を描画、リモート画像 2 件（Markdown の画像と `<img>`）は読み込まない
  （ローカルのテスト用 TCP サーバーへの接続 0 件）、Ctrl+U でソース表示と戻り。PASS。
- `markdown-large`: 4 MB 超の Markdown はソース表示と注意書き。PASS。
- `html-safe`: スクリプト・外部 CSS/画像/フォント/iframe/object/embed/video/audio・meta refresh・prefetch・フォーム・ダウンロード リンク・file: 画像を含む HTML。
  ページは描画（インライン スタイルの見出しを画面のピクセルで確認、data: 画像を表示）、スクリプトは実行されない、テスト用サーバーへの接続 0 件、iframe 2 件の移動を取り消し、
  Ctrl+U でソース、閉じた後に Quick View の WebView2 プロセスは 0。初回の WebView2 作成込みで 700〜940 ms、2 回目以降 95 ms。PASS。

全体の一括実行で見つかったもの（すべてハーネス側。製品の変更は自動化ピアの追加のみ）:

- テキスト表示の外枠（Grid/Canvas）・注意書きの外枠（Border）は UI Automation の要素にならない → テキスト表示のルートに「Document」の自動化ピアを追加
  （スクリーン リーダーにも文書として見える。製品側の改善）、注意書きは TextBlock に ID。
- HTML の外部要求は Content-Security-Policy がブラウザー内で止めるため `WebResourceRequested` まで来ない（要求 0 件）→ 判定を「接続 0 件・iframe の移動の取り消し・
  ダウンロード 0」に変更。WebView2 の中の文字は UI Automation で取れないことがあるため、見出しの色を画面のピクセルで確認（表示の色管理で #00AA66 が (79,166,108) になる
  ことを実測し、色相で判定）。
- 既存の `message-not-replaced`: 以前の実行で作った `03 notes.txt` がテスト フォルダーに残り、↓ がそれに止まった（.txt はテキスト表示になったため名前を .docx に変えた）
  → 古いファイルを消すよう修正し PASS。
- 1 回目の表示計測で 1 度、Space がタイプアヘッドと判定された（`space-classified` reason=TypeAhead。フックと判定は今回変更していない）。全体実行では 7/7 OK。

結果: Quick View 表示 7/7、Quick View のシナリオ 44 件はすべて PASS（`cli-quickview` は全体実行で 1 回失敗し単独で PASS。既知の揺らぎ）、本体 15/15。
常駐の起動直後のワーキング セットは 142.5 MB で Phase 3（142.8 MB）と同等。Markdig と WebView2 は最初の Markdown・HTML を表示するまで読み込まれない。

- 未確認: ライト テーマ・ハイコントラストでの色付けの見え方、数 GB のテキスト（上限での打ち切りは単体テストのみ）、タッチでのパン、スクリーン リーダーでの読み上げ、
  Explorer がフォーカスを持たない状態での HTML 内の Space。

#### Quick Preview Phase 5（2026-11-02: EXIF カード・共有前チェック・メタデータを除いたコピー・類似画像検索）

利用者の方針（2026-11-02 変更）: 実装中はテストを実行しない。実装完了後にテスト計画を示し、許可を得てから一括で実行する。
実装中に行ったのはビルド（コンパイル）確認のみ。

| 区分 | 内容 | 件数・時間の目安 |
|---|---|---|
| 単体（新規 `Mavue.Metadata.Tests`） | EXIF の値（両バイト順）、南緯・西経、XMP/IPTC/ICC/MPF/コメント/EOI 以降、PNG/WebP/GIF/TIFF の読み取り、非対応形式、IFD のループ・巨大な件数・範囲外オフセット、壊したファイル 400 個で読み取りと除去が落ちない、プライバシー判定、JPEG/PNG/WebP/GIF の除去（圧縮データが同一・向きを残す・選んだ値だけ残す・CRC・RIFF の大きさ・VP8X のフラグ・GIF のフレームとループ）、EXIF の書き出し、表記 | 約 25 件、数秒 |
| 単体（`Mavue.Core.Tests` に追加 `SimilarityTests`） | 同一・縮小・明るさ変更は類似、乱数画像は非類似、入力検査、署名キャッシュ（大きさ/時刻での一致、保存/読み込み、パスを保存しない、上限、壊れたファイル）、プロトコル | 約 7 件、数秒 |
| 単体（`Mavue.Image.Tests` に追加 `SimilarSearchTests`） | 実ファイルでの検索（コピー・縮小を見つけ、他を除外、自分を除外、壊れた JPEG を除外、縮小画像の大きさ、2 回目はキャッシュ）、件数上限と取り消し、読めない対象、実 JPEG の除去後に WIC で同じ画素 | 4 件、10〜20 秒 |
| 単体（既存の全体） | 回帰（Phase 4 までの 1,091 件） | 全体で約 1 分 |
| 実機 E2E（新規 `RunnerPhase5.cs`） | `exif-card`（E で表示・設定保存・再度 E で非表示）、`share-check`（検出・コピーを保存: 検証・除去漏れなし・向き・元ファイル不変（SHA-256）・MotW 引き継ぎ、クリップボード）、`similar-search`（24 枚から 3 枚のコピーを検出・他を除外・ワーカー終了・結果のクリックで Pin 表示・Explorer の選択不変・行を閉じる）、`similar-resources`（600 枚: 即中止でワーカー終了、検索中も ↓ の表示が通常どおり（1 秒未満）、ワーカーのピーク メモリ、2 回目はキャッシュ、ワーカーが残らない） | 4 件、テスト画像の生成込みで 3〜5 分。**デスクトップを操作する** |
| 実機 E2E（回帰） | Quick View の表示 7 形式＋既存シナリオ 44 件（Phase 4 まで）、本体 15 件 | 15〜25 分。**デスクトップを操作する** |
| 手動（任意） | 実際のスマートフォン写真（HEIC・GPS 付き JPEG）・カメラの RAW での EXIF カード、ライト テーマとハイコントラストでのカード/フライアウト、ナレーターでの読み上げ | 利用者の判断 |

**結果（2026-11-02、利用者の許可後に一括実行、Debug）**

- 単体: **1,123 件（1,122 成功、1 スキップ: 既存のシンボリックリンクのテスト、失敗 0）**。初回は 2 件失敗し、どちらも製品側を修正:
  1. 縮小した滑らかなグラデーション画像の類似度が 70 %: DCT の知覚ハッシュは係数が中央値付近に集まる滑らかな画像で 14/64 ビット反転していた
     （差分ハッシュは 0、色はほぼ同じ）→ 構造の距離を知覚ハッシュと差分ハッシュの両方から求める（片方の不安定さで明らかな一致を落とさない、無関係な画像は両方が離れる）。
  2. 取り消し済みの検索が「対象を読めない」と報告していた（対象画像の処理中の取り消しを握りつぶしていた）→ 取り消しをそのまま伝える。
- 新規 E2E 4 件すべて PASS:
  - `exif-card`: カード表示 28 ms（表示後に読み込み）、カメラ・レンズ・位置情報/シリアル番号の注意、設定保存、E で非表示。
  - `share-check`: 検出 5 種類（位置・カメラ・シリアル・所有者・ソフトウェア）、「コピーを保存」181 ms で検証済み・除去漏れなし・向き OK・元ファイル不変（SHA-256）・
    MotW 引き継ぎ、クリップボードにファイル。
  - `similar-search`: 23 枚から 3 枚のコピー（縮小・低画質・PNG）を検出し他を除外、280〜370 ms（ワーカー 180 ms）、ワーカー終了コード 0・残存 0、結果のクリックで Pin 表示・
    Explorer の選択不変、行を閉じる。
  - `similar-resources`: 中止でワーカーは 79 ms 後に終了、599 枚（1600×1200 PNG）を 0.86 秒（2 回目はキャッシュで 0.24 秒、ヒット 601）、検索中の ↓ の表示 29〜44 ms、
    ジョブのピーク メモリ 14 MB（Windows のジョブ情報）。
- ハーネス側の修正: テスト用 EXIF に撮影日時がないのに期待していた、600 枚の小さな画像は Stop を押す前に検索が終わった（画像を大きくし、開始と中止を 1 回の UI Automation 呼び出しで）、
  ワーカーが終わった後はメモリを測れない（ホストがジョブのピーク メモリ `PeakJobMemoryUsed` を `similar-worker-ended` に記録するよう追加）、
  共有前チェックの後の最初の Esc はフライアウトを閉じる。`share-check` は 1 回、利用者のブラウザーが前面に出て Quick View が仕様どおり閉じたため失敗し、再実行で PASS。
- 回帰: Quick View 表示 7/7、シナリオ 48 件中 47 件 PASS（`cli-quickview` は全体実行で失敗し単独で PASS。既知の揺らぎ）、本体 15/15。

#### Phase 5 の続き（2026-11-02: HEIF/RAW の除去・本体の書き出し・類似検索の設定・回転・キャッシュ削除）

利用者の方針: 実装中はテストを実行しない。実装中はビルド（コンパイル）確認のみ。下の計画を示し、許可を得てから実行した。

| 区分 | 内容 | 件数・時間の目安 |
|---|---|---|
| 単体（新規 `HeifAndExportTests`、Mavue.Metadata.Tests） | 組み立てた HEIC/AVIF（iloc version 0/1）から EXIF と XMP を読む、壊した HEIF 300 個で落ちない、動画/CR3 を HEIF としない、別の元から残す値だけの EXIF を書く（向きなし・位置なし） | 4 件、数秒 |
| 単体（新規 `SimilarSettingsTests`、Mavue.Core.Tests） | 感度としきい値、設定の既定値・保存/読み込み・不正値、キャッシュの大きさと削除（一時ファイル・使用中）、要求とエクスポートのプロトコル | 4 件、数秒 |
| 単体（新規 `RotationAndExportTests`、Mavue.Image.Tests） | 90° 回転・左右反転のコピーを回転オンだけで検出、向き 6 の JPEG を PNG に変換（正立 200×320・カメラ名を残す・位置/向きなし・検証）、そのままの書き出し、読めない HEIC、画素数の上限 | 5 件、5〜10 秒 |
| 単体（既存の全体） | 回帰（1,123 件） | 全体で約 1 分 |
| 実機 E2E（新規 `RunnerPhase6.cs`） | `share-convert`（テスト用 HEIC → 共有前チェックで変換を確認 → 保存: 変換・検証・JPEG・除去漏れなし・元ファイル不変・MotW・ワーカーが残らない）、`similar-rotations`（回転/反転したコピーを検出、説明付き、他を除外）、`similar-settings`（メニューで「ほぼ同じ」→ しきい値 84 で再検索・保存、キャッシュ削除 → ファイルが消える） | 3 件、2〜4 分。**デスクトップを操作する** |
| 実機 E2E（回帰） | Quick View の表示 7 形式＋既存シナリオ 48 件、本体 15 件 | 15〜25 分。**デスクトップを操作する** |
| 手動（推奨） | 本体の「メタデータを除いて書き出す…」（保存ダイアログを使うため自動化しない）: JPEG そのまま・HEIC → JPEG・PNG、元ファイルを保存先に選んだときの拒否、キャンセル。設定画面の類似検索の項目とキャッシュ削除。実際の iPhone の HEIC・カメラの RAW | 10〜15 分、利用者の判断 |

**結果（2026-11-02、利用者の許可後に実行、Debug）**

- 単体: **1,136 件（1,135 成功、1 スキップ: 既存のシンボリックリンクのテスト、失敗 0）**。初回は 1 件失敗:
  Phase 5 の `OtherFormats_AreUnsupported` が「HEIC は非対応」を前提にしていた（今回 HEIF を読むようにしたため期待どおりの変化）→ 非対応の例を MP4（ftyp isom）に変更。
- 新規 E2E 3 件すべて PASS（単独と全体実行の両方）。初回は 2 件失敗し、どちらも製品側を修正:
  1. `share-convert`: 変換ワーカーが `too-large` を返した。原因は Windows のバックグラウンド モード（`PROCESS_MODE_BACKGROUND_BEGIN`）で、
     このモードでは HEIF（HEVC）のデコーダーが E_OUTOFMEMORY で失敗する（ジョブなしで手動実行しても再現、モードを外すと 532 ms で成功）。
     → 両ワーカー（変換・類似検索）とも、CPU の優先度 BelowNormal・メモリの優先度 Low を個別に設定する方式に変更（`Workers/WorkerPriority`）。
     類似検索でもフォルダー内の HEIC を比較できていなかったことになる（同じ原因）。ワーカーは失敗時に例外の種類と HRESULT だけを標準エラーに出すようにした。
  2. `similar-settings`: メニューの「ほぼ同じ」を UI Automation で選んでも再検索されなかった。ラジオ/トグルのメニュー項目は Toggle パターンで操作され、
     Click が発生しない（ナレーター等の支援技術でも同じ）→ Click ではなくチェック状態の変化で設定を反映（同じ値なら何もしない）。メニューを開くたびに
     チェック状態を設定ファイルの値に合わせる（本体の設定画面で変えた場合）。ハーネスは Select → Toggle → Invoke の順に試すよう修正。
  - `share-convert`: HEIC → JPEG 639〜863 ms（ワーカー込み）、検証済み・除去漏れなし・元ファイル不変（SHA-256）・MotW 引き継ぎ・ワーカー残存なし。
  - `similar-rotations`: 10 枚から 90° 回転と左右反転のコピー 2 枚を検出、説明付き、他は除外。
  - `similar-settings`: 「ほぼ同じ」でしきい値 84 で再検索・設定に保存、キャッシュ削除でファイルが消える。
- 回帰: Quick View 表示 7/7、シナリオ 51 件中 50 件 PASS（`cli-quickview` は全体実行で失敗し単独で PASS。既知の揺らぎ）。Phase 5 の 4 件も PASS。
  本体 15 件中 14 件 PASS、`app-pdf-password` は**テスト環境の Caps Lock がオン**だったため正しいパスワード "mavue" が "MAVUE" として入力されて失敗
  （製品の問題ではない）。ハーネスがこのシナリオの間だけ Caps Lock を切り、終了後に元に戻すよう修正し、単独で PASS → 本体 15/15。
- 未実施: 本体の「メタデータを除いて書き出す…」の画面操作（保存ダイアログを使うため自動化していない）、実機の iPhone HEIC・カメラ RAW → 手動確認待ち。

#### Phase 5 の完了: 本体の「メタデータを除いて書き出す…」を Quick View と共通化（2026-11-02〜03）

利用者の方針: テストは勝手に実行しない。実装中はビルド（コンパイル）確認のみ。下の計画を示し、許可を得てから実行した。

| 区分 | 内容 | 件数・時間の目安 |
|---|---|---|
| 単体（新規 `CleanCopyRulesTests`、Mavue.Image.Tests） | 元の隣の名前と (2)、元ファイルの判定（大文字小文字・`..`）、失敗の分類（両アプリ共通の文言）、ワーカーのメモリ上限、一時コピーの掃除（古いクリップボード用フォルダーと work 内の古いファイルだけ消す）、PrivacyReview（GPS 付き JPEG・デコードできない HEIC・存在しないファイル・代替の呼び出し） | 7 件、5〜10 秒 |
| 単体（既存の全体） | 回帰（1,136 件）。ShareCheck・CleanCopy・ワーカーを共通ライブラリへ移したため | 全体で約 1 分 |
| 実機 E2E（新規 `RunnerAppExport.cs`、本体） | `app-export-clean`（… メニュー → 見つかったもの → コピーを保存: 検証・除去漏れなし・MotW・元ファイル不変、クリップボード）、`app-export-convert`（HEIC → Mavue.exe のワーカーで JPEG、ワーカーが残らない）、`app-export-unsupported`（SVG は理由を表示し何も書かない） | 3 件、1〜2 分。**デスクトップを操作する** |
| 実機 E2E（Quick View の関連分） | `share-check`・`share-convert`（ShareCheck を共通処理に置き換えたため）、`exif-card` | 3 件、1〜2 分 |
| 実機 E2E（回帰） | Quick View の表示 7 形式＋シナリオ 51 件、本体 15 件（＋新規 3 件）。本体の起動処理（Program.cs）を変えたため本体は全件 | 15〜25 分。**デスクトップを操作する** |
| 手動（推奨） | 「保存先を選ぶ…」（保存ダイアログ: JPEG そのまま・HEIC → PNG・元ファイルを選んだときの拒否・キャンセル）、ダイアログのライト/ダーク/ハイコントラスト表示とキーボード操作・ナレーター、実際の iPhone の HEIC・カメラの RAW | 10〜15 分、利用者の判断 |

**結果（2026-11-03、利用者の許可後に実行、Debug）**

- 単体: **1,143 件（1,142 成功、1 スキップ: 既存のシンボリックリンクのテスト、失敗 0）**。新規 `CleanCopyRulesTests` 7 件も成功。
- 本体 E2E: **18/18 PASS**（既存 15 件＋新規 3 件。起動処理を Program.cs に変えた後の全シナリオを含む）。
  - `app-export-clean`: 見つかったもの 5 種、「コピーを保存」16 ms で検証済み・除去漏れなし・MotW 引き継ぎ・元ファイル不変（SHA-256）、クリップボードにファイル。
  - `app-export-convert`: HEIC → JPEG を Mavue.exe のワーカーで 674 ms、検証済み・元ファイル不変・ワーカー残存なし。
  - `app-export-unsupported`: SVG は理由を表示し何も書かない、画像はそのまま表示。
  - 初回は `app-export-unsupported` が失敗: 拒否の記録（`export-clean-check`）を説明ダイアログを閉じた**後**に書いていたため、ハーネスが待ち切れなかった
    → 製品側で記録を先に書くよう修正（拒否した時点の記録として正しい順序）。ハーネスの「表示中の内容」の確認を PDF 専用の関数から一般の状態に修正。
- Quick View 回帰 E2E: 表示 7/7、シナリオ 51 件中 48 件 PASS。失敗 3 件はいずれも今回の変更と無関係で、単独の再実行で PASS:
  `cli-quickview`（既知の揺らぎ）、`markdown-large`（テスト中に Discord が前面に出た）、`html-safe`（全体実行で描画の確認が間に合わず、単独で描画・スクリプト不実行・接続 0 を確認）。
  共通化した共有前チェックの `share-check`・`share-convert`・`exif-card`、類似検索の 5 件はすべて PASS。
- 未実施（手動確認待ち）: 「保存先を選ぶ…」（保存ダイアログ）、ダイアログのライト/ダーク/ハイコントラスト表示・キーボード・ナレーター、実機の iPhone HEIC・カメラ RAW。

**残りの手動確認（2026-11-03、利用者の依頼「残りの手動確認を完了」。大量テストはせず、必要最小限）**

自動化できるものはハーネスに加えた（`RunnerAppExportPicker.cs`・`RunnerAppExportReal.cs`）。

- 「保存先を選ぶ…」（保存ダイアログ）: E2E `app-export-picker`・`app-export-picker-convert` **PASS**。
  別のフォルダーへ JPEG（検証済み・除去漏れなし・MotW 引き継ぎ）、元ファイルを選んで「置き換える」→ 拒否・元ファイル不変（SHA-256）、キャンセル → 何も書かない・
  その後も保存できる、HEIC → PNG（1.7 秒、検証済み・除去漏れなし・元ファイル不変）。
  - 保存ダイアログはピッカーのホスト プロセスにあり（Mavue.exe ではない）、UI Automation からはボタンが Pane としか見えず操作できない。
    ハーネスは所有者ウィンドウで探し、ウィンドウ メッセージ（WM_SETTEXT・WM_COMMAND IDOK/IDCANCEL、置き換えの確認は TDM_CLICK_BUTTON IDYES）で操作する。
  - 製品の追加: キャンセルと元ファイルの拒否も計測 `export-clean` に記録（`cancelled` / `refused=original`、パスは記録しない）。
- 実機の iPhone HEIC: 利用者の写真 3 枚（**コピー**を一時フォルダーに作り、コピーだけを開いた。値は記録せず種類だけ）で `app-export-real-01〜03` **PASS**。
  見つかったもの: 位置情報・撮影日時・カメラ・ソフトウェア・メーカー独自データ（Apple MakerNote）・色プロファイル。「コピーを保存」→ JPEG に変換
  （0.6〜0.7 秒）、検証済み、**残ったもの なし**、縦横の向きは元と同じ（3024×4032 → 3024×4032、4032×1740 → 4032×1740）、元ファイル不変。
  色: iPhone の Display P3 は変換時に sRGB へ色変換するため、プロファイルなしでも色は正しい。
  実行方法: 環境変数 `MAVUE_E2E_REAL_IMAGES`（`;` 区切り）を設定し `--app --only app-export-real`。
- カメラの RAW: **未確認**。この PC に RAW ファイルがない（C:/D:/E: を検索）。RAW 拡張機能（Microsoft.RawImageExtension 2.5.42）は入っている。
- ダーク/ライト/ハイコントラスト: 本体のテーマ設定（試験用の設定ファイル）でダーク・ライト、ハイコントラストは Windows の設定を数秒だけオンにして
  スクリーンショットで確認（終了後にオフへ戻したことを確認）。3 つとも文字・ボタン・チェックボックス・区切り線が読める。
  修正: 強調色のボタンが 2 つ（「保存先を選ぶ…」と既定ボタンの「閉じる」）あったため、既定ボタンをなくし強調は「保存先を選ぶ…」だけに（Esc で閉じるのは同じ）。
- ナレーター/アクセシビリティ: UI Automation のツリーとキーボードで確認（音声そのものは聞いていない）。すべての操作に名前、
  Tab は ファイルの種類 → 4 つのチェック → クリップボード → コピーを保存 → 保存先を選ぶ → 閉じる → 先頭へ循環、状態の文は Polite のライブ領域。
  修正: 見つかったものの重要度がアイコンの形と色だけで、読み上げでは分からなかった → 種類の名前を「要注意: 位置情報」「技術情報: カメラ・撮影設定」
  「残します: 色プロファイル」のように読み上げる（本体と Quick View の共有前チェックの両方、日本語・英語）。
- 修正後の最小限の回帰: 本体 `app-export-*` 5/5 PASS、Quick View `share-check` PASS（同時に動いた基本操作 4 件も PASS）。

#### Quick View のウィンドウの大きさ・背景の透明度（2026-11-03）

利用者の方針: テストは勝手に実行しない。実装中はビルド（コンパイル）確認のみ（`dotnet build Mavue.slnx` 成功、警告 0）。

| 区分 | 内容 | 件数・時間の目安 |
|---|---|---|
| 単体（新規 `QuickViewAppearanceTests`、Mavue.QuickView.Tests） | 初期サイズ（4K 150 %・フル HD・4K 100 % の上限・横長・縦置き・小さい画面・極小・拡大率不明）、以前より小さく形が 4:5〜16:10、透明度の既定（古い設定ファイル・ファイルなし）・範囲外の値の丸め・保存と読み込みで他の設定を壊さない | 18 件、数秒 |
| 単体（Mavue.QuickView.Tests 全体） | 設定ファイルの読み書きを変えたため | 約 10 秒 |
| 実機 E2E（新規 `RunnerAppearance.cs`） | `window-size`（`placed` の大きさが作業領域と DPI からの計算どおり、以前の 70%×75% より小さい）、`window-transparency`（透明度 0 → 20 → 0: 背景の種類が Mica → Acrylic → Mica、画像の中央の画素が 3 回とも同じ色） | 2 件、1〜2 分。**デスクトップを操作する** |
| 実機 E2E（関連の回帰） | 表示 7 形式（Space → 表示の時間が以前と同等か）、`monitor-*`（複数モニター・DPI の配置）、`small-image-not-enlarged`・`settings-reload`、`pixel-inspector`、Phase 2 のデザイン関連 | 5〜10 分。**デスクトップを操作する** |
| 実機 E2E（全体の回帰、任意） | Quick View のシナリオ 51 件＋新規 2 件。ウィンドウが小さくなったため、大きさに依存する確認がないか | 15〜20 分 |
| 手動（推奨、見た目） | 既定 15 % の見た目（暗い/明るいウィンドウや壁紙の上）、0 % と 30 %、ライト テーマ、ハイコントラスト（不透明になる）、Windows の「透明効果」オフ、設定のスライダー（キーボード操作・ナレーターの読み上げ）、3 台のモニターでの大きさ | 5〜10 分、利用者の判断 |

**結果（2026-11-03、利用者の許可後に実行、Debug。全体回帰は未実施＝利用者の指示）**

- 単体: 新規 18 件すべて成功（Mavue.QuickView.Tests 全体 187 件成功、失敗 0）。
- E2E `window-size` **PASS**: 作業領域 3840×2100 @144 dpi → 2150×1344（計算どおり、以前 2688×1575）。
- E2E `window-transparency` **PASS**: 0 % Mica → 20 % Acrylic → 0 % Mica、画像の画素は 3 回とも #3366CC。`--host-args --no-backdrop`（性能段階 Low と同じ分岐）では
  20 % でも Solid・舞台 #1E1E1E。
- 追加の確認（新規 `RunnerAppearanceLook.cs`、明るい縞模様のウィンドウを後ろに置く。Windows の設定はそのケースの間だけ変えて元に戻す）:
  - `window-look` **PASS**: 舞台の色 0 % #1E1E1E → 15 % #404040 → 30 % #616161（透明度に応じて透ける）、ライト 15 % #F5F5F5、ハイコントラスト Solid（システムの色）、
    透明効果オフ 30 % #1E1E1E（OS が単色に切り替える）。画像は全ケースで #3366CC。終了後: ハイコントラスト オフ・アプリ モード ダーク・透明効果 オン に戻ったことを確認。
    スクリーンショット（`look-*.png`）で、15 % は背後がぼけてうっすら見え、ツールバーの文字・アイコンは不透明時と同じく読めることを確認。
  - `window-content-colors` **PASS**: 0 % と 30 % で、画像（#3366CC）・PDF のページ・テキストの背景（#1E1E1E、4 点）の画素が完全に同じ。
  - Space → 表示（small/large JPEG 各 10 回、中央値 ms、0 % は 2 回）:

    | | 表示 (shown) | 最初のフレーム | フル品質 | 画面の画素 | ピーク WS |
    |---|---|---|---|---|---|
    | small 0 % | 8.7 / 9.5 | 14.0 / 16.4 | 26.7 / 28.6 | 74.6 / 74.3 | 173 MB |
    | small 15 % | 9.2 / 9.8 | 15.7 / 16.2 | 30.2 / 26.3 | 75.6 / 74.5 | 173〜174 MB |
    | large 0 % | 8.4 / 8.9 | 13.8 / 14.5 | 84.2 / 82.6 | 118.1 / 119.0 | 223 MB |
    | large 15 % | 9.0 / 8.0 | 14.6 / 14.7 | 86.2 / 83.8 | 127.5 / 122.5 | 223〜227 MB |

    差は実行ごとのばらつき（表示前の選択の取得も 0.7 ms 程度ずれる）の範囲内で、悪化は見られない。
- 見つけて直したこと:
  - 計測 `backdrop` が、ハイコントラストへの切り替えで背景が変わった後に記録されなかった（直前の記録ではなく直前のウィンドウの状態と比べていた）→ 最後の記録と比べる。
  - ハーネス: `--transparency N`（時間の比較用、既定 0）。明るい背景の確認は `--warm`（「中立のウィンドウ」として開く Mavue 本体が Quick View の後ろに来るため）。
- 残り 2 点の確認（2026-11-03）:
  - 設定のスライダー（UI Automation とキーボード）: 種類 Slider、名前「Quick View の背景の透明度」、範囲 0〜30・刻み 1、キーボードで操作でき、→ で 16・17、Home で 0、
    隣の値の文字（「17%」「0%（不透明）」）も追従、保存される。説明文がスライダーに結び付いていなかった → HelpText に設定（読み上げで説明が続く）。
    確認では利用者の Quick View 設定ファイルを退避し、終了後に元の内容へ戻した。
  - 別のモニター: E2E `window-monitors`（新規）**PASS**。15 % で 3 台とも、そのモニターに開く・大きさが計算どおり（3840×2160@144 → 2150×1344、
    1920×1080@96 と 2560×1080@96 → 1106×691）・DPI が一致・小さい画像は 640×420 のまま・背景は Acrylic・画像の画素は不変。
    PDF のツールバー（ボタンが最も多い）も 3 台ともウィンドウ内に収まる。スクリーンショットで見た目を確認（`look-monitor-*.png`）。

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
- Phase 4 のシナリオ: `text-plain`・`text-code`・`text-large`・`text-routing`・`markdown-render`・`markdown-large`・`html-safe`（§「Quick Preview Phase 4」）。
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
