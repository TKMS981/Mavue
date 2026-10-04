# Mavue 別 PC での配布確認チェックリスト（RC）

開発 PC では確認できない項目（署名証明書を信頼していない PC、標準のアプリ構成、他のユーザー・言語など）を、公開前に**別のクリーンな PC または VM** で確認する。
仕様と開発 PC での結果は `docs/PACKAGING.md`（§4.2・§5.2）。結果は末尾の記録欄と `docs/TESTING.md` に残す。

凡例: ★ = 開発 PC では確認できていない項目（未確認）。

## 0. 準備

- [ ] Windows 11（最新の一般向け更新）のクリーンな VM/PC。可能なら Windows 10 2004 以降も 1 台。★ ARM64 機（ARM64 版を作る場合）。
- [ ] ★ 開発用証明書（`CN=Mavue Dev`）を入れていない。開発者モードはオフ。
- [ ] 日本語表示と英語表示のユーザー、★ 管理者でない標準ユーザー、★ ユーザー名（`C:\Users\…`）に日本語やスペースを含むユーザー（可能なら）。
- [ ] DPI の違うモニター 2 台（可能なら）。★ Smart App Control が有効な PC（可能なら）。
- [ ] 配布物をブラウザーでダウンロードする（Web のマークを付けるため）。`SHA256SUMS.txt` と `Get-FileHash` が一致する。
- [ ] 実リリース用の証明書で署名した成果物か、RC（開発用証明書）かを記録する。

## 1. ZIP 版（第一配布候補）

インストール
- [ ] エクスプローラーで「すべて展開」。展開先を 3 通り試す: ダウンロード フォルダー、**日本語とスペースを含むフォルダー**（例: `C:\テスト フォルダー\展開 先`）、深いパス。
- [ ] `Install.cmd` をダブルクリック: ★ SmartScreen / Smart App Control の表示（発行元名）を記録。管理者権限を求められない。メッセージが表示言語（日本語/英語）で出る。
- [ ] 完了表示に「利用条件: …\licenses\EULA.txt」。スタート メニューに Mavue。設定 › アプリ › インストールされているアプリに Mavue（版・発行元）。
- [ ] `%LOCALAPPDATA%\Programs\Mavue\app-*\` の `Mavue*.exe/dll`・`pdfium.dll` が `Get-AuthenticodeSignature` で Valid、発行元が期待どおり。Web のマーク（Zone.Identifier）が残っていない。
- [ ] `licenses\` に LICENSE.txt・NOTICE.txt・THIRD-PARTY-NOTICES.txt・EULA.txt・pdfium\・dotnet\・windowsappsdk\・webview2\。

起動・Quick View
- [ ] Mavue で画像（JPEG・PNG・HEIC/WebP/AVIF は拡張機能がある場合）・GIF・SVG・PDF（大きいもの・パスワード付き）・動画・音声を開ける。設定 › Mavue について のバージョンと「ライセンス情報」。
- [ ] Explorer でファイルを選んで **Space** → Quick View。矢印で次のファイル、Esc で閉じる。PDF のページ移動、動画の再生。別のモニター。
- [ ] **Explorer を再起動した直後**（タスク マネージャーで「エクスプローラー」を再起動）に Space と右クリック「Mavue Quick View」が動く。
- [ ] サインアウト → サインインで Quick View が常駐している（設定 › アプリ › スタートアップ）。

Explorer 連携
- [ ] 「プログラムから開く」と既定のアプリの候補に Mavue。**既定のアプリが変わっていない**。
- [ ] プレビュー ウィンドウ: PNG・JPEG・GIF・SVG・MP4・MP3/WAV が Mavue。PDF は既存（Edge 等）のまま。壊れたファイルでメッセージ。
- [ ] 設定 › Windows との統合 で PDF プレビューを Mavue に → PDF が Mavue。戻すと元に戻る。
- [ ] サムネイル（大アイコン表示）: PDF・SVG が表示される。JPEG 等は Windows のまま。
- [ ] 32 ビット アプリのファイル ダイアログでプレビュー/サムネイル。
- [ ] 右クリック: Windows 11 上段の「Mavue Quick View」（★ 実リリース用の署名時）。★ **証明書が信頼されない PC** では従来メニュー（その他のオプションを確認）に出る。「Mavue で開く」。
- [ ] ダーク/ライト モード、高 DPI（150%・200%）でプレビューと Quick View が正しく表示される。

更新・アンインストール
- [ ] 外観・表示サイズを変更し、PDF プレビューを Mavue にした状態で、新しい版の ZIP の `Install.cmd` → 設定と PDF プレビューの選択が維持される。古い `app-*` は使用中でなければ消える。
- [ ] アンインストール（設定 › アプリ）: `HKCU\Software\Classes` の Mavue 関連、App Paths、Run、スタート メニュー、`%LOCALAPPDATA%\Programs\Mavue` が残らない
      （使用中のファイルは次回サインイン後に消える）。PDF 等のプレビューが元に戻る。設定ファイル `%LOCALAPPDATA%\Mavue` は残る。
- [ ] アンインストール後に再インストールできる。

## 2. MSIX 版（将来の Store・自動更新向け候補）

- [ ] `.msix` をダブルクリック（App Installer）: 発行元表示。★ 実リリース用の署名でインストールできる（信頼されない署名ではできないこと）。
- [ ] スタート メニューから起動。「プログラムから開く」に Mavue。Space と右クリックの「Mavue Quick View」。
- [ ] ★ サインイン時に Quick View が常駐するか（StartupTask の初回動作、設定 › アプリ › スタートアップの表示）。
- [ ] プレビュー ウィンドウ / サムネイル: どの種類で Mavue が使われるかを記録（開発 PC では .x3f のプレビューと PDF のサムネイルのみ。`docs/PACKAGING.md` §5.3）。
- [ ] ★ **手動**: 設定 › アプリ › 既定のアプリ で .svg と .png を Mavue にしたとき、プレビュー/サムネイルが Mavue になるか。確認後は元に戻す。
- [ ] 新規 PC で設定がパッケージ内（`%LOCALAPPDATA%\Packages\Mavue_*\LocalCache\Local\Mavue\`）に作られる。
- [ ] ★ 更新: 新しい版の `.msix` をダブルクリック（Mavue・Quick View 起動中、Explorer でプレビューした後）→ App Installer の表示と結果。
- [ ] アンインストール後、スタート メニュー・Explorer の項目・パッケージ データが残らない。

## 3. 共通

- [ ] ZIP 版を入れたまま MSIX を入れない（Install.cmd は MSIX があると中止する。逆方向は手順で防ぐ）。
- [ ] 大きなファイル（24 MP 以上の画像、600 ページの PDF）、壊れたファイルで異常終了しない。
- [ ] ネットワーク通信が無い（利用条件のプライバシーの記述と一致。ファイアウォールのログや Resource Monitor）。
- [ ] `licenses\THIRD-PARTY-NOTICES.txt` の一覧と `licenses\` の内容が一致する。

## 4. 記録欄

| 日付 | PC / OS / 言語 | 成果物（版・署名） | 結果（失敗した項目と内容） | 確認者 |
|---|---|---|---|---|
| | | | | |
