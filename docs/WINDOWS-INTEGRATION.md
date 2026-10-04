# Mavue Windows 統合設計

> SPEC §3, §22, `CLAUDE.md` §7 に対応。Explorer 統合はオプションではなく第一級機能として扱う。
> すべての項目は実機 Windows 11 での検証が必須（`docs/TESTING.md` §5）。

最終更新: 2026-10-04（MSIX 版の Explorer 連携を実測: §0 の結論を再検討。2026-10-03 プレビューハンドラー・サムネイルプロバイダーを実装。それ以前: Quick View PoC の実測結果と MSIX 整理）
対象 OS: Windows 11（開発機: 25H2 build 26200.9550）。Windows App SDK 2.x の最小要件は Windows 10 1809 だが、
Explorer 統合（タブ、上段コンテキストメニュー）は Windows 11 を主対象とする。Windows 10 での動作範囲は別途記録する。

---

## 0. 配布形態と登録方式の全体像

| 統合ポイント | MSIX（主） | 非パッケージ（開発・代替） |
|---|---|---|
| ファイル関連付け | `uap:FileTypeAssociation` | `HKCU\Software\Classes` + `RegisteredApplications` |
| 上段コンテキストメニュー（Win11） | `desktop4:FileExplorerContextMenus` + `com:ComServer`（`IExplorerCommand`） | **不可**（パッケージ ID 必須）→ スパースパッケージ（外部ロケーション付きパッケージ）で ID を付与 |
| 従来コンテキストメニュー（「その他のオプションを確認」） | 静的 verb (`uap3:Verb`) | `shell\<verb>` レジストリ |
| サムネイルプロバイダー | `desktop2:ThumbnailHandler` + `com:SurrogateServer` | `ShellEx\{E357FCCD-A995-4576-B01F-234630154E96}`（**実装済み**: `Mavue.exe --register`、§5） |
| プレビューハンドラー | `desktop2:DesktopPreviewHandler` + `com:SurrogateServer` | `ShellEx\{8895b1c6-b41f-4c1c-a562-0d564250836f}` + `PreviewHandlers` 一覧（**実装済み**: `Mavue.exe --register`、§4） |
| プロパティハンドラー | `desktop2:DesktopPropertyHandler` | `HKLM\...\PropertySystem\PropertyHandlers\.ext`（**HKLM のみ・管理者権限要**） |
| IFilter（全文検索） | **マニフェスト拡張なし（調査済み: 公式拡張一覧に記載なし）** | `HKLM` 登録（管理者権限要）→ 別インストーラ部品として提供（Investigating） |
| ログオン時起動 | `desktop:StartupTask` | `HKCU\...\Run` |
| 実行エイリアス (`mavue.exe`) | `uap3:AppExecutionAlias` | PATH |
| 共有ターゲット | `uap:ShareTarget` | 不可（ID 必須） |

**結論（2026-10-02 時点の案）**: MSIX パッケージで配布し、Windows Search IFilter のみ追加の（任意・管理者）コンポーネントとして扱う案を第一とする。
**2026-10-04 の実測で再検討が必要**: MSIX の `desktop2:DesktopPreviewHandler`/`ThumbnailHandler` は拡張子キーに書かれず、Windows がパッケージの
カタログから引く。実測では、その種類を宣言するパッケージが Mavue だけのとき（この PC では .pdf と .x3f）にだけ使われ、フォト・ペイント・メディア プレーヤー等も
宣言する .png・.svg・.mp4 などでは使われない（Explorer 再起動後も同じ。既定のアプリにした場合は未確認）。HKCU の拡張子キーからパッケージの COM クラスを
指す方法は `REGDB_E_CLASSNOTREG` で不可。非パッケージの HKCU 登録（ZIP 版）はすべて動作する。どちらを主たる配布形態にするかはユーザーの判断待ち
（`docs/PACKAGING.md` §1・§5.3）。
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

### 3.1 実装（2026-10-03、アンパッケージ版・現在のユーザーのみ）

`Mavue.Shell.AppRegistration`（`Mavue.exe --register` / `--unregister`、または本体の設定画面「Windows との統合」）が HKCU に書く:

| キー | 内容 |
|---|---|
| `Software\Microsoft\Windows\CurrentVersion\App Paths\Mavue.exe` | Mavue.exe の場所（名前で起動できる。Quick View の「Mavue で開く」もここから探す） |
| `Software\Classes\Mavue.Image` / `Mavue.Pdf` / `Mavue.Video` / `Mavue.Audio` | ProgID（種類名は登録時の言語、アイコン、`shell\open\command` = `"Mavue.exe" "%1"`） |
| `Software\Classes\.ext\OpenWithProgids` | 対応する全拡張子に ProgID を追加（「プログラムから開く」に出る）。**既定値（既定のアプリ）は変更しない** |
| `Software\Classes\Applications\Mavue.exe` | 表示名・SupportedTypes |
| `Software\Mavue\Capabilities` + `Software\RegisteredApplications` | 「設定 › アプリ › 既定のアプリ」に Mavue が出る（ユーザーがそこで既定にする。設定画面から `ms-settings:defaultapps?registeredAppUser=Mavue` を開ける） |
| `Software\Classes\SystemFileAssociations\.ext\shell\Mavue.Open` | 従来メニューの「Mavue で開く」（1 ファイル選択時。複数選択は Quick View で） |

削除は Mavue の名前のキー・値だけ（他のアプリの OpenWithProgids・動詞は残す。単体テスト `AppRegistrationTests` で確認）。
Windows 11 上段メニューの「Mavue で開く」は未実装（`native/Mavue.Shell.Native` に 2 つ目の `IExplorerCommand` を追加し、識別パッケージを再作成・再登録する）。
Quick View の右クリック（従来・上段）の対象拡張子に動画・音声・SVG・RAW を追加した（従来メニューは `--register`、上段は識別パッケージの再作成で反映）。

## 4. Preview Handler（Explorer プレビューウィンドウ）— 2026-10-03 実装

`native/Mavue.Shell.Preview`（C++20、MSVC、静的 CRT）の `Mavue.Shell.Preview.dll`。サムネイル（§5）と同じ DLL。

| 項目 | 内容 |
|---|---|
| インターフェース | `IPreviewHandler`・`IInitializeWithStream`（ストリーム初期化のみ。仮想フォルダーの項目でも動く）・`IObjectWithSite`（`IPreviewHandlerFrame` へキーを渡す）・`IOleWindow`・`IPreviewHandlerVisuals`（背景色・文字色に追従） |
| ホスト | **専用の prevhost.exe**: CLSID の `AppID` を Mavue 専用 `{E2B69F32-…}` にし、`DllSurrogate=%SystemRoot%\system32\prevhost.exe`。実機で `prevhost.exe {AB883DEA-…} -Embedding` が Mavue のためだけに起動することを確認（他社ハンドラーの不具合と相互に巻き込まない）。prevhost は**低整合性**で動く（診断ログは `%USERPROFILE%\AppData\LocalLow` にしか書けない、実測） |
| 処理の分担 | UI スレッド（prevhost の STA）は描画（Direct2D、HWND レンダーターゲット）と入力だけ。読み込み・デコード・PDF の描画はプレビューごとの背景スレッド（MTA、ストリームは `CoMarshalInterThreadInterfaceInStream` で渡す）。`Unload` は背景処理を待たない（スレッドは切り離し、DLL は `DllCanUnloadNow` で処理終了まで保持） |
| 画像 | WIC（Mavue と同じデコーダー。HEIF/AVIF/WebP/RAW は Windows の拡張機能）。EXIF/XMP の向き（`System.Photo.Orientation`）を適用、ペインのモニターの大きさ（最大 4096 px）に縮小デコード、表示は**拡大しない**（Mavue の規則）・高品質キュービック。4 億画素超は「大きすぎる」表示 |
| GIF アニメ | 全フレームを論理画面に合成（Disposal 対応、20 ms 未満の遅延は 100 ms＝`GifComposer` と同じ）。合計 256 MB を超えるものは 1 枚目を静止表示 |
| SVG | Direct2D の `ID2D1SvgDocument`（スクリプト・外部参照なし）。width/height/viewBox から大きさを決め、ペインに合わせて拡大縮小 |
| PDF | Mavue と同じ pdfium.dll（DLL と同じフォルダーからフルパスで読み込み、ない時は「PDF のプレビューを利用できません」）。幅に合わせた連続表示、スクロールバー・ホイール・キー、ページ番号表示。**表示範囲の前後 1 ページだけ**その大きさで描画（600 ページで描画 2〜3 ページ、prevhost のプライベートメモリ 114〜181 MB）。描画は進行型で取り消し可能、PDFium の呼び出しはプロセス内で 1 本化。パスワード付きは案内表示 |
| 動画・音声 | Media Foundation のメディアエンジン（Mavue のプレーヤーと同じ Windows のコーデック）。動画は子ウィンドウに描画（拡大しない）、下部に再生/一時停止・時間・シーク・ミュート。自動再生はしない（クリックまたは Space）。**エンジンの終了は別スレッド**: UI スレッドで行うと約 200 ms COM で待ち、その間に Explorer で押されたキーが失われた（Explorer と prevhost の子ウィンドウは入力キューを共有。Quick View の E2E `media-formats` で発見・計測）。描画先の子ウィンドウは終了まで message-only ウィンドウ（`HWND_MESSAGE`）として残す（トップレベルにすると前面を奪った、実測） |
| 不正・空・非対応 | 文言を表示（「このファイルはプレビューできません」「このファイルは空です」等、日本語/英語の文字列テーブル）。中身で判定（`%PDF-`、`<svg`、WIC、残りはメディアエンジン）するので拡張子違いも安全 |

登録は §4.1、テストは `docs/TESTING.md`。

### 4.1 登録（`Mavue.exe --register` / `--unregister`、現在のユーザーのみ・管理者不要・署名不要）

`Mavue.Shell.ShellHandlerRegistration`（`AppRegistration` から呼ぶ。Mavue.exe と同じフォルダーに DLL があるときだけ）:

- `HKCU\Software\Classes\CLSID\{AB883DEA-…}`（プレビュー）・`{B4E9FA4B-…}`（サムネイル）: `InprocServer32`=DLL、`ThreadingModel=Apartment`。プレビューは `AppID`、`HKCU\…\PreviewHandlers` に一覧登録。
- 結び付け（`ShellEx\{IID}`）は **Explorer が今どのハンドラーを使うか（`AssocQueryString(ASSOCSTR_SHELLEXTENSION)`）を調べ、無い拡張子だけ** `SystemFileAssociations\.ext` に書く（最も優先度が低い場所: 後から他のアプリが登録すればそちらが優先）。本機の結果: プレビューは 66 種類（画像・SVG・動画・音声）、**PDF は Edge のまま**。サムネイルは **.pdf と .svg だけ**（JPEG/PNG/HEIC/RAW は Windows の Photo Thumbnail Provider、動画・音声は Windows のまま）。
- Mavue の ProgID（`Mavue.Image`/`Pdf`/`Video`/`Audio`）にもプレビューを結び付ける（利用者が既定のアプリを Mavue にした場合に使われる。既定のアプリ自体は変更しない。この経路は既定のアプリを変えずに実機確認できないため未検証）。
- **オプトイン** `Mavue.exe --register --prefer-mavue-preview`: 既存のプレビュー（例: Edge の PDF）も Mavue にする。`HKCU\Software\Classes\.ext\ShellEx` に書き、以前の HKCU の値は `HKCU\Software\Mavue\ShellExBackup` に保存して解除時に戻す。書いた後に実際に Mavue が選ばれるか確認し、効かない場合（利用者の既定アプリの ProgID が上位で登録している等）は元に戻す。
- `--unregister`: Mavue の CLSID・AppID・一覧の値と、Mavue の CLSID を指す結び付けだけを削除（他社の値は残す）。保存した値を復元。
- 32 ビット（2026-10-03 対応）: `x86\`の x86 版（x86 の pdfium.dll 付き）を 32 ビット ビュー（`Classes\Wow6432Node\CLSID`）にも登録。プレビューは Windows の 32 ビット prevhost（AppID `{534A1E02-…}`）、
  サムネイルは 32 ビットの分離プロセスで動く（E2E `explorer-32bit`）。x86 版がない状態では、結び付け（両ビュー共通）だけが見えて 32 ビット アプリで `REGDB_E_CLASSNOTREG` になっていた。詳細は `docs/PACKAGING.md` §4。
- PDF のプレビュー切替: 設定画面「エクスプローラーのプレビュー ウィンドウでの PDF」（`--prefer-mavue-preview` と同じ処理を `.pdf` だけに。オフで以前の値を復元）。
- テーマ: Explorer は暗色モードでも `IPreviewHandlerVisuals` で白・黒を渡す（実測）。Windows のアプリ モードが暗色で背景が明るいときは Explorer と同じ暗色（#191919）を使い、
  表示中の切り替え（`WM_SETTINGCHANGE` "ImmersiveColorSet"）にも追従。PDF のページは紙として白のまま。
- DPI: prevhost のスレッドは**システム DPI 対応**（実測 `DPI_AWARENESS_SYSTEM_AWARE`）、Explorer のペインはモニターごと（v2）で物理ピクセルを渡す。そのままだと
  100 % のモニターでプレビューのウィンドウが 150 % 分ずれた位置・大きさになった（実測、E2E `explorer-monitors` で発見）。プレビューのウィンドウは作成・配置の間だけ
  スレッドを per-monitor v2 にして作る（終われば元に戻す: 32 ビットの prevhost は他社ハンドラーと共有するため）。別の DPI のモニターへ移ると `WM_DPICHANGED_AFTERPARENT` で再配置。
- パッケージ・署名: アンパッケージ版の HKCU 登録には不要。Smart App Control を有効にした PC では未署名 DLL の読み込みが拒否されうる（配布時はコード署名が前提）。MSIX では `desktop2:DesktopPreviewHandler`/`desktop2:ThumbnailHandler` + `com:SurrogateServer` で宣言（試作パッケージで実装、`docs/PACKAGING.md`）。
- ビルド中の注意: Explorer が DLL を読み込んでいる間（prevhost / サムネイル用 dllhost）は上書きできない。Mavue 専用の prevhost（コマンドラインに `{AB883DEA-…}`）を終了してからビルドする。

## 5. Thumbnail Provider — 2026-10-03 実装

- `IThumbnailProvider` + `IInitializeWithStream`（`DisableProcessIsolation` は使わない）→ Explorer の**分離されたサムネイル用プロセス**（`DllHost.exe /Processid:{AB8902B4-09CA-4BB6-B78D-A8F59079A8D5}`、実測）で動く。
- 出力: 要求サイズに合わせた 32bpp トップダウン DIB（`WTSAT_ARGB`）。PDF は 1 ページ目（白い紙、不透明）、SVG はサイズまで拡大、画像は縮小のみ（小さい画像は Explorer が拡大）。
- 対象: 既存のプロバイダーが無い拡張子だけ（本機では .pdf と .svg）。JPEG/PNG 等 Windows が持つ形式は上書きしない。
- 速さ: 実機で PDF 7〜34 ms、SVG 11〜18 ms。壊れた PDF・SVG でない文書は `WTS_E_FAILEDEXTRACTION` を 7〜88 ms で返す。

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
