using System.Runtime.InteropServices;
using System.Windows.Interop;
using System.Windows.Threading;

namespace MyTaskTray.Services
{
    /// <summary>連続コピー＆ペーストの現在の段階。</summary>
    internal enum SequentialCopyPastePhase
    {
        Capturing,
        Pasting,
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
    /// クリップボード変更通知とキーボードフックを有効にする。
    /// </summary>
    internal sealed class SequentialCopyPasteSession : IDisposable
    {
        private const int WmClipboardUpdate = 0x031D;
        private const int WmKeyDown = 0x0100;
        private const int WmKeyUp = 0x0101;
        private const int WmSysKeyDown = 0x0104;
        private const int WmSysKeyUp = 0x0105;
        private const int WhKeyboardLl = 13;
        private const uint VkControl = 0x11;
        private const uint VkLControl = 0xA2;
        private const uint VkRControl = 0xA3;
        private const uint VkV = 0x56;
        private const uint LlkhfInjected = 0x00000010;
        private static readonly IntPtr MessageOnlyWindow = new(-3);
        private static readonly TimeSpan Timeout = TimeSpan.FromMinutes(10);

        private readonly Action<string, int> _captured;
        private readonly Action _captureRejected;
        private readonly Action<SequentialPasteProgress> _pasted;
        private readonly Action _pasteFailed;
        private readonly Action _completed;
        private readonly Action _timedOut;
        private readonly HwndSource _source;
        private readonly Dispatcher _dispatcher;
        private readonly DispatcherTimer _timer;
        private readonly LowLevelKeyboardProc _keyboardProc;
        private readonly List<string> _items = [];

        private IntPtr _keyboardHook;
        private int _pasteIndex;
        private bool _listening;
        private bool _handlingClipboard;
        private bool _pasteKeyDown;
        private bool _completionPending;
        private bool _disposed;
        private bool _sourceDisposed;

        public SequentialCopyPasteSession(
            Action<string, int> captured,
            Action captureRejected,
            Action<SequentialPasteProgress> pasted,
            Action pasteFailed,
            Action completed,
            Action timedOut)
        {
            _captured = captured;
            _captureRejected = captureRejected;
            _pasted = pasted;
            _pasteFailed = pasteFailed;
            _completed = completed;
            _timedOut = timedOut;
            _dispatcher = Dispatcher.CurrentDispatcher;
            _keyboardProc = KeyboardHookProc;

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

        public int CapturedCount => _items.Count;

        public int PastedCount => _pasteIndex;

        public int RemainingCount => _items.Count - _pasteIndex;

        /// <summary>
        /// クリップボードと Ctrl+V の監視を始める。どちらかを登録できなければ false。
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

            handled = true;
            _handlingClipboard = true;
            try
            {
                string value = ClipboardService.GetText();
                if (string.IsNullOrEmpty(value))
                {
                    RestartTimer();
                    _captureRejected();
                    return IntPtr.Zero;
                }

                // 同じ値が複数行に現れる業務データもあるため、重複は除外しない。
                _items.Add(value);
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
                if (data.VirtualKey != VkV || (data.Flags & LlkhfInjected) != 0)
                {
                    return CallNextHookEx(_keyboardHook, code, wParam, lParam);
                }

                int message = wParam.ToInt32();
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
                    PostIfActive(_captureRejected);
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
            StopListening();
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

        private static bool IsKeyDown(uint virtualKey)
            => (GetAsyncKeyState((int)virtualKey) & 0x8000) != 0;

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
            _timer.Stop();
            _timer.Tick -= OnTimeout;
            StopListening();

            if (_keyboardHook != IntPtr.Zero)
            {
                _ = UnhookWindowsHookEx(_keyboardHook);
                _keyboardHook = IntPtr.Zero;
            }

            DisposeSource();
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

        private delegate IntPtr LowLevelKeyboardProc(int code, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool AddClipboardFormatListener(IntPtr hwnd);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool RemoveClipboardFormatListener(IntPtr hwnd);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr SetWindowsHookEx(
            int hookId,
            LowLevelKeyboardProc callback,
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
