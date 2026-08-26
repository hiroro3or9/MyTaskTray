using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using MyTaskTray.Models;

namespace MyTaskTray.Services
{
    /// <summary>スマートアクションの判定結果と、出力で使えるキャプチャ。</summary>
    internal sealed record ClipboardMatchResult(
        bool IsMatch,
        IReadOnlyDictionary<string, string> Captures)
    {
        public static ClipboardMatchResult NoMatch { get; } = new(
            false,
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));

        public static ClipboardMatchResult Matched(string value)
            => new(true, new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["value"] = value,
            });
    }

    /// <summary>
    /// 表示条件に合った「件」の並び。
    ///
    /// <para>
    /// 通常は 0 件か 1 件。項目の「複数行にも適用する」が有効なときだけ、
    /// クリップボードの行のうち条件に合ったものが順に並ぶ。
    /// </para>
    /// </summary>
    /// <param name="Rows">条件に合った件。書いた順（＝行の順）。</param>
    /// <param name="Truncated">
    /// 件数の上限または照合時間の予算に達し、残りを見ずに打ち切ったかどうか。
    /// 黙って切り捨てると「全部変換した」と誤解されるため、呼び出し側で必ず伝える。
    /// </param>
    internal sealed record ClipboardMatchRows(
        IReadOnlyList<ClipboardMatchResult> Rows,
        bool Truncated)
    {
        public static ClipboardMatchRows None { get; } = new([], false);

        public int Count => Rows.Count;

        /// <summary>1 件でも条件に合ったかどうか（＝メニューに表示するかどうか）。</summary>
        public bool IsMatch => Rows.Count > 0;
    }

    /// <summary>クリップボードの文字列が、スマートアクションの表示条件に合うか判定する。</summary>
    internal static partial class ClipboardMatcher
    {
        /// <summary>
        /// 1 つの項目の照合に使える<strong>合計</strong>時間。
        ///
        /// <para>
        /// これを 1 行あたりの上限にすると、行ごとに照合したときの最悪時間が行数倍になり
        /// （200 行で 40 秒）、メニューが固まる。表示するかどうかの判定は
        /// メニューを開くたびに走るため、上限は項目単位で持つ。
        /// 詳細は DESIGN_BULK_APPLY.md §5。
        /// </para>
        /// </summary>
        private static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(200);

        /// <summary>
        /// 残りの予算がこれを下回ったら打ち切る。
        /// <see cref="Regex"/> は 0 以下のタイムアウトを受け付けない。
        /// </summary>
        private static readonly TimeSpan MinimumSlice = TimeSpan.FromMilliseconds(1);

        /// <summary>
        /// 1 回のコピーで作る件数の上限。
        /// 貼り付け先を埋め尽くす事故を防ぐためのもので、超えた分は照合せずに打ち切る。
        /// </summary>
        public const int MaxBulkRows = 200;

        private static readonly char[] NewLineChars = ['\r', '\n'];

        [GeneratedRegex(
            @"^[^\s@]+@(?<domain>[^\s@]+\.[^\s@]+)$",
            RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
        private static partial Regex EmailRegex();

        [GeneratedRegex(
            @"^(?:[A-Za-z]:[\\/]|\\\\[^\\/\r\n]+[\\/][^\\/\r\n]+)",
            RegexOptions.CultureInvariant)]
        private static partial Regex WindowsPathRegex();

        /// <summary>クリップボード全体を 1 件として照合する。</summary>
        public static ClipboardMatchResult Match(ClipItem item, string clipboard)
            => MatchValue(item, clipboard.Trim(), RegexTimeout);

        /// <summary>
        /// 表示条件に合う件を並べて返す。
        ///
        /// <para>
        /// <see cref="ClipItem.UsesEachLine"/> が false なら、従来どおり
        /// クリップボード全体を 1 件として見る。
        /// </para>
        /// <para>
        /// true のときの切り方は<strong>条件の種類で変わる</strong>。
        /// 正規表現は「一致箇所がいくつあるか」を言えるので全体に繰り返し当て、
        /// それ以外（URL・日付・数値・メールなど「文字列全体がその形か」を見る条件）は
        /// 繰り返す先が無いので 1 行を 1 件として照合する。
        /// どちらも<strong>条件に合わなかったところは飛ばす</strong>
        /// （見出し行や空行が自然に落ちる）。詳細は DESIGN_BULK_APPLY.md §4-2。
        /// </para>
        /// </summary>
        public static ClipboardMatchRows MatchEach(ClipItem item, string clipboard)
        {
            if (!item.UsesEachLine)
            {
                ClipboardMatchResult single = Match(item, clipboard);
                return single.IsMatch ? new ClipboardMatchRows([single], false) : ClipboardMatchRows.None;
            }

            return item.ClipboardCondition == ClipboardMatchKind.Regex
                ? MatchEveryRegex(clipboard, item.ClipboardPattern)
                : MatchEveryLine(item, clipboard);
        }

        /// <summary>
        /// クリップボード全体に正規表現を繰り返し当て、一致した箇所を順に 1 件ずつ返す。
        ///
        /// <para>
        /// 行に切らないので、<c>(\w+)[\r\n]+(\w+)</c> のようにパターンへ改行を書けば
        /// <strong>1 件が複数行にまたがれる</strong>。番号付き・名前付きキャプチャも
        /// 1 行のときとまったく同じに使える。
        /// </para>
        /// <para>
        /// <see cref="RegexOptions.Multiline"/> を付け、<c>^</c> と <c>$</c> を
        /// 各行の先頭・末尾にする。付けないと、1 行用に書いた <c>^…$</c> の項目が
        /// このチェックを入れた瞬間に一致しなくなり、<strong>黙ってメニューから消える</strong>。
        /// </para>
        /// </summary>
        private static ClipboardMatchRows MatchEveryRegex(string clipboard, string pattern)
        {
            string value = NormalizeNewLines(clipboard.Trim());

            if (string.IsNullOrEmpty(value) || string.IsNullOrWhiteSpace(pattern))
            {
                return ClipboardMatchRows.None;
            }

            List<ClipboardMatchResult> rows = [];

            try
            {
                Regex regex = new(
                    pattern,
                    RegexOptions.CultureInvariant | RegexOptions.Multiline,
                    RegexTimeout);

                // 空文字に一致するパターンでも NextMatch() が 1 文字進めるため終わる。
                // 件数が膨らむだけなので、上限で受け止める
                for (Match match = regex.Match(value); match.Success; match = match.NextMatch())
                {
                    rows.Add(BuildRegexCaptures(regex, match, match.Value));

                    if (rows.Count >= MaxBulkRows)
                    {
                        return new ClipboardMatchRows(rows, true);
                    }
                }
            }
            catch (ArgumentException)
            {
                // 不正なパターンは、1 件として照合するときと同じく「一致しなかった」扱い
                return ClipboardMatchRows.None;
            }
            catch (RegexMatchTimeoutException)
            {
                // 途中まで取れているなら捨てず、打ち切ったことだけ伝える
                return rows.Count > 0 ? new ClipboardMatchRows(rows, true) : ClipboardMatchRows.None;
            }

            return rows.Count > 0 ? new ClipboardMatchRows(rows, false) : ClipboardMatchRows.None;
        }

        /// <summary>
        /// 1 行を 1 件として照合する。正規表現以外の条件で使う。
        /// </summary>
        private static ClipboardMatchRows MatchEveryLine(ClipItem item, string clipboard)
        {
            List<ClipboardMatchResult> rows = [];
            long started = Stopwatch.GetTimestamp();

            foreach (string line in SplitLines(clipboard))
            {
                if (rows.Count >= MaxBulkRows)
                {
                    return new ClipboardMatchRows(rows, true);
                }

                TimeSpan remaining = RegexTimeout - Stopwatch.GetElapsedTime(started);
                if (remaining < MinimumSlice)
                {
                    // 予算切れ。「タイムアウトは一致しなかったものとして扱う」という
                    // 既存の方針に合わせ、ここから先は見なかったことにする
                    return new ClipboardMatchRows(rows, true);
                }

                // 行の前後を Trim() しないのは、先頭まで落とすと
                // 「1 列目が空のタブ区切り」が 1 列ぶんずれてしまうため。
                // 行末の空白と \r だけ落とす
                ClipboardMatchResult result = MatchValue(item, line.TrimEnd(), remaining);
                if (result.IsMatch)
                {
                    rows.Add(result);
                }
            }

            return rows.Count > 0 ? new ClipboardMatchRows(rows, false) : ClipboardMatchRows.None;
        }

        /// <summary>
        /// 改行を <c>\n</c> へ揃える。
        ///
        /// <para>
        /// <strong>これが無いと <see cref="RegexOptions.Multiline"/> の <c>$</c> が効かない。</strong>
        /// <c>$</c> が一致するのは <c>\n</c> の直前だけで、<c>\r</c> の手前では一致しない。
        /// Windows のクリップボードは <c>\r\n</c> が普通なので、揃えずに照合すると
        /// <c>^…$</c> を書いた項目が「行末に見えない <c>\r</c> があるせいで」ほとんど一致しなくなる
        /// （4 行に <c>^(\w+)$</c> を当てて 1 件しか取れない、という壊れ方をする）。
        /// </para>
        /// <para>
        /// 揃えた結果はキャプチャにも入るため、複数行にまたがる <c>{match:0}</c> の改行は
        /// <c>\n</c> になる。件をつなぐときも <c>\n</c> なので、出力全体で揃う。
        /// </para>
        /// </summary>
        private static string NormalizeNewLines(string value)
            => value.Contains('\r')
                ? value.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n')
                : value;

        /// <summary>
        /// 改行で行に切る。CRLF と LF のどちらも区切りとして扱う。
        /// 途中の空行はそのまま返し（条件に合わないので呼び出し側で落ちる）、
        /// 末尾の改行のうしろは行として数えない。
        /// </summary>
        private static IEnumerable<string> SplitLines(string value)
        {
            int start = 0;
            while (start < value.Length)
            {
                int index = value.IndexOfAny(NewLineChars, start);
                if (index < 0)
                {
                    yield return value[start..];
                    yield break;
                }

                yield return value[start..index];
                start = value[index] == '\r' && index + 1 < value.Length && value[index + 1] == '\n'
                    ? index + 2
                    : index + 1;
            }
        }

        /// <param name="value">前後の整え方は呼び出し側が決める。ここでは触らない。</param>
        /// <param name="timeout">この 1 件の照合に使える時間。</param>
        private static ClipboardMatchResult MatchValue(ClipItem item, string value, TimeSpan timeout)
        {
            return item.ClipboardCondition switch
            {
                ClipboardMatchKind.Always => ClipboardMatchResult.Matched(value),
                ClipboardMatchKind.HasText => string.IsNullOrEmpty(value)
                    ? ClipboardMatchResult.NoMatch
                    : ClipboardMatchResult.Matched(value),
                ClipboardMatchKind.Date => MatchDate(value),
                ClipboardMatchKind.Url => MatchUrl(value),
                ClipboardMatchKind.Number => MatchNumber(value),
                ClipboardMatchKind.Json => MatchJson(value),
                ClipboardMatchKind.FilePath => MatchFilePath(value),
                ClipboardMatchKind.Email => MatchEmail(value),
                ClipboardMatchKind.Regex => MatchRegex(value, item.ClipboardPattern, timeout),
                _ => ClipboardMatchResult.NoMatch,
            };
        }

        /// <summary>保存前に利用者が入力した正規表現を検証する。</summary>
        public static bool TryValidateRegex(string? pattern, out string error)
        {
            if (string.IsNullOrWhiteSpace(pattern))
            {
                error = "正規表現を入力してください。";
                return false;
            }

            try
            {
                _ = new Regex(pattern, RegexOptions.CultureInvariant, RegexTimeout);
                error = string.Empty;
                return true;
            }
            catch (ArgumentException ex)
            {
                error = "正規表現が正しくありません。" + Environment.NewLine + ex.Message;
                return false;
            }
        }

        private static ClipboardMatchResult MatchDate(string value)
            => !string.IsNullOrEmpty(value) && TemplateEngine.CanParseClipboardDate(value)
                ? ClipboardMatchResult.Matched(value)
                : ClipboardMatchResult.NoMatch;

        private static ClipboardMatchResult MatchUrl(string value)
        {
            if (!Uri.TryCreate(value, UriKind.Absolute, out Uri? uri)
                || uri.Scheme is not ("http" or "https"))
            {
                return ClipboardMatchResult.NoMatch;
            }

            return MatchedWith(value,
                ("scheme", uri.Scheme),
                ("host", uri.Host),
                ("path", uri.AbsolutePath),
                ("query", uri.Query.TrimStart('?')));
        }

        private static ClipboardMatchResult MatchNumber(string value)
        {
            const NumberStyles styles = NumberStyles.Number | NumberStyles.AllowExponent;
            bool matched = decimal.TryParse(value, styles, CultureInfo.InvariantCulture, out _)
                || decimal.TryParse(value, styles, CultureInfo.CurrentCulture, out _);

            return matched
                ? MatchedWith(value, ("number", value))
                : ClipboardMatchResult.NoMatch;
        }

        private static ClipboardMatchResult MatchJson(string value)
        {
            return ClipboardTextActions.IsJsonObjectOrArray(value)
                ? ClipboardMatchResult.Matched(value)
                : ClipboardMatchResult.NoMatch;
        }

        private static ClipboardMatchResult MatchFilePath(string value)
        {
            if (string.IsNullOrEmpty(value) || !WindowsPathRegex().IsMatch(value))
            {
                return ClipboardMatchResult.NoMatch;
            }

            try
            {
                return MatchedWith(value,
                    ("name", Path.GetFileName(value)),
                    ("directory", Path.GetDirectoryName(value) ?? string.Empty),
                    ("extension", Path.GetExtension(value)));
            }
            catch (ArgumentException)
            {
                return ClipboardMatchResult.Matched(value);
            }
        }

        private static ClipboardMatchResult MatchEmail(string value)
        {
            Match match = EmailRegex().Match(value);
            if (!match.Success)
            {
                return ClipboardMatchResult.NoMatch;
            }

            int at = value.LastIndexOf('@');
            return MatchedWith(value,
                ("local", at > 0 ? value[..at] : string.Empty),
                ("domain", match.Groups["domain"].Value));
        }

        private static ClipboardMatchResult MatchRegex(string value, string pattern, TimeSpan timeout)
        {
            if (string.IsNullOrEmpty(value) || string.IsNullOrWhiteSpace(pattern))
            {
                return ClipboardMatchResult.NoMatch;
            }

            try
            {
                Regex regex = new(pattern, RegexOptions.CultureInvariant, timeout);
                Match match = regex.Match(value);

                // 1 件として見るときの {match:value} は、照合した文字列そのもの。
                // 一致した部分だけが欲しい場合は {match:0} で取れる
                return match.Success
                    ? BuildRegexCaptures(regex, match, value)
                    : ClipboardMatchResult.NoMatch;
            }
            catch (ArgumentException)
            {
                return ClipboardMatchResult.NoMatch;
            }
            catch (RegexMatchTimeoutException)
            {
                return ClipboardMatchResult.NoMatch;
            }
        }

        /// <summary>
        /// 正規表現の一致から <c>{match:…}</c> の値を組み立てる。
        /// 1 件として照合する場合と、全体へ繰り返し当てる場合とで同じ規則を使うために分けてある。
        /// </summary>
        /// <param name="value">
        /// <c>{match:value}</c> に入れる文字列。
        /// 1 件として見るときは照合した文字列全体、繰り返し当てるときは
        /// <strong>その一致箇所</strong>（＝件の単位）を渡す。
        /// </param>
        private static ClipboardMatchResult BuildRegexCaptures(Regex regex, Match match, string value)
        {
            Dictionary<string, string> captures = new(StringComparer.OrdinalIgnoreCase)
            {
                ["value"] = value,
            };

            for (int i = 0; i < match.Groups.Count; i++)
            {
                Group group = match.Groups[i];
                if (group.Success)
                {
                    captures[i.ToString(CultureInfo.InvariantCulture)] = group.Value;
                }
            }

            foreach (string groupName in regex.GetGroupNames())
            {
                Group group = match.Groups[groupName];
                if (group.Success)
                {
                    captures[groupName] = group.Value;
                }
            }

            return new ClipboardMatchResult(true, captures);
        }

        private static ClipboardMatchResult MatchedWith(
            string value, params (string Name, string Value)[] fields)
        {
            Dictionary<string, string> captures = new(StringComparer.OrdinalIgnoreCase)
            {
                ["value"] = value,
            };

            foreach ((string name, string fieldValue) in fields)
            {
                captures[name] = fieldValue;
            }

            return new ClipboardMatchResult(true, captures);
        }
    }
}
