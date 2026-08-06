namespace MyTaskTray.Services
{
    /// <summary>組み込みアクションの実行形態。</summary>
    internal enum TrayActionKind
    {
        /// <summary>1 回の選択で完了する処理。</summary>
        OneShot,

        /// <summary>開始後も状態を持ち、完了またはキャンセルまで続く処理。</summary>
        Session,

        /// <summary>クリップボードなど、現在の状況に合うときだけ前面へ出す処理。</summary>
        Contextual,
    }

    /// <summary>メニューを組み立てる時点でのアクションの利用可否。</summary>
    internal readonly record struct TrayActionAvailability(
        bool IsVisible,
        bool IsEnabled,
        string DisabledReason)
    {
        public static TrayActionAvailability Enabled { get; } = new(true, true, string.Empty);

        public static TrayActionAvailability Hidden { get; } = new(false, false, string.Empty);

        public static TrayActionAvailability Disabled(string reason)
            => new(true, false, reason ?? string.Empty);
    }

    /// <summary>
    /// アクションの表示条件と実行処理へ渡す、そのメニュー表示中だけの状況。
    /// クリップボードは必要になったときだけ 1 度読み、その後は同じ値を返す。
    /// </summary>
    internal sealed class TrayActionContext(
        Func<string> clipboard,
        ForegroundApp foregroundApp,
        ActionSessionManager sessions,
        Func<string, bool, bool> isActionVisible)
    {
        public string Clipboard => clipboard();

        public ForegroundApp ForegroundApp { get; } = foregroundApp;

        public ActionSessionManager Sessions { get; } = sessions;

        public bool IsActionVisible(TrayActionDefinition action)
            => isActionVisible(action.Id, action.DefaultEnabled);
    }

    /// <summary>
    /// 「作業ツール」へ載せる組み込みアクションの宣言。
    /// 新しい処理は、処理本体とこの定義を 1 件追加すればメニューへ参加できる。
    /// </summary>
    internal sealed record TrayActionDefinition(
        string Id,
        string Label,
        string ToolTip,
        string Group,
        string GroupLabel,
        int GroupOrder,
        int Order,
        char AccessKey,
        TrayActionKind Kind,
        bool DefaultEnabled,
        bool AllowDuringSession,
        Func<TrayActionContext, TrayActionAvailability> Evaluate,
        Action<TrayActionContext> Execute);

    /// <summary>登録された組み込みアクションを重複なく保持し、表示順に返す。</summary>
    internal sealed class TrayActionRegistry
    {
        private readonly Dictionary<string, TrayActionDefinition> _byId
            = new(StringComparer.Ordinal);

        public void Register(TrayActionDefinition action)
        {
            ArgumentNullException.ThrowIfNull(action);

            if (string.IsNullOrWhiteSpace(action.Id))
            {
                throw new ArgumentException("アクション ID を指定してください。", nameof(action));
            }

            if (string.IsNullOrWhiteSpace(action.Label)
                || string.IsNullOrWhiteSpace(action.Group)
                || string.IsNullOrWhiteSpace(action.GroupLabel))
            {
                throw new ArgumentException("表示名と表示用グループを指定してください。", nameof(action));
            }

            if (!char.IsAsciiLetterOrDigit(action.AccessKey))
            {
                throw new ArgumentException("アクセスキーは半角英数字で指定してください。", nameof(action));
            }

            if (_byId.ContainsKey(action.Id))
            {
                throw new InvalidOperationException($"アクション ID「{action.Id}」が重複しています。");
            }

            bool contextual = action.Kind == TrayActionKind.Contextual;
            if (_byId.Values.Any(existing =>
                (existing.Kind == TrayActionKind.Contextual) == contextual
                && char.ToUpperInvariant(existing.AccessKey)
                    == char.ToUpperInvariant(action.AccessKey)))
            {
                throw new InvalidOperationException(
                    $"同じメニュー内でアクセスキー「{action.AccessKey}」が重複しています。");
            }

            _byId.Add(action.Id, action);
        }

        public IReadOnlyList<(TrayActionDefinition Action, TrayActionAvailability Availability)> Evaluate(
            TrayActionContext context)
        {
            ArgumentNullException.ThrowIfNull(context);

            List<(TrayActionDefinition Action, TrayActionAvailability Availability)> evaluated = [];
            IEnumerable<TrayActionDefinition> ordered = _byId.Values
                .OrderBy(action => action.GroupOrder)
                .ThenBy(action => action.Order)
                .ThenBy(action => action.Label, StringComparer.CurrentCulture);

            foreach (TrayActionDefinition action in ordered)
            {
                if (!context.IsActionVisible(action))
                {
                    continue;
                }

                TrayActionAvailability availability;
                try
                {
                    availability = action.Evaluate(context);
                }
                catch (Exception)
                {
                    // 1 つの拡張アクションの判定失敗で、トレイメニュー全体を開けなくしない。
                    availability = TrayActionAvailability.Disabled("現在の状態を確認できませんでした");
                }

                if (availability.IsVisible)
                {
                    evaluated.Add((action, availability));
                }
            }

            return evaluated;
        }

        public IReadOnlyList<TrayActionDefinition> Definitions
            => [.. _byId.Values
                .OrderBy(action => action.GroupOrder)
                .ThenBy(action => action.Order)
                .ThenBy(action => action.Label, StringComparer.CurrentCulture)];
    }

    /// <summary>組み込みアクションと内部セッションで共有する安定した ID。</summary>
    internal static class TrayActionIds
    {
        public const string RemoveBlankLines = "remove-blank-lines";
        public const string JsonMinify = "json-minify";
        public const string JsonFormat = "json-format";
        public const string SequentialCopyPaste = "sequential-copy-paste";
        public const string MultipleInput = "multiple-input";
    }
}
