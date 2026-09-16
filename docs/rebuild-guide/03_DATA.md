# モデルと保存契約

保存先: %APPDATA%\MyTaskTray\settings.json。UTF-8 BOMなし、インデントあり、日本語非エスケープ。PascalCase、列挙値は文字列。
利用者の実データはこのキットに含まない。

| AppSettings | 型 | 既定 |
| --- | --- | --- |
| Version | int | 3 |
| Items | ClipItem[] | [] |
| Categories | ClipCategory[] | [] |
| RegularMenu / ContextualMenu | MenuLayoutNode[] | [] |
| ShowCopyNotification | bool | true |
| SequentialCaptureTrigger | UserInput / CopyKey / Any | UserInput |
| MenuHotKey / SwapCopyHotKey | string | "" |
| ActionStates | string→bool（Ordinal） | {} |
| SprintAnchorDate | DateTime? | null |
| SprintLengthDays | int | 14 |
| IsFallback | bool、保存対象外 | false |

new AppSettings()は空。CreateDefault()だけがspecs/default-items.mdの15項目を生成する。

| ClipItem | 型 | 既定 |
| --- | --- | --- |
| Id / Name / Text / ExpansionTrigger | string | "" |
| Category / CategoryId | string | "" |
| Format | Plain / Html / Markdown | Plain |
| IsSeparator | bool | false |
| SequenceValue / SequenceStep | int | 1 / 1 |
| ClipboardCondition | Always / HasText / Date / Url / Number / Json / FilePath / Email / Regex | Always |
| ClipboardPattern | string | "" |
| ApplyToEachLine | bool | false |
| AppProcess / AppTitlePattern | string | "" |

ClipItemはINotifyPropertyChanged、null文字列は空へ。Cloneは独立コピー。表示用計算プロパティはJsonIgnore。
ClipCategory: Id/Name/Color/Icon（string、既定空）。色は空または大文字#RRGGBB。未知アイコンキーは描画しない。
MenuLayoutNode: Kind=Item/Category、Id=参照先、Children=項目ID配列、Itemでは空。
CategoryIdが所属の正本、Categoryは旧形式互換の表示名。カテゴリは1階層。通常/条件付きの配置は独立。

## JSON例

```json
{
  "Version":3,
  "Items":[
    {"Id":"a","Name":"お礼","Text":"ありがとうございます。","CategoryId":"c","Category":"定型文"},
    {"Id":"b","Name":"日付変換","Text":"{date@clip:yyyyMMdd}","ClipboardCondition":"Date"}
  ],
  "Categories":[{"Id":"c","Name":"定型文","Color":"#4F8EF7","Icon":"message"}],
  "RegularMenu":[{"Kind":"Category","Id":"c","Children":["a"]}],
  "ContextualMenu":[{"Kind":"Item","Id":"b","Children":[]}],
  "ShowCopyNotification":true,
  "SequentialCaptureTrigger":"UserInput",
  "MenuHotKey":"",
  "SwapCopyHotKey":"",
  "ActionStates":{},
  "SprintAnchorDate":null,
  "SprintLengthDays":14
}
```

IDは生成時Guid N形式。既存の非空文字列IDをGuid形式でない理由で破棄しない。

## 読書き

- ファイルなし: CreateDefault→Normalize→保存。保存失敗でも起動しIsFallback=true。
- 読取り最大3回/100ms間隔。読取り不能は空AppSettings+IsFallback、退避/上書きしない。
- JSON構文不正/null/Items内nullは破損。settings.json.bakへコピー後、既定設定へ置換。退避失敗なら上書き不可、IsFallback=true。
- Items/ActionStates=nullは空へ。正規化で変更があれば採番結果等を再保存。再保存失敗でも現在値で継続。
- SaveはNormalize→同フォルダーのsettings.json.tmp→既存ならFile.Replace、なければFile.Move。
- IsFallback中は連番等の自動保存とQuickAddを抑止。利用者が設定画面で明示保存する場合は区別する。

## Normalize（冪等）

1. 空/重複項目IDを採番、前後空白整理。
2. 空カテゴリ名除去。名/色を正規化。同名はOrdinalで最初の定義へ統合しID参照を付替え。重複IDを修復。
3. 既存CategoryId優先、無効なら旧Category名で解決/作成、両方なければトップレベル。
4. 条件付き=非区切りかつClipboardCondition!=Always。他は通常。
5. レイアウトの有効参照を重複なく保持。別領域/別所属の子/不在IDを除去。Items順で未掲載を追記。空カテゴリノードは出さない。
6. Version<3は3へ。読込時に既存Version>3を下げない。設定画面の保存結果は現行3を生成。
7. 両領域を並べ直すときはItemsの他方領域のスロット順を壊さない。

## 編集保存

設定画面はCloneを編集。複製は新ID。通常の改名はID維持、既存名への改名は確認後既存カテゴリへ統合。
保存前: 空NameはText.Trim()、Category/AppProcess/AppTitlePatternをTrim、SequenceStep=0を1へ、空Idを採番。
画面で明示編集したSequenceValueのIdを記録し、その項目だけ外部追従しない。他はトレイで進んだ番号を即時反映、Dirtyにしない。
保存前にも最新settingsを読み、明示編集でない同Idの連番を取込む。最新がIsFallbackなら取込まない。
未知ActionStatesキーは維持。選択/折畳み/プレビュー更新はDirtyにしない。購読解除漏れを防ぐ。
