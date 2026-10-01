# 初期設計 自己レビュー（2026-10-02）

対象: `docs/ARCHITECTURE.md`, `DEPENDENCIES.md`, `WINDOWS-INTEGRATION.md`, `TESTING.md`, `FEATURES.md`（初版）
観点: 技術的矛盾 / 要件漏れ / ライセンス / Windows API 制約 / 重大な実装リスク
結果の扱い: 「修正済み」は本レビュー時点でドキュメントへ反映済み。「継続」は FEATURES.md / 各 doc の Investigating 項目として追跡。

---

## A. 技術的矛盾

| # | 指摘 | 対応 |
|---|---|---|
| A1 | ARCHITECTURE の表では `Mavue.QuickView.exe`、モジュール構成では `Mavue.QuickView.Host` と名称が不一致 | 修正済み: Host プロジェクトの出力アセンブリ名を `Mavue.QuickView` と明記 |
| A2 | FEATURES で F03.01（Space Quick View）が Investigating、F22.01（同一機能）が Planned | 修正済み: 両方 Investigating に統一 |
| A3 | ARCHITECTURE §4.3(3)「フック起点の入力直後なので SetForegroundWindow が許可される想定」は根拠不十分。前面化ロック規則の「最後の入力を受け取ったプロセス」に、LL フックでキーを握りつぶしたプロセスが該当するかは文書化されていない | 修正済み: 「未検証リスク」として明記し、プロトタイプ最初の検証項目に指定（R2） |
| A4 | `Mavue.Core` を「Windows 非依存」としたが、安全保存で使う `ReplaceFileW` の属性・ACL・ADS 維持は Windows 固有の挙動 | 問題なし: `File.Replace` は BCL API。挙動差は Windows 上のテストで担保。Core の TFM は `net10.0` のまま |
| A5 | 「プロパティハンドラーは HKLM のみ」と「MSIX の `desktop2:DesktopPropertyHandler`」の併記 | 矛盾なし: 非パッケージ時は HKLM、MSIX はマニフェストで登録される（表の列が別） |
| A6 | 常駐 Quick View を NativeAOT 対象とする一方、Win2D / WinAppSDK の NativeAOT 互換性が未確認 | 継続: プロトタイプで検証。非互換なら ReadyToRun + 常駐で代替（常駐方式自体は変わらない） |

## B. 要件漏れ（SPEC 項目に対する設計の欠落）

| # | SPEC 項目 | 指摘 | 対応 |
|---|---|---|---|
| B1 | §8 GPS / 地図表示 | 地図タイルはオンラインサービスが必要になりがちで、§27「ローカル既定・外部送信なし」と衝突しうる | 修正済み: FEATURES 備考に記載。方針: 地図表示は**明示操作時のみ**・送信内容（座標のみ）を開示・オプトイン。オフライン地図も調査。機能は削除しない |
| B2 | §13 AutoFill | 入力元データ（氏名・住所等）の保持方式が未設計 | 継続: Mavue 内のプロフィール（DPAPI 暗号化）+ フィールド名マッピング。ARCHITECTURE §12 に追記 |
| B3 | §5 Slideshow / §9 Presentation | 設計記述なし | 継続: 共通ビューアの全画面モードの派生として実装（Planned のまま追跡） |
| B4 | §21 Tabs / Multiple windows | プロセス表に記載のみ | 継続: AppInstance による単一プロセス・複数ウィンドウ・タブ移動（ウィンドウ間ドラッグ）を App 設計時に詳細化 |
| B5 | §3 Quick View の「Copy」 | ファイルのコピーか画像データのコピーか曖昧 | 方針: 両方（ファイル = CF_HDROP、画像/選択テキスト = データ）。WINDOWS-INTEGRATION §7 に準拠 |
| B6 | §12 Cryptographic signatures | 「後段階」と SPEC にあるが除外ではない | DEPENDENCIES §3 に CNG/PAdES 方針を記載済み。FEATURES で Planned 扱い（SPEC の箇条書き外のため X 項目として追加） |
| B7 | §24 Thumbnail caching / Preview caching | キャッシュの格納場所・上限・無効化キーが未定義 | 修正済み（方針）: `%LOCALAPPDATA%\Mavue\Cache`、キー = ボリュームシリアル + FileId + サイズ + 更新時刻 + レンダリング設定、LRU・容量上限は設定可能。機微データ扱いのため設定でクリア可能 |
| B8 | §17 Scanner → JPG/PDF の自動傾き補正 | アルゴリズム未定 | 継続: 投影プロファイル法を自前実装（依存追加なし）。精度テストを TESTING に追加予定 |

**SPEC で除外されていない機能を削除・永久延期したものはない**（FEATURES.md は SPEC から機械抽出し 322 項目を全件掲載。除外は SPEC §29 記載分のみ、PDF→Office は Backlog）。

## C. ライセンス

| # | 指摘 | 重大度 | 対応 |
|---|---|---|---|
| C1 | HEIC フォールバック（libde265）は LGPL に加え **HEVC 特許**の問題 | 高 | 法務確認まで同梱しない。HEIC 機能は OS 拡張で提供（機能は維持） |
| C2 | LGPL コンポーネントを MSIX で配布する場合の再リンク要件 | 中 | 法務確認。代替として LibRaw は CDDL を選択 |
| C3 | MuPDF / iText は AGPL | 高 | 不採用（Rejected）を記録済み |
| C4 | 背景除去モデル BRIA RMBG は非商用 | 高 | Rejected を記録。Apache/MIT モデルのみ候補 |
| C5 | QuickLook は GPL-3.0 | 中 | コード参照・流用禁止を記録 |
| C6 | FFmpeg は LGPL/GPL 構成次第 + コーデック特許 | 中 | Legal review。既定は Media Foundation |
| C7 | PDFium 同梱サードパーティ（FreeType FTL のクレジット表記等） | 低 | THIRD-PARTY-NOTICES に全文同梱 |
| C8 | TWAIN DSM のライセンス一次情報が未確認 | 低 | DSM は同梱しない方針を第一案として回避 |
| C9 | 地図タイル提供元の利用規約（B1） | 中 | 地図実装時に調査 |

## D. Windows API 上の制約

| # | 制約 | 影響 | 対応 |
|---|---|---|---|
| D1 | Explorer に Space キー拡張ポイントなし | Quick View の根幹 | LL フック方式（ADR-3）。非公開ウィンドウクラスへの依存を 1 か所に隔離 + 実機回帰テスト |
| D2 | LL フックのタイムアウト無通知解除 / UIPI | Space 無反応 | 定期再インストール、昇格 Explorer は非対応として明記、代替トリガー |
| D3 | 既定アプリはプログラムで設定不可 | 関連付け UX | 設定画面への誘導 |
| D4 | Win11 上段コンテキストメニューはパッケージ ID 必須 | 配布形態 | MSIX 主体（ADR-8） |
| D5 | IFilter に MSIX 拡張なし | Windows Search 統合 | 別コンポーネント（管理者インストール）を調査。機能は削除しない |
| D6 | WinUI 3 InkCanvas は Experimental | Windows Ink | Pointer API で独自実装（ADR-9） |
| D7 | Windows AI（TextRecognizer, ImageObjectExtractor）は Copilot+ PC（NPU）+ パッケージ ID 必須。本機は NPU なし | OCR/背景除去の品質 | 既定は Windows.Media.Ocr / ONNX。Windows AI は対応機でのみ優先 |
| D8 | Windows.Media.Ocr は言語パック依存、最大 10000px | OCR | タイル分割、未導入言語は設定アプリへ誘導 |
| D9 | Windows Protected Print Mode | 印刷 | XPS/D2D 印刷パス。実機で WPP 有効化して検証 |
| D10 | 64bit アプリから 32bit TWAIN DS をロード不可 | TWAIN | x86 ブリッジプロセス |
| D11 | プレビューハンドラーの登録優先順位（MSIX vs 既存登録）が未検証 | Preview Pane | 実機検証。既存登録を無断上書きしない |
| D12 | クラウドプレースホルダの読み取りはダウンロードを誘発 | Quick View | 属性確認 + 明示操作でのみハイドレーション |

## E. 重大な実装リスク

| # | リスク | 確度 / 影響 | 緩和策 |
|---|---|---|---|
| R1 | **真の墨消し**: テキスト（部分グリフ）、ベクター、画像の部分消去、フォーム XObject 内、注釈外観、増分更新の旧データ残存 | 高 / 高（情報漏えい） | 完全書き換え保存（増分更新禁止）、抽出による自動検証、UI で「墨消し」と「マスク」を区別、保守的設計（不明確な場合は領域全体をラスタ化する選択肢） |
| R2 | **Quick View の前面化**: フック経由では SetForegroundWindow が拒否される可能性 | 中 / 高 | プロトタイプ最初の検証項目。失敗時の代替（トップモスト表示 + フォーカス取得のユーザー操作）を用意 |
| R3 | **Space 判定の誤爆**（タイプアヘッド、IME 変換中、XAML 部分） | 中 / 高 | 表駆動テスト + 実機回帰。IME 変換中（`ImmGetContext` ではなくフォーカススレッドの IME 状態確認）を判定に追加 |
| R4 | Explorer の内部構造変更（Windows Update） | 中 / 高 | 判定ロジックの隔離、Insider ビルドでの早期確認 |
| R5 | 信頼できない PDF/画像の解析による脆弱性 | 中 / 高 | 依存の迅速な更新、ファジング、将来の低権限 Worker |
| R6 | 常駐プロセスのメモリ・電力コスト | 中 / 中 | 遅延ロード、メモリ通知で縮退、無効化オプション |
| R7 | **本機に C++ ツールチェーンがない**（VS / Build Tools / Windows SDK 未導入） | 確定 / 高 | シェル拡張・PDFium ラッパー・TWAIN ブリッジのビルドに必須。導入は管理者権限が必要なためユーザー判断を仰ぐ |
| R8 | .NET 10 ランタイムがマシン全体に未導入（SDK はユーザーローカル） | 確定 / 中 | 開発時は `DOTNET_ROOT` を設定。配布物は自己完結 or ランタイム依存をインストーラで解決 |
| R9 | Git リポジトリ未初期化、ルートに無関係の `package.json` / `node_modules`（`claude-code` 依存） | 確定 / 低 | `.gitignore` で除外。削除はユーザー判断（本作業では触らない） |
| R10 | セキュリティ製品による LL フックの誤検知 | 中 / 中 | 署名・透明性・設定で無効化 |
