# Mavue 依存関係・ライセンス記録

> `CLAUDE.md` §12 に基づく記録。主要ライブラリの採用前に、ライセンス・推移的依存・再配布条件・
> 商用利用制限・ネイティブバイナリ再配布要件を確認し、ここに記録する。
> **本書は法的助言ではない。** 「要法務確認」の項目は製品配布前に専門家の確認を必須とする。

最終更新: 2026-10-04（配布準備: Windows SDK の C# 投影を REDIST 一覧で確認し再配布可に、.NET ランタイムを self-contained で同梱（MIT）、配布物の `licenses\` を整備、Build Tools の使用条件と EULA を未解決として明記。以前: 2026-10-03 Explorer のプレビュー・サムネイル用ネイティブ DLL: 外部依存の追加なし（Windows SDK の WIC/Direct2D/DirectWrite/Media Foundation と既存の pdfium.dll のみ）。**PDFium を採用・追加**（§3.1、§3.2）。それ以前: 2026-10-02 Quick View ホストの Windows App SDK 参照を必要な部品に限定し、出力を 155 → 95 MB に。Mavue 本体を Apache-2.0 とし、ライセンス関連ファイルを追加。Quick View 第 3 工程時点。製品コードに追加した外部依存はなし — WIC 直接デコード・設定ファイル（System.Text.Json）は OS / .NET 標準機能のみ。PDFium の配布物を実物で確認）

## 0. Mavue 本体のライセンス（2026-10-02 決定）

- Mavue 自作のソースコードは **Apache License 2.0**（リポジトリ直下の `LICENSE`、`NOTICE`）。
- 第三者コンポーネントは各自のライセンスのまま扱い、Apache-2.0 で上書きしない。一覧は `THIRD-PARTY-NOTICES.md`。
- 互換性の注意: Apache-2.0 のコードに **GPL-2.0 のみ**のコードは組み込めない。GPL-3.0 / AGPL-3.0 のコードを取り込むと
  配布物全体がその条件に縛られる。LGPL は動的リンクなら併用可（§9）。これらのコードは今後も流用しない。
- Windows App SDK の再配布条件（各パッケージの `license.txt` §3。WinUI・Foundation・InteractiveExperiences・Base・
  メタパッケージの `license.txt` は同一ファイル（SHA-256 一致）であることを確認）:
  - §3(a)(i): NuGet パッケージが出力に置くファイルは再配布可。フレームワーク依存・自己完結の両方に適用される。
  - §3(b): 再配布するときは (i) Mavue 側で主要な機能を加えること、(ii) 配布者とエンドユーザーに、このコードと Microsoft を
    本契約と同等以上に保護する条項へ同意させること、(iii) 配布に関する請求について Microsoft を補償すること。
  - §3(c): Microsoft の商標を、Microsoft が提供・推奨していると誤認させる形で使わないこと。配布可能コードを、ソース開示や
    改変許可を求めるライセンス（コピーレフト）の対象にしないこと。Apache-2.0 はソース開示を求めないため抵触しない。
  - **バイナリ配布の開始前に、利用条件（EULA 相当）の文面を用意する**（ソースのみ公開の現状では対象外）。
- WebView2（`LICENSE.txt`、BSD-3-Clause 型）: バイナリ再配布時は著作権表示・条件・免責をドキュメント等に再掲する。
- Windows SDK の C# 投影 `Microsoft.Windows.SDK.NET.dll` と `WinRT.Runtime.dll`（.NET SDK が `net10.0-windows` 向けに自動で
  追加。パッケージ `Microsoft.Windows.SDK.NET.Ref` 10.0.26100.57）— **2026-10-04 確認: Windows SDK の REDIST 一覧に記載あり、再配布可**:
  - パッケージの nuspec の `licenseUrl` は Windows SDK のライセンス（`https://aka.ms/WinSDKLicenseURL`）。`WinRT.Runtime.dll` も
    このパッケージから出力に入るため、C#/WinRT のソース（MIT）ではなく**このパッケージの Windows SDK 条件**で扱う。
  - Windows SDK ライセンス（本機の `C:\Program Files (x86)\Windows Kits\10\Licenses\10.0.26100.0\sdk_license.rtf` で原文を確認）の
    Distributable Code は「REDIST.TXT 記載のファイルと、REDIST.TXT 一覧（http://go.microsoft.com/fwlink/?LinkId=524842）のファイル」。
  - REDIST 一覧（https://learn.microsoft.com/en-us/legal/windows-sdk/redist 、2024-10-21 更新）の「Microsoft.Windows.SDK.NET.Ref」節に
    `./lib/net8.0/Microsoft.Windows.SDK.NET.dll`、`Microsoft.Windows.UI.Xaml.dll`、`WinRT.Runtime.dll`（net6.0 版、winmd 等も）が
    「改変せずに NuGet パッケージとして、またはアプリが WinRT API を呼ぶためにプログラムの一部として」配布可能と記載。
  - Mavue の配布物の 2 ファイルはパッケージの `lib/net8.0` と **SHA-256 が一致**（`tools/build-release.ps1` の出力で確認。無改変）。
    `Microsoft.Windows.UI.Xaml.dll` は出力に含まれない。
  - 条件（sdk_license の Distribution Requirements）: 主要な機能を加える、.lib はリンク結果のみ、配布者とエンドユーザーに本契約と同等以上に
    保護する条項へ同意させる、自分の著作権表示を表示する、Microsoft を補償する。禁止: 著作権等の表示の改変、Microsoft 商標の誤認的使用、
    Microsoft OS 以外での実行、悪意あるプログラムへの組み込み、Excluded License（コピーレフト）の対象にすること。
    → Windows App SDK §3(b) と同種。**エンドユーザー向け利用条件（EULA 相当）の用意が両方の条件**（§10 の 7）。
- .NET ランタイム（self-contained で同梱。`runtimepack.Microsoft.NETCore.App.Runtime.win-x64` 10.0.12）: **MIT**（パッケージの
  `LICENSE.TXT`、第三者通知 `THIRD-PARTY-NOTICES.TXT`）。両ファイルを配布物の `licenses\dotnet\` に同梱する。WindowsDesktop
  ランタイム（WPF/WinForms）は含まれない（`Mavue.runtimeconfig.json` の includedFrameworks は Microsoft.NETCore.App のみ。確認済み）。
- Microsoft C/C++ ランタイム（Mavue.Shell.Native/Preview に静的リンク。ビルドは **Visual Studio Build Tools 2026**）: VC++ 再頒布
  パッケージの DLL は配布しない（リンク結果のみ）。ただし **Build Tools の使用条件**は「Visual Studio のライセンスを持つユーザーの補助」
  または「OSI 承認ライセンスのオープンソース依存部品のビルド」（Build Tools のライセンス条項。Mavue 自身のコードのビルドは前者に当たる）。
  Visual Studio の有効なライセンス（例: 個人開発者なら Community の条件）を満たすかは**ユーザーが確認する事項**（未確認）。

### 0.1 Quick View ホストの依存の整理（2026-10-02、実測）

| 項目 | 変更前（メタパッケージ） | 変更後（必要な部品のみ） |
|---|---|---|
| 参照 | `Microsoft.WindowsAppSDK` 2.5.1 | `Microsoft.WindowsAppSDK.WinUI` 2.3.9、`.Foundation` 2.3.12、`.InteractiveExperiences` 2.1.9（`.Base` 2.0.4 は自動） |
| Release 出力 | 254 ファイル・155 MB | 187 ファイル・95 MB |
| 出力から消えたもの | — | Windows ML（`onnxruntime.dll` 20.7 MB、`DirectML.dll` 17.8 MB、`Microsoft.ML.OnnxRuntime.dll`、`Microsoft.Windows.AI.MachineLearning*.dll`）、Windows AI（`Microsoft.Windows.AI.*`、`Microsoft.Windows.Workloads*`、`NPUDetect.dll`、`PerceptiveStreaming.dll` など）、Search、Widgets、DWriteCore、`System.Numerics.Tensors.dll` など 67 ファイル |

- 判断の根拠: 7 形式（JPEG×2・PNG・WebP・AVIF・HEIF・PDF）を実際に表示した後の常駐プロセスが読み込んでいたアプリフォルダーの DLL は、
  WinUI・Foundation（`Microsoft.WindowsAppRuntime.dll`、MRT Core）・InteractiveExperiences（Windowing・Input・Composition）と
  Mavue 自身だけだった。Windows ML・DirectML・ONNX Runtime・WebView2・AI・Search・Widgets・DWriteCore は一度も読み込まれていない
  （文字描画は OS の `DWrite.dll`）。コードも `Microsoft.UI.*` と `Microsoft.Windows.ApplicationModel.Resources` 以外の Windows App SDK API を使っていない。
- 部品パッケージの単独参照は Microsoft Learn「Use the Windows App SDK in an existing project」で認められている
  （「通常はメインのパッケージを推奨するが、特定の部品だけを参照するためにサブパッケージを個別に導入できる場合がある」）。
- WinUI 2.3.9 は InteractiveExperiences を「2.1.8 以上」で要求するが 2.1.8 は公開されていない（NU1603）。メタパッケージと同じ 2.1.9 を明示した。
- WebView2 は WinUI パッケージの依存なので出力に残る（読み込まれない）。除外すると WinUI の型解決に影響しうるため残した。
- `Mavue.App` と `Mavue.Viewer` も 2026-10-02 から同じ部品パッケージだけを参照する（閲覧基盤の実装時。クリーンな出力に ML・AI・Search・Widgets・DWriteCore が含まれないことを確認、WebView2 は WinUI の依存として残る）。
  OCR・背景除去などで Windows AI / Windows ML が必要になった時点で、その部品だけを追加する。

状態の凡例: **Adopted**（採用・プロジェクトに追加済み）/ **Selected**（採用決定・未追加）/ **Candidate**（評価中）/ **Rejected**（不採用）/ **Legal review**（法務確認待ち）

---

## 1. ツールチェーン

| 項目 | バージョン | 入手 | 状態 | 備考 |
|---|---|---|---|---|
| .NET SDK | 10.0.401（.NET 10 LTS, サポート終了 2028-11-14） | dotnet-install / winget | Adopted | `global.json` でピン留め（`rollForward: latestFeature`） |
| Windows App SDK | 2.5.1（2026-09-16 安定版） | NuGet: 部品パッケージ（WinUI 2.3.9 / Foundation 2.3.12 / InteractiveExperiences 2.1.9）。Quick View ホスト・`Mavue.App`・`Mavue.Viewer` | Adopted | MS ソフトウェアライセンス。出力に置かれるファイルは再配布可（フレームワーク依存 / 自己完結、`license.txt` §3。条件は §0） |
| Windows SDK BuildTools | 10.0.28000.2705 | NuGet `Microsoft.Windows.SDK.BuildTools` | Adopted | makepri / MSIX ツール。VS 不要でビルド可能 |
| Windows SDK 投影 (C#) | TFM `net10.0-windows10.0.26100.0` | .NET SDK が自動取得 | Adopted | |
| Visual Studio 2026 / Build Tools（C++ ワークロード, MSVC v14.5x, Windows 11 SDK 10.0.26100 以降） | 18.10.2（MSVC 19.51） | winget（管理者） | **Adopted**（2026-10-02 導入） | `native/Mavue.Shell.Native` のビルドに使用。CRT は静的リンク（VC++ 再頒布パッケージを配布しない） |
| CMake / Ninja | Build Tools 同梱 | VS 同梱 | **Adopted** | `tools/build-native.ps1` が使用 |
| vcpkg（マニフェストモード） | — | VS 同梱 / git | Candidate | ネイティブ依存のバージョン固定。ベースラインをコミット |
| C++/WinRT | 3.0.260818.1 | NuGet | Candidate | ネイティブ側で WinRT を使う場合 |

## 2. .NET パッケージ

| パッケージ | バージョン | ライセンス | 用途 | 状態 | 再配布・注意 |
|---|---|---|---|---|---|
| Microsoft.WindowsAppSDK.WinUI / .Foundation / .InteractiveExperiences | 2.3.9 / 2.3.12 / 2.1.9（メタパッケージ 2.5.1 の固定版） | MS-EULA（再配布可） | WinUI 3 / MRT Core / Windowing | Adopted | メタパッケージ `Microsoft.WindowsAppSDK` は参照しない（§0.1）。AI・ML 等は必要になった時点で部品として追加 |
| Microsoft.Windows.SDK.BuildTools | 10.0.28000.2705 | MS-EULA | ビルドツール | Adopted | 開発時のみ |
| **bblanchon.PDFium.Win32** | **156.0.8076** | PDFium: BSD-3-Clause（同梱成分は §3.1。いずれも許容型） / ビルドスクリプト: MIT（nuspec の表記は Apache-2.0） | PDF の描画・文字・検索・リンク・目次（`Mavue.Pdf`） | **Adopted**（2026-10-03） | ネイティブ `pdfium.dll`（win-x64 / win-arm64）を出力に置く。ライセンス文は `licenses/pdfium/`（出力にも同梱）。Explorer のプレビュー・サムネイル（`native/Mavue.Shell.Preview`）も同じ `pdfium.dll` を使う（同パッケージのヘッダーでビルド、DLL は Mavue.exe と同じフォルダーから実行時に読み込む。新しい依存・再配布物は増えない）。§3.2 |
| Microsoft.Graphics.Win2D | 1.4.0 | MIT | GPU 2D 描画・エフェクト | Selected | ネイティブ DLL 同梱 |
| CommunityToolkit.Mvvm | 8.4.2 | MIT | MVVM（ソース生成） | Selected | |
| xunit.v3 | 4.0.1 | Apache-2.0 | テスト | Adopted | 開発時のみ |
| Microsoft.Testing.Platform（xunit.v3 の推移的依存。Telemetry 拡張・ApplicationInsights を含む） | 2.4.0 | MIT | テスト実行 | Adopted | 開発時のみ。テスト実行時のテレメトリは `TESTINGPLATFORM_TELEMETRY_OPTOUT=1` で無効化できる |
| xunit.runner.visualstudio / Microsoft.NET.Test.Sdk | — | Apache-2.0 / MIT | — | 未使用 | 以前の記録で Adopted としていたが、xunit.v3 4.x は Microsoft Testing Platform で動くため実際には参照していない（2026-10-02 に `dotnet list package` で確認） |
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
| MuPDF | **AGPL-3.0** または Artifex 商用ライセンス | ◎ | ◎ | ○ | ◎ | ◎ | ◎ | — | **Rejected**: 組み込むと Mavue 全体が AGPL の条件に縛られ、Apache-2.0 で提供できなくなる。商用ライセンス購入時のみ再検討 |
| iText 9 | **AGPL-3.0** / 商用 | ✕ | ○ | ◎ | ◎ | ◎ | ◎ | — | Rejected（同上） |
| Windows.Data.Pdf (WinRT) | OS 内蔵 | ○ | ✕ | ✕ | ✕ | ✕ | ✕ | ✕ | Selected（描画専用フォールバック） |
| pdf.js (WebView2) | Apache-2.0 | ○ | ○ | ○ | △ | ✕ | ✕ | ✕ | Rejected: ネイティブ方針に反する、Quick View 起動が重い |
| Apryse / PSPDFKit(Nutrient) / Syncfusion / Aspose | 商用 | ◎ | ◎ | ◎ | ◎ | ◎ | ◎ | ◎ | 予算判断事項として記録のみ |

### 3.1 PDFium 配布物の確認結果（2026-10-02 確認、2026-10-03 に採用して再確認）

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
- バイナリの入手は NuGet でもよいが、**ライセンス文は同じバージョンの .tgz の `LICENSE` と `licenses/*` から取得**し、配布物の `THIRD-PARTY-NOTICES.txt` に全文を同梱する（ビルド時にバージョン一致を検証するスクリプトを用意する）。リポジトリの `THIRD-PARTY-NOTICES.md` にも追加時に記載する。
- PDFium と同梱コンポーネントはそれぞれのライセンス（BSD-3-Clause、FTL など）のまま扱い、Mavue の Apache-2.0 には含めない。
- Windows での配布: `pdfium.dll` をアプリと同じフォルダ（MSIX 内）に配置。システムフォルダには入れない。
- 更新方法: chromium の PDFium は頻繁にセキュリティ修正が入るため、**最低でも月 1 回**リリースを確認し、`Directory.Packages.props` のバージョンを更新して PDF 回帰テスト（`docs/TESTING.md`）を通してから取り込む。ビルド番号・コミット（nuspec の `repository commit`）を記録する。
- 自前ビルドへの切り替え基準: 修正の取り込みが遅い、または独自パッチ（墨消し用の API 拡張など）が必要になった場合。その場合は Build Tools と depot_tools が必要。

### 3.2 採用の記録（2026-10-03）

| 項目 | 内容 |
|---|---|
| 参照 | `Mavue.Pdf` → NuGet `bblanchon.PDFium.Win32` 156.0.8076（`Directory.Packages.props`）。`Mavue.Viewer`・`Mavue.App`・Quick View ホストは `Mavue.Pdf` 経由 |
| 由来の確認 | NuGet の `runtimes/win-x64/native/pdfium.dll` と GitHub Release `chromium/8076` の `pdfium-win-x64.tgz` の `bin/pdfium.dll` は SHA-256 が一致（`69F1E860…BAEA6`）。arm64 も一致（`45E414BC…9F40`）。nuspec の `repository commit` は `f2e9a1c45bb17b85b540abf1af30146ef65416ac` |
| ビルド設定（`args.gn`） | `pdf_enable_v8 = false`、`pdf_enable_xfa = false`、`is_component_build = false`（JavaScript・XFA なし） |
| 同梱するライセンス文 | `licenses/pdfium/`（`pdfium.txt`、`abseil.txt`、`agg23.txt`、`fast_float.txt`、`freetype.txt`、`icu.txt`、`lcms.txt`、`libjpeg_turbo.ijg`/`.md`、`libopenjpeg.txt`、`libpng.txt`、`llvm-libc.txt`、`simdutf.txt`、`zlib.txt`、ビルドスクリプトの MIT `pdfium-binaries-build-scripts.txt`、`VERSION.txt`、`args.gn.txt`）。`Mavue.Pdf` のビルドで各プログラムの出力の `licenses/pdfium/` にコピーされる |
| 更新手順 | `Directory.Packages.props` の版を変更 → `tools/update-pdfium-licenses.ps1`（同じ版の .tgz を取得し、x64/arm64 の DLL が NuGet と一致することを検証してからライセンス文を置き換える。不一致なら失敗）→ `licenses/pdfium` の差分確認 → PDF の単体テスト・E2E。**最低月 1 回**リリースを確認 |
| コピーレフトの確認 | バイナリに含まれる成分はすべて許容型。`icu.txt` に GPL の文言があるが、ICU4C の autoconf 用スクリプト（`aclocal.m4`・`config.guess`、Autoconf 例外付き）に関するもので、`pdfium.dll` には含まれない。FreeType は FTL を選択（文書へのクレジット表記が必要 → THIRD-PARTY-NOTICES.md に記載） |
| 使い方の制約（Mavue 側） | PDFium はスレッドセーフでないため、すべての呼び出しをプロセス全体のロックで直列化（`PdfiumLibrary`）。ファイルはコールバックで部分読みし（全体を読み込まない）、読み書き・削除共有で開く。リンクは http/https/mailto のみ扱い、JavaScript・ファイル起動・他ファイルへのリンクは実行しない |
| 代替 | pdfium.dll が読み込めない環境では表示のみ Windows.Data.Pdf にフォールバック（文字・検索・リンク・目次は使えない） |

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

- リポジトリ: `LICENSE`（Apache-2.0 全文、apache.org の公式テキストをそのまま使用）、`NOTICE`、`THIRD-PARTY-NOTICES.md`（依存の一覧とライセンス）。
- 配布物（2026-10-04 実装。ZIP と MSIX の共通フォルダー、`tools/build-release.ps1` が作成・検査）の `licenses\`:
  `LICENSE.txt`・`NOTICE.txt`（Mavue）、`THIRD-PARTY-NOTICES.txt`、`pdfium\`（PDFium と同梱部品の全文）、`dotnet\`（.NET の LICENSE と
  THIRD-PARTY-NOTICES）、`windowsappsdk\`（WinUI・Foundation・InteractiveExperiences の license.txt と WinUI の NOTICE.txt）、
  `webview2\`（LICENSE.txt・NOTICE.txt）、各フォルダーの `VERSIONS.txt`（出力の `Mavue.deps.json` から読んだパッケージと版）。
  NuGet パッケージのファイルを版を合わせて複写するので、依存の更新で取り違えない（一覧にないパッケージが deps.json から消えたらビルドを止める）。
  アプリの 設定 › Mavue について › ライセンス情報 から開ける（バージョン表示も同じ場所）。Windows App SDK Base は MSBuild 用で出力に何も置かないため対象外。
- LGPL コンポーネント（採用時）: 動的リンク、DLL 差し替えを妨げない（MSIX でもユーザーが差し替え可能な手段の提供方法を法務確認）、ソース入手方法を記載。

## 10. 未解決事項

1. HEVC（HEIC フォールバック）の特許 — 法務確認。
2. LGPL コンポーネントを MSIX（改ざん防止された配布形式）で配布する場合の「再リンク可能性」要件の解釈 — 法務確認。
3. TWAIN DSM のライセンス表記の一次情報確認。
4. PDFium の XFA 有効ビルドの要否（XFA は V8 依存でサイズ・攻撃面が増大）。現状の方針は XFA/V8 なしのビルド（2026-10-02 確認の公式ビルドと同じ）。
5. bblanchon.PDFium の NuGet 表記（Apache-2.0）とリポジトリ LICENSE（MIT）の食い違い — 実害は小さい（どちらも許容型）が、通知文はリポジトリ/tgz の表記に合わせる。
6. ~~`Microsoft.Windows.SDK.NET.dll` の再配布可否~~ — 2026-10-04 解決: Windows SDK の REDIST 一覧に記載（§0）。条件は 7 と共通。
7. **バイナリ配布用の利用条件（EULA 相当）の確定 — 草案（`licenses/EULA.txt`、RC の配布物に草案と明記して同梱。論点は `docs/EULA-DRAFT.md`）・要法務確認**。Windows App SDK `license.txt` §3(b)(ii) と Windows SDK の
   Distribution Requirements の両方が「配布者とエンドユーザーに、Microsoft のコードを本契約と同等以上に保護する条項へ同意させる」ことを求める。
   Mavue のコードの Apache-2.0 はこれを満たさない（第三者部品の条件は別）。文面・提示方法（インストール時の同意、MSIX/Store の場合の扱い）は
   人の判断が必要で、本リポジトリでは推測で作らない。
8. ~~`Mavue.App` の依存整理~~（2026-10-02 対応: 部品パッケージのみ。§0.1）。Windows AI / Windows ML を使う機能を実装するときに、必要な部品だけを追加して再確認する。
9. Visual Studio Build Tools 2026 でネイティブ DLL をビルドする権利（Visual Studio の有効なライセンスの有無。§0）— ユーザー確認。
10. ネイティブ DLL・Mavue の exe/dll の **公開用コード署名**（docs/PACKAGING.md §3）— 証明書の入手は人の判断・契約が必要。
