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
    /// 1 回のコピー操作で受け取る入力。
    /// <paramref name="Patterns"/> があれば、すべての正規表現に一致する文字列だけを受け付ける。
    /// </summary>
    internal sealed record InputCaptureDefinition(
        string Name,
        IReadOnlyList<string> Patterns);

    /// <summary>
    /// <c>{choice:名前:選択肢|選択肢}</c> で定義された、コピー時に選ばせる選択肢。
    /// </summary>
    /// <param name="AllowMultiple">
    /// <c>{choices:…}</c>（複数形）で書かれ、いくつでも選べるかどうか。
    /// 選ばれたものは <see cref="TemplateEngine.ChoicesSeparator"/> で連結して差し込む。
    /// </param>
    internal sealed record ChoiceDefinition(
        string Name,
        IReadOnlyList<string> Options,
        bool AllowMultiple);

    /// <summary>選択肢の書き方の誤り。設定画面で気付けるようにするために集める。</summary>
    internal enum ChoiceIssueKind
    {
        /// <summary>名前に <c>|</c> が入っている（無名形式のつもりで書いた可能性）。</summary>
        NameHasPipe,

        /// <summary>選択肢が <see cref="TemplateEngine.MinChoiceOptions"/> 個未満。</summary>
        TooFewOptions,

        /// <summary>選択肢が <see cref="TemplateEngine.MaxChoiceOptions"/> 個を超える。</summary>
        TooManyOptions,

        /// <summary>
        /// 同じ名前の定義が 2 つ以上ある（最初のものを使う）。
        /// <c>{choice}</c> と <c>{choices}</c> にまたがる場合も含む。
        /// </summary>
        Duplicate,

        /// <summary>選択肢を書かずに名前だけ参照している。</summary>
        Undefined,

        /// <summary>選択肢の中に、そこでは使えない差し込みが書かれている。</summary>
        UnsupportedPlaceholderInOption,
    }

    /// <summary>選択肢の書き方の誤り 1 件。</summary>
    internal sealed record ChoiceIssue(ChoiceIssueKind Kind, string Name);

    /// <summary>テンプレートに書かれた選択肢の一覧と、その誤り。</summary>
    internal sealed record ChoiceAnalysis(
        IReadOnlyList<ChoiceDefinition> Definitions,
        IReadOnlyList<ChoiceIssue> Issues);

    /// <summary>
    /// <see cref="TemplateEngine.Expand(string, DateTime, int, ExpandValues)"/> へ渡す値。
    ///
    /// <para>
    /// 差し込みが増えるたびに位置引数を足していくと、呼び出し側で何番目が何なのか読めなくなる。
    /// 名前付きで書ける袋にまとめ、必要なものだけを指定できるようにする。
    /// </para>
    /// </summary>
    public sealed record ExpandValues
    {
        /// <summary>
        /// <c>{clip}</c> が現れたときに呼ばれ、クリップボードの文字列を返す関数。
        /// null の場合、<c>{clip}</c> は書いたままの文字列として残す。
        /// </summary>
        public Func<string>? Clipboard { get; init; }

        /// <summary>
        /// <c>@sprint</c> と <c>sp</c> 単位が参照するスプリントの区切り。
        /// null（未設定）の場合、それらを使った差し込みは書いたままの文字列として残す。
        /// </summary>
        public SprintSchedule? Sprint { get; init; }

        /// <summary><c>{input:名前}</c> に差し込む値。</summary>
        public IReadOnlyDictionary<string, string>? Inputs { get; init; }

        /// <summary><c>{match:名前}</c> に差し込む値。</summary>
        public IReadOnlyDictionary<string, string>? Matches { get; init; }

        /// <summary>
        /// <c>{app:name}</c> に差し込む、メニューを開く直前に前面だったアプリの名前。
        /// null の場合、app 系の差し込みは書いたままの文字列として残す。
        /// </summary>
        public string? AppName { get; init; }

        /// <summary>
        /// <c>{app:title}</c> に差し込む、メニューを開く直前に前面だったウィンドウのタイトル。
        /// null の場合、タイトルを使う差し込みは書いたままの文字列として残す。
        /// </summary>
        public string? AppTitle { get; init; }

        /// <summary>
        /// <c>{choice:名前}</c> に差し込む値。
        ///
        /// <para>
        /// 将来の複数選択（<c>{choices:…}</c>）も、選ばれたものを連結した<strong>1 つの文字列</strong>として
        /// ここへ入れる。器を増やさずに済ませるための取り決め。
        /// </para>
        /// </summary>
        public IReadOnlyDictionary<string, string>? Choices { get; init; }

        /// <summary>
        /// 差し込んだ値だけに適用する後処理。テンプレート本体には適用しない。
        /// HTML として組み立てる項目で、値に含まれる記号をエスケープするために使う。
        /// </summary>
        public Func<string, string>? ValueTransform { get; init; }
    }

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
    /// 選択肢: <c>{choice:名前:選択肢|選択肢}</c>（コピーするときにメニューで選ぶ）
    ///   例) {choice:敬称:様|御中}  2 回目以降は {choice:敬称} だけで同じ選択を差し込める
    ///       {choices:出席者:田中|佐藤|鈴木}（複数形。選んだものを「、」でつなぐ）
    /// クリップボード: <c>{clip[:書式]}</c>
    ///   例) {clip}  {clip:digits}  {clip:/ID-(\d+)/}
    /// 文字列置換: <c>{replace:元の値|検索文字|置換文字}</c>
    /// 正規表現置換: <c>{regexreplace:元の値|パターン|置換文字}</c>
    ///   例) {replace:{clip}| |-}  {regexreplace:{input:名前}|\s+|-}
    /// 前面アプリ: <c>{app:name}</c> <c>{app:title}</c> <c>{app:title:/正規表現/}</c>
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
        /// <c>{choice:名前:…}</c> の選択肢の数の下限。
        /// 1 個以下は選ばせる意味がないので、書き間違いとみなして書いたまま残す。
        /// </summary>
        internal const int MinChoiceOptions = 2;

        /// <summary>
        /// 選択肢の数の上限。メニュー 1 枚に並べきれる数として決めている
        /// （番号キーが振れるのは 10 個までだが、それ以降は矢印キーで選べる）。
        /// </summary>
        internal const int MaxChoiceOptions = 20;

        /// <summary>
        /// <c>{choices:…}</c> で選ばれたものをつなぐ文字。
        ///
        /// <para>
        /// 指定する記法はまだ決めていない（<c>DESIGN_CHOICE.md</c> §1-4）。
        /// 選択肢の並びの中に区切りを書く場所が無く、無理に足すと選択肢と見分けがつかなくなる。
        /// 「<c>、</c> 以外が要る」場面が出てから決める。
        /// </para>
        /// </summary>
        internal const string ChoicesSeparator = "、";

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
            new("{app:name}", "前面アプリ", "メニューを開く直前に前面だったアプリ名（.exe は除く）"),
            new("{app:title}", "前面アプリ", "メニューを開く直前に前面だったウィンドウのタイトル"),
            new("{app:title:/Issue #(\\d+)/}", "前面アプリ", "タイトルを正規表現で取り出す（かっこがあればその中身）"),
            new("{app:name:/^(.*)$/}", "前面アプリ", "アプリ名を正規表現で取り出す"),
            new("{clip}", "クリップボード", "いまコピーしてある文字列（前後の空白は取り除く）"),
            new("{clip:digits}", "クリップボード", "コピー済みの文字列から最初の数字だけを取り出す"),
            new("{clip:/ID-(\\d+)/}", "クリップボード", "正規表現で取り出す（かっこがあればその中身）"),
            new("{clip:line}", "クリップボード", "1 行目だけを取り出す"),
            new("{clip:upper}", "クリップボード", "大文字にする（lower で小文字）"),
            new("{clip:raw}", "クリップボード", "空白や改行も含めてそのまま"),
            new("{input:名前}", "複数入力", "項目を選んだあと、名前ごとにコピーした値を順番に差し込む"),
            new("{input:Issue URL:/issues/(\\d+)/}", "複数入力", "正規表現に一致する入力だけを受け取り、かっこの中身を差し込む"),
            new("{match:名前}", "スマートアクション", "正規表現などの表示条件で取り出した値を差し込む"),
            new("{choice:敬称:様|御中}", "選択肢", "コピーするときにメニューで選ぶ（2〜20 個まで）"),
            new("{choice:敬称}", "選択肢", "同じ名前を書けば、選んだものを 2 か所以上に差し込める"),
            new("{choice:急ぎ:|【至急】}", "選択肢", "空の選択肢も書ける（付けない / 【至急】）"),
            new("{choices:出席者:田中|佐藤|鈴木}", "選択肢", "いくつでも選べる。選んだものを「、」でつなぐ"),
            new("{replace:{clip}|検索|置換}", "文字列変換", "差し込み結果に含まれる文字列をすべて置き換える"),
            new("{regexreplace:{clip}|\\s+|-}", "文字列変換", "正規表現に一致する箇所をすべて置き換える"),
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
            DateTime now,
            int sequenceValue,
            Func<string>? clipboard,
            SprintSchedule? sprint,
            IReadOnlyDictionary<string, string>? inputs,
            IReadOnlyDictionary<string, string>? matches,
            IReadOnlyDictionary<string, string>? choices,
            string? appName,
            string? appTitle)
        {
            private string? _clipboard;

            public DateTime Now { get; } = now;

            public int SequenceValue { get; } = sequenceValue;

            /// <summary>スプリントの区切り。未設定なら <c>@sprint</c> は書いたままにする。</summary>
            public SprintSchedule? Sprint { get; } = sprint is { IsValid: true } ? sprint : null;

            /// <summary>呼び出し元がクリップボードの読み取り手段を渡しているかどうか。</summary>
            public bool HasClipboard { get; } = clipboard is not null;

            public string Clipboard => _clipboard ??= clipboard?.Invoke() ?? string.Empty;

            public IReadOnlyDictionary<string, string>? Inputs { get; } = inputs;

            public IReadOnlyDictionary<string, string>? Matches { get; } = matches;

            public string? AppName { get; } = appName;

            public string? AppTitle { get; } = appTitle;

            /// <summary>
            /// <c>{choice:名前}</c> に差し込む値。
            /// 選ぶ前（設定画面のプレビューやツールチップ）は null で、
            /// そのとき <c>{choice:…}</c> は書いたままの文字列として残る。
            /// </summary>
            public IReadOnlyDictionary<string, string>? Choices { get; } = choices;

            /// <summary>
            /// 差し込んだ値に対する後処理。テンプレート本体には適用しない。
            /// HTML として組み立てる項目で、値に含まれる &lt; や &amp; を
            /// タグとして解釈させないために使う。
            /// </summary>
            public Func<string, string>? ValueTransform { get; init; }
        }

        /// <summary>差し込みを展開した文字列を返す。</summary>
        /// <remarks>
        /// 渡す値は <see cref="ExpandValues"/> にまとめて名前付きで指定する。
        ///
        /// <para>
        /// 以前は「クリップボード」「スプリント」「入力」…と位置引数を足していくオーバーロードが
        /// 5 段あったが、<c>{choice}</c> で 6 つめの値が増えたのを機に袋へ移した。
        /// 位置引数のままだと、呼び出し側に並ぶ <c>null</c> が何を指すのか読めなくなる。
        /// </para>
        /// </remarks>
        public static string Expand(
            string template, DateTime now, int sequenceValue, ExpandValues values)
            => Expand(
                template,
                new ExpandContext(
                    now,
                    sequenceValue,
                    values.Clipboard,
                    values.Sprint,
                    values.Inputs,
                    values.Matches,
                    values.Choices,
                    values.AppName,
                    values.AppTitle)
                {
                    ValueTransform = values.ValueTransform,
                },
                0,
                false);

        /// <summary>
        /// テンプレートに現れる <c>{input:名前}</c> の名前を、最初に現れた順で返す。
        /// 同じ名前は大文字小文字を区別せず 1 回だけ返す。
        /// </summary>
        public static IReadOnlyList<string> GetInputNames(string template)
            => [.. GetInputDefinitions(template).Select(static input => input.Name)];

        /// <summary>
        /// テンプレートに現れる入力を、最初に現れた順で返す。
        /// 同じ名前は大文字小文字を区別せず 1 回にまとめ、
        /// <c>{input:名前:/正規表現/}</c> の条件が複数あればすべて保持する。
        /// </summary>
        internal static IReadOnlyList<InputCaptureDefinition> GetInputDefinitions(string template)
        {
            List<string> names = [];
            Dictionary<string, List<string>> patterns = new(StringComparer.OrdinalIgnoreCase);
            CollectInputDefinitions(template, names, patterns, 0);

            return [.. names.Select(name => new InputCaptureDefinition(name, [.. patterns[name]]))];
        }

        private static void CollectInputDefinitions(
            string template,
            List<string> names,
            Dictionary<string, List<string>> patterns,
            int depth)
        {
            if (string.IsNullOrEmpty(template) || depth > MaxDepth)
            {
                return;
            }

            for (int i = 0; i < template.Length; i++)
            {
                if (template[i] != '{')
                {
                    continue;
                }

                if (i + 1 < template.Length && template[i + 1] == '{')
                {
                    i++;
                    continue;
                }

                int close = FindClosingBrace(template, i);
                if (close < 0)
                {
                    continue;
                }

                string inner = template[(i + 1)..close];
                if (TryParseInputToken(inner, out string inputName, out string? pattern))
                {
                    if (!patterns.TryGetValue(inputName, out List<string>? inputPatterns))
                    {
                        names.Add(inputName);
                        inputPatterns = [];
                        patterns[inputName] = inputPatterns;
                    }

                    if (pattern is not null
                        && !inputPatterns.Contains(pattern, StringComparer.Ordinal))
                    {
                        inputPatterns.Add(pattern);
                    }
                }
                else if (TryGetTextTransformBody(inner, out _, out string transformBody))
                {
                    // 検索文字と置換文字はリテラルとして扱う。
                    // 入れ子として展開する「元の値」だけから入力を収集する。
                    if (TrySplitTextTransformArguments(
                        transformBody,
                        out string source,
                        out _,
                        out _))
                    {
                        CollectInputDefinitions(source, names, patterns, depth + 1);
                    }
                }
                else
                {
                    // {calc:{input:金額}*1.1} のような入れ子も拾う。
                    CollectInputDefinitions(inner, names, patterns, depth + 1);
                }

                i = close;
            }
        }

        /// <summary>
        /// <c>input:名前</c> または <c>input:名前:/正規表現/</c> を解析する。
        /// 正規表現は <c>{clip:/…/}</c> と同じくスラッシュで囲み、
        /// 最初のキャプチャがあればその値、なければ一致全体を差し込む。
        /// </summary>
        private static bool TryParseInputToken(
            string inner,
            out string name,
            out string? pattern)
        {
            const string Prefix = "input:";
            string trimmed = inner.Trim();
            if (!trimmed.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase))
            {
                name = string.Empty;
                pattern = null;
                return false;
            }

            string body = trimmed[Prefix.Length..].Trim();
            int patternSeparator = body.IndexOf(":/", StringComparison.Ordinal);

            if (patternSeparator < 0)
            {
                name = body;
                pattern = null;
            }
            else
            {
                if (body.Length <= patternSeparator + 2 || body[^1] != '/')
                {
                    name = string.Empty;
                    pattern = null;
                    return false;
                }

                name = body[..patternSeparator].Trim();
                pattern = body[(patternSeparator + 2)..^1];
                if (pattern.Length == 0)
                {
                    name = string.Empty;
                    pattern = null;
                    return false;
                }
            }

            return name.Length is >= 1 and <= 80
                && name.IndexOfAny(['{', '}', '\r', '\n']) < 0;
        }

        /// <summary>
        /// <c>app:name</c> / <c>app:title</c> と、末尾に正規表現を付けた形を解析する。
        /// 正規表現の書き方と取り出し規則は <c>{clip:/…/}</c>・<c>{input:…:/…/}</c> と揃える。
        /// </summary>
        private static bool TryParseAppToken(
            string inner,
            out string member,
            out string? pattern)
        {
            const string Prefix = "app:";
            string trimmed = inner.Trim();
            if (!trimmed.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase))
            {
                member = string.Empty;
                pattern = null;
                return false;
            }

            string body = trimmed[Prefix.Length..].Trim();
            int patternSeparator = body.IndexOf(":/", StringComparison.Ordinal);

            if (patternSeparator < 0)
            {
                member = body;
                pattern = null;
            }
            else
            {
                if (body.Length <= patternSeparator + 2 || body[^1] != '/')
                {
                    member = string.Empty;
                    pattern = null;
                    return false;
                }

                member = body[..patternSeparator].Trim();
                pattern = body[(patternSeparator + 2)..^1];
                if (pattern.Length == 0)
                {
                    member = string.Empty;
                    pattern = null;
                    return false;
                }
            }

            return member.Equals("name", StringComparison.OrdinalIgnoreCase)
                || member.Equals("title", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// テンプレートに現れる <c>{choice:名前:選択肢|選択肢}</c> を、最初に現れた順で返す。
        /// 同じ名前が 2 度定義されている場合は最初のものを採る。
        /// </summary>
        internal static IReadOnlyList<ChoiceDefinition> GetChoiceDefinitions(string template)
            => AnalyzeChoices(template).Definitions;

        /// <summary>
        /// テンプレートに書かれた選択肢を集め、あわせて書き方の誤りも返す。
        ///
        /// <para>
        /// 誤りのある選択肢は <see cref="ChoiceAnalysis.Definitions"/> に含めない。
        /// 展開時に値が見つからず、書いたままの文字列が残る（既存の方針と同じ）。
        /// 設定画面はここで集めた誤りを使って、貼り付ける前に気付けるようにする。
        /// </para>
        /// </summary>
        internal static ChoiceAnalysis AnalyzeChoices(string template)
        {
            List<ChoiceDefinition> definitions = [];
            List<ChoiceIssue> issues = [];
            Dictionary<string, ChoiceDefinition> byName = new(StringComparer.OrdinalIgnoreCase);

            // 参照（{choice:名前}）は定義より前に書かれていることもあるので、
            // いったん集めてから、最後に定義の有無を確かめる
            List<string> references = [];

            CollectChoiceDefinitions(template, definitions, issues, byName, references, 0);

            // 同じ名前を 2 か所で参照していても、伝えたいことは 1 つ。
            // 名前ごとに 1 回だけ知らせる
            HashSet<string> reported = new(StringComparer.OrdinalIgnoreCase);
            foreach (string name in references)
            {
                if (!byName.ContainsKey(name) && reported.Add(name))
                {
                    issues.Add(new ChoiceIssue(ChoiceIssueKind.Undefined, name));
                }
            }

            return new ChoiceAnalysis(definitions, issues);
        }

        /// <summary>選択肢の差し込みを含むかどうか。</summary>
        public static bool ContainsChoice(string template)
            => ContainsToken(template, static (name, _) => IsChoiceName(name), 0);

        /// <summary>
        /// <strong>まだ選んでいない段階</strong>で選択肢を見せるときに使う代表値。
        /// 各選択肢の<strong>先頭</strong>を選んだものとして返す。選択肢が無ければ null。
        ///
        /// <para>
        /// <c>{input:名前}</c> は編集時点で値が原理的に分からないため書いたまま残すが、
        /// 選択肢は候補がテンプレートに書いてある。書いたまま残すのは、
        /// エンジンが知っている情報を捨てることになる。
        /// </para>
        /// <para>
        /// <strong>この規則は 3 か所で揃える必要がある</strong>
        /// ――設定画面のプレビュー、差し込み一覧の「現在値」、トレイのツールチップ。
        /// 揃えるために、選び方をここに 1 つだけ置いている。
        /// 代表値であることは、設定画面が別途文言で伝える。
        /// </para>
        /// </summary>
        /// <param name="values">
        /// 選択肢の中の差し込み（<c>{date}</c> など）を展開するために使う。
        /// <c>Choices</c> と <c>ValueTransform</c> は無視される
        /// ――前者は循環し、後者はここで掛けると<strong>コピー時と二重にエスケープされる</strong>。
        /// </param>
        internal static IReadOnlyDictionary<string, string>? GetDefaultChoices(
            string template, DateTime now, int sequenceValue, ExpandValues values)
        {
            IReadOnlyList<ChoiceDefinition> definitions = GetChoiceDefinitions(template);
            if (definitions.Count == 0)
            {
                return null;
            }

            Dictionary<string, string> resolved = new(StringComparer.OrdinalIgnoreCase);
            foreach (ChoiceDefinition definition in definitions)
            {
                // 複数選択も先頭 1 個だけを選んだものとして扱う。
                // 0 個（＝空文字）にすると、何も見えないプレビューになってしまう
                resolved[definition.Name] = ResolveChoiceOption(
                    definition.Options[0], now, sequenceValue, values);
            }

            return resolved;
        }

        /// <summary>
        /// 選択肢に書かれた差し込みを展開して、実際に差し込まれる文字列にする。
        /// </summary>
        /// <remarks>
        /// <c>ValueTransform</c> は<strong>意図的に外す</strong>。
        /// 展開した値はこのあとコピー時に「差し込まれた値」として一度エスケープされるので、
        /// ここでも掛けると <c>&amp;amp;lt;</c> のように二重になる。
        /// </remarks>
        internal static string ResolveChoiceOption(
            string option, DateTime now, int sequenceValue, ExpandValues values)
            => option.Length == 0
                ? string.Empty
                : Expand(
                    option,
                    now,
                    sequenceValue,
                    values with { Choices = null, ValueTransform = null });

        /// <summary>
        /// <c>{choices:…}</c> で選ばれたものを 1 つの文字列につなぐ。
        ///
        /// <para>
        /// 並びは<strong>選択肢を書いた順</strong>で、クリックした順ではない。
        /// クリック順にすると、同じものを選び直しただけで結果の文字列が変わってしまう。
        /// </para>
        /// <para>
        /// 1 つも選ばなければ空文字。空の選択肢を許している（「付けない」が正当な結果である）のと
        /// 同じ理屈で、これも誤りとしては扱わない。
        /// </para>
        /// </summary>
        /// <param name="options">
        /// <strong>展開後</strong>の選択肢。メニューに出したものと同じ文字列を渡す
        /// （見たものがそのままコピーされるようにするため）。
        /// </param>
        /// <param name="selectedIndexes">
        /// 選ばれた選択肢の位置。同じ文字列の選択肢が 2 つ書かれていても
        /// 取り違えないよう、値ではなく位置で受け取る。
        /// </param>
        /// <remarks>
        /// 空の選択肢は選ばれていても連結に加えない。
        /// 加えると <c>{choices:敬称:|様|さん}</c> で「空」と「さん」を選んだときに
        /// <c>、さん</c> という<strong>区切りだけが浮いた文字列</strong>になってしまう。
        ///
        /// <para>
        /// 単一選択では空の選択肢に「付けない」という意味があるが、
        /// 複数選択では<strong>何も選ばないことで同じ結果になる</strong>ため、
        /// 空の選択肢を落としても表せなくなるものは無い。
        /// メニューでは <c>(空)</c> の行にチェックを付けても結果が変わらないので、
        /// 意味が無いことはその場で分かる。
        /// </para>
        /// </remarks>
        internal static string JoinChoices(
            IReadOnlyList<string> options, IReadOnlyCollection<int> selectedIndexes)
            => string.Join(
                ChoicesSeparator,
                options.Where((option, index) => selectedIndexes.Contains(index) && option.Length > 0));

        private static bool IsChoiceName(string name)
            => name.Equals("choice", StringComparison.OrdinalIgnoreCase)
                || name.Equals("choices", StringComparison.OrdinalIgnoreCase);

        private static void CollectChoiceDefinitions(
            string template,
            List<ChoiceDefinition> definitions,
            List<ChoiceIssue> issues,
            Dictionary<string, ChoiceDefinition> byName,
            List<string> references,
            int depth)
        {
            if (string.IsNullOrEmpty(template) || depth > MaxDepth)
            {
                return;
            }

            for (int i = 0; i < template.Length; i++)
            {
                if (template[i] != '{')
                {
                    continue;
                }

                if (i + 1 < template.Length && template[i + 1] == '{')
                {
                    i++;
                    continue;
                }

                int close = FindClosingBrace(template, i);
                if (close < 0)
                {
                    continue;
                }

                string inner = template[(i + 1)..close];

                if (TryParseChoiceToken(
                    inner,
                    out string name,
                    out IReadOnlyList<string>? options,
                    out bool allowMultiple))
                {
                    CollectChoice(
                        name, options, allowMultiple, definitions, issues, byName, references);
                }
                else if (IsMalformedChoice(inner, out ChoiceIssueKind kind, out string malformed))
                {
                    // 定義として受け付けなかったものの理由を伝える。
                    // 展開はされず書いたまま残るが、貼り付けたあとで気付くのは遅い
                    issues.Add(new ChoiceIssue(kind, malformed));
                }
                else if (TryGetTextTransformBody(inner, out _, out string transformBody))
                {
                    // 検索文字と置換文字はリテラル。展開される「元の値」だけを見る
                    if (TrySplitTextTransformArguments(transformBody, out string source, out _, out _))
                    {
                        CollectChoiceDefinitions(
                            source, definitions, issues, byName, references, depth + 1);
                    }
                }
                else
                {
                    // {calc:{choice:倍率:1|2}*100} のような入れ子も拾う
                    CollectChoiceDefinitions(inner, definitions, issues, byName, references, depth + 1);
                }

                i = close;
            }
        }

        private static void CollectChoice(
            string name,
            IReadOnlyList<string>? options,
            bool allowMultiple,
            List<ChoiceDefinition> definitions,
            List<ChoiceIssue> issues,
            Dictionary<string, ChoiceDefinition> byName,
            List<string> references)
        {
            if (options is null)
            {
                // {choice:名前} — 定義ではなく参照
                references.Add(name);
                return;
            }

            if (byName.ContainsKey(name))
            {
                // 選択肢を合併すると書き手の意図と違う一覧になるため、最初の定義を採る
                issues.Add(new ChoiceIssue(ChoiceIssueKind.Duplicate, name));
                return;
            }

            if (options.Count < MinChoiceOptions)
            {
                issues.Add(new ChoiceIssue(ChoiceIssueKind.TooFewOptions, name));
                return;
            }

            if (options.Count > MaxChoiceOptions)
            {
                issues.Add(new ChoiceIssue(ChoiceIssueKind.TooManyOptions, name));
                return;
            }

            ChoiceDefinition definition = new(name, options, allowMultiple);
            definitions.Add(definition);
            byName[name] = definition;
        }

        /// <summary>
        /// <c>choice:名前</c>（参照）または <c>choice:名前:選択肢|選択肢</c>（定義）を解析する。
        /// <c>choices:</c>（複数形）も同じ文法で、<paramref name="allowMultiple"/> だけが変わる。
        ///
        /// <para>
        /// 最初の <c>:</c> までが名前で、残りが選択肢。名前に <c>:</c> は使えないが、
        /// 2 つめ以降の <c>:</c> は区切りにしないので選択肢には使える。
        /// </para>
        /// </summary>
        /// <param name="options">
        /// 定義なら選択肢の一覧、参照（選択肢を書いていない）なら null。
        /// </param>
        /// <param name="allowMultiple">
        /// <c>choices:</c>（複数形）で書かれていれば true。
        /// 参照の場合は定義側で決まるため、この値に意味は無い。
        /// </param>
        private static bool TryParseChoiceToken(
            string inner,
            out string name,
            out IReadOnlyList<string>? options,
            out bool allowMultiple)
        {
            name = string.Empty;
            options = null;

            // 選択肢には末尾の空白にも意味があるため、前だけを落とす
            if (!TryGetChoiceBody(inner.TrimStart(), out string body, out allowMultiple))
            {
                return false;
            }

            int separator = body.IndexOf(':', StringComparison.Ordinal);

            if (separator < 0)
            {
                name = body.Trim();
                return IsValidChoiceName(name);
            }

            name = body[..separator].Trim();
            if (!IsValidChoiceName(name))
            {
                return false;
            }

            string optionsBody = body[(separator + 1)..];

            // 選択肢の中には差し込みを書けるが、書けないものもある（下記）。
            // 混ざっている場合は定義として受け付けず、書いたままを残す
            // （理由は ChoiceIssueKind.UnsupportedPlaceholderInOption で伝える）
            if (ContainsUnsupportedChoiceOptionToken(optionsBody))
            {
                name = string.Empty;
                return false;
            }

            options = SplitChoiceOptions(optionsBody);
            return true;
        }

        /// <summary>
        /// 選択肢の中に書けない差し込みを含むかどうか。
        /// </summary>
        private static bool ContainsUnsupportedChoiceOptionToken(string optionsBody)
            => ContainsToken(optionsBody, static (name, _) => IsUnsupportedInChoiceOption(name), 0);

        /// <summary>
        /// 選択肢の中では使えない差し込みかどうか。
        ///
        /// <para>
        /// 選択肢は<strong>メニューを組み立てる時点で展開する</strong>。
        /// その時点で値が決まらないもの、決めようとすると筋が通らなくなるものを弾く。
        /// </para>
        /// <list type="bullet">
        /// <item><c>input</c> — 入力を集めるのは選択の<strong>あと</strong>。
        /// メニューに出す時点では値が無く、<c>{input:名前}</c> という文字が並ぶだけになる。
        /// 順序を入れ替えない理由は <c>DESIGN_CHOICE.md</c> §4-3。</item>
        /// <item><c>choice</c> / <c>choices</c> — 自己参照。
        /// 選択肢の中の選択を先に解決しないとメニューを出せないが、それは選択肢の中にいる。</item>
        /// <item><c>seq</c> — 値は決まるが、<see cref="ContainsSequence"/> はテンプレート全体を見るため、
        /// <strong>その選択肢を選ばなくても連番が進む</strong>。
        /// 「選ばなかったのに番号が飛んだ」になるので許さない。</item>
        /// </list>
        /// </summary>
        private static bool IsUnsupportedInChoiceOption(string name) => name.ToLowerInvariant() switch
        {
            "input" or "choice" or "choices" or "seq" => true,
            _ => false,
        };

        /// <summary>
        /// <c>choice:</c> / <c>choices:</c> の接頭辞を取り除く。
        /// </summary>
        /// <remarks>
        /// <c>choices:x</c> は <c>choice:</c> で始まらない（<c>choice</c> の次が <c>s</c> であって <c>:</c> ではない）ため、
        /// 2 つの接頭辞が食い違うことはない。複数形を先に見る必要も無いが、
        /// 読む側が迷わないよう長いほうから確かめる。
        /// </remarks>
        private static bool TryGetChoiceBody(string trimmed, out string body, out bool allowMultiple)
        {
            const string MultiplePrefix = "choices:";
            const string SinglePrefix = "choice:";

            if (trimmed.StartsWith(MultiplePrefix, StringComparison.OrdinalIgnoreCase))
            {
                body = trimmed[MultiplePrefix.Length..];
                allowMultiple = true;
                return true;
            }

            if (trimmed.StartsWith(SinglePrefix, StringComparison.OrdinalIgnoreCase))
            {
                body = trimmed[SinglePrefix.Length..];
                allowMultiple = false;
                return true;
            }

            body = string.Empty;
            allowMultiple = false;
            return false;
        }

        /// <summary>
        /// 定義として受け付けなかった <c>{choice:…}</c> の理由を判定する。
        /// 書いたままを残すのは既存の方針どおりだが、設定画面では理由まで出す。
        /// </summary>
        private static bool IsMalformedChoice(string inner, out ChoiceIssueKind kind, out string name)
        {
            kind = default;
            name = string.Empty;

            if (!TryGetChoiceBody(inner.TrimStart(), out string body, out _))
            {
                return false;
            }

            int separator = body.IndexOf(':', StringComparison.Ordinal);
            name = (separator < 0 ? body : body[..separator]).Trim();

            // {choice:様|御中} のように、名前を書かずに選択肢だけを並べた場合
            if (name.Contains('|', StringComparison.Ordinal))
            {
                kind = ChoiceIssueKind.NameHasPipe;
                return true;
            }

            // {choice:番号:{seq}|なし} のように、選択肢の中に書けない差し込みを混ぜた場合
            if (separator >= 0
                && IsValidChoiceName(name)
                && ContainsUnsupportedChoiceOptionToken(body[(separator + 1)..]))
            {
                kind = ChoiceIssueKind.UnsupportedPlaceholderInOption;
                return true;
            }

            return false;
        }

        /// <summary>
        /// 選択の名前として使えるかどうか。<c>{input:}</c> の制約に加えて
        /// <c>|</c> を禁じる（無名形式のつもりの書き間違いを検出できるようにするため）。
        /// </summary>
        private static bool IsValidChoiceName(string name)
            => name.Length is >= 1 and <= 80
                && name.IndexOfAny(['{', '}', '\r', '\n', '|']) < 0;

        /// <summary>
        /// 選択肢を <c>|</c> で分ける。入れ子の差し込みの中にある <c>|</c> は区切りにしない。
        /// <c>\|</c> は選択肢に含まれる文字として扱う。
        /// </summary>
        /// <remarks>
        /// 前後の空白は落とさない。「, 」のような区切り文字そのものを選ばせたいことがある。
        /// </remarks>
        private static List<string> SplitChoiceOptions(string body)
        {
            List<string> options = [];
            int start = 0;
            int depth = 0;

            for (int i = 0; i < body.Length; i++)
            {
                char c = body[i];

                if (c == '\\' && i + 1 < body.Length)
                {
                    // \| や \\ は後段のデコードまでそのまま持ち越す
                    i++;
                    continue;
                }

                if (c is '{' or '}')
                {
                    if (i + 1 < body.Length && body[i + 1] == c)
                    {
                        i++;
                        continue;
                    }

                    depth += c == '{' ? 1 : -1;
                    continue;
                }

                if (c == '|' && depth == 0)
                {
                    options.Add(DecodeTextTransformArgument(body[start..i], decodeBraces: true));
                    start = i + 1;
                }
            }

            options.Add(DecodeTextTransformArgument(body[start..], decodeBraces: true));
            return options;
        }

        private static bool TryGetNamedValue(string inner, string tokenName, out string value)
        {
            string prefix = tokenName + ":";
            string trimmed = inner.Trim();
            if (!trimmed.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                value = string.Empty;
                return false;
            }

            value = trimmed[prefix.Length..].Trim();
            return value.Length is >= 1 and <= 80
                && value.IndexOfAny(['{', '}', '\r', '\n']) < 0;
        }

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

                    // 差し込んだ値だけに後処理をかける。テンプレート本体（sb へ直接
                    // 積んでいる文字）には触らない。HTML の項目で、利用者が書いたタグは
                    // 生かしたまま、差し込まれた値だけをエスケープするための分岐。
                    //
                    // depth が 0 のときだけ通す。入れ子の展開でも通すと、
                    // 内側で一度エスケープしたものを外側でもう一度エスケープしてしまう。
                    // 式の中（numericOnly）も通さない。数値が実体参照になると計算できない
                    if (depth == 0 && !numericOnly && context.ValueTransform is { } transform)
                    {
                        expanded = transform(expanded);
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
        ///
        /// <para>
        /// <c>}}</c> の読み方は<strong>文法上あいまい</strong>である。
        /// 「内側の差し込みを閉じてから外側も閉じる」とも
        /// 「エスケープした <c>}</c> 1 文字」とも読めて、どちらも成り立つ:
        /// </para>
        /// <code>
        /// {calc:{seq}}          → 閉じる + 閉じる が正しい
        /// {choice:x:a{{b}}c|d}  → エスケープ（選択肢に {b} という文字）が正しい
        /// </code>
        /// <para>
        /// そこで<strong>まずエスケープとして読み、それで最後まで閉じられなかったときだけ</strong>
        /// 閉じ優先で読み直す。読み直しが走るのは既に失敗している場合に限られるので、
        /// いま正しく読めている書き方の結果は変わらない。
        /// </para>
        /// </summary>
        private static int FindClosingBrace(string text, int open)
        {
            int close = ScanClosingBrace(text, open, preferClose: false);
            return close >= 0 ? close : ScanClosingBrace(text, open, preferClose: true);
        }

        /// <param name="preferClose">
        /// <c>}}</c> に出会ったとき、エスケープではなく「1 つ閉じる」として読むかどうか。
        /// 外側がまだ開いている（深さ 1 以上）ときだけ意味を持つ。
        /// </param>
        private static int ScanClosingBrace(string text, int open, bool preferClose)
        {
            int depth = 0;

            for (int i = open; i < text.Length; i++)
            {
                char c = text[i];

                if (c != '{' && c != '}')
                {
                    continue;
                }

                // エスケープはまとめて読み飛ばす。
                // ただし読み直し（preferClose）では、閉じられる位置の }} を閉じとして読む
                if (i + 1 < text.Length
                    && text[i + 1] == c
                    && !(preferClose && c == '}' && depth >= 1))
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

        /// <summary>
        /// 文字列を「そのままコピーされる」形に直す。
        ///
        /// <para>
        /// 波かっこは差し込みの記号なので、コピーしてきた文字列（JSON やソースコードなど）を
        /// そのまま項目にすると、一部が差し込みとして評価されてしまう。
        /// <c>{{</c> <c>}}</c> のエスケープに直しておくと、書いたとおりの文字列がコピーされる。
        /// </para>
        /// </summary>
        public static string EscapeLiteral(string? value)
        {
            if (string.IsNullOrEmpty(value) || !value.AsSpan().ContainsAny('{', '}'))
            {
                return value ?? string.Empty;
            }

            return value.Replace("{", "{{").Replace("}", "}}");
        }

        /// <summary>波かっこを含み、エスケープすると見た目が変わるかどうか。</summary>
        public static bool NeedsEscaping(string? value)
            => !string.IsNullOrEmpty(value) && value.AsSpan().ContainsAny('{', '}');

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

                bool contains;
                if (TryGetTextTransformBody(inner, out _, out string transformBody))
                {
                    // replace の検索文字・置換文字は展開しないので、元の値だけを調べる。
                    contains = TrySplitTextTransformArguments(
                        transformBody,
                        out string source,
                        out _,
                        out _)
                        && ContainsToken(source, matches, depth + 1);
                }
                else
                {
                    // {seq} 自身か、{calc:{seq}*100} のように式の中で使われている場合
                    contains = IsToken(inner, matches)
                        || ContainsToken(inner, matches, depth + 1);
                }

                if (contains)
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
            // 置換後の文字列には半角スペースだけを指定することもあるため、
            // 従来トークン用の Trim() より先に振り分け、末尾の空白を失わないようにする。
            if (TryGetTextTransformBody(inner, out bool useRegex, out string transformBody))
            {
                return ExpandTextTransform(transformBody, original, context, depth, useRegex);
            }

            string trimmed = inner.Trim();

            // app 系は「app:対象:/正規表現/」とコロンを 2 段使うため、
            // 名前:書式の汎用パーサへ渡す前に専用の文法で読む。
            if (TryParseAppToken(trimmed, out string appMember, out string? appPattern))
            {
                return ExpandAppToken(appMember, appPattern, original, context);
            }

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
                    case "input":
                        RejectBase(name, hasBase);
                        RejectOffset(name, hasOffset);
                        if (!TryParseInputToken(trimmed, out string inputName, out string? inputPattern)
                            || context.Inputs is null
                            || !context.Inputs.TryGetValue(inputName, out string? inputValue))
                        {
                            return original;
                        }

                        return inputPattern is null
                            ? inputValue
                            : ExtractByRegex(inputValue, inputPattern, original);

                    case "match":
                        RejectBase(name, hasBase);
                        RejectOffset(name, hasOffset);
                        if (!TryGetNamedValue(trimmed, "match", out string matchName)
                            || context.Matches is null
                            || !context.Matches.TryGetValue(matchName, out string? matchValue))
                        {
                            return original;
                        }

                        return matchValue;

                    // 複数選択（choices）も、選ばれたものを連結した 1 つの文字列として
                    // Choices に入っている。差し込む側から見れば違いは無い
                    case "choice":
                    case "choices":
                        RejectBase(name, hasBase);
                        RejectOffset(name, hasOffset);

                        // 選ぶ前（プレビューやツールチップ）は Choices が null なので、
                        // 書いたままの文字列が残る
                        if (!TryParseChoiceToken(trimmed, out string choiceName, out _, out _)
                            || context.Choices is null
                            || !context.Choices.TryGetValue(choiceName, out string? choiceValue))
                        {
                            return original;
                        }

                        return choiceValue;

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

        /// <summary>
        /// <c>{replace:元の値|検索文字|置換文字}</c> と
        /// <c>{regexreplace:元の値|パターン|置換文字}</c> を展開する。
        /// 元の値だけは入れ子の差し込みとして展開し、残り 2 つはリテラルとして扱う。
        /// </summary>
        private static string ExpandTextTransform(
            string body,
            string original,
            ExpandContext context,
            int depth,
            bool useRegex)
        {
            if (!TrySplitTextTransformArguments(
                    body,
                    out string sourceExpression,
                    out string searchExpression,
                    out string replacementExpression)
                || string.IsNullOrWhiteSpace(sourceExpression))
            {
                return original;
            }

            // エスケープはテンプレート側へだけ適用する。
            // 展開後に処理すると、クリップボード中の C:\new の \n まで改行になってしまう。
            string sourceTemplate = DecodeTextTransformArgument(sourceExpression.Trim(), decodeBraces: false);
            string source = Expand(sourceTemplate, context, depth + 1, false);
            string search = DecodeTextTransformArgument(searchExpression, decodeBraces: true);
            string replacement = DecodeTextTransformArgument(replacementExpression, decodeBraces: true);

            if (string.IsNullOrEmpty(search))
            {
                // 空文字の置換はすべての文字間へ挿入する動作になり、意図しにくいので許可しない。
                return original;
            }

            if (!useRegex)
            {
                return source.Replace(search, replacement, StringComparison.Ordinal);
            }

            try
            {
                return Regex.Replace(
                    source,
                    search,
                    replacement,
                    RegexOptions.CultureInvariant,
                    RegexTimeout);
            }
            catch (ArgumentException)
            {
                // 正規表現または置換文字列が不正なら、書いたまま残して気付けるようにする。
                return original;
            }
            catch (RegexMatchTimeoutException)
            {
                return original;
            }
        }

        /// <summary>文字列変換の名前を判定し、先頭の名前を除いた本体を返す。</summary>
        private static bool TryGetTextTransformBody(
            string inner,
            out bool useRegex,
            out string body)
        {
            string trimmed = inner.TrimStart();
            const string ReplacePrefix = "replace:";
            const string RegexReplacePrefix = "regexreplace:";

            if (trimmed.StartsWith(RegexReplacePrefix, StringComparison.OrdinalIgnoreCase))
            {
                useRegex = true;
                body = trimmed[RegexReplacePrefix.Length..];
                return true;
            }

            if (trimmed.StartsWith(ReplacePrefix, StringComparison.OrdinalIgnoreCase))
            {
                useRegex = false;
                body = trimmed[ReplacePrefix.Length..];
                return true;
            }

            useRegex = false;
            body = string.Empty;
            return false;
        }

        /// <summary>
        /// 文字列変換の 3 引数を、入れ子の差し込み内にある <c>|</c> を避けて分割する。
        /// <c>\|</c> は引数内の文字として扱う。
        /// </summary>
        private static bool TrySplitTextTransformArguments(
            string body,
            out string source,
            out string search,
            out string replacement)
        {
            List<string> arguments = [];
            int start = 0;
            int depth = 0;

            for (int i = 0; i < body.Length; i++)
            {
                char c = body[i];

                if (c == '\\' && i + 1 < body.Length)
                {
                    // \| だけでなく、\\ や \n も後段のデコードまでそのまま保持する。
                    i++;
                    continue;
                }

                if (c is '{' or '}')
                {
                    if (i + 1 < body.Length && body[i + 1] == c)
                    {
                        i++;
                        continue;
                    }

                    depth += c == '{' ? 1 : -1;
                    continue;
                }

                if (c == '|' && depth == 0)
                {
                    arguments.Add(body[start..i]);
                    start = i + 1;
                }
            }

            arguments.Add(body[start..]);
            if (arguments.Count != 3 || depth != 0)
            {
                source = string.Empty;
                search = string.Empty;
                replacement = string.Empty;
                return false;
            }

            source = arguments[0];
            search = arguments[1];
            replacement = arguments[2];
            return true;
        }

        /// <summary>
        /// 文字列変換の引数で使えるエスケープを戻す。
        /// 未知の <c>\x</c> は正規表現へそのまま渡せるよう保持する。
        /// </summary>
        private static string DecodeTextTransformArgument(string value, bool decodeBraces)
        {
            StringBuilder result = new(value.Length);

            for (int i = 0; i < value.Length; i++)
            {
                char c = value[i];
                if (c == '\\' && i + 1 < value.Length)
                {
                    char escaped = value[++i];
                    switch (escaped)
                    {
                        case '|':
                            result.Append('|');
                            break;
                        case 'n':
                            result.Append('\n');
                            break;
                        case 'r':
                            result.Append('\r');
                            break;
                        case 't':
                            result.Append('\t');
                            break;
                        case '\\':
                            result.Append('\\');
                            break;
                        default:
                            result.Append('\\').Append(escaped);
                            break;
                    }

                    continue;
                }

                if (decodeBraces
                    && c is '{' or '}'
                    && i + 1 < value.Length
                    && value[i + 1] == c)
                {
                    result.Append(c);
                    i++;
                    continue;
                }

                result.Append(c);
            }

            return result.ToString();
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
            => TryExtractByRegex(value, pattern, out string extracted) ? extracted : original;

        /// <summary>
        /// メニューを開く直前に記録した前面アプリの値を返す。
        /// 値を取得できなかった場合や正規表現に一致しなかった場合は、
        /// 他の差し込みと同じく書いたままを残して誤りに気付けるようにする。
        /// </summary>
        private static string ExpandAppToken(
            string member,
            string? pattern,
            string original,
            ExpandContext context)
        {
            string? value = member.ToLowerInvariant() switch
            {
                "name" => context.AppName,
                "title" => context.AppTitle,
                _ => null,
            };

            if (value is null)
            {
                return original;
            }

            return pattern is null ? value : ExtractByRegex(value, pattern, original);
        }

        /// <summary>
        /// 正規表現に最初に一致した部分（キャプチャがあれば最初のキャプチャ）を返す。
        /// 入力キャプチャ時の検証と、テンプレート展開で同じ規則を使うための共通処理。
        /// </summary>
        internal static bool TryExtractByRegex(string value, string pattern, out string extracted)
        {
            extracted = string.Empty;
            if (string.IsNullOrEmpty(pattern))
            {
                return false;
            }

            try
            {
                Match m = Regex.Match(value, pattern, RegexOptions.CultureInvariant, RegexTimeout);

                if (!m.Success)
                {
                    return false;
                }

                extracted = m.Groups.Count > 1 && m.Groups[1].Success ? m.Groups[1].Value : m.Value;
                return true;
            }
            catch (Exception)
            {
                // 正規表現が誤っている、または照合が長引いて打ち切られた場合
                return false;
            }
        }

        /// <summary>入力に書かれた正規表現を、キャプチャ開始前に検証する。</summary>
        internal static bool TryValidateInputPattern(string pattern, out string error)
        {
            try
            {
                _ = new Regex(pattern, RegexOptions.CultureInvariant, RegexTimeout);
                error = string.Empty;
                return true;
            }
            catch (ArgumentException ex)
            {
                error = ex.Message;
                return false;
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
