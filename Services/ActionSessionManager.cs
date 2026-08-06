namespace MyTaskTray.Services
{
    /// <summary>
    /// クリップボード監視などを行う一時セッションを、アプリ全体で同時に 1 つだけ管理する。
    /// 別の作業を暗黙に破棄せず、開始可否は呼び出し側へ返す。
    /// </summary>
    internal sealed class ActionSessionManager : IDisposable
    {
        private IDisposable? _current;

        public string? CurrentActionId { get; private set; }

        public string? CurrentDisplayName { get; private set; }

        public bool HasActiveSession => _current is not null;

        public bool TryStart(string actionId, string displayName, IDisposable session)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(actionId);
            ArgumentException.ThrowIfNullOrWhiteSpace(displayName);
            ArgumentNullException.ThrowIfNull(session);

            if (_current is not null)
            {
                return false;
            }

            CurrentActionId = actionId;
            CurrentDisplayName = displayName;
            _current = session;
            return true;
        }

        public T? Get<T>(string actionId) where T : class, IDisposable
            => string.Equals(CurrentActionId, actionId, StringComparison.Ordinal)
                ? _current as T
                : null;

        public bool IsCurrent(string actionId, IDisposable? session)
            => session is not null
                && string.Equals(CurrentActionId, actionId, StringComparison.Ordinal)
                && ReferenceEquals(_current, session);

        /// <summary>
        /// 自力で完了・破棄したセッションを管理対象から外す。
        /// セッション自体はここでは破棄しない。
        /// </summary>
        public bool Complete(string actionId, IDisposable? session)
        {
            if (!IsCurrent(actionId, session))
            {
                return false;
            }

            ClearCurrent();
            return true;
        }

        /// <summary>指定したセッションが現在実行中なら、管理対象から外して破棄する。</summary>
        public bool Cancel(string actionId, IDisposable? session)
        {
            if (!IsCurrent(actionId, session))
            {
                return false;
            }

            IDisposable current = _current!;
            ClearCurrent();
            current.Dispose();
            return true;
        }

        /// <summary>現在実行中のセッションを、種類にかかわらず破棄する。</summary>
        public bool CancelCurrent()
        {
            IDisposable? current = _current;
            if (current is null)
            {
                return false;
            }

            ClearCurrent();
            current.Dispose();
            return true;
        }

        private void ClearCurrent()
        {
            _current = null;
            CurrentActionId = null;
            CurrentDisplayName = null;
        }

        public void Dispose() => CancelCurrent();
    }
}
