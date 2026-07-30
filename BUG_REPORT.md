# MyTaskTray 不具合調査レポート

対象: 2026-07-30 時点のソース全体（`*.cs` / `*.xaml`）を静的レビュー。
サンドボックスに .NET SDK がないため実行確認はしておらず、コード読解による指摘です。

## 対応状況

| # | 内容 | 状況 |
| --- | --- | --- |
| 1 | 設定画面を開いている間のコピーで連番が巻き戻る | **修正済み** |
| 2 | `{calc:}` に文字列の差し込みを入れると誤った数値になる | **修正済み** |
| 3 | 単位の書き間違いが黙って無視される | **修正済み** |
| 4 | `{seq}` / `{guid}` / `{random}` のオフセットが無視される | **修正済み** |
| 5 | トップレベルの区切り線が整理されない | **修正済み** |
| 6 | メニューを開いたままテーマが切り替わるとクラッシュしうる | **修正済み** |
| 7 | エスケープした `{{seq}}` でも連番が進む | **修正済み** |
| 8 | 空文字列でも「コピーしました」と通知される | **修正済み** |
| 9 | サロゲートペアの途中で文字列が切られる | **修正済み** |
| 10 | 終了時に未保存確認ダイアログのキャンセルが効かない | **修正済み** |
| 11〜20 | 下記参照 | 未対応 |

---

## 優先度: 高（修正済み）

### 1. 設定画面を開いている間にコピーすると、連番が巻き戻る

- `TrayIconManager.ShowSettingsWindow()` は `_settings.Clone()`（開いた時点のスナップショット）を渡す。
- 設定画面が開いている間にトレイから `{seq}` 項目をコピーすると、`CopyToClipboard()` が
  `AdvanceSequence()` → `SettingsStore.Save(_settings)` でファイルを更新する。
- そのあと設定画面で「保存して閉じる」を押すと、**古い `SequenceValue` でファイル全体を上書き**する。
  → 連番が戻り、同じ番号が二重に発行される。
- `ClipItem.Id` は README（`| Id | 項目の識別子（自動生成。連番の引き継ぎに使用） |`）と
  コード内 XML コメント（「連番カウンターの引き継ぎに使う」）で引き継ぎ用と説明されているが、
  **Id を読んでいる箇所が存在しない**（代入と `Clone` のみ）。機能が未実装。

**対応**: `SettingsWindow.AdoptExternalSequenceValues()` を追加。保存直前に `SettingsStore.Load()` で
ファイルを読み直し、`Id` が一致する項目の `SequenceValue` をファイル側の値で取り込む。
ただし画面上で「次の番号」を直接編集した項目はユーザーの指定を優先するため、
`SettingsViewModel.SequenceEditedIds` で編集済みの `Id` を記録して除外する。
これで `Id` の本来の役目（連番の引き継ぎ）が実装された。

参照: `SettingsWindow.xaml.cs` の `TrySave()` / `AdoptExternalSequenceValues()`、
`ViewModels/SettingsViewModel.cs` の `SequenceEditedIds` / `OnAnyItemPropertyChanged()`

---

### 2. `{calc:}` に文字列の差し込みを入れると、エラーにならず誤った数値になる

`{calc:{date}*2}` の場合:

1. 内側を展開 → `2026/07/30*2`
2. `ExpressionEvaluator` は `/` を除算として解釈 → `2026 / 7 / 30 * 2` = `19.2952380952`

README には「文字列の差し込みは数値として読めないため…」とあるが、実際は
**読めないのではなく黙って別の数値になる**。ユーザーは誤りに気付けない。

**対応**: `Expand()` に `numericOnly` を追加し、`{calc:…}`（`{=…}`）の式の中を展開するときだけ
**各差し込みの展開結果が数値として読めるか**を `LooksLikeNumber()` で確かめる。
数値でなければ例外にして、`{calc:…}` を書いたまま残す。
名前の白リストではなく展開結果で判定するため、`{calc:{date:yyyyMMdd}*2}` のように
数値だけになる書式は今までどおり使える。

参照: `Services/TemplateEngine.cs` の `Expand()` / `ExpandCalc()` / `LooksLikeNumber()`

---

### 3. 単位の書き間違いが黙って無視される

`TemplateEngine.Shift()` の `switch` は未知の単位を `_ => value` で捨てている。

| 入力 | 期待 | 実際 |
|---|---|---|
| `{date+1m}` | 1 か月後（正しくは `mo`） | **今日**（オフセット消失） |
| `{date+3M}` | 3 か月後 | **今日** |
| `{time+30min}` | 30 分後（正しくは `mi`） | **現在時刻** |

エンジンの他の箇所は「解釈できない差し込みは書いたまま残して気付けるようにする」方針なのに、
ここだけ黙って成功したように見えるため一貫していない。

**対応**: `Shift()` の `_ => value` を `FormatException` に変更。
`ExpandToken()` の既存の `catch (Exception)` が拾って `original` を返すため、
書き間違えた差し込みはそのまま文字列として残り、ユーザーが気付ける。
`{date+0mo}` のように offset が 0 でも単位が付いている場合を検査できるよう、
早期 return の条件も `offset == 0 && 単位が空` に修正。

参照: `Services/TemplateEngine.cs` の `Shift()`

---

### 4. `{seq}` / `{guid}` / `{random}` のオフセットが無視される

- `SequenceRegex` は `\{seq(?:[+\-]\d+[A-Za-z]*)?...\}` とオフセットを許容し、
  `InnerRegex` も `sign`/`num` を解析している。
- しかし `case "seq":` は `FormatSequence(sequenceValue, format)` で **offset を使っていない**。
  → `{seq+1}` と `{seq}` が同じ結果になる。`{guid+1}` `{random+5}` も同様。

**対応**:

- `{seq+1}` は「次の番号 + 1」として**実装**（`sequenceValue + offset`）。
  `No.{seq}〜No.{seq+4}` のような範囲の書き方ができるようになった。
  差し込み挿入パネルと README の一覧にも追加。単位（`{seq+1d}`）は意味を持たないため誤り扱い。
- `{guid}` / `{random}` はオフセットに意味がないため、付いていたら `RejectOffset()` で
  誤りとして扱い、書いたままを残す。

参照: `Services/TemplateEngine.cs` の `ExpandToken()` / `RejectOffset()`

---

### 5. トップレベルの区切り線が整理されない

`TrimEdgeSeparators()` はカテゴリのサブメニューにしか適用されていない（`categories.Values` のみ）。

- 一覧の先頭が区切り線 → メニューの一番上に線が出る
- 一覧の末尾が区切り線 → `RebuildMenu()` の
  `if (menu.Items.Count > 0) menu.Items.Add(new ToolStripSeparator());` と重なって**線が二重**になる
- トップレベルで区切り線を 2 つ連続させても、そのまま 2 本描かれる

**対応**: `RebuildMenu()` で `BuildClipItems()` の直後、区切り線を足す前に
`TrimEdgeSeparators(menu.Items)` を呼ぶようにした。
あわせて `TrimEdgeSeparators()` が取り除いた `ToolStripItem` を `Dispose()` するようにし、
メニューを作り直すたびに使われない項目が残らないようにした。

参照: `TrayIconManager.cs` の `RebuildMenu()` / `TrimEdgeSeparators()` / `RemoveAndDispose()`

---

## 優先度: 中（修正済み）

### 6. メニューを開いたままテーマが切り替わるとクラッシュしうる

`ThemeManager` の `UserPreferenceChanged` → `Dispatcher.BeginInvoke(Apply)` → `ThemeChanged`
→ `TrayIconManager.OnThemeChanged()` → `RebuildMenu()` → `old?.Dispose()`。

トレイメニューが表示中でも `Dispose()` されるため、`ObjectDisposedException` の可能性がある。

**対応**: `DisposeMenu()` を追加。表示中なら `Closed` を待ち、さらに Dispatcher で
いったん制御を戻してから破棄する（`Closed` の中はまだ閉じる処理の途中のため）。
10 番の修正で「終了時に設定画面を保存 → `ReloadSettings()` → `RebuildMenu()`」という
経路が増えたが、これも同じ仕組みで安全になった。

参照: `TrayIconManager.cs` の `RebuildMenu()` / `DisposeMenu()`

---

### 7. エスケープした `{{seq}}` でも連番が進む

`ContainsSequence()` は生テキストに正規表現をかけるだけなので、
リテラル `{seq}` を出力するための `{{seq}}` も「連番あり」と判定される。
→ コピーごとに `AdvanceSequence()` が走り、設定ファイルへの書き込みも毎回発生する。
設定画面の「連番」バッジと連番設定パネルも誤って表示される。

**対応**: 正規表現による判定をやめ、`Expand()` と同じ走査（エスケープを飛ばし、
`FindClosingBrace()` で対応する `}` を探す）で本物の差し込みだけを見るようにした。
判定自体は `InnerRegex()` の解析結果の名前が `seq` かどうかで行うため、
展開処理とルールが二重管理にならない。`{calc:{seq}*100}` のような入れ子も再帰でたどる。
専用の `SequenceRegex` は不要になったので削除。

参照: `Services/TemplateEngine.cs` の `ContainsSequence()` / `IsSequenceToken()`

---

### 8. 空文字列でも「コピーしました」と通知される

`ClipboardService.TryCopy("")` は `Clipboard.Clear()` を呼んで `true` を返す。
呼び出し側はこれを成功として扱うため、**クリップボードを空にしたのに
「コピーしました」とトーストが出る**。名前も内容も空の項目は保存できてしまうので
（`TrySave()` は `Name` が空なら `Text.Trim()` を入れるだけ）、
メニューに「空白のクリックできる行」が並ぶ状態も作れる。

**対応**:

- 展開結果が空のときは通知文を「クリップボードを空にしました / コピーする文字列が空の項目です」に変え、
  実際の動作をそのまま伝えるようにした。
- 名前もコピー文字列も空の項目は、メニューのラベルを `(空の項目)` にして
  「クリックできるのに何も見えない行」にならないようにした。

参照: `TrayIconManager.cs` の `CopyToClipboard()` / `CreateClipMenuItem()`

---

### 9. サロゲートペアの途中で文字列が切られる

`TrayIconManager.Truncate()` と `TemplateEngine.ToSingleLine()` は
`oneLine[..maxLength]` で切っている。絵文字や一部の漢字（サロゲートペア）が
境界に来ると文字が壊れて `?` のように表示される。
メニューのラベル・ツールチップ・一覧のプレビューすべてが対象。

**対応**: `TemplateEngine.Truncate()` を追加し、切る位置の直前が上位サロゲートなら
1 つ手前で切るようにした。`ToSingleLine()` と `TrayIconManager.Truncate()` の両方が
これを通るので、メニューのラベル・ツールチップ・一覧のプレビューすべてに効く。

参照: `Services/TemplateEngine.cs` の `Truncate()` / `ToSingleLine()`、
`TrayIconManager.cs` の `Truncate()`

---

### 10. 終了時に未保存確認ダイアログが出る（キャンセルが効かない）

`ExitApplication()` は `Application.Current.Shutdown()` を呼ぶだけなので、
設定画面が未保存で開いていると `OnWindowClosing()` の確認ダイアログが出る。
`e.Cancel = true` にしてもシャットダウン自体は止まらないため、
ユーザーの「キャンセル」が無視される形になる。

**対応**: `ExitApplication()` で先に `_settingsWindow.Close()` を呼び、その結果を見るようにした。
閉じられた場合は `Closed` ハンドラで `_settingsWindow` が null になるため、
まだ残っていれば「キャンセルされた」と判断して終了せず、設定画面を前面に戻す。

参照: `TrayIconManager.cs` の `ExitApplication()`

---

## 優先度: 中（未対応）

### 11. 仮想化と組み合わせたときにドロップ線が残る

`ClearDropIndicators()` は `ItemContainerGenerator.ContainerFromItem()` を使うため、
スクロールで仮想化された行の添付プロパティはクリアできない。
コンテナが再利用されると、無関係な行に挿入線が残ることがある。

対応案: `DropIndicator` を設定した `ListBoxItem` を1つだけフィールドで保持し、そこだけ戻す。

参照: `SettingsWindow.xaml.cs:320-329`

---

### 12. 連番の入力欄が実用上不便

- `OnDigitsOnly` が `-` を弾くため、`SequenceStep` に負の値（カウントダウン）を入力できない。
  `AdvanceSequence()` 自体は負数に対応している。
- 貼り付け（`DataObject.Pasting`）は検証していないので、文字を貼ると
  バインディングが無言で失敗し、値が更新されないまま古い値が残る。
- 空欄にした場合も同様に、無言で古い値が残る。

参照: `SettingsWindow.xaml.cs:438-448` / `SettingsWindow.xaml:213-218`

---

## 優先度: 低 / 仕様確認

| # | 内容 | 参照 |
|---|---|---|
| 13 | `Items` に `Reset`（`Clear()`）が来ると `PropertyChanged` の購読が外れない。現状 `Clear()` を呼ぶ箇所はないため潜在的 | `ViewModels/SettingsViewModel.cs:268-294` |
| 14 | 絞り込み中に項目名を編集しても `StatusText` の件数が更新されない | `ViewModels/SettingsViewModel.cs:296-314` |
| 15 | `-2^2` が `4` になる（単項マイナスがべき乗より強い）。Excel と同じだが数学の慣習とは逆。README に明記推奨 | `Services/ExpressionEvaluator.cs:137-170` |
| 16 | decimal のオーバーフローは `OverflowException` で、`ExpressionException` に包まれていない。上位で捕まるので実害はないがエラー種別が不統一 | `Services/ExpressionEvaluator.cs:418-454` |
| 17 | カテゴリ名の比較が `StringComparer.Ordinal`。`日付` と `日付 `（末尾空白）が別サブメニューになる | `TrayIconManager.cs:186` |
| 18 | トーストは `SystemParameters.WorkArea`（プライマリのみ）を使うため、常にプライマリモニタ右下に出る | `ToastWindow.xaml.cs:51-56` |
| 19 | 左クリックのメニュー表示が `NotifyIcon` の private メソッドをリフレクションで呼んでいる。.NET のバージョンアップやトリミング公開で壊れる（フォールバックはあり） | `TrayIconManager.cs:75-111` |
| 20 | `Alt+↑/↓` と `Ctrl+N/D/F` が Window の `PreviewKeyDown` で常に処理されるため、テキスト入力中にも発火する | `SettingsWindow.xaml.cs:586-624` |

---

## 問題が見つからなかった箇所

- リソースキーの整合性: `Light.xaml` / `Dark.xaml` のキーは完全一致、
  XAML から参照している `DynamicResource` / `StaticResource` はすべて定義済み。
  （`Brush.Surface.Alt` のみ定義されて未使用）
- ドラッグ＆ドロップの並べ替え計算（`OnListDrop` の `from < to` 補正）はケースを追った限り正しい。
- 設定ファイルの保存は一時ファイル + `File.Replace` で、破損時は `.bak` に退避。妥当。
- 二重起動防止、`ShutdownMode="OnExplicitShutdown"`、トーストの多重表示制御はいずれも問題なし。
- `{date}` `{monthend}` `{week}` `{dow}` `{calc:...}` などの計算ロジック自体は、
  上記 2〜4 のケースを除けば README の記載どおりの結果になる。
