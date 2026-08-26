namespace MyTaskTray.Services
{
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

    /// <summary><c>{choice:…}</c> と <c>{choices:…}</c> の検出・解析・解決。</summary>
    public static partial class TemplateEngine
    {
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
    }
}
