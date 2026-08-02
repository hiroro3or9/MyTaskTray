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
        private const string ExitSeparatorName = "ExitSeparator";
        private const string ExitMenuItemName = "ExitMenuItem";

        private static readonly IReadOnlyDictionary<string, string> EmptyCaptures
            = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        private readonly record struct MenuEntry(
            ClipItem Item,
            IReadOnlyDictionary<string, string> Captures);

        // NotifyIcon が右クリック時に使っている内部処理。左クリックでも同じ見せ方をするために借りる。
        // 非公開メンバーなので将来の .NET で無くなる可能性があるが、
        // 取得は 1 度だけ試し、見つからなければ ShowTrayMenu() が手動表示に切り替える。
        private static readonly MethodInfo? ShowContextMenuMethod = typeof(NotifyIcon).GetMethod(
            "ShowContextMenu",
            BindingFlags.Instance | BindingFlags.NonPublic);

        private readonly NotifyIcon _notifyIcon;
        private AppSettings _settings;
        private SettingsWindow? _settingsWindow;
        private GlobalHotKey? _menuHotKey;
        private ClipboardCaptureSession? _captureSession;
        private Icon? _icon;
        private bool _hideExitWhileMenuOpen;
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
            RegisterMenuHotKey();
        }

        /// <summary>
        /// 設定されている場合だけメニュー表示用ホットキーを登録する。
        /// 空欄は明示的な無効状態で、他アプリのキーを既定で奪わない。
        /// </summary>
        private void RegisterMenuHotKey()
        {
            _menuHotKey?.Dispose();
            _menuHotKey = null;

            string configured = _settings.MenuHotKey?.Trim() ?? string.Empty;
            if (string.IsNullOrEmpty(configured))
            {
                return;
            }

            if (!HotKeyGesture.TryParse(configured, out HotKeyGesture gesture, out string error))
            {
                ToastWindow.ShowToast("ホットキーの設定が不正です", error);
                return;
            }

            try
            {
                _menuHotKey = new GlobalHotKey(gesture, () => ShowTrayMenu(atCursor: true));
                if (_menuHotKey.IsRegistered)
                {
                    return;
                }

                _menuHotKey.Dispose();
                _menuHotKey = null;
            }
            catch (Exception)
            {
                _menuHotKey?.Dispose();
                _menuHotKey = null;
            }

            ToastWindow.ShowToast(
                "ホットキーを登録できません",
                $"{gesture.DisplayName} は別のアプリで使用されている可能性があります");
        }

        /// <summary>設定を読み直してメニューを作り直す。</summary>
        public void ReloadSettings()
        {
            CancelCapture(showToast: false, rebuildMenu: false);
            _settings = SettingsStore.Load();
            RebuildMenu();
            RegisterMenuHotKey();
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
        private void ShowTrayMenu(bool atCursor = false)
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

            if (!atCursor)
            {
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
            }

            if (atCursor)
            {
                _hideExitWhileMenuOpen = true;

                void RestoreExitItems(object? sender, ToolStripDropDownClosedEventArgs e)
                {
                    menu.Closed -= RestoreExitItems;
                    _hideExitWhileMenuOpen = false;
                }

                menu.Closed += RestoreExitItems;
            }

            // ホットキーでは作業中の画面にあるカーソル位置へ表示する。
            // 通常クリックのフォールバックでも同じ経路を使う。
            menu.Show(System.Windows.Forms.Cursor.Position);

            // 前面に持ってこられなくてもメニュー自体は出ているため、結果は見ない
            _ = SetForegroundWindow(menu.Handle);

            if (atCursor)
            {
                // ホットキーを押した手をマウスへ移さず、矢印キーと Enter で選べるようにする
                SelectFirstEnabledItem(menu.Items);
            }
        }

        private static void SelectFirstEnabledItem(ToolStripItemCollection items)
        {
            foreach (ToolStripItem item in items)
            {
                if (item.Available && item.Enabled && item is not ToolStripSeparator)
                {
                    item.Select();
                    return;
                }
            }
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

            // スマートアクションは現在のクリップボードに応じて変わるため、
            // メニューを表示する直前に内容を組み立て直す。
            menu.Opening += (_, _) => PopulateMenu(menu);
            PopulateMenu(menu);

            ContextMenuStrip? old = _notifyIcon.ContextMenuStrip;
            _notifyIcon.ContextMenuStrip = menu;
            DisposeMenu(old);

            _notifyIcon.Text = BuildIconToolTip();
        }

        /// <summary>現在のクリップボードとキャプチャ状態からメニュー内容を作る。</summary>
        private void PopulateMenu(ContextMenuStrip menu)
        {
            ClearAndDispose(menu.Items);

            Func<string> clipboard = CreateClipboardReader();

            if (_captureSession is not null)
            {
                ClipboardCaptureProgress progress = _captureSession.Progress;
                menu.Items.Add(new ToolStripMenuItem(
                    $"入力待ち: {progress.CurrentName} ({progress.CapturedCount + 1}/{progress.TotalCount})")
                {
                    Enabled = false,
                });

                ToolStripMenuItem cancelCapture = new("複数入力をキャンセル(&C)");
                cancelCapture.Click += (_, _) => CancelCapture(showToast: true, rebuildMenu: true);
                menu.Items.Add(cancelCapture);
                menu.Items.Add(new ToolStripSeparator());
            }

            List<MenuEntry> regular = [];
            List<MenuEntry> smart = [];

            foreach (ClipItem item in _settings.Items)
            {
                if (item.IsSeparator || item.ClipboardCondition == ClipboardMatchKind.Always)
                {
                    regular.Add(new MenuEntry(item, EmptyCaptures));
                    continue;
                }

                ClipboardMatchResult result = ClipboardMatcher.Match(item, clipboard());
                if (result.IsMatch)
                {
                    smart.Add(new MenuEntry(item, result.Captures));
                }
            }

            if (smart.Count > 0)
            {
                ToolStripMenuItem smartParent = new("この内容でできること")
                {
                    ToolTipText = "現在のクリップボードに合うスマートアクション",
                };
                if (smartParent.DropDown is ToolStripDropDownMenu smartDropDown)
                {
                    smartDropDown.ShowImageMargin = false;
                }

                BuildClipItems(smartParent.DropDownItems, smart, clipboard);
                TrimEdgeSeparators(smartParent.DropDownItems);
                menu.Items.Add(smartParent);
                menu.Items.Add(new ToolStripSeparator());
            }

            if (regular.Count == 0 && smart.Count == 0 && _settings.Items.Count > 0)
            {
                menu.Items.Add(new ToolStripMenuItem("(現在の内容に合うアクションはありません)")
                {
                    Enabled = false,
                });
            }

            BuildClipItems(menu.Items, regular, clipboard);

            // 先頭・末尾・連続した区切り線を取り除く。
            // キャプチャ欄やスマートアクションとの境界は残したいため、通常項目を足したあとに整理する。
            TrimEdgeSeparators(menu.Items);

            if (menu.Items.Count > 0)
            {
                menu.Items.Add(new ToolStripSeparator());
            }

            // 設定フォルダーを開く操作は設定画面に置いているため、メニューには出さない
            ToolStripMenuItem settingsItem = new("設定(&S)...");
            settingsItem.Click += (_, _) => ShowSettingsWindow();
            menu.Items.Add(settingsItem);

            menu.Items.Add(new ToolStripSeparator { Name = ExitSeparatorName });

            ToolStripMenuItem exitItem = new("終了(&X)") { Name = ExitMenuItemName };
            exitItem.Click += (_, _) => ExitApplication();
            menu.Items.Add(exitItem);

            if (_hideExitWhileMenuOpen)
            {
                menu.Items[ExitSeparatorName]?.Available = false;
                menu.Items[ExitMenuItemName]?.Available = false;
            }

            // 動的に追加したサブメニューにも現在の配色を適用する。
            TrayMenuTheme.Apply(menu);
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
            if (_settings.IsFallback)
            {
                return ToolTipText + "（設定を読み込めませんでした）";
            }

            int count = _settings.Items.Count(i => !i.IsSeparator);
            return count == 0
                ? ToolTipText + "（項目がありません）"
                : $"{ToolTipText}（{count} 項目）";
        }

        /// <summary>
        /// 設定の項目順を保ちながら、カテゴリごとにサブメニューへ振り分ける。
        /// 同じカテゴリが離れた位置に現れても、最初に登場した位置のサブメニューにまとめる。
        /// </summary>
        private void BuildClipItems(
            ToolStripItemCollection target,
            IEnumerable<MenuEntry> entries,
            Func<string> clipboard)
        {
            List<MenuEntry> source = [.. entries];
            if (source.Count == 0)
            {
                // 設定ファイルを読めていない場合、「項目がありません」は事実と違ううえ、
                // 追加して保存すると元の設定を失うため、そうと分かる文言にする
                if (_settings.Items.Count == 0)
                {
                    ToolStripMenuItem empty = new(_settings.IsFallback
                        ? "(設定を読み込めませんでした)"
                        : "(項目がありません。設定から追加してください)")
                    {
                        Enabled = false,
                    };
                    target.Add(empty);
                }

                return;
            }

            Dictionary<string, ToolStripMenuItem> categories = new(StringComparer.Ordinal);

            foreach (MenuEntry menuEntry in source)
            {
                ClipItem item = menuEntry.Item;
                ToolStripItem entry = item.IsSeparator
                    ? new ToolStripSeparator()
                    : CreateClipMenuItem(item, clipboard, menuEntry.Captures);

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

        /// <summary>
        /// クリップボードを 1 度だけ読み、以降は同じ値を返す関数を作る。
        /// メニューを開くたびに項目の数だけ読みに行くと、他アプリのコピー操作を妨げてしまう。
        /// </summary>
        private static Func<string> CreateClipboardReader()
        {
            string? cached = null;
            return () => cached ??= ClipboardService.GetText();
        }

        private ToolStripMenuItem CreateClipMenuItem(
            ClipItem item,
            Func<string> clipboard,
            IReadOnlyDictionary<string, string> captures)
        {
            string label = string.IsNullOrWhiteSpace(item.Name) ? item.Text : item.Name;

            // 名前もコピー文字列も空だと、クリックできるのに何も見えない行になってしまう
            if (string.IsNullOrWhiteSpace(label))
            {
                label = "(空の項目)";
            }

            ToolStripMenuItem menuItem = new(EscapeAmpersand(Truncate(label, MenuTextMaxLength)))
            {
                ToolTipText = BuildToolTip(item, clipboard, _settings.Sprint, captures),
                Tag = item,
            };
            menuItem.Click += (_, _) => ActivateClipItem(item, clipboard, captures);
            return menuItem;
        }

        /// <summary>差し込みを含む場合は、展開後の値もツールチップに出す。</summary>
        private static string BuildToolTip(
            ClipItem item,
            Func<string> clipboard,
            SprintSchedule? sprint,
            IReadOnlyDictionary<string, string> captures)
        {
            string raw = Truncate(item.Text, 200);
            string expanded = TemplateEngine.Expand(
                item.Text, DateTime.Now, item.SequenceValue, clipboard, sprint, null, captures);

            IReadOnlyList<string> inputNames = TemplateEngine.GetInputNames(item.Text);
            string inputHint = inputNames.Count == 0
                ? string.Empty
                : "\n入力: " + string.Join(" → ", inputNames);

            if (string.Equals(raw, Truncate(expanded, 200), StringComparison.Ordinal))
            {
                return raw + inputHint;
            }

            return raw + "\n→ " + Truncate(expanded, 200) + inputHint;
        }

        /// <summary>
        /// 入力のない項目はそのままコピーし、<c>{input:名前}</c> があればキャプチャを開始する。
        /// スマートアクションの判定に使ったクリップボードとキャプチャは、完了まで同じ値を保持する。
        /// </summary>
        private void ActivateClipItem(
            ClipItem item,
            Func<string> clipboard,
            IReadOnlyDictionary<string, string> captures)
        {
            IReadOnlyList<string> inputNames = TemplateEngine.GetInputNames(item.Text);
            if (inputNames.Count == 0)
            {
                CopyToClipboard(item, clipboard, null, captures);
                return;
            }

            bool preserveClipboard = item.HasSmartCondition || TemplateEngine.ContainsClipboard(item.Text);
            string sourceClipboard = preserveClipboard ? clipboard() : string.Empty;
            StartCapture(item, inputNames, sourceClipboard, captures);
        }

        private void StartCapture(
            ClipItem item,
            IReadOnlyList<string> inputNames,
            string sourceClipboard,
            IReadOnlyDictionary<string, string> captures)
        {
            CancelCapture(showToast: false, rebuildMenu: false);

            ClipboardCaptureSession? session = null;
            session = new ClipboardCaptureSession(
                inputNames,
                progressed: progress =>
                {
                    if (!ReferenceEquals(_captureSession, session))
                    {
                        return;
                    }

                    RebuildMenu();
                    ShowCapturePrompt(progress, "入力を受け取りました");
                },
                completed: inputs =>
                {
                    if (!ReferenceEquals(_captureSession, session))
                    {
                        return;
                    }

                    _captureSession = null;
                    CopyToClipboard(item, () => sourceClipboard, inputs, captures);
                    RebuildMenu();
                },
                timedOut: () =>
                {
                    if (!ReferenceEquals(_captureSession, session))
                    {
                        return;
                    }

                    _captureSession = null;
                    RebuildMenu();
                    ToastWindow.ShowToast("複数入力をキャンセルしました", "2 分間コピーがなかったため終了しました");
                },
                rejected: name => ToastWindow.ShowToast(
                    $"入力: {name}",
                    "文字列をコピーしてください。空またはテキスト以外の内容は入力として使えません"));

            _captureSession = session;
            if (!session.Start())
            {
                _captureSession = null;
                ToastWindow.ShowToast(
                    "複数入力を開始できません",
                    "Windows のクリップボード変更通知を受け取れませんでした");
                return;
            }

            RebuildMenu();
            ShowCapturePrompt(session.Progress, "複数入力を開始しました");
        }

        private static void ShowCapturePrompt(ClipboardCaptureProgress progress, string title)
        {
            ToastWindow.ShowToast(
                title,
                $"{progress.CapturedCount + 1}/{progress.TotalCount}: "
                    + $"「{progress.CurrentName}」に入れる文字列をコピーしてください");
        }

        private void CancelCapture(bool showToast, bool rebuildMenu)
        {
            ClipboardCaptureSession? session = _captureSession;
            _captureSession = null;
            session?.Dispose();

            if (rebuildMenu && !_disposed)
            {
                RebuildMenu();
            }

            if (showToast && session is not null)
            {
                ToastWindow.ShowToast("複数入力をキャンセルしました", string.Empty);
            }
        }

        private void CopyToClipboard(
            ClipItem item,
            Func<string> clipboardReader,
            IReadOnlyDictionary<string, string>? inputs,
            IReadOnlyDictionary<string, string> captures)
        {
            // クリップボードを開くと他アプリのコピー操作を妨げるうえ、ロックされていると
            // 再試行のあいだ操作が止まる。{clip} を使う項目でだけ読みに行く。
            // その場合はコピーで上書きされる前の内容が必要なので、展開より先に読む
            bool usesClipboard = TemplateEngine.ContainsClipboard(item.Text);
            string clipboard = usesClipboard ? clipboardReader() : string.Empty;

            // {clip} を使う項目でクリップボードが空だと、差し込む先が抜けた文字列になってしまう。
            // 気付かずに貼り付けてしまわないよう、コピーせずに知らせる
            if (usesClipboard && string.IsNullOrWhiteSpace(clipboard))
            {
                ToastWindow.ShowToast(
                    "クリップボードが空です",
                    "差し込む値をコピーしてから、もう一度この項目を選んでください");
                return;
            }

            // {date@clip} のようにクリップボードを日付として読む項目で、日付として読めない場合。
            // このまま展開すると差し込みが書いたまま残った文字列がコピーされ、
            // 気付かずに貼り付けてしまう。空のときと同じく、コピーせずに知らせる
            if (TemplateEngine.ContainsClipboardDate(item.Text)
                && !TemplateEngine.CanParseClipboardDate(clipboard))
            {
                ToastWindow.ShowToast(
                    "日付として読み取れません",
                    $"「{TemplateEngine.ToSingleLine(clipboard.Trim(), 30)}」から日付を読み取れませんでした。"
                        + "2026-08-15 のような形でコピーしてください");
                return;
            }

            string value = TemplateEngine.Expand(
                item.Text,
                DateTime.Now,
                item.SequenceValue,
                () => clipboard,
                _settings.Sprint,
                inputs,
                captures);

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
                bool sequenceReset = item.AdvanceSequence();
                TrySaveSettings();

                // 設定画面は開いた時点の複製を持っているため、進んだ番号は自動では伝わらない。
                // 保存時に突き合わせるので値は失われないが、開いているあいだ
                // 画面の「次の番号」が実際と違う値のままになるので、その場で伝える
                _settingsWindow?.NotifySequenceAdvanced(item.Id, item.SequenceValue);

                if (sequenceReset)
                {
                    ToastWindow.ShowToast(
                        "連番を 1 に戻しました",
                        "上限または下限を超えるため、次の番号を初期値へ戻しました。今回のコピーは完了しています");
                }
            }
        }

        private void TrySaveSettings()
        {
            // 設定ファイルを読めずに既定値で動いている状態では、
            // 連番の自動保存で利用者の設定を既定値に置き換えてしまう
            if (_settings.IsFallback)
            {
                return;
            }

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
            // 設定ファイルを読めていない状態で編集画面を開くと、
            // 空の内容で保存して元の設定を失うおそれがある。
            // 一時的なロックなら読み直しで解消するので、まず試す
            if (_settings.IsFallback)
            {
                ReloadSettings();
            }

            if (_settings.IsFallback)
            {
                System.Windows.MessageBox.Show(
                    "設定ファイルを読み込めませんでした。他のソフトが使用している可能性があります。\n"
                    + "しばらく待ってから、もう一度お試しください。\n\n"
                    + SettingsStore.FilePath,
                    "MyTaskTray",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return;
            }

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

        /// <summary>動的に作り直す前のメニュー項目を、イベントハンドラーごと破棄する。</summary>
        private static void ClearAndDispose(ToolStripItemCollection items)
        {
            while (items.Count > 0)
            {
                RemoveAndDispose(items, items.Count - 1);
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

                    // app.ico は 16/20/24/32px をピクセル単位で描き分けている。
                    // サイズを渡さないと 32px が選ばれて NotifyIcon 側で縮小され、線がにじむ。
                    // SmallIconSize は DPI に追従するので、そのまま最適なフレームが選ばれる。
                    return new Icon(stream, SystemInformation.SmallIconSize);
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
            CancelCapture(showToast: false, rebuildMenu: false);
            _menuHotKey?.Dispose();
            _menuHotKey = null;
            _notifyIcon.MouseUp -= OnIconMouseUp;
            _notifyIcon.Visible = false;
            _notifyIcon.ContextMenuStrip?.Dispose();
            _notifyIcon.Dispose();
            _icon?.Dispose();
            _icon = null;
        }
    }
}
