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
    ///   例) {date}  {date:yyyyMMdd}  {date+1}  {date-1w:M月d日}  {seq:0000}  {seq+1}  {guid}
    /// 計算式: <c>{calc:式[|書式]}</c>（<c>{=式}</c> と書いてもよい）
    ///   例) {calc:(1000+200)*1.1}  {calc:1000*8%|#,##0}  {calc:{seq}*100}
    /// クリップボード: <c>{clip[:書式]}</c>
    ///   例) {clip}  {clip:digits}  {clip:/ID-(\d+)/}
    /// <c>{{</c> と <c>}}</c> はそれぞれ <c>{</c> <c>}</c> のエスケープ。
    /// 解釈できない差し込み（未知の名前・単位・書式、オフセットを付けられない差し込みなど）は、
    /// 書いたままの文字列を残してユーザーが誤りに気付けるようにする。
    /// </summary>
    public static partial class TemplateEngine
    {
        private const string DefaultDateFormat = "yyyy/MM/dd";
        private const string DefaultTimeFormat = "HH:mm";
        private const string DefaultDateTimeFormat = "yyyy/MM/dd HH:mm";

        /// <summary>計算式の中の差し込みを展開する際の入れ子の上限。</summary>
        private const int MaxDepth = 8;

        /// <summary>
        /// <c>{clip:…}</c> に書かれた正規表現の実行時間の上限。
        /// 利用者が自由に書けるため、組み合わせによっては照合が終わらなくなることがある。
        /// </summary>
        private static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(200);

        // 名前 + 任意のオフセット + 任意の書式。
        // GeneratedRegex はコンパイル時に実装を生成するため、RegexOptions.Compiled は不要。
        [GeneratedRegex(
            @"^(?<name>[A-Za-z]+)(?:(?<sign>[+\-])(?<num>\d+)(?<unit>[A-Za-z]*))?(?::(?<fmt>.*))?$",
            RegexOptions.CultureInvariant)]
        private static partial Regex InnerRegex();

        /// <summary>設定画面の「差し込みを挿入」で提示する一覧（並び順がパネルの表示順になる）。</summary>
        public static IReadOnlyList<PlaceholderInfo> Placeholders { get; } =
        [
            new("{clip}", "クリップボード", "いまコピーしてある文字列（前後の空白は取り除く）"),
            new("{clip:digits}", "クリップボード", "コピー済みの文字列から最初の数字だけを取り出す"),
            new("{clip:/ID-(\\d+)/}", "クリップボード", "正規表現で取り出す（かっこがあればその中身）"),
            new("{clip:line}", "クリップボード", "1 行目だけを取り出す"),
            new("{clip:upper}", "クリップボード", "大文字にする（lower で小文字）"),
            new("{clip:raw}", "クリップボード", "空白や改行も含めてそのまま"),
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
            new("{calc:(1000+200)*1.1}", "計算", "式を計算する（+ - * / ^ とかっこ）"),
            new("{calc:1000*8%}", "計算", "パーセント。8% は 0.08 として扱う"),
            new("{calc:1000*1.1|#,##0}", "計算", "| のうしろは数値の書式（#,##0 / 0.00 など）"),
            new("{calc:round(1000/3,2)}", "計算", "四捨五入。floor / ceil / trunc も同様"),
            new("{calc:max(1,2,3)}", "計算", "関数: min max sum avg abs sqrt pow mod sign log exp"),
            new("{calc:{seq}*100}", "計算", "式の中で他の差し込みを参照できる"),
            new("{=1+2}", "計算", "{calc:…} の短い書き方"),
            new("{year}", "日付の数値", "今年（4 桁の数値。+1 で翌年）"),
            new("{month}", "日付の数値", "今月（1〜12。{month:00} で 2 桁）"),
            new("{day}", "日付の数値", "今日の日（1〜31）"),
            new("{dow}", "日付の数値", "曜日番号（月曜=1 〜 日曜=7）"),
            new("{week}", "日付の数値", "ISO の週番号"),
            new("{doy}", "日付の数値", "元日からの通日"),
            new("{daysinmonth}", "日付の数値", "今月の日数"),
            new("{daysuntil:2026-12-31}", "日付の数値", "指定日までの日数（過ぎていれば負の数）"),
            new("{seq}", "連番・その他", "連番。コピーするたびに増える"),
            new("{seq:0000}", "連番・その他", "桁を揃えた連番"),
            new("{seq+1}", "連番・その他", "次の番号 + 1（範囲を書くときなど）"),
            new("{guid}", "連番・その他", "GUID（小文字・ハイフンあり）"),
            new("{guid:N}", "連番・その他", "GUID（ハイフンなし / B 波かっこ / U 大文字）"),
            new("{random}", "連番・その他", "1〜100 の乱数"),
            new("{random:1-6}", "連番・その他", "範囲を指定した乱数"),
            new("{{", "連番・その他", "波かっこ { そのもの（}} なら }）"),
        ];

        /// <summary>
        /// 1 回の展開のあいだ持ち回る値。
        /// クリップボードの読み取りは <c>{clip}</c> が実際に現れたときだけ行い、
        /// 同じ展開の中では何度使っても同じ値になるように覚えておく。
        /// </summary>
        private sealed class ExpandContext(DateTime now, int sequenceValue, Func<string>? clipboard)
        {
            private string? _clipboard;

            public DateTime Now { get; } = now;

            public int SequenceValue { get; } = sequenceValue;

            /// <summary>呼び出し元がクリップボードの読み取り手段を渡しているかどうか。</summary>
            public bool HasClipboard { get; } = clipboard is not null;

            public string Clipboard => _clipboard ??= clipboard?.Invoke() ?? string.Empty;
        }

        /// <summary>差し込みを展開した文字列を返す。</summary>
        public static string Expand(string template, DateTime now, int sequenceValue)
            => Expand(template, now, sequenceValue, null);

        /// <summary>差し込みを展開した文字列を返す。</summary>
        /// <param name="clipboard">
        /// <c>{clip}</c> が現れたときに呼ばれ、クリップボードの文字列を返す関数。
        /// null の場合、<c>{clip}</c> は書いたままの文字列として残す。
        /// </param>
        public static string Expand(string template, DateTime now, int sequenceValue, Func<string>? clipboard)
            => Expand(template, new ExpandContext(now, sequenceValue, clipboard), 0, false);

        /// <summary>
        /// 差し込みを展開する。計算式は中に別の差し込みを書けるため、
        /// 正規表現ではなく前から 1 文字ずつ読み、かっこの対応を数えて切り出す。
        /// </summary>
        /// <param name="numericOnly">
        /// <c>{calc:…}</c> の式の中を展開しているとき true。
        /// 数値にならない差し込み（<c>{date}</c> など）は誤った計算結果になるため、
        /// このとき展開結果が数値かどうかを確かめ、数値でなければ例外にする。
        /// </param>
        private static string Expand(string template, ExpandContext context, int depth, bool numericOnly)
        {
            if (string.IsNullOrEmpty(template))
            {
                return string.Empty;
            }

            if (depth > MaxDepth)
            {
                // 差し込みが入れ子で循環している場合の保険
                return template;
            }

            StringBuilder sb = new(template.Length);
            int i = 0;

            while (i < template.Length)
            {
                char c = template[i];

                if (c == '{' || c == '}')
                {
                    // {{ }} はエスケープ
                    if (i + 1 < template.Length && template[i + 1] == c)
                    {
                        sb.Append(c);
                        i += 2;
                        continue;
                    }

                    int close = c == '{' ? FindClosingBrace(template, i) : -1;
                    if (close < 0)
                    {
                        sb.Append(c);
                        i++;
                        continue;
                    }

                    string inner = template[(i + 1)..close];
                    string token = template[i..(close + 1)];
                    string expanded = ExpandToken(inner, token, context, depth);

                    // 式の中で {date} のような文字列の差し込みを使うと
                    // 2026/07/30 が「2026 ÷ 7 ÷ 30」として計算されてしまうため、ここで弾く
                    if (numericOnly && !LooksLikeNumber(expanded))
                    {
                        throw new FormatException($"{token} は数値にならないため式の中では使えません。");
                    }

                    sb.Append(expanded);
                    i = close + 1;
                    continue;
                }

                sb.Append(c);
                i++;
            }

            return sb.ToString();
        }

        /// <summary>
        /// <paramref name="open"/> の <c>{</c> に対応する <c>}</c> の位置を返す。
        /// 見つからなければ -1。
        /// </summary>
        private static int FindClosingBrace(string text, int open)
        {
            int depth = 0;

            for (int i = open; i < text.Length; i++)
            {
                char c = text[i];

                if (c != '{' && c != '}')
                {
                    continue;
                }

                // エスケープはまとめて読み飛ばす
                if (i + 1 < text.Length && text[i + 1] == c)
                {
                    i++;
                    continue;
                }

                depth += c == '{' ? 1 : -1;
                if (depth == 0)
                {
                    return i;
                }
            }

            return -1;
        }

        /// <summary>
        /// 連番の差し込みを含むかどうか。含む場合、コピー後にカウンターを進める。
        /// <c>{{seq}}</c> のようにエスケープした場合はリテラルの文字列になるため、含まないと判定する。
        /// </summary>
        public static bool ContainsSequence(string template) => ContainsToken(template, "seq", 0);

        /// <summary>
        /// クリップボードの差し込みを含むかどうか。
        /// 含む場合だけクリップボードを読みに行き、空のときに注意を促す。
        /// </summary>
        public static bool ContainsClipboard(string template) => ContainsToken(template, "clip", 0);

        /// <summary>
        /// 指定した名前の差し込みを含むかどうか。計算式の中に書かれている場合も含むと判定する。
        /// </summary>
        private static bool ContainsToken(string template, string name, int depth)
        {
            if (string.IsNullOrEmpty(template) || depth > MaxDepth)
            {
                return false;
            }

            int i = 0;

            while (i < template.Length)
            {
                char c = template[i];

                if (c != '{' && c != '}')
                {
                    i++;
                    continue;
                }

                // {{ }} はエスケープなので、中身は差し込みとして扱わない
                if (i + 1 < template.Length && template[i + 1] == c)
                {
                    i += 2;
                    continue;
                }

                int close = c == '{' ? FindClosingBrace(template, i) : -1;
                if (close < 0)
                {
                    i++;
                    continue;
                }

                string inner = template[(i + 1)..close];

                // {seq} 自身か、{calc:{seq}*100} のように式の中で使われている場合
                if (IsToken(inner, name) || ContainsToken(inner, name, depth + 1))
                {
                    return true;
                }

                i = close + 1;
            }

            return false;
        }

        /// <summary>差し込みの中身が指定した名前かどうか（書式やオフセット付きも含む）。</summary>
        private static bool IsToken(string inner, string name)
        {
            Match m = InnerRegex().Match(inner.Trim());
            return m.Success && m.Groups["name"].Value.Equals(name, StringComparison.OrdinalIgnoreCase);
        }

        private static string ExpandToken(string inner, string original, ExpandContext context, int depth)
        {
            string trimmed = inner.Trim();

            // 計算式は式の中に差し込みを書けるので、名前の解析より先に振り分ける
            if (trimmed.StartsWith('='))
            {
                return ExpandCalc(trimmed[1..], original, context, depth);
            }

            if (trimmed.StartsWith("calc:", StringComparison.OrdinalIgnoreCase))
            {
                return ExpandCalc(trimmed[5..], original, context, depth);
            }

            Match m = InnerRegex().Match(trimmed);
            if (!m.Success)
            {
                return original;
            }

            DateTime now = context.Now;
            int sequenceValue = context.SequenceValue;
            string name = m.Groups["name"].Value.ToLowerInvariant();
            string format = m.Groups["fmt"].Success ? m.Groups["fmt"].Value : string.Empty;

            bool hasOffset = m.Groups["num"].Success;
            int offset = 0;
            string unit = string.Empty;
            if (hasOffset)
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
                        // {seq+1} は「次の番号 + 1」。単位は意味を持たないため誤りとして扱う
                        if (!string.IsNullOrEmpty(unit))
                        {
                            throw new FormatException("{seq} に単位は指定できません。");
                        }

                        return FormatSequence(unchecked(sequenceValue + offset), format);

                    case "clip":
                        RejectOffset(name, hasOffset);

                        // 呼び出し元がクリップボードを読めない場面（テストなど）では
                        // 中途半端な文字列を返さず、書いたままを残す
                        if (!context.HasClipboard)
                        {
                            return original;
                        }

                        return FormatClipboard(context.Clipboard, format, original);

                    case "guid":
                        RejectOffset(name, hasOffset);
                        return FormatGuid(format);

                    case "random":
                        RejectOffset(name, hasOffset);
                        return FormatRandom(format);

                    case "year":
                        return FormatNumber(Shift(now, offset, unit, "y").Year, format);

                    case "month":
                        return FormatNumber(Shift(now, offset, unit, "mo").Month, format);

                    case "day":
                        return FormatNumber(Shift(now, offset, unit, "d").Day, format);

                    case "hour":
                        return FormatNumber(Shift(now, offset, unit, "h").Hour, format);

                    case "minute":
                        return FormatNumber(Shift(now, offset, unit, "mi").Minute, format);

                    case "second":
                        return FormatNumber(Shift(now, offset, unit, "s").Second, format);

                    case "dow":
                        return FormatNumber(((int)Shift(now, offset, unit, "d").DayOfWeek + 6) % 7 + 1, format);

                    case "doy":
                        return FormatNumber(Shift(now, offset, unit, "d").DayOfYear, format);

                    case "week":
                        return FormatNumber(
                            ISOWeek.GetWeekOfYear(Shift(now, offset, unit, "w").Date), format);

                    case "daysinmonth":
                        DateTime target = Shift(now, offset, unit, "mo");
                        return FormatNumber(DateTime.DaysInMonth(target.Year, target.Month), format);

                    case "daysuntil":
                        return FormatDaysUntil(now, format, original);

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

        /// <summary>
        /// <c>{calc:式[|書式]}</c> を評価する。式の中の差し込みを先に展開してから計算する。
        /// </summary>
        private static string ExpandCalc(string body, string original, ExpandContext context, int depth)
        {
            (string expression, string format) = SplitCalcFormat(body);

            try
            {
                // {seq} や {daysuntil:…} を式の中で使えるようにする。
                // 数値にならない差し込みが混ざっていた場合はここで例外になる。
                string resolved = Expand(expression, context, depth + 1, true);
                return ExpressionEvaluator.Format(ExpressionEvaluator.Evaluate(resolved), format);
            }
            catch (Exception)
            {
                // 式が誤っていればそのまま残し、コピー結果から気付けるようにする
                return original;
            }
        }

        /// <summary>展開結果がそのまま数値として読めるかどうか。</summary>
        private static bool LooksLikeNumber(string value)
            => decimal.TryParse(
                value.Trim(),
                NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint,
                CultureInfo.InvariantCulture,
                out _);

        /// <summary>オフセットを付けられない差し込みに付いていたら誤りとして扱う。</summary>
        private static void RejectOffset(string name, bool hasOffset)
        {
            if (hasOffset)
            {
                throw new FormatException($"{{{name}}} にオフセットは指定できません。");
            }
        }

        /// <summary>式と書式を <c>|</c> で分ける。差し込みの中の <c>|</c> は無視する。</summary>
        private static (string Expression, string Format) SplitCalcFormat(string body)
        {
            int depth = 0;

            for (int i = body.Length - 1; i >= 0; i--)
            {
                char c = body[i];

                if (c == '}')
                {
                    depth++;
                }
                else if (c == '{')
                {
                    depth--;
                }
                else if (c == '|' && depth == 0)
                {
                    return (body[..i], body[(i + 1)..].Trim());
                }
            }

            return (body, string.Empty);
        }

        /// <summary>
        /// クリップボードの文字列を書式にしたがって整える。
        /// 書式が既知のキーワードでなければ正規表現として扱い、一致した部分を取り出す。
        /// （<c>/…/</c> のようにスラッシュで囲んでもよい）
        /// 取り出せなかった場合は、書いたままを残してユーザーが気付けるようにする。
        /// </summary>
        private static string FormatClipboard(string value, string format, string original)
        {
            string spec = format.Trim();

            switch (spec.ToLowerInvariant())
            {
                case "":
                case "trim":
                    return value.Trim();

                case "raw":
                    return value;

                case "digits":
                    return ExtractByRegex(value, @"\d+", original);

                case "upper":
                    return value.Trim().ToUpperInvariant();

                case "lower":
                    return value.Trim().ToLowerInvariant();

                case "line":
                case "line1":
                    return FirstLine(value);
            }

            // スラッシュで囲まれていれば、その中身を正規表現として扱う
            if (spec.Length >= 2 && spec[0] == '/' && spec[^1] == '/')
            {
                spec = spec[1..^1];
            }

            return ExtractByRegex(value, spec, original);
        }

        /// <summary>
        /// 正規表現に最初に一致した部分を返す。
        /// かっこ（キャプチャ）が書かれていればその中身を返すため、
        /// <c>ID-(\d+)</c> のように「目印＋取り出したい部分」と書ける。
        /// </summary>
        private static string ExtractByRegex(string value, string pattern, string original)
        {
            if (string.IsNullOrEmpty(pattern))
            {
                return original;
            }

            try
            {
                Match m = Regex.Match(value, pattern, RegexOptions.CultureInvariant, RegexTimeout);

                if (!m.Success)
                {
                    return original;
                }

                return m.Groups.Count > 1 && m.Groups[1].Success ? m.Groups[1].Value : m.Value;
            }
            catch (Exception)
            {
                // 正規表現が誤っている、または照合が長引いて打ち切られた場合
                return original;
            }
        }

        /// <summary>最初の行を返す（前後の空白は取り除く）。</summary>
        private static string FirstLine(string value)
        {
            int end = value.IndexOfAny(['\r', '\n']);
            return (end < 0 ? value : value[..end]).Trim();
        }

        private static string FormatNumber(int value, string format)
            => string.IsNullOrEmpty(format)
                ? value.ToString(CultureInfo.InvariantCulture)
                : value.ToString(format, CultureInfo.CurrentCulture);

        /// <summary>指定日までの日数。書式部分に基準日（yyyy-MM-dd など）を書く。</summary>
        private static string FormatDaysUntil(DateTime now, string format, string original)
        {
            if (string.IsNullOrWhiteSpace(format))
            {
                return original;
            }

            string text = format.Trim();

            // 2026-12-31 のような表記と、地域設定の表記（2026/12/31）の両方を受け付ける
            if (!DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime target)
                && !DateTime.TryParse(text, CultureInfo.CurrentCulture, DateTimeStyles.None, out target))
            {
                return original;
            }

            int days = (int)(target.Date - now.Date).TotalDays;
            return days.ToString(CultureInfo.InvariantCulture);
        }

        /// <summary>
        /// オフセットを適用する。単位が省略された場合は defaultUnit を使う。
        /// 単位を解釈できない場合（{date+1m} のような書き間違い）は例外にする。
        /// 黙って今日の日付を返すと、ユーザーが誤りに気付けないため。
        /// </summary>
        private static DateTime Shift(DateTime value, int offset, string unit, string defaultUnit)
        {
            if (offset == 0 && string.IsNullOrEmpty(unit))
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
                _ => throw new FormatException(
                    $"単位 '{unit}' を解釈できません。d 日 / w 週 / mo 月 / y 年 / h 時 / mi 分 / s 秒 が使えます。"),
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

            return Truncate(sb.ToString(), maxLength);
        }

        /// <summary>
        /// 指定した長さで切り詰めて末尾に … を付ける。
        /// サロゲートペア（絵文字など）の途中で切ると文字が壊れるため、その場合は 1 つ手前で切る。
        /// </summary>
        public static string Truncate(string value, int maxLength)
        {
            if (string.IsNullOrEmpty(value) || value.Length <= maxLength)
            {
                return value;
            }

            int end = maxLength;

            // 切る位置の直前が上位サロゲートなら、対になる下位サロゲートと分断されてしまう
            if (end > 0 && char.IsHighSurrogate(value[end - 1]))
            {
                end--;
            }

            return value[..end] + "…";
        }
    }
}
