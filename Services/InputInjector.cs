using System.Runtime.InteropServices;

namespace MyTaskTray.Services
{
    /// <summary>
    /// 前面のウィンドウへキー操作を送る。
    /// Swap コピーが、利用者の代わりに Ctrl+C と Ctrl+V を押すために使う。
    ///
    /// <para>
    /// 送ったキーには <see cref="ExtraInfo"/> の印を付ける。
    /// 低レベルフック側は <c>LLKHF_INJECTED</c> で自動化された入力を除外できるが、
    /// 印があると「MyTaskTray が送ったもの」だと調べるときに分かる。
    /// </para>
    /// </summary>
    internal static class InputInjector
    {
        private const uint InputKeyboard = 1;
        private const uint KeyEventKeyUp = 0x0002;
        private const uint MapVirtualKeyToScanCode = 0;

        private const ushort VkControl = 0x11;
        private const ushort VkC = 0x43;
        private const ushort VkV = 0x56;

        /// <summary>送った入力に付ける印。"MTSW"（MyTaskTray SWap）。</summary>
        private static readonly IntPtr ExtraInfo = new(0x4D545357);

        /// <summary>
        /// 押されたままかどうかを見る修飾キー。左右を個別に見る。
        ///
        /// <para>
        /// <c>VK_CONTROL</c> のようなまとめ役へ「離した」を送っても、
        /// 実際に押されている左右のキーの状態は変わらない。
        /// </para>
        /// </summary>
        private static readonly ushort[] ModifierKeys =
        [
            0xA0, // VK_LSHIFT
            0xA1, // VK_RSHIFT
            0xA2, // VK_LCONTROL
            0xA3, // VK_RCONTROL
            0xA4, // VK_LMENU
            0xA5, // VK_RMENU
            0x5B, // VK_LWIN
            0x5C, // VK_RWIN
        ];

        /// <summary>Ctrl+C を送る。すべての入力を送れたら true。</summary>
        public static bool TrySendCopy() => TrySendControlShortcut(VkC);

        /// <summary>Ctrl+V を送る。すべての入力を送れたら true。</summary>
        public static bool TrySendPaste() => TrySendControlShortcut(VkV);

        /// <summary>
        /// 押されたままの修飾キーを、離した状態にする。
        ///
        /// <para>
        /// ホットキーで呼ばれた直後は、利用者の手がまだ Ctrl や Alt を押している。
        /// そのまま Ctrl+C を送ると Ctrl+Alt+C のような別の組み合わせになり、
        /// アプリによってはまったく違う動作になる。
        /// </para>
        ///
        /// <para>
        /// 離したことにするだけで、あとで押し直しはしない。
        /// 利用者が実際に指を離したときに、本来の離上が改めて届く。
        /// </para>
        /// </summary>
        public static bool TryReleaseModifiers()
        {
            List<Input> inputs = [];
            foreach (ushort key in ModifierKeys)
            {
                if (IsKeyDown(key))
                {
                    inputs.Add(KeyUp(key));
                }
            }

            return inputs.Count == 0 || Send([.. inputs]);
        }

        private static bool TrySendControlShortcut(ushort virtualKey) => Send(
        [
            KeyDown(VkControl),
            KeyDown(virtualKey),
            KeyUp(virtualKey),
            KeyUp(VkControl),
        ]);

        /// <summary>
        /// 入力をまとめて送る。1 回の <c>SendInput</c> で送ると、
        /// 途中に他のアプリの入力が割り込まない。
        /// </summary>
        private static bool Send(Input[] inputs)
        {
            if (inputs.Length == 0)
            {
                return true;
            }

            try
            {
                return SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<Input>())
                    == (uint)inputs.Length;
            }
            catch (Exception)
            {
                return false;
            }
        }

        private static Input KeyDown(ushort virtualKey) => CreateKey(virtualKey, flags: 0);

        private static Input KeyUp(ushort virtualKey) => CreateKey(virtualKey, KeyEventKeyUp);

        private static Input CreateKey(ushort virtualKey, uint flags) => new()
        {
            Type = InputKeyboard,
            Data = new InputUnion
            {
                Keyboard = new KeyboardInput
                {
                    VirtualKey = virtualKey,

                    // 仮想キーではなく走査コードを見るアプリ（一部のリモート接続やゲーム）がある。
                    // 添えても仮想キーの解釈は変わらないので、常に載せる
                    ScanCode = (ushort)MapVirtualKey(virtualKey, MapVirtualKeyToScanCode),
                    Flags = flags,
                    Time = 0,
                    ExtraInfo = ExtraInfo,
                },
            },
        };

        private static bool IsKeyDown(ushort virtualKey)
            => (GetAsyncKeyState(virtualKey) & 0x8000) != 0;

        [StructLayout(LayoutKind.Sequential)]
        private struct Input
        {
            public uint Type;
            public InputUnion Data;
        }

        /// <summary>
        /// INPUT の共用体。キー入力しか使わないが、
        /// 構造体の大きさは <c>MOUSEINPUT</c> で決まるため 3 つとも宣言する。
        /// 大きさが合わないと <c>SendInput</c> は何も送らずに 0 を返す。
        /// </summary>
        [StructLayout(LayoutKind.Explicit)]
        private struct InputUnion
        {
            [FieldOffset(0)]
            public MouseInput Mouse;

            [FieldOffset(0)]
            public KeyboardInput Keyboard;

            [FieldOffset(0)]
            public HardwareInput Hardware;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MouseInput
        {
            public int X;
            public int Y;
            public uint MouseData;
            public uint Flags;
            public uint Time;
            public IntPtr ExtraInfo;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct KeyboardInput
        {
            public ushort VirtualKey;
            public ushort ScanCode;
            public uint Flags;
            public uint Time;
            public IntPtr ExtraInfo;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct HardwareInput
        {
            public uint Message;
            public ushort ParamLow;
            public ushort ParamHigh;
        }

        [DllImport("user32.dll", SetLastError = true)]
        private static extern uint SendInput(uint count, Input[] inputs, int size);

        [DllImport("user32.dll")]
        private static extern uint MapVirtualKey(uint code, uint mapType);

        [DllImport("user32.dll")]
        private static extern short GetAsyncKeyState(int virtualKey);
    }
}
