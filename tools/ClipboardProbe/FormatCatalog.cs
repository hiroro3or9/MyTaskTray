using System;
using System.Collections.Generic;
using System.Linq;

namespace ClipboardProbe;

/// <summary>形式を持ち帰ってよいかどうかの判定。</summary>
internal enum FormatVerdict
{
    /// <summary>許可リストに載っている。保存する。</summary>
    Keep,

    /// <summary>コピー元のプロセスやドキュメントを指すだけの形式。保存してはいけない。</summary>
    DropOle,

    /// <summary>自動変換で作られるので、保存する必要がない。</summary>
    DropAutoConvert,

    /// <summary>中身は正当だが、他の形式と重複している。大きさに見合わないので保存しない。</summary>
    DropRedundant,

    /// <summary>知らない形式。実機で見てから振り分ける。</summary>
    Unknown,
}

/// <summary>
/// DESIGN_RICH_COPY.md §6-3 / §6-7 の分類。
///
/// <para>
/// 初版は机上の想定だったが、Excel の範囲をコピーした状態で実測した結果を反映してある。
/// 実測で分かったことは各リストのコメントに残す。
/// </para>
/// </summary>
internal static class FormatCatalog
{
    /// <summary>
    /// 持ち帰る形式。
    /// </summary>
    private static readonly HashSet<string> Allow = new(StringComparer.OrdinalIgnoreCase)
    {
        "UnicodeText",
        "HTML Format",
        "Rich Text Format",
        "CSV",
        "Csv",
        "XML Spreadsheet",
        "FileDrop",
        "PNG",
        "Xaml",
        "XamlPackage",

        // Excel から Excel へ完全に戻すための形式。
        // 実測では Biff5(52.7KB) / Biff8(32.8KB) / Biff12(14.1KB) が同時に載っていた。
        // 新しいものほど小さいので、Biff12 だけ持てばよい
        "Biff12",

        // 画像。実測では DeviceIndependentBitmap は載っておらず、Bitmap だけが来た。
        // 「DIB を保存すれば Windows が Bitmap を合成してくれる」という前提が成り立たないので、
        // 来たほうを保存する。ただし無圧縮で数 MB になるため、保存時は PNG にする
        "Bitmap",
        "DeviceIndependentBitmap",
    };

    /// <summary>
    /// 持ち帰ってはいけない形式。中身ではなくコピー元への参照でしかない。
    /// 復元すると、閉じたドキュメントへリンクを張ろうとして固まることがある。
    /// 実測では Object Descriptor が載っていて、中身は null だった（想定どおり）。
    /// </summary>
    private static readonly HashSet<string> Ole = new(StringComparer.OrdinalIgnoreCase)
    {
        "Ole Private Data",
        "Object Descriptor",
        "Link Source",
        "Link Source Descriptor",
        "Embed Source",
        "OwnerLink",
        "ObjectLink",
        "Native",
    };

    /// <summary>Windows と .NET が勝手に補完してくれるので、自前で保存しなくてよい形式。</summary>
    private static readonly HashSet<string> AutoConvert = new(StringComparer.OrdinalIgnoreCase)
    {
        // UnicodeText から合成される
        "Text",
        "OEMText",
        "System.String",

        // Bitmap から合成される（Bitmap 側を保存する）
        "MetaFilePict",
    };

    /// <summary>
    /// 中身は正当だが、他の形式と内容が重複していて、大きさに見合わないもの。
    /// 「知らないから未分類」ではなく「知ったうえで捨てている」と示すために分けている。
    /// </summary>
    private static readonly HashSet<string> Redundant = new(StringComparer.OrdinalIgnoreCase)
    {
        // 古い Excel のバイナリ形式。Biff12 があれば要らない
        "Biff8",
        "Biff5",

        // どちらも表計算の古い交換形式。XML Spreadsheet と CSV で足りる。
        // SymbolicLink は OLE のリンクではなく SYLK という表形式なので、
        // 害はないが 48KB を占めるだけになる
        "SymbolicLink",
        "DataInterchangeFormat",

        // Excel の内部用。実測では中身が null だった
        "ShadowWorkbook",

        // ブラウザが独自形式を名前で引くための対応表。復元しても意味がない
        "Web Custom Format Map",

        // 数値だけの名前は、名前を持たない古い定義済み形式。
        // 129 は CF_DSPTEXT（所有者描画用の表示テキスト）
        "Format129",

        // ベクタ画像。Word へ貼ると綺麗だが、扱うには System.Drawing が要る。
        // 実測では Metafile 型で返ってきた。必要になったら Allow へ移す
        "EnhancedMetafile",
    };

    /// <summary>
    /// 「これを履歴に残すな」という印。パスワード管理ソフトなどが付ける。
    /// Windows 標準のクリップボード履歴はこれを尊重している。
    /// 1 つでも載っていたら、捕獲そのものを断る。
    /// </summary>
    private static readonly string[] ExclusionMarkers =
    [
        "ExcludeClipboardContentFromMonitorProcessing",
        "CanIncludeInClipboardHistory",
        "CanUploadToCloudClipboard",
        "Clipboard Viewer Ignore",
    ];

    public static FormatVerdict Judge(string format)
    {
        if (Ole.Contains(format))
        {
            return FormatVerdict.DropOle;
        }

        if (AutoConvert.Contains(format))
        {
            return FormatVerdict.DropAutoConvert;
        }

        if (Redundant.Contains(format))
        {
            return FormatVerdict.DropRedundant;
        }

        return Allow.Contains(format) ? FormatVerdict.Keep : FormatVerdict.Unknown;
    }

    public static string Label(FormatVerdict verdict) => verdict switch
    {
        FormatVerdict.Keep => "保存する",
        FormatVerdict.DropOle => "除外(OLE)",
        FormatVerdict.DropAutoConvert => "除外(自動変換)",
        FormatVerdict.DropRedundant => "除外(重複)",
        _ => "未分類",
    };

    /// <summary>載っている形式のうち、保存を禁じる印に当たるものを返す。</summary>
    public static IReadOnlyList<string> FindExclusionMarkers(IEnumerable<string> formats)
    {
        HashSet<string> present = new(formats, StringComparer.OrdinalIgnoreCase);
        return [.. ExclusionMarkers.Where(present.Contains)];
    }
}
