using System.Drawing;
using System.Windows.Forms;

namespace MyTaskTray.Services
{
    /// <summary>
    /// トレイの右クリックメニュー（WinForms）をダークテーマに合わせて描画する。
    /// ライトテーマのときは Windows 標準の見た目をそのまま使う。
    /// </summary>
    public static class TrayMenuTheme
    {
        /// <summary>メニュー全体に現在のテーマを適用する。</summary>
        public static void Apply(ContextMenuStrip menu)
        {
            if (!ThemeManager.IsDark)
            {
                return;
            }

            (Color background, Color text, Color hover, Color border, Color disabled) = ThemeManager.TrayMenuColors;
            DarkColorTable table = new(background, hover, border);
            DarkRenderer renderer = new(table, text, disabled, background, hover);

            menu.BackColor = background;
            menu.ForeColor = text;
            menu.Renderer = renderer;

            ApplyToItems(menu.Items, background, text, renderer);
        }

        private static void ApplyToItems(
            ToolStripItemCollection items, Color background, Color text, ToolStripRenderer renderer)
        {
            foreach (ToolStripItem item in items)
            {
                item.BackColor = background;
                item.ForeColor = text;

                if (item is not ToolStripMenuItem { HasDropDownItems: true } menuItem)
                {
                    continue;
                }

                menuItem.DropDown.BackColor = background;
                menuItem.DropDown.ForeColor = text;
                menuItem.DropDown.Renderer = renderer;
                ApplyToItems(menuItem.DropDownItems, background, text, renderer);
            }
        }

        private sealed class DarkColorTable : ProfessionalColorTable
        {
            private readonly Color _background;
            private readonly Color _hover;
            private readonly Color _border;

            public DarkColorTable(Color background, Color hover, Color border)
            {
                _background = background;
                _hover = hover;
                _border = border;
                UseSystemColors = false;
            }

            public override Color ToolStripDropDownBackground => _background;

            public override Color MenuBorder => _border;

            public override Color MenuItemBorder => _hover;

            public override Color MenuItemSelected => _hover;

            public override Color MenuItemSelectedGradientBegin => _hover;

            public override Color MenuItemSelectedGradientEnd => _hover;

            public override Color MenuItemPressedGradientBegin => _hover;

            public override Color MenuItemPressedGradientMiddle => _hover;

            public override Color MenuItemPressedGradientEnd => _hover;

            public override Color ImageMarginGradientBegin => _background;

            public override Color ImageMarginGradientMiddle => _background;

            public override Color ImageMarginGradientEnd => _background;

            public override Color SeparatorDark => _border;

            public override Color SeparatorLight => _background;

            // チェック済み項目の箱。既定は明るい水色で、暗いメニューの中で浮く。
            // 実際の描画は DarkRenderer が自前で行うが、
            // 別の経路でこれらが参照されても浮かないよう塞いでおく
            public override Color CheckBackground => _hover;

            public override Color CheckSelectedBackground => _hover;

            public override Color CheckPressedBackground => _hover;

            public override Color ButtonSelectedBorder => _border;
        }

        private sealed class DarkRenderer(
            ProfessionalColorTable colorTable,
            Color text,
            Color disabled,
            Color background,
            Color hover)
            : ToolStripProfessionalRenderer(colorTable)
        {
            protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
            {
                e.TextColor = e.Item is null || e.Item.Enabled ? text : disabled;
                base.OnRenderItemText(e);
            }

            protected override void OnRenderArrow(ToolStripArrowRenderEventArgs e)
            {
                e.ArrowColor = e.Item is null || e.Item.Enabled ? text : disabled;
                base.OnRenderArrow(e);
            }

            /// <summary>
            /// チェック済み項目（<c>{choices}</c> の選択肢）の印を描く。
            ///
            /// <para>
            /// 既定の描画は<strong>明るい箱を敷いたうえに、システム色のチェックを重ねる</strong>。
            /// 暗いメニューではその箱だけが白く浮く。
            /// かといって色表だけを暗くすると、今度はチェック自体が
            /// 暗い地に暗い線で描かれて見えなくなる。
            /// </para>
            /// <para>
            /// 地とチェックの両方をこちらで決める必要があるため、
            /// <c>base</c> を呼ばずに描き切る。
            /// </para>
            /// </summary>
            protected override void OnRenderItemCheck(ToolStripItemImageRenderEventArgs e)
            {
                Rectangle rect = e.ImageRectangle;
                if (rect.Width <= 0 || rect.Height <= 0)
                {
                    return;
                }

                // カーソルが乗っている行は行全体が hover 色で塗られている。
                // 地をそれに合わせないと、チェックの周りだけ四角く色が違って見える
                Color back = e.Item is { Selected: true, Enabled: true } ? hover : background;

                ControlPaint.DrawMenuGlyph(e.Graphics, rect, MenuGlyph.Checkmark, text, back);
            }
        }
    }
}
