# Mavue 配布・パッケージング・署名

最終更新: 2026-10-04（リリース候補（RC）: ZIP 版を第一候補に、EULA 草案を同梱、署名の区別。以前: MSIX の Explorer 連携の原因特定、成果物の整合チェック）。対象: Windows 11 x64（ARM64 はビルドのみ確認）。
関連: `docs/RELEASE-CHECKLIST.md`（別 PC での確認）、`docs/EULA-DRAFT.md`（利用条件のたたき台）、`docs/WINDOWS-INTEGRATION.md`、`docs/BUILD.md`、
`docs/DEPENDENCIES.md`（ライセンス）、`THIRD-PARTY-NOTICES.md`。

## 1. 配布形態の比較（正式配布の判断材料）

どちらも `tools/build-release.ps1` が同じ `Mavue\` フォルダー（同一のファイル、SHA-256 一致を自動検査）から作る。

**RC の方針（2026-10-04）**: **ZIP 版を第一配布候補**とする。MSIX 版は Explorer のプレビュー/サムネイルの制約（§5.3）を明記したうえで、
将来の Microsoft Store・自動更新向けの候補として成果物に残す（制約を回避する実装はしない）。最終的な公開形態の決定はユーザー（§9）。

| 観点 | ZIP 版（Install.cmd） | MSIX 版 |
|---|---|---|
| 位置付け | **正式配布候補**（Explorer 連携がすべて動く） | **条件付き候補**（Explorer のプレビュー/サムネイルに Windows 側の制約、§5.3） |
| インストール | ZIP を展開して `Install.cmd`。ユーザー単位 `%LOCALAPPDATA%\Programs\Mavue\app-<ver>\`。**管理者不要**。3 秒 | `.msix` をダブルクリック（App Installer）または `Add-AppxPackage`。ユーザー単位。**管理者不要**（証明書が信頼済みの場合）。1〜2 秒 |
| 必要な事前導入 | なし（.NET・Windows App SDK 同梱） | なし（同） |
| 「プログラムから開く」・既定のアプリの候補 | あり（HKCU 登録） | あり（パッケージのファイルの種類） |
| Explorer のプレビュー ウィンドウ | **67 種類で Mavue**（既存の他社プレビューは保持。PDF は設定で Mavue に切替可） | **他のパッケージ（フォト・メディア プレーヤー等）も登録している種類では使われない**。この PC で Mavue が使われたのは .x3f だけ（.pdf は Edge の拡張子単位のプレビューが優先） |
| サムネイル | PDF・SVG で Mavue | PDF のみ Mavue（SVG はフォトも登録しているため使われない） |
| 32 ビット アプリのファイル ダイアログ | プレビュー・サムネイルとも Mavue（x86 版） | 確認していない |
| Windows 11 上段メニュー「Mavue Quick View」 | あり（識別パッケージ。**利用者の PC が署名証明書を信頼している必要**。信頼しない場合は従来メニューに切替、未確認） | あり（パッケージの宣言） |
| サインイン時の Quick View | HKCU Run | StartupTask（設定 › アプリ › スタートアップで無効化可。初回動作は未確認） |
| 設定ファイル | `%LOCALAPPDATA%\Mavue`（アンインストール後も残す） | 既存の `%LOCALAPPDATA%\Mavue` があればそれを使う。無ければパッケージ内（`LocalCache`）に作られ、**削除で消える** |
| 更新 | 新しい ZIP の `Install.cmd`。設定・PDF プレビューの選択・登録を引き継ぐ。旧版フォルダーは使用中なら次回削除 | 上位バージョンの `.msix`。**Mavue/Quick View/Explorer が読み込んだ Mavue のハンドラーが動いていると失敗**（0x80073D02）。終了してから、または `-ForceApplicationShutdown`（App Installer での挙動は未確認） |
| アンインストール | 設定 › アプリ › インストールされているアプリ（`Uninstall.ps1`）。使用中ファイルは次回サインインで削除 | 設定 › アプリ。即時・完全 |
| 署名 | exe/dll（15 ファイル）と識別パッケージ。未署名でも動くが SmartScreen・Smart App Control の対象 | パッケージの署名が**必須**（証明書が信頼されないとインストール不可）。中の exe/dll も署名済み |
| 自動更新 | なし（手動） | App Installer ファイル（`.appinstaller`）で可能（未作成） |
| Microsoft Store | 不可 | 可能（Store の審査・署名。未検討） |
| 併用 | **ZIP 版と MSIX 版は同時に入れない**（Install.cmd は MSIX があると中止） | 同左（MSIX 側からは検出しない） |

現時点の技術的な結論: Explorer 連携（プレビュー ウィンドウ・サムネイル）を製品の要件どおり提供できるのは **ZIP 版**。MSIX 版は Windows の仕組み上、
他のアプリのパッケージと種類が重なるとハンドラーが使われない（§5.3）。MSIX を正式にするなら、この制約を受け入れるか、Store/企業配布など別の利点で判断する。

## 2. ランタイム・依存関係の扱い（確定）

| 項目 | 決定 | 根拠・確認 |
|---|---|---|
| .NET | self-contained（.NET 10.0.12 の Microsoft.NETCore.App を同梱） | パッケージ版でも coreclr.dll がパッケージ内から読まれ、`C:\Program Files\dotnet` のモジュールは 0 個（実測）。WindowsDesktop ランタイムは含まない。MIT |
| Windows App SDK | self-contained（WinUI 2.3.9 / Foundation 2.3.12 / InteractiveExperiences 2.1.9） | Microsoft.UI.Xaml.dll がパッケージ内から読まれる（実測）。Windows App Runtime の導入は不要 |
| Mavue と Quick View | 1 フォルダーに同居 | 共通ファイルはバイト一致。不一致ならビルドを止める |
| Windows SDK の C# 投影 | 同梱（無改変） | Windows SDK の REDIST 一覧に記載、SHA-256 一致（`docs/DEPENDENCIES.md` §0） |
| VC++ ランタイム | 同梱しない（ネイティブ DLL は静的リンク） | |
| 最低 OS | Windows 10 2004（19041）。確認は Windows 11 | |

保守: self-contained の .NET / Windows App SDK は Windows Update で更新されない。セキュリティ更新が出たら Mavue を再ビルドして再配布する。

## 3. リリース成果物（`tools/build-release.ps1`）

```powershell
powershell -File tools/build-native.ps1 -Architecture x64
powershell -File tools/build-native.ps1 -Architecture x86          # 32 ビット アプリ用のプレビュー DLL と pdfium
$env:DOTNET_ROOT = "$env:LOCALAPPDATA\Microsoft\dotnet"            # SDK をユーザー フォルダーに入れている場合
powershell -File tools/build-release.ps1 -Version 0.1.0            # 開発用証明書（CN=Mavue Dev）
powershell -File tools/build-release.ps1 -Version 0.1.0 -Subject 'CN=<公開用証明書>' -TimestampUrl <URL>
```

| ファイル（`artifacts\release\`） | 内容 |
|---|---|
| `Mavue-<ver>-win-x64.zip`（73 MB） | `Install.cmd`・`Install.ps1`・`Uninstall.ps1`・`README.txt`（日英）と `Mavue\`（アプリ、`licenses\`（`EULA.txt` を含む）、`Uninstall.ps1`、上段メニュー用の識別パッケージ） |
| `Mavue-<ver>-x64.msix`（74.5 MB） | `Mavue\`（識別パッケージとスクリプトを除く同じファイル）、ロゴ、マニフェスト、署名 |
| `Mavue-<ver>-win-x64-symbols.zip` | Mavue の .pdb（ネイティブ DLL の .pdb は未生成） |
| `SHA256SUMS.txt` | ZIP・MSIX・シンボルの SHA-256 |

**整合チェック（ビルドの最後に自動）**: リリース フォルダーの全ファイルが ZIP と MSIX の `Mavue\` に同じ SHA-256 で入っていること、余分なファイルがないこと、
ZIP の `Install.cmd`・`Install.ps1`・`Uninstall.ps1`・`README.txt`・`licenses\LICENSE.txt`・`NOTICE.txt`・`THIRD-PARTY-NOTICES.txt`・`EULA.txt`、MSIX の
`AppxManifest.xml`・`AppxBlockMap.xml`・署名、シンボルが出荷する Mavue アセンブリと対応すること、ZIP の項目名にバックスラッシュがないこと。
1 つでも外れればビルドが失敗する。

**署名の区別**: 開発用証明書（`CN=Mavue Dev`）で署名したとき、署名しなかったとき（`-NoSign`）、タイムスタンプが無いときは、ビルドの最後に
「配布用ではない（信頼される証明書が必要）」と警告する。**RC の成果物は開発用証明書の署名であり、公開には実リリース用の証明書での再ビルドが必要**（§6）。

フォルダーの中身（2026-10-04 確認）: 署名対象 15（Mavue の exe 2・dll 11（x86 含む）と pdfium.dll 2）、識別パッケージ、`licenses\`、ロゴ以外は .NET と Windows App SDK の
ランタイム（Microsoft 署名済み。`createdump.exe`・`RestartAgent.exe` もランタイムの一部）。テスト・ハーネス・.pdb・開発用ファイルは含まれない。

修正した問題（2026-10-04）: Windows PowerShell の `Compress-Archive`（と `ZipFile.CreateFromDirectory`）は ZIP の項目名を `Mavue\Mavue.exe` のように
バックスラッシュで書く（ZIP 形式の違反。展開ツールによっては 1 つのファイル名になる）。項目を `/` 区切りで書くように変更し、チェックで検出する。

## 4. ZIP 版（Install.cmd）

### 4.1 動作

`Install.cmd`（→ `Install.ps1`）: ZIP 展開済み・Windows 10 19041 以降・アーキテクチャ・**MSIX 版が無い**ことを確認 → 旧版の Quick View を終了 →
`app-<version>\` にコピー（`.partial` から改名、Web マークを解除）→ `Mavue.exe --register --keep-preview-choices`（利用者の PDF プレビューの選択を維持）→
識別パッケージで上段メニュー（失敗時は従来メニュー）→ `Mavue.QuickView.Host.exe --register`（サインイン時起動）→ スタート メニュー、インストール済みアプリの項目 →
古い `app-*` を削除（使用中は次回）→ Quick View を起動。メッセージはユーザーの言語設定の先頭（Mavue の UI と同じ）で日本語/英語。

`Uninstall.ps1`: Mavue のウィンドウが開いていれば閉じるよう求める（強制終了しない）→ Quick View 終了 → 登録をすべて解除（置き換えたプレビューは元に戻る）→
ショートカット・項目・フォルダーを削除（使用中は次回サインインで）。設定は残す（`-RemoveSettings` で削除）。

### 4.2 実機確認

**RC（2026-10-04 19 時、RC の成果物。開発用の登録をすべて外し、PC を操作しない状態）**

| 確認 | 結果 |
|---|---|
| ZIP を日本語とスペースを含むフォルダー（`Mavue RC テスト …\展開 先`）に展開、全ファイルに Web のマークを付けて `Install.cmd`（cmd 経由） | 3 秒、成功。日本語の表示、利用条件（EULA.txt）の案内、インストール先の Web のマーク 0、署名 Valid 15/15、上段メニューあり |
| 本体 E2E / Quick View E2E（インストール先） | 15/15 / 21/23（既知の 2 件: 注入した Ctrl+T/Ctrl+Tab の制約、`cli-quickview` の間欠） |
| Explorer E2E（インストール先） | 8/10 → 再実行で `explorer-preview-pdf` PASS。`explorer-monitors` は 96 DPI モニターで Explorer 自身のプレビュー ウィンドウ幅が 68 px になっており、テストの「幅 100 px 超」の条件を満たさない（Mavue はその幅いっぱいに正しい DPI で表示。スクリーンショットで確認）。環境（Explorer の保存されたレイアウト）による |
| Explorer 再起動の直後 | 常駐 Quick View は同じプロセスで継続し、Space・矢印・Space で閉じる・フォーカス系 6/6 PASS。右クリック「Mavue Quick View」も成功 |
| 更新 0.1.0 → 0.1.1（`Install.cmd`） | PDF プレビューの選択（Mavue）を維持、本体と Quick View の設定ファイルは内容不変、登録・Run・App Paths は `app-0.1.1`、Quick View は新版、上段メニュー成功。更新後のプレビュー/サムネイル PASS（PDF が Mavue のまま＝選択の維持を確認） |
| アンインストール | Mavue のキー 0、`HKCU\Software\Classes` に `Programs\Mavue` を指す値 0、Run・識別パッケージ・ショートカット・プロセスなし、PDF プレビューは Edge に戻る、設定は残る。使用中だったフォルダーはサインイン時の後片付けコマンドで削除されることを確認。※ Windows 自身がスタート メニューのタイル バックアップ（`AppListBackup`）等に Mavue.exe のパスを 21 件記録しており、これは Windows の管理するデータで Mavue は削除しない |
| MSIX（0.1.0 → 0.1.1 → 削除） | プローブ 12 PASS + SVG サムネイル KNOWN、プレビューは .x3f のみ、上段メニュー成功、閉じずに更新は 0x80073D02、強制更新 33 秒、削除後に何も残らない |

**以前の確認（2026-10-04、最終ビルド。開発用の登録をすべて外した状態から）**

| 確認 | 結果 |
|---|---|
| インストール（0.1.0） | 3 秒。登録はすべて `app-0.1.0`、上段メニューあり、署名 Valid 15/15 |
| Explorer E2E（`--explorer --explorer-restart`、操作のない状態） | 9/10。`explorer-theme` の失敗はテスト側の順序の問題（直前のモニター移動が落ち着く前に始まる）で、待つように直して 2/2 PASS |
| アプリ E2E / Quick View E2E（インストール先） | 15/15 / 21/23（既知: 注入した Ctrl+T/Ctrl+Tab の制約、`cli-quickview` 間欠。単独再実行で PASS） |
| 0.1.0 → 0.1.1 の更新 | 3 秒。PDF プレビューを Mavue にした選択・本体と Quick View の設定ファイル（内容不変）を維持、登録は `app-0.1.1` へ、Quick View は新版で再起動 |
| 上段メニュー（更新直後の初回を含む） | 成功。※ 以前の 2 回、更新直後の初回だけ E_FAIL（再現 8 回試行で再現せず）。1 回だけ再試行するように変更（効果は未確認） |
| Explorer 再起動直後の上段メニュー | **修正**: 再起動後しばらくデスクトップが ShellWindows に未登録で、初回のメニューが失敗していた。開いている Explorer ウィンドウ経由で起動するようにし、未登録の状態で成功を確認 |
| アンインストール | 2 秒。Mavue のキー 0、PDF プレビューは Edge に戻る、設定は残る。プレビュー ホストが使用中のファイルは次回サインインで削除 |
| ZIP の項目 | 505 項目、バックスラッシュ 0（`tar -tf` で確認） |

## 5. MSIX 版

### 5.1 構成

`Mavue-<ver>-x64.msix`: `AppxManifest.xml`（`Mavue.Shell.MsixPackageManifest` が生成）、`Assets\`、`Mavue\`。マニフェストで宣言: `uap:FileTypeAssociation`
（PDF・SVG・画像・動画・音声）に `desktop2:DesktopPreviewHandler`（全種類）と `desktop2:ThumbnailHandler`（PDF・SVG）、COM は `com:SurrogateServer`、
`desktop4:FileExplorerContextMenus`、`desktop:StartupTask`。パッケージ内では `--register` 等は何もしない。設定画面は「他のパッケージも登録している種類では
プレビュー/サムネイルが使われないことがある、ZIP 版はすべての種類で提供する」と表示する。

### 5.2 実機確認（2026-10-04、最終ビルド。`tools/Mavue.QuickView.Harness/scripts/msix-probe.ps1`）

| 確認 | 結果 |
|---|---|
| インストール・起動（Mavue をファイル付きで、Quick View 常駐とクライアント） | PASS。.NET・WinUI はパッケージ内から |
| パッケージ内の登録ガード、COM 3 クラス、上段メニュー（Invoke） | PASS（Quick View がパッケージから表示） |
| PDF サムネイル | PASS（Mavue） |
| SVG サムネイル / PNG・SVG・動画・音声のプレビュー ウィンドウ | Mavue は使われない（KNOWN、§5.3）。Explorer 再起動後も同じ |
| .x3f（Mavue だけが登録する種類）のプレビュー | Mavue が使われる |
| 更新 0.1.0 → 0.1.1 | Quick View や Explorer が読み込んだハンドラーの dllhost が動いていると 0x80073D02。`-ForceApplicationShutdown` で 3 秒。設定は維持 |
| 削除 | パッケージ・データ・スタート メニュー・パッケージ COM 登録とも残らない |

### 5.3 Explorer 連携の原因（調査結果）

1. **プレビュー/サムネイルのハンドラー**: パッケージの `desktop2` ハンドラーはレジストリの拡張子キーには書かれず（`HKCR\.png\ShellEx` などに無い。
   パッケージの ProgID にも ShellEx は無い）、Windows がパッケージのカタログから引く。実測では、**その種類を宣言しているパッケージが Mavue だけの場合**
   （この PC では .pdf と .x3f）に限って使われた。.png・.svg・.mp4 等はフォト・ペイント・メディア プレーヤー・Clipchamp も宣言しており、Mavue は呼ばれない。
   拡張子の既定 ProgID を一時的に Mavue のものにしても SVG サムネイルは変わらなかった。Explorer 再起動でも変わらない。
   **Mavue を既定のアプリにした場合に使われるかは未確認**（既定のアプリはプログラムから設定できず、この確認は手動で行えなかった）。
2. **代替策の検証**: 拡張子キー（HKCU）にパッケージの COM クラスを結び付ける方法は、Explorer のサムネイル ホストで `REGDB_E_CLASSNOTREG`、
   プレビューも呼ばれず**不可**（パッケージの COM クラスは従来の拡張子結び付けからは作れない）。パッケージ内から従来の HKCU 登録（DLL のパスを
   WindowsApps に向ける）を書くには制限付き機能 `unvirtualizedResources` が必要で、**アンインストール後も登録が残り**（Microsoft の文書に明記）、
   更新ごとにパスが変わる。Store では審査対象。いずれも採用しない（Preview/Thumbnail を無理に Mavue 優先へ変更しない方針とも合う）。
3. **PDF のプレビュー**: Edge が拡張子単位で登録しているため、パッケージ版では Mavue にできない（ZIP 版は設定で切替可能）。
4. **動画/音声のプレビュー（パッケージの代理プロセス）**: Explorer がパッケージの代理プロセスに渡すストリームはスレッドに縛られており、Media Foundation の
   スレッドから読むと RPC_E_WRONG_THREAD になる場合があった（不正なファイル）。Global Interface Table 経由に変えるとプレビュー ウィンドウのスレッドと
   待ち合わせてハングしたため**元に戻した**。現状、パッケージのハンドラーが動画・音声に使われる場面は無い（1.）ため影響は無いが、MSIX を正式にし、
   既定のアプリで使われることが分かった場合は対応が必要（未解決）。

影響: MSIX 版の利用者は、標準のアプリが入った Windows 11 では Explorer のプレビュー ウィンドウで Mavue を使えない（PDF サムネイル・上段メニュー・
「プログラムから開く」・Quick View は使える）。

## 6. コード署名

### 6.1 署名対象（`tools/sign-release.ps1`。すべて SHA-256 + RFC 3161 タイムスタンプ）

| 対象 | 数 | 理由 |
|---|---|---|
| `Mavue.exe`、`Mavue.QuickView.Host.exe` | 2 | 起動時の SmartScreen / Smart App Control。Quick View は低レベル キーボード フックで常駐するため署名は必須級 |
| `Mavue.*.dll`（マネージド） | 8 | 同上（Smart App Control は読み込まれる DLL も評価） |
| `Mavue.Shell.Native.dll`、`Mavue.Shell.Preview.dll`（x64）、`x86\Mavue.Shell.Preview.dll` | 3 | Explorer・prevhost・dllhost に読み込まれる |
| `pdfium.dll`、`x86\pdfium.dll` | 2 | 配布元が未署名（BSD-3 で再配布可）。Mavue として署名 |
| `Mavue.QuickView.Identity.msix`（ZIP 版の上段メニュー） | 1 | 署名必須。利用者の PC が証明書を信頼している必要 |
| `Mavue-<ver>-x64.msix` | 1 | 署名必須。Publisher（`CN=…`）は証明書のサブジェクトと一致 |
| Microsoft の DLL（.NET・Windows App SDK・WebView2・Windows SDK 投影） | — | Microsoft 署名済み。触らない（配布フォルダーで未署名の Microsoft ファイル 0 を確認） |

### 6.2 証明書の種類（購入・契約前に発行元/Microsoft の最新文書で要確認）

| 選択肢 | 向く形態 | 注意（**要確認**） |
|---|---|---|
| 公開 CA のコード署名証明書（OV、個人向けがあるか） | ZIP・MSIX（サイドロード）とも | 鍵はハードウェア トークン/HSM 保管が業界の要件と理解している。CI での使い方、費用、日本の個人が取得できる種類 |
| EV コード署名証明書 | 同上 | SmartScreen の評判の扱いが OV と違うか（現状）、費用 |
| Azure の署名サービス（Trusted Signing） | 同上（署名ツールから利用） | 個人・日本からの利用可否、本人確認の条件、費用 |
| Microsoft Store | MSIX のみ | Store が署名するため自前の証明書は不要と理解。開発者登録の条件・費用、審査（`runFullTrust` 等）。ZIP 版には使えない |

### 6.3 更新時の注意（証明書・パッケージ）

- **MSIX の Publisher は証明書のサブジェクトと同じでなければならない**。証明書の更新でサブジェクト（名前・組織名）が変わると、パッケージの
  ファミリー名が変わり**別アプリ扱い**になる（上書き更新できない、パッケージ内の設定は引き継がれない）。更新時は同じサブジェクトで取得する。
- タイムスタンプ付きの署名は証明書の期限が切れても有効。タイムスタンプ無しの署名（開発用）は期限で無効になる。
- 証明書を替えると SmartScreen の評判がリセットされうる（要確認）。
- ZIP 版の識別パッケージは Install.cmd が毎回外して入れ直すため、発行者が変わっても上段メニューは移行できる（実装上。発行者を替えての確認は未実施）。
- 秘密鍵はリポジトリ・CI ログに置かない。

### 6.4 手順

1. 証明書（またはクラウド署名）を使える状態にする。2. `tools/build-release.ps1 -Version x.y.z -Subject 'CN=…'`（または `-Thumbprint`）`-TimestampUrl <URL>`。
3. 検証: ビルドの整合チェック、`Get-AuthenticodeSignature`（Mavue のファイルが Valid）、`signtool verify /pa`、別 PC で `docs/RELEASE-CHECKLIST.md`。

## 7. ライセンス・利用条件

- 配布物の `licenses\`: Mavue（Apache-2.0 の LICENSE・NOTICE）、THIRD-PARTY-NOTICES、PDFium と同梱部品、.NET、Windows App SDK、WebView2。アプリの 設定 › Mavue について › ライセンス情報。
- Windows SDK の C# 投影は再配布可（REDIST 一覧、確認済み）。
- **エンドユーザー向け利用条件（EULA 相当）**: Windows App SDK と Windows SDK の再配布条件で必要。RC では `licenses/EULA.txt`（日英、
  **法務確認前の草案**と明記、【要法務確認】の箇所あり）を配布物の `licenses\EULA.txt` に同梱し、Install.cmd の完了表示と README.txt で案内する。
  同意の取得（インストール時の同意画面など）は同意の取り方が法務判断のため実装していない。論点は `docs/EULA-DRAFT.md`。
- ネイティブ DLL のビルドに使う **Visual Studio Build Tools の使用条件**（Visual Studio の有効なライセンスが必要）はユーザーが確認する（`docs/DEPENDENCIES.md` §0）。

## 8. 移行・アップデート手順

| 場面 | 手順 | 確認状況 |
|---|---|---|
| ZIP 版の更新 | 新しい ZIP を展開して `Install.cmd` | 0.1.0 → 0.1.1 で確認（設定・PDF 選択を維持） |
| MSIX 版の更新 | Mavue・Quick View を終了してから新しい `.msix`（Explorer が Mavue のハンドラーを読み込んでいると失敗。`Add-AppxPackage -ForceApplicationShutdown` なら可） | PowerShell で確認。App Installer（ダブルクリック）は未確認 |
| ZIP 版 → MSIX 版 | ZIP 版をアンインストール → MSIX をインストール。設定は `%LOCALAPPDATA%\Mavue` をそのまま使う | 各段階を確認 |
| MSIX 版 → ZIP 版 | MSIX を削除 → `Install.cmd`。MSIX が新規に作った設定（パッケージ内）は消える | 中止動作と設定の場所を確認 |
| 開発ビルド → 配布版 | `Install.cmd` が HKCU 登録と識別パッケージを上書き・再登録する | 確認 |

## 9. 配布前に人が行う作業

1. 正式配布形態の決定（§1 の比較。MSIX は §5.3 の制約を受け入れるか）。可能なら **Mavue を既定のアプリにした MSIX 版でプレビューが使われるか**を手動で確認（`docs/RELEASE-CHECKLIST.md`）。
2. 署名証明書（または署名サービス、Store）の選定・購入・契約（§6.2 の要確認事項）。
3. 利用条件（`docs/EULA-DRAFT.md`）の確定と法務確認、提示方法の決定。
4. Visual Studio Build Tools の使用条件の確認。
5. 別のクリーンな PC（VM）での `docs/RELEASE-CHECKLIST.md` の実施、ARM64 機での確認。
6. 配布場所（Web・GitHub Releases・Store）と、.NET / Windows App SDK のセキュリティ更新に合わせた再リリースの運用。
