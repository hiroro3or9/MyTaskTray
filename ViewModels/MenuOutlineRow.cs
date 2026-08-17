using MyTaskTray.Models;

namespace MyTaskTray.ViewModels
{
    /// <summary>設定画面のメニュー構成に表示する領域。</summary>
    public enum MenuOutlineSection
    {
        Regular,
        Contextual,
    }

    /// <summary>設定画面のアウトライン 1 行の種類。</summary>
    public enum MenuOutlineRowKind
    {
        Section,
        Category,
        Item,
    }

    /// <summary>
    /// フラットな設定一覧から、実際のトレイメニューに近い見た目を作る表示用の行。
    /// 保存対象ではなく、元データは常に <see cref="ClipItem"/> の一覧に置く。
    /// </summary>
    public sealed class MenuOutlineRow
    {
        private MenuOutlineRow(
            MenuOutlineRowKind kind,
            MenuOutlineSection section,
            string categoryId,
            string category,
            string categoryColor,
            string categoryIcon,
            ClipItem? item,
            int itemCount,
            bool isExpanded)
        {
            Kind = kind;
            Section = section;
            CategoryId = categoryId;
            Category = category;
            CategoryColor = CategoryAppearanceCatalog.NormalizeColor(categoryColor);
            CategoryIcon = CategoryAppearanceCatalog.NormalizeIcon(categoryIcon);
            Item = item;
            ItemCount = itemCount;
            IsExpanded = isExpanded;
        }

        public MenuOutlineRowKind Kind { get; }

        public MenuOutlineSection Section { get; }

        public string CategoryId { get; }

        public string Category { get; }

        public string CategoryColor { get; }

        public string CategoryIcon { get; }

        public bool HasCategoryColor => CategoryColor.Length > 0;

        public bool HasCategoryIcon => CategoryAppearanceCatalog.FindIcon(CategoryIcon) is not null;

        public string CategoryColorBrush => HasCategoryColor ? CategoryColor : "Transparent";

        public string CategoryIconBrush => HasCategoryColor ? CategoryColor : "#808080";

        public string CategoryIconGlyph => CategoryAppearanceCatalog.GetIconGlyph(CategoryIcon);

        public string CategoryIconFontFamily => CategoryAppearanceCatalog.IconFontFamily;

        public ClipItem? Item { get; }

        public int ItemCount { get; }

        public bool IsExpanded { get; }

        public bool IsSection => Kind == MenuOutlineRowKind.Section;

        public bool IsCategory => Kind == MenuOutlineRowKind.Category;

        public bool IsItem => Kind == MenuOutlineRowKind.Item;

        public bool IsCategoryItem => IsItem && Category.Length > 0;

        public string SectionTitle => Section == MenuOutlineSection.Regular
            ? "通常メニュー"
            : "この内容でできること";

        public string SectionHint => Section == MenuOutlineSection.Regular
            ? "常に使える項目"
            : "クリップボードの内容に合うとき表示";

        public string ItemCountText => $"{ItemCount}件";

        public string ExpandGlyph => IsExpanded ? "▾" : "▸";

        public static MenuOutlineRow CreateSection(MenuOutlineSection section)
            => new(
                MenuOutlineRowKind.Section,
                section,
                string.Empty,
                string.Empty,
                string.Empty,
                string.Empty,
                null,
                0,
                true);

        public static MenuOutlineRow CreateCategory(
            MenuOutlineSection section,
            ClipCategory category,
            int itemCount,
            bool isExpanded)
            => new(
                MenuOutlineRowKind.Category,
                section,
                category.Id,
                category.Name,
                category.Color,
                category.Icon,
                null,
                itemCount,
                isExpanded);

        public static MenuOutlineRow CreateItem(
            MenuOutlineSection section,
            ClipItem item,
            string categoryId,
            string category)
            => new(
                MenuOutlineRowKind.Item,
                section,
                categoryId,
                category,
                string.Empty,
                string.Empty,
                item,
                0,
                true);
    }
}
