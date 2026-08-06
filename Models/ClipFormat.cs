using System.Text.Json.Serialization;

namespace MyTaskTray.Models
{
    /// <summary>
    /// コピーするときに、クリップボードへどの形式で載せるか。
    ///
    /// <para>
    /// Windows のクリップボードは 1 回のコピーで複数の形式を同時に載せられ、
    /// 貼り付け先のアプリが自分の扱える形式を選ぶ。ここではその候補の組み立て方を決める。
    /// 詳細は DESIGN_RICH_COPY.md を参照。
    /// </para>
    /// </summary>
    /// <remarks>
    /// 設定ファイルには数値ではなく <c>"Html"</c> のような名前で書き出す。
    /// 手で編集できることを前提にしているため（<see cref="ClipboardMatchKind"/> と揃える）。
    /// </remarks>
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public enum ClipFormat
    {
        /// <summary>
        /// プレーンテキストだけを載せる。従来どおりの動き。
        /// 設定ファイルに書かれていない場合はこれになる。
        /// </summary>
        Plain = 0,

        /// <summary>
        /// コピー文字列を HTML として扱い、プレーンテキストと一緒に HTML も載せる。
        /// Word や Outlook、Slack へ貼ると書式が付く。
        /// メモ帳のようにプレーンテキストしか扱えない相手には、書いた HTML がそのまま入る。
        /// </summary>
        Html = 1,

        /// <summary>
        /// コピー文字列を Markdown として解釈し、変換した HTML も一緒に載せる。
        /// Word や Slack へ貼ると見出しや箇条書きになり、
        /// メモ帳やエディタへ貼ると Markdown の元テキストがそのまま入る。
        /// </summary>
        Markdown = 2,

        // Table はこれから。値を先に予約すると、
        // 未対応のまま設定ファイルへ書けてしまい「選べるのに効かない」状態になるため、
        // 実装と同時に足す。
    }
}
