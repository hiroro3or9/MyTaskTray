using System.Text.Json.Serialization;

namespace MyTaskTray.Models
{
    /// <summary>
    /// 連続コピーが 1 件として集める、クリップボード更新の範囲。
    ///
    /// <para>
    /// Windows はクリップボードが変わったことしか教えてくれず、
    /// 「誰が」「どうやって」コピーしたかは分からない。
    /// そのため、直前の入力操作からこちらで推測する。
    /// 詳細は DESIGN_COPY_INTENT.md を参照。
    /// </para>
    /// </summary>
    /// <remarks>
    /// 設定ファイルには数値ではなく <c>"UserInput"</c> のような名前で書き出す。
    /// 手で編集できることを前提にしているため（<see cref="ClipFormat"/> と揃える）。
    /// </remarks>
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public enum SequentialCaptureTrigger
    {
        /// <summary>
        /// キーまたはマウスを操作した直後に起きた更新だけを集める。
        /// 右クリックの「コピー」やアプリのコピーボタンも対象になり、
        /// 利用者が何も触っていないあいだの自動的な書き込みは対象外になる。
        /// </summary>
        /// <remarks>
        /// 既定値。設定ファイルに書かれていない場合はこれになる。
        /// 従来（Ctrl+C のみ）から既定の挙動が変わる唯一の項目。
        /// </remarks>
        UserInput = 0,

        /// <summary>
        /// Ctrl+C などのコピー系のキー操作の直後だけを集める。従来どおりの動き。
        /// 最も確実だが、右クリックの「コピー」やコピーボタンは集まらない。
        /// </summary>
        CopyKey = 1,

        /// <summary>
        /// クリップボードの更新をすべて集める。
        /// 取りこぼしは無くなるが、常駐している他のアプリが
        /// 自動で書き込んだ内容も入る。
        /// </summary>
        Any = 2,
    }
}
