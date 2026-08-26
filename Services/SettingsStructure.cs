using MyTaskTray.Models;

namespace MyTaskTray.Services
{
    /// <summary>
    /// 旧形式のカテゴリ文字列と新しい ID / レイアウトを相互に整合させる。
    /// 手編集された JSON もここで修復し、アプリ本体と設定画面は正規化済みの構造だけを扱う。
    /// </summary>
    internal static class SettingsStructure
    {
        public static bool Normalize(AppSettings settings)
        {
            bool changed = false;
            settings.Items ??= [];
            settings.Categories ??= [];
            settings.RegularMenu ??= [];
            settings.ContextualMenu ??= [];

            changed |= EnsureItemIds(settings.Items);
            changed |= NormalizeCategories(settings);

            List<MenuLayoutNode> regular = ReconcileLayout(
                settings.RegularMenu,
                settings.Items.Where(item => !IsContextual(item)));
            if (!LayoutEquals(settings.RegularMenu, regular))
            {
                settings.RegularMenu = regular;
                changed = true;
            }

            List<MenuLayoutNode> contextual = ReconcileLayout(
                settings.ContextualMenu,
                settings.Items.Where(IsContextual));
            if (!LayoutEquals(settings.ContextualMenu, contextual))
            {
                settings.ContextualMenu = contextual;
                changed = true;
            }

            if (settings.Version < AppSettings.CurrentVersion)
            {
                settings.Version = AppSettings.CurrentVersion;
                changed = true;
            }

            return changed;
        }

        /// <summary>
        /// レイアウトを、旧来のフラットな Items 順にも反映する。
        /// 通常／条件付き項目が占めていた位置は保ち、それぞれの部分列だけを並べ替える。
        /// </summary>
        public static void ApplyLayoutOrder(AppSettings settings)
        {
            ApplySectionOrder(settings.Items, settings.RegularMenu, contextual: false);
            ApplySectionOrder(settings.Items, settings.ContextualMenu, contextual: true);
        }

        /// <summary>現在のフラット順から、指定領域の明示的なレイアウトを作る。</summary>
        public static List<MenuLayoutNode> BuildLayout(
            IEnumerable<ClipItem> items,
            bool contextual)
        {
            List<MenuLayoutNode> result = [];
            Dictionary<string, MenuLayoutNode> categories = new(StringComparer.Ordinal);

            foreach (ClipItem item in items.Where(item => IsContextual(item) == contextual))
            {
                string categoryId = item.CategoryId.Trim();
                if (categoryId.Length == 0)
                {
                    result.Add(MenuLayoutNode.ForItem(item.Id));
                    continue;
                }

                if (!categories.TryGetValue(categoryId, out MenuLayoutNode? node))
                {
                    node = MenuLayoutNode.ForCategory(categoryId, []);
                    categories.Add(categoryId, node);
                    result.Add(node);
                }

                node.Children.Add(item.Id);
            }

            return result;
        }

        public static bool IsContextual(ClipItem item)
            => !item.IsSeparator && item.ClipboardCondition != ClipboardMatchKind.Always;

        private static bool EnsureItemIds(List<ClipItem> items)
        {
            bool changed = false;
            HashSet<string> used = new(StringComparer.Ordinal);
            foreach (ClipItem item in items)
            {
                string id = item.Id?.Trim() ?? string.Empty;
                if (id.Length == 0 || !used.Add(id))
                {
                    item.Id = ClipItem.NewId();
                    used.Add(item.Id);
                    changed = true;
                }
                else if (!string.Equals(item.Id, id, StringComparison.Ordinal))
                {
                    item.Id = id;
                    changed = true;
                }
            }

            return changed;
        }

        private static bool NormalizeCategories(AppSettings settings)
        {
            bool changed = false;
            List<ClipCategory> normalized = [];
            Dictionary<string, ClipCategory> byId = new(StringComparer.Ordinal);
            Dictionary<string, ClipCategory> byName = new(StringComparer.Ordinal);
            Dictionary<string, string> remappedIds = new(StringComparer.Ordinal);

            foreach (ClipCategory category in settings.Categories.Where(category => category is not null))
            {
                string oldId = category.Id?.Trim() ?? string.Empty;
                string name = category.Name?.Trim() ?? string.Empty;
                string color = CategoryAppearanceCatalog.NormalizeColor(category.Color);
                string icon = CategoryAppearanceCatalog.NormalizeIcon(category.Icon);
                if (name.Length == 0)
                {
                    changed = true;
                    continue;
                }

                if (byName.TryGetValue(name, out ClipCategory? sameName))
                {
                    if (oldId.Length > 0)
                    {
                        // 壊れた JSON で同じ旧 ID が複数名に使われていても、最初の定義を正本にする。
                        remappedIds.TryAdd(oldId, sameName.Id);
                    }

                    changed = true;
                    continue;
                }

                string id = oldId;
                if (id.Length == 0 || byId.ContainsKey(id))
                {
                    id = ClipCategory.NewId();
                    changed = true;
                }

                ClipCategory clean = new()
                {
                    Id = id,
                    Name = name,
                    Color = color,
                    Icon = icon,
                };
                normalized.Add(clean);
                byId[id] = clean;
                byName[name] = clean;
                if (oldId.Length > 0)
                {
                    remappedIds.TryAdd(oldId, id);
                }

                changed |= !string.Equals(category.Id, id, StringComparison.Ordinal)
                    || !string.Equals(category.Name, name, StringComparison.Ordinal)
                    || !string.Equals(category.Color, color, StringComparison.Ordinal)
                    || !string.Equals(category.Icon, icon, StringComparison.Ordinal);
            }

            foreach (ClipItem item in settings.Items)
            {
                string categoryId = item.CategoryId?.Trim() ?? string.Empty;
                if (remappedIds.TryGetValue(categoryId, out string? mapped))
                {
                    categoryId = mapped;
                }

                ClipCategory? category = categoryId.Length > 0
                    && byId.TryGetValue(categoryId, out ClipCategory? byExistingId)
                        ? byExistingId
                        : null;

                if (category is null)
                {
                    string legacyName = item.Category?.Trim() ?? string.Empty;
                    if (legacyName.Length > 0 && !byName.TryGetValue(legacyName, out category))
                    {
                        category = new ClipCategory
                        {
                            Id = ClipCategory.NewId(),
                            Name = legacyName,
                        };
                        normalized.Add(category);
                        byId[category.Id] = category;
                        byName[category.Name] = category;
                        changed = true;
                    }
                }

                string nextId = category?.Id ?? string.Empty;
                string nextName = category?.Name ?? string.Empty;
                if (!string.Equals(item.CategoryId, nextId, StringComparison.Ordinal))
                {
                    item.CategoryId = nextId;
                    changed = true;
                }

                if (!string.Equals(item.Category, nextName, StringComparison.Ordinal))
                {
                    item.Category = nextName;
                    changed = true;
                }
            }

            if (settings.RegularMenu is not null)
            {
                changed |= RemapLayoutCategories(settings.RegularMenu, remappedIds);
            }

            if (settings.ContextualMenu is not null)
            {
                changed |= RemapLayoutCategories(settings.ContextualMenu, remappedIds);
            }

            if (!CategoriesEqual(settings.Categories, normalized))
            {
                settings.Categories = normalized;
                changed = true;
            }

            return changed;
        }

        private static bool RemapLayoutCategories(
            IEnumerable<MenuLayoutNode> layout,
            Dictionary<string, string> remappedIds)
        {
            bool changed = false;
            foreach (MenuLayoutNode node in layout.Where(node => node is not null))
            {
                if (node.Kind == MenuLayoutNodeKind.Category
                    && remappedIds.TryGetValue(node.Id ?? string.Empty, out string? mapped)
                    && !string.Equals(node.Id, mapped, StringComparison.Ordinal))
                {
                    node.Id = mapped;
                    changed = true;
                }
            }

            return changed;
        }

        private static List<MenuLayoutNode> ReconcileLayout(
            IEnumerable<MenuLayoutNode> existing,
            IEnumerable<ClipItem> expectedItems)
        {
            List<ClipItem> expected = [.. expectedItems];
            Dictionary<string, ClipItem> byId = expected.ToDictionary(item => item.Id, StringComparer.Ordinal);
            HashSet<string> used = new(StringComparer.Ordinal);
            List<MenuLayoutNode> result = [];
            Dictionary<string, MenuLayoutNode> categoryNodes = new(StringComparer.Ordinal);

            foreach (MenuLayoutNode source in existing.Where(node => node is not null))
            {
                string id = source.Id?.Trim() ?? string.Empty;
                if (source.Kind == MenuLayoutNodeKind.Item)
                {
                    if (byId.TryGetValue(id, out ClipItem? item)
                        && item.CategoryId.Length == 0
                        && used.Add(id))
                    {
                        result.Add(MenuLayoutNode.ForItem(id));
                    }

                    continue;
                }

                if (id.Length == 0)
                {
                    continue;
                }

                if (!categoryNodes.TryGetValue(id, out MenuLayoutNode? categoryNode))
                {
                    categoryNode = MenuLayoutNode.ForCategory(id, []);
                    categoryNodes.Add(id, categoryNode);
                    result.Add(categoryNode);
                }

                foreach (string childId in source.Children ?? [])
                {
                    if (byId.TryGetValue(childId, out ClipItem? child)
                        && string.Equals(child.CategoryId, id, StringComparison.Ordinal)
                        && used.Add(childId))
                    {
                        categoryNode.Children.Add(childId);
                    }
                }
            }

            foreach (ClipItem item in expected)
            {
                if (!used.Add(item.Id))
                {
                    continue;
                }

                if (item.CategoryId.Length == 0)
                {
                    result.Add(MenuLayoutNode.ForItem(item.Id));
                    continue;
                }

                if (!categoryNodes.TryGetValue(item.CategoryId, out MenuLayoutNode? categoryNode))
                {
                    categoryNode = MenuLayoutNode.ForCategory(item.CategoryId, []);
                    categoryNodes.Add(item.CategoryId, categoryNode);
                    result.Add(categoryNode);
                }

                categoryNode.Children.Add(item.Id);
            }

            return [.. result.Where(node => node.Kind == MenuLayoutNodeKind.Item || node.Children.Count > 0)];
        }

        private static void ApplySectionOrder(
            List<ClipItem> items,
            IReadOnlyList<MenuLayoutNode> layout,
            bool contextual)
        {
            Dictionary<string, ClipItem> byId = items
                .Where(item => IsContextual(item) == contextual)
                .ToDictionary(item => item.Id, StringComparer.Ordinal);
            List<ClipItem> ordered = [];
            HashSet<string> used = new(StringComparer.Ordinal);

            foreach (MenuLayoutNode node in layout)
            {
                IEnumerable<string> ids = node.Kind == MenuLayoutNodeKind.Item
                    ? [node.Id]
                    : node.Children ?? [];
                foreach (string id in ids)
                {
                    if (byId.TryGetValue(id, out ClipItem? item) && used.Add(id))
                    {
                        ordered.Add(item);
                    }
                }
            }

            ordered.AddRange(items.Where(item => IsContextual(item) == contextual && used.Add(item.Id)));
            List<int> positions = [.. items
                .Select((item, index) => (item, index))
                .Where(entry => IsContextual(entry.item) == contextual)
                .Select(entry => entry.index)];
            for (int i = 0; i < positions.Count && i < ordered.Count; i++)
            {
                items[positions[i]] = ordered[i];
            }
        }

        private static bool CategoriesEqual(
            List<ClipCategory> left,
            List<ClipCategory> right)
            => left.Count == right.Count
                && left.Zip(right).All(pair => pair.First is not null
                    && string.Equals(pair.First.Id, pair.Second.Id, StringComparison.Ordinal)
                    && string.Equals(pair.First.Name, pair.Second.Name, StringComparison.Ordinal)
                    && string.Equals(pair.First.Color, pair.Second.Color, StringComparison.Ordinal)
                    && string.Equals(pair.First.Icon, pair.Second.Icon, StringComparison.Ordinal));

        private static bool LayoutEquals(
            List<MenuLayoutNode> left,
            List<MenuLayoutNode> right)
            => left.Count == right.Count
                && left.Zip(right).All(pair => pair.First is not null
                    && pair.First.Kind == pair.Second.Kind
                    && string.Equals(pair.First.Id, pair.Second.Id, StringComparison.Ordinal)
                    && (pair.First.Children ?? []).SequenceEqual(pair.Second.Children, StringComparer.Ordinal));
    }
}
