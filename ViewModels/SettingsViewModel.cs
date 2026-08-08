using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Windows.Data;
using MyTaskTray.Models;
using MyTaskTray.Services;

namespace MyTaskTray.ViewModels
{
    public sealed record ClipboardMatchOption(
        ClipboardMatchKind Kind,
        string Name,
        string Description);

    /// <summary>コピーする形式の選択肢。</summary>
    public sealed record ClipFormatOption(
        ClipFormat Format,
        string Name,
        string Description);

    /// <summary>設定画面に表示する、組み込みアクション 1 件の表示設定。</summary>
    public sealed class ActionSettingRow : INotifyPropertyChanged
    {
        private bool _isVisible;

        internal ActionSettingRow(TrayActionDefinition action, bool isVisible)
        {
            Id = action.Id;
            Name = action.Label;
            Description = action.ToolTip;
            Group = action.GroupLabel;
            _isVisible = isVisible;
        }

        public string Id { get; }

        public string Name { get; }

        public string Description { get; }

        public string Group { get; }

        public bool IsVisible
        {
            get => _isVisible;
            set
            {
                if (_isVisible == value)
                {
                    return;
                }

                _isVisible = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsVisible)));
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;
    }

    /// <summary>
    /// 設定画面のためのビューモデル。
    /// </summary>
    public class SettingsViewModel : INotifyPropertyChanged
    {
        /// <summary>スプリントの基準日を入力・保存するときの表記。</summary>
        private const string SprintDateFormat = "yyyy-MM-dd";

        /// <summary>スプリントの長さとして受け付ける上限（日）。約 2 年。</summary>
        private const int MaxSprintLengthDays = 730;

        private readonly ICollectionView _itemsView;
        private readonly Dictionary<string, bool> _actionStates;
        private ForegroundApp _appContext;

        // 画面上で「次の番号」を直接編集した項目の Id。
        // 設定画面を開いている間にトレイ側で連番が進んでいた場合、
        // 編集していない項目はトレイ側の値を優先して取り込む（保存時に巻き戻してしまわないため）。
        private readonly HashSet<string> _sequenceEditedIds = new(StringComparer.Ordinal);

        // PropertyChanged を購読している項目。CollectionChanged の Reset（Clear など）では
        // OldItems が渡されず購読を外せないため、購読中の一覧を自分で持つ。
        private readonly List<ClipItem> _subscribedItems = [];

        private ClipItem? _selectedItem;
        private string _filterText = string.Empty;
        private bool _showCopyNotification;
        private string _menuHotKey = string.Empty;
        private bool _isDirty;

        // トレイ側で進んだ連番を取り込んでいる最中かどうか。
        // 取り込みは利用者の編集ではないため、「未保存」にも
        // 「画面で直接指定した番号」にも数えてはいけない。
        private bool _adoptingSequence;

        // スプリントの設定は入力途中でも打ち直せるよう文字列で持ち、
        // 解釈できたときだけ差し込みに反映する。
        private string _sprintAnchorText = string.Empty;
        private string _sprintLengthText = string.Empty;

        // プレビューに使うクリップボードの内容。
        // Preview の中で毎回読むと、入力欄を 1 文字打つたびにクリップボードを開くことになり、
        // 他アプリのコピー操作と競合する（ロック中は再試行のあいだ画面が止まる）。
        // ウィンドウがアクティブになったときなど、区切りのよいところでだけ読み直す。
        private string _clipboard = string.Empty;

        public SettingsViewModel(AppSettings settings)
            : this(settings, [], [], ForegroundApp.Unknown)
        {
        }

        public SettingsViewModel(AppSettings settings, IReadOnlyList<string> recentApps)
            : this(settings, recentApps, [], ForegroundApp.Unknown)
        {
        }

        internal SettingsViewModel(
            AppSettings settings,
            IReadOnlyList<string> recentApps,
            IReadOnlyList<TrayActionDefinition> actions)
            : this(settings, recentApps, actions, ForegroundApp.Unknown)
        {
        }

        internal SettingsViewModel(
            AppSettings settings,
            IReadOnlyList<string> recentApps,
            IReadOnlyList<TrayActionDefinition> actions,
            ForegroundApp appContext)
        {
            Version = settings.Version;
            _appContext = appContext;
            _showCopyNotification = settings.ShowCopyNotification;
            _menuHotKey = settings.MenuHotKey ?? string.Empty;
            _actionStates = new(settings.ActionStates ?? [], StringComparer.Ordinal);
            _sprintAnchorText = settings.SprintAnchorDate?.ToString(SprintDateFormat, CultureInfo.InvariantCulture)
                ?? string.Empty;
            _sprintLengthText = settings.SprintLengthDays.ToString(CultureInfo.InvariantCulture);

            Items = new ObservableCollection<ClipItem>(settings.Items);
            KnownCategories = [];
            KnownApps = [.. recentApps];
            (TrayActionDefinition Action, ActionSettingRow Row)[] actionRows =
            [
                .. actions.Select(action => (
                    action,
                    new ActionSettingRow(
                        action,
                        settings.IsActionVisible(action.Id, action.DefaultEnabled)))),
            ];
            ActionSettings = new ObservableCollection<ActionSettingRow>(
                actionRows.Select(entry => entry.Row));
            WorkToolActionSettings = new ObservableCollection<ActionSettingRow>(
                actionRows
                    .Where(entry => entry.Action.Kind != TrayActionKind.Contextual)
                    .Select(entry => entry.Row));
            ContextualActionSettings = new ObservableCollection<ActionSettingRow>(
                actionRows
                    .Where(entry => entry.Action.Kind == TrayActionKind.Contextual)
                    .Select(entry => entry.Row));
            foreach (ActionSettingRow action in ActionSettings)
            {
                action.PropertyChanged += OnActionSettingChanged;
            }

            Placeholders = new ObservableCollection<PlaceholderRow>(
                TemplateEngine.Placeholders.Select(p => new PlaceholderRow(p)));
            ClipboardMatchOptions =
            [
                new(ClipboardMatchKind.Always, "常に表示", "従来どおり通常のメニューに表示します。"),
                new(ClipboardMatchKind.HasText, "文字列がある", "クリップボードに文字列があるとき表示します。"),
                new(ClipboardMatchKind.Date, "日付", "2026-08-15 などの日付を読み取れるとき表示します。"),
                new(ClipboardMatchKind.Url, "Web URL", "http:// または https:// の URL のとき表示します。"),
                new(ClipboardMatchKind.Number, "数値", "クリップボード全体を数値として読めるとき表示します。"),
                new(ClipboardMatchKind.Json, "JSON", "JSON のオブジェクトまたは配列のとき表示します。"),
                new(ClipboardMatchKind.FilePath, "Windowsのパス", "ドライブ文字または UNC で始まるパスのとき表示します。"),
                new(ClipboardMatchKind.Email, "メールアドレス", "メールアドレスの形に一致するとき表示します。"),
                new(ClipboardMatchKind.Regex, "正規表現", "指定した正規表現に一致するとき表示します。"),
            ];
            ClipFormatOptions =
            [
                new(
                    ClipFormat.Plain,
                    "そのまま",
                    "書いた文字列をそのままコピーします。"),
                new(
                    ClipFormat.Markdown,
                    "Markdown",
                    "書いた内容を Markdown として解釈します。Word や Slack へ貼ると "
                        + "見出しや箇条書きになり、エディタへ貼ると書いたままの文字列が入ります。"),
                new(
                    ClipFormat.Html,
                    "HTML",

                    // 改行のことは必ず書く。HTML では生の改行がただの空白になるため、
                    // 複数行を書いた項目が Word や Slack で 1 行に潰れる。
                    // プレーンテキストでは改行が残るので、貼り付け先によって結果が違い、
                    // 何が起きているのか分かりにくい
                    "書いた内容を HTML として扱います。タグを直接書きたい場合に使います。"
                        + "改行を書いても Word や Slack では行が変わりません（<br> や <p> が要ります）。"
                        + "「- 」で箇条書きにしたい場合は Markdown を選んでください。"),
            ];

            _itemsView = CollectionViewSource.GetDefaultView(Items);
            _itemsView.Filter = o => o is ClipItem item && MatchesFilter(item);

            RefreshCategories();
            RefreshPlaceholderSamples();

            // 変更を検知して「未保存」の状態を持つ
            Items.CollectionChanged += OnItemsCollectionChanged;
            ResubscribeItems();

            SelectedItem = Items.FirstOrDefault();
        }

        public int Version { get; }

        public ObservableCollection<ClipItem> Items { get; }

        /// <summary>カテゴリ入力欄の候補。</summary>
        public ObservableCollection<string> KnownCategories { get; }

        /// <summary>
        /// 「現在のアプリ ▾」に出す候補。トレイメニューを開いたときに前面だったアプリを
        /// 新しい順に数件だけ持ち回したもの。利用者は実行ファイル名を知らないため、この候補が要る。
        /// </summary>
        public ObservableCollection<string> KnownApps { get; }

        /// <summary>すべての組み込みアクションの表示設定。保存と変更検知に使う。</summary>
        public ObservableCollection<ActionSettingRow> ActionSettings { get; }

        /// <summary>メニュー下部の「作業ツール」に表示されるアクション。</summary>
        public ObservableCollection<ActionSettingRow> WorkToolActionSettings { get; }

        /// <summary>条件に合うと「この内容でできること」に表示されるアクション。</summary>
        public ObservableCollection<ActionSettingRow> ContextualActionSettings { get; }

        public bool HasActionSettings => ActionSettings.Count > 0;

        public bool HasWorkToolActionSettings => WorkToolActionSettings.Count > 0;

        public bool HasContextualActionSettings => ContextualActionSettings.Count > 0;

        /// <summary>候補に出せる前面アプリがあるかどうか。</summary>
        public bool HasKnownApps => KnownApps.Count > 0;

        /// <summary>「差し込みを挿入」パネルに並べる一覧。</summary>
        public ObservableCollection<PlaceholderRow> Placeholders { get; }

        /// <summary>スマートアクションの表示条件として選べる一覧。</summary>
        public IReadOnlyList<ClipboardMatchOption> ClipboardMatchOptions { get; }

        /// <summary>コピーする形式として選べる一覧。</summary>
        public IReadOnlyList<ClipFormatOption> ClipFormatOptions { get; }

        /// <summary>選択項目の形式の説明。何が起きるかを短く出す。</summary>
        public string ClipFormatStatus
        {
            get
            {
                if (SelectedItem is null || SelectedItem.IsSeparator)
                {
                    return string.Empty;
                }

                ClipFormatOption? option = ClipFormatOptions.FirstOrDefault(
                    o => o.Format == SelectedItem.Format);

                return option is null
                    ? "保存されている形式を解釈できません。形式を選び直してください。"
                    : option.Description;
            }
        }

        /// <summary>
        /// ユーザーが画面上で連番の値を直接編集した項目の <see cref="ClipItem.Id"/>。
        /// 保存時、この項目だけは画面の値をそのまま使う。
        /// </summary>
        public IReadOnlySet<string> SequenceEditedIds => _sequenceEditedIds;

        /// <summary>保存されていない変更があるかどうか。</summary>
        public bool IsDirty
        {
            get => _isDirty;
            private set
            {
                if (_isDirty == value)
                {
                    return;
                }

                _isDirty = value;
                OnPropertyChanged();
            }
        }

        /// <summary>コピー時に通知を出すかどうか。</summary>
        public bool ShowCopyNotification
        {
            get => _showCopyNotification;
            set
            {
                if (_showCopyNotification == value)
                {
                    return;
                }

                _showCopyNotification = value;
                IsDirty = true;
                OnPropertyChanged();
            }
        }

        /// <summary>
        /// トレイメニューを表示するグローバルホットキー。空欄なら無効。
        /// 入力途中を許すため文字列で持ち、保存時に解釈できるか検証する。
        /// </summary>
        public string MenuHotKey
        {
            get => _menuHotKey;
            set
            {
                string next = value ?? string.Empty;
                if (string.Equals(_menuHotKey, next, StringComparison.Ordinal))
                {
                    return;
                }

                _menuHotKey = next;
                IsDirty = true;
                OnPropertyChanged();
                OnPropertyChanged(nameof(MenuHotKeyStatus));
            }
        }

        /// <summary>ホットキー入力欄の下に表示する、無効・正常・エラーの説明。</summary>
        public string MenuHotKeyStatus
        {
            get
            {
                if (string.IsNullOrWhiteSpace(_menuHotKey))
                {
                    return "空欄のため、ホットキーは無効です。";
                }

                return HotKeyGesture.TryParse(_menuHotKey, out HotKeyGesture gesture, out string error)
                    ? $"保存後に {gesture.DisplayName} でメニューを表示します。"
                    : error;
            }
        }

        /// <summary>
        /// 保存用にホットキーを検証し、Ctrl+Alt+V のような統一表記へ整える。
        /// 空欄は有効な「無効」設定として受け付ける。
        /// </summary>
        public bool TryGetNormalizedMenuHotKey(out string normalized, out string error)
        {
            if (string.IsNullOrWhiteSpace(_menuHotKey))
            {
                normalized = string.Empty;
                error = string.Empty;
                return true;
            }

            if (!HotKeyGesture.TryParse(_menuHotKey, out HotKeyGesture gesture, out error))
            {
                normalized = string.Empty;
                return false;
            }

            normalized = gesture.DisplayName;
            return true;
        }

        /// <summary>
        /// スプリントの基準日（yyyy-MM-dd）。どれか 1 つのスプリントの開始日を書く。
        /// 空、または解釈できない文字列のあいだは <c>@sprint</c> の差し込みを展開しない。
        /// </summary>
        public string SprintAnchorText
        {
            get => _sprintAnchorText;
            set
            {
                string next = value ?? string.Empty;
                if (string.Equals(_sprintAnchorText, next, StringComparison.Ordinal))
                {
                    return;
                }

                _sprintAnchorText = next;
                IsDirty = true;
                OnPropertyChanged();
                OnSprintChanged();
            }
        }

        /// <summary>スプリント 1 つの長さ（日数）。</summary>
        public string SprintLengthText
        {
            get => _sprintLengthText;
            set
            {
                string next = value ?? string.Empty;
                if (string.Equals(_sprintLengthText, next, StringComparison.Ordinal))
                {
                    return;
                }

                _sprintLengthText = next;
                IsDirty = true;
                OnPropertyChanged();
                OnSprintChanged();
            }
        }

        /// <summary>
        /// 入力から組み立てた区切り。解釈できなければ null（差し込みは書いたまま残る）。
        /// </summary>
        public SprintSchedule? Sprint
        {
            get => TryGetSprintSchedule(out SprintSchedule? sprint, out _) ? sprint : null;
        }

        /// <summary>
        /// スプリント入力を保存できる状態か検証する。
        /// 基準日の空欄は意図的な「未設定」として受け付けるが、
        /// 何か入力されていて解釈できない場合は保存させない。
        /// </summary>
        public bool TryGetSprintSchedule(out SprintSchedule? sprint, out string error)
        {
            sprint = null;
            error = string.Empty;

            if (string.IsNullOrWhiteSpace(_sprintAnchorText))
            {
                return true;
            }

            if (!DateTime.TryParseExact(
                    _sprintAnchorText.Trim(),
                    SprintDateFormat,
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.None,
                    out DateTime anchor))
            {
                error = $"基準日は {SprintDateFormat} 形式（例 2026-04-06）で入力してください。";
                return false;
            }

            if (!int.TryParse(
                    _sprintLengthText.Trim(),
                    NumberStyles.Integer,
                    CultureInfo.InvariantCulture,
                    out int length)
                || length < 1
                || length > MaxSprintLengthDays)
            {
                error = $"スプリントの長さは 1〜{MaxSprintLengthDays} の整数で入力してください。";
                return false;
            }

            sprint = new SprintSchedule(anchor, length);
            return true;
        }

        /// <summary>スプリント設定の入力欄の下に出す説明。いまのスプリントの期間か、誤りの内容。</summary>
        public string SprintStatus
        {
            get
            {
                if (string.IsNullOrWhiteSpace(_sprintAnchorText))
                {
                    return "基準日を入れると {date@sprint} などが使えるようになります。";
                }

                if (!TryGetSprintSchedule(out SprintSchedule? sprint, out string error))
                {
                    return error;
                }

                DateTime start = sprint!.StartOf(DateTime.Now);
                DateTime end = start.AddDays(sprint.LengthDays - 1);
                return $"いまのスプリント: {start:yyyy/MM/dd}（{start:ddd}）〜 {end:yyyy/MM/dd}（{end:ddd}）";
            }
        }

        /// <summary>一覧の絞り込みキーワード。</summary>
        public string FilterText
        {
            get => _filterText;
            set
            {
                string next = value ?? string.Empty;
                if (string.Equals(_filterText, next, StringComparison.Ordinal))
                {
                    return;
                }

                _filterText = next;
                _itemsView.Refresh();

                OnPropertyChanged();
                OnPropertyChanged(nameof(HasFilter));
                OnPropertyChanged(nameof(CanReorder));
                OnPropertyChanged(nameof(StatusText));
            }
        }

        /// <summary>絞り込み中かどうか。</summary>
        public bool HasFilter => !string.IsNullOrEmpty(_filterText);

        public ClipItem? SelectedItem
        {
            get => _selectedItem;
            set
            {
                if (ReferenceEquals(_selectedItem, value))
                {
                    return;
                }

                _selectedItem?.PropertyChanged -= OnSelectedItemPropertyChanged;

                _selectedItem = value;

                _selectedItem?.PropertyChanged += OnSelectedItemPropertyChanged;

                OnPropertyChanged();
                OnPropertyChanged(nameof(HasSelection));
                OnPropertyChanged(nameof(CanReorder));
                OnPropertyChanged(nameof(IsItemEditable));
                OnPropertyChanged(nameof(IsSequenceVisible));
                OnPropertyChanged(nameof(IsChoiceVisible));
                OnPropertyChanged(nameof(ChoiceStatus));
                OnPropertyChanged(nameof(ShowEditorHint));
                OnPropertyChanged(nameof(EditorHint));
                OnPropertyChanged(nameof(Preview));
                OnPropertyChanged(nameof(NeedsPreviewRefresh));
                OnPropertyChanged(nameof(ClipboardConditionStatus));
                OnPropertyChanged(nameof(AppConditionStatus));
                OnPropertyChanged(nameof(ClipFormatStatus));
            }
        }

        public bool HasSelection => SelectedItem is not null;

        /// <summary>並べ替えできるのは、絞り込みをしていないときだけ。</summary>
        public bool CanReorder => HasSelection && !HasFilter;

        /// <summary>区切り線は編集する内容がないため、編集欄自体を出さない。</summary>
        public bool IsItemEditable => SelectedItem is not null && !SelectedItem.IsSeparator;

        /// <summary>編集欄の代わりに案内を出すかどうか。</summary>
        public bool ShowEditorHint => !IsItemEditable;

        /// <summary>編集できないときに出す案内。</summary>
        public string EditorHint => SelectedItem is null
            ? "左の一覧から項目を選ぶか、「追加」で新しい項目を作成してください。"
            : "区切り線には編集する内容がありません。メニューのグループ分けに使えます。";

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

                bool matched = ClipboardMatcher.Match(SelectedItem, _clipboard).IsMatch;
                return option.Description + (matched
                    ? " 現在のクリップボードには一致しています。"
                    : " 現在のクリップボードには一致していません。");
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

                ClipboardMatchResult match = SelectedItem.HasSmartCondition
                    ? ClipboardMatcher.Match(SelectedItem, _clipboard)
                    : ClipboardMatchResult.NoMatch;

                DateTime now = DateTime.Now;
                int sequence = SelectedItem.SequenceValue;

                ExpandValues values = new()
                {
                    Clipboard = () => _clipboard,
                    Sprint = Sprint,
                    Matches = match.IsMatch ? match.Captures : null,
                    AppName = _appContext.IsKnown && _appContext.Name.Length > 0
                        ? _appContext.Name
                        : null,
                    AppTitle = _appContext.IsKnown && _appContext.Title.Length > 0
                        ? _appContext.Title
                        : null,
                };

                return TemplateEngine.Expand(
                    SelectedItem.Text,
                    now,
                    sequence,
                    values with
                    {
                        // 選ぶのはコピーのときなので、ここでは先頭の選択肢を代表として使う。
                        // 代表値であることは ChoiceStatus で伝える
                        Choices = TemplateEngine.GetDefaultChoices(
                            SelectedItem.Text, now, sequence, values),

                        // 実際にコピーされる文字列を出すのが目的なので、
                        // 形式ごとの後処理もここで通しておく。通さないと、
                        // HTML の項目でプレビューと実際のコピー内容が食い違う
                        ValueTransform = ClipboardService.GetValueTransform(SelectedItem.Format),
                    });
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

        /// <summary>既存項目のカテゴリを重複なく集めて候補を作り直す。</summary>
        public void RefreshCategories()
        {
            // 前後の空白の有無で候補が分かれないよう、トリムしてから重複を除く
            List<string> categories = [.. Items
                .Select(i => i.Category.Trim())
                .Where(c => !string.IsNullOrEmpty(c))
                .Distinct(StringComparer.Ordinal)
                .OrderBy(c => c, StringComparer.CurrentCulture)];

            KnownCategories.Clear();
            foreach (string category in categories)
            {
                KnownCategories.Add(category);
            }

            OnPropertyChanged(nameof(HasCategories));
        }

        /// <summary>カテゴリ候補があるかどうか。</summary>
        public bool HasCategories => KnownCategories.Count > 0;

        /// <summary>
        /// 保存用の設定オブジェクトを作る。ホットキーとスプリントは保存前に検証済みの値を受け取る。
        /// </summary>
        public AppSettings ToSettings(string normalizedMenuHotKey, SprintSchedule? validatedSprint)
        {
            Dictionary<string, bool> actionStates = new(_actionStates, StringComparer.Ordinal);
            foreach (ActionSettingRow action in ActionSettings)
            {
                actionStates[action.Id] = action.IsVisible;
            }

            return new()
            {
                Version = Version,
                ShowCopyNotification = ShowCopyNotification,
                MenuHotKey = normalizedMenuHotKey,
                ActionStates = actionStates,
                SprintAnchorDate = validatedSprint?.AnchorDate,
                SprintLengthDays = validatedSprint?.LengthDays ?? 14,
                Items = [.. Items.Select(i => i.Clone())],
            };
        }

        /// <summary>保存が完了したことを伝える。</summary>
        public void MarkSaved() => IsDirty = false;

        private void OnActionSettingChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (sender is not ActionSettingRow action || e.PropertyName != nameof(ActionSettingRow.IsVisible))
            {
                return;
            }

            _actionStates[action.Id] = action.IsVisible;
            IsDirty = true;
        }

        /// <summary>全項目の正規表現条件を保存前に検証する。</summary>
        public bool TryValidateSmartConditions(out ClipItem? invalidItem, out string error)
        {
            foreach (ClipItem item in Items)
            {
                if (item.IsSeparator)
                {
                    continue;
                }

                if (!Enum.IsDefined(item.ClipboardCondition))
                {
                    invalidItem = item;
                    error = "表示条件を解釈できません。条件を選び直してください。";
                    return false;
                }

                if (item.ClipboardCondition == ClipboardMatchKind.Regex
                    && !ClipboardMatcher.TryValidateRegex(item.ClipboardPattern, out error))
                {
                    invalidItem = item;
                    return false;
                }

                // 空欄は「タイトルを見ない」という有効な状態なので、書かれている場合だけ検証する
                if (!AppContextMatcher.TryValidateTitlePattern(item.AppTitlePattern, out error))
                {
                    invalidItem = item;
                    return false;
                }
            }

            invalidItem = null;
            error = string.Empty;
            return true;
        }

        private bool MatchesFilter(ClipItem item)
        {
            if (!HasFilter)
            {
                return true;
            }

            // 絞り込み中は区切り線を隠す（検索結果としては意味がないため）
            if (item.IsSeparator)
            {
                return false;
            }

            return item.Name.Contains(_filterText, StringComparison.CurrentCultureIgnoreCase)
                || item.Text.Contains(_filterText, StringComparison.CurrentCultureIgnoreCase)
                || item.Category.Contains(_filterText, StringComparison.CurrentCultureIgnoreCase)
                || item.ClipboardPattern.Contains(_filterText, StringComparison.CurrentCultureIgnoreCase)

                // アプリ条件で隠れている項目を「見当たらない」まま終わらせないため、検索でも見つかるようにする
                || item.AppProcess.Contains(_filterText, StringComparison.CurrentCultureIgnoreCase)
                || item.AppTitlePattern.Contains(_filterText, StringComparison.CurrentCultureIgnoreCase);
        }

        private void OnItemsCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
        {
            // Reset（Clear など）では OldItems / NewItems が渡されないため、
            // 差分ではなく購読し直す。項目数はたかだか数十なのでコストは問題にならない。
            ResubscribeItems();

            IsDirty = true;
            OnPropertyChanged(nameof(StatusText));
        }

        /// <summary>現在の項目に PropertyChanged を張り直す。</summary>
        private void ResubscribeItems()
        {
            foreach (ClipItem item in _subscribedItems)
            {
                item.PropertyChanged -= OnAnyItemPropertyChanged;
            }

            _subscribedItems.Clear();

            foreach (ClipItem item in Items)
            {
                item.PropertyChanged += OnAnyItemPropertyChanged;
                _subscribedItems.Add(item);
            }
        }

        private void OnAnyItemPropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            switch (e.PropertyName)
            {
                case nameof(ClipItem.Name):
                case nameof(ClipItem.Text):
                case nameof(ClipItem.Category):
                case nameof(ClipItem.IsSeparator):
                case nameof(ClipItem.ClipboardCondition):
                case nameof(ClipItem.ClipboardPattern):
                case nameof(ClipItem.AppProcess):
                case nameof(ClipItem.AppTitlePattern):
                    IsDirty = true;

                    // 絞り込み中は表示件数が変わるため、件数の表示も作り直す
                    OnPropertyChanged(nameof(StatusText));
                    break;

                case nameof(ClipItem.SequenceStep):
                    IsDirty = true;
                    break;

                case nameof(ClipItem.SequenceValue):
                    // トレイ側で進んだ値の取り込みは利用者の編集ではない。
                    // ここで数えてしまうと、開いているだけで「未保存」になり、
                    // そのうえ以降の取り込みが止まってしまう
                    if (_adoptingSequence)
                    {
                        break;
                    }

                    IsDirty = true;

                    // 「次の番号」を画面で直接指定した場合は、トレイ側で進んだ値より優先する。
                    // 増分だけを変えたときは番号に触っていないため、ここには入らない。
                    if (sender is ClipItem edited && !string.IsNullOrEmpty(edited.Id))
                    {
                        _sequenceEditedIds.Add(edited.Id);
                    }

                    break;
            }

            if (e.PropertyName == nameof(ClipItem.Category))
            {
                RefreshCategories();
            }
        }

        private void OnSelectedItemPropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            switch (e.PropertyName)
            {
                case nameof(ClipItem.Text):
                    OnPropertyChanged(nameof(Preview));
                    OnPropertyChanged(nameof(IsSequenceVisible));
                    OnPropertyChanged(nameof(IsChoiceVisible));
                    OnPropertyChanged(nameof(ChoiceStatus));
                    OnPropertyChanged(nameof(NeedsPreviewRefresh));
                    break;

                case nameof(ClipItem.ClipboardCondition):
                case nameof(ClipItem.ClipboardPattern):
                    OnPropertyChanged(nameof(Preview));
                    OnPropertyChanged(nameof(ClipboardConditionStatus));
                    break;

                // 形式が変わると、差し込んだ値のエスケープの有無が変わる。
                // プレビューは「実際にコピーされる文字列」なので作り直す
                case nameof(ClipItem.Format):
                    OnPropertyChanged(nameof(Preview));
                    OnPropertyChanged(nameof(ClipFormatStatus));
                    break;

                case nameof(ClipItem.AppProcess):
                case nameof(ClipItem.AppTitlePattern):
                    OnPropertyChanged(nameof(AppConditionStatus));
                    break;

                case nameof(ClipItem.SequenceValue):
                case nameof(ClipItem.SequenceStep):
                    OnPropertyChanged(nameof(Preview));
                    break;
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        protected void OnPropertyChanged([CallerMemberName] string? name = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
