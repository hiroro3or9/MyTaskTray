# MyTaskTray 不具合調査レポート（2 回目）

対象: 2026-08-01 時点のソース全体（`*.cs` / `*.xaml`）＋ `README.md` との突き合わせ。
前回（`BUG_REPORT.md`, 2026-07-30）で指摘済みの 20 件は、いずれも修正が入っていることをコード上で確認しました。
以下は **今回あらたに見つかった指摘** です。サンドボックスに .NET SDK が無いため実行確認はしておらず、コード読解による指摘です。

## 一覧

| # | 内容 | 優先度 | 状況 |
| --- | --- | --- | --- |
| 1 | `{clip}` を含む項目を編集中、キー入力のたびにクリップボードを読む | 高 | **修正済み** |
| 2 | カテゴリ候補のポップアップを開いたまま Esc を押すと設定画面ごと閉じる | 高 | **修正済み** |
| 3 | `{daysuntil+1:…}` のオフセットが黙って無視される | 中 | **修正済み** |
| 4 | `{clip}` を使わない項目のコピーでも毎回クリップボードを読む | 中 | **修正済み** |
| 5 | プレビューが毎秒更新され `{guid}` / `{random}` がちらつく | 中 | **修正済み** |
| 6 | 初期サンプルの名前に開発日（2026/07/30）が焼き込まれている | 中 | **修正済み** |
| 7 | 数式パーサに再帰の深さ制限がなく、深い入れ子でプロセスごと落ちる | 低 | **修正済み** |
| 8 | `TrySave()` の正規化が画面に反映されない | 低 | **修正済み** |
| 9 | `OnWindowClosing()` の `Saved` 判定が潜在的に危険 | 低 | **修正済み** |
| 10 | 数値の書式に使うカルチャが差し込みごとに違う | 低 | **修正済み** |
| 11 | `Id` の無い項目は読み込むたびに別の Id になる | 低 | **修正済み** |
| 12 | 一時的な読み取り失敗を「破損」として扱う | 低 | **修正済み** |
| 13 | README と実装の細かな食い違い | 低 | **修正済み** |

各項目の末尾に、実際に入れた修正内容を「**対応**」として追記しています。

---

## 優先度: 高

### 1. `{clip}` を含む項目を編集中、キー入力のたびにクリップボードを読む

**経路**

1. `SettingsWindow.xaml` の `TextBox_Content` は `UpdateSourceTrigger=PropertyChanged`
2. 1 文字打つたびに `ClipItem.Text` → `SettingsViewModel.OnSelectedItemPropertyChanged()` → `OnPropertyChanged(nameof(Preview))`
3. `Preview` ゲッターが `TemplateEngine.Expand(..., ClipboardService.GetText)` を呼ぶ
4. 展開対象に `{clip}` があると `ClipboardService.GetText()` が実行される

さらに `SettingsWindow` の `_previewTimer`（1 秒間隔）も `RefreshPreview()` で同じ経路を通すため、
**設定画面を開いて `{clip}` の項目を選んでいる間、1 秒ごとにクリップボードを開き続ける**ことになります。

`ClipboardService.GetText()` は失敗時に `Thread.Sleep(80)` × 4 回リトライするため、
他アプリがクリップボードをロックしていると **UI スレッドが 1 回あたり最大 320ms 止まります**。
「クリップボードを何度も読むと他アプリのコピー操作を妨げる」という懸念は
`TrayIconManager.CreateClipboardReader()` のコメントに書かれているのに、設定画面側では対策されていません。
`{clip}` の値を差し替えながら試したいユーザーがいちばん困る場面で競合します。

**対応**: `SettingsViewModel` に `_clipboard` フィールドと `RefreshClipboard()` を追加し、
`Preview` は読み取りではなく覚えている値（`() => _clipboard`）を使うようにした。
読み直すのは次の 2 か所だけで、キー入力やタイマーでは読まない。

- `SettingsWindow` の `Activated`（他アプリでコピーしてから戻ってきたとき）
- `RefreshPlaceholderSamples()`（差し込み挿入パネルを開いたとき。従来どおり一覧全体で 1 回）

値が変わったときだけ `Preview` の更新を通知するため、無駄な再評価も起きない。
あわせて #5 の対応でタイマー自体が `{time}` 系を含むときしか回らなくなったため、1 秒ごとの読み取りもなくなった。

参照: `ViewModels/SettingsViewModel.cs` の `Preview` / `RefreshClipboard()` / `RefreshPlaceholderSamples()`、
`SettingsWindow.xaml.cs` のコンストラクタ（`Activated`）

---

### 2. カテゴリ候補のポップアップを開いたまま Esc を押すと、設定画面ごと閉じる

README のショートカット表は「Esc = キャンセル（**ポップアップが開いているときは閉じる**）」としています。

- 差し込み挿入パネル（`InsertPopup`）は `OnOpenInsertPopup()` が `PlaceholderFilterBox.Focus()` を呼ぶため、
  キーボードフォーカスがポップアップ内に入り、`OnPopupPreviewKeyDown()` が Esc を拾える。
- カテゴリ候補（`CategoryPopup`）は `OnOpenCategoryPopup()` が `IsOpen = true` にするだけで、
  **フォーカスは `CategoryPickButton`（＝ポップアップの外）に残る**。
  WPF の `Popup` は開いてもキーボードフォーカスを自動で移さない（`StaysOpen="False"` が確保するのはマウスキャプチャ）ため、
  Esc は `PreviewKeyDown` のルートに乗らず、`IsCancel="True"` の「キャンセル」ボタンが反応する。

結果、カテゴリを選ぼうとして Esc を押すと**ポップアップではなくウィンドウが閉じようとします**。
未保存の変更があれば確認ダイアログが出るので即消失はしませんが、README の記載とも食い違います。

**対応**: フォーカス位置に依存しないよう、`OnWindowPreviewKeyDown()` の先頭で処理するようにした。

```csharp
if (e.Key == Key.Escape && (InsertPopup.IsOpen || CategoryPopup.IsOpen))
{
    InsertPopup.IsOpen = false;
    CategoryPopup.IsOpen = false;
    e.Handled = true;
    return;
}
```

`PreviewKeyDown` はルートの先頭から下りてくるため、フォーカスがポップアップの中にあっても外にあっても
ウィンドウ側が先に拾える。既存の `OnPopupPreviewKeyDown()` は二重の保険として残している。

参照: `SettingsWindow.xaml.cs` の `OnWindowPreviewKeyDown()`

---

## 優先度: 中

### 3. `{daysuntil+1:2026-12-31}` のオフセットが黙って無視される

前回 #4 で「オフセットに意味がない差し込みは `RejectOffset()` で誤りとして扱う」方針にしましたが、
`daysuntil` だけ抜けています。

```csharp
case "daysuntil":
    return FormatDaysUntil(now, format, original);   // hasOffset / offset を見ていない
```

`InnerRegex()` は `{daysuntil+1:2026-12-31}` を name=`daysuntil` / sign=`+` / num=`1` / fmt=`2026-12-31` と解析するため、
**オフセットが黙って捨てられ、`{daysuntil:2026-12-31}` と同じ結果**になります。
「解釈できない差し込みは書いたまま残して気付けるようにする」というエンジン全体の方針から外れています。

**対応**: `case "daysuntil":` の先頭に `RejectOffset(name, hasOffset);` を追加。
`{guid}` / `{random}` と同じく、オフセットを付けた場合は書いたままの文字列が残る。
README のオフセット非対応の例にも `{daysuntil+1:…}` を追記した。

参照: `Services/TemplateEngine.cs` の `ExpandToken()`

---

### 4. `{clip}` を使わない項目のコピーでも毎回クリップボードを読む

```csharp
private void CopyToClipboard(ClipItem item)
{
    string clipboard = ClipboardService.GetText();          // 常に読む
    if (string.IsNullOrWhiteSpace(clipboard) && TemplateEngine.ContainsClipboard(item.Text)) { ... }
```

`ContainsClipboard()` の判定より先に読んでいるため、`{clip}` を使わない普通の定型文をコピーするときも
クリップボードを開きます。ロックされていると #1 と同じく最大 320ms 固まり、
「メニューをクリックしてから貼り付けられるまで一瞬待たされる」形になります。

**対応**: `ContainsClipboard(item.Text)` を先に評価し、true のときだけ `GetText()` を呼ぶようにした。

```csharp
bool usesClipboard = TemplateEngine.ContainsClipboard(item.Text);
string clipboard = usesClipboard ? ClipboardService.GetText() : string.Empty;

if (usesClipboard && string.IsNullOrWhiteSpace(clipboard)) { /* 通知して中止 */ }
```

`{clip}` を含まない項目では `Expand()` がクリップボードの関数を呼ばないため、
`() => clipboard` をそのまま渡しても読み取りは発生しない。

参照: `TrayIconManager.cs` の `CopyToClipboard()`

---

### 5. プレビューが毎秒更新され、`{guid}` / `{random}` がちらつく

`_previewTimer` は選択項目の内容に関係なく 1 秒ごとに `Preview` を再評価します。
`{guid}` や `{random}` は評価のたびに違う値になるため、
**「プレビュー（実際にコピーされる文字列）」と書かれた欄の中身が毎秒書き換わります**。
表示の落ち着きが悪いだけでなく、「この値がコピーされる」という説明とも食い違います。

**対応**: `TemplateEngine.ContainsTimeSensitive()` を追加し（`time` `datetime` `now` `hour` `minute` `second`）、
`SettingsViewModel.NeedsPreviewRefresh` が true のときだけタイマーを回すようにした。
`SettingsWindow.UpdatePreviewTimer()` が選択項目とコピー文字列の変更に追従して開始／停止する
（`Start()` は動作中に呼ぶと間隔が測り直しになるため、状態が変わるときだけ操作する）。

`{guid}` / `{random}` だけの項目ではタイマーが止まるためちらつかない。
判定は `ContainsSequence()` / `ContainsClipboard()` と同じ走査（`ContainsToken()` を名前の条件を受け取る形に一般化）を通すため、
`{{time}}` のエスケープや `{calc:{hour}*60}` のような入れ子も正しく扱える。

なお `{time}` と `{guid}` を両方含む項目では、時刻に追従する以上 GUID も毎秒変わる。
これは差し込みの性質上避けられないため、そのままにしている。

参照: `Services/TemplateEngine.cs` の `ContainsTimeSensitive()` / `ContainsToken()`、
`ViewModels/SettingsViewModel.cs` の `NeedsPreviewRefresh`、`SettingsWindow.xaml.cs` の `UpdatePreviewTimer()`

---

### 6. 初期サンプルの名前に開発日（2026/07/30）が焼き込まれている

```csharp
new() { Category = "日付", Name = "今日 (2026/07/30)", Text = "{date}" },
new() { Category = "日付", Name = "今日 (20260730)", Text = "{date:yyyyMMdd}" },
```

初回起動時に作られるサンプルの**表示名が固定文字列**のため、
たとえば今日（2026/08/01）に初めて起動したユーザーのメニューには
「今日 (2026/07/30)」と出るのに、コピーされるのは `2026/08/01` になります。
最初に触る画面で「日付がずれている」と誤解させます。

**対応**: 名前を書式そのものにした（`今日 (yyyy/MM/dd)` / `今日 (yyyyMMdd)`）。
実際の値はメニューのツールチップと設定画面のプレビューで確認できる。

※ すでに起動したことがある環境では `settings.json` が作成済みのため、この変更は反映されない
（初回起動時のサンプルのみ）。手元で確認する場合は `%APPDATA%\MyTaskTray\settings.json` を退避してから起動する。

参照: `Models/AppSettings.cs` の `CreateDefault()`

---

## 優先度: 低

### 7. 数式パーサに再帰の深さ制限がない

`ExpressionEvaluator.Parser` は再帰下降で、`ParseExpression → ParseTerm → ParsePower → ParseUnary → ParsePostfix → ParsePrimary`
の 6 段が `(` 1 個ごとに積まれます。`ParseUnary` も `-` 1 個ごとに 1 段積みます。
深い入れ子（`{calc:((((((…`、あるいは区切り線代わりの `-------…` を式に貼り付けた場合など）で
**`StackOverflowException` になり、.NET では捕捉できないためプロセスごと落ちます**。
`Evaluate()` の `catch` も `TemplateEngine.ExpandCalc()` の `catch (Exception)` も効きません。
プレビューはキー入力のたびに評価するので、貼り付け 1 回で起こりえます。

**対応**: `Parser` に `_depth` と `EnterDepth()` を追加し、64 段を超えたら `ExpressionException` にした。
数えるのは再帰の入口である `ParsePower()` と `ParseUnary()` の 2 か所。

- かっこ・関数の引数は `ParsePrimary() → ParseExpression() → … → ParsePower()` を必ず通る
- `^` の連続は `ParsePower() → ParsePower()`
- 符号の連続は `ParseUnary() → ParseUnary()` で、`ParsePower()` を通らないため別に数える

`1+1+1+…` のような平坦な式では `ParseExpression()` の while ループで処理され深さが増えないよう、
`try / finally` で必ず戻している。README の記法一覧にも上限を追記した。

参照: `Services/ExpressionEvaluator.cs` の `MaxDepth` / `EnterDepth()` / `ParsePower()` / `ParseUnary()`

### 8. `TrySave()` の正規化が画面に反映されない

`ToSettings()` は `Clone()` を返すため、`TrySave()` の中で行う
「名前が空ならコピー文字列を入れる」「カテゴリをトリム」「増分 0 を 1 に直す」は**クローン側にしか効きません**。
保存後も画面には空の名前・トリム前のカテゴリ・増分 0 が残り、`MarkSaved()` で未保存表示だけ消えるため、
画面の内容とファイルの内容が食い違ったままになります。

**対応**: 正規化を `NormalizeItems()` として切り出し、`ToSettings()` の**前**に `_vm.Items` に対して行うようにした。
補完された名前やトリム後のカテゴリが一覧にもそのまま反映される。
`SequenceValue` には触れないため、`SequenceEditedIds`（画面で編集した項目の記録）を汚さない。
あわせて #11 の Id 採番もここで行う。

参照: `SettingsWindow.xaml.cs` の `TrySave()` / `NormalizeItems()`

### 9. `OnWindowClosing()` の `Saved` 判定が潜在的に危険

```csharp
if (Saved || !_vm.IsDirty) { return; }
```

`TrySave()` が成功すると `MarkSaved()` で `IsDirty` は false になるので、`!_vm.IsDirty` だけで足ります。
現状 `Saved` が true になる経路は必ずウィンドウを閉じるため顕在化しませんが、
今後 Ctrl+S を「保存のみ（閉じない）」に変えると、
**保存後の変更が確認なしに破棄される**ようになります。`Saved ||` は外しておくのが安全です。

**対応**: 条件を `!_vm.IsDirty` だけにした。

参照: `SettingsWindow.xaml.cs` の `OnWindowClosing()`

### 10. 数値の書式に使うカルチャが差し込みごとに違う

`FormatSequence()` は `CultureInfo.InvariantCulture`、`FormatNumber()`（`{year}` `{month}` `{day}` など）は
`CultureInfo.CurrentCulture` を使っています。`{seq:#,##0}` と `{month:#,##0}` で桁区切り記号が変わりうるため、
どちらかに揃えるのが自然です（`{calc:…}` の `Format()` は書式指定時 CurrentCulture、既定時 Invariant で同様に混在）。

**対応**: 3 か所のうち 2 か所がすでに従っていた
**「書式を指定しなければ Invariant、指定したら CurrentCulture」** に `FormatSequence()` を合わせた。

素の数値（`{seq}` `{year}`）は貼り付け先で機械的に扱われることが多いので地域設定に左右されないほうがよく、
`{calc:1000000|#,##0}` のように書式を明示した場合は桁区切りが地域設定に従うほうが自然、という切り分けです。
日本語環境では `{seq:0000}` も `{seq:#,##0}` も出力は変わりません。README にも規則を明記しました。

参照: `Services/TemplateEngine.cs` の `FormatSequence()` / `FormatNumber()`

### 11. `Id` の無い項目は読み込むたびに別の Id になる

`ClipItem.Id` は `= Guid.NewGuid().ToString("N")` の初期化子を持つため、
JSON に `Id` が無い項目は `SettingsStore.Load()` のたびに**別の Id** が割り当てられます。
README の設定ファイル例（`{ "Name": "", "Text": "", "Category": "", "IsSeparator": true }` など）は `Id` 無しで、
手編集を案内しているので現実に起こります。この状態では
`AdoptExternalSequenceValues()` の突き合わせが必ず失敗し、前回 #1 の修正が効きません。

**対応**: `Id` の既定値を `string.Empty` にして「ファイルに書かれていなかった」ことを見分けられるようにし、
2 か所で採番するようにした。

- `SettingsStore.Load()`: 空の `Id` があれば採番して**書き戻す**（`EnsureIds()`）
- `SettingsWindow.NormalizeItems()`: 画面で追加した項目に保存時に採番

`ClipItem.NewId()` を追加し、`OnDuplicateItem()` の採番もこれに統一。
README の設定ファイルの説明にも「`Id` を書かずに足した場合は次回読み込み時に採番する」旨を追記。

参照: `Models/ClipItem.cs` の `Id` / `NewId()`、`Services/SettingsStore.cs` の `EnsureIds()`、
`SettingsWindow.xaml.cs` の `NormalizeItems()`

### 12. 一時的な読み取り失敗を「破損」として扱う

`SettingsStore.Load()` は `catch (Exception)` で `TryBackupBrokenFile()` → 既定値を返します。
JSON の破損だけでなく、**ファイルが一時的にロックされていた場合も既定値になります**。
その直後に `{seq}` のコピーなどで `Save()` が走ると、既定値で設定を上書きしてしまいます。
`JsonException` と `IOException` を分け、後者は少し待ってリトライするほうが安全です。

**対応**: 読み取りと解析を分け、扱いを変えた。

| 状況 | 動作 |
| --- | --- |
| 読み取り失敗 | 100ms 間隔で 3 回まで再試行 |
| それでも読めない | 退避せず、`AppSettings.IsFallback = true` の空の設定を返す |
| 解析失敗（`JsonException` / null） | 従来どおり `.bak` に退避して既定値 |

`IsFallback`（`[JsonIgnore]`）が立っているあいだは、利用者が指示していない保存を行いません。

- `TrayIconManager.TrySaveSettings()`（連番の自動保存）は何もしない
- `AdoptExternalSequenceValues()` は取り込まない（既定値で連番を巻き戻さないため）
- `ShowSettingsWindow()` はまず `ReloadSettings()` で読み直しを試み、
  それでも駄目ならメッセージを出して**設定画面を開かない**
  （空の一覧を保存すると元の設定を失うため）
- メニューとアイコンのツールチップに「(設定を読み込めませんでした)」と表示し、
  「項目がありません」と誤解させない

参照: `Services/SettingsStore.cs` の `Load()` / `TryReadFile()`、`Models/AppSettings.cs` の `IsFallback`、
`TrayIconManager.cs` の `TrySaveSettings()` / `ShowSettingsWindow()` / `BuildClipItems()` / `BuildIconToolTip()`

### 13. README と実装の細かな食い違い

| 箇所 | 内容 | 対応 |
| --- | --- | --- |
| README 71 行目 | 「単位…省略時は日（`{time}` のみ分）」。実際は `{monthstart}`/`{monthend}`/`{month}`/`{daysinmonth}` は月、`{weekstart}`/`{weekend}`/`{week}` は週、`{year}` は年、`{hour}`/`{minute}`/`{second}` はそれぞれ時・分・秒が既定 | 省略時の単位を差し込みごとの表にして README を書き換え |
| 差し込み挿入パネル | README の一覧にある `{hour}` `{minute}` `{second}` が `TemplateEngine.Placeholders` に無い | 「時刻」グループに 3 件追加 |
| `{clip:/…/}` | 正規表現に単独の `}` が含まれる場合（例 `{clip:/}/}`）、`FindClosingBrace()` のかっこ対応が崩れて途中で切れる。`\d{2,4}` のような対になった波かっこは正しく動く | 差し込みの終わりと区別できないため**仕様**として README に明記（対になっていれば使える旨も併記） |
| `{random:-5-5}` | 負の下限を含む範囲は `Split('-', 2)` で解釈できず、書いたまま残る | `RandomRangeRegex()`（`^\s*(-?\d+)\s*-\s*(-?\d+)\s*$`）で読むようにし、`{random:-5-5}` `{random:-10--5}` を**サポート**。README と挿入パネルにも追記 |

---

## 確認できなかった点（Windows 実機での確認を推奨）

- **マルチディスプレイ＋DPI 混在時のトースト位置**: `ToastWindow.GetWorkArea()` は
  `PresentationSource.FromVisual(this)` の `TransformFromDevice` で実ピクセル → WPF 座標に変換しますが、
  この倍率は**移動前のモニタ**のものです。100% と 150% が混在する環境で、
  カーソルが別倍率のモニタにあるとき初回表示の位置がずれる可能性があります
  （`DpiChanged` → 再レイアウト → `SizeChanged` → `Reposition()` で自己修正される可能性もあります）。
- **サブメニュー項目のツールチップ**: `ToolStripDropDown.DefaultShowItemToolTips` は `true` のため
  表示されるはずですが、実機での確認が確実です。
- 前回 #19 のリフレクション（`NotifyIcon.ShowContextMenu`）は .NET 10 でも存在するか。
  フォールバックがあるので機能は失われませんが、表示位置の挙動が変わります。

## 問題が見つからなかった箇所（今回追加で確認）

- `TemplateEngine.ContainsToken()` / `FindClosingBrace()` のエスケープ処理は、
  `{{seq}}`・`{calc:{seq}*100}`・`{clip:/\d{2,4}/}` のいずれも意図どおり。
- `ExpressionEvaluator` の演算子優先順位・右結合のべき乗・後置 `%`・0 除算・オーバーフロー処理は
  README の記載どおり（深さ制限のみ #7 の指摘）。
- `SettingsViewModel.ResubscribeItems()` による購読の張り直し、`_dropIndicatorTarget` 方式の挿入線、
  `DisposeMenu()` の遅延破棄はいずれも前回の指摘どおりに直っている。
- `ThemeManager.Attach()` のローカル関数による `-=` は、同一の `Attach()` 呼び出し内であれば
  クロージャのインスタンスが同じためデリゲートが等価と判定され、正しく購読解除される（コメントの記述は正しい）。
- `ThemeManager.Apply()` の `MergedDictionaries` の入れ替え順、二重起動防止、
  `SettingsStore.Save()` の一時ファイル + `File.Replace` は妥当。
