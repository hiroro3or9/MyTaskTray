namespace MyTaskTray.Models
{
    /// <summary>
    /// JSON に永続化する設定全体。
    /// </summary>
    public class AppSettings
    {
        /// <summary>設定ファイルのフォーマットバージョン。</summary>
        public int Version { get; set; } = 1;

        /// <summary>コピー項目。リストの順序がそのままメニューの順序になる。</summary>
        public List<ClipItem> Items { get; set; } = new();

        /// <summary>コピーしたときに画面右下へ小さな通知を出すかどうか。</summary>
        public bool ShowCopyNotification { get; set; } = true;

        /// <summary>初回起動時に表示するサンプル設定を作る。</summary>
        public static AppSettings CreateDefault() => new()
        {
            Items = new List<ClipItem>
            {
                new() { Name = "メールアドレス", Text = "example@example.com" },
                new() { Name = "電話番号", Text = "03-0000-0000" },
                new() { IsSeparator = true },
                new() { Category = "日付", Name = "今日 (2026/07/30)", Text = "{date}" },
                new() { Category = "日付", Name = "今日 (20260730)", Text = "{date:yyyyMMdd}" },
                new() { Category = "日付", Name = "現在の日時", Text = "{datetime}" },
                new() { Category = "日付", Name = "明日", Text = "{date+1}" },
                new() { Category = "日付", Name = "今月末", Text = "{monthend}" },
                new() { IsSeparator = true },
                new() { Category = "定型文", Name = "お礼", Text = "お世話になっております。ご対応ありがとうございました。" },
                new() { Category = "定型文", Name = "確認依頼", Text = "ご確認のほど、よろしくお願いいたします。" },
                new() { Category = "定型文", Name = "議事録の見出し", Text = "# {date:yyyy/MM/dd} 定例ミーティング 議事録" },
            },
        };

        public AppSettings Clone() => new()
        {
            Version = Version,
            ShowCopyNotification = ShowCopyNotification,
            Items = Items.Select(i => i.Clone()).ToList(),
        };
    }
}
