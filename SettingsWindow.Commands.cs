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
    /// <summary>プレビューのコピー、設定フォルダー、保存前検証と設定保存を扱う。</summary>
    public partial class SettingsWindow
    {
        private void OnCopyPreview(object sender, RoutedEventArgs e)
        {
            if (_vm.SelectedItem is { UsesInputs: true })
            {
                MessageBox.Show(
                    "{input:名前} を含む項目は、トレイメニューから選んでコピー操作を行うと完成します。\n"
                        + "正規表現で絞り込む場合は {input:名前:/正規表現/} と書けます。",
                    "MyTaskTray",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
                return;
            }

            string value = _vm.Preview;
            if (string.IsNullOrEmpty(value))
            {
                return;
            }

            // プレビューは既に形式ごとの後処理を通してあるので、ここでは載せ方だけを合わせる
            if (ClipboardService.TryCopy(value, _vm.SelectedItem?.Format ?? ClipFormat.Plain))
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

        /// <summary>Swap コピーのホットキー欄を開いて選択する。保存できなかったときに使う。</summary>
        private void FocusSwapCopyHotKeyBox()
        {
            HotKeyPopup.IsOpen = true;
            Dispatcher.BeginInvoke(
                new Action(() =>
                {
                    SwapCopyHotKeyBox.Focus();
                    SwapCopyHotKeyBox.SelectAll();
                }),
                DispatcherPriority.Input);
        }

        private bool TrySave()
        {
            if (!_vm.TryGetNormalizedMenuHotKey(out string normalizedMenuHotKey, out string hotKeyError))
            {
                MessageBox.Show(
                    "ホットキーを保存できません。\n" + hotKeyError,
                    "MyTaskTray",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);

                HotKeyPopup.IsOpen = true;
                Dispatcher.BeginInvoke(
                    new Action(() =>
                    {
                        HotKeyBox.Focus();
                        HotKeyBox.SelectAll();
                    }),
                    DispatcherPriority.Input);
                return false;
            }

            if (!_vm.TryGetNormalizedSwapCopyHotKey(
                out string normalizedSwapCopyHotKey, out string swapHotKeyError))
            {
                MessageBox.Show(
                    "Swap コピーのホットキーを保存できません。\n" + swapHotKeyError,
                    "MyTaskTray",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);

                FocusSwapCopyHotKeyBox();
                return false;
            }

            // 同じキーを 2 つの用途へ登録すると、後から登録するほうが必ず失敗する。
            // どちらが効いているのか利用者には見えないので、保存の時点で断る
            if (normalizedSwapCopyHotKey.Length > 0
                && string.Equals(
                    normalizedMenuHotKey,
                    normalizedSwapCopyHotKey,
                    StringComparison.OrdinalIgnoreCase))
            {
                MessageBox.Show(
                    "Swap コピーのホットキーを保存できません。\n"
                        + "メニューを表示するホットキーと同じキーは指定できません。",
                    "MyTaskTray",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);

                FocusSwapCopyHotKeyBox();
                return false;
            }

            if (!_vm.TryGetSprintSchedule(out SprintSchedule? validatedSprint, out string sprintError))
            {
                MessageBox.Show(
                    "スプリントの設定を保存できません。\n" + sprintError,
                    "MyTaskTray",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);

                SprintPopup.IsOpen = true;
                Dispatcher.BeginInvoke(
                    new Action(() =>
                    {
                        SprintAnchorBox.Focus();
                        SprintAnchorBox.SelectAll();
                    }),
                    DispatcherPriority.Input);
                return false;
            }

            if (!_vm.TryValidateSmartConditions(out ClipItem? invalidItem, out string conditionError))
            {
                // 正規表現はスマートアクションとアプリ条件の両方にあるため、
                // どちらの入力欄を直せばよいかを見分けてから案内する
                bool appProblem = invalidItem is not null
                    && !AppContextMatcher.TryValidateTitlePattern(invalidItem.AppTitlePattern, out _);

                MessageBox.Show(
                    (appProblem ? "表示するアプリの条件を保存できません。\n" : "スマートアクションの表示条件を保存できません。\n")
                        + conditionError,
                    "MyTaskTray",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);

                ClearFilter();
                _vm.SelectedItem = invalidItem;
                if (invalidItem is not null)
                {
                    ItemsList.ScrollIntoView(invalidItem);
                }

                SmartActionExpander.IsExpanded = !appProblem;
                AppContextExpander.IsExpanded = appProblem;
                Dispatcher.BeginInvoke(
                    new Action(() =>
                    {
                        TextBox target = appProblem ? AppTitleBox : ClipboardPatternBox;
                        target.Focus();
                        target.SelectAll();
                    }),
                    DispatcherPriority.Input);
                return false;
            }

            // 画面の項目そのものを整えてから写す。
            // 写したあとで整えると、保存した内容と画面の表示が食い違ったままになる
            NormalizeItems();

            AppSettings settings = _vm.ToSettings(
                normalizedMenuHotKey, normalizedSwapCopyHotKey, validatedSprint);

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

                // アプリ条件は空欄が「条件なし」を意味するため、空白だけの入力は空にそろえる
                item.AppProcess = item.AppProcess.Trim();
                item.AppTitlePattern = item.AppTitlePattern.Trim();

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
    }
}
