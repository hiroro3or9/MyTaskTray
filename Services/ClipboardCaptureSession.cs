using System.Runtime.InteropServices;
using System.Windows.Interop;
using System.Windows.Threading;

namespace MyTaskTray.Services
{
    /// <summary>複数回キャプチャの現在位置。</summary>
    internal readonly record struct ClipboardCaptureProgress(
        int CapturedCount,
        int TotalCount,
        string CurrentName);

    /// <summary>
    /// <c>{input:名前}</c> の値を、利用者の通常のコピー操作から順番に受け取る一時セッション。
    /// 常時履歴は取らず、セッション中だけ WM_CLIPBOARDUPDATE を購読する。
    /// </summary>
    internal sealed class ClipboardCaptureSession : IDisposable
    {
        private const int WmClipboardUpdate = 0x031D;
        private static readonly IntPtr MessageOnlyWindow = new(-3);
        private static readonly TimeSpan Timeout = TimeSpan.FromMinutes(2);

        private readonly IReadOnlyList<string> _names;
        private readonly Dictionary<string, string> _values = new(StringComparer.OrdinalIgnoreCase);
        private readonly Action<ClipboardCaptureProgress> _progressed;
        private readonly Action<IReadOnlyDictionary<string, string>> _completed;
        private readonly Action _timedOut;
        private readonly Action<string> _rejected;
        private readonly HwndSource _source;
        private readonly DispatcherTimer _timer;

        private int _index;
        private bool _listening;
        private bool _handling;
        private bool _disposed;
        private bool _sourceDisposed;

        public ClipboardCaptureSession(
            IReadOnlyList<string> names,
            Action<ClipboardCaptureProgress> progressed,
            Action<IReadOnlyDictionary<string, string>> completed,
            Action timedOut,
            Action<string> rejected)
        {
            if (names.Count == 0)
            {
                throw new ArgumentException("入力名を 1 つ以上指定してください。", nameof(names));
            }

            _names = names;
            _progressed = progressed;
            _completed = completed;
            _timedOut = timedOut;
            _rejected = rejected;

            HwndSourceParameters parameters = new("MyTaskTray.ClipboardCapture")
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

        public ClipboardCaptureProgress Progress
            => new(_index, _names.Count, _names[_index]);

        /// <summary>クリップボード変更通知の受信を始める。登録できなければ false。</summary>
        public bool Start()
        {
            if (_disposed || _listening)
            {
                return false;
            }

            _listening = AddClipboardFormatListener(_source.Handle);
            if (!_listening)
            {
                Dispose();
                return false;
            }

            RestartTimer();
            return true;
        }

        private IntPtr WndProc(
            IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (message != WmClipboardUpdate || !_listening || _handling || _disposed)
            {
                return IntPtr.Zero;
            }

            handled = true;
            _handling = true;
            try
            {
                string value = ClipboardService.GetText();
                if (string.IsNullOrWhiteSpace(value))
                {
                    RestartTimer();
                    _rejected(_names[_index]);
                    return IntPtr.Zero;
                }

                _values[_names[_index]] = value.Trim();
                _index++;

                if (_index >= _names.Count)
                {
                    Dictionary<string, string> completed = new(_values, StringComparer.OrdinalIgnoreCase);
                    // WndProc の処理中に HwndSource 自体を破棄すると、呼び出し元の
                    // ウィンドウプロシージャが戻る前に HWND が消える。通知だけ先に解除し、
                    // HwndSource の破棄と完了処理はメッセージから戻ったあとに行う。
                    _disposed = true;
                    StopListening();
                    _source.Dispatcher.BeginInvoke(
                        new Action(() =>
                        {
                            DisposeSource();
                            _completed(completed);
                        }),
                        DispatcherPriority.Send);
                    return IntPtr.Zero;
                }

                RestartTimer();
                _progressed(Progress);
            }
            finally
            {
                _handling = false;
            }

            return IntPtr.Zero;
        }

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

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            StopListening();
            DisposeSource();
        }

        private void StopListening()
        {
            _timer.Stop();
            _timer.Tick -= OnTimeout;

            if (_listening)
            {
                _ = RemoveClipboardFormatListener(_source.Handle);
                _listening = false;
            }
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

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool AddClipboardFormatListener(IntPtr hwnd);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool RemoveClipboardFormatListener(IntPtr hwnd);
    }
}
