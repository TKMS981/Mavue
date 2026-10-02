# Mavue

macOS のプレビューと Finder のクイックルックに着想を得た、Windows 11 向けのネイティブデスクトップアプリケーション。
（A native Windows 11 app inspired by macOS Preview and Finder Quick Look.）
画像・PDF の高速プレビュー（Explorer で Space）、閲覧・編集・注釈・署名・フォーム・OCR・スキャン・印刷・変換・メタデータ管理、
Explorer との深い統合を目標とする。

> 現在は**初期段階**。製品機能はまだ完成していない。Explorer で Space を押すと画像/PDF をプレビューする **Quick View の最小 PoC** が動作する
> （検証結果: [docs/QUICKVIEW-POC.md](docs/QUICKVIEW-POC.md)）。状態は [docs/FEATURES.md](docs/FEATURES.md) を参照。

## ドキュメント

| ファイル | 内容 |
|---|---|
| [CLAUDE.md](CLAUDE.md) | 開発ルール |
| [docs/SPEC.md](docs/SPEC.md) | 製品仕様（正） |
| [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md) | アーキテクチャ（Quick View 設計を含む） |
| [docs/WINDOWS-INTEGRATION.md](docs/WINDOWS-INTEGRATION.md) | Explorer / Shell / 印刷 / スキャン / Ink 統合 |
| [docs/DEPENDENCIES.md](docs/DEPENDENCIES.md) | 依存ライブラリとライセンス判断 |
| [docs/BUILD.md](docs/BUILD.md) | ビルド手順 |
| [docs/TESTING.md](docs/TESTING.md) | テスト戦略 |
| [docs/FEATURES.md](docs/FEATURES.md) | 機能ステータス |
| [docs/QUICKVIEW-POC.md](docs/QUICKVIEW-POC.md) | Quick View PoC の実機検証結果（前面表示・レイテンシ・メモリ） |
| [docs/REDACTION.md](docs/REDACTION.md) | 安全な墨消しの技術要件 |
| [docs/reviews/](docs/reviews/) | 自己レビュー記録 |

## クイックスタート

```powershell
# .NET 10 SDK が必要（docs/BUILD.md §2）
dotnet build Mavue.slnx
dotnet test --solution Mavue.slnx
powershell -ExecutionPolicy Bypass -File tools/smoke-test.ps1

# 本体: 画像・GIF・PDF・動画・音声を開く（引数 / Ctrl+O / ドロップ）
.\src\Mavue.App\bin\Debug\net10.0-windows10.0.26100.0\win-x64\Mavue.exe <ファイル>

# Quick View 常駐ホスト（PoC）: 起動後、Explorer で画像/PDF/動画/音声を選んで Space
.\src\Mavue.QuickView.Host\bin\Debug\net10.0-windows10.0.26100.0\win-x64\Mavue.QuickView.Host.exe
```

## 技術スタック

C# / .NET 10 / WinUI 3 (Windows App SDK 2.5)。Explorer にロードされるシェル拡張は C++ / COM。
PDF は PDFium + QPDF、画像は WIC を中心に構成（詳細と理由は ARCHITECTURE / DEPENDENCIES）。

## ライセンス

Mavue 自身のソースコードは [Apache License 2.0](LICENSE) で提供する（[NOTICE](NOTICE)）。
依存ライブラリ・Windows App SDK などの第三者コンポーネントは、それぞれのライセンスに従う
（Apache-2.0 には含まれない）。一覧は [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md)。

## 商標

macOS、Finder、Quick Look（クイックルック）は、米国およびその他の国で登録された Apple Inc. の商標です。
Windows は Microsoft グループの商標です。Mavue は独立したプロジェクトであり、Apple Inc. および Microsoft Corporation とは
提携・承認・後援の関係にありません。Mavue は Apple のコード・画像・アイコン・フォントを使用していません。

macOS, Finder and Quick Look are trademarks of Apple Inc., registered in the U.S. and other countries. Mavue is not
affiliated with, endorsed by, or sponsored by Apple Inc.
