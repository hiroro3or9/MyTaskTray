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
    /// <summary>コピー項目の追加・削除・選択と、ドラッグ＆ドロップによる並べ替えを扱う。</summary>
    public partial class SettingsWindow
    {
        // ==================================================================
        // 一覧の操作
        // ==================================================================

        private void OnSelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            _vm.RefreshCategories();
            SmartActionExpander.IsExpanded = _vm.SelectedItem?.HasSmartCondition == true;
            AppContextExpander.IsExpanded = _vm.SelectedItem?.HasAppCondition == true;
        }

        private void OnAddItem(object sender, RoutedEventArgs e)
        {
            MenuOutlineRow? location = _vm.SelectedOutlineRow;
            ClipItem item = new()
            {
                Name = "新しい項目",
                Text = string.Empty,
                Category = location?.Category ?? _vm.SelectedItem?.Category ?? string.Empty,
                CategoryId = location?.CategoryId ?? _vm.SelectedItem?.CategoryId ?? string.Empty,
                ClipboardCondition = location?.Section == MenuOutlineSection.Contextual
                    ? ClipboardMatchKind.HasText
                    : ClipboardMatchKind.Always,
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
            if (_vm.SelectedOutlineRow is { IsCategory: true })
            {
                OnRemoveCategory(sender, e);
                return;
            }

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
            if (!_vm.CanReorder)
            {
                return;
            }

            _vm.MoveSelectedOutline(offset);
            ItemsList.ScrollIntoView(_vm.SelectedOutlineRow);
        }

        private void InsertAfterSelection(ClipItem item)
        {
            int index;
            if (_vm.SelectedOutlineRow is { IsCategory: true } category)
            {
                int last = -1;
                for (int i = 0; i < _vm.Items.Count; i++)
                {
                    ClipItem candidate = _vm.Items[i];
                    bool sameSection = category.Section == MenuOutlineSection.Regular
                        ? candidate.IsSeparator || candidate.ClipboardCondition == ClipboardMatchKind.Always
                        : !candidate.IsSeparator && candidate.ClipboardCondition != ClipboardMatchKind.Always;
                    if (sameSection
                        && string.Equals(
                            candidate.CategoryId,
                            category.CategoryId,
                            StringComparison.Ordinal))
                    {
                        last = i;
                    }
                }

                index = last >= 0 ? last + 1 : _vm.Items.Count;
            }
            else
            {
                index = _vm.SelectedItem is null
                    ? _vm.Items.Count
                    : _vm.Items.IndexOf(_vm.SelectedItem) + 1;
            }

            _vm.Items.Insert(index, item);
            _vm.SelectedItem = item;
            ItemsList.ScrollIntoView(_vm.SelectedOutlineRow);
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
                || container.DataContext is not MenuOutlineRow { IsSection: false } row)
            {
                return;
            }

            _draggingRow = row;
            try
            {
                DragDrop.DoDragDrop(container, row, DragDropEffects.Move);
            }
            finally
            {
                _draggingRow = null;
                ClearDropIndicators();
            }
        }

        private void OnListDragOver(object sender, DragEventArgs e)
        {
            if (_draggingRow is null)
            {
                ClearDropIndicators();
                e.Effects = DragDropEffects.None;
                e.Handled = true;
                return;
            }

            if (FindContainer(e.OriginalSource) is ListBoxItem container
                && container.DataContext is MenuOutlineRow target
                && !target.IsSection
                && target.Section == _draggingRow.Section
                && !ReferenceEquals(target, _draggingRow)
                && !(_draggingRow.IsCategory
                    && string.Equals(
                        _draggingRow.CategoryId,
                        target.CategoryId,
                        StringComparison.Ordinal)))
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

            if (_draggingRow is not MenuOutlineRow moving)
            {
                return;
            }

            if (FindContainer(e.OriginalSource) is ListBoxItem container
                && container.DataContext is MenuOutlineRow target
                && !target.IsSection
                && target.Section == moving.Section
                && !ReferenceEquals(target, moving))
            {
                bool below = e.GetPosition(container).Y > container.ActualHeight / 2;
                if (_vm.MoveOutlineRow(moving, target, below))
                {
                    ItemsList.ScrollIntoView(_vm.SelectedOutlineRow);
                }
            }
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
    }
}
