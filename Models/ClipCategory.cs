using System.Text.Json.Serialization;

namespace MyTaskTray.Models
{
    /// <summary>表示名が変わっても所属を保てる、コピー項目のカテゴリ定義。</summary>
    public sealed class ClipCategory
    {
        public string Id { get; set; } = string.Empty;

        public string Name { get; set; } = string.Empty;

        /// <summary>カテゴリのアクセント色。空文字または #RRGGBB。</summary>
        public string Color { get; set; } = string.Empty;

        /// <summary>カテゴリのプリセットアイコンキー。空文字ならアイコンなし。</summary>
        public string Icon { get; set; } = string.Empty;

        [JsonIgnore]
        public bool HasColor => CategoryAppearanceCatalog.NormalizeColor(Color).Length > 0;

        [JsonIgnore]
        public bool HasIcon => CategoryAppearanceCatalog.FindIcon(Icon) is not null;

        [JsonIgnore]
        public string ColorBrush => HasColor
            ? CategoryAppearanceCatalog.NormalizeColor(Color)
            : "Transparent";

        [JsonIgnore]
        public string IconBrush => HasColor
            ? CategoryAppearanceCatalog.NormalizeColor(Color)
            : "#808080";

        [JsonIgnore]
        public string IconGlyph => CategoryAppearanceCatalog.GetIconGlyph(Icon);

        [JsonIgnore]
        public string IconFontFamily => CategoryAppearanceCatalog.IconFontFamily;

        public static string NewId() => Guid.NewGuid().ToString("N");

        public ClipCategory Clone() => new()
        {
            Id = Id,
            Name = Name,
            Color = Color,
            Icon = Icon,
        };
    }
}
