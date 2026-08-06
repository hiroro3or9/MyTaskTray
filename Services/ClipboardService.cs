using System.Threading;
using MyTaskTray.Models;

namespace MyTaskTray.Services
{
    /// <summary>
    /// クリップボードの読み書きを行う。
    /// 他プロセスがクリップボードをロックしている場合があるため、失敗時は少し待って再試行する。
    /// </summary>
    public static class ClipboardService
    {
        private const int MaxAttempts = 5;
        private const int RetryDelayMs = 80;

        /// <summary>
        /// 指定した文字列をプレーンテキストとしてクリップボードにコピーする。成功したら true。
        /// </summary>
        public static bool TryCopy(string text) => TryCopy(text, ClipFormat.Plain);

        /// <summary>
        /// 指定した文字列を、形式に応じてクリップボードにコピーする。成功したら true。
        ///
        /// <para>
        /// <see cref="ClipFormat.Plain"/> 以外では、プレーンテキストに加えて
        /// 書式付きの形式も一緒に載せる。どれが使われるかは貼り付け先が選ぶ。
        /// </para>
        /// </summary>
        public static bool TryCopy(string text, ClipFormat format)
        {
            // 空の DataObject を載せると「空文字がコピーされた状態」と
            // 「クリップボードが空の状態」が食い違い、{clip} 側の判定が狂う。
            // 従来どおりクリアする
            if (string.IsNullOrEmpty(text))
            {
                return TryRun(System.Windows.Clipboard.Clear);
            }

            System.Windows.DataObject data = new();

            // どの形式にも必ずプレーンテキストを載せる。
            // これが無いと、書式を扱えない相手に何も渡らない
            data.SetData(System.Windows.DataFormats.UnicodeText, text);

            string? htmlFragment = format switch
            {
                // 書いた内容がそのまま HTML
                ClipFormat.Html => text,

                // Markdown として解釈して HTML に変換する。
                // 変換できなかった場合は null が返り、プレーンテキストだけで続行する
                ClipFormat.Markdown => MarkdownRenderer.ToHtml(text),

                _ => null,
            };

            if (htmlFragment is not null)
            {
                // 文字列のまま渡す。.NET が UTF-8 で書き、NUL 終端も付けてくれる。
                // MemoryStream でバイト列を渡すと終端が付かず、末尾 1 バイトが落ちる
                data.SetData(System.Windows.DataFormats.Html, CfHtml.Build(htmlFragment));
            }

            // copy: true を付けないと、このアプリを終了した時点で中身が消える
            return TryRun(() => System.Windows.Clipboard.SetDataObject(data, copy: true));
        }

        /// <summary>
        /// 形式に応じた「差し込んだ値の後処理」を返す。
        /// <c>TemplateEngine.Expand</c> の <c>valueTransform</c> へ渡して使う。
        /// 後処理が要らない形式では null を返す。
        ///
        /// <para>
        /// テンプレート本体ではなく差し込まれた値だけに効かせるのが要点。
        /// HTML の項目で、利用者が書いたタグは生かしたまま、
        /// <c>{input:…}</c> や <c>{clip}</c> に入った記号だけをエスケープする。
        /// </para>
        /// </summary>
        public static Func<string, string>? GetValueTransform(ClipFormat format) => format switch
        {
            // 書いた内容がそのまま HTML になるので、差し込む値は自分でエスケープする必要がある
            ClipFormat.Html => CfHtml.Escape,

            // Markdown では何もしない。差し込まれた値は Markdown の本文として扱われ、
            // HTML への変換時に Markdig が正しくエスケープする。
            // ここでエスケープすると、プレーンテキスト側に &amp; が出てしまう
            _ => null,
        };

        /// <summary>
        /// クリップボードの文字列を読み取る。
        /// 文字列が入っていない場合や読み取れなかった場合は空文字列を返す。
        /// <c>{clip}</c> の差し込みで使う。
        /// </summary>
        public static string GetText()
        {
            string result = string.Empty;

            TryRun(() =>
            {
                if (System.Windows.Clipboard.ContainsText())
                {
                    result = System.Windows.Clipboard.GetText() ?? string.Empty;
                }
            });

            return result;
        }

        /// <summary>
        /// クリップボードに文字列が入っていそうかどうか。
        ///
        /// <para>
        /// 中身は読まないので <see cref="GetText"/> より軽く、他アプリのコピー操作を妨げにくい。
        /// メニューの項目を有効にするかどうかの判定に使う。
        /// 判定できなかった場合は true を返す。押せなくして「なぜか使えない」となるより、
        /// 押した結果を通知で伝えるほうが分かりやすい。
        /// </para>
        /// </summary>
        public static bool HasText()
        {
            try
            {
                return System.Windows.Clipboard.ContainsText();
            }
            catch (Exception)
            {
                return true;
            }
        }

        private static bool TryRun(Action action)
        {
            for (int attempt = 1; attempt <= MaxAttempts; attempt++)
            {
                try
                {
                    action();
                    return true;
                }
                catch (Exception)
                {
                    if (attempt == MaxAttempts)
                    {
                        return false;
                    }

                    Thread.Sleep(RetryDelayMs);
                }
            }

            return false;
        }
    }
}
