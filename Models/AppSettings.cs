using System.Text.Json.Serialization;
using MyTaskTray.Services;

namespace MyTaskTray.Models
{
    /// <summary>
    /// JSON に永続化する設定全体。
    /// </summary>
    public class AppSettings
    {
        public const int CurrentVersion = 3;

        /// <summary>
        /// 設定ファイルを読めず、既定値で代用しているかどうか。
        /// この状態のまま保存すると利用者の設定を既定値で上書きしてしまうため、
        /// 連番の自動保存のような「利用者が指示していない保存」は行わない。
        /// </summary>
        [JsonIgnore]
        public bool IsFallback { get; set; }

        /// <summary>設定ファイルのフォーマットバージョン。</summary>
        public int Version { get; set; } = CurrentVersion;

        /// <summary>
        /// コピー項目。通常は各メニューの明示レイアウトで並べ、
        /// 旧設定の移行や未知の項目にはこのリスト順をフォールバックとして使う。
        /// </summary>
        public List<ClipItem> Items { get; set; } = [];

        /// <summary>表示名から独立したカテゴリ定義。</summary>
        public List<ClipCategory> Categories { get; set; } = [];

        /// <summary>通常メニュー直下の項目・カテゴリ順。</summary>
        public List<MenuLayoutNode> RegularMenu { get; set; } = [];

        /// <summary>「この内容でできること」直下の項目・カテゴリ順。</summary>
        public List<MenuLayoutNode> ContextualMenu { get; set; } = [];

        /// <summary>コピーしたときに画面右下へ小さな通知を出すかどうか。</summary>
        public bool ShowCopyNotification { get; set; } = true;

        /// <summary>
        /// 連続コピーが 1 件として集める、クリップボード更新の範囲。
        ///
        /// <para>
        /// 既定は「操作した直後」で、これは従来（Ctrl+C のみ）と異なる。
        /// この項目を持たない設定ファイルを読んでも既定値が入るため、
        /// 連続コピーの集まり方だけは以前と変わる。
        /// 取りこぼしのほうが実害が大きいと判断した（DESIGN_COPY_INTENT.md §8-1）。
        /// 従来の挙動は <see cref="Models.SequentialCaptureTrigger.CopyKey"/> で選べる。
        /// </para>
        /// </summary>
        public SequentialCaptureTrigger SequentialCaptureTrigger { get; set; }
            = SequentialCaptureTrigger.UserInput;

        /// <summary>
        /// トレイメニューをカーソル位置へ表示するグローバルホットキー。
        /// 空文字なら登録しない。既定で他アプリのキーを奪わないよう、初期値は空にする。
        /// </summary>
        public string MenuHotKey { get; set; } = string.Empty;

        /// <summary>
        /// Swap コピー（選択中の文字列とクリップボードの入れ替え）を実行するグローバルホットキー。
        /// 空文字なら登録しない。
        ///
        /// <para>
        /// この機能はトレイメニューからは実行できない。メニューを開くと前面が移り、
        /// 貼り付け先の選択とフォーカスが失われるため（DESIGN_SWAP_COPY.md §2）。
        /// </para>
        /// </summary>
        public string SwapCopyHotKey { get; set; } = string.Empty;

        /// <summary>
        /// 組み込みアクションをメニューへ表示するかどうか。キーは安定したアクション ID。
        /// 記録がないアクションは、アクション定義側の既定値を使う。
        /// </summary>
        public Dictionary<string, bool> ActionStates { get; set; }
            = new(StringComparer.Ordinal);

        /// <summary>
        /// スプリントの区切りを数え始める日（どれか 1 つのスプリントの開始日）。
        /// null なら未設定で、<c>@sprint</c> を使った差し込みは展開されない。
        /// </summary>
        public DateTime? SprintAnchorDate { get; set; }

        /// <summary>スプリント 1 つの長さ（日数）。</summary>
        public int SprintLengthDays { get; set; } = 14;

        /// <summary>
        /// 差し込みの <c>@sprint</c> が参照する区切り。設定が揃っていなければ null。
        /// </summary>
        [JsonIgnore]
        public SprintSchedule? Sprint
            => SprintAnchorDate is { } anchor && SprintLengthDays >= 1
                ? new SprintSchedule(anchor, SprintLengthDays)
                : null;

        /// <summary>初回起動時に表示するサンプル設定を作る。</summary>
        public static AppSettings CreateDefault() => new()
        {
            Items =
            [
                new() { Name = "メールアドレス", Text = "example@example.com" },
                new() { Name = "電話番号", Text = "03-0000-0000" },
                new() { IsSeparator = true },
                // 名前に具体的な日付を書くと、初回起動した日と食い違って誤解を招くため書式で示す。
                // 実際の値はメニューのツールチップと設定画面のプレビューで確認できる
                new() { Category = "日付", Name = "今日 (yyyy/MM/dd)", Text = "{date}" },
                new() { Category = "日付", Name = "今日 (yyyyMMdd)", Text = "{date:yyyyMMdd}" },
                new() { Category = "日付", Name = "現在の日時", Text = "{datetime}" },
                new() { Category = "日付", Name = "明日", Text = "{date+1}" },
                new() { Category = "日付", Name = "今月末", Text = "{monthend}" },
                new()
                {
                    Name = "コピーした日付を yyyyMMdd に",
                    Text = "{date@clip:yyyyMMdd}",
                    ClipboardCondition = ClipboardMatchKind.Date,
                },
                new() { IsSeparator = true },
                new() { Category = "定型文", Name = "お礼", Text = "お世話になっております。ご対応ありがとうございました。" },
                new() { Category = "定型文", Name = "確認依頼", Text = "ご確認のほど、よろしくお願いいたします。" },
                // Markdown として載せると、Word や Slack へ貼ったときに見出しになる。
                // エディタへ貼れば書いたままの「# …」が入る
                new()
                {
                    Category = "定型文",
                    Name = "議事録の見出し",
                    Text = "# {date:yyyy/MM/dd} 定例ミーティング 議事録",
                    Format = ClipFormat.Markdown,
                },
                new()
                {
                    Category = "定型文",
                    Name = "議事録のひな形",
                    // 改行は CRLF にする。設定画面のテキストボックスが返すのも CRLF なので、
                    // 既定の項目だけ LF だと、編集して保存しただけで差分が出てしまう
                    Text = "# {date:yyyy/MM/dd} 定例ミーティング 議事録\r\n\r\n"
                        + "## 決定事項\r\n\r\n- \r\n\r\n## 宿題\r\n\r\n- \r\n",
                    Format = ClipFormat.Markdown,
                },
                new()
                {
                    Category = "定型文",
                    Name = "番号とタイトルを組み立てる",
                    Text = "[{input:番号}] {input:タイトル}",
                },
            ],
        };

        public AppSettings Clone() => new()
        {
            Version = Version,
            ShowCopyNotification = ShowCopyNotification,
            SequentialCaptureTrigger = SequentialCaptureTrigger,
            MenuHotKey = MenuHotKey,
            SwapCopyHotKey = SwapCopyHotKey,
            ActionStates = new(ActionStates ?? [], StringComparer.Ordinal),
            SprintAnchorDate = SprintAnchorDate,
            SprintLengthDays = SprintLengthDays,
            Items = [.. Items.Select(i => i.Clone())],
            Categories = [.. (Categories ?? []).Select(category => category.Clone())],
            RegularMenu = [.. (RegularMenu ?? []).Select(node => node.Clone())],
            ContextualMenu = [.. (ContextualMenu ?? []).Select(node => node.Clone())],
        };

        /// <summary>保存済みの指定があれば使い、なければアクション固有の既定値を返す。</summary>
        public bool IsActionVisible(string actionId, bool defaultEnabled)
            => ActionStates is not null && ActionStates.TryGetValue(actionId, out bool visible)
                ? visible
                : defaultEnabled;
    }
}
