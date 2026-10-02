# Mavue 機能ステータス

> `docs/SPEC.md` の全項目をここで追跡する（`CLAUDE.md` §18–19, SPEC §30）。
> 本表は SPEC から機械抽出した項目に状態を付与したもの。**SPEC の項目は削除しない**。
> 「Implemented」は中核動作が機能する場合のみ、「Tested」は SPEC §31 の Definition of Done を満たす場合のみ使用する。

最終更新: 2026-10-02（Quick View 第 3 工程: タブ追従・先読み・WIC デコード・表示サイズ）

状態: **Planned** / **Investigating** / **In Progress** / **Implemented** / **Tested** / **Blocked** / **Excluded (SPEC §29)** / **Backlog (SPEC §16)**

現時点で **Implemented / Tested の製品機能はない**。Quick View PoC で動作を確認した項目は **In Progress**（製品品質・DoD 未達）。
"Blocked" は実装ではなく「実機検証」がハードウェア不足で止まっている項目を備考に記載している（実装自体は Planned/Investigating のまま）。


## SPEC §3. Quick View / Quick Look

| ID | 機能 | 状態 | 備考 |
|---|---|---|---|
| F03.01 | Explorer file selected + Space → instant preview | In Progress | PoC: 常駐ホスト + LL フック + Shell COM（STA）+ panel 表示。画像/PDF で Space→表示→Esc を E2E・ユーザー実操作（リモート・**物理キー**）で確認。QuickLook 共存対策済み |
| F03.02 | Multiple selected files | In Progress | PoC: 複数選択は ←/→ で選択内を移動（Explorer の選択は維持）、位置表示「i / N」。E2E・物理キーで確認 |
| F03.03 | Previous/next file navigation | In Progress | PoC: Explorer の選択変更イベントに追従（ポーリングなし、イベント後 約 2 ms で切替）。E2E・物理キーで確認。Explorer のタブ切替にも追従（E2E・物理キーで確認）。前後の項目を先読み |
| F03.04 | Escape to close | In Progress | PoC: Esc（フック経由）・Space トグル・他アプリ切替で自動クローズを実操作で確認 |
| F03.05 | Fullscreen | Planned |  |
| F03.06 | Zoom | Planned |  |
| F03.07 | Rotate | Planned |  |
| F03.08 | Thumbnail/index sheet | Planned |  |
| F03.09 | File name | In Progress | PoC: ファイル名を表示 |
| F03.10 | File information | In Progress | PoC: 形式・サイズ・ピクセル寸法（PDF はページ数）を表示 |
| F03.11 | Open with another application | Planned |  |
| F03.12 | Copy | Planned |  |
| F03.13 | Share | Planned | DataTransferManagerInterop.ShowShareUIForWindow |
| F03.14 | Print | Planned |  |
| F03.15 | Markup from Quick View | Planned |  |
| F03.16 | GIF preview | Planned |  |
| F03.17 | Audio preview | Planned | PoC では音声（.wav/.ogg）は「デコーダーなし」表示（実操作で確認） |
| F03.18 | Video preview | Planned |  |
| F03.19 | HDR support where practical | Investigating | scRGB スワップチェーン。形式ごとの HDR メタデータ対応を調査 |
| F03.20 | Explorer Preview Pane integration | Planned |  |
| F03.21 | File association integration | Planned |  |
| F03.22 | Context-menu integration | Planned |  |
| F03.23 | Drag & drop | Planned |  |
| F03.24 | Clipboard integration | Planned |  |
| F03.25 | Windows Search integration where practical | Investigating | IFilter は MSIX 拡張なし。登録方式を調査（WINDOWS-INTEGRATION §0） |
| F03.26 | Thumbnail Provider | Planned |  |
| F03.27 | Preview Handler | Planned |  |

## SPEC §4. Image Formats

| ID | 機能 | 状態 | 備考 |
|---|---|---|---|
| F04.01 | JPG | In Progress | Quick View で表示確認（WIC）。編集・変換は未実装 |
| F04.02 | JPEG | In Progress | 同上（192 MP まで縮小デコードを確認） |
| F04.03 | PNG | In Progress | Quick View で表示確認（100 MP まで） |
| F04.04 | WebP | In Progress | Quick View で表示確認（OS の WebP 拡張） |
| F04.05 | GIF | Planned |  |
| F04.06 | BMP | Planned |  |
| F04.07 | TIFF | Planned |  |
| F04.08 | HEIC | In Progress | OS 拡張（HEIF+HEVC）で Quick View 表示を確認（WIC で生成した HEIC）。実カメラの HEIC は未確認。同梱フォールバックは HEVC 特許の法務確認待ち |
| F04.09 | HEIF | In Progress | 同上 |
| F04.10 | ICO | Planned |  |
| F04.11 | SVG | Planned | resvg 採用予定 |
| F04.12 | AVIF | In Progress | Quick View で表示確認（OS の HEIF + AV1 拡張） |
| F04.13 | RAW formats | Planned | WIC Raw Image Extension + LibRaw フォールバック。RAW サンプルがなく未確認 |
| F04.14 | JPEG 2000 | Planned | OpenJPEG |
| F04.15 | other practical image formats where appropriate | Planned |  |
| F04.16 | EPS | Excluded (SPEC §29) | |
| F04.17 | OpenEXR | Excluded (SPEC §29) | |

## SPEC §5. Image Viewing

| ID | 機能 | 状態 | 備考 |
|---|---|---|---|
| F05.01 | Zoom | Planned |  |
| F05.02 | Pan | Planned |  |
| F05.03 | Fit to Window | In Progress | Quick View のみ: ウィンドウに収める（拡大はしない）。物理ピクセルでデコードし 1:1 表示（E2E で画面上の寸法を確認）。メインビューアは未実装 |
| F05.04 | Actual Size | In Progress | Quick View のみ: 設定ファイルの `imageScale` を `ActualSize` にすると原寸（5,000 万画素まで、はみ出す場合はスクロール）。設定は Quick View を開くたびに読み直す。ウィンドウ内の切替ボタンはユーザー要望で廃止し、設定画面（F23.03）で切り替える予定。メインビューアは未実装 |
| F05.05 | Rotate | Planned |  |
| F05.06 | Flip horizontal | Planned |  |
| F05.07 | Flip vertical | Planned |  |
| F05.08 | Fullscreen | Planned |  |
| F05.09 | Slideshow | Planned |  |
| F05.10 | Image information | Planned |  |
| F05.11 | Pixel-level inspection where useful | Planned |  |
| F05.12 | Transparency checkerboard | Planned |  |
| F05.13 | Drag & drop | Planned |  |
| F05.14 | Copy/paste | Planned |  |

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
| F08.01 | File size | Planned |  |
| F08.02 | Resolution | Planned |  |
| F08.03 | DPI | Planned |  |
| F08.04 | Creation time | Planned |  |
| F08.05 | Modification time | Planned |  |
| F08.06 | Author | Planned |  |
| F08.07 | Keywords | Planned |  |
| F08.08 | GPS | Planned | GPS 地図表示も実装対象（SPEC §8）。地図タイルはオフライン/外部送信の扱いを要設計（Investigating） |
| F08.09 | EXIF | Planned |  |
| F08.10 | EXIF removal | Planned |  |
| F08.11 | ICC profile metadata handling where practical | Planned | ColorSync/Soft Proof は除外 |

## SPEC §9. PDF Viewer

| ID | 機能 | 状態 | 備考 |
|---|---|---|---|
| F09.01 | Fast PDF rendering | Planned |  |
| F09.02 | Page navigation | Planned |  |
| F09.03 | Thumbnail sidebar | Planned |  |
| F09.04 | Table of contents | Planned |  |
| F09.05 | Page numbers | Planned |  |
| F09.06 | Jump to page | Planned |  |
| F09.07 | Continuous scroll | Planned |  |
| F09.08 | Single-page view | Planned |  |
| F09.09 | Two-page spread | Planned |  |
| F09.10 | Zoom | Planned |  |
| F09.11 | Fit Width | Planned |  |
| F09.12 | Fit Page | Planned |  |
| F09.13 | Actual Size | In Progress | Quick View の 1 ページ目のみ: 設定 `imageScale=ActualSize` で 100 %（96 dpi × ウィンドウのあるモニターの倍率）描画（150 %・100 % のモニターで E2E 確認）。PDF ビューアは未実装 |
| F09.14 | Ctrl+F search | Planned |  |
| F09.15 | Search result list | Planned |  |
| F09.16 | Text selection | Planned |  |
| F09.17 | Copy | Planned |  |
| F09.18 | Internal PDF links | Planned |  |
| F09.19 | External URLs | Planned |  |
| F09.20 | Bookmarks | Planned |  |
| F09.21 | Print | Planned |  |
| F09.22 | Presentation/slideshow where practical | Planned |  |
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
| F15.01 | Password protection | Planned | QPDF（AES-256） |
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
| F19.01 | Audio preview | Planned |  |
| F19.02 | Video preview | Planned |  |
| F19.03 | Playback controls | Planned |  |
| F19.04 | Seek | Planned |  |
| F19.05 | Volume | Planned |  |
| F19.06 | Fullscreen | Planned |  |
| F19.07 | Supported Windows/media formats where practical | Planned | Media Foundation。FFmpeg は法務確認待ち |
| F19.08 | Video trimming | Excluded (SPEC §29) | |

## SPEC §20. GIF

| ID | 機能 | 状態 | 備考 |
|---|---|---|---|
| F20.01 | Animated GIF preview | Planned |  |
| F20.02 | Playback | Planned |  |
| F20.03 | Pause | Planned |  |
| F20.04 | Frame navigation where practical | Planned |  |
| F20.05 | Fullscreen | Planned |  |
| F20.06 | Zoom | Planned |  |

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
| F21.10 | File properties/info | Planned |  |
| F21.11 | Recent files | Planned |  |
| F21.12 | Drag & drop | Planned |  |
| F21.13 | Clipboard | Planned |  |
| F21.14 | Version/history/backup where practical | Planned |  |
| F21.15 | Tabs | Planned |  |
| F21.16 | Multiple windows | Planned |  |

## SPEC §22. Windows Explorer Integration

| ID | 機能 | 状態 | 備考 |
|---|---|---|---|
| F22.01 | Space-key Quick View | In Progress | F03.01 と同一（PoC 実装済み） |
| F22.02 | Right-click “Open in Mavue” | Planned | Win11 上段はパッケージ ID 必須（MSIX） |
| F22.03 | Right-click “Mavue Quick View” | Planned | 同上 |
| F22.04 | File associations | Planned |  |
| F22.05 | PDF association | Planned |  |
| F22.06 | Image associations | Planned |  |
| F22.07 | Thumbnail Provider | Planned |  |
| F22.08 | Preview Pane | Planned | 既存ハンドラー（本機 .pdf は Edge）を無断で上書きしない |
| F22.09 | Preview Handler | Planned |  |
| F22.10 | Shell extension | Planned |  |
| F22.11 | Windows Search integration | Investigating | IFilter 登録方式を調査 |
| F22.12 | Drag & drop | Planned |  |
| F22.13 | Clipboard | Planned |  |
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
| F23.01 | Dark mode | Planned |  |
| F23.02 | Light mode | Planned |  |
| F23.03 | Settings | Planned | 最初の項目: Quick View の表示サイズ（拡大しない／原寸）。ユーザー要望で、その場の切替ではなく設定画面で切り替える。それまでは `%LOCALAPPDATA%\Mavue\QuickView\settings.json` を直接編集（開くたびに読み直し） |
| F23.04 | Keyboard shortcuts | Planned |  |
| F23.05 | High-DPI support | In Progress | Quick View のみ: ウィンドウのあるモニターの DPI で物理ピクセルに合わせてデコードし 1:1 表示（150 % と 100 % のモニターで E2E 確認）。メインアプリは未実装 |
| F23.06 | Multi-monitor support | In Progress | Quick View のみ: Explorer のあるモニターに表示。拡大率の異なるモニターへの移動・表示中のリサイズで画像を作り直す（3 台構成で E2E 確認）。メインアプリは未実装 |
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
| X.02 | 多言語 PDF テキスト・メタデータ | Planned | |
| X.03 | ローカル完結・テレメトリなし | In Progress | 最小構成に外部通信なし |
| X.04 | 機微データをログに出さない | Planned | |
| X.05 | モジュール構成（SPEC §28） | In Progress | 最小構成でプロジェクト分割済み |
| X.06 | 暗号学的デジタル署名（SPEC §12: 後段階・除外ではない） | Planned | CNG + PAdES |
| X.07 | フォーム AutoFill 用プロフィールの暗号化保存 | Planned | DPAPI |

## 最小構成で実装済みの基盤（製品機能ではない）

| 基盤 | 状態 | 備考 |
|---|---|---|
| ソリューション / モジュール分割 | Implemented | `Mavue.slnx` |
| ファイル形式判定（マジックバイト） | Implemented | `Mavue.Core.Formats.FileFormatDetector`、単体テストあり |
| 安全保存（一時ファイル + アトミック置換） | Implemented | `Mavue.Core.IO.SafeFileWriter`、単体テストあり。シンボリックリンク経由の保存は実体を更新するよう修正（本機では symlink を作成できずテストはスキップ＝未検証） |
| WinUI 3 アプリ起動 | Implemented | `Mavue.App`（空のメインウィンドウ、ja/en リソース） |
| Quick View 常駐ホスト（PoC） | In Progress | `Mavue.QuickView.Host`: フック、STA 選択取得、panel 表示、段階表示、計測（ETW/JSONL）、プリウォーム |
| Quick View 実機 E2E ハーネス | Implemented | `tools/Mavue.QuickView.Harness`（開発用ツール） |
| ファイル安全ポリシー（プレビュー） | Implemented | `PreviewSafetyPolicy`（クラウドプレースホルダー・デバイスパス・UNC・巨大寸法）、単体テストあり |
