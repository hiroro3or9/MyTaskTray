using System;
using System.Runtime.InteropServices;

namespace ClipboardProbe;

/// <summary>
/// Win32 の API でクリップボードを直接読む。
///
/// <para>
/// .NET の <c>Clipboard</c> を通さずに生のバイト列を取るためのもの。
/// 「クリップボードに載ってはいるが .NET 経由では読めない」形式があるかどうかを
/// 切り分けるために使う。ここで読めるなら、データはあって .NET の包み方が問題ということになる。
/// </para>
/// </summary>
internal static class RawClipboard
{
    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern uint RegisterClipboardFormatW(string lpszFormat);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenClipboard(IntPtr hWndNewOwner);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseClipboard();

    /// <summary>
    /// 遅延レンダリングの場合、この呼び出しでコピー元のプロセスが実データを作る。
    /// つまりここが「重い」かどうかの本体。
    /// </summary>
    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr GetClipboardData(uint uFormat);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsClipboardFormatAvailable(uint format);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GlobalLock(IntPtr hMem);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalUnlock(IntPtr hMem);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern UIntPtr GlobalSize(IntPtr hMem);

    /// <summary>形式名から、この Windows での ID を得る。</summary>
    public static uint GetFormatId(string format) => RegisterClipboardFormatW(format);

    /// <summary>その形式がクリップボードに載っているか。中身は読まない。</summary>
    public static bool IsAvailable(string format)
    {
        uint id = GetFormatId(format);
        return id != 0 && IsClipboardFormatAvailable(id);
    }

    /// <summary>
    /// 指定した形式の中身を、そのままのバイト列として読む。
    /// 読めなかった場合は null と、どこで失敗したかを返す。
    /// </summary>
    public static (byte[]? Bytes, string Note) Read(string format)
    {
        uint id = GetFormatId(format);
        if (id == 0)
        {
            return (null, $"RegisterClipboardFormat が失敗 (Win32 {Marshal.GetLastWin32Error()})");
        }

        // 他のプロセスが開いている間は失敗する。数回試す
        bool opened = false;
        for (int attempt = 1; attempt <= 5 && !opened; attempt++)
        {
            opened = OpenClipboard(IntPtr.Zero);
            if (!opened)
            {
                System.Threading.Thread.Sleep(80);
            }
        }

        if (!opened)
        {
            return (null, $"OpenClipboard が失敗 (Win32 {Marshal.GetLastWin32Error()})");
        }

        try
        {
            IntPtr handle = GetClipboardData(id);
            if (handle == IntPtr.Zero)
            {
                // 遅延レンダリングでコピー元が応じられない場合もここに来る
                return (null, $"GetClipboardData が 0 を返した (Win32 {Marshal.GetLastWin32Error()})");
            }

            IntPtr pointer = GlobalLock(handle);
            if (pointer == IntPtr.Zero)
            {
                // HGLOBAL でない形式（ビットマップやメタファイルのハンドル）はここで失敗する。
                // 異常ではなく「この読み方が使えない形式」という意味
                return (null, "GlobalLock が失敗（HGLOBAL ではない形式の可能性）");
            }

            try
            {
                int size = (int)GlobalSize(handle);
                if (size <= 0)
                {
                    return (null, "大きさが 0");
                }

                byte[] buffer = new byte[size];
                Marshal.Copy(pointer, buffer, 0, size);
                return (buffer, string.Empty);
            }
            finally
            {
                GlobalUnlock(handle);
            }
        }
        finally
        {
            CloseClipboard();
        }
    }
}
