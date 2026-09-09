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
    public sealed record ClipboardMatchOption(
        ClipboardMatchKind Kind,
        string Name,
        string Description);

    /// <summary>コピーする形式の選択肢。</summary>
    public sealed record ClipFormatOption(
        ClipFormat Format,
        string Name,
        string Description);

    /// <summary>連続コピーが 1 件として集める範囲の選択肢。</summary>
    public sealed record SequentialCaptureOption(
        SequentialCaptureTrigger Trigger,
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
    public partial class SettingsViewModel : INotifyPropertyChanged
    {
        /// <summary>スプリントの基準日を入力・保存するときの表記。</summary>
        private const string SprintDateFormat = "yyyy-MM-dd";

        /// <summary>スプリントの長さとして受け付ける上限（日）。約 2 年。</summary>
        private const int MaxSprintLengthDays = 730;

        private readonly ICollectionView _itemsView;
        private readonly Dictionary<string, bool> _actionStates;
        private readonly List<ClipCategory> _categories;
        private readonly HashSet<(MenuOutlineSection Section, string CategoryId)> _collapsedCategories = [];
        private ForegroundApp _appContext;

        // 画面上で「次の番号」を直接編集した項目の Id。
        // 設定画面を開いている間にトレイ側で連番が進んでいた場合、
        // 編集していない項目はトレイ側の値を優先して取り込む（保存時に巻き戻してしまわないため）。
        private readonly HashSet<string> _sequenceEditedIds = new(StringComparer.Ordinal);

        // PropertyChanged を購読している項目。CollectionChanged の Reset（Clear など）では
        // OldItems が渡されず購読を外せないため、購読中の一覧を自分で持つ。
        private readonly List<ClipItem> _subscribedItems = [];

        private ClipItem? _selectedItem;
        private MenuOutlineRow? _selectedOutlineRow;
        private string _categoryNameDraft = string.Empty;
        private string _filterText = string.Empty;
        private bool _showCopyNotification;
        private SequentialCaptureTrigger _sequentialCaptureTrigger;
        private string _menuHotKey = string.Empty;
        private string _swapCopyHotKey = string.Empty;
        private bool _isDirty;

        // トレイ側で進んだ連番を取り込んでいる最中かどうか。
        // 取り込みは利用者の編集ではないため、「未保存」にも
        // 「画面で直接指定した番号」にも数えてはいけない。
        private bool _adoptingSequence;

        // カテゴリ名の一括変更や並べ替えでは PropertyChanged / CollectionChanged が連続する。
        // 途中の不完全な一覧を何度も作らず、操作の最後に 1 度だけアウトラインを更新する。
        private bool _suppressOutlineRebuild;
        private bool _rebuildingOutline;
        private bool _synchronizingCategories;

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
            SettingsStructure.Normalize(settings);
            SettingsStructure.ApplyLayoutOrder(settings);

            Version = settings.Version;
            _categories = [.. settings.Categories.Select(category => category.Clone())];
            _appContext = appContext;
            _showCopyNotification = settings.ShowCopyNotification;
            _sequentialCaptureTrigger = settings.SequentialCaptureTrigger;
            _menuHotKey = settings.MenuHotKey ?? string.Empty;
            _swapCopyHotKey = settings.SwapCopyHotKey ?? string.Empty;
            _actionStates = new(settings.ActionStates ?? [], StringComparer.Ordinal);
            _sprintAnchorText = settings.SprintAnchorDate?.ToString(SprintDateFormat, CultureInfo.InvariantCulture)
                ?? string.Empty;
            _sprintLengthText = settings.SprintLengthDays.ToString(CultureInfo.InvariantCulture);

            Items = new ObservableCollection<ClipItem>(settings.Items);
            MenuOutline = [];
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
            SequentialCaptureOptions =
            [
                new(
                    SequentialCaptureTrigger.UserInput,
                    "操作した直後のコピー",
                    "キーやマウスを操作した直後にクリップボードが変わったら集めます。"
                        + "右クリックの「コピー」やアプリのコピーボタンも集まります。"
                        + "常駐している他のアプリが自動で書き換えた内容は集めません。"),
                new(
                    SequentialCaptureTrigger.CopyKey,
                    "Ctrl+C のときだけ",
                    "Ctrl+C や Ctrl+X を押した直後だけ集めます。最も確実ですが、"
                        + "右クリックの「コピー」やアプリのコピーボタンは集まりません。"),
                new(
                    SequentialCaptureTrigger.Any,
                    "変わったときは常に",
                    "クリップボードが変わるたびに集めます。取りこぼしはありませんが、"
                        + "常駐している他のアプリが自動で書き込んだ内容も入ります。"),
            ];

            _itemsView = CollectionViewSource.GetDefaultView(Items);
            _itemsView.Filter = o => o is ClipItem item && MatchesFilter(item);

            RefreshCategories();
            RefreshPlaceholderSamples();

            // 変更を検知して「未保存」の状態を持つ
            Items.CollectionChanged += OnItemsCollectionChanged;
            ResubscribeItems();

            RebuildMenuOutline();
            SelectedItem = Items.FirstOrDefault();
        }

        public int Version { get; }

        public ObservableCollection<ClipItem> Items { get; }

        /// <summary>実際のトレイメニュー構造に寄せて再構成した、設定画面用の一覧。</summary>
        public ObservableCollection<MenuOutlineRow> MenuOutline { get; }

        /// <summary>カテゴリ入力欄の候補。</summary>
        public ObservableCollection<ClipCategory> KnownCategories { get; }

        public IReadOnlyList<CategoryColorPreset> CategoryColorOptions
            => CategoryAppearanceCatalog.Colors;

        public IReadOnlyList<CategoryIconPreset> CategoryIconOptions
            => CategoryAppearanceCatalog.Icons;

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

        /// <summary>連続コピーの収集条件として選べる一覧。</summary>
        public IReadOnlyList<SequentialCaptureOption> SequentialCaptureOptions { get; }

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

        /// <summary>連続コピーが 1 件として集める範囲。</summary>
        public SequentialCaptureTrigger SequentialCaptureTrigger
        {
            get => _sequentialCaptureTrigger;
            set
            {
                if (_sequentialCaptureTrigger == value)
                {
                    return;
                }

                _sequentialCaptureTrigger = value;
                IsDirty = true;
                OnPropertyChanged();
                OnPropertyChanged(nameof(SequentialCaptureStatus));
            }
        }

        /// <summary>選んでいる収集条件の説明。何が集まるかを短く出す。</summary>
        public string SequentialCaptureStatus
        {
            get
            {
                SequentialCaptureOption? option = SequentialCaptureOptions.FirstOrDefault(
                    o => o.Trigger == SequentialCaptureTrigger);

                return option?.Description ?? string.Empty;
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
        /// Swap コピーを実行するグローバルホットキー。空欄なら無効。
        /// メニュー用と同じく、入力途中を許すため文字列で持ち、保存時に検証する。
        /// </summary>
        public string SwapCopyHotKey
        {
            get => _swapCopyHotKey;
            set
            {
                string next = value ?? string.Empty;
                if (string.Equals(_swapCopyHotKey, next, StringComparison.Ordinal))
                {
                    return;
                }

                _swapCopyHotKey = next;
                IsDirty = true;
                OnPropertyChanged();
                OnPropertyChanged(nameof(SwapCopyHotKeyStatus));
            }
        }

        /// <summary>Swap コピーのホットキー入力欄の下に表示する説明。</summary>
        public string SwapCopyHotKeyStatus
        {
            get
            {
                if (string.IsNullOrWhiteSpace(_swapCopyHotKey))
                {
                    return "空欄のため、Swap コピーは無効です。";
                }

                return HotKeyGesture.TryParse(
                    _swapCopyHotKey, out HotKeyGesture gesture, out string error)
                    ? $"保存後に {gesture.DisplayName} で入れ替えます。"
                    : error;
            }
        }

        /// <summary>
        /// 保存用に Swap コピーのホットキーを検証し、統一表記へ整える。
        /// 空欄は有効な「無効」設定として受け付ける。
        /// </summary>
        public bool TryGetNormalizedSwapCopyHotKey(out string normalized, out string error)
        {
            if (string.IsNullOrWhiteSpace(_swapCopyHotKey))
            {
                normalized = string.Empty;
                error = string.Empty;
                return true;
            }

            if (!HotKeyGesture.TryParse(_swapCopyHotKey, out HotKeyGesture gesture, out error))
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
                RebuildMenuOutline();

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
                OnPropertyChanged(nameof(CanDuplicate));
                OnPropertyChanged(nameof(CanDelete));
                OnPropertyChanged(nameof(CanReorder));
                OnPropertyChanged(nameof(IsItemEditable));
                OnPropertyChanged(nameof(IsCategoryEditable));
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

                if (!_suppressOutlineRebuild)
                {
                    SelectOutlineForItem(value);
                }
            }
        }

        public bool HasSelection => SelectedItem is not null;

        /// <summary>アウトラインで現在選ばれている行。</summary>
        public MenuOutlineRow? SelectedOutlineRow
        {
            get => _selectedOutlineRow;
            set
            {
                if (_rebuildingOutline && value is null)
                {
                    return;
                }

                if (ReferenceEquals(_selectedOutlineRow, value))
                {
                    return;
                }

                _selectedOutlineRow = value;
                OnPropertyChanged();

                _suppressOutlineRebuild = true;
                try
                {
                    SelectedItem = value?.Item;
                }
                finally
                {
                    _suppressOutlineRebuild = false;
                }

                CategoryNameDraft = value is { IsCategory: true }
                    ? value.Category
                    : string.Empty;

                OnPropertyChanged(nameof(CanDelete));
                OnPropertyChanged(nameof(CanReorder));
                OnPropertyChanged(nameof(IsCategoryEditable));
                OnPropertyChanged(nameof(ShowEditorHint));
                OnPropertyChanged(nameof(EditorHint));
                OnPropertyChanged(nameof(SelectedCategoryItemCount));
                OnPropertyChanged(nameof(SelectedCategoryLocation));
                OnPropertyChanged(nameof(SelectedCategoryColor));
                OnPropertyChanged(nameof(SelectedCategoryIcon));
                OnPropertyChanged(nameof(HasSelectedCategoryAppearance));
            }
        }

        /// <summary>カテゴリ名の編集欄。確定するまで項目側のカテゴリ名は変えない。</summary>
        public string CategoryNameDraft
        {
            get => _categoryNameDraft;
            set
            {
                string next = value ?? string.Empty;
                if (string.Equals(_categoryNameDraft, next, StringComparison.Ordinal))
                {
                    return;
                }

                _categoryNameDraft = next;
                OnPropertyChanged();
            }
        }

        public bool CanDuplicate => SelectedItem is not null;

        public bool CanDelete => SelectedItem is not null || SelectedOutlineRow?.IsCategory == true;

        /// <summary>並べ替えできるのは、絞り込みをしていないときだけ。</summary>
        public bool CanReorder
            => SelectedOutlineRow is { IsSection: false } && !HasFilter;

        /// <summary>区切り線は編集する内容がないため、編集欄自体を出さない。</summary>
        public bool IsItemEditable => SelectedItem is not null && !SelectedItem.IsSeparator;

        /// <summary>カテゴリ見出しを選んでいるとき、カテゴリ単位の編集欄を出す。</summary>
        public bool IsCategoryEditable => SelectedOutlineRow?.IsCategory == true;

        /// <summary>編集欄の代わりに案内を出すかどうか。</summary>
        public bool ShowEditorHint => !IsItemEditable && !IsCategoryEditable;

        /// <summary>編集できないときに出す案内。</summary>
        public string EditorHint => SelectedItem is not null
            ? "区切り線には編集する内容がありません。メニューのグループ分けに使えます。"
            : SelectedOutlineRow?.IsSection == true
                ? "この領域には、トレイメニューで同じ場所に表示される項目がまとまります。"
                : "左の一覧から項目かカテゴリを選ぶか、「追加」で新しい項目を作成してください。";

        public int SelectedCategoryItemCount
            => SelectedOutlineRow is { IsCategory: true } row
                ? Items.Count(item => string.Equals(item.CategoryId, row.CategoryId, StringComparison.Ordinal))
                : 0;

        public string SelectedCategoryLocation
            => SelectedOutlineRow is { IsCategory: true } row
                ? row.Section == MenuOutlineSection.Regular
                    ? "通常メニューに表示されるカテゴリです。"
                    : "条件に合うとき「この内容でできること」の中に表示されます。"
                : string.Empty;

        public string SelectedCategoryColor
        {
            get => SelectedOutlineRow is { IsCategory: true } row
                ? FindCategoryById(row.CategoryId)?.Color ?? string.Empty
                : string.Empty;
            set
            {
                if (value is null || SelectedOutlineRow is not { IsCategory: true } row)
                {
                    return;
                }

                ClipCategory? category = FindCategoryById(row.CategoryId);
                string normalized = CategoryAppearanceCatalog.NormalizeColor(value);
                if (category is null || string.Equals(category.Color, normalized, StringComparison.Ordinal))
                {
                    return;
                }

                category.Color = normalized;
                CategoryAppearanceChanged();
            }
        }

        public string SelectedCategoryIcon
        {
            get => SelectedOutlineRow is { IsCategory: true } row
                ? FindCategoryById(row.CategoryId)?.Icon ?? string.Empty
                : string.Empty;
            set
            {
                if (value is null || SelectedOutlineRow is not { IsCategory: true } row)
                {
                    return;
                }

                ClipCategory? category = FindCategoryById(row.CategoryId);
                string normalized = CategoryAppearanceCatalog.NormalizeIcon(value);
                if (category is null || string.Equals(category.Icon, normalized, StringComparison.Ordinal))
                {
                    return;
                }

                category.Icon = normalized;
                CategoryAppearanceChanged();
            }
        }

        public bool HasSelectedCategoryAppearance
            => SelectedCategoryColor.Length > 0 || SelectedCategoryIcon.Length > 0;

        public void ResetSelectedCategoryAppearance()
        {
            if (SelectedOutlineRow is not { IsCategory: true } row
                || FindCategoryById(row.CategoryId) is not { } category
                || (category.Color.Length == 0 && category.Icon.Length == 0))
            {
                return;
            }

            category.Color = string.Empty;
            category.Icon = string.Empty;
            CategoryAppearanceChanged();
        }

        private void CategoryAppearanceChanged()
        {
            IsDirty = true;
            RefreshCategories();
            RebuildMenuOutline();
            OnPropertyChanged(nameof(SelectedCategoryColor));
            OnPropertyChanged(nameof(SelectedCategoryIcon));
            OnPropertyChanged(nameof(HasSelectedCategoryAppearance));
        }

    }
}
