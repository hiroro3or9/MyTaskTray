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
    /// スプリント（一定の日数で繰り返す期間）の区切り。
    /// <paramref name="AnchorDate"/> はどれか 1 つのスプリントの開始日で、
    /// そこから <paramref name="LengthDays"/> 日ごとに区切りが並んでいるものとして扱う。
    /// </summary>
    public sealed record SprintSchedule(DateTime AnchorDate, int LengthDays)
    {
        /// <summary>設定として妥当かどうか（長さが 1 日以上）。</summary>
        public bool IsValid => LengthDays >= 1;

        /// <summary><paramref name="value"/> を含むスプリントの開始日を返す。</summary>
        public DateTime StartOf(DateTime value)
        {
            if (!IsValid)
            {
                // 呼び出し側で弾いているが、0 除算でプロセスを巻き込まないための保険
                return value.Date;
            }

            DateTime anchor = AnchorDate.Date;
            double days = (value.Date - anchor).TotalDays;

            // 基準日より前でも 1 つ前のスプリントに落ちるよう、負の側にも切り下げる
            double index = Math.Floor(days / LengthDays);
            return anchor.AddDays(index * LengthDays);
        }
    }

    /// <summary>
    /// コピー文字列に含まれる <c>{...}</c> 形式の差し込みを展開する。
    ///
    /// 書式: <c>{名前[@基準][±数値[単位]]…[:書式]}</c>
    ///   例) {date}  {date:yyyyMMdd}  {date+1}  {date-1w:M月d日}  {seq:0000}  {seq+1}  {guid}
    /// 基準: 展開の起点となる日付を今日から差し替える。
    ///   @sprint    今日を含むスプリントの開始日（設定の基準日と長さから求める）
    ///   @clip      クリップボードに入っている日付
    ///   @date @monthstart @monthend @weekstart @weekend  既存の日付の差し込みと同じ日
    ///   例) {date@sprint:yyyyMMdd}  {date@clip+1w}  {week@monthstart}  {year@sprint-3mo}
    /// オフセットは複数書ける（書いた順に適用する）。
    ///   例) {date@sprint+1sp-1}（今のスプリントの最終日）  {date+1mo-1}（翌月の前日）
    /// 計算式: <c>{calc:式[|書式]}</c>（<c>{=式}</c> と書いてもよい）
    ///   例) {calc:(1000+200)*1.1}  {calc:1000*8%|#,##0}  {calc:{seq}*100}
    /// クリップボード: <c>{clip[:書式]}</c>
    ///   例) {clip}  {clip:digits}  {clip:/ID-(\d+)/}
    /// <c>{{</c> と <c>}}</c> はそれぞれ <c>{</c> <c>}</c> のエスケープ。
    /// 解釈できない差し込み（未知の名前・基準・単位・書式、オフセットを付けられない差し込みなど）は、
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

        // 名前 + 任意の基準 + 任意のオフセット（複数可）+ 任意の書式。
        // オフセットは必ず符号で始まるため、繰り返しても切り出し方が一通りに決まる。
        // GeneratedRegex はコンパイル時に実装を生成するため、RegexOptions.Compiled は不要。
        [GeneratedRegex(
            @"^(?<name>[A-Za-z]+)(?:@(?<base>[A-Za-z]+))?(?:(?<sign>[+\-])(?<num>\d+)(?<unit>[A-Za-z]*))*(?::(?<fmt>.*))?$",
            RegexOptions.CultureInvariant)]
        private static partial Regex InnerRegex();

        // クリップボードの文字列から日付らしい並びを取り出す（{date@clip} 用）。
        // 年を 4 桁に限ることで、電話番号（03-0000-0000）のような並びを拾わないようにする。
        [GeneratedRegex(
            @"(?<y>\d{4})\s*[-/.年]\s*(?<mo>\d{1,2})\s*[-/.月]\s*(?<d>\d{1,2})\s*日?|(?<!\d)(?<ymd>\d{8})(?!\d)",
            RegexOptions.CultureInvariant)]
        private static partial Regex ClipboardDateRegex();

        /// <summary>
        /// <c>@clip</c> が「文字列全体が日付」として受け付ける表記。
        /// 地域設定の解釈（<c>08/15/2026</c> のような並び）に左右されないよう、明示的に列挙する。
        /// </summary>
        /// <remarks>
        /// <c>M</c> / <c>d</c> は 1 桁でも 2 桁でも読めるため、
        /// <c>2026-8-15</c> と <c>2026-08-15</c> の両方をこの 1 つで受け付ける。
        /// </remarks>
        private static readonly string[] ClipboardDateFormats =
        [
            "yyyy-M-d", "yyyy/M/d", "yyyy.M.d", "yyyyMMdd", "yyyy年M月d日",
        ];

        // {random:1-6} の範囲。{random:-5-5} や {random:-10--5} のように下限が負でも読めるようにする。
        [GeneratedRegex(
            @"^\s*(?<min>-?\d+)\s*-\s*(?<max>-?\d+)\s*$",
            RegexOptions.CultureInvariant)]
        private static partial Regex RandomRangeRegex();

        /// <summary>設定画面の「差し込みを挿入」で提示する一覧（並び順がパネルの表示順になる）。</summary>
        public static IReadOnlyList<PlaceholderInfo> Placeholders { get; } =
        [
            new("{clip}", "クリップボード", "いまコピーしてある文字列（前後の空白は取り除く）"),
            new("{clip:digits}", "クリップボード", "コピー済みの文字列から最初の数字だけを取り出す"),
            new("{clip:/ID-(\\d+)/}", "クリップボード", "正規表現で取り出す（かっこがあればその中身）"),
            new("{clip:line}", "クリップボード", "1 行目だけを取り出す"),
            new("{clip:upper}", "クリップボード", "大文字にする（lower で小文字）"),
            new("{clip:raw}", "クリップボード", "空白や改行も含めてそのまま"),
            new("{date@clip:yyyyMMdd}", "クリップボードの日付",
                "コピーしてある日付を別の書式にする（2026/08/15 → 20260815）"),
            new("{date@clip:yyyy年M月d日}", "クリップボードの日付", "和文の日付にする"),
            new("{date@clip:ddd}", "クリップボードの日付", "その日の曜日"),
            new("{date@clip+1w}", "クリップボードの日付", "その 1 週間後（単位は {date} と同じ）"),
            new("{week@clip}", "クリップボードの日付", "その日の ISO 週番号（{month@clip} なども同様）"),
            new("{date}", "日付", "今日の日付"),
            new("{date:yyyyMMdd}", "日付", "書式を指定した日付"),
            new("{date:yyyy年M月d日}", "日付", "和文の日付"),
            new("{date+1}", "日付", "明日（-1 で昨日）"),
            new("{date+1w}", "日付", "1週間後（単位 d 日 / w 週 / mo 月 / y 年）"),
            new("{time}", "時刻", "現在の時刻"),
            new("{time:HH:mm:ss}", "時刻", "秒まで含む時刻"),
            new("{time+30}", "時刻", "30分後（単位 h 時 / mi 分 / s 秒）"),
            new("{datetime}", "時刻", "日付と時刻"),
            new("{hour}", "時刻", "現在の時（0〜23。{hour:00} で 2 桁）"),
            new("{minute}", "時刻", "現在の分（0〜59）"),
            new("{second}", "時刻", "現在の秒（0〜59）"),
            new("{date@sprint}", "スプリント", "今日を含むスプリントの開始日（設定の基準日と長さから求める）"),
            new("{date@sprint:yyyyMMdd}", "スプリント", "書式を指定したスプリント開始日"),
            new("{date@sprint+1sp}", "スプリント", "次のスプリントの開始日（-1sp で前のスプリント）"),
            new("{date@sprint+1sp-1}", "スプリント", "今のスプリントの最終日（オフセットは重ねて書ける）"),
            new("{calc:{year@sprint-3mo}-1996}-{calc:ceil({month@sprint-3mo}/3)}", "スプリント",
                "スプリント名。3 か月ずらして年度と年度内四半期（4〜6月=1）を求める"),
            new("{year@sprint-3mo}", "スプリント", "スプリント開始日の年度（4 月始まり。1〜3 月は前年度）"),
            new("{calc:ceil({month@sprint-3mo}/3)}", "スプリント", "スプリント開始日の年度内四半期（1〜4）"),
            new("{monthstart}", "月・週", "今月の初日（+1 で翌月）"),
            new("{monthend}", "月・週", "今月の末日（+1 で翌月）"),
            new("{weekstart}", "月・週", "今週の月曜日（+1 で翌週）"),
            new("{weekend}", "月・週", "今週の日曜日"),
            new("{date+1mo-1}", "月・週", "翌月の前日（オフセットは重ねて書ける）"),
            new("{week@monthstart}", "月・週", "今月 1 日の ISO 週番号（@ で基準の日を差し替える）"),
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
            new("{random:1-6}", "連番・その他", "範囲を指定した乱数（下限は負でもよい）"),
            new("{{", "連番・その他", "波かっこ { そのもの（}} なら }）"),
        ];

        /// <summary>
        /// 1 回の展開のあいだ持ち回る値。
        /// クリップボードの読み取りは <c>{clip}</c> が実際に現れたときだけ行い、
        /// 同じ展開の中では何度使っても同じ値になるように覚えておく。
        /// </summary>
        private sealed class ExpandContext(
            DateTime now, int sequenceValue, Func<string>? clipboard, SprintSchedule? sprint)
        {
            private string? _clipboard;

            public DateTime Now { get; } = now;

            public int SequenceValue { get; } = sequenceValue;

            /// <summary>スプリントの区切り。未設定なら <c>@sprint</c> は書いたままにする。</summary>
            public SprintSchedule? Sprint { get; } = sprint is { IsValid: true } ? sprint : null;

            /// <summary>呼び出し元がクリップボードの読み取り手段を渡しているかどうか。</summary>
            public bool HasClipboard { get; } = clipboard is not null;

            public string Clipboard => _clipboard ??= clipboard?.Invoke() ?? string.Empty;
        }

        /// <summary>差し込みを展開した文字列を返す。</summary>
        public static string Expand(string template, DateTime now, int sequenceValue)
            => Expand(template, now, sequenceValue, null, null);

        /// <summary>差し込みを展開した文字列を返す。</summary>
        /// <param name="clipboard">
        /// <c>{clip}</c> が現れたときに呼ばれ、クリップボードの文字列を返す関数。
        /// null の場合、<c>{clip}</c> は書いたままの文字列として残す。
        /// </param>
        public static string Expand(string template, DateTime now, int sequenceValue, Func<string>? clipboard)
            => Expand(template, now, sequenceValue, clipboard, null);

        /// <summary>差し込みを展開した文字列を返す。</summary>
        /// <param name="clipboard">
        /// <c>{clip}</c> が現れたときに呼ばれ、クリップボードの文字列を返す関数。
        /// null の場合、<c>{clip}</c> は書いたままの文字列として残す。
        /// </param>
        /// <param name="sprint">
        /// <c>@sprint</c> と <c>sp</c> 単位が参照するスプリントの区切り。
        /// null（未設定）の場合、それらを使った差し込みは書いたままの文字列として残す。
        /// </param>
        public static string Expand(
            string template, DateTime now, int sequenceValue, Func<string>? clipboard, SprintSchedule? sprint)
            => Expand(template, new ExpandContext(now, sequenceValue, clipboard, sprint), 0, false);

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
        public static bool ContainsSequence(string template)
            => ContainsToken(template, static (name, _) => IsSequenceName(name), 0);

        /// <summary>
        /// クリップボードの差し込みを含むかどうか。
        /// 含む場合だけクリップボードを読みに行き、空のときに注意を促す。
        /// 名前（<c>{clip}</c>）だけでなく基準（<c>{date@clip}</c>）も数える。
        /// </summary>
        public static bool ContainsClipboard(string template)
            => ContainsToken(template, static (name, @base) => IsClipboardName(name) || IsClipboardName(@base), 0);

        /// <summary>
        /// クリップボードを<strong>日付として</strong>読む差し込み（<c>{date@clip}</c> など）を含むかどうか。
        /// 含む場合、コピーする前に <see cref="CanParseClipboardDate"/> で読めるかを確かめる。
        /// </summary>
        public static bool ContainsClipboardDate(string template)
            => ContainsToken(template, static (_, @base) => IsClipboardName(@base), 0);

        /// <summary>
        /// クリップボードの文字列を日付として読めるかどうか。
        /// 読めないまま展開すると、差し込みが書いたまま残った文字列がコピーされてしまう。
        /// </summary>
        public static bool CanParseClipboardDate(string clipboard)
            => TryParseClipboardDate(clipboard) is not null;

        /// <summary>
        /// 秒〜分の単位で結果が変わる差し込み（<c>{time}</c> など）を含むかどうか。
        /// プレビューを一定間隔で更新し続ける必要があるかの判定に使う。
        /// <c>{guid}</c> や <c>{random}</c> も評価のたびに変わるが、
        /// 時間の経過で変わるわけではないため含めない（更新するとプレビューがちらつくだけになる）。
        /// </summary>
        public static bool ContainsTimeSensitive(string template)
            => ContainsToken(
                template,
                static (name, @base) => IsTimeSensitiveName(name) || IsTimeSensitiveName(@base),
                0);

        private static bool IsSequenceName(string name)
            => name.Equals("seq", StringComparison.OrdinalIgnoreCase);

        private static bool IsClipboardName(string name)
            => name.Equals("clip", StringComparison.OrdinalIgnoreCase);

        private static bool IsTimeSensitiveName(string name) => name.ToLowerInvariant() switch
        {
            "time" or "datetime" or "now" or "hour" or "minute" or "second" => true,
            _ => false,
        };

        /// <summary>
        /// 条件に合う差し込みを含むかどうか。計算式の中に書かれている場合も含むと判定する。
        /// </summary>
        /// <param name="matches">
        /// 差し込みの名前と基準（<c>@…</c>。無ければ空文字）を受け取り、数えるかどうかを返す。
        /// 名前と基準のどちらを見るかは条件ごとに違うため、呼び出し側で決める。
        /// </param>
        private static bool ContainsToken(string template, Func<string, string, bool> matches, int depth)
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
                if (IsToken(inner, matches) || ContainsToken(inner, matches, depth + 1))
                {
                    return true;
                }

                i = close + 1;
            }

            return false;
        }

        /// <summary>
        /// 差し込みの中身が条件に合うかどうか（書式やオフセット付きも含む）。
        ///
        /// 名前だけでなく<strong>基準（<c>@…</c>）も渡す</strong>。
        /// <c>{date@clip}</c> は名前が date だがクリップボードを読む必要があり、
        /// 名前しか見ないと <see cref="ContainsClipboard"/> が false を返して
        /// 空の文字列のまま展開されてしまう（しかも空クリップボードの警告も出ない）。
        /// </summary>
        private static bool IsToken(string inner, Func<string, string, bool> matches)
        {
            Match m = InnerRegex().Match(inner.Trim());

            if (!m.Success)
            {
                return false;
            }

            return matches(
                m.Groups["name"].Value,
                m.Groups["base"].Success ? m.Groups["base"].Value : string.Empty);
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

            int sequenceValue = context.SequenceValue;
            string name = m.Groups["name"].Value.ToLowerInvariant();
            string format = m.Groups["fmt"].Success ? m.Groups["fmt"].Value : string.Empty;

            // 基準（@sprint など）。日付を今日以外から数え始めたいときに使う
            bool hasBase = m.Groups["base"].Success;
            DateTime now = context.Now;
            if (hasBase)
            {
                // 未知の基準、スプリントが未設定、クリップボードが日付として読めない場合は、
                // 黙って今日を使うと誤りに気付けないため書いたままを残す
                if (ResolveBase(m.Groups["base"].Value.ToLowerInvariant(), context) is not { } resolved)
                {
                    return original;
                }

                now = resolved;
            }

            // オフセットは複数書ける（{date@sprint+1sp-1} など）。書いた順に適用する
            CaptureCollection nums = m.Groups["num"].Captures;
            CaptureCollection signs = m.Groups["sign"].Captures;
            CaptureCollection units = m.Groups["unit"].Captures;

            (int Offset, string Unit)[] offsets = new (int, string)[nums.Count];
            for (int k = 0; k < nums.Count; k++)
            {
                if (!int.TryParse(nums[k].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int value))
                {
                    return original;
                }

                offsets[k] = (signs[k].Value == "-" ? -value : value, units[k].Value.ToLowerInvariant());
            }

            bool hasOffset = offsets.Length > 0;

            // オフセットを適用した日付。差し込みごとに既定の単位が違うため、その場で渡す
            DateTime At(string defaultUnit)
            {
                DateTime value = now;
                int sprintDays = context.Sprint?.LengthDays ?? 0;

                foreach ((int offset, string unit) in offsets)
                {
                    value = Shift(value, offset, unit, defaultUnit, sprintDays);
                }

                return value;
            }

            try
            {
                switch (name)
                {
                    case "date":
                        return FormatDate(At("d"), format, DefaultDateFormat);

                    case "time":
                        return FormatDate(At("mi"), format, DefaultTimeFormat);

                    case "datetime":
                    case "now":
                        return FormatDate(At("d"), format, DefaultDateTimeFormat);

                    case "monthstart":
                        return FormatDate(MonthStart(At("mo")), format, DefaultDateFormat);

                    case "monthend":
                        return FormatDate(MonthEnd(At("mo")), format, DefaultDateFormat);

                    case "weekstart":
                        return FormatDate(WeekStart(At("w")), format, DefaultDateFormat);

                    case "weekend":
                        return FormatDate(WeekStart(At("w")).AddDays(6), format, DefaultDateFormat);

                    case "seq":
                        RejectBase(name, hasBase);

                        // {seq+1} は「次の番号 + 1」。日付ではないので、
                        // 単位も、オフセットを重ねて書くことも意味を持たない
                        if (offsets.Length > 1)
                        {
                            throw new FormatException("{seq} にオフセットは 1 つだけ指定できます。");
                        }

                        if (offsets.Length == 1 && !string.IsNullOrEmpty(offsets[0].Unit))
                        {
                            throw new FormatException("{seq} に単位は指定できません。");
                        }

                        // 表示用のオフセットは永続カウンターを変更しない。
                        // int の範囲を超えた場合は外側の catch で差し込みを未展開のまま残す。
                        return FormatSequence(
                            checked(sequenceValue + (offsets.Length == 1 ? offsets[0].Offset : 0)), format);

                    case "clip":
                        RejectBase(name, hasBase);
                        RejectOffset(name, hasOffset);

                        // 呼び出し元がクリップボードを読めない場面（テストなど）では
                        // 中途半端な文字列を返さず、書いたままを残す
                        if (!context.HasClipboard)
                        {
                            return original;
                        }

                        return FormatClipboard(context.Clipboard, format, original);

                    case "guid":
                        RejectBase(name, hasBase);
                        RejectOffset(name, hasOffset);
                        return FormatGuid(format);

                    case "random":
                        RejectBase(name, hasBase);
                        RejectOffset(name, hasOffset);
                        return FormatRandom(format);

                    case "year":
                        return FormatNumber(At("y").Year, format);

                    case "month":
                        return FormatNumber(At("mo").Month, format);

                    case "day":
                        return FormatNumber(At("d").Day, format);

                    case "hour":
                        return FormatNumber(At("h").Hour, format);

                    case "minute":
                        return FormatNumber(At("mi").Minute, format);

                    case "second":
                        return FormatNumber(At("s").Second, format);

                    case "dow":
                        return FormatNumber(((int)At("d").DayOfWeek + 6) % 7 + 1, format);

                    case "doy":
                        return FormatNumber(At("d").DayOfYear, format);

                    case "week":
                        return FormatNumber(ISOWeek.GetWeekOfYear(At("w").Date), format);

                    case "daysinmonth":
                        DateTime target = At("mo");
                        return FormatNumber(DateTime.DaysInMonth(target.Year, target.Month), format);

                    case "daysuntil":
                        // 基準日は書式部分で指定するため、オフセットには意味がない。
                        // 黙って捨てると誤りに気付けないので、書いたままを残す
                        RejectOffset(name, hasOffset);
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
        /// 基準（<c>@…</c>）が指す日付を返す。解釈できなければ null。
        ///
        /// 基準は「展開の起点となる日付を差し替える」ものに限る。
        /// 年度や四半期のような<em>派生</em>は、基準を 1 つしか書けない以上ここに置くと
        /// <c>@sprint</c> と併用できなくなるため、名前側（またはオフセット）で表す。
        ///
        /// 基準そのものにオフセットは書けない（<c>{year@sprint-3mo}</c> の <c>-3mo</c> は結果に掛かる）。
        /// 入れ子にすると読み手にも追えなくなるため、意図的に許していない。
        /// </summary>
        private static DateTime? ResolveBase(string baseName, ExpandContext context) => baseName switch
        {
            // 今日を含むスプリントの開始日。設定が無ければ使えない
            "sprint" => context.Sprint?.StartOf(context.Now),

            // クリップボードに入っている日付。読み取り手段が無い場面（テストなど）でも使えない
            "clip" => context.HasClipboard ? TryParseClipboardDate(context.Clipboard) : null,

            // 既存の日付の差し込みは、そのまま基準にもできる。
            // {monthstart:ddd} のように書けるものも多いが、
            // {week@monthstart} のように「数値の差し込みを今日以外に対して使う」のはこれでしか書けない
            "date" or "today" or "datetime" or "now" => context.Now,
            "monthstart" => MonthStart(context.Now),
            "monthend" => MonthEnd(context.Now),
            "weekstart" => WeekStart(context.Now),
            "weekend" => WeekStart(context.Now).AddDays(6),

            _ => null,
        };

        /// <summary>
        /// クリップボードの文字列から日付を読み取る。読み取れなければ null。
        ///
        /// まず文字列全体を決まった表記として読み、駄目なら文中から日付らしい並びを 1 つ取り出す。
        /// 「リリース日: 2026/08/15 まで」のような文字列をそのまま使えるようにするためで、
        /// 年を 4 桁に限ることで電話番号のような並びは拾わないようにしている。
        /// ただし 8 桁の数字は伝票番号と区別できないため、そこは避けられない曖昧さとして受け入れる。
        /// </summary>
        private static DateTime? TryParseClipboardDate(string value)
        {
            if (DateTime.TryParseExact(
                    value.Trim(),
                    ClipboardDateFormats,
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.None,
                    out DateTime exact))
            {
                return exact;
            }

            Match m = ClipboardDateRegex().Match(value);
            if (!m.Success)
            {
                return null;
            }

            if (m.Groups["ymd"].Success)
            {
                return DateTime.TryParseExact(
                    m.Groups["ymd"].Value,
                    "yyyyMMdd",
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.None,
                    out DateTime compact)
                    ? compact
                    : null;
            }

            int year = int.Parse(m.Groups["y"].Value, CultureInfo.InvariantCulture);
            int month = int.Parse(m.Groups["mo"].Value, CultureInfo.InvariantCulture);
            int day = int.Parse(m.Groups["d"].Value, CultureInfo.InvariantCulture);

            // 2026-13-45 のような存在しない日付は、読み取れなかったものとして扱う
            if (year < 1 || year > 9999
                || month < 1 || month > 12
                || day < 1 || day > DateTime.DaysInMonth(year, month))
            {
                return null;
            }

            return new DateTime(year, month, day);
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

        /// <summary>日付から作られない差し込みに基準（@…）が付いていたら誤りとして扱う。</summary>
        private static void RejectBase(string name, bool hasBase)
        {
            if (hasBase)
            {
                throw new FormatException($"{{{name}}} に基準（@）は指定できません。");
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

        /// <summary>
        /// 数値の差し込みを整える。書式を指定しない素の数値は地域設定に左右されないよう
        /// InvariantCulture、書式を指定した場合は桁区切りなどを地域設定に合わせる。
        /// </summary>
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
        /// <param name="sprintDays">
        /// 単位 <c>sp</c>（スプリント）1 つ分の日数。0 ならスプリントが未設定で、<c>sp</c> は使えない。
        /// </param>
        private static DateTime Shift(DateTime value, int offset, string unit, string defaultUnit, int sprintDays)
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
                "sp" => sprintDays >= 1
                    ? value.AddDays((double)offset * sprintDays)
                    : throw new FormatException("単位 'sp' を使うには、設定でスプリントの基準日と長さを決めてください。"),
                _ => throw new FormatException(
                    $"単位 '{unit}' を解釈できません。d 日 / w 週 / mo 月 / y 年 / h 時 / mi 分 / s 秒 / sp スプリント が使えます。"),
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

        /// <summary>
        /// 連番を書式にしたがって整える。書式を指定しない素の数値は
        /// 地域設定に左右されないよう InvariantCulture、
        /// 書式を指定した場合は <see cref="FormatNumber"/> と同じく CurrentCulture で揃える。
        /// </summary>
        private static string FormatSequence(int value, string format)
            => string.IsNullOrEmpty(format)
                ? value.ToString(CultureInfo.InvariantCulture)
                : value.ToString(format, CultureInfo.CurrentCulture);

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
                // 単純に '-' で分けると {random:-5-5} のような負の下限を扱えないため、
                // 「数値 - 数値」の形として読む
                Match m = RandomRangeRegex().Match(format);
                if (!m.Success
                    || !int.TryParse(m.Groups["min"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out min)
                    || !int.TryParse(m.Groups["max"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out max))
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
