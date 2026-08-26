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
    /// <summary>カテゴリと、通常／条件付きメニューのアウトライン構造を同期・並べ替えする。</summary>
    public partial class SettingsViewModel
    {
        /// <summary>既存項目のカテゴリを重複なく集めて候補を作り直す。</summary>
        public void RefreshCategories()
        {
            List<ClipCategory> categories = [.. _categories
                .Where(category => Items.Any(item => string.Equals(
                    item.CategoryId,
                    category.Id,
                    StringComparison.Ordinal)))
                .OrderBy(category => category.Name, StringComparer.CurrentCulture)
                .Select(category => category.Clone())];

            KnownCategories.Clear();
            foreach (ClipCategory category in categories)
            {
                KnownCategories.Add(category);
            }

            OnPropertyChanged(nameof(HasCategories));
        }

        /// <summary>カテゴリ候補があるかどうか。</summary>
        public bool HasCategories => KnownCategories.Count > 0;

        /// <summary>カテゴリの開閉を切り替える。検索中は一致項目を必ず見せるため開閉しない。</summary>
        public void ToggleCategory(MenuOutlineRow row)
        {
            if (!row.IsCategory || HasFilter)
            {
                return;
            }

            (MenuOutlineSection, string) key = (row.Section, row.CategoryId);
            if (!_collapsedCategories.Add(key))
            {
                _collapsedCategories.Remove(key);
            }

            _selectedOutlineRow = row;
            RebuildMenuOutline();
        }

        /// <summary>選択カテゴリの名前を、そのカテゴリに属する全項目へまとめて反映する。</summary>
        public void RenameSelectedCategory(string newName)
        {
            if (SelectedOutlineRow is not { IsCategory: true } selected)
            {
                return;
            }

            string normalized = (newName ?? string.Empty).Trim();
            if (normalized.Length == 0)
            {
                return;
            }

            MenuOutlineSection section = selected.Section;
            ClipCategory? source = FindCategoryById(selected.CategoryId);
            if (source is null)
            {
                return;
            }

            ClipCategory? target = _categories.FirstOrDefault(category =>
                !string.Equals(category.Id, source.Id, StringComparison.Ordinal)
                && string.Equals(category.Name, normalized, StringComparison.Ordinal));
            string targetId = target?.Id ?? source.Id;

            _suppressOutlineRebuild = true;
            _synchronizingCategories = true;
            try
            {
                if (target is null)
                {
                    source.Name = normalized;
                }

                foreach (ClipItem item in Items.Where(item => string.Equals(
                    item.CategoryId,
                    source.Id,
                    StringComparison.Ordinal)))
                {
                    item.Category = normalized;
                    item.CategoryId = targetId;
                }

                if (target is not null)
                {
                    _categories.Remove(source);
                }
            }
            finally
            {
                _synchronizingCategories = false;
                _suppressOutlineRebuild = false;
            }

            RefreshCategories();
            RebuildMenuOutline();
            SelectCategory(section, targetId);
        }

        /// <summary>選択カテゴリをなくし、中の項目をトップレベルへ移す。</summary>
        public void RemoveSelectedCategory()
        {
            if (SelectedOutlineRow is not { IsCategory: true } selected)
            {
                return;
            }

            string categoryId = selected.CategoryId;
            MenuOutlineSection section = selected.Section;
            List<ClipItem> affected = [.. Items.Where(item => string.Equals(
                item.CategoryId,
                categoryId,
                StringComparison.Ordinal))];

            _suppressOutlineRebuild = true;
            _synchronizingCategories = true;
            try
            {
                foreach (ClipItem item in affected)
                {
                    item.Category = string.Empty;
                    item.CategoryId = string.Empty;
                }

                _categories.RemoveAll(category => string.Equals(
                    category.Id,
                    categoryId,
                    StringComparison.Ordinal));
            }
            finally
            {
                _synchronizingCategories = false;
                _suppressOutlineRebuild = false;
            }

            RefreshCategories();
            RebuildMenuOutline();

            ClipItem? next = affected.FirstOrDefault(item => GetSection(item) == section)
                ?? affected.FirstOrDefault();
            SelectedItem = next;
        }

        public bool CategoryExists(string category, string? excludingCategoryId = null)
        {
            string normalized = (category ?? string.Empty).Trim();
            return normalized.Length > 0
                && _categories.Any(existing => string.Equals(existing.Name, normalized, StringComparison.Ordinal)
                    && (excludingCategoryId is null
                        || !string.Equals(existing.Id, excludingCategoryId, StringComparison.Ordinal)));
        }

        private ClipCategory? FindCategoryById(string? categoryId)
            => _categories.FirstOrDefault(category => string.Equals(
                category.Id,
                categoryId,
                StringComparison.Ordinal));

        private ClipCategory? FindCategoryByName(string? name)
        {
            string normalized = NormalizeCategory(name);
            return _categories.FirstOrDefault(category => string.Equals(
                category.Name,
                normalized,
                StringComparison.Ordinal));
        }

        /// <summary>
        /// 画面で直接編集される旧来の Category 文字列を、保存上の正本である CategoryId へ同期する。
        /// 入力中の末尾空白は TextBox から消さず、保存時の正規化だけに任せる。
        /// </summary>
        private void SynchronizeItemCategory(ClipItem item)
        {
            if (_synchronizingCategories)
            {
                return;
            }

            string name = NormalizeCategory(item.Category);
            ClipCategory? target = null;
            if (name.Length > 0)
            {
                ClipCategory? current = FindCategoryById(item.CategoryId);
                target = current is not null
                    && string.Equals(current.Name, name, StringComparison.Ordinal)
                        ? current
                        : FindCategoryByName(name);
                if (target is null)
                {
                    target = new ClipCategory
                    {
                        Id = ClipCategory.NewId(),
                        Name = name,
                    };
                    _categories.Add(target);
                }
            }

            bool previousSuppress = _suppressOutlineRebuild;
            _synchronizingCategories = true;
            _suppressOutlineRebuild = true;
            try
            {
                string nextId = target?.Id ?? string.Empty;
                if (!string.Equals(item.CategoryId, nextId, StringComparison.Ordinal))
                {
                    item.CategoryId = nextId;
                }

                if (name.Length == 0 && item.Category.Length > 0)
                {
                    item.Category = string.Empty;
                }
            }
            finally
            {
                _suppressOutlineRebuild = previousSuppress;
                _synchronizingCategories = false;
            }

            RemoveUnusedCategories();
        }

        private void SynchronizeAllItemCategories()
        {
            foreach (ClipItem item in Items)
            {
                SynchronizeItemCategory(item);
            }

            RemoveUnusedCategories();
        }

        private void RemoveUnusedCategories()
        {
            HashSet<string> used = [.. Items
                .Select(item => item.CategoryId)
                .Where(id => id.Length > 0)];
            _categories.RemoveAll(category => !used.Contains(category.Id));
            _collapsedCategories.RemoveWhere(entry => !used.Contains(entry.CategoryId));
        }

        /// <summary>選択行を、画面に見えている同じ階層の中で上下へ移動する。</summary>
        public void MoveSelectedOutline(int offset)
        {
            if (!CanReorder || SelectedOutlineRow is not { } selected || offset == 0)
            {
                return;
            }

            List<OutlineUnit> units = BuildSectionUnits(selected.Section);
            if (selected.IsCategory)
            {
                int index = units.FindIndex(unit => string.Equals(
                    unit.CategoryId,
                    selected.CategoryId,
                    StringComparison.Ordinal));
                int target = index + Math.Sign(offset);
                if (index < 0 || target < 0 || target >= units.Count)
                {
                    return;
                }

                (units[index], units[target]) = (units[target], units[index]);
                ApplySectionOrder(selected.Section, units.SelectMany(unit => unit.Items));
                SelectCategory(selected.Section, selected.CategoryId);
                return;
            }

            if (selected.Item is not { } moving)
            {
                return;
            }

            if (selected.CategoryId.Length > 0)
            {
                OutlineUnit? categoryUnit = units.FirstOrDefault(
                    unit => string.Equals(
                        unit.CategoryId,
                        selected.CategoryId,
                        StringComparison.Ordinal));
                if (categoryUnit is null)
                {
                    return;
                }

                int index = categoryUnit.Items.IndexOf(moving);
                int target = index + Math.Sign(offset);
                if (index < 0 || target < 0 || target >= categoryUnit.Items.Count)
                {
                    return;
                }

                (categoryUnit.Items[index], categoryUnit.Items[target])
                    = (categoryUnit.Items[target], categoryUnit.Items[index]);
            }
            else
            {
                int index = units.FindIndex(unit => unit.Items.Count == 1
                    && ReferenceEquals(unit.Items[0], moving));
                int target = index + Math.Sign(offset);
                if (index < 0 || target < 0 || target >= units.Count)
                {
                    return;
                }

                (units[index], units[target]) = (units[target], units[index]);
            }

            ApplySectionOrder(selected.Section, units.SelectMany(unit => unit.Items));
            SelectedItem = moving;
        }

        /// <summary>ドラッグした項目またはカテゴリを、同じメニュー領域の指定行へ移す。</summary>
        public bool MoveOutlineRow(MenuOutlineRow moving, MenuOutlineRow target, bool below)
        {
            if (HasFilter
                || moving.IsSection
                || target.IsSection
                || moving.Section != target.Section
                || ReferenceEquals(moving, target))
            {
                return false;
            }

            if (moving.IsCategory)
            {
                string targetCategoryId = target.CategoryId;
                if (string.Equals(moving.CategoryId, targetCategoryId, StringComparison.Ordinal))
                {
                    return false;
                }

                List<OutlineUnit> units = BuildSectionUnits(moving.Section);
                int from = units.FindIndex(unit => string.Equals(
                    unit.CategoryId,
                    moving.CategoryId,
                    StringComparison.Ordinal));
                int to = targetCategoryId.Length > 0
                    ? units.FindIndex(unit => string.Equals(
                        unit.CategoryId,
                        targetCategoryId,
                        StringComparison.Ordinal))
                    : units.FindIndex(unit => target.Item is not null
                        && unit.Items.Count == 1
                        && ReferenceEquals(unit.Items[0], target.Item));
                if (from < 0 || to < 0)
                {
                    return false;
                }

                OutlineUnit unit = units[from];
                units.RemoveAt(from);
                if (from < to)
                {
                    to--;
                }

                if (below)
                {
                    to++;
                }

                units.Insert(Math.Clamp(to, 0, units.Count), unit);
                ApplySectionOrder(moving.Section, units.SelectMany(entry => entry.Items));
                SelectCategory(moving.Section, moving.CategoryId);
                return true;
            }

            if (moving.Item is not { } movingItem)
            {
                return false;
            }

            string destinationCategoryId = target.CategoryId;
            string destinationCategory = target.Category;

            _suppressOutlineRebuild = true;
            _synchronizingCategories = true;
            try
            {
                movingItem.CategoryId = destinationCategoryId;
                movingItem.Category = destinationCategory;

                List<ClipItem> ordered = [.. BuildSectionUnits(moving.Section)
                    .SelectMany(unit => unit.Items)
                    .Where(item => !ReferenceEquals(item, movingItem))];

                int insertAt;
                if (target.Item is { } targetItem)
                {
                    insertAt = ordered.IndexOf(targetItem);
                    if (insertAt < 0)
                    {
                        return false;
                    }

                    if (below)
                    {
                        insertAt++;
                    }
                }
                else
                {
                    int lastInCategory = ordered.FindLastIndex(
                        item => string.Equals(
                            item.CategoryId,
                            destinationCategoryId,
                            StringComparison.Ordinal));
                    insertAt = lastInCategory + 1;
                }

                ordered.Insert(Math.Clamp(insertAt, 0, ordered.Count), movingItem);
                ApplySectionOrderCore(moving.Section, ordered);
            }
            finally
            {
                _synchronizingCategories = false;
                _suppressOutlineRebuild = false;
            }

            RemoveUnusedCategories();
            RefreshCategories();
            RebuildMenuOutline();
            SelectedItem = movingItem;
            return true;
        }

        private void RebuildMenuOutline()
        {
            ClipItem? selectedItem = SelectedItem;
            MenuOutlineSection? selectedSection = _selectedOutlineRow?.Section;
            string selectedCategoryId = _selectedOutlineRow?.IsCategory == true
                ? _selectedOutlineRow.CategoryId
                : string.Empty;

            _rebuildingOutline = true;
            try
            {
                MenuOutline.Clear();
                AddSectionRows(MenuOutlineSection.Regular);
                AddSectionRows(MenuOutlineSection.Contextual);
            }
            finally
            {
                _rebuildingOutline = false;
            }

            MenuOutlineRow? nextSelection = selectedItem is not null
                ? MenuOutline.FirstOrDefault(row => ReferenceEquals(row.Item, selectedItem))
                : selectedCategoryId.Length > 0 && selectedSection is { } section
                    ? MenuOutline.FirstOrDefault(row => row.IsCategory
                        && row.Section == section
                        && string.Equals(
                            row.CategoryId,
                            selectedCategoryId,
                            StringComparison.Ordinal))
                    : null;

            if (!ReferenceEquals(_selectedOutlineRow, nextSelection))
            {
                _selectedOutlineRow = nextSelection;
                OnPropertyChanged(nameof(SelectedOutlineRow));
            }

            OnPropertyChanged(nameof(CanDelete));
            OnPropertyChanged(nameof(CanReorder));
            OnPropertyChanged(nameof(IsCategoryEditable));
            OnPropertyChanged(nameof(ShowEditorHint));
            OnPropertyChanged(nameof(EditorHint));
            OnPropertyChanged(nameof(SelectedCategoryItemCount));
            OnPropertyChanged(nameof(SelectedCategoryColor));
            OnPropertyChanged(nameof(SelectedCategoryIcon));
            OnPropertyChanged(nameof(HasSelectedCategoryAppearance));
        }

        private void AddSectionRows(MenuOutlineSection section)
        {
            List<ClipItem> sectionItems = [.. Items
                .Where(item => GetSection(item) == section)
                .Where(MatchesFilter)];

            // 通常メニューは空でも追加先として意味がある。条件付き領域は項目があるときだけ出す。
            if (section == MenuOutlineSection.Contextual && sectionItems.Count == 0)
            {
                return;
            }

            MenuOutline.Add(MenuOutlineRow.CreateSection(section));

            HashSet<string> emittedCategories = new(StringComparer.Ordinal);
            foreach (ClipItem item in sectionItems)
            {
                string categoryId = item.CategoryId;
                if (categoryId.Length == 0)
                {
                    MenuOutline.Add(MenuOutlineRow.CreateItem(
                        section,
                        item,
                        string.Empty,
                        string.Empty));
                    continue;
                }

                if (!emittedCategories.Add(categoryId))
                {
                    continue;
                }

                ClipCategory category = FindCategoryById(categoryId) ?? new ClipCategory
                {
                    Id = categoryId,
                    Name = NormalizeCategory(item.Category),
                };
                List<ClipItem> children = [.. sectionItems
                    .Where(candidate => string.Equals(
                        candidate.CategoryId,
                        categoryId,
                        StringComparison.Ordinal))];
                bool expanded = HasFilter || !_collapsedCategories.Contains((section, categoryId));
                MenuOutline.Add(MenuOutlineRow.CreateCategory(
                    section,
                    category,
                    children.Count,
                    expanded));

                if (!expanded)
                {
                    continue;
                }

                foreach (ClipItem child in children)
                {
                    MenuOutline.Add(MenuOutlineRow.CreateItem(
                        section,
                        child,
                        categoryId,
                        category.Name));
                }
            }
        }

        private void SelectOutlineForItem(ClipItem? item)
        {
            MenuOutlineRow? row = item is null
                ? null
                : MenuOutline.FirstOrDefault(candidate => ReferenceEquals(candidate.Item, item));
            if (row is null && item is not null && item.CategoryId.Length > 0 && !HasFilter)
            {
                _collapsedCategories.Remove((GetSection(item), item.CategoryId));
                RebuildMenuOutline();
                row = MenuOutline.FirstOrDefault(candidate => ReferenceEquals(candidate.Item, item));
            }

            if (ReferenceEquals(_selectedOutlineRow, row))
            {
                return;
            }

            _selectedOutlineRow = row;
            OnPropertyChanged(nameof(SelectedOutlineRow));
        }

        private void SelectCategory(MenuOutlineSection section, string categoryId)
        {
            SelectedOutlineRow = MenuOutline.FirstOrDefault(row => row.IsCategory
                && row.Section == section
                && string.Equals(row.CategoryId, categoryId, StringComparison.Ordinal));
        }

        private List<OutlineUnit> BuildSectionUnits(MenuOutlineSection section)
        {
            List<ClipItem> sectionItems = [.. Items.Where(item => GetSection(item) == section)];
            List<OutlineUnit> units = [];
            Dictionary<string, OutlineUnit> categories = new(StringComparer.Ordinal);

            foreach (ClipItem item in sectionItems)
            {
                string categoryId = item.CategoryId;
                if (categoryId.Length == 0)
                {
                    units.Add(new OutlineUnit(string.Empty, string.Empty, [item]));
                    continue;
                }

                if (!categories.TryGetValue(categoryId, out OutlineUnit? unit))
                {
                    string category = FindCategoryById(categoryId)?.Name
                        ?? NormalizeCategory(item.Category);
                    unit = new OutlineUnit(categoryId, category, []);
                    categories.Add(categoryId, unit);
                    units.Add(unit);
                }

                unit.Items.Add(item);
            }

            return units;
        }

        private void ApplySectionOrder(MenuOutlineSection section, IEnumerable<ClipItem> ordered)
        {
            _suppressOutlineRebuild = true;
            try
            {
                ApplySectionOrderCore(section, [.. ordered]);
            }
            finally
            {
                _suppressOutlineRebuild = false;
            }

            RebuildMenuOutline();
        }

        private void ApplySectionOrderCore(MenuOutlineSection section, List<ClipItem> ordered)
        {
            List<ClipItem> final = [.. Items];
            List<int> positions = [.. final
                .Select((item, index) => (item, index))
                .Where(entry => GetSection(entry.item) == section)
                .Select(entry => entry.index)];
            if (positions.Count != ordered.Count)
            {
                return;
            }

            for (int i = 0; i < positions.Count; i++)
            {
                final[positions[i]] = ordered[i];
            }

            for (int target = 0; target < final.Count; target++)
            {
                int current = Items.IndexOf(final[target]);
                if (current >= 0 && current != target)
                {
                    Items.Move(current, target);
                }
            }
        }

        private static MenuOutlineSection GetSection(ClipItem item)
            => item.IsSeparator || item.ClipboardCondition == ClipboardMatchKind.Always
                ? MenuOutlineSection.Regular
                : MenuOutlineSection.Contextual;

        private static string NormalizeCategory(string? category) => (category ?? string.Empty).Trim();

        private sealed record OutlineUnit(
            string CategoryId,
            string Category,
            List<ClipItem> Items);
    }
}
