using Markdig;

namespace MyTaskTray.Services
{
    /// <summary>
    /// Markdown を HTML へ変換する。
    ///
    /// <para>
    /// Markdig への依存をこのファイル 1 つに閉じ込めている。
    /// 別のライブラリへ差し替えたくなった場合も、書き換えるのはここだけで済む。
    /// （設計メモでは <c>IMarkdownRenderer</c> を切る案にしていたが、
    /// 実装が 1 つしかなく、このアプリの他のサービスもすべて static なので、
    /// インターフェースは置かずファイル単位の分離にとどめた。）
    /// </para>
    /// </summary>
    public static class MarkdownRenderer
    {
        /// <summary>
        /// 変換の設定。組み立てに費用がかかるため使い回す。
        ///
        /// <para>
        /// <c>UseAdvancedExtensions</c> で表・脚注・打ち消し線・タスクリストなどを有効にする。
        /// 「可能な限り対応する」方針なので、標準の CommonMark に絞らず拡張まで入れている。
        /// </para>
        /// </summary>
        private static readonly MarkdownPipeline Pipeline =
            new MarkdownPipelineBuilder()
                .UseAdvancedExtensions()
                .Build();

        /// <summary>
        /// Markdown を HTML の断片へ変換する。変換できなかった場合は null。
        ///
        /// <para>
        /// null を返した場合、呼び出し元は HTML を載せずにプレーンテキストだけで続行する。
        /// 書式が付かないだけで、コピー自体は成立させる。
        /// </para>
        /// </summary>
        public static string? ToHtml(string markdown)
        {
            if (string.IsNullOrEmpty(markdown))
            {
                return null;
            }

            try
            {
                return Markdown.ToHtml(markdown, Pipeline);
            }
            catch (Exception)
            {
                // Markdig は任意の文字列を受け付ける作りだが、
                // ここで落ちるとコピーそのものができなくなる。
                // 書式を諦めてプレーンテキストで通すほうが利用者の損が小さい
                return null;
            }
        }
    }
}
