# Mavue 機能ステータス

> `docs/SPEC.md` の全項目をここで追跡する（`CLAUDE.md` §18–19, SPEC §30）。
> 本表は SPEC から機械抽出した項目に状態を付与したもの。**SPEC の項目は削除しない**。
> 「Implemented」は中核動作が機能する場合のみ、「Tested」は SPEC §31 の Definition of Done を満たす場合のみ使用する。

最終更新: 2026-10-04（MSIX の Explorer 連携の原因特定、上段メニューの Explorer 再起動直後の失敗を修正、配布物の整合チェック。配布物の完成: ランタイム同梱・ZIP インストーラー・MSIX の実機確認・ライセンス同梱（X.08）。2026-10-03 配布準備: MSIX・署名・32 ビット・テーマ・DPI・暗号化 PDF・PDF プレビュー切替、未実装項目の整理。それ以前: Explorer のプレビューウィンドウ・サムネイル（F03.20/26/27、F22.07–10）。それ以前: Quick View の PDF を本体と共通化: 連続/見開き（表紙単独）・検索・選択とコピー・目次/しおり・リンク・プレゼンテーション。それ以前: PDF ビューア: PDFium 導入、検索・結果一覧・文字選択とコピー・目次・リンク・連続/見開き/単一ページ。それ以前: 主要閲覧機能: 拡大縮小・パン・回転/反転・PDF サムネイル/ページ指定・GIF 一時停止/コマ送り・SVG・RAW 判定・情報パネル・コピー/貼り付け・最近使ったファイル・設定（外観/表示サイズ/Windows 統合）・ウィンドウ位置の保存・「プログラムから開く」/「Mavue で開く」/既定のアプリ登録、Quick View の拡大/回転/ファイル操作）

状態: **Planned** / **Investigating** / **In Progress** / **Implemented** / **Tested** / **Blocked** / **Excluded (SPEC §29)** / **Backlog (SPEC §16)**

現時点で **Implemented / Tested の製品機能はない**。Quick View PoC と本体の閲覧基盤で動作を確認した項目は **In Progress**（製品品質・DoD 未達）。
"Blocked" は実装ではなく「実機検証」がハードウェア不足で止まっている項目を備考に記載している（実装自体は Planned/Investigating のまま）。


## SPEC §3. Quick View / Quick Look

| ID | 機能 | 状態 | 備考 |
|---|---|---|---|
| F03.01 | Explorer file selected + Space → instant preview | In Progress | PoC: 常駐ホスト + LL フック + Shell COM（STA）+ panel 表示。画像/PDF で Space→表示→Esc を E2E・ユーザー実操作（リモート・**物理キー**）で確認。QuickLook 共存対策済み |
| F03.02 | Multiple selected files | In Progress | PoC: 複数選択は ←/→ で選択内を移動（Explorer の選択は維持）、位置表示「i / N」。E2E・物理キーで確認 |
| F03.03 | Previous/next file navigation | In Progress | PoC: Explorer の選択変更イベントに追従（ポーリングなし、イベント後 約 2 ms で切替）。E2E・物理キーで確認。Explorer のタブ切替にも追従（E2E・物理キーで確認）。前後の項目を先読み |
| F03.04 | Escape to close | In Progress | PoC: Esc（フック経由）・Space トグル・他アプリ切替で自動クローズを実操作で確認 |
| F03.05 | Fullscreen | Planned |  |
| F03.06 | Zoom | In Progress | Ctrl+ホイール（Explorer にキーボードを残したまま）・ウィンドウのボタン・Ctrl++/-/0/1（Quick View をクリックした後）。拡大表示は即時に伸縮し、止まった後に表示サイズで再デコード（E2E `quickview-zoom-rotate`）。ファイルを移ると元に戻る |
| F03.07 | Rotate | In Progress | ボタンと Ctrl+R（Quick View がアクティブなとき）。画像・GIF・PDF のページを 90° 単位で回転（表示のみ。ファイルは変更しない）。E2E `quickview-zoom-rotate` |
| F03.08 | Thumbnail/index sheet | Planned |  |
| F03.09 | File name | In Progress | PoC: ファイル名を表示 |
| F03.10 | File information | In Progress | PoC: 形式・サイズ・ピクセル寸法（PDF はページ数）を表示 |
| F03.11 | Open with another application | In Progress | 「プログラムから開く」（Windows の SHOpenWithDialog）と「Mavue で開く」（Mavue.exe を App Paths／同じフォルダーから探して起動し、Quick View を閉じる）。「Mavue で開く」の実機 E2E は未作成 |
| F03.12 | Copy | In Progress | Quick View がアクティブなとき Ctrl+C でファイル（画像は画像データも）をクリップボードへ。Explorer にフォーカスがあるときは Explorer 自身の Ctrl+C |
| F03.13 | Share | Planned | DataTransferManagerInterop.ShowShareUIForWindow |
| F03.14 | Print | Planned |  |
| F03.15 | Markup from Quick View | Planned |  |
| F03.16 | GIF preview | In Progress | アニメーション再生（F20.01）。E2E `gif-animation` |
| F03.17 | Audio preview | In Progress | MediaPlayerElement（Media Foundation）で再生。音符アイコン＋再生状態（再生中/一時停止/再生終了）＋標準の操作バー。E2E `media-switch`/`media-formats` で MP3・WAV・M4A・WMA・FLAC の再生と音声出力（ホストの音声セッションのピーク値）を確認 |
| F03.18 | Video preview | In Progress | MediaPlayerElement で映像＋音声。拡大しない（画像と同じ規則）。E2E `media-video` で MP4（H.264/AAC）の画面上の映像・音声・一時停止・シーク・音量、`media-formats` で WMV を確認 |
| F03.19 | HDR support where practical | Investigating | scRGB スワップチェーン。形式ごとの HDR メタデータ対応を調査 |
| F03.20 | Explorer Preview Pane integration | In Progress | Explorer のプレビューウィンドウ: `Mavue.Shell.Preview.dll`（C++、WINDOWS-INTEGRATION §4）を `Mavue.exe --register` で登録。画像（向き適用・拡大しない）、GIF アニメ、SVG、動画・音声（クリックで再生、シーク・ミュート）、PDF（幅に合わせた連続表示、表示付近のページだけ描画）。既存のプレビューがある形式（本機の PDF は Edge）は奪わない（`--prefer-mavue-preview` で明示的に切り替え、解除で復元）。不正・空のファイルは文言表示。E2E `explorer-preview-pane`・`explorer-preview-pdf`・`explorer-preview-keyboard`、単体 `PreviewNativeTests` |
| F03.21 | File association integration | In Progress | F22.04 と同じ（Mavue 本体の「プログラムから開く」・既定のアプリ登録） |
| F03.22 | Context-menu integration | In Progress | 「Mavue Quick View」（F22.03）。動画・音声・SVG・RAW の拡張子も対象に追加（従来メニューは再登録、新メニューは識別パッケージの再作成・再登録で反映） |
| F03.23 | Drag & drop | Planned |  |
| F03.24 | Clipboard integration | Planned |  |
| F03.25 | Windows Search integration where practical | Investigating | IFilter は MSIX 拡張なし。登録方式を調査（WINDOWS-INTEGRATION §0） |
| F03.26 | Thumbnail Provider | In Progress | F22.07 と同じ |
| F03.27 | Preview Handler | In Progress | F22.09 と同じ |

## SPEC §4. Image Formats

| ID | 機能 | 状態 | 備考 |
|---|---|---|---|
| F04.01 | JPG | In Progress | Quick View で表示確認（WIC）。編集・変換は未実装 |
| F04.02 | JPEG | In Progress | 同上（192 MP まで縮小デコードを確認） |
| F04.03 | PNG | In Progress | Quick View で表示確認（100 MP まで） |
| F04.04 | WebP | In Progress | Quick View で表示確認（OS の WebP 拡張） |
| F04.05 | GIF | In Progress | 本体・Quick View で表示・アニメーション（F20） |
| F04.06 | BMP | In Progress | WIC で表示（他の画像と同じ経路）。本体で生成ファイルを開いて確認（2026-10-03、手動）。形式別の E2E は未作成 |
| F04.07 | TIFF | In Progress | WIC で表示（他の画像と同じ経路）。本体で生成ファイルを開いて確認（2026-10-03、手動）。形式別の E2E は未作成 |
| F04.08 | HEIC | In Progress | OS 拡張（HEIF+HEVC）で Quick View 表示を確認（WIC で生成した HEIC）。実カメラの HEIC は未確認。同梱フォールバックは HEVC 特許の法務確認待ち |
| F04.09 | HEIF | In Progress | 同上 |
| F04.10 | ICO | In Progress | WIC で表示（他の画像と同じ経路）。本体で生成ファイルを開いて確認（2026-10-03、手動）。形式別の E2E は未作成 |
| F04.11 | SVG | In Progress | XAML の SvgImageSource（Direct2D）で表示。要素の大きさで描画するので拡大しても鮮明。本来の大きさは width/height/viewBox から読む（DTD・外部実体は処理しない）。**制限: Direct2D の SVG は `<text>`・フィルター等を描かない**（resvg 導入で解消予定）。回転は未対応。E2E `app-svg`・`quickview-zoom-rotate` |
| F04.12 | AVIF | In Progress | Quick View で表示確認（OS の HEIF + AV1 拡張） |
| F04.13 | RAW formats | In Progress | 形式判定（TIFF 系 RAW は拡張子、CR3・RAF は内容）と WIC（Raw Image Extension）でのデコード経路を追加。コーデックがなければ「デコーダーがない」と表示。**RAW のサンプルがなく実ファイルでは未確認**。LibRaw フォールバックは未実装 |
| F04.14 | JPEG 2000 | Planned | OpenJPEG |
| F04.15 | other practical image formats where appropriate | Planned |  |
| F04.16 | EPS | Excluded (SPEC §29) | |
| F04.17 | OpenEXR | Excluded (SPEC §29) | |

## SPEC §5. Image Viewing

| ID | 機能 | 状態 | 備考 |
|---|---|---|---|
| F05.01 | Zoom | In Progress | 本体: Ctrl++/-（段階）、Ctrl+ホイール（ポインター位置を中心）、ツールバーの倍率メニュー（25〜400%）。即時に伸縮し、止まった後に必要な画素数で再デコード（原寸を超える拡大は原画素を引き伸ばす、上限 5,000 万画素）。E2E `app-zoom-rotate`。Quick View は F03.06 |
| F05.02 | Pan | In Progress | 拡大時: マウス/ペンのドラッグ、スクロールバー、↑/↓。タッチはスクロールビューアー標準 |
| F05.03 | Fit to Window | In Progress | Quick View と本体: ウィンドウに収める（拡大はしない）。Ctrl+0 で戻す。回転時は縦横を入れ替えて収める |
| F05.04 | Actual Size | In Progress | 本体: Ctrl+1・倍率メニュー、設定で「開いたときの表示サイズ」を選択。Quick View: 設定画面（本体の設定）で選択、Ctrl+1（アクティブ時） |
| F05.05 | Rotate | In Progress | 本体: Ctrl+R / Ctrl+Shift+R・ツールバー。表示した画素を回転（ファイルは変更しない）。GIF アニメーションも回転して再生。E2E `app-zoom-rotate` |
| F05.06 | Flip horizontal | In Progress | 本体: 「…」メニューの左右反転（表示のみ）。単体テスト `PixelOrientationTests` |
| F05.07 | Flip vertical | In Progress | 本体: 「…」メニューの上下反転（表示のみ） |
| F05.08 | Fullscreen | Planned |  |
| F05.09 | Slideshow | Planned |  |
| F05.10 | Image information | In Progress | 本体: ファイル情報パネル（Ctrl+I）。Windows のプロパティシステムから名前・場所・種類・サイズ・日時・大きさ・解像度・色深度・撮影日時・カメラ・レンズ・露出・絞り・ISO・焦点距離・位置（緯度経度）・タイトル・作成者等。E2E `app-info-copy-settings` |
| F05.11 | Pixel-level inspection where useful | Planned |  |
| F05.12 | Transparency checkerboard | Planned |  |
| F05.13 | Drag & drop | In Progress | 本体: ファイルをウィンドウにドロップして開く（Explorer からのドラッグを合成マウス入力で実機確認）。アプリからのドラッグ（書き出し）は未実装 |
| F05.14 | Copy/paste | In Progress | コピー: Ctrl+C でファイル（画像は画像データも）。貼り付け: Ctrl+V でクリップボードのファイルを開く。クリップボードの画像データを新規として開くのは未実装 |

## SPEC §6. Image Editing

| ID | 機能 | 状態 | 備考 |
|---|---|---|---|
| F06.01 | Crop | Planned |  |
| F06.02 | Resize | Planned |  |
| F06.03 | Rotate | Planned |  |
| F06.04 | Flip | Planned |  |
| F06.05 | Brightness | Planned |  |
| F06.06 | Contrast | Planned |  |
| F06.07 | Saturation | Planned |  |
| F06.08 | Tint | Planned |  |
| F06.09 | Exposure | Planned |  |
| F06.10 | Gamma | Planned |  |
| F06.11 | Sharpness | Planned |  |
| F06.12 | Temperature | Planned |  |
| F06.13 | Auto Color | Planned |  |
| F06.14 | Background removal | Investigating | Windows AI（Copilot+ のみ）/ 商用可 ONNX モデル選定中。RMBG は非商用のため不可 |
| F06.15 | Other practical non-lasso selection tools | Planned |  |
| F06.16 | Copy | Planned |  |
| F06.17 | Paste | Planned |  |
| F06.18 | Markup | Planned |  |
| F06.19 | Text | Planned |  |
| F06.20 | Shapes | Planned |  |
| F06.21 | Freehand drawing | Planned |  |
| F06.22 | Signature | Planned | Mavue.Markup の署名モデルを共用 |
| F06.23 | Undo | Planned |  |
| F06.24 | Redo | Planned |  |
| F06.25 | Lasso | Excluded (SPEC §29) | |
| F06.26 | Smart Lasso | Excluded (SPEC §29) | |

## SPEC §7. Image Conversion

| ID | 機能 | 状態 | 備考 |
|---|---|---|---|
| F07.01 | JPG → PNG | Planned |  |
| F07.02 | PNG → JPG | Planned |  |
| F07.03 | WebP | Planned |  |
| F07.04 | HEIC → JPG | Planned |  |
| F07.05 | HEIC → PNG | Planned |  |
| F07.06 | TIFF | Planned |  |
| F07.07 | BMP | Planned |  |
| F07.08 | GIF | Planned |  |
| F07.09 | AVIF | Planned |  |
| F07.10 | JPEG 2000 | Planned |  |
| F07.11 | other practical formats | Planned |  |
| F07.12 | JPEG quality | Planned |  |
| F07.13 | PNG compression | Planned |  |
| F07.14 | metadata preservation | Planned |  |
| F07.15 | metadata removal | Planned |  |
| F07.16 | batch conversion where practical | Planned |  |

## SPEC §8. Metadata

| ID | 機能 | 状態 | 備考 |
|---|---|---|---|
| F08.01 | File size | In Progress | 表示のみ（情報パネル）。編集・削除は未実装 |
| F08.02 | Resolution | In Progress | 表示のみ（情報パネル） |
| F08.03 | DPI | In Progress | 表示のみ（情報パネル） |
| F08.04 | Creation time | In Progress | 表示のみ（情報パネル） |
| F08.05 | Modification time | In Progress | 表示のみ（情報パネル） |
| F08.06 | Author | In Progress | 表示のみ（情報パネル） |
| F08.07 | Keywords | In Progress | 表示のみ（情報パネル） |
| F08.08 | GPS | In Progress | 緯度・経度・高度を情報パネルに表示（地図表示は未実装。外部送信なし） |
| F08.09 | EXIF | In Progress | 主な EXIF（撮影日時・カメラ・レンズ・露出・絞り・ISO・焦点距離）を表示のみ |
| F08.10 | EXIF removal | Planned |  |
| F08.11 | ICC profile metadata handling where practical | Planned | ColorSync/Soft Proof は除外 |

## SPEC §9. PDF Viewer

| ID | 機能 | 状態 | 備考 |
|---|---|---|---|
| F09.01 | Fast PDF rendering | In Progress | PDFium（`Mavue.Pdf`、bblanchon/pdfium-binaries 156.0.8076）で描画。本体は見えているページから順にバックグラウンドで描画し、ズーム中は伸縮表示→止まったら表示サイズで再描画。Quick View も本体と共通の `PdfSession`/`PdfDocumentView`（表示中のみ文書を開き、閉じる・次のファイルへ移るとすぐ解放。常駐プロセスがファイルを掴み続けない）。pdfium.dll が無い環境は Windows.Data.Pdf にフォールバック（表示のみ）（実機で pdfium.dll を外して `pdf-pages` PASS を確認） |
| F09.02 | Page navigation | In Progress | Quick View: PageUp/PageDown（Explorer が前面のときはフックで受け取り、Explorer の選択は変えない）・ホイール・前後ボタン、前後ページ先読み（E2E `pdf-pages`）。本体: PageUp/PageDown・Home/End・ツールバー・サムネイル・目次・リンク・検索結果。連続/見開きではスクロールして移動、単一ページではホイールでもページ送り（E2E `app-pdf-pages`・`app-pdf-layouts`）。Quick View も連続/見開きでスクロール移動、ページ番号欄（Ctrl+G）・前後ボタン・PageUp/PageDown（Explorer 前面でもフック経由）を維持（E2E `quickview-pdf`） |
| F09.03 | Thumbnail sidebar | In Progress | 本体: サイドバー「ページ」。見えた項目だけ描画（最大 240 枚の参照を保持、表示中のものは破棄しない）。クリックでページ移動、現在ページに追従。E2E `app-pdf-navigation`。Quick View も同じ実装（`PdfSession`・`PdfSearchBar`・`PdfSidebar`、E2E `quickview-pdf`）：ツールバーのサイドバーボタンで「ページ・目次・検索結果」を表示（開閉状態は設定に保存） |
| F09.04 | Table of contents | In Progress | 本体と Quick View: サイドバー「目次・しおり」（PDF のアウトライン＝しおり、階層表示、最大 20,000 項目・64 階層）。項目で該当ページ・位置へ移動。E2E `app-pdf-text`・`quickview-pdf`（UI Automation で「Chapter 5」→ 5 ページ） |
| F09.05 | Page numbers | In Progress | Quick View: 情報バーに「PDF · 現在 / 総ページ」、ツールバーにページ番号欄と「/ 総ページ」。本体: ツールバーに「現在 / 総ページ」 |
| F09.06 | Jump to page | In Progress | 本体と Quick View: ツールバーのページ番号欄（Ctrl+G）、Home/End で先頭/末尾。E2E `app-pdf-navigation`・`quickview-pdf`（Ctrl+G 1 Enter → 1 ページ） |
| F09.07 | Continuous scroll | In Progress | 本体と Quick View の既定（それぞれの設定に保存）。表示中の前後 1 画面分のページだけ要素とビットマップを持つ（600 ページで要素 5〜8 枚、E2E `app-pdf-layouts`）。Quick View E2E `quickview-pdf`（ホイールでスクロール） |
| F09.08 | Single-page view | In Progress | 本体: 表示方法メニュー・Ctrl+Shift+1。Quick View: 表示方法メニュー・Ctrl+Shift+1（単一ページではホイールでもページ送り、E2E `pdf-pages`） |
| F09.09 | Two-page spread | In Progress | 本体と Quick View: 表示方法メニュー・Ctrl+Shift+3（1–2, 3–4 …）と Ctrl+Shift+4「見開き（表紙を単独）」（1, 2–3, 4–5 …）。連続スクロール、PageDown は見開き単位。E2E `app-pdf-layouts`（表紙単独: 1 ページ目が単独行、2–3 が横並び）・`quickview-pdf`（2–3 ページが横並び）、単体テスト `PdfPageLayoutTests` |
| F09.10 | Zoom | In Progress | 本体: 画像と同じ拡大縮小（F05.01）。拡大後は表示サイズで再描画（鮮明） |
| F09.11 | Fit Width | In Progress | 本体: 倍率メニュー・Ctrl+2 |
| F09.12 | Fit Page | In Progress | 本体: 既定・Ctrl+0（PDF はページを拡大して収める） |
| F09.13 | Actual Size | In Progress | 本体: Ctrl+1（96 dpi × モニター倍率）。Quick View: 設定 `imageScale=ActualSize` |
| F09.14 | Ctrl+F search | In Progress | 本体と Quick View: Ctrl+F の検索バー。全ページをバックグラウンドで検索（進捗表示、最大 10,000 件）、大文字小文字の区別、Enter/F3 で次・Shift+Enter/Shift+F3 で前（循環）、全結果を黄、現在の結果を橙で強調。E2E `app-pdf-text`・`quickview-pdf`（8 件、F3 で次。日本語 PDF で「東京」2 件）。Quick View では検索欄の Esc は検索を閉じるだけ |
| F09.15 | Search result list | In Progress | 本体と Quick View: サイドバー「検索結果」（ページ番号と前後の文字。クリックでその結果へ） |
| F09.16 | Text selection | In Progress | 本体と Quick View: ドラッグで選択（ページをまたぐ選択可）、ダブルクリックで単語、Shift+クリックで延長、Ctrl+A で全文、Esc で解除。PDFium の文字位置で判定（UI スレッドを止めないよう別スレッド）。E2E `app-pdf-text`・`quickview-pdf`（ドラッグ選択、日本語 PDF の Ctrl+A）。Quick View の Esc は常に閉じる（Quick Look と同じ） |
| F09.17 | Copy | In Progress | 本体と Quick View: 選択中は Ctrl+C で文字をコピー（ページ間は改行）。選択がなければファイルをコピー。E2E `quickview-pdf`（英文のドラッグ選択と日本語 PDF 全文のコピー） |
| F09.18 | Internal PDF links | In Progress | 本体と Quick View: リンク注釈（ページ・位置へ移動）。カーソルが手の形になる。E2E `app-pdf-text`（「Go to the last page」→ 8 ページ）・`quickview-pdf` |
| F09.19 | External URLs | In Progress | 本体と Quick View: URI リンクと本文中の URL（PDFium の Web リンク検出）を既定のブラウザ/メールで開く。http/https/mailto のみ（javascript:・file:・起動アクションは実行しない）。E2E は `--no-launch` で記録のみ（`app-pdf-text`・`quickview-pdf`） |
| F09.20 | Bookmarks | Planned | PDF 自体のしおり（アウトライン）は F09.04 で表示・移動に対応。利用者が任意のページに付けるブックマーク（Preview の「ブックマークを追加」相当）は未実装 |
| F09.21 | Print | Planned |  |
| F09.22 | Presentation/slideshow where practical | In Progress | F5（本体はメニュー「プレゼンテーション」、Quick View はツールバー）で現在ページから全画面表示（共通の `PdfPresentationWindow`、ページを画面に収めて描画、前後ページ先読み）。→/↓/PageDown/Space/クリックで次、←/↑/PageUp/BackSpace で前、Home/End、Esc で終了し元の表示に戻る（Quick View は開いたまま）。E2E `app-pdf-layouts`（全画面・→ で 2 ページ・Esc で終了）・`quickview-pdf` |
| F09.23 | PDF-as-image rendering where useful | Planned |  |

## SPEC §10. PDF Page Management

| ID | 機能 | 状態 | 備考 |
|---|---|---|---|
| F10.01 | Add page | Planned |  |
| F10.02 | Blank page | Planned |  |
| F10.03 | Add pages from another PDF/file | Planned |  |
| F10.04 | Delete page | Planned |  |
| F10.05 | Move page | Planned |  |
| F10.06 | Reorder pages | Planned |  |
| F10.07 | Drag-and-drop page reorder | Planned |  |
| F10.08 | Duplicate page | Planned |  |
| F10.09 | Extract pages | Planned |  |
| F10.10 | Split PDF | Planned |  |
| F10.11 | Merge PDFs | Planned |  |
| F10.12 | Move pages between PDFs | Planned |  |
| F10.13 | Copy pages between PDFs | Planned |  |
| F10.14 | Rotate one page | Planned |  |
| F10.15 | Rotate multiple pages | Planned |  |
| F10.16 | Crop pages | Planned |  |
| F10.17 | Change page size | Planned |  |

## SPEC §11. PDF Markup and Annotations

| ID | 機能 | 状態 | 備考 |
|---|---|---|---|
| F11.01 | Highlight | Planned |  |
| F11.02 | Underline | Planned |  |
| F11.03 | Strikethrough | Planned |  |
| F11.04 | Text box | Planned |  |
| F11.05 | Note/comment | Planned |  |
| F11.06 | Speech bubble | Planned |  |
| F11.07 | Rectangle | Planned |  |
| F11.08 | Ellipse/circle | Planned |  |
| F11.09 | Line | Planned |  |
| F11.10 | Arrow | Planned |  |
| F11.11 | Freehand | Planned |  |
| F11.12 | Pen | Planned |  |
| F11.13 | Marker | Planned |  |
| F11.14 | Image insertion | Planned |  |
| F11.15 | Stamp | Planned |  |
| F11.16 | Color | Planned |  |
| F11.17 | Line width | Planned |  |
| F11.18 | Fill | Planned |  |
| F11.19 | Opacity | Planned |  |
| F11.20 | Move annotations | Planned |  |
| F11.21 | Resize annotations | Planned |  |
| F11.22 | Delete annotations | Planned |  |
| F11.23 | Annotation list/sidebar | Planned |  |
| F11.24 | Zoom lens where practical | Planned |  |
| F11.25 | Other Preview-like annotation features | Planned |  |
| F11.26 | Lasso | Excluded (SPEC §29) | |
| F11.27 | Smart Lasso | Excluded (SPEC §29) | |

## SPEC §12. PDF Signing

| ID | 機能 | 状態 | 備考 |
|---|---|---|---|
| F12.01 | Signature creation | Planned |  |
| F12.02 | Mouse signature | Planned |  |
| F12.03 | Windows Ink signature | Investigating | WinUI 3 InkCanvas は Experimental。Pointer API で独自実装。実機ペン検証は Blocked（デバイスなし） |
| F12.04 | Signature image import | Planned |  |
| F12.05 | Save signatures | Planned |  |
| F12.06 | Place signature | Planned |  |
| F12.07 | Move signature | Planned |  |
| F12.08 | Resize signature | Planned |  |
| F12.09 | Delete signature | Planned |  |
| F12.10 | Multiple saved signatures | Planned |  |
| F12.11 | Webcam signature where practical | Investigating | Windows.Media.Capture + 2 値化・背景除去 |

## SPEC §13. PDF Forms

| ID | 機能 | 状態 | 備考 |
|---|---|---|---|
| F13.01 | Text fields | Planned |  |
| F13.02 | Checkboxes | Planned |  |
| F13.03 | Radio buttons | Planned |  |
| F13.04 | Dropdowns | Planned |  |
| F13.05 | Form saving | Planned |  |
| F13.06 | Printing | Planned |  |
| F13.07 | AutoFill | Planned |  |
| F13.08 | Reset where available | Planned |  |

## SPEC §14. PDF OCR

| ID | 機能 | 状態 | 備考 |
|---|---|---|---|
| F14.01 | OCR | Planned | Windows.Media.Ocr 既定（本機 ja/en 利用可を確認） |
| F14.02 | Scanned PDF OCR | Planned |  |
| F14.03 | Select OCR text | Planned |  |
| F14.04 | Copy OCR text | Planned |  |
| F14.05 | Japanese OCR | Investigating | Windows.Media.Ocr / Tesseract jpn・jpn_vert / PaddleOCR の精度比較が必要 |
| F14.06 | English OCR | Planned |  |
| F14.07 | Multilingual OCR | Planned |  |
| F14.08 | Search OCR text | Planned |  |
| F14.09 | Embed OCR text | Planned |  |
| F14.10 | OCR settings | Planned |  |

## SPEC §15. PDF Security

| ID | 機能 | 状態 | 備考 |
|---|---|---|---|
| F15.01 | Password protection | Planned | 保護の付与は QPDF（AES-256）で Planned。保護された PDF を**開く**: 本体はパスワード入力ダイアログ（誤りは再入力、キャンセルで案内、パスワードは保存・記録しない）、Quick View とプレビューは「Mavue で開く」案内、サムネイルは作らない（E2E `app-pdf-password`、単体 `EncryptedPdf_*`） |
| F15.02 | Encryption | Planned | QPDF |
| F15.03 | Print restrictions | Planned |  |
| F15.04 | Copy restrictions | Planned |  |
| F15.05 | Edit restrictions | Planned |  |
| F15.06 | Annotation restrictions | Planned |  |
| F15.07 | True redaction | Investigating | PDFium/QPDF に API なし。独自実装＋抽出検証。最大リスク項目 |
| F15.08 | Flatten annotations | Planned |  |

## SPEC §16. PDF Compression and Conversion

| ID | 機能 | 状態 | 備考 |
|---|---|---|---|
| F16.01 | PDF compression | Planned |  |
| F16.02 | Quality controls | Planned |  |
| F16.03 | Image compression | Planned |  |
| F16.04 | Web optimization / linearization | Planned | QPDF |
| F16.05 | PDF → JPG | Planned |  |
| F16.06 | PDF → PNG | Planned |  |
| F16.07 | PDF → TIFF | Planned |  |
| F16.08 | JPG → PDF | Planned |  |
| F16.09 | PNG → PDF | Planned |  |
| F16.10 | Multiple images → PDF | Planned |  |
| F16.11 | Multiple PDFs → PDF | Planned |  |
| F16.12 | PDF → Word | Backlog (SPEC §16) | |
| F16.13 | PDF → Excel | Backlog (SPEC §16) | |
| F16.14 | PDF → PowerPoint | Backlog (SPEC §16) | |

## SPEC §17. Scanning

| ID | 機能 | 状態 | 備考 |
|---|---|---|---|
| F17.01 | WIA scanner support | Planned | 実機検証は Blocked（スキャナ未接続） |
| F17.02 | TWAIN scanner support | Investigating | 64bit DSM + x86 ブリッジ。実機検証 Blocked |
| F17.03 | Scanner → PDF | Planned |  |
| F17.04 | Scanner → JPG | Planned |  |
| F17.05 | ADF | Planned | 実機検証 Blocked |
| F17.06 | Duplex scanning | Planned | 実機検証 Blocked |
| F17.07 | Color | Planned |  |
| F17.08 | Grayscale | Planned |  |
| F17.09 | Black & white | Planned |  |
| F17.10 | DPI selection | Planned |  |
| F17.11 | Auto deskew | Planned |  |
| F17.12 | OCR | Planned |  |
| F17.13 | Multi-page PDF | Planned |  |

## SPEC §18. Printing

| ID | 機能 | 状態 | 備考 |
|---|---|---|---|
| F18.01 | Print | Planned | D2D/XPS 印刷パス + 標準印刷 UI。物理プリンタ検証は Blocked |
| F18.02 | Page selection | Planned |  |
| F18.03 | Copies | Planned |  |
| F18.04 | Paper size | Planned |  |
| F18.05 | Orientation | Planned |  |
| F18.06 | Scaling | Planned |  |
| F18.07 | Pages per sheet | Planned |  |
| F18.08 | Fit to paper | Planned |  |
| F18.09 | Color | Planned |  |
| F18.10 | Black & white | Planned |  |
| F18.11 | Print preview | Planned |  |

## SPEC §19. Audio and Video Preview

| ID | 機能 | 状態 | 備考 |
|---|---|---|---|
| F19.01 | Audio preview | In Progress | Quick View（F03.17）と本体（共通の `MediaSession`。E2E `app-formats` で MP3・WAV・M4A・WMA・FLAC の音声出力を確認） |
| F19.02 | Video preview | In Progress | Quick View（F03.18）と本体（E2E `app-formats` で MP4・WMV の画面上の映像と音声を確認） |
| F19.03 | Playback controls | In Progress | Quick View: 標準の操作バー（再生/一時停止）と Enter（Quick View にフォーカスがあるとき）。本体: 操作バーと Space/Enter（E2E `app-media-keys`） |
| F19.04 | Seek | In Progress | Quick View と本体: シークバーと Ctrl+←/→（10 秒） |
| F19.05 | Volume | In Progress | Quick View と本体: 音量/ミュートボタンと Ctrl+↑/↓（10%） |
| F19.06 | Fullscreen | Planned | WinUI 3 の MediaTransportControls には全画面ボタンがない（テンプレートでコメントアウト、未サポート）。別途実装が必要 |
| F19.07 | Supported Windows/media formats where practical | In Progress | 形式判定: MP4/M4V・MOV・MKV・WebM・AVI・WMV/ASF・MPEG-TS/M2TS・MPEG-PS・Ogg（映像/音声）・MP3・AAC・M4A/M4B・WAV・FLAC・Opus・WMA。再生は Windows（Media Foundation）にあるデコーダー次第。実機確認済み: MP4(H.264/AAC)・MP3・WAV・M4A・WMA・FLAC・WMV。MKV/WebM/HEVC 等は Store の拡張機能の有無に依存（未確認）。FFmpeg は法務確認待ち |
| F19.08 | Video trimming | Excluded (SPEC §29) | |

## SPEC §20. GIF

| ID | 機能 | 状態 | 備考 |
|---|---|---|---|
| F20.01 | Animated GIF preview | In Progress | Quick View のみ: 各フレームを GIF の遅延時間で再生（20 ms 未満は 100 ms）、NETSCAPE2.0 のループ回数どおり繰り返し、オフセット・透過・破棄方法（0〜3）を合成。フレームはバックグラウンドで 1 枚ずつ復号し、メモリはキャンバス分のみ。800 万画素を超える GIF は 1 コマ目の静止表示。ファイル切替・ページ送り・非表示で停止。E2E `gif-animation` で画面上の変化・ループ・二重再生なしを確認。本体も同じ `GifPlayer`（E2E `app-formats`） |
| F20.02 | Playback | In Progress | 本体: 自動再生、Space/Enter で一時停止・再開。Quick View: 自動再生、Enter で一時停止・再開（アクティブ時） |
| F20.03 | Pause | In Progress | 本体 Space/Enter、Quick View Enter。E2E `app-gif-controls` |
| F20.04 | Frame navigation where practical | In Progress | 本体: , / . で前後のコマ（一時停止して移動、戻るときは先頭から合成し直す）。E2E `app-gif-controls` |
| F20.05 | Fullscreen | Planned |  |
| F20.06 | Zoom | In Progress | 本体・Quick View の拡大縮小（要素の伸縮のみで再デコード不要） |

## SPEC §21. File Operations

| ID | 機能 | 状態 | 備考 |
|---|---|---|---|
| F21.01 | Save | In Progress | Mavue.Core SafeFileWriter（アトミック置換）を実装・テスト中。UI 未接続 |
| F21.02 | Save As | Planned |  |
| F21.03 | Export | Planned |  |
| F21.04 | Auto Save | Planned |  |
| F21.05 | Undo | Planned |  |
| F21.06 | Redo | Planned |  |
| F21.07 | Duplicate | Planned |  |
| F21.08 | Rename | Planned |  |
| F21.09 | Lock | Planned |  |
| F21.10 | File properties/info | In Progress | 情報パネル（F05.10）と Windows のプロパティダイアログ（Alt+Enter） |
| F21.11 | Recent files | In Progress | 開いたファイルを最大 15 件記録（%LOCALAPPDATA%\Mavue\settings.json）。ツールバーと起動画面から開く。消去可能。E2E `app-info-copy-settings` |
| F21.12 | Drag & drop | In Progress | F05.13 と同じ（ドロップで開く）。複数ファイルのドロップはその順に前後移動 |
| F21.13 | Clipboard | In Progress | Ctrl+C（ファイル＋画像データ）、Ctrl+V（ファイルを開く） |
| F21.14 | Version/history/backup where practical | Planned |  |
| F21.15 | Tabs | Planned |  |
| F21.16 | Multiple windows | In Progress | 「新しいウィンドウ」（Ctrl+N）。ファイルを開くたびに別プロセスのウィンドウ（Explorer の「開く」も同様） |

## SPEC §22. Windows Explorer Integration

| ID | 機能 | 状態 | 備考 |
|---|---|---|---|
| F22.01 | Space-key Quick View | In Progress | F03.01 と同一（PoC 実装済み） |
| F22.02 | Right-click “Open in Mavue” | In Progress | **従来メニュー**: 「Mavue で開く」（設定画面の「Windows に追加」または `Mavue.exe --register`。HKCU の SystemFileAssociations、1 ファイル選択時）。**Windows 11 上段メニューは未実装**（IExplorerCommand の追加と識別パッケージの再登録が必要） |
| F22.03 | Right-click “Mavue Quick View” | In Progress | **Windows 11 上段メニュー**: `IExplorerCommand`（C++、`native/Mavue.Shell.Native`）+ 署名済み識別パッケージ（スパース）で実装。DLL は Explorer に `--quickview` を起動させるだけで、既存のパイプ経路に合流（前面表示・複数選択・選択追従・未起動からの起動を実機確認。WINDOWS-INTEGRATION §15）。Windows 11 標準の新メニュー（上段）と StartAllBack の従来型メニューの両方で実機確認。**従来メニュー**（Windows 10・パッケージ未登録時）: HKCU の SystemFileAssociations（`--register`）。新メニュー登録時は従来メニューを外す。本番配布には信頼される証明書での署名が必要。2026-10-04: Explorer 起動/再起動の直後（デスクトップが ShellWindows に未登録の数秒間）に初回のコマンドが失敗していたのを、開いている Explorer ウィンドウ経由で起動するよう修正（実機確認）。失敗時に 1 回再試行 |
| F22.04 | File associations | In Progress | `Mavue.exe --register`／設定画面: ProgID（Mavue.Image/Pdf/Video/Audio）・OpenWithProgids・Applications・Capabilities・RegisteredApplications・App Paths を HKCU に登録。「プログラムから開く」と「設定 › 既定のアプリ」に Mavue が出る。既定の変更は Windows の仕様どおりユーザーが行う（設定画面から既定のアプリのページを開ける）。単体テスト `AppRegistrationTests`。Explorer の実機確認は未 |
| F22.05 | PDF association | In Progress | F22.04（.pdf → Mavue.Pdf） |
| F22.06 | Image associations | In Progress | F22.04（画像の全拡張子 → Mavue.Image） |
| F22.07 | Thumbnail Provider | In Progress | IThumbnailProvider（ストリーム初期化、Explorer の分離プロセスで動作）。PDF 1 ページ目・SVG・WIC 画像。既存のプロバイダーが無い拡張子だけ登録（本機では .pdf・.svg。JPEG 等は Windows のまま）。E2E `explorer-thumbnails`（Windows 経由で生成、大アイコン表示で画面上に SVG）、単体 `PreviewNativeTests`。32 ビット アプリ（ファイル ダイアログ等）用に x86 版（`x86\Mavue.Shell.Preview.dll` と x86 の pdfium.dll）を 32 ビット ビューに登録（Windows の 32 ビット分離プロセスで動作、E2E `explorer-32bit`）。MSIX 版: PDF は Mavue、SVG は使われない（他のパッケージも .svg を宣言しているため。PACKAGING §5.3） |
| F22.08 | Preview Pane | In Progress | Explorer のプレビューウィンドウ: `Mavue.Shell.Preview.dll`（C++、WINDOWS-INTEGRATION §4）を `Mavue.exe --register` で登録。画像（向き適用・拡大しない）、GIF アニメ、SVG、動画・音声（クリックで再生、シーク・ミュート）、PDF（幅に合わせた連続表示、表示付近のページだけ描画）。既存のプレビューがある形式（本機の PDF は Edge）は奪わない（`--prefer-mavue-preview` で明示的に切り替え、解除で復元）。不正・空のファイルは文言表示。E2E `explorer-preview-pane`・`explorer-preview-pdf`・`explorer-preview-keyboard`、単体 `PreviewNativeTests`。設定画面「エクスプローラーのプレビュー ウィンドウでの PDF」で Mavue／既存（Edge 等）を切り替え（既定は既存のまま。効かない環境では案内）。CLI は `--prefer-mavue-preview`。**MSIX 版では他のパッケージ（フォト・メディア プレーヤー等）も宣言する種類で Explorer が使わない**（Mavue だけが宣言する .x3f では使われる。ZIP 版は全種類で動作。PACKAGING §5.3、2026-10-04 実測） |
| F22.09 | Preview Handler | In Progress | IPreviewHandler（専用 AppID の prevhost.exe で動作、ストリーム初期化、テーマ色に追従）。詳細は F03.20。登録・解除は `Mavue.exe --register` / `--unregister`（Explorer 再起動後も有効、解除で Mavue の値だけ削除: E2E `explorer-lifecycle`）。32 ビット アプリ用は Windows の 32 ビット prevhost（AppID {534A1E02-…}）で動作（E2E `explorer-32bit`） |
| F22.10 | Shell extension | In Progress | ネイティブ DLL 2 つ: `Mavue.Shell.Native`（上段メニュー、F22.03）と `Mavue.Shell.Preview`（プレビュー・サムネイル）。どちらも静的 CRT、`/W4 /WX`、x64・ARM64 |
| F22.11 | Windows Search integration | Investigating | IFilter 登録方式を調査 |
| F22.12 | Drag & drop | Planned |  |
| F22.13 | Clipboard | In Progress | Mavue からのコピーを Explorer に貼り付け、Explorer でコピーしたファイルを Mavue で Ctrl+V |
| F22.14 | Context actions | Planned |  |
| | **コンテキスト操作** | | |
| F22.15 | Merge PDFs | Planned |  |
| F22.16 | Split PDFs | Planned |  |
| F22.17 | Compress PDFs | Planned |  |
| F22.18 | Convert images | Planned |  |
| F22.19 | Create PDF from images | Planned |  |
| F22.20 | Other relevant file operations | Planned |  |

## SPEC §23. System and UI

| ID | 機能 | 状態 | 備考 |
|---|---|---|---|
| F23.01 | Dark mode | In Progress | 本体: 設定（Windows に従う／ライト／ダーク、ウィンドウ内容とタイトルバー）。Quick View: Windows のアプリ モードに追従（暗色 #1E1E1E／明色 #F3F3F3、タイトルバーも、切り替えに即時追従）。Explorer のプレビュー: 同上（Explorer が渡す色は暗色でも白黒のため自前で判定、表示中の切り替えにも追従）。E2E `explorer-theme`・`theme-light`（一時的に明色へ切り替えて 3 つとも確認し、元に戻す） |
| F23.02 | Light mode | In Progress | F23.01 と同じ |
| F23.03 | Settings | In Progress | 本体の設定ダイアログ（Ctrl+,）: 外観、開いたときの表示サイズ、Quick View の表示サイズ（Quick View の settings.json に保存し、次に開いたときから反映）、Windows との統合（追加・削除・既定のアプリ）、最近使ったファイルの消去。即時保存（SafeFileWriter） |
| F23.04 | Keyboard shortcuts | In Progress | 本体: 一覧ダイアログ（F1）。開く・ファイル/ページ移動・拡大縮小・回転・再生・コピー/貼り付け・情報・プロパティ・新しいウィンドウ・閉じる（Ctrl+W）・設定。カスタマイズは未実装 |
| F23.05 | High-DPI support | In Progress | Quick View: ウィンドウのあるモニターの DPI で物理ピクセルに合わせてデコード。本体: モニターごとの DPI で表示（E2E `app-monitors`: 150 %/100 %/100 % の 3 台）。Explorer のプレビュー: プレビューのウィンドウがペインの DPI を持ち、DPI の違うモニターへの移動で再配置（E2E `explorer-monitors`） |
| F23.06 | Multi-monitor support | In Progress | Quick View: Explorer のあるモニターに表示（E2E `monitor-*`）。本体・Explorer のプレビュー: 3 台のモニターで確認（`app-monitors`・`explorer-monitors`） |
| F23.07 | Accessibility | Planned |  |
| F23.08 | Touch support | Planned |  |
| F23.09 | Windows Ink support | Investigating | InkCanvas Experimental。実機ペン検証 Blocked |
| F23.10 | Japanese localization | In Progress | 最小構成で MRT Core resw (ja-JP/en-US) を導入 |
| F23.11 | English localization | In Progress | 同上 |

## SPEC §24. Performance

| ID | 機能 | 状態 | 備考 |
|---|---|---|---|
| F24.01 | Fast startup | Planned |  |
| F24.02 | Fast Quick View | In Progress | PoC 実測: Space→表示呼び出し 7〜12 ms、小画像フル品質 約 21 ms、24MP JPEG 約 103 ms（Debug）。Release: 24MP JPEG 約 87 ms（WIC 直接）、先読み済みの次ファイル 約 30 ms、ReadyToRun で起動直後の初回表示 59〜60 ms |
| F24.03 | Responsive UI | Planned |  |
| F24.04 | Efficient large-image rendering | In Progress | 画面サイズへの縮小デコード（192 MP JPEG で WS +36 MB を実測）。JPEG は WIC 直接デコード（192 MP で 449 → 251 ms）。ICC プロファイル付き JPEG（スマートフォン写真に多い）は色管理付きの従来経路のまま（24 MP で約 260 ms、ユーザーの実ファイルで確認）。タイル描画は未実装 |
| F24.05 | Efficient large-PDF rendering | Planned |  |
| F24.06 | Efficient multi-page PDF navigation | Planned |  |
| F24.07 | RAW image handling | Planned |  |
| F24.08 | Background decoding | In Progress | PoC: デコードはバックグラウンド、UI スレッド非ブロック、キャンセル可能 |
| F24.09 | Incremental rendering where appropriate | In Progress | PoC: サムネイル → フル品質の段階表示（白画面で待たない）を実測で確認 |
| F24.10 | GPU acceleration where appropriate | Planned |  |
| F24.11 | CPU/software fallback | Planned |  |
| F24.12 | Thumbnail caching | In Progress | PoC: Windows サムネイルキャッシュを INCACHEONLY で利用。Mavue 独自キャッシュは未実装 |
| F24.13 | Preview caching | In Progress | Quick View: 表示中の前後を先読みしてメモリ内 LRU キャッシュ（192 MB）に保持。ディスクキャッシュは未実装 |
| F24.14 | Low unnecessary memory consumption | In Progress | Quick View: 非表示 1 秒後に GC、先読みの画素数上限、WIC 結果を中間配列なしで書き込み（原寸表示の繰り返しで 1 GB 超 → 約 0.5 GB で横ばい、実測） |

## SPEC §25. Large Files

| ID | 機能 | 状態 | 備考 |
|---|---|---|---|
| F25.01 | images | In Progress | PoC: 192 MP JPEG / 100 MP PNG をメモリ全展開せずに表示。1 ギガピクセル超は拒否 |
| F25.02 | PDFs | Planned |  |
| F25.03 | multi-page documents | Planned |  |
| F25.04 | RAW files | Planned |  |

## SPEC §26–28 横断要件

| ID | 要件 | 状態 | 備考 |
|---|---|---|---|
| X.01 | Unicode / 日本語ファイル名 | In Progress | Core のファイル処理で Unicode パスをテスト |
| X.02 | 多言語 PDF テキスト・メタデータ | In Progress | PDF の文字・アウトラインは PDFium の Unicode（UTF-16）で扱う。Edge で作成した日本語 PDF（埋め込みフォント）で文字抽出・「東京」の検索（2 ページ 2 件、強調位置）・リンクを確認（2026-10-03、手動）。縦書き・CJK の複雑な PDF は未確認 |
| X.03 | ローカル完結・テレメトリなし | In Progress | 最小構成に外部通信なし |
| X.04 | 機微データをログに出さない | Planned | |
| X.05 | モジュール構成（SPEC §28） | In Progress | 最小構成でプロジェクト分割済み |
| X.06 | 暗号学的デジタル署名（SPEC §12: 後段階・除外ではない） | Planned | CNG + PAdES |
| X.07 | フォーム AutoFill 用プロフィールの暗号化保存 | Planned | DPAPI |
| X.08 | 配布（SPEC §28・CLAUDE.md §17）: ランタイム同梱・インストーラー・MSIX・署名・ライセンス同梱 | In Progress | 2026-10-04: self-contained（.NET 10.0.12 + Windows App SDK）で Mavue と Quick View を 1 フォルダーに同居、`tools/build-release.ps1` が ZIP（Install.cmd、ユーザー単位、更新・アンインストール対応）・MSIX・シンボル・SHA256 を作成、`licenses\` を同梱し設定 › Mavue について から表示。ZIP 版のインストール→E2E→更新→削除、MSIX のインストール→起動→削除を実機確認。2026-10-04（続き）: ZIP/MSIX の整合チェック（同一 500 ファイル・必須ファイル・署名・シンボル）をビルドに組み込み、ZIP の項目名のバックスラッシュを修正、MSIX の更新（使用中は失敗、強制で可）を確認、配布形態の比較・別 PC チェックリスト・EULA たたき台を作成。未完: 正式配布形態の決定、公開用署名証明書、EULA の確定、MSIX の Explorer 連携の制約（docs/PACKAGING.md §1・§5.3・§9） |

## 未実装項目の整理（2026-10-03、SPEC との照合）

SPEC の項目は**削除・延期していない**。下の「後回し」は今回の作業範囲に入れなかったという意味で、各項目の状態は上の表のとおり Planned / Investigating のまま。
優先度（A: 次に着手を推奨、B: その次、C: 大きな機能群・前提あり）は提案であり、順序の決定は利用者が行う。

### 今回対応（このバッチで進めた項目。完了扱いではなく In Progress のまま）

| 項目 | 今回の内容 |
|---|---|
| Dark mode / Light mode（F23.01 / F23.02） | Quick View と Explorer のプレビューも Windows のアプリ モードに追従（Explorer は暗色でも白を渡すため自前で判定）。E2E `explorer-theme`・`theme-light` |
| High-DPI / Multi-monitor（F23.05 / F23.06） | 本体（`app-monitors`）と Explorer のプレビュー（`explorer-monitors`）を 3 台・2 種の DPI で確認。DPI の違うモニターへの移動も |
| Thumbnail Provider / Preview Handler（F22.07 / F22.09） | 32 ビット アプリ（ファイル ダイアログ）用の x86 版と 32 ビット ビューへの登録（`explorer-32bit`） |
| Preview Pane（F22.08） | PDF のプレビューを Mavue にする／戻すを設定画面で切り替え |
| パスワード付き PDF を開く（F15.01 の関連・F09） | 本体はパスワード入力（誤り時は再入力、キャンセルで案内）。Quick View・プレビューは「Mavue で開く」案内、サムネイルは作らない。E2E `app-pdf-password` |
| 配布（SPEC §28・CLAUDE.md §17） | MSIX パッケージ（`tools/package-msix.ps1`）とコード署名（`tools/sign-release.ps1`）。2026-10-04 に X.08 として継続。docs/PACKAGING.md |

### 後回し（未実装 208 項目。Planned / Investigating のまま）

| SPEC | 件数 | 優先度（提案） | 項目 |
|---|---|---|---|
| §3 Quick View / Quick Look | 9 | A | Fullscreen, Thumbnail/index sheet, Share, Print, Markup from Quick View, HDR support where practical（調査中）, Drag & drop, Clipboard integration, Windows Search integration where practical（調査中） |
| §4 Image Formats | 2 | C | JPEG 2000, other practical image formats where appropriate |
| §5 Image Viewing | 4 | A | Fullscreen, Slideshow, Pixel-level inspection where useful, Transparency checkerboard |
| §6 Image Editing | 24 | B | Crop, Resize, Rotate, Flip, Brightness, Contrast, Saturation, Tint, Exposure, Gamma, Sharpness, Temperature, Auto Color, Background removal（調査中）, Other practical non-lasso selection tools, Copy, Paste, Markup, Text, Shapes, Freehand drawing, Signature, Undo, Redo |
| §7 Image Conversion | 16 | B | JPG → PNG, PNG → JPG, WebP, HEIC → JPG, HEIC → PNG, TIFF, BMP, GIF, AVIF, JPEG 2000, other practical formats, JPEG quality, PNG compression, metadata preservation, metadata removal, batch conversion where practical |
| §8 Metadata | 2 | B | EXIF removal, ICC profile metadata handling where practical |
| §9 PDF Viewer | 3 | A | Bookmarks, Print, PDF-as-image rendering where useful |
| §10 PDF Page Management | 17 | B | Add page, Blank page, Add pages from another PDF/file, Delete page, Move page, Reorder pages, Drag-and-drop page reorder, Duplicate page, Extract pages, Split PDF, Merge PDFs, Move pages between PDFs, Copy pages between PDFs, Rotate one page, Rotate multiple pages, Crop pages, Change page size |
| §11 PDF Markup and Annotations | 25 | C | Highlight, Underline, Strikethrough, Text box, Note/comment, Speech bubble, Rectangle, Ellipse/circle, Line, Arrow, Freehand, Pen, Marker, Image insertion, Stamp, Color, Line width, Fill, Opacity, Move annotations, Resize annotations, Delete annotations, Annotation list/sidebar, Zoom lens where practical, Other Preview-like annotation features |
| §12 PDF Signing | 11 | C | Signature creation, Mouse signature, Windows Ink signature（調査中）, Signature image import, Save signatures, Place signature, Move signature, Resize signature, Delete signature, Multiple saved signatures, Webcam signature where practical（調査中） |
| §13 PDF Forms | 8 | C | Text fields, Checkboxes, Radio buttons, Dropdowns, Form saving, Printing, AutoFill, Reset where available |
| §14 PDF OCR | 10 | C | OCR, Scanned PDF OCR, Select OCR text, Copy OCR text, Japanese OCR（調査中）, English OCR, Multilingual OCR, Search OCR text, Embed OCR text, OCR settings |
| §15 PDF Security | 8 | C | Password protection, Encryption, Print restrictions, Copy restrictions, Edit restrictions, Annotation restrictions, True redaction（調査中）, Flatten annotations |
| §16 PDF Compression and Conversion | 11 | B | PDF compression, Quality controls, Image compression, Web optimization / linearization, PDF → JPG, PDF → PNG, PDF → TIFF, JPG → PDF, PNG → PDF, Multiple images → PDF, Multiple PDFs → PDF |
| §17 Scanning | 13 | C | WIA scanner support, TWAIN scanner support（調査中）, Scanner → PDF, Scanner → JPG, ADF, Duplex scanning, Color, Grayscale, Black & white, DPI selection, Auto deskew, OCR, Multi-page PDF |
| §18 Printing | 11 | A | Print, Page selection, Copies, Paper size, Orientation, Scaling, Pages per sheet, Fit to paper, Color, Black & white, Print preview |
| §19 Audio and Video Preview | 1 | A | Fullscreen |
| §20 GIF | 1 | A | Fullscreen |
| §21 File Operations | 10 | A | Save As, Export, Auto Save, Undo, Redo, Duplicate, Rename, Lock, Version/history/backup where practical, Tabs |
| §22 Windows Explorer Integration | 9 | B | Windows Search integration（調査中）, Drag & drop, Context actions, Merge PDFs, Split PDFs, Compress PDFs, Convert images, Create PDF from images, Other relevant file operations |
| §23 System and UI | 3 | B | Accessibility, Touch support, Windows Ink support（調査中） |
| §24 Performance | 7 | B | Fast startup, Responsive UI, Efficient large-PDF rendering, Efficient multi-page PDF navigation, RAW image handling, GPU acceleration where appropriate, CPU/software fallback |
| §25 Large Files | 3 | B | PDFs, multi-page documents, RAW files |

### 対象外（SPEC §29 の明示的な除外と §16 の Backlog のみ）

| 区分 | 項目 |
|---|---|
| 除外（SPEC §29） | 動画のトリミング、Copy Subject、Live Photo、EPS、OpenEXR、Lasso、Smart Lasso、3D 機能すべて（USD/USDZ/OBJ/STL/glTF/GLB/PLY/Alembic/MaterialX/OpenVDB/Gaussian Splat、3D の表示・編集・アニメーション・注釈・書き出し）、Soft Proof、ColorSync、Vision Pro / Apple 固有の 3D 連携 |
| Backlog（SPEC §16、恒久的な除外ではない） | PDF → Word、PDF → Excel、PDF → PowerPoint |

## 最小構成で実装済みの基盤（製品機能ではない）

| 基盤 | 状態 | 備考 |
|---|---|---|
| ソリューション / モジュール分割 | Implemented | `Mavue.slnx` |
| ファイル形式判定（マジックバイト） | Implemented | `Mavue.Core.Formats.FileFormatDetector`、単体テストあり |
| 安全保存（一時ファイル + アトミック置換） | Implemented | `Mavue.Core.IO.SafeFileWriter`、単体テストあり。シンボリックリンク経由の保存は実体を更新するよう修正（本機では symlink を作成できずテストはスキップ＝未検証） |
| WinUI 3 アプリ起動 | Implemented | `Mavue.App`（ja/en リソース、`tools/smoke-test.ps1`） |
| 本体の閲覧基盤 | In Progress | `Mavue.App`: 引数・開くダイアログ（Ctrl+O）・ドロップでファイルを開き、共通ビューアで画像・GIF・PDF・動画・音声を表示。1 ファイルならフォルダー内の対応ファイルを ←/→ で移動、複数ならその範囲。終了・切替で再生停止とファイル解放。ツールバー（拡大縮小・回転・ページ・サムネイル・情報・コピー・プログラムから開く・フォルダーに表示・プロパティ・新しいウィンドウ・設定・ショートカット）、最近使ったファイル、ウィンドウ位置の保存（`Mavue.Core.Settings.AppSettings`）。E2E `--app`（11 シナリオ） |
| 共通ビューア | In Progress | `Mavue.Viewer`（PDF 表示 `PdfDocumentView`（連続/見開き/単一、選択・検索強調・リンク、表示中付近のページだけ保持）、表示面 `ViewerSurface`（拡大・パン）・`DocumentViewer`（拡大縮小・回転・SVG・PDF を開いたまま描画する `PdfDocumentSession`）・画像デコード・GIF（一時停止・コマ送り・回転）・動画/音声・ファイル情報）と `Mavue.Core.Viewing`（`ViewerZoom`・`ViewOrientation`・表示サイズ・ページ・安全ポリシー・ファイル一覧）、`Mavue.Shell.ShellActions`（プログラムから開く・フォルダーに表示・プロパティ・クリップボード・Mavue の起動）。Quick View と本体で共有 |
| Quick View 常駐ホスト（PoC） | In Progress | `Mavue.QuickView.Host`: フック、STA 選択取得、panel 表示、段階表示、計測（ETW/JSONL）、プリウォーム |
| PDF エンジン | In Progress | `Mavue.Pdf`: PDFium の C API（`PdfiumDocument`: 部分読み・共有付きで開く、描画、文字・位置、検索、リンク、アウトライン、ページキャッシュ 4 枚、全呼び出しを直列化）。単体テスト `Mavue.Pdf.Tests` |
| Quick View 実機 E2E ハーネス | Implemented | `tools/Mavue.QuickView.Harness`（開発用ツール） |
| ファイル安全ポリシー（プレビュー） | Implemented | `PreviewSafetyPolicy`（クラウドプレースホルダー・デバイスパス・UNC・巨大寸法）、単体テストあり |
