using System.Text.Json.Serialization;

namespace MyTaskTray.Models
{
    /// <summary>
    /// スマートアクションをトレイメニューに表示する条件。
    /// <see cref="Always"/> 以外の項目は、条件に合ったときだけ
    /// 「この内容でできること」サブメニューへ表示する。
    /// </summary>
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public enum ClipboardMatchKind
    {
        Always,
        HasText,
        Date,
        Url,
        Number,
        Json,
        FilePath,
        Email,
        Regex,
    }
}
