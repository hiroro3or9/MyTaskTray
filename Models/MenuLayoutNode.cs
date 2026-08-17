using System.Text.Json.Serialization;

namespace MyTaskTray.Models
{
    /// <summary>メニュー領域の直下に置く要素の種類。</summary>
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public enum MenuLayoutNodeKind
    {
        Item,
        Category,
    }

    /// <summary>
    /// 通常メニューまたは条件付きメニューの直下 1 件。
    /// Item の Id は項目 ID、Category の Id はカテゴリ ID を参照する。
    /// </summary>
    public sealed class MenuLayoutNode
    {
        public MenuLayoutNodeKind Kind { get; set; }

        public string Id { get; set; } = string.Empty;

        /// <summary>カテゴリ配下の項目 ID。Item ノードでは空。</summary>
        public List<string> Children { get; set; } = [];

        public MenuLayoutNode Clone() => new()
        {
            Kind = Kind,
            Id = Id,
            Children = [.. Children ?? []],
        };

        public static MenuLayoutNode ForItem(string itemId) => new()
        {
            Kind = MenuLayoutNodeKind.Item,
            Id = itemId,
        };

        public static MenuLayoutNode ForCategory(string categoryId, IEnumerable<string> itemIds) => new()
        {
            Kind = MenuLayoutNodeKind.Category,
            Id = categoryId,
            Children = [.. itemIds],
        };
    }
}
