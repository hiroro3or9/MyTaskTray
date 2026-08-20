using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using MyTaskTray.Models;

namespace MyTaskTray.Services
{
    /// <summary>クリップボードを 1 回開いて読んだ結果。</summary>
    public enum ClipboardReadResult
    {
        /// <summary>文字列を取り出せた。</summary>
        Text,

        /// <summary>空だった、または文字列ではなかった。</summary>
        Empty,

        /// <summary>コピー元が、他アプリでの保存を拒んでいる。</summary>
        MonitoringExcluded,
    }

    /// <summary>
    /// クリップボードの読み書きを行う。
    /// 他プロセスがクリップボードをロックしている場合があるため、失敗時は少し待って再試行する。
    /// </summary>
    public static class ClipboardService
    {
        private const int MaxAttempts = 5;
        private const int RetryDelayMs = 80;

        /// <summary>
        /// 自分が書き込んでから、その更新通知が届くまでに見込む猶予。
        /// シーケンス番号での照合が効かなかった場合の保険なので、短くてよい。
        /// </summary>
        private const long SelfWriteGraceMs = 500;

        /// <summary>
        /// クリップボードを監視するアプリに対して「保存するな」と伝える形式。
        /// パスワード管理ソフトなどが載せる。Windows 標準のクリップボード履歴もこれを尊重する。
        /// 詳細は DESIGN_RICH_COPY.md §6-7 / DESIGN_COPY_INTENT.md §7 を参照。
        /// </summary>
        private static readonly string[] ExclusionMarkerFormats =
        [
            // 載っているだけで拒否を意味するもの
            "ExcludeClipboardContentFromMonitorProcessing",
            "Clipboard Viewer Ignore",
        ];

        /// <summary>
        /// 値が DWORD で、0 のときだけ拒否を意味する形式。1 なら許可なので、
        /// 載っていることだけを根拠に断ってはいけない。
        /// </summary>
        private static readonly string[] ExclusionFlagFormats =
        [
            "CanIncludeInClipboardHistory",
            "CanUploadToCloudClipboard",
        ];

        /// <summary>直前にこのアプリ自身が書き込んだ直後のシーケンス番号。</summary>
        private static uint _selfWriteSequenceNumber;

        /// <summary>直前にこのアプリ自身が書き込んだ時刻。</summary>
        private static long _selfWriteAt;

        /// <summary>
        /// 指定した文字列をプレーンテキストとしてクリップボードにコピーする。成功したら true。
        /// </summary>
        public static bool TryCopy(string text) => TryCopy(text, ClipFormat.Plain);

        /// <summary>
        /// 指定した文字列を、形式に応じてクリップボードにコピーする。成功したら true。
        ///
        /// <para>
        /// <see cref="ClipFormat.Plain"/> 以外では、プレーンテキストに加えて
        /// 書式付きの形式も一緒に載せる。どれが使われるかは貼り付け先が選ぶ。
        /// </para>
        /// </summary>
        public static bool TryCopy(string text, ClipFormat format)
        {
            // 空の DataObject を載せると「空文字がコピーされた状態」と
            // 「クリップボードが空の状態」が食い違い、{clip} 側の判定が狂う。
            // 従来どおりクリアする
            if (string.IsNullOrEmpty(text))
            {
                return RecordSelfWrite(TryRun(System.Windows.Clipboard.Clear));
            }

            System.Windows.DataObject data = new();

            // どの形式にも必ずプレーンテキストを載せる。
            // これが無いと、書式を扱えない相手に何も渡らない
            data.SetData(System.Windows.DataFormats.UnicodeText, text);

            string? htmlFragment = format switch
            {
                // 書いた内容がそのまま HTML
                ClipFormat.Html => text,

                // Markdown として解釈して HTML に変換する。
                // 変換できなかった場合は null が返り、プレーンテキストだけで続行する
                ClipFormat.Markdown => MarkdownRenderer.ToHtml(text),

                _ => null,
            };

            if (htmlFragment is not null)
            {
                // 文字列のまま渡す。.NET が UTF-8 で書き、NUL 終端も付けてくれる。
                // MemoryStream でバイト列を渡すと終端が付かず、末尾 1 バイトが落ちる
                data.SetData(System.Windows.DataFormats.Html, CfHtml.Build(htmlFragment));
            }

            // copy: true を付けないと、このアプリを終了した時点で中身が消える
            return RecordSelfWrite(
                TryRun(() => System.Windows.Clipboard.SetDataObject(data, copy: true)));
        }

        /// <summary>
        /// 形式に応じた「差し込んだ値の後処理」を返す。
        /// <c>TemplateEngine.Expand</c> の <c>valueTransform</c> へ渡して使う。
        /// 後処理が要らない形式では null を返す。
        ///
        /// <para>
        /// テンプレート本体ではなく差し込まれた値だけに効かせるのが要点。
        /// HTML の項目で、利用者が書いたタグは生かしたまま、
        /// <c>{input:…}</c> や <c>{clip}</c> に入った記号だけをエスケープする。
        /// </para>
        /// </summary>
        public static Func<string, string>? GetValueTransform(ClipFormat format) => format switch
        {
            // 書いた内容がそのまま HTML になるので、差し込む値は自分でエスケープする必要がある
            ClipFormat.Html => CfHtml.Escape,

            // Markdown では何もしない。差し込まれた値は Markdown の本文として扱われ、
            // HTML への変換時に Markdig が正しくエスケープする。
            // ここでエスケープすると、プレーンテキスト側に &amp; が出てしまう
            _ => null,
        };

        /// <summary>
        /// クリップボードの文字列を読み取る。
        /// 文字列が入っていない場合や読み取れなかった場合は空文字列を返す。
        /// <c>{clip}</c> の差し込みで使う。
        /// </summary>
        public static string GetText()
        {
            string result = string.Empty;

            TryRun(() =>
            {
                if (System.Windows.Clipboard.ContainsText())
                {
                    result = System.Windows.Clipboard.GetText() ?? string.Empty;
                }
            });

            return result;
        }

        /// <summary>
        /// クリップボードに文字列が入っていそうかどうか。
        ///
        /// <para>
        /// 中身は読まないので <see cref="GetText"/> より軽く、他アプリのコピー操作を妨げにくい。
        /// メニューの項目を有効にするかどうかの判定に使う。
        /// 判定できなかった場合は true を返す。押せなくして「なぜか使えない」となるより、
        /// 押した結果を通知で伝えるほうが分かりやすい。
        /// </para>
        /// </summary>
        public static bool HasText()
        {
            try
            {
                return System.Windows.Clipboard.ContainsText();
            }
            catch (Exception)
            {
                return true;
            }
        }

        /// <summary>
        /// 連続コピーが 1 件として取り込むために、クリップボードを 1 回だけ開いて読む。
        ///
        /// <para>
        /// 保存を拒む印の確認と文字列の取り出しを 1 回の読み取りにまとめてある。
        /// 別々に開くと、コピー直後の混み合った時間帯にロックを 2 回待つことになる。
        /// </para>
        /// </summary>
        public static ClipboardReadResult TryReadForCapture(out string text)
        {
            string value = string.Empty;
            ClipboardReadResult result = ClipboardReadResult.Empty;

            TryRun(() =>
            {
                System.Windows.IDataObject? data = System.Windows.Clipboard.GetDataObject();
                if (data is null)
                {
                    value = string.Empty;
                    result = ClipboardReadResult.Empty;
                    return;
                }

                // 中身を読む前に判定する。拒まれているものは読まずに帰る。
                if (IsMonitoringExcluded(data))
                {
                    value = string.Empty;
                    result = ClipboardReadResult.MonitoringExcluded;
                    return;
                }

                // ここは例外を握りつぶさない。読み取りに失敗したときは
                // TryRun に再試行させたいため（他プロセスのロックは時間で解ける）。
                value = data.GetData(System.Windows.DataFormats.UnicodeText, autoConvert: true)
                    as string ?? string.Empty;
                result = string.IsNullOrEmpty(value)
                    ? ClipboardReadResult.Empty
                    : ClipboardReadResult.Text;
            });

            text = value;
            return result;
        }

        /// <summary>
        /// 直前のクリップボード更新が、このアプリ自身の書き込みかどうか。
        ///
        /// <para>
        /// 連続コピーの収集中に、トレイメニューから定型文をコピーした内容を
        /// 収集してしまわないために使う。
        /// </para>
        /// </summary>
        /// <param name="sequenceNumber">
        /// 更新通知を受け取った時点のシーケンス番号。取得できなかった場合は 0 を渡す。
        /// </param>
        public static bool IsSelfWrite(uint sequenceNumber)
        {
            uint selfWrite = Volatile.Read(ref _selfWriteSequenceNumber);
            if (sequenceNumber != 0 && selfWrite != 0)
            {
                // 番号で確実に判定できるときは、これだけで決める。
                // 時間の猶予まで併用すると、自分がコピーした直後に
                // 利用者が別のアプリでコピーした分まで落としてしまう。
                return sequenceNumber == selfWrite;
            }

            // 番号を取得できなかった場合の保険。
            long writtenAt = Interlocked.Read(ref _selfWriteAt);
            return writtenAt != 0 && Environment.TickCount64 - writtenAt < SelfWriteGraceMs;
        }

        /// <summary>書き込みが成功していれば、自分の書き込みとして記録する。</summary>
        private static bool RecordSelfWrite(bool succeeded)
        {
            if (!succeeded)
            {
                return false;
            }

            // SetDataObject と Clear は同期的にクリップボードを更新するので、
            // 戻った時点で新しい番号が取れている。
            Volatile.Write(ref _selfWriteSequenceNumber, GetClipboardSequenceNumber());
            Interlocked.Exchange(ref _selfWriteAt, Environment.TickCount64);
            return true;
        }

        /// <summary>コピー元が、他アプリでの保存を拒んでいるかどうか。</summary>
        private static bool IsMonitoringExcluded(System.Windows.IDataObject data)
        {
            foreach (string format in ExclusionMarkerFormats)
            {
                if (IsDataPresentSafely(data, format))
                {
                    return true;
                }
            }

            foreach (string format in ExclusionFlagFormats)
            {
                if (IsDataPresentSafely(data, format)
                    && DenotesDenial(GetDataSafely(data, format, autoConvert: false)))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// 拒否を表す DWORD かどうか。0 なら拒否で、1 は許可。
        ///
        /// <para>
        /// 読み取れなかった場合は拒否と見なさない。
        /// この 2 つの形式は「載っていること」ではなく「値」に意味があり、
        /// 読めなかったという事実は何も語っていない。
        /// ここで断ると、明示的に許可しているアプリからコピーできなくなる。
        /// 本当に拒みたいアプリは、載っているだけで拒否になる
        /// <c>ExcludeClipboardContentFromMonitorProcessing</c> も併せて載せる。
        /// </para>
        /// </summary>
        private static bool DenotesDenial(object? value) => value switch
        {
            int number => number == 0,
            short number => number == 0,
            byte[] bytes => TryReadDword(bytes, out int number) && number == 0,
            MemoryStream stream => TryReadDword(stream.ToArray(), out int number) && number == 0,
            _ => false,
        };

        private static bool TryReadDword(byte[] bytes, out int value)
        {
            if (bytes.Length < sizeof(int))
            {
                value = 0;
                return false;
            }

            value = BitConverter.ToInt32(bytes, 0);
            return true;
        }

        /// <summary>
        /// 1 つの形式が読めなくても、他の判定は続けられるようにする。
        /// 遅延レンダリング中の形式は例外を投げることがある（DESIGN_RICH_COPY.md §6-2）。
        /// </summary>
        private static bool IsDataPresentSafely(System.Windows.IDataObject data, string format)
        {
            try
            {
                return data.GetDataPresent(format, autoConvert: false);
            }
            catch (Exception)
            {
                return false;
            }
        }

        private static object? GetDataSafely(
            System.Windows.IDataObject data, string format, bool autoConvert)
        {
            try
            {
                return data.GetData(format, autoConvert);
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static bool TryRun(Action action)
        {
            for (int attempt = 1; attempt <= MaxAttempts; attempt++)
            {
                try
                {
                    action();
                    return true;
                }
                catch (Exception)
                {
                    if (attempt == MaxAttempts)
                    {
                        return false;
                    }

                    Thread.Sleep(RetryDelayMs);
                }
            }

            return false;
        }

        [DllImport("user32.dll")]
        private static extern uint GetClipboardSequenceNumber();
    }
}
