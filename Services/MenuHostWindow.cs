using System.Windows.Forms;

namespace MyTaskTray.Services
{
    /// <summary>
    /// トレイメニューを出す直前に前面化するためだけの、画面に現れないウィンドウ。
    ///
    /// <para>
    /// <see cref="ContextMenuStrip"/> は独立したポップアップウィンドウで、
    /// アプリが前面になっていない状態で表示すると
    /// 「他所をクリックしても閉じない」「矢印キーや Enter が別のアプリへ行く」
    /// という 2 つの問題が起きる。表示の直前にどれか自分のウィンドウを前面化しておく必要があるが、
    /// このアプリは常駐中に見えるウィンドウを持たないため、その役目だけの窓を用意する。
    /// </para>
    /// <para>
    /// <c>WS_VISIBLE</c> を付けないので画面には出ないが、トップレベルなので
    /// <c>SetForegroundWindow</c> の対象にできる。
    /// <see cref="NotifyIcon"/> が右クリックメニューを出すときに使っている窓と同じ考え方。
    /// </para>
    /// </summary>
    internal sealed class MenuHostWindow : NativeWindow, IDisposable
    {
        // タスクバーや Alt+Tab に現れないようにするための保険。
        // WS_VISIBLE が無いため本来現れないが、明示しておく。
        private const int WsExToolWindow = 0x0080;

        public MenuHostWindow()
        {
            CreateHandle(new CreateParams
            {
                ExStyle = WsExToolWindow,
            });
        }

        public void Dispose()
        {
            if (Handle != IntPtr.Zero)
            {
                DestroyHandle();
            }
        }
    }
}
