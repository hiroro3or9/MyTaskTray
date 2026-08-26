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
    /// <summary>コピー項目の起動、複数入力、テンプレート展開、クリップボードへの書き込みを担う。</summary>
    public sealed partial class TrayIconManager
    {
        private static Func<string> CreateClipboardReader()
        {
            string? cached = null;
            return () => cached ??= ClipboardService.GetText();
        }

        /// <summary>
        /// 1 回のメニュー操作で記録した前面アプリを、差し込みエンジンへ渡す値に加える。
        /// タイトルを取れなかった場合は null とし、<c>{app:title}</c> を書いたまま残す。
        /// </summary>
        private static ExpandValues AddAppContext(ExpandValues values, ForegroundApp app)
            => values with
            {
                AppName = app.IsKnown && app.Name.Length > 0 ? app.Name : null,
                AppTitle = app.IsKnown && app.Title.Length > 0 ? app.Title : null,
            };

        /// <summary>
        /// ツールチップや選択肢のプレビューで、代表として見せる 1 件。
        /// 複数行に適用する項目でも、見せるのは先頭の件だけにする。
        /// </summary>
        private static IReadOnlyDictionary<string, string> Representative(
            IReadOnlyList<IReadOnlyDictionary<string, string>> captures)
            => captures.Count > 0 ? captures[0] : EmptyCaptures;

        /// <summary>
        /// 複数行に適用したときに、ツールチップへ添える説明。
        /// 従来どおり 1 行を 1 件として 1 件だけ作る場合は空文字を返す。
        /// </summary>
        private static string DescribeBulk(ClipItem item, ClipboardMatchRows rows)
        {
            if (!item.UsesEachLine || rows.Count == 0)
            {
                return string.Empty;
            }

            if (rows.Count <= 1 && !rows.Truncated)
            {
                // 結果として 1 件を作っただけ。従来と同じなので何も言わない
                return string.Empty;
            }

            return $"\n{rows.Count} 件をまとめてコピーします"
                + (rows.Truncated
                    ? $"\n上限 {ClipboardMatcher.MaxBulkRows} 件のため、以降は対象外です"
                    : string.Empty);
        }

        private ToolStripMenuItem CreateClipMenuItem(
            ClipItem item,
            Func<string> clipboard,
            IReadOnlyList<IReadOnlyDictionary<string, string>> captures,
            ForegroundApp appContext,
            string bulkHint,
            bool enabled)
        {
            string label = string.IsNullOrWhiteSpace(item.Name) ? item.Text : item.Name;

            // 名前もコピー文字列も空だと、クリックできるのに何も見えない行になってしまう
            if (string.IsNullOrWhiteSpace(label))
            {
                label = "(空の項目)";
            }

            ToolStripMenuItem menuItem = new(EscapeAmpersand(Truncate(label, MenuTextMaxLength)))
            {
                Enabled = enabled,
                ToolTipText = enabled
                    ? BuildToolTip(item, clipboard, _settings.Sprint, captures, appContext, bulkHint)
                    : BuildActiveSessionBlockedReason(),
                Tag = item,
            };
            menuItem.Click += (_, _) => ActivateClipItem(item, clipboard, captures, appContext);
            return menuItem;
        }

        /// <summary>差し込みを含む場合は、展開後の値もツールチップに出す。</summary>
        private static string BuildToolTip(
            ClipItem item,
            Func<string> clipboard,
            SprintSchedule? sprint,
            IReadOnlyList<IReadOnlyDictionary<string, string>> captures,
            ForegroundApp appContext,
            string bulkHint)
        {
            string raw = Truncate(item.Text, 200);
            DateTime now = DateTime.Now;

            ExpandValues values = AddAppContext(new ExpandValues
            {
                Clipboard = clipboard,
                Sprint = sprint,
                Matches = Representative(captures),
            }, appContext);

            // 選択肢は、設定画面のプレビューと同じ規則で先頭のものを代表として出す
            string expanded = TemplateEngine.Expand(
                item.Text,
                now,
                item.SequenceValue,
                values with
                {
                    Choices = TemplateEngine.GetDefaultChoices(
                        item.Text, now, item.SequenceValue, values),
                });

            IReadOnlyList<string> inputNames = TemplateEngine.GetInputNames(item.Text);
            string inputHint = inputNames.Count == 0
                ? string.Empty
                : "\n入力: " + string.Join(" → ", inputNames);

            IReadOnlyList<ChoiceDefinition> choices = TemplateEngine.GetChoiceDefinitions(item.Text);
            string choiceHint = choices.Count == 0
                ? string.Empty
                : "\n選択: " + string.Join(" → ", choices.Select(c => c.Name))
                    + "（下は先頭の選択肢）";

            // 複数行に適用する項目では、全件を並べても Truncate() が 200 文字で切ってしまい読めない。
            // 件数は言葉で伝え（bulkHint）、展開結果は先頭 1 件を代表として見せる
            // （DESIGN_BULK_APPLY.md §6）
            string hints = bulkHint + choiceHint + inputHint;

            if (string.Equals(raw, Truncate(expanded, 200), StringComparison.Ordinal))
            {
                return raw + hints;
            }

            return raw + "\n→ " + Truncate(expanded, 200) + hints;
        }

        /// <summary>
        /// 入力のない項目はそのままコピーし、<c>{input:名前}</c> があればキャプチャを開始する。
        /// <c>{choice:名前:…}</c> があれば、その前に選択肢のメニューを出す。
        /// スマートアクションの判定に使ったクリップボードとキャプチャは、完了まで同じ値を保持する。
        /// </summary>
        private void ActivateClipItem(
            ClipItem item,
            Func<string> clipboard,
            IReadOnlyList<IReadOnlyDictionary<string, string>> captures,
            ForegroundApp appContext)
        {
            // メニューを開いたあとに別経路でセッションが始まった場合も、
            // 実行中のデータを暗黙に破棄したり、収集内容へ定型文を混ぜたりしない。
            //
            // 選択肢のメニューを出すより先に確かめる。
            // 選ばせてから「使用できません」と言うのは筋が悪い
            if (_actionSessions.HasActiveSession)
            {
                ToastWindow.ShowToast("コピー項目を使用できません", BuildActiveSessionBlockedReason());
                return;
            }

            IReadOnlyList<ChoiceDefinition> choices = TemplateEngine.GetChoiceDefinitions(item.Text);
            if (choices.Count == 0)
            {
                ContinueActivateClipItem(item, clipboard, captures, appContext, null);
                return;
            }

            // 選択が先、入力が後。
            // {input:…} のキャプチャはクリップボードを 2 分間占有するため、
            // その最中にメニューを何枚も出すとタイマーが動き続ける。
            // 選択を先に済ませればセッションは中断なく進み、
            // 途中で中止した場合も、まだ始まっていないので巻き戻す状態が無い
            AskChoices(
                item,
                choices,
                clipboard,
                Representative(captures),
                appContext,
                selected => ContinueActivateClipItem(item, clipboard, captures, appContext, selected));
        }

        /// <summary>選択が済んだあと（または選択が要らない場合）のコピー処理。</summary>
        private void ContinueActivateClipItem(
            ClipItem item,
            Func<string> clipboard,
            IReadOnlyList<IReadOnlyDictionary<string, string>> captures,
            ForegroundApp appContext,
            IReadOnlyDictionary<string, string>? choices)
        {
            // 選択メニューを出しているあいだに別経路でセッションが始まっている可能性がある
            if (_actionSessions.HasActiveSession)
            {
                ToastWindow.ShowToast("コピー項目を使用できません", BuildActiveSessionBlockedReason());
                return;
            }

            IReadOnlyList<InputCaptureDefinition> inputs = TemplateEngine.GetInputDefinitions(item.Text);
            if (inputs.Count == 0)
            {
                CopyToClipboard(item, clipboard, null, captures, appContext, choices);
                return;
            }

            bool preserveClipboard = item.HasSmartCondition || TemplateEngine.ContainsClipboard(item.Text);
            string sourceClipboard = preserveClipboard ? clipboard() : string.Empty;
            StartCapture(item, inputs, sourceClipboard, captures, appContext, choices);
        }

        private void StartCapture(
            ClipItem item,
            IReadOnlyList<InputCaptureDefinition> inputs,
            string sourceClipboard,
            IReadOnlyList<IReadOnlyDictionary<string, string>> captures,
            ForegroundApp appContext,
            IReadOnlyDictionary<string, string>? choices)
        {
            if (_actionSessions.HasActiveSession)
            {
                ToastWindow.ShowToast("複数入力を開始できません", BuildActiveSessionBlockedReason());
                return;
            }

            foreach (InputCaptureDefinition input in inputs)
            {
                foreach (string pattern in input.Patterns)
                {
                    if (!TemplateEngine.TryValidateInputPattern(pattern, out string error))
                    {
                        ToastWindow.ShowToast(
                            $"入力「{input.Name}」の正規表現が正しくありません",
                            TemplateEngine.ToSingleLine(error, 100));
                        return;
                    }
                }
            }

            ClipboardCaptureSession? session = null;
            session = new ClipboardCaptureSession(
                inputs,
                progressed: progress =>
                {
                    if (!_actionSessions.IsCurrent(TrayActionIds.MultipleInput, session))
                    {
                        return;
                    }

                    RebuildMenu();
                    ShowCapturePrompt(progress, "入力を受け取りました");
                },
                completed: inputs =>
                {
                    if (!_actionSessions.IsCurrent(TrayActionIds.MultipleInput, session))
                    {
                        return;
                    }

                    _actionSessions.Complete(TrayActionIds.MultipleInput, session);
                    CopyToClipboard(item, () => sourceClipboard, inputs, captures, appContext, choices);
                    RebuildMenu();
                },
                timedOut: () =>
                {
                    if (!_actionSessions.IsCurrent(TrayActionIds.MultipleInput, session))
                    {
                        return;
                    }

                    _actionSessions.Complete(TrayActionIds.MultipleInput, session);
                    RebuildMenu();
                    ToastWindow.ShowToast("複数入力をキャンセルしました", "2 分間コピーがなかったため終了しました");
                },
                rejected: rejection =>
                {
                    string message = rejection.FailedPattern is null
                        ? "文字列をコピーしてください。空またはテキスト以外の内容は入力として使えません"
                        : $"正規表現 /{TemplateEngine.ToSingleLine(rejection.FailedPattern, 70)}/ "
                            + "に一致しません。別の文字列をコピーしてください";
                    ToastWindow.ShowToast($"入力: {rejection.Progress.CurrentName}", message);
                });

            if (!_actionSessions.TryStart(
                TrayActionIds.MultipleInput,
                "複数入力",
                session))
            {
                session.Dispose();
                ToastWindow.ShowToast("複数入力を開始できません", BuildActiveSessionBlockedReason());
                return;
            }

            if (!session.Start())
            {
                _actionSessions.Complete(TrayActionIds.MultipleInput, session);
                ToastWindow.ShowToast(
                    "複数入力を開始できません",
                    "Windows のクリップボード変更通知を受け取れませんでした");
                return;
            }

            RebuildMenu();
            ShowCapturePrompt(session.Progress, "複数入力を開始しました");
        }

        /// <summary>
        /// <c>{choice:名前:…}</c> の選択を 1 つずつ尋ねている途中の状態。
        /// メニューを 1 枚出すたびに <c>Index</c> が進む。
        /// </summary>
        /// <param name="Origin">
        /// 1 枚目を出した位置。以降も同じ場所に出して、その場で切り替わるように見せる。
        /// カーソルに追従させると、選ぶたびにメニューが右下へずれていく。
        /// </param>
        /// <param name="PreviousForeground">
        /// 連鎖の最後にフォーカスを戻す先。
        /// <strong>連鎖を始める時点で捕まえておく必要がある。</strong>
        /// 2 枚目以降は前面が <see cref="MenuHostWindow"/> になっているため、あとからは取り直せない。
        /// 取り出し元が <c>_menuContextWindow</c> ではなく <c>_focusReturnWindow</c> なのは、
        /// 前者がクリックより先に走る <see cref="ClearMenuContext"/> で消えてしまうから。
        /// </param>
        /// <param name="FromHotKey">ホットキー経路かどうか。連鎖のあいだ引き継ぐ。</param>
        /// <param name="Clipboard">ツールチップに完成形を出すためのクリップボード読み取り。</param>

        private static void ShowCapturePrompt(ClipboardCaptureProgress progress, string title)
        {
            string condition = progress.Patterns.Count == 0
                ? string.Empty
                : $"\n条件: /{TemplateEngine.ToSingleLine(progress.Patterns[0], 70)}/"
                    + (progress.Patterns.Count > 1 ? $" ほか {progress.Patterns.Count - 1} 件" : string.Empty);

            ToastWindow.ShowToast(
                title,
                $"{progress.CapturedCount + 1}/{progress.TotalCount}: "
                    + $"「{progress.CurrentName}」に入れる文字列をコピーしてください"
                    + condition);
        }

        private void CancelCapture(bool showToast, bool rebuildMenu)
        {
            ClipboardCaptureSession? session = _actionSessions.Get<ClipboardCaptureSession>(
                TrayActionIds.MultipleInput);
            bool canceled = _actionSessions.Cancel(TrayActionIds.MultipleInput, session);

            if (rebuildMenu && !_disposed)
            {
                RebuildMenu();
            }

            if (showToast && canceled)
            {
                ToastWindow.ShowToast("複数入力をキャンセルしました", string.Empty);
            }
        }

        private void CopyToClipboard(
            ClipItem item,
            Func<string> clipboardReader,
            IReadOnlyDictionary<string, string>? inputs,
            IReadOnlyList<IReadOnlyDictionary<string, string>> captures,
            ForegroundApp appContext,
            IReadOnlyDictionary<string, string>? choices)
        {
            // クリップボードを開くと他アプリのコピー操作を妨げるうえ、ロックされていると
            // 再試行のあいだ操作が止まる。{clip} を使う項目でだけ読みに行く。
            // その場合はコピーで上書きされる前の内容が必要なので、展開より先に読む
            bool usesClipboard = TemplateEngine.ContainsClipboard(item.Text);
            string clipboard = usesClipboard ? clipboardReader() : string.Empty;

            // {clip} を使う項目でクリップボードが空だと、差し込む先が抜けた文字列になってしまう。
            // 気付かずに貼り付けてしまわないよう、コピーせずに知らせる
            if (usesClipboard && string.IsNullOrWhiteSpace(clipboard))
            {
                ToastWindow.ShowToast(
                    "クリップボードが空です",
                    "差し込む値をコピーしてから、もう一度この項目を選んでください");
                return;
            }

            // {date@clip} のようにクリップボードを日付として読む項目で、日付として読めない場合。
            // このまま展開すると差し込みが書いたまま残った文字列がコピーされ、
            // 気付かずに貼り付けてしまう。空のときと同じく、コピーせずに知らせる
            if (TemplateEngine.ContainsClipboardDate(item.Text)
                && !TemplateEngine.CanParseClipboardDate(clipboard))
            {
                ToastWindow.ShowToast(
                    "日付として読み取れません",
                    $"「{TemplateEngine.ToSingleLine(clipboard.Trim(), 30)}」から日付を読み取れませんでした。"
                        + "2026-08-15 のような形でコピーしてください");
                return;
            }

            // 時刻は全部の件で同じ値を使う。件ごとに DateTime.Now を読むと、
            // {time:HH:mm:ss} を並べたときに行どうしで秒がずれる（ResolveChoiceOptions と同じ理由）。
            // 連番も 1 回のコピーにつき 1 つの値として扱うため、ここでは進めない
            DateTime now = DateTime.Now;
            ExpandValues values = AddAppContext(new ExpandValues
            {
                Clipboard = () => clipboard,
                Sprint = _settings.Sprint,
                Inputs = inputs,
                Choices = choices,

                // HTML の項目では、差し込まれた値だけをエスケープする。
                // 利用者が書いたタグは生かしたまま、{input:…} や {choice:…} に入った
                // & や < が壊れた HTML にならないようにするため
                ValueTransform = ClipboardService.GetValueTransform(item.Format),
            }, appContext);

            // 件の数だけ展開して改行でつなぐ。
            // 通常は 1 件なので、結果も進む連番も従来とまったく同じになる
            IReadOnlyList<IReadOnlyDictionary<string, string>> rows =
                captures.Count > 0 ? captures : EmptyCaptureRows;

            StringBuilder builder = new();
            for (int i = 0; i < rows.Count; i++)
            {
                if (i > 0)
                {
                    builder.Append('\n');
                }

                builder.Append(TemplateEngine.Expand(
                    item.Text, now, item.SequenceValue, values with { Matches = rows[i] }));
            }

            string value = builder.ToString();

            if (!ClipboardService.TryCopy(value, item.Format))
            {
                System.Windows.MessageBox.Show(
                    "クリップボードにコピーできませんでした。他のアプリがクリップボードを使用している可能性があります。",
                    "MyTaskTray",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return;
            }

            if (_settings.ShowCopyNotification)
            {
                if (string.IsNullOrEmpty(value))
                {
                    // 空の文字列はクリップボードを空にする動作になるため、そのまま伝える
                    ToastWindow.ShowToast("クリップボードを空にしました", "コピーする文字列が空の項目です");
                }
                else
                {
                    string label = string.IsNullOrWhiteSpace(item.Name) ? "コピーしました" : item.Name;

                    // プレーンテキスト以外で載せた場合は、その旨も伝える。
                    // 貼り付け先によって結果が変わるので、何で載せたかが分かるほうがよい
                    if (item.HasFormat)
                    {
                        label += $" ({item.FormatLabel})";
                    }

                    // 条件に合わない行は飛ばすため、行数と件数は一致しない。
                    // 何件ぶんが入ったのかを言わないと、取りこぼしに気付けない
                    if (rows.Count > 1)
                    {
                        label += $"（{rows.Count} 件）";
                    }

                    ToastWindow.ShowToast(label, TemplateEngine.ToSingleLine(value, 120));
                }
            }

            // 連番を使った場合はカウンターを進めて保存する
            if (item.UsesSequence)
            {
                bool sequenceReset = item.AdvanceSequence();
                TrySaveSettings();

                // 設定画面は開いた時点の複製を持っているため、進んだ番号は自動では伝わらない。
                // 保存時に突き合わせるので値は失われないが、開いているあいだ
                // 画面の「次の番号」が実際と違う値のままになるので、その場で伝える
                _settingsWindow?.NotifySequenceAdvanced(item.Id, item.SequenceValue);

                if (sequenceReset)
                {
                    ToastWindow.ShowToast(
                        "連番を 1 に戻しました",
                        "上限または下限を超えるため、次の番号を初期値へ戻しました。今回のコピーは完了しています");
                }
            }
        }
    }
}
