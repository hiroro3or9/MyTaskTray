using System.ComponentModel;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Forms;
using System.Windows.Threading;
using MyTaskTray.Models;
using MyTaskTray.Services;

namespace MyTaskTray
{
    /// <summary>テンプレートの選択肢メニューを組み立て、選択結果を次の処理へ渡す。</summary>
    public sealed partial class TrayIconManager
    {
        private sealed record ChoicePrompt(
            ClipItem Item,
            IReadOnlyList<ChoiceDefinition> Definitions,
            Dictionary<string, string> Selected,
            int Index,
            System.Drawing.Point Origin,
            IntPtr PreviousForeground,
            bool FromHotKey,
            Func<string> Clipboard,
            IReadOnlyDictionary<string, string> Captures,
            ForegroundApp AppContext,
            Action<IReadOnlyDictionary<string, string>> Completed);

        /// <summary>
        /// 選択肢を 1 つずつメニューで尋ね、すべて選び終えたら <paramref name="completed"/> を呼ぶ。
        /// 途中で中止した場合は何も呼ばない（コピーもしない）。
        /// </summary>
        private void AskChoices(
            ClipItem item,
            IReadOnlyList<ChoiceDefinition> definitions,
            Func<string> clipboard,
            IReadOnlyDictionary<string, string> captures,
            ForegroundApp appContext,
            Action<IReadOnlyDictionary<string, string>> completed)
        {
            // _menuContextWindow ではなくこちらを読む。
            // ここは項目の Click の中で、メニューの Closed（= ClearMenuContext）は
            // それより先に走り終えているため、_menuContextWindow は既に空になっている
            IntPtr previousForeground = _focusReturnWindow == _menuHost.Handle
                ? IntPtr.Zero
                : _focusReturnWindow;

            ChoicePrompt prompt = new(
                Item: item,
                Definitions: definitions,
                Selected: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
                Index: 0,
                Origin: System.Windows.Forms.Cursor.Position,
                PreviousForeground: previousForeground,
                FromHotKey: _menuOpenedFromHotKey,
                Clipboard: clipboard,
                Captures: captures,
                AppContext: appContext,
                Completed: completed);

            // ここで立てるのが要。いま呼び出し元のトレイメニューが閉じようとしていて、
            // その「閉じたら元のウィンドウへ戻す」が予約される。
            // 立てておかないと、その復帰が 1 枚目の選択メニューのフォーカスを奪う
            // （RestoreForeground() の説明を参照）
            _choiceChainActive = true;

            // 選択メニューはドロップダウンが入力を掴んでいるあいだだけの一時的なもので、
            // 閉じれば必ず終わる。ActionSessionManager へ登録すると、
            // 連鎖のあいだ HasActiveSession が全項目を無効にしてしまい噛み合わない。
            //
            // 1 枚目もいまは出さない。ここは項目をクリックしたメニューの Click の中で、
            // そのメニューはこれから閉じるところ。閉じる処理と重ねると後始末とぶつかる
            InvokeAfterMenuClose(() => ShowChoiceMenu(prompt));
        }

        /// <summary>
        /// 選択肢のメニューを 1 枚出す。
        ///
        /// <para>
        /// ここは <see cref="InvokeAfterMenuClose"/> から呼ばれる。ディスパッチャのコールバックで
        /// 例外を出すとアプリごと落ちるため、トレイメニューの表示
        /// （<see cref="ShowMenuFromHotKey"/>）と同じく、捕まえて通知に変える。
        /// </para>
        /// <para>
        /// あわせて連鎖の後始末をする。<c>_choiceChainActive</c> を立てたまま抜けると、
        /// 以降フォーカスの復帰が効かなくなり、メニューを閉じても
        /// 元のウィンドウへ戻らないアプリになってしまう。
        /// </para>
        /// </summary>
        private void ShowChoiceMenu(ChoicePrompt prompt)
        {
            if (_disposed)
            {
                _choiceChainActive = false;
                return;
            }

            try
            {
                ShowChoiceMenuCore(prompt);
            }
            catch (Exception ex)
            {
                _choiceChainActive = false;
                RestoreForeground(prompt.PreviousForeground);
                ToastWindow.ShowToast("選択肢を表示できませんでした", ex.Message);
            }
        }

        private void ShowChoiceMenuCore(ChoicePrompt initialPrompt)
        {
            ContextMenuStrip menu = new()
            {
                // トレイメニューと同じ設定にする。チェック欄の有無だけは、
                // 表示中の選択が単一か複数かに合わせて PopulateChoiceMenu() で切り替える
                ShowImageMargin = false,
            };

            List<ToolStripMenuItem> numbered = [];
            HashSet<Keys> pressedActivationKeys = [];
            ChoicePrompt? pendingPrompt = null;
            bool transitionDispatchPending = false;
            bool closedByChoice = false;

            // 1 つ選ぶたびにメニューを閉じて作り直すと、画面上では点滅して見える。
            // 項目クリックによる自動クローズを止め、同じメニューの中身だけを差し替える。
            menu.Closing += (_, e) =>
            {
                if (e.CloseReason == ToolStripDropDownCloseReason.ItemClicked)
                {
                    e.Cancel = true;
                }
            };

            // 中身を差し替えてもハンドラーを積み増さないよう、キー処理はメニューへ 1 回だけ付ける。
            // 数字・Enter・Space は、離すまで同じ押下として扱う。候補を切り替えた直後に
            // キーリピートが次の候補まで決定してしまうのを防ぐため。
            menu.KeyDown += (_, e) =>
            {
                if (!IsChoiceActivationKey(e.KeyCode))
                {
                    return;
                }

                if (!pressedActivationKeys.Add(e.KeyCode) || pendingPrompt is not null)
                {
                    SuppressKey(e);
                    return;
                }

                ActivateNumberedItem(numbered, e);
            };

            menu.KeyUp += (_, e) =>
            {
                _ = pressedActivationKeys.Remove(e.KeyCode);
                if (pressedActivationKeys.Count == 0 && pendingPrompt is not null)
                {
                    SchedulePendingMove();
                }
            };

            void CompleteChoice(
                ChoicePrompt completedPrompt,
                Dictionary<string, string> selected)
            {
                closedByChoice = true;
                menu.Close(ToolStripDropDownCloseReason.CloseCalled);

                // 連鎖の終わり。ここで初めてフォーカスを元のウィンドウへ戻す。
                _choiceChainActive = false;
                RestoreForeground(completedPrompt.PreviousForeground);

                // Close の後始末と重ならないよう、完了処理もメニューが落ち着いてから呼ぶ
                InvokeAfterMenuClose(() => completedPrompt.Completed(selected));
            }

            void AbortChoice(ChoicePrompt prompt, Exception ex)
            {
                closedByChoice = true;
                menu.Close(ToolStripDropDownCloseReason.CloseCalled);
                _choiceChainActive = false;
                RestoreForeground(prompt.PreviousForeground);
                ToastWindow.ShowToast("選択肢を表示できませんでした", ex.Message);
            }

            void SchedulePendingMove()
            {
                if (transitionDispatchPending)
                {
                    return;
                }

                transitionDispatchPending = true;
                InvokeAfterMenuItemClick(() =>
                {
                    transitionDispatchPending = false;

                    if (!menu.Visible || _disposed)
                    {
                        return;
                    }

                    // 決定に使ったキーをまだ押している間は、現在の画面を保つ。
                    // KeyUp が来た時点でもう一度ここへ予約される。
                    if (pressedActivationKeys.Count > 0 || pendingPrompt is null)
                    {
                        return;
                    }

                    ChoicePrompt prompt = pendingPrompt;
                    pendingPrompt = null;

                    try
                    {
                        PopulateChoiceMenu(prompt);
                        if (prompt.FromHotKey)
                        {
                            SelectFirstEnabledItem(menu.Items);
                        }
                    }
                    catch (Exception ex)
                    {
                        AbortChoice(prompt, ex);
                    }
                });
            }

            void MoveTo(ChoicePrompt prompt)
            {
                // Click の処理中に、その Click を送ってきた項目を破棄してはいけない。
                // メニュー自体は開いたまま、イベントを抜けたあと、決定キーが離されてから
                // 内容だけを入れ替える。
                pendingPrompt = prompt;
                SchedulePendingMove();
            }

            void AdvanceChoice(ChoicePrompt prompt, string name, string option)
            {
                Dictionary<string, string> selected = new(
                    prompt.Selected,
                    StringComparer.OrdinalIgnoreCase)
                {
                    [name] = option,
                };

                ChoicePrompt next = prompt with
                {
                    Selected = selected,
                    Index = prompt.Index + 1,
                };

                if (next.Index >= next.Definitions.Count)
                {
                    CompleteChoice(next, selected);
                    return;
                }

                MoveTo(next);
            }

            void GoBackChoice(ChoicePrompt prompt)
            {
                Dictionary<string, string> selected = new(
                    prompt.Selected,
                    StringComparer.OrdinalIgnoreCase);
                selected.Remove(prompt.Definitions[prompt.Index - 1].Name);

                MoveTo(prompt with
                {
                    Selected = selected,
                    Index = prompt.Index - 1,
                });
            }

            void PopulateChoiceMenu(ChoicePrompt prompt)
            {
                ChoiceDefinition definition = prompt.Definitions[prompt.Index];

                // 選択肢に書かれた差し込みは、この画面を作るときに 1 回だけ展開する。
                // 選んだあとに展開し直すと、{time} や {guid} のように評価のたびに変わるものが
                // メニューで見た値と食い違う。以降は表示にも値にも、この文字列だけを使う
                List<string> options = ResolveChoiceOptions(prompt, definition);
                List<ToolStripMenuItem> numberCandidates = [];

                menu.SuspendLayout();
                try
                {
                    numbered.Clear();
                    ClearAndDispose(menu.Items);

                    // ShowImageMargin と ShowCheckMargin の両方が false だと WinForms は
                    // Checked の印を描かないため、複数選択の画面だけチェック欄を出す
                    menu.ShowCheckMargin = definition.AllowMultiple;

                    string heading = definition.AllowMultiple ? "複数選択" : "選択";
                    heading += prompt.Definitions.Count > 1
                        ? $": {definition.Name} ({prompt.Index + 1}/{prompt.Definitions.Count})"
                        : $": {definition.Name}";

                    menu.Items.Add(new ToolStripMenuItem(EscapeAmpersand(heading))
                    {
                        Enabled = false,
                    });

                    if (definition.AllowMultiple)
                    {
                        AddMultipleChoiceItems(
                            menu,
                            prompt,
                            definition,
                            options,
                            numberCandidates,
                            selected => AdvanceChoice(prompt, definition.Name, selected));
                    }
                    else
                    {
                        foreach (string option in options)
                        {
                            ToolStripMenuItem entry = new(EscapeAmpersand(FormatChoiceOption(option)))
                            {
                                // 選んだ場合の完成形を出す。一覧で確認する代わりに、
                                // 選択肢ごとに結果が見えるようにする
                                ToolTipText = BuildChoicePreview(prompt, definition.Name, option),
                            };

                            entry.Click += (_, _) => AdvanceChoice(prompt, definition.Name, option);
                            menu.Items.Add(entry);
                            numberCandidates.Add(entry);
                        }
                    }

                    if (prompt.Index > 0)
                    {
                        menu.Items.Add(new ToolStripSeparator());

                        ToolStripMenuItem back = new("← 戻る");
                        back.Click += (_, _) => GoBackChoice(prompt);
                        menu.Items.Add(back);
                        numberCandidates.Add(back);
                    }

                    numbered.AddRange(AssignNumberAccessKeys(numberCandidates));

                    // 項目を全部追加したあとに呼ぶこと（Items をたどって配色を配るため）
                    TrayMenuTheme.Apply(menu);
                }
                finally
                {
                    menu.ResumeLayout(performLayout: true);
                }
            }

            void OnClosed(object? sender, ToolStripDropDownClosedEventArgs e)
            {
                menu.Closed -= OnClosed;
                DisposeMenuLater(menu);

                if (closedByChoice)
                {
                    return;
                }

                // 中止（Esc・別の場所をクリック）。連鎖はここで終わり
                _choiceChainActive = false;
                RestoreForeground(initialPrompt.PreviousForeground);
            }

            menu.Closed += OnClosed;

            try
            {
                PopulateChoiceMenu(initialPrompt);

                // 最初の 1 回だけウィンドウを表示する。以降は同じウィンドウの中身を差し替える。
                _ = ShowMenuAtCursor(
                    menu,
                    initialPrompt.FromHotKey,
                    position: initialPrompt.Origin,
                    carriedForeground: initialPrompt.PreviousForeground,
                    restoreForegroundOnClose: false);
            }
            catch
            {
                menu.Closed -= OnClosed;
                menu.Dispose();
                throw;
            }
        }

        /// <summary>
        /// <c>{choices:…}</c> の、いくつでも選べるメニューを組み立てる。
        ///
        /// <para>
        /// 1 つ選ぶごとに閉じてしまっては選べないので、
        /// <see cref="ToolStripDropDown.Closing"/> で「項目のクリックによる閉じ」を打ち消す。
        /// 代わりに「決定」を置き、そこで初めて閉じる。
        /// </para>
        /// <para>
        /// 番号キーは <see cref="ToolStripMenuItem.PerformClick"/> を通るため、
        /// マウスと同じくチェックの反転として効く（<see cref="EnableNumberKeys"/>）。
        /// </para>
        /// </summary>
        /// <param name="accept">「決定」を押したとき、連結した選択結果を呼び出し元へ渡す。</param>
        /// <param name="options">展開済みの選択肢。表示にも値にもこれを使う。</param>
        private void AddMultipleChoiceItems(
            ContextMenuStrip menu,
            ChoicePrompt prompt,
            ChoiceDefinition definition,
            List<string> options,
            List<ToolStripMenuItem> numbered,
            Action<string> accept)
        {
            // 同じ文字列の選択肢が 2 つ書かれていても取り違えないよう、値ではなく位置で覚える
            HashSet<int> selectedIndexes = [];
            List<ToolStripMenuItem> entries = [];

            // 「決定」の見出しに選んだ件数は入れない。
            // AssignNumberAccessKeys が Text の先頭へ番号を差し込むため、
            // あとから書き換えると番号ごと消えてしまう。
            // 件数は、すぐ上に並ぶチェックの数を見れば分かる
            ToolStripMenuItem commit = new("決定");

            void Refresh()
            {
                commit.ToolTipText = BuildMultipleChoiceTip(prompt, definition, options, selectedIndexes);

                // それぞれの選択肢には「いま押したらどうなるか」を出す。
                // 1 つ押すたびに全部の意味が変わるので、まとめて作り直す
                for (int i = 0; i < entries.Count; i++)
                {
                    HashSet<int> hypothetical = [.. selectedIndexes];
                    if (!hypothetical.Remove(i))
                    {
                        _ = hypothetical.Add(i);
                    }

                    entries[i].ToolTipText = BuildMultipleChoiceTip(
                        prompt, definition, options, hypothetical);
                }
            }

            for (int i = 0; i < options.Count; i++)
            {
                int index = i;

                ToolStripMenuItem entry = new(EscapeAmpersand(FormatChoiceOption(options[i])))
                {
                    // 番号キーは PerformClick を通るので、マウスと同じく反転として効く
                    CheckOnClick = true,
                };

                entry.Click += (_, _) =>
                {
                    // CheckOnClick は Click より先に反転するので、ここで読むのは反転後の状態
                    if (entry.Checked)
                    {
                        _ = selectedIndexes.Add(index);
                    }
                    else
                    {
                        _ = selectedIndexes.Remove(index);
                    }

                    Refresh();
                };

                menu.Items.Add(entry);
                entries.Add(entry);
                numbered.Add(entry);
            }

            menu.Items.Add(new ToolStripSeparator());

            commit.Click += (_, _) =>
            {
                accept(TemplateEngine.JoinChoices(options, selectedIndexes));
            };

            menu.Items.Add(commit);
            numbered.Add(commit);

            Refresh();
        }

        /// <summary>
        /// 複数選択の状態を 1 行で説明する。
        /// 1 つも選んでいない場合は、選び忘れと「あえて選ばない」を区別できるよう明示する。
        /// </summary>
        private string BuildMultipleChoiceTip(
            ChoicePrompt prompt,
            ChoiceDefinition definition,
            List<string> options,
            HashSet<int> selectedIndexes)
        {
            string joined = TemplateEngine.JoinChoices(options, selectedIndexes);
            string head = selectedIndexes.Count == 0
                ? "何も選んでいません（この差し込みは空になります）"
                : $"{selectedIndexes.Count} 件: {TemplateEngine.ToSingleLine(joined, 80)}";

            return head + "\n→ " + BuildChoicePreview(prompt, definition.Name, joined);
        }

        /// <summary>
        /// クリックを送ってきた項目を安全に破棄できるよう、現在の入力イベントを抜けてから実行する。
        /// メニューは閉じないため、待機は通常優先度の 1 回だけでよい。
        /// </summary>
        private static void InvokeAfterMenuItemClick(Action action)
        {
            System.Windows.Application? app = System.Windows.Application.Current;
            if (app is null)
            {
                action();
                return;
            }

            app.Dispatcher.BeginInvoke(DispatcherPriority.Normal, action);
        }

        /// <summary>
        /// メニューを閉じる処理の途中では次のメニューを出せないため、
        /// 入力・フォーカス・閉じる処理が落ち着いてから実行する。
        ///
        /// <para>
        /// 既定の <see cref="DispatcherPriority.Normal"/> で予約すると、元のメニューが
        /// 遅れて処理するフォーカス変更より先に次のメニューが開く。その直後に元の処理が
        /// 前面を切り替え、出たばかりの選択肢が <c>AppFocusChange</c> で閉じてしまう。
        /// <see cref="DispatcherPriority.ContextIdle"/> まで待つことで、元のメニューに属する
        /// 入力とフォーカス変更を先に処理し終える。
        /// </para>
        /// </summary>
        private static void InvokeAfterMenuClose(Action action)
        {
            System.Windows.Application? app = System.Windows.Application.Current;
            if (app is null)
            {
                action();
                return;
            }

            app.Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, action);
        }

        /// <summary>閉じる処理の途中で破棄すると例外になるため、戻してから破棄する。</summary>
        private static void DisposeMenuLater(ContextMenuStrip menu)
        {
            System.Windows.Application? app = System.Windows.Application.Current;
            if (app is null)
            {
                menu.Dispose();
                return;
            }

            app.Dispatcher.BeginInvoke(new Action(menu.Dispose));
        }

        /// <summary>
        /// 選択肢をメニューの 1 行にする。
        /// 空や空白だけの選択肢はそのまま出すと<strong>押せる行が見えなくなる</strong>ため、
        /// そうと分かる表示に置き換える（改行やタブは <see cref="TemplateEngine.ToSingleLine"/> が記号にする）。
        /// </summary>
        private static string FormatChoiceOption(string option)
        {
            if (option.Length == 0)
            {
                return "(空)";
            }

            // 元の文字ではなく「描いたあと」で判定する。
            // ToSingleLine は改行を ⏎ にする（見える）が、タブは空白 4 つにする（見えない）。
            // 元の文字で判定すると、この違いを取りこぼす
            string rendered = TemplateEngine.ToSingleLine(option, MenuTextMaxLength);

            return rendered.All(char.IsWhiteSpace)
                ? $"(空白 {option.Length} 文字)"
                : rendered;
        }

        /// <summary>
        /// 選択肢に書かれた差し込みを展開する。<strong>メニュー 1 枚につき 1 回だけ</strong>呼ぶ。
        ///
        /// <para>
        /// 時刻は全部の選択肢で同じ値を使う。1 つずつ <c>DateTime.Now</c> を読むと、
        /// <c>{time:HH:mm:ss}</c> を並べたときに選択肢どうしで秒がずれることがある。
        /// </para>
        /// </summary>
        private List<string> ResolveChoiceOptions(ChoicePrompt prompt, ChoiceDefinition definition)
        {
            DateTime now = DateTime.Now;
            ExpandValues values = AddAppContext(new ExpandValues
            {
                Clipboard = prompt.Clipboard,
                Sprint = _settings.Sprint,
                Matches = prompt.Captures,
            }, prompt.AppContext);

            List<string> resolved = new(definition.Options.Count);
            foreach (string option in definition.Options)
            {
                resolved.Add(TemplateEngine.ResolveChoiceOption(
                    option, now, prompt.Item.SequenceValue, values));
            }

            return resolved;
        }

        /// <summary>
        /// その選択肢を選んだ場合の完成形。ここまでに選んだものも反映する。
        /// まだ選んでいない選択肢は、書いたままの文字列として残る。
        /// </summary>
        private string BuildChoicePreview(ChoicePrompt prompt, string name, string option)
        {
            Dictionary<string, string> selected = new(prompt.Selected, StringComparer.OrdinalIgnoreCase)
            {
                [name] = option,
            };

            string expanded = TemplateEngine.Expand(
                prompt.Item.Text,
                DateTime.Now,
                prompt.Item.SequenceValue,
                AddAppContext(new ExpandValues
                {
                    Clipboard = prompt.Clipboard,
                    Sprint = _settings.Sprint,
                    Matches = prompt.Captures,
                    Choices = selected,
                }, prompt.AppContext));

            return TemplateEngine.ToSingleLine(expanded, 200);
        }
    }
}
