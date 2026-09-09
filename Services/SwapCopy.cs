namespace MyTaskTray.Services
{
    /// <summary>Swap コピーが終わった理由。通知の文面を分けるために使う。</summary>
    internal enum SwapCopyResult
    {
        /// <summary>入れ替えまで完了した。</summary>
        Success,

        /// <summary>クリップボードが空で、貼り付けるものがなかった。</summary>
        ClipboardEmpty,

        /// <summary>クリップボードを読み書きできなかった。</summary>
        ClipboardUnavailable,

        /// <summary>Ctrl+C を送ってもクリップボードが変わらなかった（選択がない）。</summary>
        NothingSelected,

        /// <summary>キー操作を送れなかった。</summary>
        InputBlocked,

        /// <summary>元の内容へ戻せず、貼り付けを行わなかった。</summary>
        OriginalRestoreFailed,

        /// <summary>貼り付けは済んだが、選択していた内容をクリップボードへ戻せなかった。</summary>
        SelectionRestoreFailed,

        /// <summary>貼り付けは済んだが、待つあいだに別のコピーが行われた。</summary>
        SelectionOverwritten,
    }

    /// <summary>1 回の Swap コピーの結果。文字列は通知の本文に使う。</summary>
    /// <param name="Result">終わった理由。</param>
    /// <param name="PastedText">貼り付けた内容（＝元のクリップボード）。</param>
    /// <param name="StashedText">クリップボードへ残した内容（＝選択していた文字列）。</param>
    internal readonly record struct SwapCopyOutcome(
        SwapCopyResult Result,
        string PastedText,
        string StashedText)
    {
        public static SwapCopyOutcome Failed(SwapCopyResult result)
            => new(result, string.Empty, string.Empty);
    }

    /// <summary>
    /// 選択している文字列と、いまクリップボードにある内容を入れ替える。
    ///
    /// <list type="number">
    /// <item>元のクリップボードを控える</item>
    /// <item>Ctrl+C を送り、選択していた内容を控える</item>
    /// <item>元の内容をクリップボードへ戻し、Ctrl+V を送る</item>
    /// <item>選択していた内容をクリップボードへ載せる</item>
    /// </list>
    ///
    /// <para>
    /// 結果として、選択箇所は元のクリップボードの内容に置き換わり、
    /// 次の Ctrl+V では選択していた内容が貼られる。
    /// 詳細と、ここで待つ時間の根拠は DESIGN_SWAP_COPY.md を参照。
    /// </para>
    ///
    /// <para>
    /// クリップボードは STA スレッドからしか触れないため、
    /// 呼び出しは UI スレッドから行い、待ち時間も <c>await</c> で譲る。
    /// <c>Thread.Sleep</c> で止めると、貼り付け先のメッセージ処理まで巻き込む。
    /// </para>
    /// </summary>
    internal static class SwapCopy
    {
        /// <summary>Ctrl+C の結果が現れるまで待つ上限。</summary>
        private static readonly TimeSpan CopyTimeout = TimeSpan.FromMilliseconds(800);

        /// <summary>クリップボードの更新を見に行く間隔。</summary>
        private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(20);

        /// <summary>
        /// 更新に気づいてから読み始めるまでの間。
        /// 1 回のコピーで形式を順に載せるアプリがあり、
        /// 最初の通知の時点では書式付きの形式がまだ載っていない。
        /// </summary>
        private static readonly TimeSpan CopySettleDelay = TimeSpan.FromMilliseconds(60);

        /// <summary>
        /// Ctrl+V を送ってから、選択していた内容をクリップボードへ載せるまでの間。
        ///
        /// <para>
        /// 貼り付け先が読み終わったことを知る手立てはないので、時間で待つしかない。
        /// 短すぎると、読むのが遅いアプリ（ブラウザや Electron 製のエディタ）が
        /// 入れ替えた後の内容を貼ってしまう。長すぎると、続けて操作したときに
        /// 待たされる。実機で詰めるならここを動かす。
        /// </para>
        /// </summary>
        private static readonly TimeSpan PasteSettleDelay = TimeSpan.FromMilliseconds(350);

        /// <summary>1 回の入れ替えを行う。UI スレッドから呼ぶこと。</summary>
        public static async Task<SwapCopyOutcome> RunAsync()
        {
            // 1. 元のクリップボードを控える。
            //    ここで失敗したら何も動かさない。読めない状態で Ctrl+C を送ると、
            //    利用者の元の内容を失ったまま先へ進んでしまう
            if (!ClipboardService.TryCaptureSnapshot(out ClipboardSnapshot original))
            {
                return SwapCopyOutcome.Failed(SwapCopyResult.ClipboardUnavailable);
            }

            if (original.IsEmpty)
            {
                return SwapCopyOutcome.Failed(SwapCopyResult.ClipboardEmpty);
            }

            uint beforeCopy = ClipboardService.GetSequenceNumber();

            // 2. 選択している内容をコピーさせる
            _ = InputInjector.TryReleaseModifiers();
            if (!InputInjector.TrySendCopy())
            {
                return SwapCopyOutcome.Failed(SwapCopyResult.InputBlocked);
            }

            if (!await WaitForClipboardChangeAsync(beforeCopy))
            {
                // クリップボードが変わらない＝コピーされていない。
                // 選択がないか、コピーを受け付けない場所にカーソルがある。
                // 何も書き換えていないので、このまま帰ってよい
                return SwapCopyOutcome.Failed(SwapCopyResult.NothingSelected);
            }

            await Task.Delay(CopySettleDelay);

            if (!ClipboardService.TryCaptureSnapshot(out ClipboardSnapshot selected)
                || selected.IsEmpty)
            {
                // 読めなかった。元へ戻して「何も起きなかった」状態にする
                _ = ClipboardService.TryRestoreSnapshot(original);
                return SwapCopyOutcome.Failed(SwapCopyResult.ClipboardUnavailable);
            }

            // 3. 元の内容へ戻してから貼り付ける
            if (!ClipboardService.TryRestoreSnapshot(original))
            {
                // 戻せなかった。いまクリップボードにあるのは選択していた内容なので、
                // ここで Ctrl+V を送ると選択箇所を同じ内容で置き換えるだけになる。
                // 単なる Ctrl+C を実行した状態で止める
                return new SwapCopyOutcome(
                    SwapCopyResult.OriginalRestoreFailed, string.Empty, selected.Text);
            }

            uint afterRestore = ClipboardService.GetSequenceNumber();

            if (!InputInjector.TrySendPaste())
            {
                // 貼り付けを送れなかった。クリップボードには元の内容が戻っているので、
                // ここで選択した内容を載せると、貼られないまま元の内容が消える。
                // 何も起きなかった状態のままにする
                return SwapCopyOutcome.Failed(SwapCopyResult.InputBlocked);
            }

            await Task.Delay(PasteSettleDelay);

            // 4. 待つあいだに他のアプリがコピーしていたら、そちらを上書きしない。
            //    利用者が新しくコピーした内容のほうが、いま欲しいものである可能性が高い
            uint afterPaste = ClipboardService.GetSequenceNumber();
            if (afterRestore != 0 && afterPaste != 0 && afterPaste != afterRestore)
            {
                return new SwapCopyOutcome(
                    SwapCopyResult.SelectionOverwritten, original.Text, selected.Text);
            }

            if (!ClipboardService.TryRestoreSnapshot(selected))
            {
                return new SwapCopyOutcome(
                    SwapCopyResult.SelectionRestoreFailed, original.Text, selected.Text);
            }

            return new SwapCopyOutcome(SwapCopyResult.Success, original.Text, selected.Text);
        }

        /// <summary>
        /// クリップボードの更新回数が変わるまで待つ。
        /// 番号を取得できない環境では判定できないため、待ち切って false を返す。
        /// </summary>
        private static async Task<bool> WaitForClipboardChangeAsync(uint before)
        {
            long deadline = Environment.TickCount64 + (long)CopyTimeout.TotalMilliseconds;

            while (Environment.TickCount64 < deadline)
            {
                await Task.Delay(PollInterval);

                if (ClipboardService.GetSequenceNumber() != before)
                {
                    return true;
                }
            }

            return false;
        }
    }
}
