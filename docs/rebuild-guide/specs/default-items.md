# 初回設定

CreateDefaultだけが以下の15項目をこのItems順に作る（区切り線2つを含む）。その他プロパティは03_DATAの既定。Id/CategoryId/LayoutsはNormalizeで作る。
カテゴリ名空はトップレベル。日付カテゴリに5項目、定型文カテゴリに5項目。日付変換は条件付き領域。

| 順 | 表示名 | カテゴリ | Text | 例外 |
| --- | --- | --- | --- | --- |
| 1 | メールアドレス | 空 | example@example.com | |
| 2 | 電話番号 | 空 | 03-0000-0000 | |
| 3 | 空 | 空 | 空 | IsSeparator=true |
| 4 | 今日 (yyyy/MM/dd) | 日付 | {date} | |
| 5 | 今日 (yyyyMMdd) | 日付 | {date:yyyyMMdd} | |
| 6 | 現在の日時 | 日付 | {datetime} | |
| 7 | 明日 | 日付 | {date+1} | |
| 8 | 今月末 | 日付 | {monthend} | |
| 9 | コピーした日付を yyyyMMdd に | 空 | {date@clip:yyyyMMdd} | ClipboardCondition=Date |
| 10 | 空 | 空 | 空 | IsSeparator=true |
| 11 | お礼 | 定型文 | お世話になっております。ご対応ありがとうございました。 | |
| 12 | 確認依頼 | 定型文 | ご確認のほど、よろしくお願いいたします。 | |
| 13 | 議事録の見出し | 定型文 | # {date:yyyy/MM/dd} 定例ミーティング 議事録 | Format=Markdown |
| 14 | 議事録のひな形 | 定型文 | 下記 | Format=Markdown |
| 15 | 番号とタイトルを組み立てる | 定型文 | [{input:番号}] {input:タイトル} | |

ひな形のTextをJSONのエスケープ表現で正確に示す。各改行はCRLF、末尾もCRLF。
```json
"# {date:yyyy/MM/dd} 定例ミーティング 議事録\r\n\r\n## 決定事項\r\n\r\n- \r\n\r\n## 宿題\r\n\r\n- \r\n"
```
