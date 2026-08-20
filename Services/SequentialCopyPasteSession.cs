using System.Runtime.InteropServices;
using System.Windows.Interop;
using System.Windows.Threading;
using MyTaskTray.Models;

namespace MyTaskTray.Services
{
    /// <summary>連続コピー＆ペーストの現在の段階。</summary>
    internal enum SequentialCopyPastePhase
    {
        Capturing,
        Pasting,
    }

    /// <summary>収集できなかった理由。通知の文面を分けるために使う。</summary>
    internal enum SequentialCaptureRejection
    {
        /// <summary>空だった、または文字列ではなかった。</summary>
        EmptyOrNonText,

        /// <summary>コピー元が、他アプリでの保存を拒んでいる。</summary>
        MonitoringExcluded,

        /// <summary>まだ 1 件も集めていないのに貼り付けようとした。</summary>
        NothingCaptured,
    }

    /// <summary>1 回の貼り付けが成功したあとの進捗。</summary>
    internal readonly record struct SequentialPasteProgress(
        int PastedCount,
        int TotalCount,
        string Value)
    {
        public int RemainingCount => TotalCount - PastedCount;
    }

    /// <summary>
    /// 利用者のコピー操作を順番に蓄え、Ctrl+V ごとに次の文字列へ差し替える一時セッション。
    /// 常駐監視はせず、利用者がトレイメニューから開始している間だけ
    /// クリップボード変更通知と入力フックを有効にする。
    ///
    /// <para>
    /// どのクリップボード更新を 1 件として採るかは
    /// <see cref="SequentialCaptureTrigger"/> で決まる。判定の考え方は
    /// DESIGN_COPY_INTENT.md を参照。
    /// </para>
    /// </summary>
    internal sealed class SequentialCopyPasteSession : IDisposable
    {
        private const int WmClipboardUpdate = 0x031D;
        private const int WmKeyDown = 0x0100;
        private const int WmKeyUp = 0x0101;
        private const int WmSysKeyDown = 0x0104;
        private const int WmSysKeyUp = 0x0105;
        private const int WmMouseMove = 0x0200;
        private const int WhKeyboardLl = 13;
        private const int WhMouseLl = 14;
        private const uint VkControl = 0x11;
        private const uint VkShift = 0x10;
        private const uint VkLControl = 0xA2;
        private const uint VkRControl = 0xA3;
        private const uint VkC = 0x43;
        private const uint VkV = 0x56;
        private const uint VkX = 0x58;
        private const uint VkInsert = 0x2D;
        private const uint VkDelete = 0x2E;
        private const uint LlkhfInjected = 0x00000010;
        private const uint LlmhfInjected = 0x00000001;
        private static readonly IntPtr MessageOnlyWindow = new(-3);
        private static readonly TimeSpan Timeout = TimeSpan.FromMinutes(10);
        private static readonly TimeSpan CopyIntentLifetime = TimeSpan.FromSeconds(2);

        /// <summary>
        /// <see cref="SequentialCaptureTrigger.Any"/> のときだけ使う間引き幅。
        ///
        /// <para>
        /// 1 回のコピーで形式を順に載せるアプリがあり、その都度 WM_CLIPBOARDUPDATE が届く。
        /// 意図の印を使わない設定では、そのまま複数件として入ってしまうため、
        /// 直後に続いた更新は同じコピーの続きと見なす。
        /// 人が意図してこの間隔で 2 回コピーすることはまず無い。
        /// </para>
        /// </summary>
        private static readonly TimeSpan CaptureDebounce = TimeSpan.FromMilliseconds(300);

        private readonly SequentialCaptureTrigger _trigger;
        private readonly Action<string, int> _captured;
        private readonly Action<SequentialCaptureRejection> _captureRejected;
        private readonly Action<SequentialPasteProgress> _pasted;
        private readonly Action _pasteFailed;
        private readonly Action _completed;
        private readonly Action _timedOut;
        private readonly HwndSource _source;
        private readonly Dispatcher _dispatcher;
        private readonly DispatcherTimer _timer;
        private readonly LowLevelKeyboardProc _keyboardProc;
        private readonly LowLevelMouseProc? _mouseProc;
        private readonly List<string> _items = [];

        private IntPtr _keyboardHook;
        private IntPtr _mouseHook;
        private int _pasteIndex;
        private bool _listening;
        private bool _handlingClipboard;
        private uint _copyKeyDownVirtualKey;
        private bool _pasteKeyDown;
        private bool _completionPending;
        private bool _disposed;
        private bool _sourceDisposed;
        private long _copyIntentExpiresAt;
        private long _lastCaptureAt;
        private uint _copyIntentSequenceNumber;

        public SequentialCopyPasteSession(
            SequentialCaptureTrigger trigger,
            Action<string, int> captured,
            Action<SequentialCaptureRejection> captureRejected,
            Action<SequentialPasteProgress> pasted,
            Action pasteFailed,
            Action completed,
            Action timedOut)
        {
            _trigger = trigger;
            _captured = captured;
            _captureRejected = captureRejected;
            _pasted = pasted;
            _pasteFailed = pasteFailed;
            _completed = completed;
            _timedOut = timedOut;
            _dispatcher = Dispatcher.CurrentDispatcher;
            _keyboardProc = KeyboardHookProc;

            // マウスの監視が要るのは「操作した直後」だけ。
            // 他の設定では張らない（低レベルフックは、通っている間ずっと
            // システム全体の入力がこのアプリを経由することを意味する）。
            if (trigger == SequentialCaptureTrigger.UserInput)
            {
                _mouseProc = MouseHookProc;
            }

            HwndSourceParameters parameters = new("MyTaskTray.SequentialCopyPaste")
            {
                ParentWindow = MessageOnlyWindow,
                WindowStyle = 0,
            };

            _source = new HwndSource(parameters);
            _source.AddHook(WndProc);

            _timer = new DispatcherTimer(DispatcherPriority.Background)
            {
                Interval = Timeout,
            };
            _timer.Tick += OnTimeout;
        }

        public SequentialCopyPastePhase Phase { get; private set; }
            = SequentialCopyPastePhase.Capturing;

        /// <summary>
        /// このセッションが 1 件として集める範囲。開始時の設定で固定される。
        /// 実行中に設定を変えても、案内の文面が実際の動きとずれないようにするため公開する。
        /// </summary>
        public SequentialCaptureTrigger Trigger => _trigger;

        public int CapturedCount => _items.Count;

        public int PastedCount => _pasteIndex;

        public int RemainingCount => _items.Count - _pasteIndex;

        /// <summary>
        /// クリップボードと入力の監視を始める。どれかを登録できなければ false。
        /// </summary>
        public bool Start()
        {
            if (_disposed || _listening || _keyboardHook != IntPtr.Zero)
            {
                return false;
            }

            _listening = AddClipboardFormatListener(_source.Handle);
            if (!_listening)
            {
                Dispose();
                return false;
            }

            _keyboardHook = SetWindowsHookEx(
                WhKeyboardLl,
                _keyboardProc,
                GetModuleHandle(null),
                0);

            if (_keyboardHook == IntPtr.Zero)
            {
                Dispose();
                return false;
            }

            if (_mouseProc is not null)
            {
                _mouseHook = SetWindowsHookEx(
                    WhMouseLl,
                    _mouseProc,
                    GetModuleHandle(null),
                    0);

                if (_mouseHook == IntPtr.Zero)
                {
                    Dispose();
                    return false;
                }
            }

            RestartTimer();
            return true;
        }

        /// <summary>収集を明示的に終え、次の Ctrl+V を 1 件目の貼り付けにする。</summary>
        public bool TryBeginPasting()
        {
            if (_disposed || Phase == SequentialCopyPastePhase.Pasting || _items.Count == 0)
            {
                return false;
            }

            BeginPasting();
            return true;
        }

        /// <summary>収集中の最後の 1 件を取り除く。取り除いた値も返す。</summary>
        public bool TryUndoLastCapture(out string removed)
        {
            removed = string.Empty;
            if (_disposed || Phase != SequentialCopyPastePhase.Capturing || _items.Count == 0)
            {
                return false;
            }

            int index = _items.Count - 1;
            removed = _items[index];
            _items.RemoveAt(index);
            RestartTimer();
            return true;
        }

        private IntPtr WndProc(
            IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (message != WmClipboardUpdate
                || !_listening
                || Phase != SequentialCopyPastePhase.Capturing
                || _handlingClipboard
                || _disposed)
            {
                return IntPtr.Zero;
            }

            uint sequenceNumber = GetClipboardSequenceNumber();

            // トレイメニューからの定型文コピーなど、このアプリ自身の書き込みは収集しない。
            // 「操作した直後」ではメニューのクリックそのものが印を立てるため、
            // これが無いと確実に混ざる。
            if (ClipboardService.IsSelfWrite(sequenceNumber))
            {
                // 印を残しておくと、直後の無関係な更新をこのクリックの結果として拾ってしまう。
                ClearCopyIntent();
                return IntPtr.Zero;
            }

            if (!TryAcceptUpdate(sequenceNumber))
            {
                return IntPtr.Zero;
            }

            handled = true;
            _handlingClipboard = true;
            try
            {
                ClipboardReadResult read = ClipboardService.TryReadForCapture(out string value);
                if (read != ClipboardReadResult.Text)
                {
                    RestartTimer();
                    _captureRejected(read == ClipboardReadResult.MonitoringExcluded
                        ? SequentialCaptureRejection.MonitoringExcluded
                        : SequentialCaptureRejection.EmptyOrNonText);
                    return IntPtr.Zero;
                }

                // 同じ値が複数行に現れる業務データもあるため、重複は除外しない。
                _items.Add(value);
                _lastCaptureAt = Environment.TickCount64;
                RestartTimer();
                _captured(value, _items.Count);
            }
            finally
            {
                _handlingClipboard = false;
            }

            return IntPtr.Zero;
        }

        private IntPtr KeyboardHookProc(int code, IntPtr wParam, IntPtr lParam)
        {
            if (code < 0 || _disposed)
            {
                return CallNextHookEx(_keyboardHook, code, wParam, lParam);
            }

            try
            {
                KeyboardHookData data = Marshal.PtrToStructure<KeyboardHookData>(lParam);
                if ((data.Flags & LlkhfInjected) != 0)
                {
                    // 自動化ツールが送ったキーは、利用者の操作と見なさない。
                    return CallNextHookEx(_keyboardHook, code, wParam, lParam);
                }

                int message = wParam.ToInt32();

                // V は貼り付けの判定に使うので、ここから先の分岐に回す。
                // その結果、Ctrl を伴わない「v」の入力だけは「操作した直後」の
                // 根拠にならないが、コピーが起きる場面ではないので無視できる。
                if (data.VirtualKey != VkV)
                {
                    HandleCaptureKey(
                        data.VirtualKey,
                        isKeyDown: message is WmKeyDown or WmSysKeyDown,
                        isKeyUp: message is WmKeyUp or WmSysKeyUp);
                    return CallNextHookEx(_keyboardHook, code, wParam, lParam);
                }

                if (message is WmKeyUp or WmSysKeyUp)
                {
                    _pasteKeyDown = false;
                    return CallNextHookEx(_keyboardHook, code, wParam, lParam);
                }

                if (message is not (WmKeyDown or WmSysKeyDown) || !IsControlDown())
                {
                    return CallNextHookEx(_keyboardHook, code, wParam, lParam);
                }

                // キーリピートで 1 回の長押しが複数件の貼り付けにならないようにする。
                if (_pasteKeyDown)
                {
                    return CallNextHookEx(_keyboardHook, code, wParam, lParam);
                }

                _pasteKeyDown = true;

                // 最終件の完了処理が UI スレッドへ戻るまでの短い間にもう一度押された場合、
                // 最終値を重ねて貼り付けない。
                if (_completionPending)
                {
                    return new IntPtr(1);
                }

                // まだ 1 件も集めていなければ通常の Ctrl+V は妨げない。
                if (_items.Count == 0)
                {
                    PostIfActive(() => _captureRejected(SequentialCaptureRejection.NothingCaptured));
                    return CallNextHookEx(_keyboardHook, code, wParam, lParam);
                }

                if (Phase == SequentialCopyPastePhase.Capturing)
                {
                    // 自分がこれから行うクリップボード更新を収集しないよう、先に監視を外す。
                    BeginPasting();
                }

                string value = _items[_pasteIndex];
                if (!ClipboardService.TryCopy(value))
                {
                    // 古いクリップボード内容が貼られるほうが危険なので、この Ctrl+V は止める。
                    PostIfActive(_pasteFailed);
                    return new IntPtr(1);
                }

                _pasteIndex++;
                RestartTimer();

                SequentialPasteProgress progress = new(_pasteIndex, _items.Count, value);
                bool isLast = _pasteIndex >= _items.Count;
                _completionPending = isLast;

                // フックから戻る前にメニュー再構築や通知表示をすると、貼り付け先への
                // V キー配送を遅らせる。進捗通知と終了処理は戻った直後に行う。
                _dispatcher.BeginInvoke(
                    new Action(() => NotifyPaste(progress, isLast)),
                    DispatcherPriority.Background);

                return CallNextHookEx(_keyboardHook, code, wParam, lParam);
            }
            catch (Exception)
            {
                // 途中状態のクリップボードを誤って貼らせない。
                PostIfActive(_pasteFailed);
                return new IntPtr(1);
            }
        }

        /// <summary>
        /// コピー側のキーを見て、必要なら意図の印を立てる。
        /// 貼り付けの Ctrl+V はここへは来ない。
        /// </summary>
        private void HandleCaptureKey(uint virtualKey, bool isKeyDown, bool isKeyUp)
        {
            if (isKeyUp)
            {
                if (_copyKeyDownVirtualKey == virtualKey)
                {
                    _copyKeyDownVirtualKey = 0;
                }

                return;
            }

            if (!isKeyDown)
            {
                return;
            }

            if (IsCopyCombination(virtualKey))
            {
                // キーリピートでは印を立て直さない。同じ内容を改めてコピーした場合は、
                // キーを離した後の次の押下になるため別の 1 件として収集できる。
                if (_copyKeyDownVirtualKey != virtualKey)
                {
                    _copyKeyDownVirtualKey = virtualKey;
                    ArmCopyIntent();
                }

                return;
            }

            // 「操作した直後」では、アプリ独自のコピー操作（Ctrl+Shift+C など）も拾いたい。
            // どのキーがコピーに割り当てられているかは分からないので、
            // 押されたこと自体を根拠にする。
            if (_trigger == SequentialCaptureTrigger.UserInput)
            {
                ArmCopyIntent();
            }
        }

        /// <summary>コピー・切り取りとして広く使われているキーの組み合わせかどうか。</summary>
        private static bool IsCopyCombination(uint virtualKey) => virtualKey switch
        {
            // Ctrl+Insert は端末エミュレータや古いアプリで今も使われる。
            // Ctrl+X は切り取りだが、集めたいのは「クリップボードに載った文字列」なので同じ扱い。
            VkC or VkX or VkInsert => IsControlDown(),
            VkDelete => IsShiftDown(),
            _ => false,
        };

        /// <summary>
        /// マウスの操作を見て、意図の印を立てる。
        /// <see cref="SequentialCaptureTrigger.UserInput"/> のときだけ張られる。
        /// </summary>
        private IntPtr MouseHookProc(int code, IntPtr wParam, IntPtr lParam)
        {
            // 移動は毎秒何百回も届く。ここで構造体を読むと PC 全体の操作が重くなるうえ、
            // 応答が LowLevelHooksTimeout を超えると Windows にフックを外される
            // （外れても通知は来ない）。wParam だけを見て早く帰る。
            int message = wParam.ToInt32();
            if (code < 0 || _disposed || message == WmMouseMove || !IsButtonMessage(message))
            {
                return CallNextHookEx(_mouseHook, code, wParam, lParam);
            }

            try
            {
                MouseHookData data = Marshal.PtrToStructure<MouseHookData>(lParam);
                if ((data.Flags & LlmhfInjected) == 0)
                {
                    // 押下と離上の両方で印を立てる。右クリックメニューの「コピー」は
                    // 項目を押した時点で、選択しただけでコピーする端末は
                    // ボタンを離した時点でクリップボードが変わるため。
                    ArmCopyIntent();
                }
            }
            catch (Exception)
            {
                // 印を立てられなかっただけ。利用者の入力は妨げずに流す。
            }

            return CallNextHookEx(_mouseHook, code, wParam, lParam);
        }

        /// <summary>ボタンの押下・離上かどうか。ホイールと移動は含めない。</summary>
        private static bool IsButtonMessage(int message) => message
            is 0x0201 or 0x0202  // WM_LBUTTONDOWN / WM_LBUTTONUP
            or 0x0204 or 0x0205  // WM_RBUTTONDOWN / WM_RBUTTONUP
            or 0x0207 or 0x0208  // WM_MBUTTONDOWN / WM_MBUTTONUP
            or 0x020B or 0x020C; // WM_XBUTTONDOWN / WM_XBUTTONUP

        private void NotifyPaste(SequentialPasteProgress progress, bool isLast)
        {
            if (_disposed)
            {
                return;
            }

            _pasted(progress);

            if (!isLast)
            {
                return;
            }

            Dispose();
            _completed();
        }

        private void BeginPasting()
        {
            Phase = SequentialCopyPastePhase.Pasting;
            ClearCopyIntent();
            StopListening();

            // 貼り付けに移ったらマウスは見なくてよい。
            // 低レベルフックは、張っているあいだシステム全体の入力がこのアプリを経由する。
            // 必要な間だけ張るという方針どおり、ここで外す。
            StopMouseHook();
            RestartTimer();
        }

        private void PostIfActive(Action action)
        {
            _dispatcher.BeginInvoke(
                new Action(() =>
                {
                    if (!_disposed)
                    {
                        action();
                    }
                }),
                DispatcherPriority.Background);
        }

        private static bool IsControlDown()
            => IsKeyDown(VkControl) || IsKeyDown(VkLControl) || IsKeyDown(VkRControl);

        private static bool IsShiftDown() => IsKeyDown(VkShift);

        private static bool IsKeyDown(uint virtualKey)
            => (GetAsyncKeyState((int)virtualKey) & 0x8000) != 0;

        private void ArmCopyIntent()
        {
            // Any では印を使わない。押されるたびにシーケンス番号を取りに行く分だけ無駄になる。
            if (_trigger == SequentialCaptureTrigger.Any
                || Phase != SequentialCopyPastePhase.Capturing)
            {
                return;
            }

            long now = Environment.TickCount64;
            long previousExpiresAt = Interlocked.Exchange(
                ref _copyIntentExpiresAt,
                now + (long)CopyIntentLifetime.TotalMilliseconds);

            // まだ消費していない印が生きているなら、期限を延ばすだけにする。
            //
            // ここで番号を取り直すと、すでにコピーが済んで通知待ちの更新まで
            // 「操作より前に起きたもの」と判定して落としてしまう。
            // 「操作した直後」ではキーもマウスも印を立てるので、
            // コピーの直後に届く次の入力（キーリピート、ボタンの離上）で必ず踏む。
            if (previousExpiresAt != 0 && now <= previousExpiresAt)
            {
                return;
            }

            // すでにキューへ入っていた古い WM_CLIPBOARDUPDATE を誤って拾わないよう、
            // 操作した時点のシーケンス番号を覚える。
            _copyIntentSequenceNumber = GetClipboardSequenceNumber();
        }

        /// <summary>この更新を 1 件として採ってよいか。設定によって判定が変わる。</summary>
        private bool TryAcceptUpdate(uint sequenceNumber)
        {
            if (_trigger == SequentialCaptureTrigger.Any)
            {
                // 意図の印を使わない設定では、同じコピーの多重通知を時間で間引く。
                //
                // 基準にするのは「採れた」時刻だけで、空や非テキストで断った更新は
                // 数えない。1 通目にテキストが載っておらず 2 通目で載るアプリがあり、
                // 断った時刻を基準にすると、その 2 通目まで落として 1 件失うため。
                return _lastCaptureAt == 0
                    || Environment.TickCount64 - _lastCaptureAt
                        >= (long)CaptureDebounce.TotalMilliseconds;
            }

            // WM_CLIPBOARDUPDATE は利用者の操作だけでなく、クリップボード所有元の終了や
            // 遅延レンダリングでも届く。直前に実際の操作があった更新だけを収集する。
            //
            // 印は 1 回しか消費できない。1 回のコピーで形式を順に載せるアプリでは
            // 通知が複数回届くが、2 回目以降はここで落ちる。
            return TryConsumeCopyIntent(sequenceNumber);
        }

        private bool TryConsumeCopyIntent(uint sequenceNumber)
        {
            long expiresAt = Interlocked.Exchange(ref _copyIntentExpiresAt, 0);
            if (expiresAt == 0 || Environment.TickCount64 > expiresAt)
            {
                return false;
            }

            // 取得不能時の 0 は時刻条件だけで判定する。通常は番号が変わったことまで確認し、
            // 操作より前に発生してキューへ残っていた通知を除外する。
            return sequenceNumber == 0
                || _copyIntentSequenceNumber == 0
                || sequenceNumber != _copyIntentSequenceNumber;
        }

        private void ClearCopyIntent()
            => Interlocked.Exchange(ref _copyIntentExpiresAt, 0);

        private void RestartTimer()
        {
            _timer.Stop();
            _timer.Start();
        }

        private void OnTimeout(object? sender, EventArgs e)
        {
            Dispose();
            _timedOut();
        }

        private void StopListening()
        {
            if (!_listening)
            {
                return;
            }

            _ = RemoveClipboardFormatListener(_source.Handle);
            _listening = false;
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            ClearCopyIntent();
            _timer.Stop();
            _timer.Tick -= OnTimeout;
            StopListening();

            if (_keyboardHook != IntPtr.Zero)
            {
                _ = UnhookWindowsHookEx(_keyboardHook);
                _keyboardHook = IntPtr.Zero;
            }

            StopMouseHook();

            DisposeSource();
        }

        private void StopMouseHook()
        {
            if (_mouseHook == IntPtr.Zero)
            {
                return;
            }

            _ = UnhookWindowsHookEx(_mouseHook);
            _mouseHook = IntPtr.Zero;
        }

        private void DisposeSource()
        {
            if (_sourceDisposed)
            {
                return;
            }

            _sourceDisposed = true;
            _source.RemoveHook(WndProc);
            _source.Dispose();
        }

        [StructLayout(LayoutKind.Sequential)]
        private readonly struct KeyboardHookData
        {
            public readonly uint VirtualKey;
            public readonly uint ScanCode;
            public readonly uint Flags;
            public readonly uint Time;
            public readonly IntPtr ExtraInfo;
        }

        /// <summary>MSLLHOOKSTRUCT。読むのはボタン操作のときだけ。</summary>
        [StructLayout(LayoutKind.Sequential)]
        private readonly struct MouseHookData
        {
            public readonly int X;
            public readonly int Y;
            public readonly uint MouseData;
            public readonly uint Flags;
            public readonly uint Time;
            public readonly IntPtr ExtraInfo;
        }

        private delegate IntPtr LowLevelKeyboardProc(int code, IntPtr wParam, IntPtr lParam);

        private delegate IntPtr LowLevelMouseProc(int code, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool AddClipboardFormatListener(IntPtr hwnd);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool RemoveClipboardFormatListener(IntPtr hwnd);

        [DllImport("user32.dll")]
        private static extern uint GetClipboardSequenceNumber();

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr SetWindowsHookEx(
            int hookId,
            LowLevelKeyboardProc callback,
            IntPtr module,
            uint threadId);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr SetWindowsHookEx(
            int hookId,
            LowLevelMouseProc callback,
            IntPtr module,
            uint threadId);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool UnhookWindowsHookEx(IntPtr hook);

        [DllImport("user32.dll")]
        private static extern IntPtr CallNextHookEx(
            IntPtr hook,
            int code,
            IntPtr wParam,
            IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern short GetAsyncKeyState(int virtualKey);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr GetModuleHandle(string? moduleName);
    }
}
