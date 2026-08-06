// 暗黙の using に頼らず明示する。
// このツールは本体とは別のプロジェクトだが、本体のビルドに巻き込まれる形で
// コンパイルされた場合でも通るようにしておく（tools\ は本体から除外済み）。
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Media.Imaging;

namespace ClipboardProbe;

/// <summary>
/// DESIGN_RICH_COPY.md §9「実機で確認が必要なこと」を手で試すためのツール。
///
/// <para>
/// 本体には手を入れずに、判断に必要な事実だけを先に集めるのが目的。
/// ここで分かったことを設計メモへ書き戻してから、本体の実装に入る。
/// </para>
/// </summary>
internal static class Program
{
    /// <summary>
    /// クリップボードは STA スレッドからしか触れない。
    /// これを忘れると、原因の分かりにくい例外で落ちる。
    /// </summary>
    [STAThread]
    private static void Main()
    {
        Console.OutputEncoding = Encoding.UTF8;

        Console.WriteLine("ClipboardProbe — DESIGN_RICH_COPY.md §9 の確認用");
        Console.WriteLine();

        // クリップボードへ載せる前に、組み立てたものが自己矛盾していないかを確かめる。
        // ここで落ちるなら、貼り付け先の問題ではない
        RunCfHtmlSelfCheck();

        while (true)
        {
            Console.WriteLine();
            Console.WriteLine("──────────────────────────────────────────────");
            Console.WriteLine("  1. CF_HTML を載せる（string で渡す）        [§9-1]");
            Console.WriteLine("  2. CF_HTML を載せる（UTF-8 バイト列で渡す）  [§9-1]");
            Console.WriteLine("  3. CF_HTML を載せる（わざと文字数で数えた）  [§4-1 の裏取り]");
            Console.WriteLine("  4. TSV と HTML を同時に載せる                [§9-3]");
            Console.WriteLine("  5. いま載っている形式を一覧する              [§9-2]");
            Console.WriteLine("  6. 形式の中身をファイルへ書き出す            [§9-2/5/6]");
            Console.WriteLine("  7. 捕まえて → クリア → 復元（往復）          [§9-9]");
            Console.WriteLine("  8. 1 つの形式を 4 通りの方法で読み比べる      [HTML Format が null の件]");
            Console.WriteLine("  9. CF_HTML を載せる（バイト列 + NUL 終端）    [§4-3 の裏取り]");
            Console.WriteLine("  0. 終了");
            Console.WriteLine("──────────────────────────────────────────────");
            Console.Write("> ");

            switch (Console.ReadLine()?.Trim())
            {
                case "1": PutHtml(asStream: false, broken: false); break;
                case "2": PutHtml(asStream: true, broken: false); break;
                case "3": PutHtml(asStream: true, broken: true); break;
                case "4": PutTable(); break;
                case "5": ListFormats(); break;
                case "6": DumpFormats(); break;
                case "7": RoundTrip(); break;
                case "8": CompareReadPaths(); break;
                case "9": PutHtml(asStream: true, broken: false, addNul: true); break;
                case "0" or "q" or null: return;
                default: Console.WriteLine("番号を入力してください。"); break;
            }
        }
    }

    // ------------------------------------------------------------------
    // 出力側
    // ------------------------------------------------------------------

    /// <summary>確認に使う断片。日本語を必ず含める（ASCII だけだと通ってしまう）。</summary>
    private const string SampleFragment =
        "<h1>2026/08/04 定例ミーティング 議事録</h1>"
        + "<ul><li>決定事項: <strong>高</strong>優先で対応</li><li>宿題: 雑務の棚卸し</li></ul>";

    private const string SamplePlain =
        "# 2026/08/04 定例ミーティング 議事録\r\n\r\n- 決定事項: **高**優先で対応\r\n- 宿題: 雑務の棚卸し";

    private static void RunCfHtmlSelfCheck()
    {
        Console.WriteLine("[起動時の自己検査] ヘッダのオフセットで正しく切り出せるか");

        string good = CfHtml.Build(SampleFragment);
        Console.WriteLine($"  バイト数で計算 : {CfHtml.SelfCheck(good, SampleFragment)}");

        string bad = CfHtml.BuildBroken(SampleFragment);
        Console.WriteLine($"  文字数で計算   : {CfHtml.SelfCheck(bad, SampleFragment)}");
        Console.WriteLine("  （下が NG になるのが期待どおり。NG にならないなら断片に日本語が足りない）");
    }

    private static void PutHtml(bool asStream, bool broken, bool addNul = false)
    {
        string cfHtml = broken
            ? CfHtml.BuildBroken(SampleFragment)
            : CfHtml.Build(SampleFragment);

        DataObject data = new();
        data.SetData(DataFormats.UnicodeText, SamplePlain);

        if (asStream)
        {
            // ヘッダのオフセットを UTF-8 バイトで数えている以上、
            // 書き込むときも UTF-8 でなければ意味がない。それを明示する渡し方。
            //
            // ただし実測では、この渡し方だと NUL 終端が付かず、
            // 読み戻したときに末尾 1 バイトが落ちた（CF_HTML は NUL 終端が慣習）。
            // addNul はその仮説を確かめるための版
            byte[] payload = Encoding.UTF8.GetBytes(cfHtml);
            if (addNul)
            {
                payload = [.. payload, 0];
            }

            data.SetData(DataFormats.Html, new MemoryStream(payload));
        }
        else
        {
            // .NET に任せる渡し方。実測ではこちらが NUL 終端まで付けてくれた
            data.SetData(DataFormats.Html, cfHtml);
        }

        if (!TrySetDataObject(data))
        {
            return;
        }

        string how = asStream
            ? (addNul ? "UTF-8 バイト列 + NUL 終端" : "UTF-8 バイト列")
            : "string";

        Console.WriteLine();
        Console.WriteLine($"載せました（{how}{(broken ? " / わざと壊した版" : string.Empty)}）。");
        Console.WriteLine($"組み立てた CF_HTML は {Encoding.UTF8.GetByteCount(cfHtml)} バイト。");
        Console.WriteLine("  メニュー 8 で読み戻すと、Win32 側と WPF 側の差が見えます。");
        Console.WriteLine("次を順に貼り付けて、結果を控えてください:");
        Console.WriteLine("  Word    -> 見出しと箇条書きになるか。日本語が化けないか");
        Console.WriteLine("  メモ帳  -> Markdown の元テキストが入るか（プレーン側が選ばれるか）");
        Console.WriteLine("  ブラウザのテキストエリア -> プレーン側が入るか");
        Console.WriteLine("  Excel   -> 何が起きるか（1 セルか、複数セルか）");
    }

    private static void PutTable()
    {
        // タブ区切り。Excel はこれをセルに割ってくれるはず
        string tsv = string.Join("\r\n",
            "項目\t担当\t期限",
            "設計\t山田\t2026/08/11",
            "実装\t佐藤\t2026/08/18");

        string html =
            "<table border=\"1\"><tr><th>項目</th><th>担当</th><th>期限</th></tr>"
            + "<tr><td>設計</td><td>山田</td><td>2026/08/11</td></tr>"
            + "<tr><td>実装</td><td>佐藤</td><td>2026/08/18</td></tr></table>";

        DataObject data = new();
        data.SetData(DataFormats.UnicodeText, tsv);
        data.SetData(DataFormats.Html, new MemoryStream(Encoding.UTF8.GetBytes(CfHtml.Build(html))));

        if (!TrySetDataObject(data))
        {
            return;
        }

        Console.WriteLine();
        Console.WriteLine("載せました。ここが §9-3 の分かれ目です:");
        Console.WriteLine("  Excel で貼る -> 3 列 3 行のセルに割れたか、1 セルに収まったか");
        Console.WriteLine("    セルに割れた   : TSV が勝っている。設計どおり");
        Console.WriteLine("    1 セルに入った : HTML が勝っている。表の項目では HTML を載せない設定が要る");
        Console.WriteLine("  Word で貼る  -> 罫線付きの表になるか");
    }

    // ------------------------------------------------------------------
    // 入力側
    // ------------------------------------------------------------------

    /// <summary>1 つの形式について読み取れたこと。</summary>
    private sealed record Probed(
        string Format,
        FormatVerdict Verdict,
        string TypeName,
        long Size,
        long ElapsedMs,
        string Note,
        object? Value);

    private static void ListFormats()
    {
        List<Probed>? probed = ProbeClipboard();
        if (probed is null)
        {
            return;
        }

        Console.WriteLine();
        Console.WriteLine($"{"形式",-40} {"判定",-16} {"型",-22} {"大きさ",12} {"ms",6}  備考");
        Console.WriteLine(new string('-', 118));

        foreach (Probed p in probed)
        {
            Console.WriteLine(
                $"{Truncate(p.Format, 40),-40} {FormatCatalog.Label(p.Verdict),-16} "
                + $"{Truncate(p.TypeName, 22),-22} {FormatSize(p.Size),12} {p.ElapsedMs,6}  {p.Note}");
        }

        Summarize(probed);
    }

    private static void Summarize(List<Probed> probed)
    {
        long total = probed.Where(p => p.Verdict == FormatVerdict.Keep).Sum(p => p.Size);
        List<Probed> unknown = [.. probed.Where(p => p.Verdict == FormatVerdict.Unknown)];
        List<Probed> tooBig = [.. probed.Where(p => p.Size > 4L * 1024 * 1024)];
        List<Probed> failed = [.. probed.Where(p => p.Value is null)];

        Console.WriteLine();
        Console.WriteLine($"形式の数     : {probed.Count}");
        Console.WriteLine($"保存する合計 : {FormatSize(total)}（上限案 16MB に対して）");
        Console.WriteLine($"読み取り時間 : {probed.Sum(p => p.ElapsedMs)} ms");

        if (tooBig.Count > 0)
        {
            Console.WriteLine($"上限超え(4MB): {string.Join(", ", tooBig.Select(p => p.Format))}");
        }

        if (failed.Count > 0)
        {
            // §6-4 の遅延レンダリング。コピー元が終了していると、ここに並ぶはず
            Console.WriteLine($"読めなかった : {string.Join(", ", failed.Select(p => p.Format))}");
        }

        if (unknown.Count > 0)
        {
            Console.WriteLine();
            Console.WriteLine("── 未分類の形式（許可リストに入れるか判断が要る） ──");
            foreach (Probed entry in unknown)
            {
                Console.WriteLine($"  {entry.Format}  ({FormatSize(entry.Size)}, {entry.TypeName})");
            }
        }
    }

    /// <summary>いま載っているものを全部読み、形式ごとの結果を返す。</summary>
    private static List<Probed>? ProbeClipboard()
    {
        IDataObject? data = null;
        if (!TryRun(() => data = Clipboard.GetDataObject()) || data is null)
        {
            Console.WriteLine("クリップボードを読み取れませんでした。");
            return null;
        }

        // autoConvert: false が肝。true にすると自動変換で合成された形式まで並び、
        // 「元のアプリが実際に載せたもの」が分からなくなる
        string[] formats;
        try
        {
            formats = data.GetFormats(autoConvert: false);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"形式の一覧を取得できませんでした: {ex.Message}");
            return null;
        }

        // §6-7。これが載っていたら中身を読みにいかない
        IReadOnlyList<string> markers = FormatCatalog.FindExclusionMarkers(formats);
        if (markers.Count > 0)
        {
            Console.WriteLine();
            Console.WriteLine("保存を禁じる印が載っています。捕獲を中止します。");
            foreach (string marker in markers)
            {
                Console.WriteLine($"  {marker}");
            }

            Console.WriteLine("（パスワード管理ソフトなどが付ける印。本体でも同じく断る）");
            return null;
        }

        List<Probed> result = [];

        foreach (string format in formats)
        {
            Stopwatch sw = Stopwatch.StartNew();
            object? value = null;
            string note = string.Empty;

            try
            {
                // 遅延レンダリングの場合、この 1 行でコピー元のプロセスが実データを作る。
                // 元アプリが終了していると null が返るか例外になる
                value = data.GetData(format, autoConvert: false);
            }
            catch (Exception ex)
            {
                note = $"例外: {ex.GetType().Name}";
            }

            sw.Stop();

            if (value is null && note.Length == 0)
            {
                note = "null が返った";
            }

            (string typeName, long size, string detail) = Describe(value);
            if (detail.Length > 0)
            {
                note = note.Length > 0 ? $"{note} / {detail}" : detail;
            }

            result.Add(new Probed(
                format,
                FormatCatalog.Judge(format),
                typeName,
                size,
                sw.ElapsedMilliseconds,
                note,
                value));
        }

        return result;
    }

    /// <summary>形式ごとに返ってくる型がばらばらなので、ここで吸収する（§6-2）。</summary>
    private static (string TypeName, long Size, string Detail) Describe(object? value) => value switch
    {
        null => ("(なし)", 0L, string.Empty),
        string s => ("string", Encoding.UTF8.GetByteCount(s), Preview(s)),
        string[] paths => ("string[]", paths.Sum(p => (long)p.Length), $"{paths.Length} 件"),
        MemoryStream ms => ("MemoryStream", ms.Length, string.Empty),
        Stream st => (st.GetType().Name, SafeLength(st), string.Empty),
        byte[] bytes => ("byte[]", (long)bytes.Length, string.Empty),

        // 画像は「そのままのバイト数」と「PNG にしたときのバイト数」が桁違いになる。
        // 保存するなら PNG 一択なので、判断できるよう両方出す
        BitmapSource bmp => ("BitmapSource", PngSize(bmp),
            $"{bmp.PixelWidth}x{bmp.PixelHeight} / 無圧縮なら {FormatSize(RawSize(bmp))}"),

        bool b => ("bool", 1L, b.ToString()),

        // System.Drawing の Metafile など、参照していない型はここに来る。
        // 型名だけ出して、扱うかどうかは実測を見てから決める
        _ => (value.GetType().Name, 0L, "（このツールでは中身を読んでいない）"),
    };

    /// <summary>PNG に圧縮したときのバイト数。保存するならこの大きさになる。</summary>
    private static long PngSize(BitmapSource bmp)
    {
        try
        {
            PngBitmapEncoder encoder = new();
            encoder.Frames.Add(BitmapFrame.Create(bmp));

            using MemoryStream buffer = new();
            encoder.Save(buffer);
            return buffer.Length;
        }
        catch (Exception)
        {
            return -1;
        }
    }

    /// <summary>圧縮せずに持った場合のバイト数。上限に引っかかるかどうかの目安。</summary>
    private static long RawSize(BitmapSource bmp)
        => (long)bmp.PixelWidth * bmp.PixelHeight * ((bmp.Format.BitsPerPixel + 7) / 8);

    private static long SafeLength(Stream s)
    {
        try
        {
            return s.Length;
        }
        catch (NotSupportedException)
        {
            return -1;
        }
    }

    private static string Preview(string s)
    {
        string single = s.ReplaceLineEndings(" ").Trim();
        return Truncate(single, 28);
    }

    // ------------------------------------------------------------------
    // 書き出しと往復
    // ------------------------------------------------------------------

    private static void DumpFormats()
    {
        List<Probed>? probed = ProbeClipboard();
        if (probed is null)
        {
            return;
        }

        // 設計メモ §6-5 の置き場と同じ形（連番 + 索引ファイル）で書き出す。
        // 形式名をそのままファイル名にすると、独自形式に含まれる記号で壊れる
        string root = Path.Combine(
            Path.GetTempPath(),
            "ClipboardProbe",
            DateTime.Now.ToString("yyyyMMdd_HHmmss"));
        Directory.CreateDirectory(root);

        List<string> index = [];
        int i = 0;

        foreach (Probed p in probed)
        {
            string fileName = $"{i:00}.bin";
            byte[]? bytes = ToBytes(p.Value);

            if (bytes is not null)
            {
                File.WriteAllBytes(Path.Combine(root, fileName), bytes);
            }

            index.Add($"{i:00}\t{p.Format}\t{FormatCatalog.Label(p.Verdict)}\t"
                + $"{p.TypeName}\t{bytes?.Length ?? 0}\t{p.Note}");
            i++;
        }

        File.WriteAllText(
            Path.Combine(root, "index.txt"),
            "番号\t形式\t判定\t型\tバイト数\t備考\r\n" + string.Join("\r\n", index),
            Encoding.UTF8);

        Console.WriteLine();
        Console.WriteLine($"書き出しました: {root}");
        Console.WriteLine("  index.txt を見て、どの形式に何が入っているか確かめてください。");
        Console.WriteLine("  HTML Format の中身を見ると、Office がどれだけゴミを付けるか分かります（§8-7）。");
    }

    private static byte[]? ToBytes(object? value)
    {
        switch (value)
        {
            case null:
                return null;
            case string s:
                return Encoding.UTF8.GetBytes(s);
            case string[] paths:
                return Encoding.UTF8.GetBytes(string.Join("\r\n", paths));
            case byte[] bytes:
                return bytes;
            case MemoryStream ms:
                return ms.ToArray();
            case BitmapSource bmp:
                {
                    // 生のピクセルではなく PNG で書き出す。無圧縮だと数 MB になる
                    PngBitmapEncoder encoder = new();
                    encoder.Frames.Add(BitmapFrame.Create(bmp));

                    using MemoryStream buffer = new();
                    encoder.Save(buffer);
                    return buffer.ToArray();
                }
            case Stream st:
                {
                    // 位置が進んでいる可能性があるので、読めるなら巻き戻してから読む
                    if (st.CanSeek)
                    {
                        st.Position = 0;
                    }

                    using MemoryStream buffer = new();
                    st.CopyTo(buffer);
                    return buffer.ToArray();
                }

            default:
                return null;
        }
    }

    private static void RoundTrip()
    {
        List<Probed>? probed = ProbeClipboard();
        if (probed is null)
        {
            return;
        }

        // 保存すると決めた形式だけを、バイト列として抱える。
        // 本体では snapshots\ に書くところ
        List<(string Format, object Value)> kept = [];

        // foreach の変数とラムダの引数を同じ名前にすると CS0136 になるので、別名にする
        foreach (Probed target in probed.Where(x => x.Verdict == FormatVerdict.Keep))
        {
            switch (target.Value)
            {
                case string s:
                    kept.Add((target.Format, s));
                    break;
                case string[] paths:
                    kept.Add((target.Format, paths));
                    break;
                case BitmapSource bmp:
                    kept.Add((target.Format, bmp));
                    break;
                default:
                    {
                        byte[]? bytes = ToBytes(target.Value);
                        if (bytes is not null)
                        {
                            kept.Add((target.Format, bytes));
                        }

                        break;
                    }
            }
        }

        if (kept.Count == 0)
        {
            Console.WriteLine("保存対象の形式が 1 つもありませんでした。");
            return;
        }

        Console.WriteLine();
        Console.WriteLine($"{kept.Count} 形式を抱えました: {string.Join(", ", kept.Select(k => k.Format))}");
        Console.Write("クリップボードを空にして復元します。Enter で続行 > ");
        Console.ReadLine();

        if (!TryRun(Clipboard.Clear))
        {
            Console.WriteLine("クリアに失敗しました。");
            return;
        }

        DataObject restored = new();
        foreach ((string format, object value) in kept)
        {
            // ストリームは読むと位置が進むので、載せるたびに新しく作る。
            // 使い回すと 2 回目以降が空になる（§6-6）
            object payload = value is byte[] bytes ? new MemoryStream(bytes) : value;
            restored.SetData(format, payload);
        }

        if (!TrySetDataObject(restored))
        {
            return;
        }

        Console.WriteLine("復元しました。元のアプリに貼り付けて、書式が戻るか確かめてください。");
        Console.WriteLine("  Excel の表なら罫線と数式、Word の表なら書式が戻るか。");
        Console.WriteLine("  戻らない形式があれば、許可リストに足すものがあるということです。");
    }

    // ------------------------------------------------------------------
    // 1 つの形式を読み比べる
    // ------------------------------------------------------------------

    /// <summary>
    /// 同じ形式を複数の経路で読み、どこで失われているかを切り分ける。
    ///
    /// <para>
    /// Excel をコピーした状態で「HTML Format」が null になった件を追うために足した。
    /// Win32 で読めて .NET で読めないなら、データはあって包み方が問題ということになり、
    /// 本体は Win32 で読む実装に倒せばよい。
    /// Win32 でも読めないなら、そもそも載っていないので設計から見直す必要がある。
    /// </para>
    /// </summary>
    private static void CompareReadPaths()
    {
        Console.Write("形式名（そのまま Enter で HTML Format） > ");
        string format = Console.ReadLine()?.Trim() ?? string.Empty;
        if (format.Length == 0)
        {
            format = "HTML Format";
        }

        Console.WriteLine();
        Console.WriteLine($"「{format}」");
        Console.WriteLine($"  Win32 の形式 ID : {RawClipboard.GetFormatId(format)}");
        Console.WriteLine($"  載っているか    : {RawClipboard.IsAvailable(format)}");
        Console.WriteLine();

        // 1) WPF・自動変換なし。ProbeClipboard と同じ読み方
        ReportManaged("WPF GetData(autoConvert:false)", format, autoConvert: false);

        // 2) WPF・自動変換あり。これで読めるなら、単に自動変換に頼るだけで済む
        ReportManaged("WPF GetData(autoConvert:true) ", format, autoConvert: true);

        // 3) テキストとして読む専用の入口。HTML はここに専用の経路がある
        if (ToTextDataFormat(format) is { } textFormat)
        {
            string text = string.Empty;
            bool ok = TryRun(() => text = Clipboard.GetText(textFormat));
            Console.WriteLine(ok && text.Length > 0
                ? $"  Clipboard.GetText({textFormat,-11})   : {Encoding.UTF8.GetByteCount(text),8} B  {Preview(text)}"
                : $"  Clipboard.GetText({textFormat,-11})   : 取れず");
        }

        // 4) Win32 で生のまま読む。ここが最後の砦
        (byte[]? bytes, string note) = RawClipboard.Read(format);
        Console.WriteLine(bytes is not null
            ? $"  Win32 直読み                     : {bytes.Length,8} B"
            : $"  Win32 直読み                     : 取れず（{note}）");

        if (bytes is null)
        {
            Console.WriteLine();
            Console.WriteLine("どの経路でも取れませんでした。この形式は載っていない扱いになります。");
            return;
        }

        // CF_HTML なら、ヘッダを読んでコピー元がどう組み立てているかを見る。
        // 自分が書く側の答え合わせにもなる（§4）
        string decoded = Encoding.UTF8.GetString(bytes);
        Console.WriteLine();
        Console.WriteLine("── 生のバイト列の先頭 ──");
        foreach (string line in decoded.Split('\n').Take(8))
        {
            Console.WriteLine($"  {Truncate(line.TrimEnd('\r'), 100)}");
        }

        string dumpPath = Path.Combine(
            Path.GetTempPath(),
            $"ClipboardProbe_{DateTime.Now:yyyyMMdd_HHmmss}.bin");
        File.WriteAllBytes(dumpPath, bytes);
        Console.WriteLine();
        Console.WriteLine($"全体を書き出しました: {dumpPath}");
    }

    private static void ReportManaged(string label, string format, bool autoConvert)
    {
        IDataObject? data = null;
        if (!TryRun(() => data = Clipboard.GetDataObject()) || data is null)
        {
            Console.WriteLine($"  {label} : クリップボードを開けず");
            return;
        }

        object? value = null;
        string note = string.Empty;

        try
        {
            value = data.GetData(format, autoConvert);
        }
        catch (Exception ex)
        {
            note = ex.GetType().Name;
        }

        if (value is null)
        {
            Console.WriteLine($"  {label} : null{(note.Length > 0 ? $" ({note})" : string.Empty)}");
            return;
        }

        (string typeName, long size, string detail) = Describe(value);
        Console.WriteLine($"  {label} : {size,8} B  {typeName} {detail}");
    }

    /// <summary>テキストとして読む入口がある形式かどうか。</summary>
    private static TextDataFormat? ToTextDataFormat(string format) => format switch
    {
        "HTML Format" => TextDataFormat.Html,
        "Rich Text Format" => TextDataFormat.Rtf,
        "UnicodeText" => TextDataFormat.UnicodeText,
        "Text" => TextDataFormat.Text,
        "CSV" or "Csv" => TextDataFormat.CommaSeparatedValue,
        _ => null,
    };

    // ------------------------------------------------------------------
    // 共通
    // ------------------------------------------------------------------

    /// <summary>本体の ClipboardService と同じ再試行。ロックされていても数十 ms で空くことが多い。</summary>
    private static bool TryRun(Action action)
    {
        const int maxAttempts = 5;
        const int retryDelayMs = 80;

        for (int attempt = 1; attempt <= maxAttempts; attempt++)
        {
            try
            {
                action();
                return true;
            }
            catch (Exception ex)
            {
                if (attempt == maxAttempts)
                {
                    Console.WriteLine($"失敗しました: {ex.GetType().Name}: {ex.Message}");
                    return false;
                }

                Thread.Sleep(retryDelayMs);
            }
        }

        return false;
    }

    /// <summary>
    /// copy: true を必ず付ける。付けないとこのプロセスが終わった時点で中身が消える。
    /// </summary>
    private static bool TrySetDataObject(DataObject data)
        => TryRun(() => Clipboard.SetDataObject(data, copy: true));

    private static string FormatSize(long bytes) => bytes switch
    {
        < 0 => "?",
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:F1} KB",
        _ => $"{bytes / (1024.0 * 1024.0):F2} MB",
    };

    private static string Truncate(string s, int max)
        => s.Length <= max ? s : string.Concat(s.AsSpan(0, max - 1), "…");
}
