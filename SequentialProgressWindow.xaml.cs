using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using MyTaskTray.Services;
using MyTaskTray.ViewModels;

namespace MyTaskTray;

/// <summary>表示時もクリック時も入力先をアクティブなまま保つ作業パネル。</summary>
public partial class SequentialProgressWindow : Window
{
    private const int GwlExStyle = -20;
    private const int WsExNoActivate = 0x08000000;
    private const int WsExToolWindow = 0x00000080;
    private readonly System.Windows.Forms.Screen _screen;
    private HwndSource? _source;
    private bool _positioning;

    internal event Action? UndoRequested;
    internal event Action? BeginPastingRequested;
    internal event Action? CancelRequested;
    internal event Action<int>? RemoveRequested;
    internal event Action<int, int>? MoveRequested;

    internal SequentialProgressWindow()
    {
        InitializeComponent();
        // 更新のたびにマウスを追わず、開始した画面に留まる。
        _screen = System.Windows.Forms.Screen.FromPoint(System.Windows.Forms.Cursor.Position);
        SourceInitialized += OnSourceInitialized;
        Loaded += (_, _) => Reposition();
        SizeChanged += (_, _) => Reposition();
        Closed += (_, _) =>
        {
            _source?.RemoveHook(WndProc);
            _source = null;
            DataContext = null;
            ToastWindow.ProgressPanelBounds = null;
        };
    }

    internal void Update(SequentialCopyPasteQueue queue)
        => DataContext = new SequentialProgressViewModel(queue);

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        IntPtr handle = new WindowInteropHelper(this).Handle;
        _source = HwndSource.FromHwnd(handle);
        _source?.AddHook(WndProc);
        // 拡張スタイルは 32bit 値なので、64bit プロセスでも Get/SetWindowLongW を使える。
        SetWindowLong(handle, GwlExStyle, GetWindowLong(handle, GwlExStyle) | WsExNoActivate | WsExToolWindow);
        Reposition();
    }

    private static IntPtr WndProc(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message == 0x0021) // WM_MOUSEACTIVATE: ボタンにはクリックを渡し、アクティブ化だけ防ぐ。
        {
            handled = true;
            return new IntPtr(3); // MA_NOACTIVATE
        }

        return IntPtr.Zero;
    }

    private void Reposition()
    {
        if (_source is null || _positioning)
        {
            return;
        }

        _positioning = true;
        try
        {
            var work = (System.Windows.Forms.Screen.AllScreens.FirstOrDefault(screen => screen.DeviceName == _screen.DeviceName)
                ?? System.Windows.Forms.Screen.FromHandle(_source.Handle)).WorkingArea;
            DpiScale dpi = VisualTreeHelper.GetDpi(this);
            MaxHeight = work.Height / dpi.DpiScaleY;
            Width = Math.Min(392, work.Width / dpi.DpiScaleX);
            int width = (int)Math.Ceiling((ActualWidth > 0 ? ActualWidth : Width) * dpi.DpiScaleX);
            int height = (int)Math.Ceiling((ActualHeight > 0 ? ActualHeight : 280) * dpi.DpiScaleY);
            int left = Math.Max(work.Left, work.Right - width);
            int top = Math.Max(work.Top, work.Bottom - height);
            SetWindowPos(_source.Handle, IntPtr.Zero, left, top, 0, 0, 0x0015); // NOSIZE | NOZORDER | NOACTIVATE
            ToastWindow.ProgressPanelBounds = new System.Drawing.Rectangle(left, top, width, height);
        }
        finally
        {
            _positioning = false;
        }
    }

    private void OnUndo(object sender, RoutedEventArgs e) => UndoRequested?.Invoke();
    private void OnBeginPasting(object sender, RoutedEventArgs e) => BeginPastingRequested?.Invoke();
    private void OnCancel(object sender, RoutedEventArgs e) => CancelRequested?.Invoke();
    private void OnRemove(object sender, RoutedEventArgs e) => RemoveRequested?.Invoke((int)((Button)sender).Tag);
    private void OnMoveUp(object sender, RoutedEventArgs e) => MoveRequested?.Invoke((int)((Button)sender).Tag, -1);
    private void OnMoveDown(object sender, RoutedEventArgs e) => MoveRequested?.Invoke((int)((Button)sender).Tag, 1);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
    private static extern int GetWindowLong(IntPtr hwnd, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongW")]
    private static extern int SetWindowLong(IntPtr hwnd, int index, int value);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(IntPtr hwnd, IntPtr insertAfter, int x, int y, int width, int height, uint flags);
}
