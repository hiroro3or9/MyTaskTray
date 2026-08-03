using System.Text.RegularExpressions;
using MyTaskTray.Models;

namespace MyTaskTray.Services
{
    /// <summary>
    /// 前面アプリによる項目の絞り込みを判定する。
    ///
    /// <para>
    /// スマートアクション（クリップボードの中身）とは独立した条件で、両方を満たしたときだけ表示する。
    /// こちらは通常の項目にも掛かるため、<strong>判定できない場合は表示する側に倒す</strong>。
    /// 「あるはずの項目が出てこない」ほうが、余分に出るより実害が大きい。
    /// </para>
    /// </summary>
    internal static class AppContextMatcher
    {
        /// <summary>タイトルの照合でメニューを止めないための打ち切り時間。</summary>
        private static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(200);

        /// <summary>プロセス名の区切り。日本語入力のままでも書けるよう読点も受ける。</summary>
        private static readonly char[] ProcessSeparators = [',', '、', ';'];

        /// <summary>この項目を、いま前面にあるアプリのもとで表示してよいか。</summary>
        public static bool Matches(ClipItem item, ForegroundApp app)
        {
            if (!item.HasAppCondition)
            {
                return true;
            }

            // 前面が取れない（前面ウィンドウが無い、プロセス名を読めない、自分自身が前面）場合は
            // 判定材料が無い。ここで隠すと原因の分からない「項目が消えた」になるため表示する
            if (!app.IsKnown)
            {
                return true;
            }

            return MatchesProcess(item.AppProcess, app.ProcessName)
                && MatchesTitle(item.AppTitlePattern, app.Title);
        }

        /// <summary>
        /// カンマ区切りの実行ファイル名のどれかに一致するか。
        /// <c>.exe</c> は省略でき、大文字小文字は区別しない。
        /// </summary>
        public static bool MatchesProcess(string? specification, string processName)
        {
            IReadOnlyList<string> names = SplitProcessNames(specification);
            if (names.Count == 0)
            {
                return true;
            }

            string actual = TrimExecutableSuffix(processName);
            foreach (string name in names)
            {
                if (string.Equals(name, actual, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>入力欄に書かれた実行ファイル名を、比較できる形に分解する。</summary>
        public static IReadOnlyList<string> SplitProcessNames(string? specification)
        {
            if (string.IsNullOrWhiteSpace(specification))
            {
                return [];
            }

            List<string> names = [];
            foreach (string part in specification.Split(ProcessSeparators, StringSplitOptions.RemoveEmptyEntries))
            {
                string name = TrimExecutableSuffix(part.Trim());
                if (name.Length > 0)
                {
                    names.Add(name);
                }
            }

            return names;
        }

        /// <summary>
        /// 保存前にタイトルの正規表現を検証する。空欄は「タイトルを見ない」という有効な状態。
        /// </summary>
        public static bool TryValidateTitlePattern(string? pattern, out string error)
        {
            if (string.IsNullOrWhiteSpace(pattern))
            {
                error = string.Empty;
                return true;
            }

            try
            {
                _ = new Regex(pattern, RegexOptions.CultureInvariant, RegexTimeout);
                error = string.Empty;
                return true;
            }
            catch (ArgumentException ex)
            {
                error = "ウィンドウタイトルの正規表現が正しくありません。" + Environment.NewLine + ex.Message;
                return false;
            }
        }

        private static bool MatchesTitle(string? pattern, string title)
        {
            if (string.IsNullOrWhiteSpace(pattern))
            {
                return true;
            }

            try
            {
                return Regex.IsMatch(title, pattern, RegexOptions.CultureInvariant, RegexTimeout);
            }
            catch (RegexMatchTimeoutException)
            {
                // 打ち切った場合は判定できていない。スマートアクションでは「一致しなかった」と
                // 扱っているが、こちらは通常の項目が消えるため表示する側に倒す
                return true;
            }
            catch (ArgumentException)
            {
                // 設定ファイルを手で編集した場合など、保存時の検証を通っていない式。
                // 誤りに気付けるよう、隠さずに表示する
                return true;
            }
        }

        private static string TrimExecutableSuffix(string name)
            => name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                ? name[..^4]
                : name;
    }
}
