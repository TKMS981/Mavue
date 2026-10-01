# Mavue 依存関係・ライセンス記録

> `CLAUDE.md` §12 に基づく記録。主要ライブラリの採用前に、ライセンス・推移的依存・再配布条件・
> 商用利用制限・ネイティブバイナリ再配布要件を確認し、ここに記録する。
> **本書は法的助言ではない。** 「要法務確認」の項目は製品配布前に専門家の確認を必須とする。

最終更新: 2026-10-02（Quick View PoC 時点。製品コードに追加した外部依存はなし。PDFium の配布物を実物で確認）

状態の凡例: **Adopted**（採用・プロジェクトに追加済み）/ **Selected**（採用決定・未追加）/ **Candidate**（評価中）/ **Rejected**（不採用）/ **Legal review**（法務確認待ち）

---

## 1. ツールチェーン

| 項目 | バージョン | 入手 | 状態 | 備考 |
|---|---|---|---|---|
| .NET SDK | 10.0.401（.NET 10 LTS, サポート終了 2028-11-14） | dotnet-install / winget | Adopted | `global.json` でピン留め（`rollForward: latestFeature`） |
| Windows App SDK | 2.5.1（2026-09-16 安定版） | NuGet `Microsoft.WindowsAppSDK` | Adopted | MS ソフトウェアライセンス。ランタイム再配布可（フレームワークパッケージ / 自己完結） |
| Windows SDK BuildTools | 10.0.28000.2705 | NuGet `Microsoft.Windows.SDK.BuildTools` | Adopted | makepri / MSIX ツール。VS 不要でビルド可能 |
| Windows SDK 投影 (C#) | TFM `net10.0-windows10.0.26100.0` | .NET SDK が自動取得 | Adopted | |
| Visual Studio 2026 / Build Tools（C++ ワークロード, MSVC v14.5x, Windows 11 SDK 10.0.26100 以降） | — | VS Installer | **未インストール（本機）** | C++ シェル拡張のビルドに必須。BUILD.md 参照 |
| CMake / Ninja | — | VS 同梱 | 未インストール | ネイティブ依存（PDFium 以外）のビルドに使用予定 |
| vcpkg（マニフェストモード） | — | VS 同梱 / git | Candidate | ネイティブ依存のバージョン固定。ベースラインをコミット |
| C++/WinRT | 3.0.260818.1 | NuGet | Candidate | ネイティブ側で WinRT を使う場合 |

## 2. .NET パッケージ

| パッケージ | バージョン | ライセンス | 用途 | 状態 | 再配布・注意 |
|---|---|---|---|---|---|
| Microsoft.WindowsAppSDK | 2.5.1 | MS-EULA（再配布可） | WinUI 3 / AppLifecycle / MRT Core | Adopted | 推移的に WinUI, Foundation, AI, ML, Search 等を含む |
| Microsoft.Windows.SDK.BuildTools | 10.0.28000.2705 | MS-EULA | ビルドツール | Adopted | 開発時のみ |
| Microsoft.Graphics.Win2D | 1.4.0 | MIT | GPU 2D 描画・エフェクト | Selected | ネイティブ DLL 同梱 |
| CommunityToolkit.Mvvm | 8.4.2 | MIT | MVVM（ソース生成） | Selected | |
| xunit.v3 | 4.0.1 | Apache-2.0 | テスト | Adopted | 開発時のみ |
| xunit.runner.visualstudio | 4.0.0 | Apache-2.0 | テストランナー | Adopted | 開発時のみ |
| Microsoft.NET.Test.Sdk | 18.10.1 | MIT | テスト | Adopted | 開発時のみ |
| BenchmarkDotNet | （追加時に記録） | MIT | 性能テスト | Candidate | 開発時のみ |
| MetadataExtractor | 2.9.3 | Apache-2.0 | メタデータ読み取り補助 | Candidate | WIC で足りない形式用 |
| Microsoft.ML.OnnxRuntime | 1.30.0 | MIT | 背景除去 / OCR モデル推論 | Candidate | Windows ML (WinAppSDK) との比較後に決定 |
| PdfSharp | 6.2.4 | MIT | — | Rejected（現時点） | PDFium + QPDF で機能が揃うため不要。暗号化の代替として保持 |
| Magick.NET | 14.17.2 | Apache-2.0（ImageMagick）+ 同梱デリゲート各種 | — | Rejected | 巨大。libheif/libde265 (LGPL・HEVC 特許) を同梱。必要なコーデックを個別採用する方針 |
| SkiaSharp | 4.153.1 | MIT | — | Rejected（現時点） | Win2D/D2D と重複 |

## 3. PDF エンジン候補

| 候補 | ライセンス | 描画 | テキスト抽出/検索 | フォーム | 注釈編集 | ページ操作 | 暗号化書込 | 線形化 | 判定 |
|---|---|---|---|---|---|---|---|---|---|
| **PDFium**（bblanchon/pdfium-binaries 156.0.8076、NuGet `bblanchon.PDFium.Win32`） | BSD-3-Clause（本体）。同梱: FreeType (FTL, BSD 系として選択), libjpeg-turbo (IJG/BSD-3/zlib), OpenJPEG (BSD-2), LittleCMS (MIT), zlib, libpng, AGG 2.3 (改変 BSD 系), Abseil (Apache-2.0), ICU (Unicode-3.0)。ビルドスクリプトは MIT | ◎ | ◎ | ◎（AcroForm。XFA はビルド依存） | ○（FPDFAnnot API） | ◎（FPDF_ImportPages, Move, Delete） | ✕ | ✕ | **Selected** |
| **QPDF** v12.4.2 | Apache-2.0。依存: zlib, libjpeg(-turbo)。暗号は内蔵 "native" プロバイダで OpenSSL 不要 | ✕ | ✕ | — | — | ◎ | ◎（AES-256 R6, 権限） | ◎ | **Selected**（PDFium の補完） |
| MuPDF | **AGPL-3.0** または Artifex 商用ライセンス | ◎ | ◎ | ○ | ◎ | ◎ | ◎ | — | **Rejected**: AGPL はクローズド配布と両立しない。商用ライセンス購入時のみ再検討 |
| iText 9 | **AGPL-3.0** / 商用 | ✕ | ○ | ◎ | ◎ | ◎ | ◎ | — | Rejected（同上） |
| Windows.Data.Pdf (WinRT) | OS 内蔵 | ○ | ✕ | ✕ | ✕ | ✕ | ✕ | ✕ | Selected（描画専用フォールバック） |
| pdf.js (WebView2) | Apache-2.0 | ○ | ○ | ○ | △ | ✕ | ✕ | ✕ | Rejected: ネイティブ方針に反する、Quick View 起動が重い |
| Apryse / PSPDFKit(Nutrient) / Syncfusion / Aspose | 商用 | ◎ | ◎ | ◎ | ◎ | ◎ | ◎ | ◎ | 予算判断事項として記録のみ |

### 3.1 PDFium 配布物の確認結果（2026-10-02、実物をダウンロードして確認。プロジェクトには未追加）

| 項目 | 確認結果 |
|---|---|
| 最新リリース | bblanchon/pdfium-binaries `chromium/8076`（2026-09-29 公開）、PDFium 156.0.8076 |
| ビルド設定（`args.gn`） | `pdf_enable_v8 = false`, `pdf_enable_xfa = false`, `is_component_build = false`, `pdf_use_partition_alloc = false`（V8/XFA なし = 攻撃面が小さい） |
| 提供形態 | NuGet `bblanchon.PDFium.Win32`（win-x64 / win-arm64 / win-x86 の `pdfium.dll` とヘッダー）、または GitHub Releases の `pdfium-win-x64.tgz` 等 |
| **NuGet パッケージのライセンス表記** | nuspec は `Apache-2.0`。一方 GitHub リポジトリと .tgz 同梱の `LICENSE` はビルドスクリプトについて **MIT**（© Benoit Blanchon）。**表記が食い違っている**。PDFium 本体の LICENSE は BSD-3-Clause で、Apache-2.0 の全文を含む |
| **NuGet にライセンス文が同梱されていない** | NuGet パッケージには PDFium 本体・サードパーティのライセンス/NOTICE ファイルが**一切含まれていない**（42 ファイルを確認）。.tgz の `licenses/` にのみ含まれる |
| .tgz 同梱のライセンス（14 件） | pdfium（BSD-3-Clause + Apache-2.0 全文）、abseil（Apache-2.0）、agg23（AGG 2.3 の許諾文）、fast_float（MIT）、freetype（FTL。GPLv2 との二重ライセンスから FTL を選択）、icu（Unicode License V3）、lcms（MIT）、libjpeg_turbo（IJG + BSD-3 + zlib）、libopenjpeg（BSD-2）、libpng（PNG Reference Library License v2）、llvm-libc（Apache-2.0 with LLVM Exceptions）、simdutf（MIT/Apache）、zlib（zlib） |
| コピーレフト | なし（GPL/LGPL/AGPL の成分なし。FreeType は FTL を選択） |
| 商用利用 | 可（いずれも許容型ライセンス）。FTL は文書へのクレジット表記が必要 |
| 特許 | Apache-2.0 成分は特許許諾を含む。JPEG 2000（OpenJPEG）に関する特許の主張は把握している範囲でなし（要法務確認事項としては低） |

**Mavue での扱い（決定）**:
- バイナリの入手は NuGet でもよいが、**ライセンス文は同じバージョンの .tgz の `LICENSE` と `licenses/*` から取得**し、`THIRD-PARTY-NOTICES.txt` に全文を同梱する（ビルド時にバージョン一致を検証するスクリプトを用意する）。
- Windows での配布: `pdfium.dll` をアプリと同じフォルダ（MSIX 内）に配置。システムフォルダには入れない。
- 更新方法: chromium の PDFium は頻繁にセキュリティ修正が入るため、**最低でも月 1 回**リリースを確認し、`Directory.Packages.props` のバージョンを更新して PDF 回帰テスト（`docs/TESTING.md`）を通してから取り込む。ビルド番号・コミット（nuspec の `repository commit`）を記録する。
- 自前ビルドへの切り替え基準: 修正の取り込みが遅い、または独自パッチ（墨消し用の API 拡張など）が必要になった場合。その場合は Build Tools と depot_tools が必要。

**PDFium 採用時の義務**: 各コンポーネントの著作権表示・ライセンス文を `THIRD-PARTY-NOTICES.txt` に同梱。
FreeType は FTL を選択（クレジット表記義務あり）。Chromium の PDFium はバイナリにパッチ履歴がないため、
bblanchon ビルドの再現性（タグ・ハッシュ）を記録する。

**PDF 機能ギャップと対処**:
- 暗号化・権限設定・線形化 → QPDF。
- 真の墨消し → 独自実装（ARCHITECTURE.md §7）。PDFium にも QPDF にも墨消し API はない。**最大の実装リスク**。
- PDF 圧縮（画像再圧縮）→ PDFium で画像 XObject 取得 → WIC/libjpeg で再エンコード → QPDF でオブジェクトストリーム化。
- デジタル署名（将来）→ CMS 生成は Windows CNG / `System.Security.Cryptography.Pkcs`、PDF への埋め込みは独自（PAdES）。

## 4. 画像処理・コーデック候補

本機で確認済みの OS 拡張（2026-10-02）: HEIF Image Extension 1.2.48, HEVC Video Extension(s) 2.5.33/2.4.110, AV1 Video Extension 2.0.35,
WebP Image Extension 1.2.31, Raw Image Extension 2.5.35, VP9 1.2.20, MPEG-2 1.2.32, Web Media Extensions 2.1.51, Microsoft JPEG XL Decoder（WIC 登録あり）。

| 形式 | 第一選択（OS） | フォールバック候補 | ライセンス | 判定 / 注意 |
|---|---|---|---|---|
| JPEG / PNG / GIF / BMP / TIFF / ICO / JPEG XR | WIC 内蔵 | — | OS | Selected |
| WebP（静止/アニメ） | WIC（WebP 拡張） | **libwebp** | BSD-3-Clause + 特許付与 | Selected（フォールバック） |
| HEIC / HEIF | WIC（HEIF 拡張 + HEVC 拡張） | libheif + libde265 | libheif: LGPL-3.0, libde265: LGPL-3.0 | **Legal review**: LGPL は動的リンク + 再リンク可能性 + ソース提供で対応可能だが、**HEVC には特許プール (Access Advance / Via LA) があり同梱デコーダーはロイヤリティ問題になりうる**。法務確認まで同梱しない。機能は OS 拡張経由で実装し、拡張未導入時は Store 導線を表示 |
| AVIF | WIC（HEIF + AV1 拡張） | **libavif + dav1d** | BSD-2-Clause / BSD-2-Clause。AV1 は AOMedia 特許ライセンス（ロイヤリティフリー） | Selected（フォールバック） |
| JPEG 2000 | （OS 非対応） | **OpenJPEG** | BSD-2-Clause | Selected（PDFium にも内包） |
| RAW（CR2/CR3/NEF/ARW/DNG/RAF/ORF/RW2 等） | WIC（Raw Image Extension） | **LibRaw** | LGPL-2.1 **または** CDDL-1.0 の選択制 | Selected（CDDL を選択予定、動的リンク。改変時はソース公開義務あり） |
| SVG | — | **resvg** | Apache-2.0 / MIT デュアル | Selected（Rust ツールチェーン要、C API 経由）。Direct2D SVG はテキスト・フィルタ非対応のため補助のみ |
| JPEG XL | WIC（JPEG XL 拡張） | libjxl | BSD-3-Clause + 特許付与 | Candidate（SPEC「その他の実用的形式」） |
| DDS / TGA / PSD（合成画像） | WIC（DDS）/ — | 調査 | — | Candidate |
| EPS / OpenEXR | — | — | — | **SPEC により除外** |

画像調整は Direct2D 組み込みエフェクトで実装し外部依存なし。

### 背景除去モデル（ライセンストラップに注意）

| モデル | ライセンス | 判定 |
|---|---|---|
| Windows AI `ImageObjectExtractor` | OS（Copilot+ PC のみ。本機は NPU なしで利用不可） | Selected（利用可能環境で優先） |
| BRIA RMBG-1.4 / 2.0 | **非商用ライセンス** | **Rejected** |
| U²-Net | Apache-2.0 | Candidate |
| IS-Net (DIS) | Apache-2.0 | Candidate |
| BiRefNet | MIT | Candidate（品質良、モデルサイズ大） |

## 5. OCR エンジン候補

本機の Windows.Media.Ocr 利用可能言語（実測）: `en-US`, `ja`。`MaxImageDimension` = 10000。NPU なし。

| 候補 | ライセンス | 日本語 | 縦書き | 判定 |
|---|---|---|---|---|
| **Windows.Media.Ocr** | OS 内蔵（言語パック依存） | ○ | △（要検証） | **Selected（既定）**。オフライン、追加配布不要。10000px 超はタイル分割 |
| Windows AI `TextRecognizer`（WinAppSDK AI） | OS（Copilot+ PC / NPU 必須、パッケージ ID 要） | ◎ | 要検証 | Selected（対応環境で優先） |
| **Tesseract 5** + Leptonica | Apache-2.0 / BSD-2 相当。学習データ `jpn`, `jpn_vert`, `eng` (tessdata_best: Apache-2.0) | ○ | ○（jpn_vert） | Selected（言語・縦書きの補完、オプションダウンロード） |
| PaddleOCR（ONNX 変換） | Apache-2.0 | ◎ | ○ | Candidate（精度比較ベンチマーク後に判断） |

## 6. メディア

| 候補 | ライセンス | 判定 |
|---|---|---|
| Media Foundation / `MediaPlayerElement` | OS | Selected。対応形式は導入済み拡張（HEVC/AV1/VP9/MPEG-2/WebM）に依存 |
| FFmpeg (LGPL ビルド) | LGPL-2.1+（GPL オプション無効化必須）。H.264/HEVC/AAC 等に特許問題 | **Legal review**。採用する場合も特許負担のないコーデックに限定する案を検討 |

## 7. スキャン・印刷・入力

| 候補 | ライセンス | 判定 / 注意 |
|---|---|---|
| Windows.Devices.Scanners（WinRT） | OS | Selected |
| WIA 2.0 COM | OS | Selected（ADF/両面/詳細設定） |
| TWAIN DSM（twain/twain-dsm） | **LGPL-2.0**（配布ページ記載。リポジトリにライセンス API 情報なし → 要再確認） | Selected（動的ロード。DSM 自体はユーザー/ドライバーが導入するものを使用し、Mavue からは同梱しない方針を第一案） |
| NTwain / NAPS2 | MIT / **GPL-2.0** | NAPS2 は参考のみ（コード流用不可）。NTwain は Candidate |
| Direct2D 印刷 / XPS Print Document Package API | OS | Selected |
| WinUI 3 `Microsoft.UI.Xaml.Printing` + `PrintManagerInterop` | WinAppSDK | Selected（標準印刷 UI 用） |
| WinUI 3 InkCanvas | WinAppSDK 2.4+ **Experimental** | 安定版化後に再評価 |

## 7.1 Quick View PoC で使用した OS 機能（追加ライセンスなし）

| 機能 | 用途 | 備考 |
|---|---|---|
| Windows.Data.Pdf（WinRT） | PoC の PDF 1 ページ目表示 | OS 内蔵。描画のみ。製品の PDF エンジンは PDFium（§3） |
| Windows.Graphics.Imaging（WIC） | 画像の縮小デコード | HEIF/AVIF/WebP/RAW は OS 拡張に依存（本機は導入済み） |
| Shell COM（IShellWindows ほか）、IShellItemImageFactory | 選択取得・サムネイル | OS 内蔵 |

## 7.2 開発時のみ使用するツール（配布物に含めない）

| ツール | ライセンス | 用途 |
|---|---|---|
| Pillow 12.2.0 + NumPy 2.5.1（Python） | MIT-CMU（HPND 系）/ BSD-3-Clause | `tools/assets/generate_quickview_assets.py` で WebP/AVIF のテスト画像を生成（WIC にエンコーダーがないため）。生成物は自作の合成画像 |

## 8. 参考にするが流用しないもの

| プロジェクト | ライセンス | 扱い |
|---|---|---|
| QuickLook (QL-Win) | **GPL-3.0** | 方式（フック + Shell COM）の公開情報のみ参照。コード流用禁止。本機には MSIX 版 4.5.0 が導入済み（参照・解析はしていない） |
| PowerToys (Peek, File Explorer add-ons) | MIT | 方式参照。流用する場合は MIT 表示を同梱 |

## 9. 配布物に同梱する通知

- `THIRD-PARTY-NOTICES.txt`（全ネイティブ/マネージド依存のライセンス全文）を MSIX に同梱し、アプリの「バージョン情報」から閲覧可能にする。
- LGPL コンポーネント（採用時）: 動的リンク、DLL 差し替えを妨げない（MSIX でもユーザーが差し替え可能な手段の提供方法を法務確認）、ソース入手方法を記載。

## 10. 未解決事項

1. HEVC（HEIC フォールバック）の特許 — 法務確認。
2. LGPL コンポーネントを MSIX（改ざん防止された配布形式）で配布する場合の「再リンク可能性」要件の解釈 — 法務確認。
3. TWAIN DSM のライセンス表記の一次情報確認。
4. PDFium の XFA 有効ビルドの要否（XFA は V8 依存でサイズ・攻撃面が増大）。現状の方針は XFA/V8 なしのビルド（2026-10-02 確認の公式ビルドと同じ）。
5. bblanchon.PDFium の NuGet 表記（Apache-2.0）とリポジトリ LICENSE（MIT）の食い違い — 実害は小さい（どちらも許容型）が、通知文はリポジトリ/tgz の表記に合わせる。
