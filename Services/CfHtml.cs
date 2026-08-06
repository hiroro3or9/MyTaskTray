using System.Text;

namespace MyTaskTray.Services
{
    /// <summary>
    /// クリップボードの HTML 形式（CF_HTML）を組み立てる。
    ///
    /// <para>
    /// .NET はこのヘッダを作ってくれないため自前で組み立てる。
    /// tools/ClipboardProbe で実機確認したものをそのまま持ってきている。
    /// 経緯は DESIGN_RICH_COPY.md の §4。
    /// </para>
    /// </summary>
    public static class CfHtml
    {
        /// <summary>
        /// オフセットは 8 桁固定にする。
        /// こうしておくと、ダミー値を入れたヘッダと本番のヘッダが同じ長さになり、
        /// 「ヘッダの長さを知るためにヘッダを作る」という循環を避けられる。
        /// </summary>
        private const string HeaderTemplate =
            "Version:0.9\r\n"
            + "StartHTML:{0:00000000}\r\n"
            + "EndHTML:{1:00000000}\r\n"
            + "StartFragment:{2:00000000}\r\n"
            + "EndFragment:{3:00000000}\r\n";

        private const string Pre = "<html><body>\r\n<!--StartFragment-->";
        private const string Post = "<!--EndFragment-->\r\n</body></html>";

        /// <summary>
        /// HTML の断片を CF_HTML の形へ包む。
        ///
        /// <para>
        /// 戻り値は <c>System.Windows.DataObject.SetData</c> へ
        /// <b>文字列のまま</b>渡すこと。実機で確認したところ、.NET はこれを UTF-8 で書き、
        /// CF_HTML の慣習である NUL 終端まで付けてくれる。
        /// <c>MemoryStream</c> でバイト列を渡すと終端が付かず、
        /// 読み戻したときに末尾 1 バイトが落ちる。
        /// </para>
        /// </summary>
        public static string Build(string fragment)
        {
            // 長さは必ず UTF-8 のバイト数で数える。
            // string.Length（文字数）で数えると日本語が入った瞬間にずれる
            int headerLength = Encoding.UTF8.GetByteCount(
                string.Format(HeaderTemplate, 0, 0, 0, 0));

            int startHtml = headerLength;
            int startFragment = startHtml + Encoding.UTF8.GetByteCount(Pre);
            int endFragment = startFragment + Encoding.UTF8.GetByteCount(fragment);
            int endHtml = endFragment + Encoding.UTF8.GetByteCount(Post);

            return string.Format(HeaderTemplate, startHtml, endHtml, startFragment, endFragment)
                + Pre + fragment + Post;
        }

        /// <summary>
        /// HTML の中に文字列を埋め込めるようにエスケープする。
        ///
        /// <para>
        /// テンプレート本体ではなく、<b>差し込まれた値だけ</b>に使う。
        /// <c>{input:タイトル}</c> に <c>A &amp; B</c> と入力しただけで
        /// 壊れた HTML ができるのを防ぐためのもの。
        /// テンプレート本体に使うと、利用者が書いたタグまで文字になってしまう。
        /// </para>
        /// </summary>
        public static string Escape(string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return string.Empty;
            }

            StringBuilder sb = new(value.Length + 16);

            foreach (char c in value)
            {
                switch (c)
                {
                    // & を最初に置き換える。あとから置き換えると、
                    // 自分で書いた &lt; の & まで二重に置き換わる
                    case '&': sb.Append("&amp;"); break;
                    case '<': sb.Append("&lt;"); break;
                    case '>': sb.Append("&gt;"); break;
                    case '"': sb.Append("&quot;"); break;
                    case '\'': sb.Append("&#39;"); break;
                    default: sb.Append(c); break;
                }
            }

            return sb.ToString();
        }
    }
}
