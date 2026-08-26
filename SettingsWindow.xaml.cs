using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using MyTaskTray.Models;
using MyTaskTray.Services;
using MyTaskTray.ViewModels;

namespace MyTaskTray
{
    /// <summary>
    /// コピー項目を編集する設定画面。
    /// </summary>
    public partial class SettingsWindow : Window
    {
        private readonly SettingsViewModel _vm;
        private readonly DispatcherTimer _previewTimer;
        // XAML の初期化中に TextChanged が走ることがあるため、null を許容する
        private readonly CollectionViewSource? _placeholderView;

        private Point _dragStartPoint;
        private bool _dragArmed;
        private MenuOutlineRow? _draggingRow;

        // 挿入線を出している行。仮想化でコンテナが消えたり再利用されたりするため、
        // 一覧全体を走査するのではなく「いま線を出している 1 行」だけを覚えておく。
        private ListBoxItem? _dropIndicatorTarget;

        public SettingsWindow(AppSettings settings)
            : this(settings, [], [])
        {
        }

        /// <param name="recentApps">
        /// 直近に前面だったアプリの実行ファイル名。「現在のアプリ ▾」の候補に使う。
        /// </param>
        public SettingsWindow(AppSettings settings, IReadOnlyList<string> recentApps)
            : this(settings, recentApps, [])
        {
        }

        internal SettingsWindow(
            AppSettings settings,
            IReadOnlyList<string> recentApps,
            IReadOnlyList<TrayActionDefinition> actions)
            : this(settings, recentApps, actions, ForegroundApp.Unknown)
        {
        }

        internal SettingsWindow(
            AppSettings settings,
            IReadOnlyList<string> recentApps,
            IReadOnlyList<TrayActionDefinition> actions,
            ForegroundApp appContext)
        {
            InitializeComponent();

            _vm = new SettingsViewModel(settings, recentApps, actions, appContext);
            DataContext = _vm;

            FolderButton.ToolTip = "設定ファイル: " + SettingsStore.FilePath;

            // 差し込み一覧はカテゴリごとに見出しを付けて表示する
            _placeholderView = new CollectionViewSource { Source = _vm.Placeholders };
            _placeholderView.GroupDescriptions.Add(new PropertyGroupDescription(nameof(PlaceholderRow.Group)));
            _placeholderView.Filter += OnPlaceholderFilter;
            PlaceholderList.ItemsSource = _placeholderView.View;

            // {time} などを含む項目を選んでいる間だけ、プレビューを 1 秒ごとに追従させる
            _previewTimer = new DispatcherTimer(DispatcherPriority.Background)
            {
                Interval = TimeSpan.FromSeconds(1),
            };
            _previewTimer.Tick += (_, _) => _vm.RefreshPreview();
            _vm.PropertyChanged += OnViewModelPropertyChanged;
            UpdatePreviewTimer();

            // 他アプリでコピーしてから戻ってきた場合に、{clip} のプレビューを追従させる。
            // プレビューのたびに読むと入力 1 文字ごとにクリップボードを開いてしまうため、ここでだけ読む
            Activated += (_, _) => _vm.RefreshClipboard();

            Closed += (_, _) =>
            {
                _previewTimer.Stop();
                _vm.PropertyChanged -= OnViewModelPropertyChanged;
            };

            ThemeManager.Attach(this);
        }

        /// <summary>保存して閉じた場合に true。</summary>
        public bool Saved { get; private set; }

        /// <summary>クイック追加の「追加先」に、未保存のカテゴリも含めて渡す。</summary>
        internal IReadOnlyList<ClipCategory> GetKnownCategories()
            => [.. _vm.KnownCategories.Select(category => category.Clone())];

        /// <summary>最新の前面アプリを app 系差し込みのプレビューへ反映する。</summary>
        internal void NotifyAppContext(ForegroundApp appContext)
            => _vm.UpdateAppContext(appContext);

        /// <summary>
        /// トレイからのコピーで連番が進んだことを受け取り、画面の「次の番号」に反映する。
        /// 設定画面は設定の複製を持っているため、トレイ側の変化は自動では伝わらない。
        /// </summary>
        public void NotifySequenceAdvanced(string id, int value) => _vm.AdoptSequenceValue(id, value);

        /// <summary>
        /// トレイの「クリップボードを項目に追加」で作られた項目を受け取る。
        ///
        /// <para>
        /// この画面は開いた時点の複製を編集しているため、トレイ側がファイルへ保存しても
        /// 保存の時点で消えてしまう。ファイルではなくこの一覧へ足し、
        /// 「追加ボタンを押して貼り付けた」のと同じ未保存の状態にする。
        /// </para>
        /// </summary>
        public void AddItem(ClipItem item)
        {
            ClearFilter();
            _vm.Items.Add(item);
            _vm.SelectedItem = item;
            ItemsList.ScrollIntoView(_vm.SelectedOutlineRow);
            _vm.RefreshCategories();
        }

        private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(SettingsViewModel.NeedsPreviewRefresh))
            {
                UpdatePreviewTimer();
            }
        }

        /// <summary>
        /// 選択項目が時間で変わる差し込みを含むときだけタイマーを回す。
        /// 常に回すと <c>{guid}</c> や <c>{random}</c> のプレビューが毎秒書き換わってしまう。
        /// </summary>
        private void UpdatePreviewTimer()
        {
            bool needed = _vm.NeedsPreviewRefresh;

            // Start() は動作中に呼ぶと間隔が測り直しになるため、状態が変わるときだけ操作する
            if (needed == _previewTimer.IsEnabled)
            {
                return;
            }

            if (needed)
            {
                _previewTimer.Start();
            }
            else
            {
                _previewTimer.Stop();
            }
        }

        private void OnWindowClosing(object sender, CancelEventArgs e)
        {
            // 保存が成功すると MarkSaved() で IsDirty が false になるため、これだけで足りる。
            // Saved を条件に足すと、保存後にさらに編集した内容を確認なしで捨ててしまう
            if (!_vm.IsDirty)
            {
                return;
            }

            MessageBoxResult answer = MessageBox.Show(
                "保存していない変更があります。保存して閉じますか？",
                "MyTaskTray",
                MessageBoxButton.YesNoCancel,
                MessageBoxImage.Question);

            switch (answer)
            {
                case MessageBoxResult.Yes:
                    if (!TrySave())
                    {
                        e.Cancel = true;
                    }

                    break;

                case MessageBoxResult.No:
                    break;

                default:
                    e.Cancel = true;
                    break;
            }
        }

        private void OnWindowPreviewKeyDown(object sender, KeyEventArgs e)
        {
            // ポップアップが開いているあいだの Esc は、ウィンドウではなくポップアップを閉じる。
            // カテゴリ候補はフォーカスがポップアップの外（▾ ボタン）に残るため、
            // ポップアップ側の PreviewKeyDown には届かず、
            // ここで拾わないと IsCancel のキャンセルボタンが反応して設定画面ごと閉じてしまう。
            if (e.Key == Key.Escape
                && (InsertPopup.IsOpen || CategoryPopup.IsOpen || AppPopup.IsOpen
                    || HotKeyPopup.IsOpen || SprintPopup.IsOpen || ActionSettingsPopup.IsOpen
                    || SequentialCapturePopup.IsOpen))
            {
                InsertPopup.IsOpen = false;
                CategoryPopup.IsOpen = false;
                AppPopup.IsOpen = false;
                HotKeyPopup.IsOpen = false;
                SprintPopup.IsOpen = false;
                ActionSettingsPopup.IsOpen = false;
                SequentialCapturePopup.IsOpen = false;
                e.Handled = true;
                return;
            }

            // Alt + ↑ / ↓ で並べ替え。
            // 文字入力中は一覧から目が離れており、気付かないまま並びが変わってしまうため無効にする。
            if (e.Key == Key.System && (e.SystemKey == Key.Up || e.SystemKey == Key.Down))
            {
                if (Keyboard.FocusedElement is TextBox)
                {
                    return;
                }

                Move(e.SystemKey == Key.Up ? -1 : 1);
                e.Handled = true;
                return;
            }

            if ((Keyboard.Modifiers & ModifierKeys.Control) != ModifierKeys.Control)
            {
                return;
            }

            switch (e.Key)
            {
                case Key.S:
                    OnSave(sender, e);
                    e.Handled = true;
                    break;

                case Key.N:
                    OnAddItem(sender, e);
                    e.Handled = true;
                    break;

                case Key.D:
                    OnDuplicateItem(sender, e);
                    e.Handled = true;
                    break;

                case Key.F:
                    FilterBox.Focus();
                    FilterBox.SelectAll();
                    e.Handled = true;
                    break;
            }
        }
    }
}
