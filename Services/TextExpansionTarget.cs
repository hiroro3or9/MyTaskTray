using System.Runtime.InteropServices;
using System.Windows.Automation;
using System.Windows.Automation.Text;

namespace MyTaskTray.Services;

internal readonly record struct TextExpansionTarget(IntPtr Window, IntPtr Focus, uint Thread)
{
    public static TextExpansionTarget Capture()
    {
        IntPtr window = GetForegroundWindow();
        uint thread = GetWindowThreadProcessId(window, out uint process);
        GuiThreadInfo info = new() { Size = Marshal.SizeOf<GuiThreadInfo>() };
        if (window == IntPtr.Zero || process == Environment.ProcessId || !GetGUIThreadInfo(thread, ref info)
            || info.Focus == IntPtr.Zero || (info.Flags & 0x1E) != 0) return default;
        return new(window, info.Focus, thread);
    }

    // 他プロセスの HIMC を使わず、そのスレッドの既定 IME ウィンドウへ問い合わせる。
    // タイムアウトや東アジア配列で状態不明のときは、オフと決めつけない。
    public bool IsImeOff()
    {
        IntPtr ime = ImmGetDefaultIMEWnd(Focus);
        if (ime != IntPtr.Zero)
            return SendMessageTimeout(ime, 0x0283, new IntPtr(5), IntPtr.Zero, 0x0002, 30, out UIntPtr result) != IntPtr.Zero
                && result == UIntPtr.Zero;
        int language = (int)(GetKeyboardLayout(Thread).ToInt64() & 0x3ff);
        return language is not (0x04 or 0x11 or 0x12);
    }

    /// <summary>
    /// 入力先が実際に合図を受け取ったことを確認する。フックの外のワーカースレッドで呼ぶ。
    /// 読み取るのはカーソル直前の合図の長さだけ。選択中・パスワード・未対応欄は変更しない。
    /// </summary>
    public bool HasTriggerAtCaret(string trigger)
    {
        try
        {
            AutomationElement? element = AutomationElement.FocusedElement;
            if (element is null || element.Current.IsPassword || !element.Current.IsEnabled
                || !element.TryGetCurrentPattern(TextPattern.Pattern, out object pattern)) return false;
            if (element.TryGetCurrentPattern(ValuePattern.Pattern, out object value)
                && ((ValuePattern)value).Current.IsReadOnly) return false;
            TextPatternRange[] selections = ((TextPattern)pattern).GetSelection();
            if (selections.Length != 1) return false;
            TextPatternRange range = selections[0];
            if (range.GetAttributeValue(TextPattern.IsReadOnlyAttribute) is true) return false;
            if (range.CompareEndpoints(TextPatternRangeEndpoint.Start, range, TextPatternRangeEndpoint.End) != 0)
                return false;
            range.MoveEndpointByUnit(TextPatternRangeEndpoint.Start, TextUnit.Character, -trigger.Length);
            return range.GetText(trigger.Length + 1) == trigger;
        }
        catch (Exception)
        {
            return false;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct GuiThreadInfo
    {
        public int Size;
        public uint Flags;
        public IntPtr Active, Focus, Capture, MenuOwner, MoveSize, Caret;
        public int Left, Top, Right, Bottom;
    }

    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint process);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetGUIThreadInfo(uint thread, ref GuiThreadInfo info);
    [DllImport("user32.dll")] internal static extern IntPtr GetKeyboardLayout(uint thread);
    [DllImport("imm32.dll")] private static extern IntPtr ImmGetDefaultIMEWnd(IntPtr window);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr SendMessageTimeout(IntPtr window, uint message, IntPtr wParam, IntPtr lParam, uint flags, uint timeout, out UIntPtr result);
}
