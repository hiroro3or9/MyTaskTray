using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace MyTaskTray.Services
{
    /// <summary>
    /// クリップボード文字列へ適用する、UI に依存しない組み込み変換。
    /// 判定と変換で同じ規則を使えるよう、アクション本体から分離する。
    /// </summary>
    internal static class ClipboardTextActions
    {
        private const int MaxJsonLength = 1024 * 1024;

        private static readonly JsonDocumentOptions JsonDocumentOptions = new()
        {
            AllowTrailingCommas = true,
            CommentHandling = JsonCommentHandling.Skip,
            MaxDepth = 64,
        };

        private static readonly JsonSerializerOptions MinifiedJsonOptions = new()
        {
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            WriteIndented = false,
        };

        private static readonly JsonSerializerOptions FormattedJsonOptions = new()
        {
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            WriteIndented = true,
        };

        /// <summary>空、または空白文字だけで構成された行を含むか。</summary>
        public static bool HasBlankLines(string? text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return false;
            }

            int start = 0;
            while (start < text.Length)
            {
                FindLine(text, start, out int contentEnd, out int nextLine);
                if (IsWhiteSpaceOnly(text.AsSpan(start, contentEnd - start)))
                {
                    return true;
                }

                start = nextLine;
            }

            return false;
        }

        /// <summary>
        /// 空、または空白文字だけで構成された行を取り除く。
        /// 残す行の改行コードと、末尾改行の有無は元の文字列どおりに保つ。
        /// </summary>
        public static string RemoveBlankLines(string text)
        {
            ArgumentNullException.ThrowIfNull(text);

            StringBuilder result = new(text.Length);
            bool removed = false;
            int start = 0;
            while (start < text.Length)
            {
                FindLine(text, start, out int contentEnd, out int nextLine);
                if (IsWhiteSpaceOnly(text.AsSpan(start, contentEnd - start)))
                {
                    removed = true;
                }
                else
                {
                    result.Append(text.AsSpan(start, nextLine - start));
                }

                start = nextLine;
            }

            return removed ? result.ToString() : text;
        }

        /// <summary>オブジェクトまたは配列の JSON として変換できるか。</summary>
        public static bool IsJsonObjectOrArray(string? text)
        {
            JsonDocument? document = TryParseJson(text);
            if (document is null)
            {
                return false;
            }

            using (document)
            {
                return document.RootElement.ValueKind is JsonValueKind.Object or JsonValueKind.Array;
            }
        }

        /// <summary>
        /// オブジェクトまたは配列の JSON を、1 行またはインデント付きへ変換する。
        /// コメントと末尾カンマは入力として許可するが、出力は標準 JSON へ正規化する。
        /// </summary>
        public static bool TryFormatJson(string? text, bool indented, out string result)
        {
            result = string.Empty;
            JsonDocument? document = TryParseJson(text);
            if (document is null)
            {
                return false;
            }

            using (document)
            {
                if (document.RootElement.ValueKind is not (JsonValueKind.Object or JsonValueKind.Array))
                {
                    return false;
                }

                result = JsonSerializer.Serialize(
                    document.RootElement,
                    indented ? FormattedJsonOptions : MinifiedJsonOptions);
                return true;
            }
        }

        private static JsonDocument? TryParseJson(string? text)
        {
            string value = text?.Trim() ?? string.Empty;
            if (value.Length is 0 or > MaxJsonLength || value[0] is not ('{' or '['))
            {
                return null;
            }

            try
            {
                return JsonDocument.Parse(value, JsonDocumentOptions);
            }
            catch (JsonException)
            {
                return null;
            }
        }

        private static void FindLine(string text, int start, out int contentEnd, out int nextLine)
        {
            contentEnd = start;
            while (contentEnd < text.Length && text[contentEnd] is not ('\r' or '\n'))
            {
                contentEnd++;
            }

            nextLine = contentEnd;
            if (nextLine >= text.Length)
            {
                return;
            }

            if (text[nextLine] == '\r'
                && nextLine + 1 < text.Length
                && text[nextLine + 1] == '\n')
            {
                nextLine += 2;
            }
            else
            {
                nextLine++;
            }
        }

        private static bool IsWhiteSpaceOnly(ReadOnlySpan<char> value)
        {
            foreach (char character in value)
            {
                if (!char.IsWhiteSpace(character))
                {
                    return false;
                }
            }

            return true;
        }
    }
}
