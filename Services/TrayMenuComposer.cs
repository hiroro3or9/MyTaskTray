namespace MyTaskTray.Services
{
    /// <summary>現在の状況から選ばれた、組み込みアクションのメニュー配置。</summary>
    internal sealed record TrayMenuComposition(
        IReadOnlyList<(TrayActionDefinition Action, TrayActionAvailability Availability)> ContextualActions,
        IReadOnlyList<(TrayActionDefinition Action, TrayActionAvailability Availability)> WorkTools);

    /// <summary>
    /// アクション定義の実行条件を評価し、コンテキスト欄と「作業ツール」へ振り分ける。
    /// WinForms の項目生成は行わず、表示部品と判断ロジックを分離する。
    /// </summary>
    internal sealed class TrayMenuComposer(TrayActionRegistry actions)
    {
        public IReadOnlyList<TrayActionDefinition> Definitions => actions.Definitions;

        public TrayMenuComposition Compose(TrayActionContext context)
        {
            IReadOnlyList<(TrayActionDefinition Action, TrayActionAvailability Availability)> evaluated
                = actions.Evaluate(context);

            List<(TrayActionDefinition Action, TrayActionAvailability Availability)> available = [];
            foreach ((TrayActionDefinition action, TrayActionAvailability availability) in evaluated)
            {
                TrayActionAvailability effective = availability;
                if (context.Sessions.HasActiveSession)
                {
                    // 実行中のセッション自身は先頭欄へ昇格しているので、通常位置には重ねて出さない。
                    if (action.Kind == TrayActionKind.Session
                        && string.Equals(
                            action.Id,
                            context.Sessions.CurrentActionId,
                            StringComparison.Ordinal))
                    {
                        continue;
                    }

                    if (!action.AllowDuringSession && effective.IsEnabled)
                    {
                        string running = context.Sessions.CurrentDisplayName ?? "別の作業モード";
                        effective = TrayActionAvailability.Disabled(
                            $"「{running}」を実行中のため使用できません");
                    }
                }

                available.Add((action, effective));
            }

            return new TrayMenuComposition(
                ContextualActions:
                [.. available.Where(entry => entry.Action.Kind == TrayActionKind.Contextual)],
                WorkTools:
                [.. available.Where(entry => entry.Action.Kind != TrayActionKind.Contextual)]);
        }
    }
}
