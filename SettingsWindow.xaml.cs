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
        private ClipItem? _draggingItem;

        // 挿入線を出している行。仮想化でコンテナが消えたり再利用されたりするため、
        // 一覧全体を走査するのではなく「いま線を出している 1 行」だけを覚えておく。
        private ListBoxItem? _dropIndicatorTarget;

        public SettingsWindow(AppSettings settings)
        {
            InitializeComponent();

            _vm = new SettingsViewModel(settings);
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

        // ==================================================================
        // 一覧の操作
        // ==================================================================

        private void OnSelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            _vm.RefreshCategories();
        }

        private void OnAddItem(object sender, RoutedEventArgs e)
        {
            ClipItem item = new()
            {
                Name = "新しい項目",
                Text = string.Empty,
                Category = _vm.SelectedItem?.Category ?? string.Empty,
            };

            ClearFilter();
            InsertAfterSelection(item);

            // 編集欄が表示され終わってから入力欄にフォーカスを移す
            Dispatcher.BeginInvoke(
                new Action(() =>
                {
                    NameBox.Focus();
                    NameBox.SelectAll();
                }),
                DispatcherPriority.Input);
        }

        private void OnAddSeparator(object sender, RoutedEventArgs e)
        {
            ClearFilter();
            InsertAfterSelection(new ClipItem { IsSeparator = true });
        }

        private void OnDuplicateItem(object sender, RoutedEventArgs e)
        {
            if (_vm.SelectedItem is null)
            {
                return;
            }

            ClipItem copy = _vm.SelectedItem.Clone();
            copy.Id = ClipItem.NewId();
            if (!copy.IsSeparator)
            {
                copy.Name = string.IsNullOrWhiteSpace(copy.Name) ? copy.Name : copy.Name + " のコピー";
            }

            ClearFilter();
            InsertAfterSelection(copy);
        }

        private void OnDeleteItem(object sender, RoutedEventArgs e)
        {
            if (_vm.SelectedItem is not ClipItem target)
            {
                return;
            }

            // 中身のある項目は誤操作を防ぐために確認する
            if (!target.IsSeparator
                && (!string.IsNullOrWhiteSpace(target.Text) || !string.IsNullOrWhiteSpace(target.Name)))
            {
                MessageBoxResult answer = MessageBox.Show(
                    $"「{target.DisplayLabel}」を削除しますか？",
                    "MyTaskTray",
                    MessageBoxButton.OKCancel,
                    MessageBoxImage.Question);

                if (answer != MessageBoxResult.OK)
                {
                    return;
                }
            }

            int index = _vm.Items.IndexOf(target);
            _vm.Items.RemoveAt(index);
            _vm.SelectedItem = _vm.Items.Count == 0
                ? null
                : _vm.Items[Math.Min(index, _vm.Items.Count - 1)];
            _vm.RefreshCategories();
            ItemsList.Focus();
        }

        private void OnMoveUp(object sender, RoutedEventArgs e) => Move(-1);

        private void OnMoveDown(object sender, RoutedEventArgs e) => Move(1);

        private void Move(int offset)
        {
            if (!_vm.CanReorder || _vm.SelectedItem is null)
            {
                return;
            }

            ClipItem moving = _vm.SelectedItem;
            int index = _vm.Items.IndexOf(moving);
            int target = index + offset;
            if (target < 0 || target >= _vm.Items.Count)
            {
                return;
            }

            _vm.Items.Move(index, target);

            // ListBox は Move で選択が外れることがあるため、選択し直す
            _vm.SelectedItem = moving;
            ItemsList.ScrollIntoView(moving);
        }

        private void InsertAfterSelection(ClipItem item)
        {
            int index = _vm.SelectedItem is null
                ? _vm.Items.Count
                : _vm.Items.IndexOf(_vm.SelectedItem) + 1;

            _vm.Items.Insert(index, item);
            _vm.SelectedItem = item;
            ItemsList.ScrollIntoView(item);
            _vm.RefreshCategories();
        }

        private void OnClearFilter(object sender, RoutedEventArgs e)
        {
            ClearFilter();
            FilterBox.Focus();
        }

        private void ClearFilter() => _vm.FilterText = string.Empty;

        private void OnListPreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Delete)
            {
                OnDeleteItem(sender, e);
                e.Handled = true;
            }
        }

        // ==================================================================
        // ドラッグ＆ドロップでの並べ替え
        // ==================================================================

        private void OnListPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            _dragStartPoint = e.GetPosition(null);
            _dragArmed = true;
        }

        private void OnListPreviewMouseMove(object sender, MouseEventArgs e)
        {
            if (!_dragArmed || e.LeftButton != MouseButtonState.Pressed)
            {
                return;
            }

            Vector moved = _dragStartPoint - e.GetPosition(null);
            if (Math.Abs(moved.X) < SystemParameters.MinimumHorizontalDragDistance
                && Math.Abs(moved.Y) < SystemParameters.MinimumVerticalDragDistance)
            {
                return;
            }

            _dragArmed = false;

            // 絞り込み中は表示順と実際の順序がずれるため、並べ替えさせない
            if (_vm.HasFilter)
            {
                return;
            }

            if (FindContainer(e.OriginalSource) is not ListBoxItem container
                || container.DataContext is not ClipItem item)
            {
                return;
            }

            _draggingItem = item;
            try
            {
                DragDrop.DoDragDrop(container, item, DragDropEffects.Move);
            }
            finally
            {
                _draggingItem = null;
                ClearDropIndicators();
            }
        }

        private void OnListDragOver(object sender, DragEventArgs e)
        {
            if (_draggingItem is null)
            {
                ClearDropIndicators();
                e.Effects = DragDropEffects.None;
                e.Handled = true;
                return;
            }

            if (FindContainer(e.OriginalSource) is ListBoxItem container
                && !ReferenceEquals(container.DataContext, _draggingItem))
            {
                bool below = e.GetPosition(container).Y > container.ActualHeight / 2;
                SetDropIndicator(container, below ? DropPosition.Below : DropPosition.Above);
            }
            else
            {
                ClearDropIndicators();
            }

            e.Effects = DragDropEffects.Move;
            e.Handled = true;
        }

        private void OnListDragLeave(object sender, DragEventArgs e) => ClearDropIndicators();

        private void OnListDrop(object sender, DragEventArgs e)
        {
            ClearDropIndicators();

            if (_draggingItem is not ClipItem moving)
            {
                return;
            }

            int from = _vm.Items.IndexOf(moving);
            if (from < 0)
            {
                return;
            }

            int to = _vm.Items.Count - 1;

            if (FindContainer(e.OriginalSource) is ListBoxItem container
                && container.DataContext is ClipItem dropTarget
                && !ReferenceEquals(dropTarget, moving))
            {
                int targetIndex = _vm.Items.IndexOf(dropTarget);
                bool below = e.GetPosition(container).Y > container.ActualHeight / 2;
                to = below ? targetIndex + 1 : targetIndex;

                // 自分を抜いた後の位置に合わせる
                if (from < to)
                {
                    to--;
                }
            }

            to = Math.Clamp(to, 0, _vm.Items.Count - 1);
            if (to != from)
            {
                _vm.Items.Move(from, to);
            }

            _vm.SelectedItem = moving;
            ItemsList.ScrollIntoView(moving);
            e.Handled = true;
        }

        /// <summary>挿入線を出す行を切り替える。</summary>
        private void SetDropIndicator(ListBoxItem container, DropPosition position)
        {
            if (!ReferenceEquals(_dropIndicatorTarget, container))
            {
                ClearDropIndicators();
            }

            DropIndicator.SetPosition(container, position);
            _dropIndicatorTarget = container;
        }

        private void ClearDropIndicators()
        {
            if (_dropIndicatorTarget is null)
            {
                return;
            }

            DropIndicator.SetPosition(_dropIndicatorTarget, DropPosition.None);
            _dropIndicatorTarget = null;
        }

        /// <summary>クリックされた要素から親をたどって行（ListBoxItem）を探す。</summary>
        private static ListBoxItem? FindContainer(object? source)
        {
            DependencyObject? current = source as DependencyObject;

            while (current is not null and not ListBoxItem)
            {
                current = current is Visual or System.Windows.Media.Media3D.Visual3D
                    ? VisualTreeHelper.GetParent(current)
                    : LogicalTreeHelper.GetParent(current);
            }

            return current as ListBoxItem;
        }

        // ==================================================================
        // 差し込みの挿入
        // ==================================================================

        private void OnOpenInsertPopup(object sender, RoutedEventArgs e)
        {
            _vm.RefreshPlaceholderSamples();
            PlaceholderFilterBox.Clear();
            InsertPopup.IsOpen = true;

            Dispatcher.BeginInvoke(
                new Action(() => PlaceholderFilterBox.Focus()),
                DispatcherPriority.Input);
        }

        private void OnPlaceholderFilterChanged(object sender, TextChangedEventArgs e)
        {
            PlaceholderFilterHint?.Visibility = string.IsNullOrEmpty(PlaceholderFilterBox.Text)
                    ? Visibility.Visible
                    : Visibility.Collapsed;

            _placeholderView?.View?.Refresh();
        }

        private void OnPlaceholderFilter(object sender, FilterEventArgs e)
        {
            string keyword = PlaceholderFilterBox?.Text ?? string.Empty;
            e.Accepted = e.Item is PlaceholderRow row && row.Matches(keyword);
        }

        /// <summary>選んだ差し込みをカーソル位置に挿入する。</summary>
        private void OnInsertPlaceholder(object sender, RoutedEventArgs e)
        {
            if (sender is not Button { DataContext: PlaceholderRow row })
            {
                return;
            }

            InsertPopup.IsOpen = false;

            int caret = TextBox_Content.SelectionStart;
            TextBox_Content.SelectedText = row.Token;
            TextBox_Content.CaretIndex = caret + row.Token.Length;
            TextBox_Content.Focus();
        }

        // ==================================================================
        // カテゴリ候補
        // ==================================================================

        private void OnOpenCategoryPopup(object sender, RoutedEventArgs e)
        {
            if (!_vm.HasCategories)
            {
                return;
            }

            CategoryPopup.IsOpen = true;
        }

        private void OnPickCategory(object sender, RoutedEventArgs e)
        {
            if (sender is not Button { DataContext: string category } || _vm.SelectedItem is null)
            {
                return;
            }

            CategoryPopup.IsOpen = false;
            _vm.SelectedItem.Category = category;
            CategoryBox.Focus();
            CategoryBox.CaretIndex = CategoryBox.Text.Length;
        }

        private void OnPopupPreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.Escape)
            {
                return;
            }

            InsertPopup.IsOpen = false;
            CategoryPopup.IsOpen = false;
            e.Handled = true;
        }

        // ==================================================================
        // 連番・プレビュー
        // ==================================================================

        /// <summary>
        /// 整数の入力欄。数字と先頭のマイナス記号だけを受け付ける
        /// （増分に負の値を入れるとカウントダウンになる）。
        /// int に収まらない桁数も弾くため、値が更新されないまま古い値が残ることがない。
        /// </summary>
        private void OnIntegerTextInput(object sender, TextCompositionEventArgs e)
        {
            e.Handled = sender is not TextBox box || !IsValidIntegerEdit(box, e.Text);
        }

        /// <summary>整数として読めない文字列の貼り付けを取り消す。</summary>
        private void OnIntegerPasting(object sender, DataObjectPastingEventArgs e)
        {
            if (sender is not TextBox box || !e.SourceDataObject.GetDataPresent(DataFormats.UnicodeText))
            {
                e.CancelCommand();
                return;
            }

            string text = e.SourceDataObject.GetData(DataFormats.UnicodeText) as string ?? string.Empty;
            if (!IsValidIntegerEdit(box, text.Trim()))
            {
                e.CancelCommand();
            }
        }

        /// <summary>
        /// 空欄や "-" だけの状態で入力欄を離れた場合は、バインディング元の値に戻す。
        /// そのままにすると、表示と実際の値が食い違ったままになる。
        /// </summary>
        private void OnIntegerLostFocus(object sender, RoutedEventArgs e)
        {
            if (sender is not TextBox box
                || int.TryParse(box.Text, NumberStyles.Integer, CultureInfo.CurrentCulture, out _))
            {
                return;
            }

            BindingOperations.GetBindingExpression(box, TextBox.TextProperty)?.UpdateTarget();
        }

        /// <summary>入力・貼り付けを反映したあとの文字列が、整数として成り立つかどうか。</summary>
        private static bool IsValidIntegerEdit(TextBox box, string input)
        {
            string next = box.Text
                .Remove(box.SelectionStart, box.SelectionLength)
                .Insert(box.SelectionStart, input);

            // 入力途中の "-" だけは、続けて数字を打てるように許す
            return next == "-"
                || int.TryParse(next, NumberStyles.Integer, CultureInfo.InvariantCulture, out _);
        }

        private void OnResetSequence(object sender, RoutedEventArgs e)
        {
            _vm.SelectedItem?.SequenceValue = 1;
        }

        private void OnCopyPreview(object sender, RoutedEventArgs e)
        {
            string value = _vm.Preview;
            if (string.IsNullOrEmpty(value))
            {
                return;
            }

            if (ClipboardService.TryCopy(value))
            {
                ToastWindow.ShowToast("コピーしました", TemplateEngine.ToSingleLine(value, 120));
            }
            else
            {
                MessageBox.Show(
                    "クリップボードにコピーできませんでした。他のアプリが使用中の可能性があります。",
                    "MyTaskTray",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
            }
        }

        // ==================================================================
        // 保存・終了
        // ==================================================================

        private void OnOpenFolder(object sender, RoutedEventArgs e)
        {
            try
            {
                Directory.CreateDirectory(SettingsStore.DirectoryPath);
                Process.Start(new ProcessStartInfo
                {
                    FileName = SettingsStore.DirectoryPath,
                    UseShellExecute = true,
                });
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    "設定フォルダーを開けませんでした。\n" + ex.Message,
                    "MyTaskTray",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
            }
        }

        private void OnSave(object sender, RoutedEventArgs e)
        {
            if (TrySave())
            {
                Close();
            }
        }

        private void OnCancel(object sender, RoutedEventArgs e)
        {
            Close();
        }

        private bool TrySave()
        {
            // 画面の項目そのものを整えてから写す。
            // 写したあとで整えると、保存した内容と画面の表示が食い違ったままになる
            NormalizeItems();

            AppSettings settings = _vm.ToSettings();

            // 設定画面を開いている間にトレイからコピーされて進んだ連番を取り込む
            AdoptExternalSequenceValues(settings);

            try
            {
                SettingsStore.Save(settings);
                Saved = true;
                _vm.MarkSaved();
                return true;
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    "設定を保存できませんでした。\n" + ex.Message,
                    "MyTaskTray",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return false;
            }
        }

        /// <summary>
        /// 保存前に項目の内容を整える。画面に見えている項目そのものを直すため、
        /// 保存したあとも表示とファイルの内容が一致する。
        /// </summary>
        private void NormalizeItems()
        {
            foreach (ClipItem item in _vm.Items)
            {
                // 表示名が空の項目はコピー文字列を名前として使う
                if (!item.IsSeparator && string.IsNullOrWhiteSpace(item.Name))
                {
                    item.Name = item.Text.Trim();
                }

                // 見た目で区別できない前後の空白でカテゴリが分かれないようにする
                item.Category = item.Category.Trim();

                if (item.SequenceStep == 0)
                {
                    item.SequenceStep = 1;
                }

                // 画面で追加した項目にはまだ Id が無い。連番の引き継ぎに使うため採番しておく
                if (string.IsNullOrEmpty(item.Id))
                {
                    item.Id = ClipItem.NewId();
                }
            }
        }

        /// <summary>
        /// 設定画面は開いた時点の内容を編集しているため、そのまま保存すると
        /// 開いている間にトレイからコピーされて進んだ連番を巻き戻してしまう。
        /// <see cref="ClipItem.Id"/> で突き合わせ、ファイル側の新しい連番を取り込む。
        /// 画面上で「次の番号」を直接編集した項目は、ユーザーの指定を優先して対象外にする。
        /// </summary>
        private void AdoptExternalSequenceValues(AppSettings settings)
        {
            AppSettings latest;
            try
            {
                latest = SettingsStore.Load();
            }
            catch (Exception)
            {
                // 読み直せない場合は画面の内容をそのまま保存する
                return;
            }

            // 読めずに既定値が返ってきた場合、取り込むと連番が既定値に戻ってしまう
            if (latest.IsFallback)
            {
                return;
            }

            Dictionary<string, int> sequences = new(StringComparer.Ordinal);
            foreach (ClipItem item in latest.Items)
            {
                if (!string.IsNullOrEmpty(item.Id))
                {
                    sequences[item.Id] = item.SequenceValue;
                }
            }

            foreach (ClipItem item in settings.Items)
            {
                if (string.IsNullOrEmpty(item.Id) || _vm.SequenceEditedIds.Contains(item.Id))
                {
                    continue;
                }

                if (sequences.TryGetValue(item.Id, out int value))
                {
                    item.SequenceValue = value;
                }
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
            if (e.Key == Key.Escape && (InsertPopup.IsOpen || CategoryPopup.IsOpen))
            {
                InsertPopup.IsOpen = false;
                CategoryPopup.IsOpen = false;
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
