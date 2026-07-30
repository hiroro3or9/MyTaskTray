using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace MyTaskTray
{
    /// <summary>
    /// コピー完了などを画面右下に短時間だけ知らせる小さな通知。
    /// フォーカスを奪わないため <c>ShowActivated="False"</c> で表示する。
    /// </summary>
    public partial class ToastWindow : Window
    {
        private static ToastWindow? _current;

        private readonly DispatcherTimer _timer;
        private bool _closing;

        private ToastWindow()
        {
            InitializeComponent();

            _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1800) };
            _timer.Tick += (_, _) => FadeOutAndClose();

            SizeChanged += (_, _) => Reposition();
        }

        /// <summary>通知を表示する。連続して呼ばれた場合は前の通知を置き換える。</summary>
        public static void ShowToast(string title, string body)
        {
            if (Application.Current is null)
            {
                return;
            }

            _current?.CloseNow();

            ToastWindow toast = new();
            toast.TitleText.Text = title;
            toast.BodyText.Text = body;
            toast.BodyText.Visibility = string.IsNullOrEmpty(body) ? Visibility.Collapsed : Visibility.Visible;

            _current = toast;
            toast.Show();
            toast.Reposition();
            toast.BeginAnimation(OpacityProperty, CreateFade(0, 1, 140));
            toast._timer.Start();
        }

        private void Reposition()
        {
            Rect area = SystemParameters.WorkArea;
            Left = area.Right - ActualWidth;
            Top = area.Bottom - ActualHeight;
        }

        private void OnClicked(object sender, MouseButtonEventArgs e) => FadeOutAndClose();

        private void FadeOutAndClose()
        {
            if (_closing)
            {
                return;
            }

            _closing = true;
            _timer.Stop();

            DoubleAnimation fade = CreateFade(Opacity, 0, 220);
            fade.Completed += (_, _) => CloseNow();
            BeginAnimation(OpacityProperty, fade);
        }

        private void CloseNow()
        {
            _timer.Stop();

            if (ReferenceEquals(_current, this))
            {
                _current = null;
            }

            BeginAnimation(OpacityProperty, null);
            Close();
        }

        private static DoubleAnimation CreateFade(double from, double to, int milliseconds) => new(from, to,
            new Duration(TimeSpan.FromMilliseconds(milliseconds)))
        {
            FillBehavior = FillBehavior.HoldEnd,
        };
    }
}
