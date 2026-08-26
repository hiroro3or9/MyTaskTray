using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using System.Windows.Data;
using MyTaskTray.Models;
using MyTaskTray.Services;

namespace MyTaskTray.ViewModels
{
    /// <summary>選択項目の状態説明と、実際にコピーされる文字列のプレビューを組み立てる。</summary>
    public partial class SettingsViewModel
    {
        /// <summary>選択項目が連番を使っているときだけ、連番の設定欄を出す。</summary>
        public bool IsSequenceVisible => IsItemEditable && SelectedItem!.UsesSequence;

        /// <summary>選択項目が <c>{choice:…}</c> を使っているときだけ、選択肢の説明を出す。</summary>
        public bool IsChoiceVisible => IsItemEditable && SelectedItem!.UsesChoices;

        /// <summary>
        /// 選択項目の <c>{choice:…}</c> の内訳と、書き方の誤り。
        ///
        /// <para>
        /// 誤りのある選択肢は展開されず、書いたままの文字列がコピーされる。
        /// 放っておいてもコピー結果から気付けるが、気付くのが「貼り付けたあと」になるため、
        /// スマート条件の説明（<see cref="ClipboardConditionStatus"/>）と同じ形でここに出す。
        /// </para>
        /// </summary>
        public string ChoiceStatus
        {
            get
            {
                if (!IsItemEditable)
                {
                    return string.Empty;
                }

                ChoiceAnalysis analysis = TemplateEngine.AnalyzeChoices(SelectedItem!.Text);
                List<string> lines = [];

                if (analysis.Definitions.Count > 0)
                {
                    string list = string.Join(
                        " → ",
                        analysis.Definitions.Select(d => d.AllowMultiple
                            ? $"{d.Name}（{d.Options.Count} 個から複数）"
                            : $"{d.Name}（{d.Options.Count} 択）"));

                    lines.Add($"{analysis.Definitions.Count} か所で選びます: {list}");

                    // プレビューは「実際にコピーされる文字列」と説明しているので、
                    // 代表値を見せていることを黙っていない
                    lines.Add("プレビューには先頭の選択肢を表示しています。");
                }

                foreach (ChoiceIssue issue in analysis.Issues)
                {
                    lines.Add(DescribeChoiceIssue(issue));
                }

                return string.Join("\n", lines);
            }
        }

        private static string DescribeChoiceIssue(ChoiceIssue issue) => issue.Kind switch
        {
            ChoiceIssueKind.NameHasPipe
                => $"「{issue.Name}」: 名前に | は使えません。"
                    + "{choice:名前:選択肢|選択肢} の形で、先に名前を書いてください。",

            ChoiceIssueKind.TooFewOptions
                => $"「{issue.Name}」: 選択肢は | で区切って "
                    + $"{TemplateEngine.MinChoiceOptions}〜{TemplateEngine.MaxChoiceOptions} 個書いてください。",

            ChoiceIssueKind.TooManyOptions
                => $"「{issue.Name}」: 選択肢が多すぎます（{TemplateEngine.MaxChoiceOptions} 個まで）。",

            ChoiceIssueKind.Duplicate
                => $"「{issue.Name}」: 同じ名前の定義が 2 つ以上あります。最初のものを使います。",

            ChoiceIssueKind.Undefined
                => $"「{issue.Name}」: 選択肢が書かれていません。"
                    + $"どこかに {{choice:{issue.Name}:選択肢|選択肢}} と書いてください。",

            ChoiceIssueKind.UnsupportedPlaceholderInOption
                => $"「{issue.Name}」: 選択肢の中では "
                    + "{input:…} {choice:…} {choices:…} {seq} は使えません。"
                    + "（{date} や {clip} などは使えます）",

            _ => string.Empty,
        };


        /// <summary>選択項目のスマート条件の説明と、現在のクリップボードに対する判定。</summary>
        public string ClipboardConditionStatus
        {
            get
            {
                if (SelectedItem is null || SelectedItem.IsSeparator)
                {
                    return string.Empty;
                }

                ClipboardMatchOption? option = ClipboardMatchOptions.FirstOrDefault(
                    o => o.Kind == SelectedItem.ClipboardCondition);
                if (option is null)
                {
                    return "保存されている表示条件を解釈できません。条件を選び直してください。";
                }

                if (SelectedItem.ClipboardCondition == ClipboardMatchKind.Always)
                {
                    return option.Description;
                }

                if (SelectedItem.ClipboardCondition == ClipboardMatchKind.Regex
                    && !ClipboardMatcher.TryValidateRegex(SelectedItem.ClipboardPattern, out string error))
                {
                    return error;
                }

                ClipboardMatchRows rows = ClipboardMatcher.MatchEach(SelectedItem, _clipboard);

                if (!SelectedItem.UsesEachLine)
                {
                    return option.Description + (rows.IsMatch
                        ? " 現在のクリップボードには一致しています。"
                        : " 現在のクリップボードには一致していません。");
                }

                string unit = SelectedItem.IsRegexCondition
                    ? "クリップボード全体に繰り返し当て、一致した箇所ごとに 1 件を作ります"
                        + "（^ と $ は各行の先頭・末尾になります）。"
                    : "各行を 1 件として照合します。";

                if (!rows.IsMatch)
                {
                    return option.Description + " " + unit
                        + "現在のクリップボードには一致がありません。";
                }

                return option.Description + " " + unit
                    + $"現在のクリップボードでは {rows.Count} 件になります"
                    + (rows.Truncated
                        ? $"（上限 {ClipboardMatcher.MaxBulkRows} 件で打ち切りました）"
                        : string.Empty)
                    + "。";
            }
        }

        /// <summary>選択項目のアプリ条件の説明。何が起きるかを、書いた内容から組み立てて出す。</summary>
        public string AppConditionStatus
        {
            get
            {
                if (SelectedItem is null || SelectedItem.IsSeparator)
                {
                    return string.Empty;
                }

                if (!SelectedItem.HasAppCondition)
                {
                    return "空欄のままなら、どのアプリを使っていても表示します。";
                }

                if (!AppContextMatcher.TryValidateTitlePattern(SelectedItem.AppTitlePattern, out string error))
                {
                    return error;
                }

                IReadOnlyList<string> apps = AppContextMatcher.SplitProcessNames(SelectedItem.AppProcess);
                bool hasTitle = !string.IsNullOrWhiteSpace(SelectedItem.AppTitlePattern);

                string app = apps.Count switch
                {
                    0 => string.Empty,
                    1 => $"{apps[0]} が前面",
                    _ => $"{string.Join(" / ", apps)} のいずれかが前面",
                };

                string title = hasTitle ? "ウィンドウタイトルが正規表現に一致する" : string.Empty;

                string condition = (app.Length, title.Length) switch
                {
                    (> 0, > 0) => app + "で、" + title,
                    (> 0, _) => app + "の",
                    _ => title,
                };

                return condition + "ときだけ表示します。"
                    + "前面のアプリを判別できない場合は、隠さずに表示します。";
            }
        }

        /// <summary>
        /// プレビューを一定間隔で更新し続ける必要があるかどうか。
        /// <c>{time}</c> のように時間の経過で変わる差し込みを含むときだけ true。
        /// 常に更新すると、<c>{guid}</c> や <c>{random}</c> を含む項目のプレビューが
        /// 毎秒書き換わってしまい「実際にコピーされる文字列」という表示と食い違う。
        /// </summary>
        public bool NeedsPreviewRefresh
            => IsItemEditable && TemplateEngine.ContainsTimeSensitive(SelectedItem!.Text);

        /// <summary>一覧の下に出す件数の表示。</summary>
        public string StatusText
        {
            get
            {
                int total = Items.Count;
                int copyItems = Items.Count(i => !i.IsSeparator);

                if (!HasFilter)
                {
                    return $"{copyItems} 項目（区切り線 {total - copyItems}）";
                }

                int shown = Items.Count(MatchesFilter);
                return $"{shown} / {copyItems} 項目を表示中（絞り込み中は並べ替えできません）";
            }
        }

        /// <summary>差し込みを展開した結果。実際にコピーされる文字列。</summary>
        public string Preview
        {
            get
            {
                if (SelectedItem is null || SelectedItem.IsSeparator)
                {
                    return string.Empty;
                }

                ClipboardMatchRows rows = SelectedItem.HasSmartCondition
                    ? ClipboardMatcher.MatchEach(SelectedItem, _clipboard)
                    : ClipboardMatchRows.None;

                DateTime now = DateTime.Now;
                int sequence = SelectedItem.SequenceValue;

                ExpandValues values = new()
                {
                    Clipboard = () => _clipboard,
                    Sprint = Sprint,

                    // 代表として先頭の件を使う。複数件あるときは下で件ごとに差し替える
                    Matches = rows.IsMatch ? rows.Rows[0].Captures : null,
                    AppName = _appContext.IsKnown && _appContext.Name.Length > 0
                        ? _appContext.Name
                        : null,
                    AppTitle = _appContext.IsKnown && _appContext.Title.Length > 0
                        ? _appContext.Title
                        : null,
                };

                ExpandValues resolved = values with
                {
                    // 選ぶのはコピーのときなので、ここでは先頭の選択肢を代表として使う。
                    // 代表値であることは ChoiceStatus で伝える
                    Choices = TemplateEngine.GetDefaultChoices(
                        SelectedItem.Text, now, sequence, values),

                    // 実際にコピーされる文字列を出すのが目的なので、
                    // 形式ごとの後処理もここで通しておく。通さないと、
                    // HTML の項目でプレビューと実際のコピー内容が食い違う
                    ValueTransform = ClipboardService.GetValueTransform(SelectedItem.Format),
                };

                // 差し込む値の並び。条件に合わなければ 1 件だけ null を置く
                // （{match:…} は書いたままの文字列として残る）。
                // トレイでのコピー（CopyToClipboard）と同じく、件の数だけ展開して改行でつなぐ
                List<IReadOnlyDictionary<string, string>?> matches = [];
                if (rows.IsMatch)
                {
                    foreach (ClipboardMatchResult row in rows.Rows)
                    {
                        matches.Add(row.Captures);
                    }
                }
                else
                {
                    matches.Add(null);
                }

                StringBuilder builder = new();
                for (int i = 0; i < matches.Count; i++)
                {
                    if (i > 0)
                    {
                        builder.Append('\n');
                    }

                    builder.Append(TemplateEngine.Expand(
                        SelectedItem.Text, now, sequence, resolved with { Matches = matches[i] }));
                }

                return builder.ToString();
            }
        }

        /// <summary>スプリントの設定が変わったので、それに依存する表示を作り直す。</summary>
        private void OnSprintChanged()
        {
            OnPropertyChanged(nameof(SprintStatus));
            OnPropertyChanged(nameof(Preview));
            RebuildPlaceholderSamples();
        }

        /// <summary>時刻の差し込みに追従させるため、外から再評価を促す。</summary>
        public void RefreshPreview() => OnPropertyChanged(nameof(Preview));

        /// <summary>
        /// プレビューに使うクリップボードの内容を読み直す。
        /// 他アプリでコピーしてから設定画面に戻ってきたときなど、区切りのよいところで呼ぶ。
        /// </summary>
        public void RefreshClipboard()
        {
            string latest = ClipboardService.GetText();
            if (string.Equals(_clipboard, latest, StringComparison.Ordinal))
            {
                return;
            }

            _clipboard = latest;
            OnPropertyChanged(nameof(Preview));
            OnPropertyChanged(nameof(ClipboardConditionStatus));
        }

        /// <summary>差し込み一覧の「現在値」を今の時刻で作り直す。</summary>
        public void RefreshPlaceholderSamples()
        {
            // クリップボードの読み取りは一覧全体で 1 回で済ませる
            RefreshClipboard();
            RebuildPlaceholderSamples();
        }

        /// <summary>
        /// トレイメニューを開いたときに取得できた最新の外部アプリを、
        /// app 系差し込みのプレビューへ反映する。
        /// </summary>
        internal void UpdateAppContext(ForegroundApp appContext)
        {
            if (!appContext.IsKnown || appContext == _appContext)
            {
                return;
            }

            _appContext = appContext;
            OnPropertyChanged(nameof(Preview));
            RebuildPlaceholderSamples();
        }

        /// <summary>
        /// 覚えているクリップボードの内容のまま、差し込み一覧の「現在値」を作り直す。
        /// スプリントの入力欄のように 1 文字ごとに呼ばれる場面では、
        /// 毎回クリップボードを開くと他アプリのコピー操作と競合するため、読み直さない。
        /// </summary>
        private void RebuildPlaceholderSamples()
        {
            DateTime now = DateTime.Now;
            int sequence = SelectedItem?.SequenceValue ?? 1;
            SprintSchedule? sprint = Sprint;

            ExpandValues values = new()
            {
                Clipboard = () => _clipboard,
                Sprint = sprint,
                AppName = _appContext.IsKnown && _appContext.Name.Length > 0
                    ? _appContext.Name
                    : null,
                AppTitle = _appContext.IsKnown && _appContext.Title.Length > 0
                    ? _appContext.Title
                    : null,
            };

            foreach (PlaceholderRow row in Placeholders)
            {
                row.Sample = TemplateEngine.ToSingleLine(
                    TemplateEngine.Expand(
                        row.Token,
                        now,
                        sequence,
                        values with
                        {
                            // プレビューと同じ規則。選択肢は先頭のものを代表として出す
                            Choices = TemplateEngine.GetDefaultChoices(
                                row.Token, now, sequence, values),
                        }),
                    60);
            }
        }

        /// <summary>
        /// トレイからのコピーで進んだ連番を、画面の表示にも取り込む。
        ///
        /// 設定画面は設定の複製を持っているため、トレイ側で番号が進んでも
        /// 黙っていると画面の「次の番号」が古いままになる。
        /// 保存時には <c>AdoptExternalSequenceValues()</c> が突き合わせるので値は失われないが、
        /// 開いているあいだ実際と違う番号が見えているのは紛らわしい。
        ///
        /// 画面で「次の番号」を直接編集した項目は、利用者の指定を優先して対象外にする
        /// （保存時の突き合わせと同じ規則）。
        /// </summary>
        public void AdoptSequenceValue(string id, int value)
        {
            if (string.IsNullOrEmpty(id) || _sequenceEditedIds.Contains(id))
            {
                return;
            }

            ClipItem? item = Items.FirstOrDefault(i => string.Equals(i.Id, id, StringComparison.Ordinal));
            if (item is null || item.SequenceValue == value)
            {
                return;
            }

            _adoptingSequence = true;
            try
            {
                // 値の変更は ClipItem 自身が通知するため、画面の表示とプレビューは自動で追従する
                item.SequenceValue = value;
            }
            finally
            {
                _adoptingSequence = false;
            }
        }
    }
}
