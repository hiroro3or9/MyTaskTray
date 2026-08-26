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
    /// <summary>保存用設定の生成、入力検証、変更追跡、項目の購読を管理する。</summary>
    public partial class SettingsViewModel
    {
        /// <summary>
        /// 保存用の設定オブジェクトを作る。ホットキーとスプリントは保存前に検証済みの値を受け取る。
        /// </summary>
        public AppSettings ToSettings(string normalizedMenuHotKey, SprintSchedule? validatedSprint)
        {
            SynchronizeAllItemCategories();

            Dictionary<string, bool> actionStates = new(_actionStates, StringComparer.Ordinal);
            foreach (ActionSettingRow action in ActionSettings)
            {
                actionStates[action.Id] = action.IsVisible;
            }

            AppSettings result = new()
            {
                Version = AppSettings.CurrentVersion,
                ShowCopyNotification = ShowCopyNotification,
                SequentialCaptureTrigger = SequentialCaptureTrigger,
                MenuHotKey = normalizedMenuHotKey,
                ActionStates = actionStates,
                SprintAnchorDate = validatedSprint?.AnchorDate,
                SprintLengthDays = validatedSprint?.LengthDays ?? 14,
                Categories = [.. _categories.Select(category => category.Clone())],
                Items = [.. Items.Select(i => i.Clone())],
            };
            result.RegularMenu = SettingsStructure.BuildLayout(result.Items, contextual: false);
            result.ContextualMenu = SettingsStructure.BuildLayout(result.Items, contextual: true);
            SettingsStructure.Normalize(result);
            return result;
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
            SynchronizeAllItemCategories();

            IsDirty = true;
            OnPropertyChanged(nameof(StatusText));

            if (!_suppressOutlineRebuild)
            {
                RebuildMenuOutline();
            }
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
            if (e.PropertyName == nameof(ClipItem.Category)
                && sender is ClipItem categoryItem
                && !_synchronizingCategories)
            {
                SynchronizeItemCategory(categoryItem);
            }

            // ClipItem が通知する変更は、読み取り専用の派生プロパティも含め、
            // すべて保存対象プロパティの変更を起点にしている。
            // 保存対象をここでも個別に列挙すると、新しいプロパティを追加したときに
            // 未保存判定だけを足し忘れるため、原則としてすべて変更ありとみなす。
            // 唯一、トレイ側で進んだ連番の取り込みは利用者の編集ではないので除外する。
            bool adoptingExternalSequence = e.PropertyName == nameof(ClipItem.SequenceValue)
                && _adoptingSequence;
            if (!adoptingExternalSequence)
            {
                IsDirty = true;
            }

            switch (e.PropertyName)
            {
                case nameof(ClipItem.Name):
                case nameof(ClipItem.Text):
                case nameof(ClipItem.Category):
                case nameof(ClipItem.CategoryId):
                case nameof(ClipItem.IsSeparator):
                case nameof(ClipItem.ClipboardCondition):
                case nameof(ClipItem.ClipboardPattern):
                case nameof(ClipItem.AppProcess):
                case nameof(ClipItem.AppTitlePattern):
                    // 絞り込み中は表示件数が変わるため、件数の表示も作り直す
                    OnPropertyChanged(nameof(StatusText));
                    break;

                case nameof(ClipItem.SequenceValue):
                    // トレイ側で進んだ値の取り込みは利用者の編集ではない。
                    // ここで数えてしまうと、開いているだけで「未保存」になり、
                    // そのうえ以降の取り込みが止まってしまう
                    if (adoptingExternalSequence)
                    {
                        break;
                    }

                    // 「次の番号」を画面で直接指定した場合は、トレイ側で進んだ値より優先する。
                    // 増分だけを変えたときは番号に触っていないため、ここには入らない。
                    if (sender is ClipItem edited && !string.IsNullOrEmpty(edited.Id))
                    {
                        _sequenceEditedIds.Add(edited.Id);
                    }

                    break;
            }

            if (e.PropertyName is nameof(ClipItem.Category) or nameof(ClipItem.CategoryId))
            {
                if (!_suppressOutlineRebuild)
                {
                    RefreshCategories();
                }
            }

            bool affectsOutline = e.PropertyName is nameof(ClipItem.Category)
                or nameof(ClipItem.CategoryId)
                or nameof(ClipItem.IsSeparator)
                or nameof(ClipItem.ClipboardCondition)
                || (HasFilter && e.PropertyName is nameof(ClipItem.Name)
                    or nameof(ClipItem.Text)
                    or nameof(ClipItem.ClipboardPattern)
                    or nameof(ClipItem.AppProcess)
                    or nameof(ClipItem.AppTitlePattern));
            if (affectsOutline && !_suppressOutlineRebuild)
            {
                RebuildMenuOutline();
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
                case nameof(ClipItem.ApplyToEachLine):
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
