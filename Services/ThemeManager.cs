using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using Microsoft.Win32;

namespace MyTaskTray.Services
{
    /// <summary>
    /// Windows の外観設定（ライト / ダーク、アクセント色）に合わせて
    /// アプリのリソースを差し替える。設定変更にも追従する。
    /// </summary>
    public static class ThemeManager
    {
        private const string PersonalizeKey = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";
        private const string DwmKey = @"Software\Microsoft\Windows\DWM";

        // DwmSetWindowAttribute のダークモード指定。20 は Windows 10 2004 以降、19 はそれ以前。
        private const int DwmUseImmersiveDarkMode = 20;
        private const int DwmUseImmersiveDarkModeBefore20H1 = 19;

        private static readonly Color FallbackAccent = Color.FromRgb(0x0F, 0x6C, 0xBD);

        private static bool _initialized;

        /// <summary>現在ダークテーマかどうか。</summary>
        public static bool IsDark { get; private set; }

        /// <summary>テーマが切り替わったときに発生する（UI スレッド）。</summary>
        public static event EventHandler? ThemeChanged;

        /// <summary>起動時に一度だけ呼ぶ。以降は Windows の設定変更に自動で追従する。</summary>
        public static void Initialize()
        {
            if (_initialized)
            {
                return;
            }

            _initialized = true;
            Apply();

            SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
        }

        /// <summary>ウィンドウのタイトルバーをテーマに合わせる。ウィンドウ生成直後に呼ぶ。</summary>
        public static void Attach(Window window)
        {
            if (window.IsLoaded)
            {
                ApplyTitleBar(window);
            }
            else
            {
                window.SourceInitialized += (_, _) => ApplyTitleBar(window);
            }

            EventHandler handler = (_, _) => ApplyTitleBar(window);
            ThemeChanged += handler;
            window.Closed += (_, _) => ThemeChanged -= handler;
        }

        private static void OnUserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
        {
            if (e.Category is not (UserPreferenceCategory.General
                or UserPreferenceCategory.Color
                or UserPreferenceCategory.VisualStyle))
            {
                return;
            }

            Application? app = Application.Current;
            if (app is null)
            {
                return;
            }

            // 設定変更の通知はレジストリ更新と前後することがあるため、少し遅らせて読み直す
            app.Dispatcher.BeginInvoke(new Action(Apply), System.Windows.Threading.DispatcherPriority.Background);
        }

        private static void Apply()
        {
            Application? app = Application.Current;
            if (app is null)
            {
                return;
            }

            bool dark = ReadIsDark();
            Color accent = AdjustAccent(ReadAccentColor(), dark);

            ResourceDictionary theme = new()
            {
                Source = new Uri(
                    dark ? "pack://application:,,,/Themes/Dark.xaml" : "pack://application:,,,/Themes/Light.xaml",
                    UriKind.Absolute),
            };

            // App.xaml が読み込んだ既定テーマも含めて、色の定義を取り除いてから入れ直す。
            // MergedDictionaries は後ろにあるものが優先されるため、色は必ず先頭に置く。
            System.Collections.ObjectModel.Collection<ResourceDictionary> merged = app.Resources.MergedDictionaries;
            for (int i = merged.Count - 1; i >= 0; i--)
            {
                if (IsThemeDictionary(merged[i]))
                {
                    merged.RemoveAt(i);
                }
            }

            merged.Insert(0, theme);

            // Application.Resources に直接入れた値は MergedDictionaries より優先されるため、
            // アクセント系だけをここで上書きする。
            Color surface = dark ? Color.FromRgb(0x2B, 0x2B, 0x2B) : Colors.White;
            app.Resources["Brush.Accent"] = Freeze(accent);
            app.Resources["Brush.Accent.Hover"] = Freeze(Mix(accent, dark ? Colors.White : Colors.Black, 0.14));
            app.Resources["Brush.Accent.Pressed"] = Freeze(Mix(accent, dark ? Colors.White : Colors.Black, 0.28));
            app.Resources["Brush.OnAccent"] = Freeze(Luminance(accent) > 0.55
                ? Color.FromRgb(0x14, 0x14, 0x14)
                : Colors.White);
            app.Resources["Brush.Selected"] = Freeze(Mix(accent, surface, dark ? 0.78 : 0.86));

            IsDark = dark;
            ThemeChanged?.Invoke(null, EventArgs.Empty);
        }

        /// <summary>色定義（Light.xaml / Dark.xaml）のリソースかどうか。</summary>
        private static bool IsThemeDictionary(ResourceDictionary dictionary)
        {
            string? source = dictionary.Source?.OriginalString;
            if (source is null)
            {
                return false;
            }

            return source.EndsWith("Themes/Light.xaml", StringComparison.OrdinalIgnoreCase)
                || source.EndsWith("Themes/Dark.xaml", StringComparison.OrdinalIgnoreCase);
        }

        private static void ApplyTitleBar(Window window)
        {
            try
            {
                IntPtr handle = new WindowInteropHelper(window).Handle;
                if (handle == IntPtr.Zero)
                {
                    return;
                }

                int value = IsDark ? 1 : 0;
                if (DwmSetWindowAttribute(handle, DwmUseImmersiveDarkMode, ref value, sizeof(int)) != 0)
                {
                    DwmSetWindowAttribute(handle, DwmUseImmersiveDarkModeBefore20H1, ref value, sizeof(int));
                }
            }
            catch (Exception)
            {
                // タイトルバーの色が変わらないだけなので無視する
            }
        }

        private static bool ReadIsDark()
        {
            try
            {
                object? value = Registry.GetValue(
                    @"HKEY_CURRENT_USER\" + PersonalizeKey, "AppsUseLightTheme", 1);
                if (value is int light)
                {
                    return light == 0;
                }
            }
            catch (Exception)
            {
                // 読めない場合はライト扱い
            }

            return false;
        }

        private static Color ReadAccentColor()
        {
            try
            {
                // AccentColor は 0xAABBGGRR（B と R が入れ替わっている）
                object? value = Registry.GetValue(@"HKEY_CURRENT_USER\" + DwmKey, "AccentColor", null);
                if (value is int raw)
                {
                    uint abgr = unchecked((uint)raw);
                    byte r = (byte)(abgr & 0xFF);
                    byte g = (byte)((abgr >> 8) & 0xFF);
                    byte b = (byte)((abgr >> 16) & 0xFF);
                    return Color.FromRgb(r, g, b);
                }
            }
            catch (Exception)
            {
                // 既定色にする
            }

            return FallbackAccent;
        }

        /// <summary>背景とのコントラストが足りないアクセント色を、読める明るさに寄せる。</summary>
        private static Color AdjustAccent(Color accent, bool dark)
        {
            double luminance = Luminance(accent);

            if (dark && luminance < 0.35)
            {
                return Mix(accent, Colors.White, 0.38);
            }

            if (!dark && luminance > 0.72)
            {
                return Mix(accent, Colors.Black, 0.30);
            }

            return accent;
        }

        private static double Luminance(Color color)
            => ((0.299 * color.R) + (0.587 * color.G) + (0.114 * color.B)) / 255.0;

        /// <summary><paramref name="ratio"/> の割合だけ <paramref name="to"/> に近づけた色。</summary>
        private static Color Mix(Color from, Color to, double ratio)
        {
            double r = Math.Clamp(ratio, 0, 1);
            return Color.FromRgb(
                (byte)Math.Round((from.R * (1 - r)) + (to.R * r)),
                (byte)Math.Round((from.G * (1 - r)) + (to.G * r)),
                (byte)Math.Round((from.B * (1 - r)) + (to.B * r)));
        }

        private static SolidColorBrush Freeze(Color color)
        {
            SolidColorBrush brush = new(color);
            brush.Freeze();
            return brush;
        }

        /// <summary>ダークテーマ時にトレイメニューへ使う色。</summary>
        public static (System.Drawing.Color Background, System.Drawing.Color Text,
            System.Drawing.Color Hover, System.Drawing.Color Border, System.Drawing.Color Disabled) TrayMenuColors
            => (System.Drawing.Color.FromArgb(0x2B, 0x2B, 0x2B),
                System.Drawing.Color.FromArgb(0xF1, 0xF1, 0xF1),
                System.Drawing.Color.FromArgb(0x3D, 0x3D, 0x3D),
                System.Drawing.Color.FromArgb(0x51, 0x51, 0x51),
                System.Drawing.Color.FromArgb(0x80, 0x80, 0x80));

        [DllImport("dwmapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
    }
}
