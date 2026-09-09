using System.Runtime.ExceptionServices;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using MyTaskTray.Services;
using Xunit;

namespace MyTaskTray.Tests;

[CollectionDefinition("WPF application", DisableParallelization = true)]
public sealed class WpfApplicationCollection;

[Collection("WPF application")]
public sealed class SequentialProgressWindowTests
{
    [Fact]
    public void WindowBindsAndLaysOutAcrossCapturePasteAndEmptyStatesInBothThemes()
    {
        RunOnStaThread(() =>
        {
            // HWND を表示せず、本番の XAML とバインディングをレイアウトまで評価する。
            Application app = new() { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            try
            {
                foreach (string theme in new[] { "Light", "Dark" })
                {
                    app.Resources.MergedDictionaries.Clear();
                    app.Resources.MergedDictionaries.Add(new ResourceDictionary
                    {
                        Source = new Uri($"/MyTaskTray;component/Themes/{theme}.xaml", UriKind.Relative),
                    });
                    app.Resources.MergedDictionaries.Add(new ResourceDictionary
                    {
                        Source = new Uri("/MyTaskTray;component/Themes/Controls.xaml", UriKind.Relative),
                    });
                    SequentialProgressWindow window = new();
                    try
                    {
                        SequentialCopyPasteQueue queue = new();
                        window.Update(queue);
                        Layout(window);
                        SavePreview(window, theme, "empty");
                        Assert.False(window.ShowActivated);
                        Assert.False(window.ShowInTaskbar);

                        queue.Capture("株式会社サンプル");
                        queue.Capture("東京都千代田区サンプル 1-2-3");
                        queue.Capture("注文番号: TEST-003\r\n2行目もそのまま保持");
                        window.Update(queue);
                        Layout(window);
                        SavePreview(window, theme, "capturing");
                        ((Expander)window.FindName("Details")).IsExpanded = true;
                        Layout(window);
                        SavePreview(window, theme, "expanded");

                        Assert.True(queue.TryBeginPasting());
                        queue.Advance();
                        window.Update(queue);
                        Layout(window);
                        SavePreview(window, theme, "pasting");
                        ProgressBar progress = FindDescendant<ProgressBar>((DependencyObject)window.Content)!;
                        Assert.NotNull(progress);
                        Assert.Equal(1, progress.Value);
                        Assert.Equal(3, progress.Maximum);
                        Assert.Equal(Visibility.Visible, progress.Visibility);

                        SequentialCopyPasteQueue longQueue = new();
                        longQueue.Capture("2 行目\r\n" + new string('長', 1000));
                        window.Update(longQueue);
                        Layout(window);
                        SavePreview(window, theme, "long-text");
                    }
                    finally
                    {
                        window.Close();
                    }
                }
            }
            finally { app.Shutdown(); }
        });
    }

    private static void Layout(Window window)
    {
        FrameworkElement content = (FrameworkElement)window.Content;
        // ScrollViewer のスクロール範囲・クリップの更新は Dispatcher へ遅延される。
        // 未表示の Window では親による再配置がないので、更新後にも明示的に配置する。
        for (int pass = 0; pass < 2; pass++)
        {
            content.InvalidateMeasure();
            content.Measure(new Size(392, 720));
            content.Arrange(new Rect(0, 0, 392, content.DesiredSize.Height));
            content.UpdateLayout();
            Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ContextIdle);
        }
        Assert.InRange(content.ActualHeight, 100, 720);
    }

    private static void SavePreview(Window window, string theme, string state)
    {
        // 指定したときだけ、実際の WPF 描画を目視確認用に出力する。
        string? output = Environment.GetEnvironmentVariable("MYTASKTRAY_PROGRESS_QA_DIR");
        if (string.IsNullOrEmpty(output)) return;
        Directory.CreateDirectory(output);
        FrameworkElement content = (FrameworkElement)window.Content;
        RenderTargetBitmap bitmap = new((int)Math.Ceiling(content.ActualWidth + content.Margin.Left + content.Margin.Right),
            (int)Math.Ceiling(content.ActualHeight + content.Margin.Top + content.Margin.Bottom), 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(content);
        PngBitmapEncoder encoder = new();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using FileStream stream = File.Create(Path.Combine(output, $"{theme}-{state}.png"));
        encoder.Save(stream);
    }

    private static T? FindDescendant<T>(DependencyObject node) where T : DependencyObject
    {
        if (node is T result) return result;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++)
        {
            T? child = FindDescendant<T>(VisualTreeHelper.GetChild(node, i));
            if (child is not null) return child;
        }
        return null;
    }

    private static void RunOnStaThread(Action action)
    {
        Exception? failure = null;
        Thread thread = new(() =>
        {
            try { action(); }
            catch (Exception ex) { failure = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(15)), "画面のレイアウトが時間内に完了しませんでした。");
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }
}
