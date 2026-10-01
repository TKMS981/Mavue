# Quick View 最小 PoC 自己レビュー（2026-10-02）

対象: `src/Mavue.QuickView`、`src/Mavue.QuickView.Host`、`tools/Mavue.QuickView.Harness`、関連ドキュメント
前提資料: `docs/QUICKVIEW-POC.md`（実測データ）

## 問題点（重大度順）

| 重大度 | # | 問題 | 状態 / 対応 |
|---|---|---|---|
| Critical | P1 | フック経由の常駐プロセスは、実ユーザー入力では**前面化できない**（物理キーで 9/11 失敗）。権利なしの `HWND_TOP` では **Explorer の後ろに表示**される（リモート操作） | **解決済み（設計変更）**: panel 表示（非アクティブ・表示中のみ Topmost）。ユーザー実操作で「問題なし」。ただし P3・P4 が残る |
| High | P2 | 自動 E2E の結果が実環境の前面化挙動を反映しない（注入入力・子プロセス起動で有利に歪む） | 部分対応: ホストの独立起動、実ユーザー入力の検出、送信前ガード。**表示方式の最終判断にはユーザー実操作が必須**（CI だけでは保証できない） |
| High | P3 | panel 方式は Topmost を使う。ユーザー指示「ユーザー操作を奪う強制 Topmost は避ける」との関係 | 範囲を限定（非アクティブ・表示中のみ・他アプリ前面化で即クローズと解除）。**ユーザーの承認待ち** |
| High | P4 | panel 方式の**物理キーボードでの確認がない**（確認はリモート操作で実施） | 未対応。次回ユーザーに依頼 |
| High | P5 | 本機の QuickLook（同じ Space フック方式）との**共存が未検証**。両方動くと二重表示や Space の奪い合いの可能性 | 未対応。検出と警告、または無効化の設定が必要 |
| Medium | P6 | 低レベルフックがタイムアウトで無通知に外れた場合の検知・再設置（ウォッチドッグ）が未実装 | 未対応。長時間常駐テストと合わせて実装 |
| Medium | P7 | 巨大 JPEG の縮小デコードが遅い（192 MP で 451 ms）。WinRT 経路で DCT 縮小が効いていないと推測 | 未対応。WIC 直接経路を検証 |
| Medium | P8 | 常駐ホストのワーキングセットが約 140 MB（Debug）。Release / ReadyToRun / NativeAOT は未計測 | 未対応 |
| Medium | P9 | 選択の前後移動・複数選択が未実装。panel 方式では Explorer の選択変更への追従が必要 | 未対応（設計方針は決定） |
| Medium | P10 | Space を握りつぶした後に選択取得が失敗した場合（選択なし、仮想フォルダ内の項目など）、Space は失われる | 記録。Explorer では選択なしの Space の影響は小さいが、仮想項目のプレビュー対応で解消する |
| Medium | P11 | IME 変換中の判定は推定（文字キー数 + IME オープン状態の越境 `SendMessageTimeout`、最大 30 ms をフック内で消費しうる） | 実機の日本語 IME での確認が未実施 |
| Medium | P12 | `SafeFileWriter` がシンボリックリンクを通常ファイルで置き換えていた | **修正済み**（実体を解決して保存）。本機では symlink を作れずテストはスキップ＝未検証 |
| Low | P13 | 音声・動画・フォルダは「デコーダーなし」「ファイルではない」表示のみ | SPEC の音声/動画プレビュー（Planned）で対応 |
| Low | P14 | PoC の PDF は Windows.Data.Pdf（製品は PDFium） | 想定どおり。PDFium は §DEPENDENCIES の確認結果に従って導入 |
| Low | P15 | 診断用 `--trace-shell` はウィンドウハンドルとクラス名をログに出す（パス・内容は出さない） | 既定オフ。ハーネスのみ使用 |
| Low | P16 | ソリューションビルドと単体ビルドで exe の出力先が分かれ、古い exe を実行する事故が発生 | **修正済み**（`AppendPlatformToOutputPath=false`） |
| Low | P17 | ハーネスの `explorer /select` が Explorer を別プロセスで起動し、ウィンドウなしのプロセスが残留 | **修正済み**（Shell.Application で既存シェル内に開く）。残留したプロセスは終了済み |

## 矛盾チェック（ドキュメント間）

- ARCHITECTURE §4.3 の「アクティブ化して前面へ」を panel 方式に更新済み。ADR-10〜12 を追加。
- WINDOWS-INTEGRATION §1 を実測結果に更新、§14（MSIX・パッケージ ID・Build Tools）を追加。
- 常駐ホストの名称を `Mavue.QuickView.Host.exe` に統一（ARCHITECTURE・BUILD）。
- FEATURES は動作確認できた範囲のみ In Progress に変更（Implemented にはしていない）。整合性テスト合格。

## ライセンス

- 製品コードに追加した外部依存: **なし**（OS の WinRT / Shell / WIC 機能のみ）。
- 開発時のみ: Pillow + NumPy（テスト画像生成）。生成物は自作の合成画像。
- PDFium は配布物を実物確認のみ（未追加）。NuGet にライセンス文が同梱されない点と表記の食い違いを記録（DEPENDENCIES §3.1）。
