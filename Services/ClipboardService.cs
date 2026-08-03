using System.Threading;

namespace MyTaskTray.Services
{
    /// <summary>
    /// クリップボードの読み書きを行う。
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

        /// <summary>
        /// クリップボードの文字列を読み取る。
        /// 文字列が入っていない場合や読み取れなかった場合は空文字列を返す。
        /// <c>{clip}</c> の差し込みで使う。
        /// </summary>
        public static string GetText()
        {
            string result = string.Empty;

            TryRun(() =>
            {
                if (System.Windows.Clipboard.ContainsText())
                {
                    result = System.Windows.Clipboard.GetText() ?? string.Empty;
                }
            });

            return result;
        }

        /// <summary>
        /// クリップボードに文字列が入っていそうかどうか。
        ///
        /// <para>
        /// 中身は読まないので <see cref="GetText"/> より軽く、他アプリのコピー操作を妨げにくい。
        /// メニューの項目を有効にするかどうかの判定に使う。
        /// 判定できなかった場合は true を返す。押せなくして「なぜか使えない」となるより、
        /// 押した結果を通知で伝えるほうが分かりやすい。
        /// </para>
        /// </summary>
        public static bool HasText()
        {
            try
            {
                return System.Windows.Clipboard.ContainsText();
            }
            catch (Exception)
            {
                return true;
            }
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
