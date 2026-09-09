using System.Runtime.InteropServices;
using System.Text;
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

        /// <summary>Pause / Break キー（VK_PAUSE）。</summary>
        private const uint VkPause = 0x13;

        /// <summary>変換キー（VK_CONVERT）。</summary>
        private const uint VkConvert = 0x1C;

        /// <summary>無変換キー（VK_NONCONVERT）。</summary>
        private const uint VkNonConvert = 0x1D;

        /// <summary>アプリケーションキー（VK_APPS）。右クリックメニューを出すキー。</summary>
        private const uint VkApps = 0x5D;

        /// <summary>F13。物理キーとしては無いことが多く、割り当てソフト経由で使う。</summary>
        private const uint VkF13 = 0x7C;

        /// <summary>F24。</summary>
        private const uint VkF24 = 0x87;

        /// <summary>
        /// 修飾キーなしでも登録してよいキー。
        ///
        /// <para>
        /// 通常の文字キーを単体で登録すると、そのキーが全アプリで打てなくなり、
        /// 設定画面でも打てないため復旧できない。一方この一覧のキーは文字を入力しないので、
        /// 単体で登録しても入力そのものは壊れない（そのキー本来の機能は奪う）。
        /// </para>
        ///
        /// <para>
        /// <strong>状態を持つキーは入れない。</strong>
        /// CapsLock・NumLock・ScrollLock の反転はホットキー処理より下で起きるため、
        /// 登録しても押すたびに状態がずれる。CapsLock を使いたい場合は、
        /// OS 側で無変換や F13 へ割り当ててから指定する。
        /// </para>
        /// </summary>
        private static readonly uint[] StandaloneKeys =
        [
            VkNonConvert,
            VkConvert,
            VkApps,
            VkPause,
        ];

        /// <summary>
        /// 名前でも押しても指定できるキー。入力欄は IME を切っているので、
        /// 日本語名のキーはローマ字表記でも受け付ける。
        /// </summary>
        private static readonly (string[] Names, uint VirtualKey, string DisplayName)[] NamedKeys =
        [
            (["無変換", "MUHENKAN", "NONCONVERT"], VkNonConvert, "無変換"),
            (["変換", "HENKAN", "CONVERT"], VkConvert, "変換"),
            (["アプリケーション", "APPS", "APPLICATION"], VkApps, "アプリケーション"),
            (["PAUSE", "BREAK", "一時停止"], VkPause, "Pause"),
        ];

        /// <summary>
        /// 修飾キーなしで登録してよいキーかどうか。
        /// F13〜F24 は普通のキーボードに無く、奪うものが何も無いため単体で許す。
        /// </summary>
        private static bool IsStandaloneKey(uint virtualKey)
            => StandaloneKeys.Contains(virtualKey)
                || virtualKey is >= VkF13 and <= VkF24;

        /// <summary>
        /// Ctrl+Alt+V のような表記を解釈する。空欄は呼び出し側で「無効」として扱う。
        /// 通常の文字入力を奪わないよう、Ctrl / Alt / Win のいずれかを必須にする。
        /// ただし <see cref="IsStandaloneKey"/> が認めるキーは単体でも登録できる。
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

            string[] parts = ToHalfWidth(text).Split('+', StringSplitOptions.TrimEntries);
            if (parts.Any(string.IsNullOrEmpty))
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
                    error = "キーは A〜Z、0〜9、F1〜F24、無変換、変換、アプリケーション、Pause"
                        + " のいずれかを指定してください。";
                    return false;
                }
            }

            if (virtualKey == 0)
            {
                error = "通常のキーを 1 つ指定してください。";
                return false;
            }

            if ((modifiers & (ModControl | ModAlt | ModWin)) == 0 && !IsStandaloneKey(virtualKey))
            {
                error = "Ctrl、Alt、Win のいずれかを含めてください"
                    + "（無変換・変換・アプリケーション・Pause・F13〜F24 は単体でも指定できます）。";
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

        /// <summary>
        /// 全角の英数字・記号・空白を半角へ揃える。
        /// 日本語入力を有効にしたまま打つと「Ｃｔｒｌ＋Ｖ」のような全角になるが、
        /// 見た目はほとんど同じで解釈だけ失敗するため、利用者には理由が分からない。
        /// </summary>
        private static string ToHalfWidth(string text)
        {
            try
            {
                return text.Normalize(NormalizationForm.FormKC);
            }
            catch (ArgumentException)
            {
                // 正規化できない文字を含む場合はそのまま読み、後続の検証で弾く
                return text;
            }
        }

        private static bool TryParseKey(string text, out uint virtualKey, out string displayName)
        {
            string key = text.Trim().ToUpperInvariant();

            // 文字を入力しないキーは、名前でも指定できるようにする。
            // 表記は NamedKeys にまとめてあり、設定画面ではキーを押しても入る
            foreach ((string[] names, uint named, string name) in NamedKeys)
            {
                if (names.Contains(key, StringComparer.Ordinal))
                {
                    virtualKey = named;
                    displayName = name;
                    return true;
                }
            }

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
    /// 登録するホットキーの識別子。<c>RegisterHotKey</c> はウィンドウごとに識別子を見るため、
    /// 用途ごとに別の値を割り当てておくと、受け取り側の取り違えに気づける。
    /// </summary>
    internal static class GlobalHotKeyIds
    {
        /// <summary>トレイメニューをカーソル位置へ表示する。</summary>
        public const int Menu = 0x4D54; // "MT"

        /// <summary>選択中の文字列とクリップボードを入れ替える。</summary>
        public const int SwapCopy = 0x4D55;
    }

    /// <summary>
    /// アプリがウィンドウを表示していない間も、設定されたグローバルホットキーを受け取る。
    /// </summary>
    internal sealed class GlobalHotKey : IDisposable
    {
        private const int WmHotKey = 0x0312;
        private const uint ModNoRepeat = 0x4000;
        private static readonly IntPtr MessageOnlyWindow = new(-3);

        private readonly Action _pressed;
        private readonly HwndSource _source;
        private readonly int _hotKeyId;
        private bool _registered;
        private bool _disposed;

        /// <param name="hotKeyId">
        /// <see cref="GlobalHotKeyIds"/> のいずれか。用途ごとに別の値を渡す。
        /// </param>
        public GlobalHotKey(HotKeyGesture gesture, Action pressed, int hotKeyId)
        {
            _pressed = pressed;
            _hotKeyId = hotKeyId;
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
                _hotKeyId,
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
            if (message == WmHotKey && wParam.ToInt32() == _hotKeyId)
            {
                handled = true;

                // ここはウィンドウプロシージャの中で、例外を外へ出すと
                // WPF のメッセージループを巻き込んでアプリごと終了してしまう。
                // 利用者への通知は呼び出し側の責務とし、ここでは外へ漏らさないことだけを保証する
                try
                {
                    _pressed();
                }
                catch (Exception)
                {
                }
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
                _ = UnregisterHotKey(_source.Handle, _hotKeyId);
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
