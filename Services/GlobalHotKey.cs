using System.Runtime.InteropServices;
using System.Windows.Interop;

namespace MyTaskTray.Services
{
    /// <summary>RegisterHotKey に渡すキーの組み合わせ。</summary>
    internal readonly record struct HotKeyGesture(uint Modifiers, uint VirtualKey, string DisplayName)
    {
        private const uint ModAlt = 0x0001;
        private const uint ModControl = 0x0002;
        private const uint ModShift = 0x0004;
        private const uint ModWin = 0x0008;

        /// <summary>
        /// Ctrl+Alt+V のような表記を解釈する。空欄は呼び出し側で「無効」として扱う。
        /// 通常の文字入力を奪わないよう、Ctrl / Alt / Win のいずれかを必須にする。
        /// </summary>
        public static bool TryParse(string? text, out HotKeyGesture gesture, out string error)
        {
            gesture = default;
            error = string.Empty;

            if (string.IsNullOrWhiteSpace(text))
            {
                error = "ホットキーが空です。";
                return false;
            }

            string[] parts = text.Split('+', StringSplitOptions.TrimEntries);
            if (parts.Length < 2 || parts.Any(string.IsNullOrEmpty))
            {
                error = "Ctrl+Alt+V のように、キーを + で区切って入力してください。";
                return false;
            }

            uint modifiers = 0;
            uint virtualKey = 0;
            string keyName = string.Empty;

            foreach (string part in parts)
            {
                uint modifier = part.ToLowerInvariant() switch
                {
                    "ctrl" or "control" => ModControl,
                    "alt" => ModAlt,
                    "shift" => ModShift,
                    "win" or "windows" => ModWin,
                    _ => 0,
                };

                if (modifier != 0)
                {
                    if ((modifiers & modifier) != 0)
                    {
                        error = $"{part} が重複しています。";
                        return false;
                    }

                    modifiers |= modifier;
                    continue;
                }

                if (virtualKey != 0)
                {
                    error = "通常のキーは 1 つだけ指定してください。";
                    return false;
                }

                if (!TryParseKey(part, out virtualKey, out keyName))
                {
                    error = "キーは A〜Z、0〜9、F1〜F24 のいずれかを指定してください。";
                    return false;
                }
            }

            if (virtualKey == 0)
            {
                error = "通常のキーを 1 つ指定してください。";
                return false;
            }

            if ((modifiers & (ModControl | ModAlt | ModWin)) == 0)
            {
                error = "Ctrl、Alt、Win のいずれかを含めてください。";
                return false;
            }

            List<string> displayParts = [];
            if ((modifiers & ModControl) != 0) displayParts.Add("Ctrl");
            if ((modifiers & ModAlt) != 0) displayParts.Add("Alt");
            if ((modifiers & ModShift) != 0) displayParts.Add("Shift");
            if ((modifiers & ModWin) != 0) displayParts.Add("Win");
            displayParts.Add(keyName);

            gesture = new HotKeyGesture(modifiers, virtualKey, string.Join("+", displayParts));
            return true;
        }

        private static bool TryParseKey(string text, out uint virtualKey, out string displayName)
        {
            string key = text.Trim().ToUpperInvariant();

            if (key.Length == 1)
            {
                char c = key[0];
                if (c is >= 'A' and <= 'Z' or >= '0' and <= '9')
                {
                    virtualKey = c;
                    displayName = c.ToString();
                    return true;
                }
            }

            if (key.Length is >= 2 and <= 3
                && key[0] == 'F'
                && int.TryParse(key[1..], out int functionNumber)
                && functionNumber is >= 1 and <= 24)
            {
                virtualKey = (uint)(0x70 + functionNumber - 1);
                displayName = "F" + functionNumber;
                return true;
            }

            virtualKey = 0;
            displayName = string.Empty;
            return false;
        }
    }

    /// <summary>
    /// アプリがウィンドウを表示していない間も、設定されたグローバルホットキーを受け取る。
    /// </summary>
    internal sealed class GlobalHotKey : IDisposable
    {
        private const int HotKeyId = 0x4D54; // "MT"
        private const int WmHotKey = 0x0312;
        private const uint ModNoRepeat = 0x4000;
        private static readonly IntPtr MessageOnlyWindow = new(-3);

        private readonly Action _pressed;
        private readonly HwndSource _source;
        private bool _registered;
        private bool _disposed;

        public GlobalHotKey(HotKeyGesture gesture, Action pressed)
        {
            _pressed = pressed;
            DisplayName = gesture.DisplayName;

            HwndSourceParameters parameters = new("MyTaskTray.GlobalHotKey")
            {
                ParentWindow = MessageOnlyWindow,
                WindowStyle = 0,
            };

            _source = new HwndSource(parameters);
            _source.AddHook(WndProc);
            _registered = RegisterHotKey(
                _source.Handle,
                HotKeyId,
                gesture.Modifiers | ModNoRepeat,
                gesture.VirtualKey);
        }

        /// <summary>登録しようとした正規化済みのキー表記。</summary>
        public string DisplayName { get; }

        /// <summary>ほかのアプリとの競合なく登録できたかどうか。</summary>
        public bool IsRegistered => _registered;

        private IntPtr WndProc(
            IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (message == WmHotKey && wParam.ToInt32() == HotKeyId)
            {
                handled = true;
                _pressed();
            }

            return IntPtr.Zero;
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;

            if (_registered)
            {
                _ = UnregisterHotKey(_source.Handle, HotKeyId);
                _registered = false;
            }

            _source.RemoveHook(WndProc);
            _source.Dispose();
        }

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool RegisterHotKey(IntPtr hwnd, int id, uint modifiers, uint virtualKey);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool UnregisterHotKey(IntPtr hwnd, int id);
    }
}
