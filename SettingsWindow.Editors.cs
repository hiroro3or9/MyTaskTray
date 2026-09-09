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
    /// <summary>ホットキー、スプリント、カテゴリ、アプリ条件などの編集ポップアップを扱う。</summary>
    public partial class SettingsWindow
    {
        // ==================================================================
        // ホットキー・スプリントの設定
        // ==================================================================

        private void OnOpenHotKeySettings(object sender, RoutedEventArgs e)
        {
            HotKeyPopup.IsOpen = true;

            Dispatcher.BeginInvoke(
                new Action(() =>
                {
                    HotKeyBox.Focus();
                    HotKeyBox.SelectAll();
                }),
                DispatcherPriority.Input);
        }

        private void OnOpenSprintSettings(object sender, RoutedEventArgs e)
        {
            SprintPopup.IsOpen = true;

            Dispatcher.BeginInvoke(
                new Action(() =>
                {
                    SprintAnchorBox.Focus();
                    SprintAnchorBox.SelectAll();
                }),
                DispatcherPriority.Input);
        }

        private void OnOpenActionSettings(object sender, RoutedEventArgs e)
        {
            ActionSettingsPopup.IsOpen = true;
        }

        private void OnOpenSequentialCaptureSettings(object sender, RoutedEventArgs e)
        {
            SequentialCapturePopup.IsOpen = true;
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

        private void OnToggleCategory(object sender, RoutedEventArgs e)
        {
            if (sender is not Button { DataContext: MenuOutlineRow row })
            {
                return;
            }

            _vm.SelectedOutlineRow = row;
            _vm.ToggleCategory(row);
            ItemsList.ScrollIntoView(_vm.SelectedOutlineRow);
            e.Handled = true;
        }

        private void OnRenameCategory(object sender, RoutedEventArgs e)
        {
            if (_vm.SelectedOutlineRow is not { IsCategory: true } selected)
            {
                return;
            }

            string next = _vm.CategoryNameDraft.Trim();
            if (next.Length == 0)
            {
                MessageBox.Show(
                    "カテゴリ名を入力してください。トップレベルへ移す場合は「カテゴリを削除」を使います。",
                    "MyTaskTray",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
                CategoryNameBox.Focus();
                return;
            }

            if (_vm.CategoryExists(next, selected.CategoryId))
            {
                MessageBoxResult answer = MessageBox.Show(
                    $"「{next}」はすでにあります。2つのカテゴリをまとめますか？",
                    "MyTaskTray",
                    MessageBoxButton.OKCancel,
                    MessageBoxImage.Question);
                if (answer != MessageBoxResult.OK)
                {
                    return;
                }
            }

            _vm.RenameSelectedCategory(next);
            ItemsList.ScrollIntoView(_vm.SelectedOutlineRow);
        }

        private void OnCategoryNameKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.Enter)
            {
                return;
            }

            OnRenameCategory(sender, e);
            e.Handled = true;
        }

        private void OnRemoveCategory(object sender, RoutedEventArgs e)
        {
            if (_vm.SelectedOutlineRow is not { IsCategory: true } selected)
            {
                return;
            }

            MessageBoxResult answer = MessageBox.Show(
                $"カテゴリ「{selected.Category}」を削除しますか？\n"
                    + $"中の {_vm.SelectedCategoryItemCount} 件の項目は削除せず、トップレベルへ移します。",
                "MyTaskTray",
                MessageBoxButton.OKCancel,
                MessageBoxImage.Question);
            if (answer != MessageBoxResult.OK)
            {
                return;
            }

            _vm.RemoveSelectedCategory();
            ItemsList.ScrollIntoView(_vm.SelectedOutlineRow);
            ItemsList.Focus();
        }

        private void OnOpenCategoryPopup(object sender, RoutedEventArgs e)
        {
            CategoryPopup.IsOpen = true;
        }

        private void OnPickCategory(object sender, RoutedEventArgs e)
        {
            if (sender is not Button { DataContext: ClipCategory category } || _vm.SelectedItem is null)
            {
                return;
            }

            CategoryPopup.IsOpen = false;
            _vm.SelectedItem.Category = category.Name;
            CategoryBox.Focus();
            CategoryBox.CaretIndex = CategoryBox.Text.Length;
        }

        private void OnResetCategoryAppearance(object sender, RoutedEventArgs e)
            => _vm.ResetSelectedCategoryAppearance();

        private void OnClearCategory(object sender, RoutedEventArgs e)
        {
            if (_vm.SelectedItem is null)
            {
                return;
            }

            CategoryPopup.IsOpen = false;
            _vm.SelectedItem.Category = string.Empty;
            CategoryBox.Focus();
        }

        /// <summary>ホットキー欄で単体指定できるキーを押したら、その名前を入力する。</summary>
        private void OnHotKeyBoxPreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (!TryGetStandaloneKeyName(e.Key, out string name))
            {
                return;
            }

            e.Handled = true;
            _vm.MenuHotKey = name;
            HotKeyBox.CaretIndex = HotKeyBox.Text.Length;
        }

        /// <summary>Swap コピーのホットキー欄も同じように扱う。</summary>
        private void OnSwapCopyHotKeyBoxPreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (!TryGetStandaloneKeyName(e.Key, out string name))
            {
                return;
            }

            e.Handled = true;
            _vm.SwapCopyHotKey = name;
            SwapCopyHotKeyBox.CaretIndex = SwapCopyHotKeyBox.Text.Length;
        }

        /// <summary>
        /// 修飾キーなしで指定できるキーの表記。押して入れられるものだけを並べる。
        ///
        /// <para>
        /// 無変換と変換は、この欄が全角入力を避けるため IME を切っており、
        /// 「無変換」という文字自体を打てない。ローマ字（muhenkan / henkan）でも
        /// 指定できるが、押して入れられるほうが早く、打ち間違いも起きない。
        /// アプリケーションキー・Pause・F13〜F24 も同じ扱いにする。
        /// </para>
        /// </summary>
        private static bool TryGetStandaloneKeyName(Key key, out string name)
        {
            if (key is >= Key.F13 and <= Key.F24)
            {
                name = "F" + (13 + (key - Key.F13));
                return true;
            }

            name = key switch
            {
                Key.ImeNonConvert => "無変換",
                Key.ImeConvert => "変換",
                Key.Apps => "アプリケーション",
                Key.Pause => "Pause",
                _ => string.Empty,
            };

            return name.Length > 0;
        }

        private void OnOpenAppPopup(object sender, RoutedEventArgs e)
        {
            if (!_vm.HasKnownApps)
            {
                return;
            }

            AppPopup.IsOpen = true;
        }

        /// <summary>
        /// 候補のアプリを入力欄へ入れる。既に書かれている場合はカンマで足す
        /// （「ブラウザ 2 つのどちらでも」のような指定が多いため）。
        /// </summary>
        private void OnPickApp(object sender, RoutedEventArgs e)
        {
            if (sender is not Button { DataContext: string app } || _vm.SelectedItem is null)
            {
                return;
            }

            AppPopup.IsOpen = false;

            string current = _vm.SelectedItem.AppProcess.Trim();
            bool already = AppContextMatcher
                .SplitProcessNames(current)
                .Any(name => AppContextMatcher.MatchesProcess(name, app));

            if (!already)
            {
                _vm.SelectedItem.AppProcess = current.Length == 0 ? app : current + ", " + app;
            }

            AppProcessBox.Focus();
            AppProcessBox.CaretIndex = AppProcessBox.Text.Length;
        }

        private void OnPopupPreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.Escape)
            {
                return;
            }

            InsertPopup.IsOpen = false;
            CategoryPopup.IsOpen = false;
            AppPopup.IsOpen = false;
            HotKeyPopup.IsOpen = false;
            SprintPopup.IsOpen = false;
            ActionSettingsPopup.IsOpen = false;
            SequentialCapturePopup.IsOpen = false;
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
            _vm.SelectedItem?.ResetSequence();
        }
    }
}
