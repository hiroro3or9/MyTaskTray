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
            DarkRenderer renderer = new(table, text, disabled);

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
        }

        private sealed class DarkRenderer : ToolStripProfessionalRenderer
        {
            private readonly Color _text;
            private readonly Color _disabled;

            public DarkRenderer(ProfessionalColorTable colorTable, Color text, Color disabled)
                : base(colorTable)
            {
                _text = text;
                _disabled = disabled;
            }

            protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
            {
                e.TextColor = e.Item is null || e.Item.Enabled ? _text : _disabled;
                base.OnRenderItemText(e);
            }

            protected override void OnRenderArrow(ToolStripArrowRenderEventArgs e)
            {
                e.ArrowColor = e.Item is null || e.Item.Enabled ? _text : _disabled;
                base.OnRenderArrow(e);
            }
        }
    }
}
