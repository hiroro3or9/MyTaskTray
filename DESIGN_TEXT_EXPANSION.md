# 文字入力によるスニペット展開と IME 対応調査

調査日: 2026-09-14

## 今回の実装

設定画面の各項目に「文字入力で自動展開」を追加した。`ExpansionTrigger` が空なら無効。
例として `;sig` を入力し終えると、その合図を消して項目の本文を貼り付ける。
登録済みの合図が一つもなければ、追加のキーボード／マウスフックは登録しない。
以前の設定には合図がないため、自動的に有効にはならない。

- 合図は `;` から始まる 2～32 文字。続けて使える文字は半角英数字、`_`、`-`。大文字小文字を区別する。
- 重複と接頭辞の衝突（`;sig` と `;signature`）は保存時に拒否する。手編集した不正設定も開始時に検証し、自動展開を無効にして通知する。
- 本文は通常のテキスト。日本語、複数行、日付、クリップボード、アプリ情報、連番の既存テンプレート処理を利用できる。
- HTML／Markdown 形式、入力や選択肢を求めるテンプレート、クリップボード表示条件、行ごとの一括適用は初期版の対象外。アプリ名・タイトル条件は適用する。
- 本文が空、クリップボード日付を解釈できない場合は合図を残す。
- 展開後の本文はクリップボードに残す。改行を Enter キーとして送らず、本文全体を貼り付ける。
- 設定画面を開いている間、トレイメニュー表示中、Swap コピーや作業セッションの実行中は停止する。

## 誤置換を防ぐ処理

低レベルキーボードフックでキーを観測し、入力先スレッドの配列で `ToUnicodeEx` を実行する。
フラグ 4 を指定して dead key 等のキーボード状態を変更しない。
変換結果が半角 1 文字でなければ照合を取り消す。[ToUnicodeEx](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-tounicodeex)

保持するのは登録済み合図の接頭辞だけで、最大 32 文字。入力履歴は保存しない。
Backspace、矢印、Tab、Enter、ショートカット、合成入力、マウスクリック／ホイール、
フォーカス／前面ウィンドウ変更、5 秒を超える入力間隔で照合を破棄する。
Shift 単体は大文字・記号の入力に使うため接頭辞を維持する。

最後のキーもそのまま入力先へ渡す。フックの外で 35 ms 待ち、UI Automation の
TextPattern で「選択が空で、カーソル直前が合図と完全一致する」ことを確認する。
パスワード欄、読み取り専用欄、確認できない入力欄では置換しない。
UIA 呼び出しはワーカーで行い、その間にキーやフォーカスが変わったら取り消す。
500 ms 以内に回答しないプロバイダーでは置換を取り消し、回答まで新たな照会を増やさない。
本文を準備し、入力先と操作世代を再確認して、Backspace と Ctrl+V を一回の SendInput にまとめる。

SendInput の成功はキー列の送信成功であり、入力先アプリでの貼り付け成功の保証ではない。
送信後に入力先独自の処理で拒否される可能性や、別アプリのクリップボード更新との競合は残る。
通常権限から管理者権限のアプリには送信できない。
連番は入力の送信に成功した時点で進む。[SendInput](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-sendinput)

初期版は UIA TextPattern でカーソル直前のテキストを確認できる入力欄が対象。
対応していない独自エディタ、ゲーム、リモート画面等へ強制的に Backspace を送るフォールバックは設けない。
合図の直後に次の文字を入力すると、削除範囲を誤らないよう展開を取り消すことがある。

## IME オフの判定と日本語本文の違い

日本語の「本文」を貼り付けることは今回対応した。
「おせわ」のような日本語を合図として変換確定後に展開する機能は、今回実装していない。

初期版では入力先の `ImmGetDefaultIMEWnd` に `WM_IME_CONTROL / IMC_GETOPENSTATUS` を送り、
IME が閉じている場合だけ展開する。応答にはタイムアウトを設ける。
IME ウィンドウが取得できず、入力言語が日本語・中国語・韓国語の場合も判定不能として停止する。
これは IMM 互換の状態問い合わせであり、全 TSF アプリ・全 IME での正確性を保証するものではない。
確定済みの合図を UIA で再確認する処理も併用する。
[ImmGetDefaultIMEWnd](https://learn.microsoft.com/en-us/windows/win32/api/imm/nf-imm-immgetdefaultimewnd)

## 日本語の合図に対応する方法

### 第一候補: UI Automation の確定通知

TextEdit パターンの `CompositionFinalized` は、変換確定した文字列をイベントで通知する。
空の確定文字列は取消・削除の場合もあるので無視する。
変換途中の `Composition` や通常の TextChanged を確定と解釈してはいけない。
[TextEdit Control Pattern](https://learn.microsoft.com/en-us/windows/win32/winauto/textedit-control-pattern)

`IUIAutomation3.AddTextEditTextChangedEventHandler` で、フォーカス中の要素だけに登録する。
専用 MTA スレッドで登録・解除を直列化し、フォーカスが変わったら購読を切り替える。
既存のマネージド TextPattern 利用だけでこの通知も受け取れると仮定せず、
IUIAutomation3 の COM 相互運用を追加する設計にする。
[通知登録 API](https://learn.microsoft.com/en-us/windows/win32/api/uiautomationclient/nf-uiautomationclient-iuiautomation3-addtextedittextchangedeventhandler)

構想:

1. 確定イベントの送信元と現在の入力先が同じか確認する。
2. 確定文字列を日本語トリガーと照合する。初期仕様は「一回の確定文字列全体と一致」とする。
3. 合図がカーソル直前にあり、選択範囲がなく、変換中の範囲でもないことを確かめる。
4. 対象範囲を選択して貼り付ける方式を検証する。日本語・絵文字・結合文字では
   .NET の string.Length 分の Backspace が文字数と一致するとは限らないので、現在の削除処理を流用しない。
5. 自分による置換通知を除外し、次の変換が始まった場合は中断する。

この方式なら常駐 WPF アプリに追加できる見込みがある。ただし提供側の TextEdit 対応が必要。
TextPattern で文字が読めることと、TextEdit の確定通知が届くことは別条件。
通知がない入力先では日本語展開を無効にする。アプリ別の対応範囲は実測が必要。

### 自アプリ内に限定する場合: IME メッセージ

Win32 入力欄なら `WM_IME_COMPOSITION` の `GCS_RESULTSTR` を見て
`ImmGetCompositionString` で確定結果を取得できる。
WPF の自前入力欄なら TextComposition のイベントも候補になる。
これらは入力先に届く通知なので、MyTaskTray のメッセージ処理を追加するだけで
別アプリの確定結果を一括取得できるわけではない。
[確定文字列の取得](https://learn.microsoft.com/en-us/windows/win32/intl/processing-the-wm-ime-composition-message)

### 広い対応を狙う場合: TSF テキストサービス

TSF では、テキストサービスを COM の in-process サーバーとして登録する。
単なる常駐 EXE のフック追加より大きな構成変更になる。
[TSF の構成](https://learn.microsoft.com/en-us/windows/win32/tsf/architecture)

`ITfTextEditSink.OnEndEdit` で編集後の状態を観測し、変換中プロパティ等で確定と区別する。
`OnEndEdit` そのものは「日本語確定だけの通知」ではない。
置換は `RequestEditSession` による編集セッションで行う。
OnEndEdit 中の同期 read/write 要求には制限があるため、非同期編集を設計する。
[ITfTextEditSink](https://learn.microsoft.com/en-us/windows/win32/api/msctf/nn-msctf-itftexteditsink)、
[RequestEditSession](https://learn.microsoft.com/en-us/windows/win32/api/msctf/nf-msctf-itfcontext-requesteditsession)

別 DLL、登録／解除、32/64 ビット構成、既存 IME と併用した際の振る舞い、配布手順まで検証が必要。
TSF 非対応の入力先まで万能に対応できるわけではない。

## 次の検証対象

以下は未検証の候補であり、対応済み一覧ではない。

| 入力先 | IME | 確認すること |
| --- | --- | --- |
| WPF TextBox / RichTextBox | Microsoft IME | TextEdit の提供、確定・取消・再変換 |
| メモ帳 | Microsoft IME | 実際のバージョンで確定通知と範囲取得が両方使えるか |
| Chrome / Edge の textarea と contenteditable | Microsoft IME / Google 日本語入力 | 通知元・フォーカス、フォームごとの差 |
| VS Code / Word | Microsoft IME / Google 日本語入力 | 独自エディタの範囲、Undo、複数カーソル等 |

共通ケース: Enter・候補クリックでの確定、Space で変換中、Esc で取消、再変換、
全角／半角、絵文字・結合文字、直後に次の文字を入力、入力先の切替、パスワード、管理者権限。
UIA の実測で対象アプリを絞れるなら第一候補を採用し、不足が大きい場合に TSF を別途検討する。

## 検証記録

- `dotnet build MyTaskTray.csproj --no-restore -v minimal`: 成功、警告なし。
- `dotnet test tests/MyTaskTray.Tests/MyTaskTray.Tests.csproj --no-restore -v minimal`: 114 件成功。
- 合図照合、リセット、衝突、未対応設定の拒否、旧設定互換、設定の変更検知／複製／JSON 往復、日本語複数行と日付展開を検証。
- サンプル設定を使った実際の設定画面で、合図の表示、日本語複数行プレビュー、編集欄のスクロールを目視確認。実設定は保存していない。
- 実キーボードによる別アプリへの自動置換と、各 IME／アプリの互換性は未検証。
  自動操作ツールの合成入力を無視する仕様なので、自動テストの成功を実キー入力の動作確認として扱わない。
