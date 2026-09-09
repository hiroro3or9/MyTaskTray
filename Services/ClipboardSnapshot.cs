namespace MyTaskTray.Services
{
    /// <summary>
    /// ある時点のクリップボードの中身を、形式ごとそのまま持ち帰る控え。
    ///
    /// <para>
    /// Swap コピーは「元の内容を貼り付けて、選択していた内容をクリップボードへ残す」ため、
    /// 同じ内容を 2 度クリップボードへ載せ直す。文字列だけを覚えると、
    /// Excel の書式付きコピーや画像が黙って平文になってしまうため、
    /// <c>IDataObject</c> に載っていた形式をすべて控える。
    /// </para>
    ///
    /// <para>
    /// メモリ上にしか置かず、設定ファイルや履歴には書かない。
    /// 保存を拒む印（パスワード管理ソフトなどが載せる形式）も控えごと運ぶので、
    /// 戻したあとのクリップボードでも拒否の意思は保たれる。
    /// </para>
    /// </summary>
    internal sealed class ClipboardSnapshot
    {
        /// <summary>1 つの形式と、その形式で載っていた値。</summary>
        internal readonly record struct Entry(string Format, object Data);

        private readonly Entry[] _entries;

        internal ClipboardSnapshot(Entry[] entries, string text)
        {
            _entries = entries;
            Text = text ?? string.Empty;
        }

        /// <summary>何も載っていなかったクリップボードを表す控え。</summary>
        public static ClipboardSnapshot Empty { get; } = new([], string.Empty);

        /// <summary>1 つの形式も控えられなかったかどうか。</summary>
        public bool IsEmpty => _entries.Length == 0;

        /// <summary>控えた形式の数。</summary>
        public int FormatCount => _entries.Length;

        /// <summary>
        /// 通知に出すための代表文字列。文字列を持たない内容（画像だけなど）では空。
        /// 復元にはこれではなく <see cref="Entries"/> を使う。
        /// </summary>
        public string Text { get; }

        internal IReadOnlyList<Entry> Entries => _entries;
    }
}
