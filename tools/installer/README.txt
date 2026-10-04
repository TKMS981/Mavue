Mavue (ZIP 版 / ZIP edition)

■ インストール（管理者権限は不要）
  1. ZIP をすべて展開します。
  2. Install.cmd を実行します。
     %LOCALAPPDATA%\Programs\Mavue にコピーし、「プログラムから開く」・既定のアプリの候補・
     エクスプローラーのプレビュー/サムネイル・右クリックメニュー「Mavue Quick View」・
     サインイン時の Quick View を現在のユーザーに登録し、スタート メニューに追加します。
     既定のアプリは変更しません（設定 > アプリ > 既定のアプリ で選べます）。
  必要なもの: Windows 10 2004 以降（Windows 11 推奨）。.NET や Windows App SDK の別途インストールは不要です。

■ 更新
  新しい ZIP を展開して Install.cmd を実行します。設定と PDF プレビューの選択は引き継がれます。

■ 削除
  設定 > アプリ > インストールされているアプリ > Mavue > アンインストール。
  設定ファイル（%LOCALAPPDATA%\Mavue）は残ります。消す場合は
  powershell -ExecutionPolicy Bypass -File "%LOCALAPPDATA%\Programs\Mavue\Uninstall.ps1" -RemoveSettings

■ MSIX 版との関係
  MSIX 版と ZIP 版は同時に使えません（メニューが重複します）。切り替えるときは先に一方を削除してください。
  ZIP 版はエクスプローラーのプレビュー ウィンドウとサムネイルを対応するすべての種類で提供します
  （MSIX 版では Windows の仕組み上、他のアプリも登録している種類では使われないことがあります）。

■ ライセンス
  Mavue\licenses\ を参照してください（Mavue: Apache License 2.0。第三者コンポーネントはそれぞれのライセンス）。
  利用条件は Mavue\licenses\EULA.txt です（このリリース候補では法務確認前の草案）。
  アプリの 設定 > Mavue について > ライセンス情報 からも開けます。

------------------------------------------------------------------------

Install (no administrator rights)
  1. Extract the whole ZIP.
  2. Run Install.cmd. It copies Mavue to %LOCALAPPDATA%\Programs\Mavue and registers it for the current user
     (Open with, Default apps candidates, File Explorer preview and thumbnails, the "Mavue Quick View" context-menu
     command, Quick View at sign-in) and adds it to the Start menu. Default apps are not changed.
  Requires Windows 10 2004 or later (Windows 11 recommended). .NET and the Windows App SDK are included.

Update: extract the new ZIP and run Install.cmd. Settings and the PDF preview choice are kept.

Uninstall: Settings > Apps > Installed apps > Mavue > Uninstall. Settings (%LOCALAPPDATA%\Mavue) are kept; add
-RemoveSettings to Uninstall.ps1 to remove them.

The MSIX edition and the ZIP edition cannot be used together; remove one before installing the other.

Licenses: Mavue\licenses\ (Mavue: Apache License 2.0; third-party components under their own licenses).
Terms of use: Mavue\licenses\EULA.txt (a draft, not yet reviewed by legal counsel, in this release candidate).
