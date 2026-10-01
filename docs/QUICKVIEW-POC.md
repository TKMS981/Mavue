# Quick View 最小 PoC 検証報告

最終更新: 2026-10-02
対象: `src/Mavue.QuickView`（ライブラリ）、`src/Mavue.QuickView.Host`（常駐 WinUI 3 プロセス）、`tools/Mavue.QuickView.Harness`（実機 E2E ハーネス）

> 本書では **「実測」**（本機 Windows 11 で実際に観測した値・挙動）と **「推測」**（観測から導いた解釈・未検証の見込み）を明確に区別する。
> 数値はすべて **Debug ビルド・JIT 実行**（ReadyToRun / NativeAOT なし）での値。

---

## 1. 検証環境（実測）

| 項目 | 値 |
|---|---|
| OS | Windows 11 Home 25H2, build 26200.9550, x64 |
| CPU / GPU / RAM | Ryzen 7 7800X3D / RTX 4080 SUPER（+ Radeon iGPU、Parsec / Meta 仮想ディスプレイあり）/ 63 GB |
| .NET / Windows App SDK | .NET 10.0.401（ユーザーローカル）/ 2.5.1（自己完結） |
| 前面ロックタイムアウト | `SPI_GETFOREGROUNDLOCKTIMEOUT` = 2147483647 ms（実質無期限 = 最も厳しい条件） |
| 本機の他の Space フック製品 | QuickLook 4.5.0（MSIX）。**作業開始時は実行中だったが、計測時には終了していた**（理由不明・こちらは終了させていない）。共存は未検証 |
| Explorer | 「フォルダーを別プロセスで開く」= 無効（`SeparateProcess=0`） |

## 2. 実装した PoC の構成

```
[Explorer で Space]
   │ WH_KEYBOARD_LL（専用スレッド）: SpaceKeyClassifier（既存・無変更）+ TypingTracker（IME/タイプアヘッド）
   │   → 対象なら Space を握りつぶし、キューに投入（フック内では COM を呼ばない）
   ▼
[選択取得] 専用 STA スレッド: IShellWindows → IShellBrowser → IShellView → IFolderView2::GetSelection
   ▼
[表示] UI スレッド: 事前生成済みウィンドウを "panel" 方式で表示（§4）
   ▼
[段階表示] ①ファイル情報 → ②Windows サムネイルキャッシュ（専用 STA、INCACHEONLY）→ ③フル品質（WinRT BitmapDecoder、画面サイズへ縮小デコード / PDF は Windows.Data.Pdf）→ ④差し替え
[閉じる] Esc / Space（フック経由）、他アプリへの切替で自動クローズ（EVENT_SYSTEM_FOREGROUND）
[計測] QuickViewTimeline: QPC タイムスタンプを ETW（EventSource "Mavue-QuickView"）と JSON Lines に出力
```

## 3. 実測で判明した Windows の挙動

### 3.1 Shell COM はスレッドアパートメントに依存する（実測）

| 呼び出し元 | `IShellBrowser::GetWindow`（Explorer への越境 COM） |
|---|---|
| MTA スレッド | **失敗** `0x8001010D` RPC_E_CANTCALLOUT_ININPUTSYNCCALL |
| STA スレッド | 成功 |

→ 選択取得は専用 STA スレッドで行う。`ExplorerSelectionProvider` は STA 以外では例外を投げる（誤用の早期検出）。

- Windows 11 のタブ: 各タブが `ShellTabWindowClass` と独自の `SHELLDLL_DefView` を持つ（実測）。フォーカス中アイテムビューの親 DefView と `IShellView::GetWindow` を照合して、アクティブタブを正確に特定できる。
- Explorer のファイル一覧のフォーカスクラスは `DirectUIHWND`、親は `SHELLDLL_DefView`（25H2 で実測）。既存の判定ロジックの前提どおり。
- シェルサムネイル（`IShellItemImageFactory`, `SIIGBF_INCACHEONLY`）は MTA/STA どちらでも動作。**プロセス内初回のみ約 60 ms、以降 4〜7 ms**（実測）→ 起動時にプリウォームする。キャッシュに入っていたのは 256px（Explorer の表示サイズ依存）。

### 3.2 前面表示（最重要）

#### 比較した方式

| 方式 | 内容 |
|---|---|
| `setforeground` | `AppWindow.Show(true)` + `SetForegroundWindow` |
| `appwindow` | `AppWindow.Show(true)` のみ |
| `hookgrant` | フックのコールバック内（入力処理中）で `AllowSetForegroundWindow(自プロセス)` → `SetForegroundWindow` |
| `attach` | `AttachThreadInput` で前面スレッドと入力キューを結合して `SetForegroundWindow`（比較用ハック） |
| `noactivate` | `SetWindowPos(HWND_TOP, SWP_NOACTIVATE)`。前面化しない |
| `noactivate-topmost` | 一瞬だけ `HWND_TOPMOST` にしてすぐ解除（比較用） |
| **`panel`（採用）** | `SetWindowPos(HWND_TOPMOST, SWP_NOACTIVATE)`。**表示中だけ最前面帯**、前面化しない、他アプリへ切替で自動的に閉じ、閉じる前に Topmost を解除 |

`AllowSetForegroundWindow` を「Explorer 側から呼んでもらう」方式は、Explorer 内でコードを実行する手段がないため Space 経路では適用不可（コンテキストメニュー経由なら、Explorer 内で動く `IExplorerCommand` が呼べるので有効。§6）。

#### 結果（実測）

**(a) ユーザーの実操作（最も信頼できるデータ）**

| 入力 | 方式 | 結果 |
|---|---|---|
| 物理キーボード（`injected:false`、11 回） | `auto`（hookgrant + 失敗時は非アクティブ表示） | フック内の権利確保は **2/11 回しか成功せず**（成功した 2 回は、ユーザーがタスクバーから Mavue を前面にした直後）。失敗時はタスクバーが点滅し、非アクティブのまま表示 → ユーザー評価「問題あり」 |
| リモート操作（Parsec 経由、`injected:true`） | `noactivate` | **Space 直後から Explorer の後ろに表示** → ユーザー評価「問題あり」 |
| リモート操作（同上、15 回の操作） | **`panel`** | 表示 8/8 回すべて Explorer の上（`aboveOwner=True`）。Space→Space、Esc、Alt+Tab による自動クローズもすべて期待どおり → ユーザー評価「**問題なし**」 |

**(b) 自動ハーネス（SendInput による注入入力）**

- ハーネスがホストを**自分の子プロセスとして起動**した場合や、ハーネスを Bash から起動した場合は、`setforeground` 以外のほぼ全方式が成功した（例: `hookgrant` 19/19、`noactivate` 25/27）。
- しかし、**ホストを独立に起動し、ハーネスは接続するだけ**の条件で、ユーザー報告と同じ失敗が再現した: `noactivate` **0/12 で Explorer の後ろ**、同条件の `panel` は **12/12 で上に表示**。
- 同じ条件の再試行では `noactivate` も成功した（6/6）。→ **前面化や z 順の結果は、入力元（物理・リモート・注入）、プロセスの起動元や履歴、リモート操作ツールの有無など、ハーネスでは制御しきれないシステム状態に左右される**（実測）。具体的に何が決め手かは特定できていない（推測の域）。
- 自動で前面を取るアプリ（本環境ではターミナルの Warp）が計測中に前面化し、数件の計測が乱れた（実ユーザー入力は 0 件と記録）。ハーネスは「キー送信直前に前面ウィンドウを確認し、違えば送らない」ガードでこれを検出した。

**結論**: 自動テストで成功しても、実環境で成功するとは限らない。前面化の権利（`SetForegroundWindow` / `AllowSetForegroundWindow`）にも、権利がないと効かない z 順の引き上げ（`HWND_TOP`）にも依存しない方式が必要。**Topmost 帯への配置は権利不要の正規の仕組み**であり、実ユーザー入力で唯一安定した `panel` 方式を採用する。

#### `panel` 方式の UX 仕様（採用）

| 操作 | 挙動 | 検証 |
|---|---|---|
| Explorer で Space | プレビューが Explorer の上に浮かぶ。**Explorer がキーボードフォーカスを保持**（矢印キーなどは Explorer に届く） | ユーザー実操作 + ハーネス 27/27 |
| Esc | フック経由で閉じる（Explorer が前面で、Quick View が非アクティブ表示中の場合のみ Esc を横取り） | ユーザー実操作 + ハーネス |
| Space（表示中） | 同じファイルなら閉じる（トグル） | ユーザー実操作 6 回 + シナリオ |
| Explorer をクリック | プレビューは開いたまま（Explorer は持ち主） | シナリオ |
| プレビューをクリック | 通常のアクティブウィンドウになり、以降のキーはプレビューが受ける | 実装済み・**ユーザー未確認** |
| 他アプリへ切替 / Alt+Tab | プレビューは自動で閉じ、Topmost も解除。他アプリの上に残らない | ユーザー実操作 2 回 + シナリオ |
| タスクバーの点滅 | 前面化を試みないので発生しない | ユーザー評価「問題なし」 |

「強制 Topmost は避ける」という要件との関係: `panel` の Topmost は (1) フォーカスを奪わない、(2) Quick View が表示されている間だけ、(3) 持ち主の Explorer 以外が前面になった瞬間に閉じる、という範囲に限定しており、ユーザー操作を奪わない。macOS の Quick Look パネル（Finder の上に浮くパネル）と同等の振る舞いである。**この判断はユーザーの最終承認事項**として報告する。

### 3.3 レイテンシ（実測・`panel`・Debug・コールド条件、各 3 回の中央値、Space 注入時刻を 0 とする）

| 形式 | 選択取得 | 表示呼び出し完了 | 最初のフレーム | サムネイル表示 | フル品質表示 | 画面上で画素を検出 | デコード | ピーク WS |
|---|---|---|---|---|---|---|---|---|
| JPEG 800×600 | 5.0 ms | 10.1 | 13.6 | （先にフル品質） | **20.7** | 51.9 | 3.2 | 146 MB |
| JPEG 6000×4000（24 MP） | 3.8 | 8.3 | 12.2 | 20.9 | 103.2 | 54.8 | 79.4 | 205 MB |
| PNG 4000×3000 | 5.1 | 11.8 | 16.8 | 27.8 | 152.0 | 70.7 | 132.4 | 271 MB |
| WebP 1600×1200 | 4.1 | 9.0 | 12.4 | 23.0 | 72.1 | 55.8 | 44.9 | 278 MB |
| AVIF 1600×1200 | 3.7 | 8.0 | 11.3 | 28.9 | 61.5 | 54.0 | 37.5 | 278 MB |
| HEIF 4000×3000 | 3.4 | 7.4 | 11.1 | 17.5 | 337.4 | 52.0 | 322.9 | 460 MB |
| PDF（1 ページ, Windows.Data.Pdf） | 3.6 | 7.8 | 10.7 | –（キャッシュなし） | 33.5 | 62.2 | 13.7 | 530 MB |
| JPEG 16000×12000（192 MP） | 3.2 | 7.6 | 11.2 | 17.7 | 451.0 | 49.7 | 438.3 | 530 MB |
| PNG 10000×10000（100 MP） | 3.6 | 7.8 | 10.0 | 21.9 | 755.0 | 24.4 | 733.2 | 530 MB |

- フック内の処理は 0.0〜0.6 ms（QPC 差）。
- 「画面上で画素を検出」はハーネスが `GetPixel` で画面をポーリングした値で、DWM の合成と `GetPixel` 自体のコスト（数十 ms 単位）を含む粗い上限値。アプリ内の「最初のフレーム」「サムネイル表示」「フル品質表示」は `CompositionTarget.Rendering` 時点。
- **白画面で待つことはない**: 大きな画像でも Space 後 11〜17 ms でウィンドウが描画され、17〜29 ms でキャッシュ済みサムネイルが表示され、その後フル品質に差し替わる（実測）。
- ピーク WS はプロセス生存中の最大値なので、表では後の行ほど前のケースを含む。単独計測は §3.5。

### 3.4 デコード方式の比較（実測、フル品質表示までの中央値）

| 画像 | WinRT Fant（既定） | WinRT Linear | WinRT Nearest | WinRT Cubic | XAML DecodePixelWidth |
|---|---|---|---|---|---|
| JPEG 24 MP | 110 ms | 83 | 91 | 244 | **78** |
| HEIF 12 MP | 345 | 334 | **274** | 415 | 547 |
| JPEG 192 MP | 454 | 318 | 321 | 442 | **240** |
| PNG 100 MP | 765 | **703** | 702 | 784 | 779 |

- WinRT 経路では、JPEG の DCT 段階縮小（`IWICBitmapSourceTransform`）が効いていないと**推測**される（192 MP で 240 ms 以上かかる）。WIC を直接使う経路（ソース変換で 1/2〜1/8 にデコード → Fant で仕上げ）は次の検証課題。
- PoC の既定は画質優先で Fant のまま（サムネイル → フル品質の段階表示で体感速度を確保）。

### 3.5 巨大画像のメモリ（実測、画像ごとに新しいホストプロセスで計測）

| 画像（全展開した場合の BGRA サイズ） | WinRT 経路のピーク WS | XAML 経路のピーク WS |
|---|---|---|
| JPEG 800×600（ベースライン） | 140 MB | 140 MB |
| JPEG 192 MP（768 MB） | **176 MB** | 174 MB |
| PNG 100 MP（400 MB） | **265 MB** | 361 MB |

→ どちらも全画素を一括でメモリに展開してはいない（画面サイズへの縮小デコード）。PNG は形式上ソース側で縮小できないため、行単位の処理で増加分が出る。1 ギガピクセル超は事前に拒否する（`PreviewSafetyPolicy.MaxSourcePixels`）。

### 3.6 起動とプリウォーム（実測）

| 項目 | プリウォームあり | なし |
|---|---|---|
| プロセス起動 → 準備完了（launched→ready） | 約 160〜170 ms | 約 62〜70 ms |
| 起動直後の最初の Space: 表示呼び出し完了 | 36〜38 ms | 52〜56 ms |
| 同: フル品質表示（小さい JPEG） | 85〜89 ms | 102〜116 ms |
| 2 回目以降の Space: フル品質表示（小さい JPEG） | 約 20〜35 ms | — |

プリウォームの内容: 非表示ウィンドウをクローク状態で 1 フレーム描画、`IShellWindows` への接続、シェルサムネイル経路の初期化。

### 3.7 物理キー・実操作で観測したその他の挙動（実測）

- 音声（.wav, .ogg）は「デコーダーなし」と表示（音声・動画プレビューは未実装）。フォルダーを選んで Space を押した場合も落ちずに処理された。
- 実操作中、QuickLook は起動しておらず二重表示は発生しなかった（共存は未検証）。

## 4. 確認できていないこと（推測・未検証）

| 項目 | 状況 |
|---|---|
| 物理キーボードでの `panel` 方式 | 未確認（`panel` のユーザー確認はリモート操作での実施）。物理キーで失敗したのは前面化の権利に依存する方式であり、`panel` は権利に依存しないため同じ結果になると**推測** |
| QuickLook との共存（二重表示） | 未検証（計測時に QuickLook が動いていなかった） |
| デスクトップ上のファイル / ファイルダイアログ | 実装済み・未検証 |
| 複数選択・前後ナビゲーション | 未実装（先頭の選択項目のみ表示） |
| 「フォルダーを別プロセスで開く」設定がオンの環境 | 未検証（設定変更が必要なため実施せず） |
| 昇格（管理者）Explorer | 未検証（UIPI によりフック不可の見込み） |
| 長時間常駐（フックのタイムアウト解除、メモリ増加） | 未検証 |
| Release / ReadyToRun / NativeAOT での起動時間 | 未計測 |
| 高 DPI・マルチモニタ間の移動 | 未検証 |
| GIF アニメーション、RAW（サンプルなし）、HEIC 実写、JPEG XL | 未検証（GIF は先頭フレームのみ表示の見込み） |
| クラウドプレースホルダー、UNC、壊れた画像・悪意ある PDF | 判定ロジックは単体テスト済み。実ファイルでの E2E は未実施 |
| パスワード付き PDF | 処理経路は実装済み・未検証 |

## 5. 設計への反映

| 変更前 | 変更後 | 理由（実測） |
|---|---|---|
| Space で Quick View を**アクティブ化**して前面に出す | **`panel`**: 非アクティブのまま表示中だけ最前面帯に浮かべる。Explorer がキーボードを保持し、Esc/Space はフックで処理 | 前面化の権利は実ユーザー入力では得られない（物理キーで 9/11 失敗）。`HWND_TOP` も権利がないと Explorer の上に出られない（リモート操作で後ろに表示） |
| 選択取得を MTA ワーカーで実行 | 専用 STA スレッド | MTA では `RPC_E_CANTCALLOUT_ININPUTSYNCCALL` |
| サムネイル取得をスレッドプールで実行 | 専用 STA スレッド + 起動時プリウォーム | 初回 60 ms のシェル初期化を Space の経路から除外 |
| 常駐ホストの出力名 `Mavue.QuickView.exe` | `Mavue.QuickView.Host.exe` | ライブラリ `Mavue.QuickView.dll` と同じフォルダでファイル名が衝突 |
| E2E でホストをハーネスの子プロセスとして起動 | WMI 経由で独立に起動（`--child-host` で旧挙動） | 子プロセス起動は前面化の結果を有利に歪める |

## 6. 今後の課題（Quick View 関連）

1. 物理キーボードでの `panel` 方式の確認、QuickLook 共存時の挙動確認（ユーザー協力が必要）。
2. 矢印キーでの前後移動: `panel` では矢印は Explorer に届くので、**Explorer の選択変更に追従**する設計にする（macOS の Finder + Quick Look と同じ）。選択変更の検知方式（`DWebBrowserEvents2` / `IFolderView` のポーリング / UI Automation イベント）を比較する。
3. WIC 直接デコード（DCT 縮小）で巨大 JPEG を高速化。
4. Release + ReadyToRun / NativeAOT で常駐プロセスの起動時間とメモリを測定（現状 Debug で WS 約 140 MB）。
5. コンテキストメニュー「Mavue Quick View」経由の表示（Explorer 内の `IExplorerCommand` が `AllowSetForegroundWindow` を呼べるため、こちらはアクティブ化も可能）。
