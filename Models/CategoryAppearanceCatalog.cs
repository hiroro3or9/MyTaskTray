namespace MyTaskTray.Models
{
    /// <summary>設定画面に表示するカテゴリ色 1 件。</summary>
    public sealed record CategoryColorPreset(
        string Value,
        string Name,
        string Brush,
        bool HasValue);

    /// <summary>設定画面に表示するカテゴリ用アイコン 1 件。</summary>
    public sealed record CategoryIconPreset(
        string Value,
        string Name,
        string Glyph,
        string FontFamily,
        bool HasValue);

    /// <summary>
    /// カテゴリ装飾のプリセット。保存する値と画面・トレイで使う表示を 1 箇所に集める。
    /// アイコンは Windows 10 以降に標準搭載される Segoe MDL2 Assets のグリフを使う。
    /// </summary>
    public static class CategoryAppearanceCatalog
    {
        public const string IconFontFamily = "Segoe MDL2 Assets";

        public static IReadOnlyList<CategoryColorPreset> Colors { get; } =
        [
            new(string.Empty, "色なし", "Transparent", false),
            new("#4F8EF7", "ブルー", "#4F8EF7", true),
            new("#2A9D8F", "ティール", "#2A9D8F", true),
            new("#55A65A", "グリーン", "#55A65A", true),
            new("#D59618", "アンバー", "#D59618", true),
            new("#E36A35", "オレンジ", "#E36A35", true),
            new("#D94B4B", "レッド", "#D94B4B", true),
            new("#8B5CF6", "パープル", "#8B5CF6", true),
            new("#D65A9E", "ピンク", "#D65A9E", true),
            new("#708090", "スレート", "#708090", true),
        ];

        public static IReadOnlyList<CategoryIconPreset> Icons { get; } =
        [
            new(string.Empty, "アイコンなし", "—", "Yu Gothic UI", false),
            new("folder", "フォルダー", "\uE8B7", IconFontFamily, true),
            new("calendar", "カレンダー", "\uE787", IconFontFamily, true),
            new("star", "スター", "\uE734", IconFontFamily, true),
            new("message", "メッセージ", "\uE8BD", IconFontFamily, true),
            new("code", "コード", "\uE943", IconFontFamily, true),
            new("link", "リンク", "\uE71B", IconFontFamily, true),
            new("work", "仕事", "\uE821", IconFontFamily, true),
            new("person", "個人", "\uE77B", IconFontFamily, true),
            new("globe", "Web", "\uE774", IconFontFamily, true),
        ];

        public static string NormalizeColor(string? value)
        {
            string color = (value ?? string.Empty).Trim().ToUpperInvariant();
            if (color.Length != 7 || color[0] != '#')
            {
                return string.Empty;
            }

            return color.Skip(1).All(Uri.IsHexDigit) ? color : string.Empty;
        }

        public static string NormalizeIcon(string? value) => (value ?? string.Empty).Trim();

        public static CategoryIconPreset? FindIcon(string? value)
        {
            string icon = NormalizeIcon(value);
            return Icons.FirstOrDefault(candidate => candidate.HasValue
                && string.Equals(candidate.Value, icon, StringComparison.Ordinal));
        }

        public static string GetIconGlyph(string? value) => FindIcon(value)?.Glyph ?? string.Empty;
    }
}
