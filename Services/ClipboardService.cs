using System.Threading;

namespace MyTaskTray.Services
{
    /// <summary>
    /// クリップボードへのコピーを行う。
    /// 他プロセスがクリップボードをロックしている場合があるため、失敗時は少し待って再試行する。
    /// </summary>
    public static class ClipboardService
    {
        private const int MaxAttempts = 5;
        private const int RetryDelayMs = 80;

        /// <summary>指定した文字列をクリップボードにコピーする。成功したら true。</summary>
        public static bool TryCopy(string text)
        {
            // Clipboard.SetText は空文字列で例外を投げるため、空の場合はクリアする
            if (string.IsNullOrEmpty(text))
            {
                return TryRun(System.Windows.Clipboard.Clear);
            }

            return TryRun(() => System.Windows.Clipboard.SetText(text));
        }

        private static bool TryRun(Action action)
        {
            for (int attempt = 1; attempt <= MaxAttempts; attempt++)
            {
                try
                {
                    action();
                    return true;
                }
                catch (Exception)
                {
                    if (attempt == MaxAttempts)
                    {
                        return false;
                    }

                    Thread.Sleep(RetryDelayMs);
                }
            }

            return false;
        }
    }
}
