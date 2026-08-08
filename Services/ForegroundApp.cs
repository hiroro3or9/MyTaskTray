using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace MyTaskTray.Services
{
    /// <summary>
    /// メニューを開く操作を受け取った時点で前面にあったウィンドウの情報。
    ///
    /// <para>
    /// メニューを組み立てる時点では、前面は既に自分（MenuHostWindow やメニュー自身）に
    /// 変わっている。そのため「開く操作を受け取った瞬間」に取って持ち回す必要がある。
    /// </para>
    /// </summary>
    internal sealed record ForegroundApp(string ProcessName, string Title)
    {
        /// <summary>前面ウィンドウが無い、または情報を取れなかった状態。</summary>
        public static ForegroundApp Unknown { get; } = new(string.Empty, string.Empty);

        /// <summary>プロセス名を取れているかどうか。取れていない場合は絞り込みを行わない。</summary>
        public bool IsKnown => !string.IsNullOrEmpty(ProcessName);

        /// <summary>
        /// 差し込みや画面表示で使う短いアプリ名。実行ファイル名末尾の <c>.exe</c> は除く。
        /// </summary>
        public string Name => ProcessName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            ? ProcessName[..^4]
            : ProcessName;
    }

    /// <summary>前面ウィンドウからプロセス名とタイトルを取り出す。</summary>
    internal static class ForegroundWindowInfo
    {
        /// <summary>
        /// タイトルとして読み込む上限。数 KB のタイトルを付けるアプリがあるため、
        /// 表示条件と差し込みに使う分だけ取る。
        /// </summary>
        private const int MaxTitleLength = 512;

        private const int ProcessQueryLimitedInformation = 0x1000;

        /// <summary>
        /// 自分自身の実行ファイル名。設定画面や通知が前面のときに
        /// 「MyTaskTray が前面」と判定してしまうと、条件の指定にも候補の一覧にも意味がない。
        /// </summary>
        private static readonly string OwnProcessName =
            Path.GetFileName(Environment.ProcessPath ?? string.Empty);

        /// <summary>
        /// 指定したウィンドウのプロセス名とタイトルを取る。
        /// 取れない場合は <see cref="ForegroundApp.Unknown"/> を返し、呼び出し側は絞り込みを行わない。
        /// </summary>
        public static ForegroundApp Capture(IntPtr window)
        {
            if (window == IntPtr.Zero || !IsWindow(window))
            {
                return ForegroundApp.Unknown;
            }

            string process = GetProcessName(window);
            if (string.IsNullOrEmpty(process))
            {
                return ForegroundApp.Unknown;
            }

            if (!string.IsNullOrEmpty(OwnProcessName)
                && string.Equals(process, OwnProcessName, StringComparison.OrdinalIgnoreCase))
            {
                return ForegroundApp.Unknown;
            }

            return new ForegroundApp(process, GetWindowTitle(window));
        }

        /// <summary>
        /// 実行ファイル名を取る。
        ///
        /// <para>
        /// <see cref="System.Diagnostics.Process"/> の <c>ProcessName</c> は、
        /// 権限の違うプロセスや保護されたプロセスで例外になる。
        /// <c>PROCESS_QUERY_LIMITED_INFORMATION</c> で開いて
        /// <c>QueryFullProcessImageName</c> を使うほうが素直に取れる。
        /// </para>
        ///
        /// <para>
        /// ストアアプリ（電卓・設定など）は前面ウィンドウが <c>ApplicationFrameHost.exe</c> になり、
        /// 中の実際のアプリ名は取れない。子ウィンドウをたどる方法は対象が限られていて確実でもないため、
        /// ここでは追わずに見えたままを返す。
        /// </para>
        /// </summary>
        private static string GetProcessName(IntPtr window)
        {
            _ = GetWindowThreadProcessId(window, out uint processId);
            if (processId == 0)
            {
                return string.Empty;
            }

            IntPtr handle = OpenProcess(ProcessQueryLimitedInformation, false, processId);
            if (handle == IntPtr.Zero)
            {
                return string.Empty;
            }

            try
            {
                StringBuilder buffer = new(1024);
                int size = buffer.Capacity;
                if (!QueryFullProcessImageName(handle, 0, buffer, ref size))
                {
                    return string.Empty;
                }

                // size には書き込まれた文字数が入るが、StringBuilder には終端まで写されているため
                // そのまま文字列にできる
                string path = buffer.ToString();
                return string.IsNullOrEmpty(path) ? string.Empty : Path.GetFileName(path);
            }
            catch (Exception)
            {
                // 判定できないことは通常の分岐として扱う（呼び出し側で絞り込みを行わない）
                return string.Empty;
            }
            finally
            {
                _ = CloseHandle(handle);
            }
        }

        /// <summary>
        /// ウィンドウタイトルを取る。
        ///
        /// <para>
        /// 他プロセスのウィンドウに対して <c>GetWindowText</c> はキャプションを直接読むため、
        /// 相手が応答していなくても固まらない（<c>WM_GETTEXT</c> を送るのは同一プロセスの場合だけ）。
        /// </para>
        /// </summary>
        private static string GetWindowTitle(IntPtr window)
        {
            try
            {
                int length = GetWindowTextLength(window);
                if (length <= 0)
                {
                    return string.Empty;
                }

                int capacity = Math.Min(length, MaxTitleLength) + 1;
                StringBuilder buffer = new(capacity);
                int copied = GetWindowText(window, buffer, capacity);
                return copied <= 0 ? string.Empty : buffer.ToString();
            }
            catch (Exception)
            {
                return string.Empty;
            }
        }

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool IsWindow(IntPtr hWnd);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern int GetWindowTextLength(IntPtr hWnd);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern int GetWindowText(IntPtr hWnd, StringBuilder text, int maxCount);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr OpenProcess(
            int desiredAccess,
            [MarshalAs(UnmanagedType.Bool)] bool inheritHandle,
            uint processId);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool QueryFullProcessImageName(
            IntPtr process,
            int flags,
            StringBuilder exeName,
            ref int size);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CloseHandle(IntPtr handle);
    }
}
