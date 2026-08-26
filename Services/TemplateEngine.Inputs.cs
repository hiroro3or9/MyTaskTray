namespace MyTaskTray.Services
{
    /// <summary>
    /// 1 回のコピー操作で受け取る入力。
    /// <paramref name="Patterns"/> があれば、すべての正規表現に一致する文字列だけを受け付ける。
    /// </summary>
    internal sealed record InputCaptureDefinition(
        string Name,
        IReadOnlyList<string> Patterns);

    /// <summary><c>{input:…}</c> の検出と解析。</summary>
    public static partial class TemplateEngine
    {
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
    }
}
