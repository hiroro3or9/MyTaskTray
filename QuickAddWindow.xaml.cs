using System.Windows;
using System.Windows.Input;
using MyTaskTray.Services;

namespace MyTaskTray
{
    /// <summary>
    /// いまコピーしてある文字列を項目として登録するとき、名前だけを尋ねる小さな窓。
    ///
    /// <para>
    /// 設定画面を開かずに登録できることが目的なので、聞くのは名前だけにする。
    /// カテゴリや差し込みは、後から設定画面で整えられる。
    /// </para>
    /// </summary>
    public partial class QuickAddWindow : Window
    {
        /// <summary>初期値として入れる名前の長さ。メニューでの見え方に合わせる。</summary>
        private const int DefaultNameMaxLength = 40;

        /// <summary>カーソルの下に少し離して出す量（WPF の単位）。</summary>
        private const double CursorOffset = 18;

        /// <param name="text">登録される文字列（波かっこのエスケープ前）。</param>
        /// <param name="escaped">波かっこをエスケープしたかどうか。した場合は理由を添える。</param>
        public QuickAddWindow(string text, bool escaped)
        {
            InitializeComponent();

            ItemName = TemplateEngine.ToSingleLine(text, DefaultNameMaxLength);
            NameBox.Text = ItemName;
            PreviewText.Text = TemplateEngine.ToSingleLine(text, 300);

            if (escaped)
            {
                EscapeHint.Visibility = Visibility.Visible;
            }

            // タイトルバーを Windows の外観設定に合わせる（他のウィンドウと同じ扱い）
            ThemeManager.Attach(this);
        }

        /// <summary>「追加」で閉じたかどうか。false の場合は何もしない。</summary>
        public bool Accepted { get; private set; }

        /// <summary>入力された名前。空のまま追加することもできる。</summary>
        public string ItemName { get; private set; }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            PositionNearCursor();

            // そのまま Enter でも成立させたいので、初期値を選択状態にして打ち替えられるようにする
            NameBox.Focus();
            NameBox.SelectAll();
        }

        /// <summary>
        /// カーソルの近くに出す。ホットキーからメニューを出すときと同じ考え方で、
        /// 利用者の視線がある場所に置く。画面からはみ出す場合は作業領域に収める。
        /// </summary>
        private void PositionNearCursor()
        {
            try
            {
                System.Drawing.Point cursor = System.Windows.Forms.Cursor.Position;
                System.Drawing.Rectangle area = System.Windows.Forms.Screen.FromPoint(cursor).WorkingArea;

                // Cursor.Position と WorkingArea はデバイスピクセル。WPF の単位へ直す
                PresentationSource? source = PresentationSource.FromVisual(this);
                double scaleX = source?.CompositionTarget?.TransformFromDevice.M11 ?? 1.0;
                double scaleY = source?.CompositionTarget?.TransformFromDevice.M22 ?? 1.0;

                double areaLeft = area.Left * scaleX;
                double areaTop = area.Top * scaleY;
                double areaRight = area.Right * scaleX;
                double areaBottom = area.Bottom * scaleY;

                // SizeToContent の高さは Loaded の時点でまだ確定していないことがある。
                // その場合はおおよその値で置き、はみ出しの判定だけ効かせる
                double width = ActualWidth > 0 ? ActualWidth : Width;
                double height = ActualHeight > 0 ? ActualHeight : 240;

                double left = (cursor.X * scaleX) - (width / 2);
                double top = (cursor.Y * scaleY) + CursorOffset;

                Left = Clamp(left, areaLeft, areaRight - width);
                Top = Clamp(top, areaTop, areaBottom - height);
            }
            catch (Exception)
            {
                // 位置が決められなくても登録はできる。既定の位置のままにする
            }
        }

        /// <summary>作業領域より窓が大きい場合でも、左上が画面外へ行かないようにする。</summary>
        private static double Clamp(double value, double min, double max)
            => max <= min ? min : Math.Clamp(value, min, max);

        private void OnWindowPreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Escape)
            {
                e.Handled = true;
                Close();
            }
        }

        private void OnAccept(object sender, RoutedEventArgs e)
        {
            ItemName = NameBox.Text ?? string.Empty;
            Accepted = true;
            Close();
        }

        private void OnCancel(object sender, RoutedEventArgs e) => Close();
    }
}
