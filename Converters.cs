using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace MyTaskTray
{
    /// <summary>
    /// bool を反転して Visibility にする。false のときだけ表示したい場所で使う。
    /// </summary>
    public sealed class NotBoolToVisibilityConverter : IValueConverter
    {
        public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
            => value is true ? Visibility.Collapsed : Visibility.Visible;

        public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
            => throw new NotSupportedException();
    }
}
