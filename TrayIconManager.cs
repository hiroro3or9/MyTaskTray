using System.Drawing;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Forms;
using MyTaskTray.Models;
using MyTaskTray.Services;

namespace MyTaskTray
{
    /// <summary>
    /// タスクトレイのアイコンと右クリックメニューを管理する。
    /// </summary>
    public sealed class TrayIconManager : IDisposable
    {
        private const string ToolTipText = "MyTaskTray";
        private const int MenuTextMaxLength = 40;

        // NotifyIcon が右クリック時に使っている内部処理。左クリックでも同じ見せ方をするために借りる。
        // 非公開メンバーなので将来の .NET で無くなる可能性があるが、
        // 取得は 1 度だけ試し、見つからなければ ShowTrayMenu() が手動表示に切り替える。
        private static readonly MethodInfo? ShowContextMenuMethod = typeof(NotifyIcon).GetMethod(
            "ShowContextMenu",
            BindingFlags.Instance | BindingFlags.NonPublic);

        private readonly NotifyIcon _notifyIcon;
        private AppSettings _settings;
        private SettingsWindow? _settingsWindow;
        private Icon? _icon;
        private bool _disposed;

        public TrayIconManager()
        {
            _settings = SettingsStore.Load();
            _icon = LoadTrayIcon();

            _notifyIcon = new NotifyIcon
            {
                Icon = _icon,
                Text = ToolTipText,
                Visible = false,
            };

            // 左クリックでもメニューを出す（右クリックと同じ内容）
            _notifyIcon.MouseUp += OnIconMouseUp;

            // Windows のテーマが変わったらメニューを作り直す
            ThemeManager.ThemeChanged += OnThemeChanged;
        }

        /// <summary>トレイアイコンを表示してメニューを構築する。</summary>
        public void Start()
        {
            RebuildMenu();
            _notifyIcon.Visible = true;
        }

        /// <summary>設定を読み直してメニューを作り直す。</summary>
        public void ReloadSettings()
        {
            _settings = SettingsStore.Load();
            RebuildMenu();
        }

        private void OnIconMouseUp(object? sender, MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Left)
            {
                return;
            }

            ShowTrayMenu();
        }

        /// <summary>
        /// 左クリックで右クリックと同じメニューを出す。
        /// 単に <c>ContextMenuStrip.Show</c> を呼ぶと、アプリが前面にならないため
        /// 別の場所をクリックしてもメニューが閉じない。NotifyIcon が右クリック時に
        /// 使っている内部処理を呼び、閉じる挙動と表示位置を右クリックに合わせる。
        /// </summary>
        private void ShowTrayMenu()
        {
            ContextMenuStrip? menu = _notifyIcon.ContextMenuStrip;
            if (menu is null)
            {
                return;
            }

            // すでに開いているときは閉じる（クリックでの開閉）
            if (menu.Visible)
            {
                menu.Close(ToolStripDropDownCloseReason.AppFocusChange);
                return;
            }

            try
            {
                MethodInfo? showContextMenu = ShowContextMenuMethod;
                if (showContextMenu is not null)
                {
                    showContextMenu.Invoke(_notifyIcon, null);
                    return;
                }
            }
            catch (Exception)
            {
                // 内部処理が使えない環境では、下の手動表示にフォールバックする
            }

            // フォールバック: 自分でメニューを出し、前面に持ってくることで
            // 別の場所をクリックしたときに閉じるようにする
            menu.Show(System.Windows.Forms.Cursor.Position);

            // 前面に持ってこられなくてもメニュー自体は出ているため、結果は見ない
            _ = SetForegroundWindow(menu.Handle);
        }

        private void OnThemeChanged(object? sender, EventArgs e)
        {
            if (_disposed)
            {
                return;
            }

            RebuildMenu();
        }

        private void RebuildMenu()
        {
            ContextMenuStrip menu = new()
            {
                ShowImageMargin = false,
            };

            BuildClipItems(menu.Items);

            // 先頭・末尾・連続した区切り線を取り除く。
            // これをしないと、下で足す区切り線と重なって線が二重に描かれる。
            TrimEdgeSeparators(menu.Items);

            if (menu.Items.Count > 0)
            {
                menu.Items.Add(new ToolStripSeparator());
            }

            // 設定フォルダーを開く操作は設定画面に置いているため、メニューには出さない
            ToolStripMenuItem settingsItem = new("設定(&S)...");
            settingsItem.Click += (_, _) => ShowSettingsWindow();
            menu.Items.Add(settingsItem);

            menu.Items.Add(new ToolStripSeparator());

            ToolStripMenuItem exitItem = new("終了(&X)");
            exitItem.Click += (_, _) => ExitApplication();
            menu.Items.Add(exitItem);

            // 表示するたびに、差し込みを展開したツールチップを作り直す
            menu.Opening += (_, _) => RefreshToolTips(menu.Items);

            // ダークテーマのときだけ、メニューの色を合わせる
            TrayMenuTheme.Apply(menu);

            ContextMenuStrip? old = _notifyIcon.ContextMenuStrip;
            _notifyIcon.ContextMenuStrip = menu;
            DisposeMenu(old);

            _notifyIcon.Text = BuildIconToolTip();
        }

        /// <summary>
        /// 使わなくなったメニューを破棄する。
        /// テーマの切り替えや設定の保存はメニューを開いたままでも起こるため、
        /// 表示中のメニューをそのまま破棄すると操作中に例外になる。閉じてから破棄する。
        /// </summary>
        private static void DisposeMenu(ContextMenuStrip? menu)
        {
            if (menu is null)
            {
                return;
            }

            if (!menu.Visible)
            {
                menu.Dispose();
                return;
            }

            void OnClosed(object? sender, ToolStripDropDownClosedEventArgs e)
            {
                menu.Closed -= OnClosed;

                // Closed の中はまだ閉じる処理の途中のため、いったん戻してから破棄する
                System.Windows.Application? app = System.Windows.Application.Current;
                if (app is null)
                {
                    menu.Dispose();
                    return;
                }

                app.Dispatcher.BeginInvoke(new Action(menu.Dispose));
            }

            menu.Closed += OnClosed;
        }

        /// <summary>トレイアイコンにマウスを乗せたときの説明。</summary>
        private string BuildIconToolTip()
        {
            int count = _settings.Items.Count(i => !i.IsSeparator);
            return count == 0
                ? ToolTipText + "（項目がありません）"
                : $"{ToolTipText}（{count} 項目）";
        }

        /// <summary>
        /// 設定の項目順を保ちながら、カテゴリごとにサブメニューへ振り分ける。
        /// 同じカテゴリが離れた位置に現れても、最初に登場した位置のサブメニューにまとめる。
        /// </summary>
        private void BuildClipItems(ToolStripItemCollection target)
        {
            if (_settings.Items.Count == 0)
            {
                ToolStripMenuItem empty = new("(項目がありません。設定から追加してください)")
                {
                    Enabled = false,
                };
                target.Add(empty);
                return;
            }

            Dictionary<string, ToolStripMenuItem> categories = new(StringComparer.Ordinal);

            foreach (ClipItem item in _settings.Items)
            {
                ToolStripItem entry = item.IsSeparator
                    ? new ToolStripSeparator()
                    : CreateClipMenuItem(item);

                // 「日付」と「日付 」（末尾に空白）が別のサブメニューになってしまわないよう、
                // 見た目で区別できない前後の空白は無視して同じカテゴリとして扱う
                string category = item.Category.Trim();

                if (string.IsNullOrEmpty(category))
                {
                    target.Add(entry);
                    continue;
                }

                if (!categories.TryGetValue(category, out ToolStripMenuItem? parent))
                {
                    parent = new ToolStripMenuItem(EscapeAmpersand(category));

                    // ShowImageMargin は ToolStripDropDownMenu 側のプロパティ
                    if (parent.DropDown is ToolStripDropDownMenu dropDownMenu)
                    {
                        dropDownMenu.ShowImageMargin = false;
                    }

                    categories[category] = parent;
                    target.Add(parent);
                }

                parent.DropDownItems.Add(entry);
            }

            // 中身が区切り線だけになってしまったサブメニューを整理する
            foreach (ToolStripMenuItem parent in categories.Values)
            {
                TrimEdgeSeparators(parent.DropDownItems);
                if (parent.DropDownItems.Count == 0)
                {
                    parent.Enabled = false;
                }
            }
        }

        private ToolStripMenuItem CreateClipMenuItem(ClipItem item)
        {
            string label = string.IsNullOrWhiteSpace(item.Name) ? item.Text : item.Name;

            // 名前もコピー文字列も空だと、クリックできるのに何も見えない行になってしまう
            if (string.IsNullOrWhiteSpace(label))
            {
                label = "(空の項目)";
            }

            ToolStripMenuItem menuItem = new(EscapeAmpersand(Truncate(label, MenuTextMaxLength)))
            {
                ToolTipText = BuildToolTip(item),
                Tag = item,
            };
            menuItem.Click += (_, _) => CopyToClipboard(item);
            return menuItem;
        }

        /// <summary>メニュー配下のツールチップを、現在時刻で展開し直す。</summary>
        private static void RefreshToolTips(ToolStripItemCollection items)
        {
            foreach (ToolStripItem item in items)
            {
                if (item is not ToolStripMenuItem menuItem)
                {
                    continue;
                }

                if (menuItem.Tag is ClipItem clip)
                {
                    menuItem.ToolTipText = BuildToolTip(clip);
                }

                if (menuItem.HasDropDownItems)
                {
                    RefreshToolTips(menuItem.DropDownItems);
                }
            }
        }

        /// <summary>差し込みを含む場合は、展開後の値もツールチップに出す。</summary>
        private static string BuildToolTip(ClipItem item)
        {
            string raw = Truncate(item.Text, 200);
            string expanded = TemplateEngine.Expand(item.Text, DateTime.Now, item.SequenceValue);

            if (string.Equals(raw, Truncate(expanded, 200), StringComparison.Ordinal))
            {
                return raw;
            }

            return raw + "\n→ " + Truncate(expanded, 200);
        }

        private void CopyToClipboard(ClipItem item)
        {
            string value = TemplateEngine.Expand(item.Text, DateTime.Now, item.SequenceValue);

            if (!ClipboardService.TryCopy(value))
            {
                System.Windows.MessageBox.Show(
                    "クリップボードにコピーできませんでした。他のアプリがクリップボードを使用している可能性があります。",
                    "MyTaskTray",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return;
            }

            if (_settings.ShowCopyNotification)
            {
                if (string.IsNullOrEmpty(value))
                {
                    // 空の文字列はクリップボードを空にする動作になるため、そのまま伝える
                    ToastWindow.ShowToast("クリップボードを空にしました", "コピーする文字列が空の項目です");
                }
                else
                {
                    string label = string.IsNullOrWhiteSpace(item.Name) ? "コピーしました" : item.Name;
                    ToastWindow.ShowToast(label, TemplateEngine.ToSingleLine(value, 120));
                }
            }

            // 連番を使った場合はカウンターを進めて保存する
            if (item.UsesSequence)
            {
                item.AdvanceSequence();
                TrySaveSettings();
            }
        }

        private void TrySaveSettings()
        {
            try
            {
                SettingsStore.Save(_settings);
            }
            catch (Exception)
            {
                // 連番の保存に失敗してもコピー自体は成功しているため、通知はしない
            }
        }

        private void ShowSettingsWindow()
        {
            if (_settingsWindow is not null)
            {
                _settingsWindow.Activate();
                if (_settingsWindow.WindowState == WindowState.Minimized)
                {
                    _settingsWindow.WindowState = WindowState.Normal;
                }
                return;
            }

            _settingsWindow = new SettingsWindow(_settings.Clone());
            _settingsWindow.Closed += (_, _) =>
            {
                bool saved = _settingsWindow?.Saved == true;
                _settingsWindow = null;
                if (saved)
                {
                    ReloadSettings();
                }
            };
            _settingsWindow.Show();
            _settingsWindow.Activate();
        }

        /// <summary>
        /// アプリを終了する。設定画面が開いている場合は先に閉じる。
        /// Shutdown() から閉じると未保存の確認でキャンセルしても終了が止まらないため、
        /// ここで閉じた結果を見てから終了する。
        /// </summary>
        private void ExitApplication()
        {
            if (_settingsWindow is not null)
            {
                _settingsWindow.Close();

                // 閉じられていれば Closed で null になっている。
                // 残っている場合は未保存の確認でキャンセルされたので、終了もしない
                if (_settingsWindow is not null)
                {
                    if (_settingsWindow.WindowState == WindowState.Minimized)
                    {
                        _settingsWindow.WindowState = WindowState.Normal;
                    }

                    _settingsWindow.Activate();
                    return;
                }
            }

            _notifyIcon.Visible = false;
            System.Windows.Application.Current.Shutdown();
        }

        /// <summary>先頭・末尾および連続した区切り線を取り除く。</summary>
        private static void TrimEdgeSeparators(ToolStripItemCollection items)
        {
            while (items.Count > 0 && items[0] is ToolStripSeparator)
            {
                RemoveAndDispose(items, 0);
            }

            while (items.Count > 0 && items[items.Count - 1] is ToolStripSeparator)
            {
                RemoveAndDispose(items, items.Count - 1);
            }

            for (int i = items.Count - 1; i > 0; i--)
            {
                if (items[i] is ToolStripSeparator && items[i - 1] is ToolStripSeparator)
                {
                    RemoveAndDispose(items, i);
                }
            }
        }

        /// <summary>取り除いた項目はメニューから外れても残るため、明示的に破棄する。</summary>
        private static void RemoveAndDispose(ToolStripItemCollection items, int index)
        {
            ToolStripItem item = items[index];
            items.RemoveAt(index);
            item.Dispose();
        }

        /// <summary>メニュー表示用に改行を可視化し、長すぎる場合は省略する。</summary>
        private static string Truncate(string value, int maxLength)
        {
            string oneLine = value
                .Replace("\r\n", " ⏎ ")
                .Replace('\n', '⏎')
                .Replace('\r', '⏎')
                .Replace('\t', ' ');

            return TemplateEngine.Truncate(oneLine, maxLength);
        }

        /// <summary>ToolStrip がニーモニックとして解釈しないよう &amp; をエスケープする。</summary>
        private static string EscapeAmpersand(string value) => value.Replace("&", "&&");

        private static Icon? LoadTrayIcon()
        {
            // まずアプリに埋め込んだリソースを試す
            try
            {
                Uri uri = new("pack://application:,,,/Resources/app.ico");
                System.Windows.Resources.StreamResourceInfo? info = System.Windows.Application.GetResourceStream(uri);
                if (info?.Stream is not null)
                {
                    using Stream stream = info.Stream;
                    return new Icon(stream);
                }
            }
            catch (Exception)
            {
                // 続けて実行ファイルのアイコンを試す
            }

            try
            {
                string exePath = Environment.ProcessPath ?? string.Empty;
                if (!string.IsNullOrEmpty(exePath))
                {
                    Icon? extracted = Icon.ExtractAssociatedIcon(exePath);
                    if (extracted is not null)
                    {
                        return extracted;
                    }
                }
            }
            catch (Exception)
            {
                // 最後の手段として既定アイコンを使う
            }

            // SystemIcons の実体は共有されているため、Dispose できるように複製する
            return (Icon)SystemIcons.Application.Clone();
        }

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetForegroundWindow(IntPtr hWnd);

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            ThemeManager.ThemeChanged -= OnThemeChanged;
            _notifyIcon.MouseUp -= OnIconMouseUp;
            _notifyIcon.Visible = false;
            _notifyIcon.ContextMenuStrip?.Dispose();
            _notifyIcon.Dispose();
            _icon?.Dispose();
            _icon = null;
        }
    }
}
