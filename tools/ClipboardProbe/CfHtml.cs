using System;
using System.Collections.Generic;
using System.Text;

namespace ClipboardProbe;

/// <summary>
/// クリップボードの HTML 形式（CF_HTML）を組み立てる。
///
/// <para>
/// DESIGN_RICH_COPY.md §4 の実装候補そのもの。ここで確かめた形をそのまま本体へ持っていく。
/// .NET はこのヘッダを作ってくれないので自前で組み立てる必要がある。
/// </para>
/// </summary>
internal static class CfHtml
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

    /// <summary>HTML の断片を CF_HTML の形へ包む。</summary>
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
    /// わざと文字数でオフセットを数えた、壊れた CF_HTML を作る。
    /// 「日本語で必ず踏む」という主張が本当かどうかを実機で見るために使う。
    /// </summary>
    public static string BuildBroken(string fragment)
    {
        int headerLength = string.Format(HeaderTemplate, 0, 0, 0, 0).Length;

        int startHtml = headerLength;
        int startFragment = startHtml + Pre.Length;
        int endFragment = startFragment + fragment.Length;
        int endHtml = endFragment + Post.Length;

        return string.Format(HeaderTemplate, startHtml, endHtml, startFragment, endFragment)
            + Pre + fragment + Post;
    }

    /// <summary>
    /// 組み立てた CF_HTML を、ヘッダのオフセットどおりに切り出せるか自己検査する。
    /// クリップボードへ載せる前にここで落ちるなら、貼り付け先を疑う必要はない。
    /// </summary>
    public static string SelfCheck(string cfHtml, string expectedFragment)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(cfHtml);

        int startHtml = ReadOffset(cfHtml, "StartHTML:");
        int endHtml = ReadOffset(cfHtml, "EndHTML:");
        int startFragment = ReadOffset(cfHtml, "StartFragment:");
        int endFragment = ReadOffset(cfHtml, "EndFragment:");

        if (endHtml > bytes.Length || endFragment > bytes.Length)
        {
            return $"NG: オフセットが全長({bytes.Length})を超えている";
        }

        string html = Encoding.UTF8.GetString(bytes, startHtml, endHtml - startHtml);
        string fragment = Encoding.UTF8.GetString(
            bytes, startFragment, endFragment - startFragment);

        List<string> problems = [];

        if (fragment != expectedFragment)
        {
            problems.Add($"Fragment がずれている -> 「{fragment}」");
        }

        if (!html.StartsWith("<html>", StringComparison.Ordinal))
        {
            problems.Add("HTML 部が <html> で始まっていない");
        }

        if (!html.EndsWith("</html>", StringComparison.Ordinal))
        {
            problems.Add("HTML 部が </html> で終わっていない");
        }

        return problems.Count == 0
            ? $"OK: 全長 {bytes.Length} バイト / Fragment {endFragment - startFragment} バイト"
            : "NG: " + string.Join(" / ", problems);
    }

    private static int ReadOffset(string cfHtml, string key)
    {
        int start = cfHtml.IndexOf(key, StringComparison.Ordinal);
        if (start < 0)
        {
            return -1;
        }

        start += key.Length;
        int end = cfHtml.IndexOf('\r', start);
        return int.Parse(cfHtml[start..end]);
    }
}
