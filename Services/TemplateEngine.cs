using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace MyTaskTray.Services
{
    /// <summary>
    /// 差し込み（プレースホルダー）の説明。設定画面の挿入パネルで使う。
    /// <paramref name="Group"/> は挿入パネルの見出し。
    /// </summary>
    public sealed record PlaceholderInfo(string Token, string Group, string Description);

    /// <summary>
    /// コピー文字列に含まれる <c>{...}</c> 形式の差し込みを展開する。
    ///
    /// 書式: <c>{名前[±数値[単位]][:書式]}</c>
    ///   例) {date}  {date:yyyyMMdd}  {date+1}  {date-1w:M月d日}  {seq:0000}  {guid}
    /// <c>{{</c> と <c>}}</c> はそれぞれ <c>{</c> <c>}</c> のエスケープ。
    /// 解釈できない差し込みは、書いたままの文字列を残す。
    /// </summary>
    public static class TemplateEngine
    {
        private const string DefaultDateFormat = "yyyy/MM/dd";
        private const string DefaultTimeFormat = "HH:mm";
        private const string DefaultDateTimeFormat = "yyyy/MM/dd HH:mm";

        // {{ / }} のエスケープ、または { ... } の差し込み
        private static readonly Regex TokenRegex = new(
            @"\{\{|\}\}|\{(?<inner>[^{}]*)\}",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

        // 名前 + 任意のオフセット + 任意の書式
        private static readonly Regex InnerRegex = new(
            @"^(?<name>[A-Za-z]+)(?:(?<sign>[+\-])(?<num>\d+)(?<unit>[A-Za-z]*))?(?::(?<fmt>.*))?$",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

        private static readonly Regex SequenceRegex = new(
            @"\{seq(?:[+\-]\d+[A-Za-z]*)?(?::[^{}]*)?\}",
            RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        /// <summary>設定画面の「差し込みを挿入」で提示する一覧（並び順がパネルの表示順になる）。</summary>
        public static IReadOnlyList<PlaceholderInfo> Placeholders { get; } = new List<PlaceholderInfo>
        {
            new("{date}", "日付", "今日の日付"),
            new("{date:yyyyMMdd}", "日付", "書式を指定した日付"),
            new("{date:yyyy年M月d日}", "日付", "和文の日付"),
            new("{date+1}", "日付", "明日（-1 で昨日）"),
            new("{date+1w}", "日付", "1週間後（単位 d 日 / w 週 / mo 月 / y 年）"),
            new("{time}", "時刻", "現在の時刻"),
            new("{time:HH:mm:ss}", "時刻", "秒まで含む時刻"),
            new("{time+30}", "時刻", "30分後（単位 h 時 / mi 分 / s 秒）"),
            new("{datetime}", "時刻", "日付と時刻"),
            new("{monthstart}", "月・週", "今月の初日（+1 で翌月）"),
            new("{monthend}", "月・週", "今月の末日（+1 で翌月）"),
            new("{weekstart}", "月・週", "今週の月曜日（+1 で翌週）"),
            new("{weekend}", "月・週", "今週の日曜日"),
            new("{seq}", "連番・その他", "連番。コピーするたびに増える"),
            new("{seq:0000}", "連番・その他", "桁を揃えた連番"),
            new("{guid}", "連番・その他", "GUID（小文字・ハイフンあり）"),
            new("{guid:N}", "連番・その他", "GUID（ハイフンなし / B 波かっこ / U 大文字）"),
            new("{random}", "連番・その他", "1〜100 の乱数"),
            new("{random:1-6}", "連番・その他", "範囲を指定した乱数"),
            new("{{", "連番・その他", "波かっこ { そのもの（}} なら }）"),
        };

        /// <summary>差し込みを展開した文字列を返す。</summary>
        public static string Expand(string template, DateTime now, int sequenceValue)
        {
            if (string.IsNullOrEmpty(template))
            {
                return string.Empty;
            }

            return TokenRegex.Replace(template, match =>
            {
                if (match.Value == "{{")
                {
                    return "{";
                }

                if (match.Value == "}}")
                {
                    return "}";
                }

                return ExpandToken(match.Groups["inner"].Value, match.Value, now, sequenceValue);
            });
        }

        /// <summary>連番の差し込みを含むかどうか。含む場合、コピー後にカウンターを進める。</summary>
        public static bool ContainsSequence(string template)
            => !string.IsNullOrEmpty(template) && SequenceRegex.IsMatch(template);

        private static string ExpandToken(string inner, string original, DateTime now, int sequenceValue)
        {
            Match m = InnerRegex.Match(inner.Trim());
            if (!m.Success)
            {
                return original;
            }

            string name = m.Groups["name"].Value.ToLowerInvariant();
            string format = m.Groups["fmt"].Success ? m.Groups["fmt"].Value : string.Empty;

            int offset = 0;
            string unit = string.Empty;
            if (m.Groups["num"].Success)
            {
                if (!int.TryParse(m.Groups["num"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out offset))
                {
                    return original;
                }

                if (m.Groups["sign"].Value == "-")
                {
                    offset = -offset;
                }

                unit = m.Groups["unit"].Value.ToLowerInvariant();
            }

            try
            {
                switch (name)
                {
                    case "date":
                        return FormatDate(Shift(now, offset, unit, "d"), format, DefaultDateFormat);

                    case "time":
                        return FormatDate(Shift(now, offset, unit, "mi"), format, DefaultTimeFormat);

                    case "datetime":
                    case "now":
                        return FormatDate(Shift(now, offset, unit, "d"), format, DefaultDateTimeFormat);

                    case "monthstart":
                        return FormatDate(MonthStart(Shift(now, offset, unit, "mo")), format, DefaultDateFormat);

                    case "monthend":
                        return FormatDate(MonthEnd(Shift(now, offset, unit, "mo")), format, DefaultDateFormat);

                    case "weekstart":
                        return FormatDate(WeekStart(Shift(now, offset, unit, "w")), format, DefaultDateFormat);

                    case "weekend":
                        return FormatDate(WeekStart(Shift(now, offset, unit, "w")).AddDays(6), format, DefaultDateFormat);

                    case "seq":
                        return FormatSequence(sequenceValue, format);

                    case "guid":
                        return FormatGuid(format);

                    case "random":
                        return FormatRandom(format);

                    default:
                        return original;
                }
            }
            catch (Exception)
            {
                // 書式が不正な場合などは、書いたままを残して気付けるようにする
                return original;
            }
        }

        /// <summary>オフセットを適用する。単位が省略された場合は defaultUnit を使う。</summary>
        private static DateTime Shift(DateTime value, int offset, string unit, string defaultUnit)
        {
            if (offset == 0)
            {
                return value;
            }

            string u = string.IsNullOrEmpty(unit) ? defaultUnit : unit;
            return u switch
            {
                "d" => value.AddDays(offset),
                "w" => value.AddDays(offset * 7),
                "mo" => value.AddMonths(offset),
                "y" => value.AddYears(offset),
                "h" => value.AddHours(offset),
                "mi" => value.AddMinutes(offset),
                "s" => value.AddSeconds(offset),
                _ => value,
            };
        }

        private static DateTime MonthStart(DateTime value) => new(value.Year, value.Month, 1, 0, 0, 0, value.Kind);

        private static DateTime MonthEnd(DateTime value)
            => new(value.Year, value.Month, DateTime.DaysInMonth(value.Year, value.Month), 0, 0, 0, value.Kind);

        /// <summary>週の始まり（月曜）を返す。</summary>
        private static DateTime WeekStart(DateTime value)
        {
            int diff = ((int)value.DayOfWeek + 6) % 7;
            return value.Date.AddDays(-diff);
        }

        private static string FormatDate(DateTime value, string format, string defaultFormat)
        {
            string f = string.IsNullOrEmpty(format) ? defaultFormat : format;

            // 1 文字の書式は標準書式指定子と解釈されてしまうため、必ずカスタム書式として扱う
            if (f.Length == 1)
            {
                f = "%" + f;
            }

            return value.ToString(f, CultureInfo.CurrentCulture);
        }

        private static string FormatSequence(int value, string format)
            => string.IsNullOrEmpty(format)
                ? value.ToString(CultureInfo.InvariantCulture)
                : value.ToString(format, CultureInfo.InvariantCulture);

        private static string FormatGuid(string format)
        {
            Guid guid = Guid.NewGuid();

            if (string.IsNullOrEmpty(format))
            {
                return guid.ToString("D");
            }

            if (format.Equals("U", StringComparison.OrdinalIgnoreCase))
            {
                return guid.ToString("D").ToUpperInvariant();
            }

            return guid.ToString(format);
        }

        private static string FormatRandom(string format)
        {
            int min = 1;
            int max = 100;

            if (!string.IsNullOrEmpty(format))
            {
                string[] parts = format.Split('-', 2);
                if (parts.Length != 2
                    || !int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out min)
                    || !int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out max))
                {
                    throw new FormatException("乱数の範囲を解釈できません。");
                }
            }

            if (min > max)
            {
                (min, max) = (max, min);
            }

            return Random.Shared.Next(min, max == int.MaxValue ? max : max + 1)
                .ToString(CultureInfo.InvariantCulture);
        }

        /// <summary>プレビュー用に、改行を含む展開結果を 1 行の説明に整える。</summary>
        public static string ToSingleLine(string value, int maxLength = 300)
        {
            StringBuilder sb = new(value.Length);
            foreach (char c in value)
            {
                sb.Append(c switch
                {
                    '\r' => string.Empty,
                    '\n' => "⏎ ",
                    '\t' => "    ",
                    _ => c.ToString(),
                });
            }

            string oneLine = sb.ToString();
            return oneLine.Length <= maxLength ? oneLine : oneLine[..maxLength] + "…";
        }
    }
}
