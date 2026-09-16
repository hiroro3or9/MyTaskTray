# 全体設計

## 技術と起動

WinExe、net10.0-windows、Nullable/ImplicitUsings、UseWPF/UseWindowsForms=true。WinForms/System.Drawingの暗黙usingを除き曖昧型を防ぐ。
ルートにcsprojを置く場合はtests/**、tools/**、rebuild-guide/**をCompile/Page/Noneから除外。
App.xamlはStartupUriなし、ShutdownMode=OnExplicitShutdown。

Mutex名: MyTaskTray.SingleInstance.{8F3A6C1E-5B2D-4A77-9C10-2E7B41D9A6F2}。
二重起動なら「MyTaskTray はすでに起動しています。タスクトレイのアイコンをご確認ください。」を情報ダイアログで表示し終了。
WinForms EnableVisualStyles→テーマ→トレイ開始。終了時NotifyIcon、ホットキー、セッション、フック、タイマーを解放。

## 推奨構成

| 層/部品 | 責務 |
| --- | --- |
| App、TrayIconManager（機能ごとpartial可） | 起動、メニュー、設定反映、各機能の接続 |
| Models | AppSettings、ClipItem、ClipCategory、MenuLayoutNode、列挙型 |
| SettingsStore / SettingsStructure | 保存・復旧・互換移行・ID/順序整合 |
| TemplateEngine / ExpressionEvaluator | OS非依存の展開と計算 |
| ClipboardMatcher / AppContextMatcher | 表示条件 |
| ClipboardService / CfHtml / MarkdownRenderer / ClipboardSnapshot | 読書き・書式・全形式退避・自己書込み判定 |
| ForegroundWindowInfo / GlobalHotKey / MenuHostWindow / InputInjector | Windowsとの境界 |
| TrayActions / TrayMenuComposer / ActionSessionManager | 作業ツール登録、配置、排他 |
| ClipboardCaptureSession / SequentialCopyPasteSession / SwapCopy / TextExpansionSession | 状態を持つ作業 |
| SettingsViewModel / SettingsWindow | 編集用コピー・選択・Dirty・プレビュー |
| QuickAddWindow / ToastWindow / SequentialProgressWindow | 補助画面 |
| Themes/Light.xaml、Dark.xaml、Controls.xaml | 色とコントロール |

大規模なDI/MVVMフレームワークは不要。内部構造は変更可能だが保存名・見た目・動作を固定。

## 展開契約

Expand(template, now, sequenceValue=1, values=null)→string。
values: Clipboard遅延読取、Sprint、Inputs、Matches、AppName、AppTitle、Choices、ValueTransform。
1展開で時刻/連番は固定、Clipboardは必要時だけ1回読む。ネスト最大8。取得値の中の{...}を再解釈しない。
ContainsSequence/Clipboard/ClipboardDate/TimeSensitive、Input/Choice解析はエスケープとネストを認識。単純Containsで判定しない。

## コピーの接続順

1. メニュー表示前の前面アプリを記録し、必要なクリップボードをその表示中に1回読む。
2. 条件とレイアウトを評価し、項目に表示時のclip/matchを結び付ける。
3. 選択肢があれば先に選ぶ。入力があれば次に収集。取消では結果を書かない。
4. 展開し、PlainまたはPlain+CF_HTMLを書き込む。
5. 成功時だけ連番1回、永続化、通知。コピー成功と連番保存失敗は分けて通知する。
通常の時刻を常にtooltip時点に固定するわけではない。選択肢内の動的値は表示時に固定する。

## 排他・寿命

ActionSessionManagerはmultiple-input/sequential-copy-pasteの1つのみ。終了イベントはIDとインスタンスが現在と一致した場合だけ扱う。
選択中とSwapにも再入防止。自動展開は設定画面・メニュー・作業セッション・Swap中に停止。
入力/キュー/Swap退避/直近アプリ最大5件はメモリのみ、設定や履歴へ書かない。
作業中は競合するコピー項目とアクションを無効化し、暗黙に現在セッションを取消しない。

## Windows連携の共通事項

クリップボードは最大5回/80ms間隔再試行。空文字コピーはClipboard.Clear。copy:trueで終了後も保持。
自己書込み番号と500msの補助判定で自分の出力を再取得しない。
フック内で長い処理をしない。合成キーを再処理せず、対象外は次のフックへ流す。
進捗窓はShowActivated=falseに加えWS_EX_NOACTIVATEとWM_MOUSEACTIVATE→MA_NOACTIVATE。
