# 画面仕様の索引

画面全体の説明は04_UI.md。以下は1回に全て読まず、今回作る画面/スタイルの見出しだけ読む。

| 実装対象 | 必要な設計表 |
| --- | --- |
| 共通部品 | [色](colors.md)、[ボタン・入力・一覧・Popup等のスタイル](ui-styles.md) |
| 設定画面全体 | [外枠・一覧・フッター](ui-settings.md) |
| カテゴリ編集 | [カテゴリ編集](ui-settings-category.md) |
| 項目編集 | [項目編集](ui-settings-item.md) |
| 設定画面の項目/カテゴリ/バッジ | [局所スタイルと行テンプレート](ui-settings-templates.md) |
| ホットキー/アクション/連続コピー/スプリント/挿入/カテゴリ/アプリ候補 | [Popup](ui-settings-popups.md) |
| クイック追加 | [画面](ui-quick-add.md)、段階11 |
| コピー通知 | [画面](ui-toast.md)、段階03 |
| 連続コピーの進捗 | [画面](ui-progress.md)、段階16 |
| カテゴリ装飾 | [色とグリフ](category-appearance.md) |
| アプリアイコン | [作図仕様](icon.md) |

## 設計表の読み方

親子構造を箇条書き、属性を名前=値として表したもの。例えばBorder→StackPanel→TextBlockならその順で包含する。
Setter/Triggerは通常値と状態別上書き。ControlTemplateはその部品内部の構造。Margin/Paddingは左,上,右,下。
イベント名は動作を接続するための識別名。同名の空ハンドラーを並べて完成扱いにせず、各段階の動作を実装する。
Bindingで計算済み文字列や可視性を参照している場合はViewModelに対応する状態を用意する。細部の内部命名より結果を一致させる。

## 未指定値

FontFamily/Foregroundなどは親から継承。コントロール既定値はWPF標準を使う。ネイティブメニューはWinForms。現行と異なるUIライブラリへ差し替えない。
