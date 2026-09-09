### コミットメッセージの書式（Conventional Commits）

```
ブランチ名
<type>(<scope>): <要約>

- <何をしたか>
- <補足・なぜそうしたか>
```

- ブランチ名はfeature/<英語のケバブケース>で書く
- **type と scope は英語、要約と本文は日本語**で書く
- 要約は現在形の言い切りで、句点は付けない。**40文字以内**
  （例: `離席検知の閾値を設定から変えられるようにする`）
- **本文は必ず書く。** 要約の後に空行を1行入れ、`- ` の**箇条書きで2〜4項目**書く
- 各項目は1行で収める。「何をしたか」を先に並べ、要約だけでは意図が分からない
  場合は「なぜ必要だったか」の項目も添える
- 破壊的変更は本文の後に `BREAKING CHANGE: 内容` を置く

使う type:

| type | 用途 |
| --- | --- |
| `feat` | 機能追加・既存機能の振る舞いの拡張 |
| `fix` | バグ修正 |
| `refactor` | 振る舞いを変えない内部構造の変更 |
| `perf` | 性能改善 |
| `docs` | ドキュメント・コメントのみの変更（CLAUDE.md や docs/ 配下） |
| `style` | 整形のみ（書式・命名・using 整理など振る舞いに影響しないもの） |
| `build` | csproj・依存パッケージ・ビルド設定の変更 |
| `chore` | 上記に当てはまらない雑務 |

scope はコードの置き場所に合わせる。省略可だが、付けられるなら付ける:
`app-usage` / `away` / `timeline` / `dayweek` / `stats` / `memo` / `settings` /
`routines` / `undo` / `persistence` / `workday` / `theme`

例（何をしたかだけ。これが基本）:

```
fix(app-usage): UWP アプリが記録されない問題を修正

- 前面ウィンドウの取得を ApplicationFrameHost の子プロセスまで辿るようにした
- 取得できなかった場合は直前のアプリを引き継ぐ
```

例（理由も添える場合）:

```
refactor(app-usage): P/Invoke のコールバックを関数ポインタにする

- コールバックの受け口を delegate* unmanaged[Stdcall] の static メソッドに変えた
- デリゲートだと LibraryImport が使えず、GC 対策の保持も必要だった
```

なお `5f2a130` 以前の履歴はこの書式ではない。過去に合わせる必要はなく、
これ以降のコミットから Conventional Commits に揃える。

### ブランチ名の書式

```
feature/<英語のケバブケース>
```

- プレフィックスは基本 `feature/`。バグ修正でもリファクタでもこれでよい
- 名前は英小文字・数字・ハイフンのみ。2〜4語程度で短く
- scope に相当する語があれば先頭に置く（`feature/app-usage-...` のように）
- 作業内容そのものを表す。日付・番号・自分の名前は入れない

```
feature/away-threshold-setting
feature/app-usage-uwp-detection
feature/pinvoke-function-pointers
feature/commit-convention