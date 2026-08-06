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

    /// <summary>クリップボードの文字列が、スマートアクションの表示条件に合うか判定する。</summary>
    internal static partial class ClipboardMatcher
    {
        private static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(200);

        [GeneratedRegex(
            @"^[^\s@]+@(?<domain>[^\s@]+\.[^\s@]+)$",
            RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
        private static partial Regex EmailRegex();

        [GeneratedRegex(
            @"^(?:[A-Za-z]:[\\/]|\\\\[^\\/\r\n]+[\\/][^\\/\r\n]+)",
            RegexOptions.CultureInvariant)]
        private static partial Regex WindowsPathRegex();

        public static ClipboardMatchResult Match(ClipItem item, string clipboard)
        {
            string value = clipboard.Trim();

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
                ClipboardMatchKind.Regex => MatchRegex(value, item.ClipboardPattern),
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

        private static ClipboardMatchResult MatchRegex(string value, string pattern)
        {
            if (string.IsNullOrEmpty(value) || string.IsNullOrWhiteSpace(pattern))
            {
                return ClipboardMatchResult.NoMatch;
            }

            try
            {
                Regex regex = new(pattern, RegexOptions.CultureInvariant, RegexTimeout);
                Match match = regex.Match(value);
                if (!match.Success)
                {
                    return ClipboardMatchResult.NoMatch;
                }

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
            catch (ArgumentException)
            {
                return ClipboardMatchResult.NoMatch;
            }
            catch (RegexMatchTimeoutException)
            {
                return ClipboardMatchResult.NoMatch;
            }
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
