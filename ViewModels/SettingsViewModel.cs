using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Data;
using MyTaskTray.Models;
using MyTaskTray.Services;

namespace MyTaskTray.ViewModels
{
    /// <summary>
    /// 設定画面のためのビューモデル。
    /// </summary>
    public class SettingsViewModel : INotifyPropertyChanged
    {
        private readonly ICollectionView _itemsView;

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
        private bool _isDirty;

        // プレビューに使うクリップボードの内容。
        // Preview の中で毎回読むと、入力欄を 1 文字打つたびにクリップボードを開くことになり、
        // 他アプリのコピー操作と競合する（ロック中は再試行のあいだ画面が止まる）。
        // ウィンドウがアクティブになったときなど、区切りのよいところでだけ読み直す。
        private string _clipboard = string.Empty;

        public SettingsViewModel(AppSettings settings)
        {
            Version = settings.Version;
            _showCopyNotification = settings.ShowCopyNotification;

            Items = new ObservableCollection<ClipItem>(settings.Items);
            KnownCategories = [];
            Placeholders = new ObservableCollection<PlaceholderRow>(
                TemplateEngine.Placeholders.Select(p => new PlaceholderRow(p)));

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

        /// <summary>「差し込みを挿入」パネルに並べる一覧。</summary>
        public ObservableCollection<PlaceholderRow> Placeholders { get; }

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
                OnPropertyChanged(nameof(ShowEditorHint));
                OnPropertyChanged(nameof(EditorHint));
                OnPropertyChanged(nameof(Preview));
                OnPropertyChanged(nameof(NeedsPreviewRefresh));
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

                return TemplateEngine.Expand(
                    SelectedItem.Text, DateTime.Now, SelectedItem.SequenceValue, () => _clipboard);
            }
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
        }

        /// <summary>差し込み一覧の「現在値」を今の時刻で作り直す。</summary>
        public void RefreshPlaceholderSamples()
        {
            // クリップボードの読み取りは一覧全体で 1 回で済ませる
            RefreshClipboard();

            DateTime now = DateTime.Now;
            int sequence = SelectedItem?.SequenceValue ?? 1;

            foreach (PlaceholderRow row in Placeholders)
            {
                row.Sample = TemplateEngine.ToSingleLine(
                    TemplateEngine.Expand(row.Token, now, sequence, () => _clipboard), 60);
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

        /// <summary>保存用の設定オブジェクトを作る。</summary>
        public AppSettings ToSettings() => new()
        {
            Version = Version,
            ShowCopyNotification = ShowCopyNotification,
            Items = [.. Items.Select(i => i.Clone())],
        };

        /// <summary>保存が完了したことを伝える。</summary>
        public void MarkSaved() => IsDirty = false;

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
                || item.Category.Contains(_filterText, StringComparison.CurrentCultureIgnoreCase);
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
                    IsDirty = true;

                    // 絞り込み中は表示件数が変わるため、件数の表示も作り直す
                    OnPropertyChanged(nameof(StatusText));
                    break;

                case nameof(ClipItem.SequenceStep):
                    IsDirty = true;
                    break;

                case nameof(ClipItem.SequenceValue):
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
                    OnPropertyChanged(nameof(NeedsPreviewRefresh));
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
