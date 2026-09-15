using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Threading;
using MyTaskTray.Models;

namespace MyTaskTray.Services;

/// <summary>登録された半角の合図だけを照合する。キーを奪わず、確認できた入力だけ置き換える。</summary>
internal sealed class TextExpansionSession : IDisposable
{
    private readonly TextExpansionMatcher _matcher;
    private readonly Func<bool> _canExpand;
    private readonly Action<ClipItem, ForegroundApp, Func<bool>> _expand;
    private readonly Dispatcher _dispatcher;
    private readonly HookProc _keyboardProc;
    private readonly HookProc _mouseProc;
    private readonly WinEventProc _focusProc;
    private IntPtr _keyboard, _mouse, _focus, _foreground;
    private TextExpansionTarget _target;
    private long _revision;
    private long _lastKeyAt;
    private bool _disposed, _checking;

    public TextExpansionSession(IEnumerable<ClipItem> items, Func<bool> canExpand,
        Action<ClipItem, ForegroundApp, Func<bool>> expand)
    {
        _matcher = new(items);
        _canExpand = canExpand;
        _expand = expand;
        _dispatcher = Dispatcher.CurrentDispatcher;
        _keyboardProc = OnKeyboard;
        _mouseProc = OnMouse;
        _focusProc = (_, _, _, _, _, _, _) => Reset();
        try
        {
            IntPtr module = GetModuleHandle(null);
            _keyboard = SetWindowsHookEx(13, _keyboardProc, module, 0);
            _mouse = SetWindowsHookEx(14, _mouseProc, module, 0);
            _focus = SetWinEventHook(0x8005, 0x8005, IntPtr.Zero, _focusProc, 0, 0, 0);
            _foreground = SetWinEventHook(3, 3, IntPtr.Zero, _focusProc, 0, 0, 0);
            if (_keyboard == IntPtr.Zero || _mouse == IntPtr.Zero || _focus == IntPtr.Zero || _foreground == IntPtr.Zero)
                throw new Win32Exception(Marshal.GetLastWin32Error());
        }
        catch { Dispose(); throw; }
    }

    private void Reset()
    {
        _revision++;
        _matcher.Reset();
    }

    private IntPtr OnMouse(int code, IntPtr message, IntPtr data)
    {
        // カーソル移動だけでは止めない。クリック・ホイールで編集位置が変わり得る。
        if (code >= 0 && message.ToInt32() != 0x0200) Reset();
        return CallNextHookEx(_mouse, code, message, data);
    }

    private IntPtr OnKeyboard(int code, IntPtr message, IntPtr pointer)
    {
        try
        {
            if (code < 0 || _disposed) return CallNextHookEx(_keyboard, code, message, pointer);
            KeyboardData data = Marshal.PtrToStructure<KeyboardData>(pointer);
            if ((data.Flags & 0x10) != 0) Reset(); // 自分を含む合成入力を合図にしない。
            else if (message.ToInt32() is 0x0100 or 0x0104)
            {
                _revision++;
                long now = Environment.TickCount64;
                TextExpansionTarget target = TextExpansionTarget.Capture();
                if (target != _target || now - _lastKeyAt > 5000) Reset();
                _target = target;
                _lastKeyAt = now;
                if (_checking || target == default || !_canExpand() || ShortcutPressed()) Reset();
                else if (data.Key is not (0x10 or 0xA0 or 0xA1)) // Shift は大文字や記号の入力に必要。
                {
                    byte[] state = new byte[256];
                    state[0x10] = (byte)(Pressed(0x10) ? 0x80 : 0);
                    state[0x14] = (byte)(GetKeyState(0x14) & 1);
                    state[data.Key] = 0x80;
                    StringBuilder text = new(8);
                    int count = ToUnicodeEx(data.Key, data.Scan, state, text, text.Capacity, 4,
                        TextExpansionTarget.GetKeyboardLayout(target.Thread));
                    if (count != 1 || text[0] is < '!' or > '~') Reset();
                    else if (_matcher.Push(text[0]) is { } item)
                    {
                        long revision = _revision;
                        _checking = true;
                        _dispatcher.BeginInvoke(new Action(() => _ = CheckAndExpandAsync(item, target, revision)));
                    }
                }
            }
        }
        catch { Reset(); }
        return CallNextHookEx(_keyboard, code, message, pointer);
    }

    private async Task CheckAndExpandAsync(ClipItem item, TextExpansionTarget target, long revision)
    {
        bool ContextValid() => !_disposed && _revision == revision && _canExpand()
            && TextExpansionTarget.Capture() == target && !ShortcutPressed();
        bool StillValid() => ContextValid() && !Pressed(0x10);
        try
        {
            // 最後のキーは入力先にそのまま渡す。入力先が処理したことを UIA で確かめてから削除する。
            await Task.Delay(35);
            // 大文字を入力した直後の Shift は、利用者が離すのを短時間だけ待つ。
            for (int attempt = 0; attempt < 20 && ContextValid() && Pressed(0x10); attempt++)
                await Task.Delay(20);
            if (!StillValid() || !target.IsImeOff()) return;
            Task<bool> check = Task.Run(() => target.HasTriggerAtCaret(item.ExpansionTrigger));
            if (await Task.WhenAny(check, Task.Delay(500)) != check)
            {
                // 応答しない UIA プロバイダーにワーカーを無制限に増やさない。
                await check;
                return;
            }
            if (!await check || !StillValid() || !target.IsImeOff()) return;
            ForegroundApp app = ForegroundWindowInfo.Capture(target.Window);
            if (!app.IsKnown || !AppContextMatcher.Matches(item, app) || !StillValid()) return;
            _expand(item, app, StillValid);
        }
        catch { /* 確認できない場合は元の入力を残す。 */ }
        finally
        {
            Reset();
            _checking = false;
        }
    }

    private static bool Pressed(int key) => (GetAsyncKeyState(key) & 0x8000) != 0;
    private static bool ShortcutPressed() => Pressed(0x11) || Pressed(0x12) || Pressed(0x5B) || Pressed(0x5C);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Reset();
        if (_keyboard != IntPtr.Zero) UnhookWindowsHookEx(_keyboard);
        if (_mouse != IntPtr.Zero) UnhookWindowsHookEx(_mouse);
        if (_focus != IntPtr.Zero) UnhookWinEvent(_focus);
        if (_foreground != IntPtr.Zero) UnhookWinEvent(_foreground);
        _keyboard = _mouse = _focus = _foreground = IntPtr.Zero;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KeyboardData { public uint Key, Scan, Flags, Time; public IntPtr Extra; }
    private delegate IntPtr HookProc(int code, IntPtr message, IntPtr data);
    private delegate void WinEventProc(IntPtr hook, uint eventId, IntPtr window, int objectId, int child, uint thread, uint time);
    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr SetWindowsHookEx(int id, HookProc callback, IntPtr module, uint thread);
    [DllImport("user32.dll")] private static extern bool UnhookWindowsHookEx(IntPtr hook);
    [DllImport("user32.dll")] private static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr message, IntPtr data);
    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr SetWinEventHook(uint min, uint max, IntPtr module, WinEventProc callback, uint process, uint thread, uint flags);
    [DllImport("user32.dll")] private static extern bool UnhookWinEvent(IntPtr hook);
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int key);
    [DllImport("user32.dll")] private static extern short GetKeyState(int key);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int ToUnicodeEx(uint key, uint scan, byte[] state, StringBuilder text, int count, uint flags, IntPtr layout);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandle(string? name);
}
