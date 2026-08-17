namespace MyTaskTray.Models
{
    /// <summary>表示名が変わっても所属を保てる、コピー項目のカテゴリ定義。</summary>
    public sealed class ClipCategory
    {
        public string Id { get; set; } = string.Empty;

        public string Name { get; set; } = string.Empty;

        public static string NewId() => Guid.NewGuid().ToString("N");

        public ClipCategory Clone() => new()
        {
            Id = Id,
            Name = Name,
        };
    }
}
