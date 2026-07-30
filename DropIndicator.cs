using System.Windows;

namespace MyTaskTray
{
    /// <summary>ドラッグ中に表示する挿入位置。</summary>
    public enum DropPosition
    {
        /// <summary>表示しない。</summary>
        None,

        /// <summary>この行の上に挿入する。</summary>
        Above,

        /// <summary>この行の下に挿入する。</summary>
        Below,
    }

    /// <summary>
    /// ListBoxItem に挿入位置の線を出すための添付プロパティ。
    /// ListBoxItem のテンプレート（Themes/Controls.xaml）がこの値を見て線を出す。
    /// </summary>
    public static class DropIndicator
    {
        public static readonly DependencyProperty PositionProperty =
            DependencyProperty.RegisterAttached(
                "Position",
                typeof(DropPosition),
                typeof(DropIndicator),
                new FrameworkPropertyMetadata(DropPosition.None));

        public static void SetPosition(DependencyObject element, DropPosition value)
            => element.SetValue(PositionProperty, value);

        public static DropPosition GetPosition(DependencyObject element)
            => (DropPosition)element.GetValue(PositionProperty);
    }
}
