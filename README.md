# Mavue

macOS のプレビューと Finder のクイックルックに着想を得た、Windows 11 向けのネイティブデスクトップアプリケーション。
（A native Windows 11 app inspired by macOS Preview and Finder Quick Look.）
画像・PDF の高速プレビュー（Explorer で Space）、閲覧・編集・注釈・署名・フォーム・OCR・スキャン・印刷・変換・メタデータ管理、
Explorer との深い統合を目標とする。

> **リリース候補（RC）段階**。今使えるのは**閲覧**（画像・GIF・SVG・PDF・動画・音声）、Explorer で Space を押す **Quick View**、
> Explorer 連携（プレビュー ウィンドウ・サムネイル・右クリック「Mavue Quick View」「Mavue で開く」・「プログラムから開く」）。
> 編集・注釈・署名・フォーム・OCR・スキャン・印刷・変換などは仕様（[docs/SPEC.md](docs/SPEC.md)）にあり、まだ実装していない。
> 機能ごとの状態は [docs/FEATURES.md](docs/FEATURES.md)。

## ドキュメント

| ファイル | 内容 |
|---|---|
| [CLAUDE.md](CLAUDE.md) | 開発ルール |
| [docs/SPEC.md](docs/SPEC.md) | 製品仕様（正） |
| [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md) | アーキテクチャ（Quick View 設計を含む） |
| [docs/WINDOWS-INTEGRATION.md](docs/WINDOWS-INTEGRATION.md) | Explorer / Shell / 印刷 / スキャン / Ink 統合 |
| [docs/DEPENDENCIES.md](docs/DEPENDENCIES.md) | 依存ライブラリとライセンス判断 |
| [docs/BUILD.md](docs/BUILD.md) | ビルド手順 |
| [docs/PACKAGING.md](docs/PACKAGING.md) | 配布物（ZIP インストーラー・MSIX）・ランタイム同梱・署名・更新手順 |
| [docs/RELEASE-CHECKLIST.md](docs/RELEASE-CHECKLIST.md) | 別 PC での配布確認チェックリスト |
| [licenses/EULA.txt](licenses/EULA.txt) | バイナリ配布物の利用条件（RC: 法務確認前の草案。配布物に `licenses\EULA.txt` として同梱） |
| [docs/EULA-DRAFT.md](docs/EULA-DRAFT.md) | 利用条件が必要な理由・法務確認の論点 |
| [docs/TESTING.md](docs/TESTING.md) | テスト戦略 |
| [docs/FEATURES.md](docs/FEATURES.md) | 機能ステータス |
| [docs/QUICKVIEW-POC.md](docs/QUICKVIEW-POC.md) | Quick View PoC の実機検証結果（前面表示・レイテンシ・メモリ） |
| [docs/REDACTION.md](docs/REDACTION.md) | 安全な墨消しの技術要件 |
| [docs/reviews/](docs/reviews/) | 自己レビュー記録 |

## インストール（ZIP 版。第一配布候補）

必要なもの: Windows 11（Windows 10 2004 以降でも動作する想定。確認は Windows 11）、x64。管理者権限・.NET などの事前インストールは不要。

1. `Mavue-<版>-win-x64.zip` を**すべて展開**する（ZIP の中から直接実行しない）。展開先はどこでもよい（日本語やスペースを含むパスも可）。
2. `Install.cmd` を実行する。`%LOCALAPPDATA%\Programs\Mavue` に入り、スタート メニュー、「プログラムから開く」、Explorer のプレビュー ウィンドウと
   サムネイル、右クリックの「Mavue Quick View」、サインイン時の Quick View が現在のユーザーに登録される。**既定のアプリは変更しない**
   （設定 › アプリ › 既定のアプリ で選べる）。
3. Explorer でファイルを選んで **Space** で Quick View。

- 更新: 新しい ZIP を展開して `Install.cmd`（設定と PDF プレビューの選択は引き継ぐ）。
- 削除: 設定 › アプリ › インストールされているアプリ › Mavue › アンインストール（設定ファイル `%LOCALAPPDATA%\Mavue` は残る）。
- PDF のプレビューは既存のもの（Edge など）を保つ。Mavue にするには Mavue の 設定 › Windows との統合。
- 署名: RC は開発用の証明書で署名している（または未署名）。Windows の SmartScreen や Smart App Control の警告が出ることがあり、
  Windows 11 の上段メニューは証明書を信頼していない PC では従来のメニュー（その他のオプションを確認）に出る。**公開版には信頼される証明書での署名が必要**。
- 利用条件: `Mavue\licenses\EULA.txt`（RC では法務確認前の草案）。ライセンス: `Mavue\licenses\`。

## MSIX 版（将来の Store・自動更新向けの候補）

同じアプリ一式の MSIX パッケージも作れる（`Mavue-<版>-x64.msix`）。インストール・削除が確実で、将来 Microsoft Store や自動更新に使える。ただし:

- **Explorer のプレビュー ウィンドウとサムネイルは、他のアプリのパッケージ（フォト・メディア プレーヤー等）も登録している種類では Mavue が使われない**
  （Windows の仕組み。標準の Windows 11 では PNG・JPEG・SVG・動画・音声などが該当。PDF のサムネイルは使われる）。Mavue を既定のアプリにした場合の挙動は**未確認**。
- 署名が信頼されていない PC にはインストールできない。更新時は Mavue と Quick View を閉じる。
- ZIP 版と同時に入れない。

比較の詳細と根拠は [docs/PACKAGING.md](docs/PACKAGING.md) §1・§5。

## 開発者向け

```powershell
# .NET 10 SDK が必要（docs/BUILD.md §2）
dotnet build Mavue.slnx
dotnet test --solution Mavue.slnx
powershell -ExecutionPolicy Bypass -File tools/smoke-test.ps1

# 本体: 画像・GIF・PDF・動画・音声を開く（引数 / Ctrl+O / ドロップ）
.\src\Mavue.App\bin\Debug\net10.0-windows10.0.26100.0\win-x64\Mavue.exe <ファイル>

# Quick View 常駐ホスト: 起動後、Explorer で画像/PDF/動画/音声を選んで Space
.\src\Mavue.QuickView.Host\bin\Debug\net10.0-windows10.0.26100.0\win-x64\Mavue.QuickView.Host.exe

# 配布物（ZIP + Install.cmd、MSIX、シンボル、SHA256SUMS）: artifacts\release（docs/PACKAGING.md §3）
powershell -File tools/build-release.ps1 -Version 0.1.0
```

公開前に開発者側では完了できない作業（署名証明書、利用条件の法務確認、Build Tools のライセンス確認、別 PC での確認）は
[docs/PACKAGING.md](docs/PACKAGING.md) §9 と [docs/RELEASE-CHECKLIST.md](docs/RELEASE-CHECKLIST.md)。

## 技術スタック

C# / .NET 10 / WinUI 3（Windows App SDK）。Explorer にロードされるシェル拡張は C++ / COM。
PDF は PDFium、画像は WIC を中心に構成（QPDF は候補。詳細と理由は ARCHITECTURE / DEPENDENCIES）。

## ライセンス

Mavue 自身のソースコードは [Apache License 2.0](LICENSE) で提供する（[NOTICE](NOTICE)）。バイナリ配布物には利用条件
（[licenses/EULA.txt](licenses/EULA.txt)、法務確認前）が付く。
依存ライブラリ・Windows App SDK などの第三者コンポーネントは、それぞれのライセンスに従う
（Apache-2.0 には含まれない）。一覧は [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md)。

## 商標

macOS、Finder、Quick Look（クイックルック）は、米国およびその他の国で登録された Apple Inc. の商標です。
Windows は Microsoft グループの商標です。Mavue は独立したプロジェクトであり、Apple Inc. および Microsoft Corporation とは
提携・承認・後援の関係にありません。Mavue は Apple のコード・画像・アイコン・フォントを使用していません。

macOS, Finder and Quick Look are trademarks of Apple Inc., registered in the U.S. and other countries. Mavue is not
affiliated with, endorsed by, or sponsored by Apple Inc.
