using System.ComponentModel;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Forms;
using System.Windows.Threading;
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

        /// <summary>メニュー項目に振るアクセスキー。1〜9 のあと 0 で 10 個。</summary>
        private const string NumberAccessKeys = "1234567890";
        private const string ExitSeparatorName = "ExitSeparator";
        private const string ExitMenuItemName = "ExitMenuItem";

        /// <summary>設定画面の「現在のアプリ ▾」に出す、直近に前面だったアプリの数。</summary>
        private const int MaxRecentApps = 5;

        // 何もしない空のメッセージ。メニュー表示後の前面化を確定させるために送る
        private const int WmNull = 0x0000;

        // SetWindowLongPtr でウィンドウの「オーナー」を指す位置
        private const int GwlHwndParent = -8;

        private static readonly IReadOnlyDictionary<string, string> EmptyCaptures
            = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        // 同じ ContextMenuStrip は開くたびに中身を作り直す。
        // KeyDown をそのたびに追加すると、1 回の押下で過去のハンドラーまで全部動くため、
        // ドロップダウンごとに 1 個だけ持ち、現在の番号一覧だけを更新する。
        private static readonly ConditionalWeakTable<ToolStripDropDown, NumberKeyBinding>
            NumberKeyBindings = [];

        private sealed class NumberKeyBinding
        {
            private IReadOnlyList<ToolStripMenuItem> _numbered = [];
            private readonly HashSet<Keys> _pressed = [];

            public NumberKeyBinding(ToolStripDropDown dropDown)
            {
                dropDown.KeyDown += OnKeyDown;
                dropDown.KeyUp += OnKeyUp;
                dropDown.Closed += (_, _) => _pressed.Clear();
            }

            public void Update(IReadOnlyList<ToolStripMenuItem> numbered)
            {
                _numbered = numbered;
                _pressed.Clear();
            }

            private void OnKeyDown(object? sender, KeyEventArgs e)
            {
                if (NumberKeyToIndex(e.KeyCode) < 0)
                {
                    return;
                }

                // KeyDown は長押し中も繰り返される。離すまでは最初の 1 回だけ通す。
                if (!_pressed.Add(e.KeyCode))
                {
                    SuppressKey(e);
                    return;
                }

                ActivateNumberedItem(_numbered, e);
            }

            private void OnKeyUp(object? sender, KeyEventArgs e)
                => _pressed.Remove(e.KeyCode);
        }

        private readonly record struct MenuEntry(
            ClipItem Item,
            IReadOnlyDictionary<string, string> Captures,
            ForegroundApp AppContext);

        // NotifyIcon が右クリック時に使っている内部処理。左クリックでも同じ見せ方をするために借りる。
        // 非公開メンバーなので将来の .NET で無くなる可能性があるが、
        // 取得は 1 度だけ試し、見つからなければ ShowTrayMenu() が手動表示に切り替える。
        private static readonly MethodInfo? ShowContextMenuMethod = typeof(NotifyIcon).GetMethod(
            "ShowContextMenu",
            BindingFlags.Instance | BindingFlags.NonPublic);

        private readonly NotifyIcon _notifyIcon;

        // メニューを出す前に前面化するためだけの窓。詳細は MenuHostWindow を参照
        private readonly MenuHostWindow _menuHost = new();

        // クリップボード監視を伴う作業は同時に 1 つだけ実行する。
        private readonly ActionSessionManager _actionSessions = new();

        // 組み込みアクションを現在の状況に応じたメニュー領域へ振り分ける。
        private readonly TrayMenuComposer _menuComposer;

        // 直近に前面だったアプリの実行ファイル名。設定画面の候補に出すためだけに使う。
        // メモリ上にだけ置き、設定ファイルにも履歴にも残さない
        private readonly List<string> _recentApps = [];

        private AppSettings _settings;
        private SettingsWindow? _settingsWindow;
        private QuickAddWindow? _quickAddWindow;
        private GlobalHotKey? _menuHotKey;
        private Icon? _icon;
        private bool _disposed;

        // メニューを開く操作を受け取った時点の前面ウィンドウ。
        // メニューを組み立てる時点では前面が自分に変わっているため、ここで覚えておく
        private IntPtr _menuContextWindow;
        private ForegroundApp _menuContext = ForegroundApp.Unknown;

        // 設定画面のプレビューでも app 系の差し込みを確認できるよう、
        // 最後に取得できた外部アプリの情報をメニューを閉じたあとも覚えておく。
        // メモリ上だけに置き、設定ファイルや履歴には保存しない
        private ForegroundApp _lastKnownApp = ForegroundApp.Unknown;

        // フォーカスを戻す先。_menuContextWindow と同じ値だが、寿命が違う。
        //
        // メニューが閉じると ClearMenuContext() が _menuContextWindow を捨てる。
        // ところが WinForms は「閉じる → 項目の Click」の順に処理するため、
        // Click の中（＝ ActivateClipItem）から読むと必ず空になっている。
        // {choice} の連鎖は最後にここへ戻す必要があるので、閉じても消さずに持つ
        private IntPtr _focusReturnWindow;

        // 直近にメニューを開いた操作がホットキーだったかどうか。
        // {choice} の選択メニューを続けて出すとき、キーボードで選べる状態を引き継ぐために使う
        private bool _menuOpenedFromHotKey;

        // {choice} の選択メニューを出している最中かどうか。
        // このあいだは RestoreForeground() を効かせない。理由は同メソッドを参照
        private bool _choiceChainActive;

        public TrayIconManager()
        {
            _settings = SettingsStore.Load();
            _menuComposer = new TrayMenuComposer(CreateActionRegistry());
            _icon = LoadTrayIcon();

            _notifyIcon = new NotifyIcon
            {
                Icon = _icon,
                Text = ToolTipText,
                Visible = false,
            };

            // 左クリックでもメニューを出す（右クリックと同じ内容）
            _notifyIcon.MouseUp += OnIconMouseUp;

            // 右クリックのメニューは NotifyIcon が自分で出すため ShowTrayMenu() を通らない。
            // 前面アプリを覚えるのは、どちらのボタンでも押し下げの時点で行う
            _notifyIcon.MouseDown += OnIconMouseDown;

            // Windows のテーマが変わったらメニューを作り直す
            ThemeManager.ThemeChanged += OnThemeChanged;
        }

        /// <summary>
        /// 通常時に「作業ツール」または「この内容でできること」へ表示する
        /// 組み込みアクションを登録する。
        /// </summary>
        private TrayActionRegistry CreateActionRegistry()
        {
            TrayActionRegistry registry = new();
            registry.Register(new TrayActionDefinition(
                Id: TrayActionIds.RemoveBlankLines,
                Label: "空行を除外",
                ToolTip: "空白だけの行を取り除き、残した行の改行コードを保ってコピーします",
                Group: "text-transform",
                GroupLabel: "テキスト加工",
                GroupOrder: 200,
                Order: 100,
                AccessKey: 'B',
                Kind: TrayActionKind.OneShot,
                DefaultEnabled: true,
                AllowDuringSession: false,
                Evaluate: context =>
                {
                    if (string.IsNullOrWhiteSpace(context.Clipboard))
                    {
                        return TrayActionAvailability.Disabled(
                            "空行を含む文字列をコピーしてから実行してください");
                    }

                    return ClipboardTextActions.HasBlankLines(context.Clipboard)
                        ? TrayActionAvailability.Enabled
                        : TrayActionAvailability.Disabled(
                            "コピーした文字列に除外できる空行がありません");
                },
                Execute: context => CopyBuiltInActionResult(
                    "空行を除外しました",
                    ClipboardTextActions.RemoveBlankLines(context.Clipboard))));

            registry.Register(new TrayActionDefinition(
                Id: TrayActionIds.JsonMinify,
                Label: "JSONをMinify",
                ToolTip: "JSON の空白と改行を取り除き、1 行にしてコピーします",
                Group: "json-transform",
                GroupLabel: "データ変換",
                GroupOrder: 300,
                Order: 100,
                AccessKey: 'M',
                Kind: TrayActionKind.Contextual,
                DefaultEnabled: true,
                AllowDuringSession: false,
                Evaluate: context => ClipboardTextActions.IsJsonObjectOrArray(context.Clipboard)
                    ? TrayActionAvailability.Enabled
                    : TrayActionAvailability.Hidden,
                Execute: context => FormatJson(context.Clipboard, indented: false)));

            registry.Register(new TrayActionDefinition(
                Id: TrayActionIds.JsonFormat,
                Label: "JSONを整形",
                ToolTip: "JSON を 2 スペースのインデントと改行で整えてコピーします",
                Group: "json-transform",
                GroupLabel: "データ変換",
                GroupOrder: 300,
                Order: 200,
                AccessKey: 'F',
                Kind: TrayActionKind.Contextual,
                DefaultEnabled: true,
                AllowDuringSession: false,
                Evaluate: context => ClipboardTextActions.IsJsonObjectOrArray(context.Clipboard)
                    ? TrayActionAvailability.Enabled
                    : TrayActionAvailability.Hidden,
                Execute: context => FormatJson(context.Clipboard, indented: true)));

            registry.Register(new TrayActionDefinition(
                Id: TrayActionIds.SequentialCopyPaste,
                Label: "連続コピー＆ペースト",
                ToolTip: "A で Ctrl+C を繰り返し、B で Ctrl+V を押すたびに順番に貼り付けます",
                Group: "continuous-work",
                GroupLabel: "連続作業",
                GroupOrder: 100,
                Order: 100,
                AccessKey: 'R',
                Kind: TrayActionKind.Session,
                DefaultEnabled: true,
                AllowDuringSession: false,
                Evaluate: _ => TrayActionAvailability.Enabled,
                Execute: _ => StartSequentialCopyPaste()));

            return registry;
        }

        private void FormatJson(string clipboard, bool indented)
        {
            if (!ClipboardTextActions.TryFormatJson(clipboard, indented, out string result))
            {
                ToastWindow.ShowToast(
                    "JSONを変換できません",
                    "クリップボードの内容を JSON のオブジェクトまたは配列として読み取れませんでした");
                return;
            }

            CopyBuiltInActionResult(
                indented ? "JSONを整形しました" : "JSONをMinifyしました",
                result);
        }

        private void CopyBuiltInActionResult(string successTitle, string result)
        {
            if (!ClipboardService.TryCopy(result))
            {
                ToastWindow.ShowToast(
                    "クリップボードを更新できません",
                    "他のアプリがクリップボードを使用している可能性があります");
                return;
            }

            if (_settings.ShowCopyNotification)
            {
                ToastWindow.ShowToast(successTitle, TemplateEngine.ToSingleLine(result, 120));
            }
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
                _menuHotKey = new GlobalHotKey(gesture, ShowMenuFromHotKey);
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
            // 連続コピーは設定と独立しているので続行できる。
            // 複数入力は選択した ClipItem を保持しているため、設定全体を差し替える前に明示的に終了する。
            CancelCapture(showToast: true, rebuildMenu: false);
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
        /// トレイアイコンを押した時点の前面ウィンドウを覚える。
        /// 右クリックのメニューは <see cref="ShowTrayMenu"/> を通らないため、
        /// 左右どちらのボタンでもここで捕まえる。
        /// </summary>
        private void OnIconMouseDown(object? sender, MouseEventArgs e)
        {
            if (e.Button is not (MouseButtons.Left or MouseButtons.Right))
            {
                return;
            }

            // 右クリックは ShowTrayMenu を通らないので、ここで落としておかないと
            // 直前にホットキーで開いたときの true が残る
            _menuOpenedFromHotKey = false;

            CaptureMenuContext();
        }

        /// <summary>
        /// いま前面にあるウィンドウを、メニューの絞り込みとフォーカス復元のために覚える。
        /// 自分自身（設定画面やメニュー用の窓）が前面だった場合は「対象なし」として扱う。
        /// </summary>
        private void CaptureMenuContext()
        {
            IntPtr window = GetForegroundWindow();
            if (window == _menuHost.Handle)
            {
                window = IntPtr.Zero;
            }

            ForegroundApp app = ForegroundWindowInfo.Capture(window);

            _menuContextWindow = window;
            _menuContext = app;

            if (app.IsKnown)
            {
                _lastKnownApp = app;
                _settingsWindow?.NotifyAppContext(app);
            }

            // 閉じても消さない控え。理由はフィールドの説明を参照
            _focusReturnWindow = window;

            RememberRecentApp(app);
        }

        /// <summary>
        /// メニューを閉じたあと、覚えていた前面ウィンドウを捨てる。
        ///
        /// <para>
        /// <c>_focusReturnWindow</c> は<strong>ここでは捨てない</strong>。
        /// この処理は項目の <c>Click</c> より先に走るため、
        /// ここで消すとクリック後にフォーカスの戻し先を見失う。
        /// </para>
        /// </summary>
        private void ClearMenuContext()
        {
            _menuContextWindow = IntPtr.Zero;
            _menuContext = ForegroundApp.Unknown;
        }

        /// <summary>設定画面の候補に出すため、前面だったアプリを新しい順に数件だけ覚える。</summary>
        private void RememberRecentApp(ForegroundApp app)
        {
            if (!app.IsKnown)
            {
                return;
            }

            _recentApps.RemoveAll(name => string.Equals(name, app.ProcessName, StringComparison.OrdinalIgnoreCase));
            _recentApps.Insert(0, app.ProcessName);

            while (_recentApps.Count > MaxRecentApps)
            {
                _recentApps.RemoveAt(_recentApps.Count - 1);
            }
        }

        /// <summary>
        /// ホットキーが押されたときの入り口。
        /// ここは Windows のウィンドウプロシージャから直接呼ばれるため、
        /// 例外を外へ出すとメッセージループを巻き込んでアプリごと落ちる。必ずここで受け止める。
        /// </summary>
        private void ShowMenuFromHotKey()
        {
            if (_disposed)
            {
                return;
            }

            try
            {
                // 前面化する前でなければ、利用者が作業していたウィンドウは取れない
                CaptureMenuContext();
                ShowTrayMenu(fromHotKey: true);
            }
            catch (Exception ex)
            {
                ToastWindow.ShowToast("メニューを表示できませんでした", ex.Message);
            }
        }

        /// <summary>
        /// 左クリックで右クリックと同じメニューを出す。
        /// 単に <c>ContextMenuStrip.Show</c> を呼ぶと、アプリが前面にならないため
        /// 別の場所をクリックしてもメニューが閉じない。NotifyIcon が右クリック時に
        /// 使っている内部処理を呼び、閉じる挙動と表示位置を右クリックに合わせる。
        /// </summary>
        /// <param name="fromHotKey">
        /// グローバルホットキーから開いたかどうか。キーボードだけで選べるよう先頭項目を選択し、
        /// 手元を見ずに Enter を押しても終了しないよう「終了」を隠す。
        /// </param>
        private void ShowTrayMenu(bool fromHotKey = false)
        {
            ContextMenuStrip? menu = _notifyIcon.ContextMenuStrip;
            if (menu is null)
            {
                return;
            }

            // すでに開いているときは閉じる（クリック・ホットキーでの開閉）
            if (menu.Visible)
            {
                menu.Close(ToolStripDropDownCloseReason.AppFocusChange);
                return;
            }

            _menuOpenedFromHotKey = fromHotKey;

            if (!fromHotKey)
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

            _ = ShowMenuAtCursor(menu, fromHotKey);
        }

        /// <summary>
        /// カーソル位置にメニューを出す。ホットキー経路と、内部処理を呼べなかったときの
        /// フォールバックの両方で使う。
        ///
        /// <para>
        /// <c>ContextMenuStrip.Show</c> を呼ぶだけではアプリが前面にならないため、
        /// 他所をクリックしてもメニューが閉じず、矢印キーや Enter も別のアプリへ行ってしまう。
        /// NotifyIcon が右クリック時に踏んでいるのと同じ手順
        /// ――「画面に出ない窓を前面化 → 表示 → その窓へ空メッセージを送る」――
        /// を再現して、通常のコンテキストメニューと同じ挙動にする。
        /// </para>
        /// </summary>
        /// <param name="position">
        /// 表示位置。既定（null）ではカーソル位置に出す。
        /// <c>{choice}</c> の選択メニューのように何枚も続けて出す場合、
        /// 1 枚目の位置を渡し続けると、その場でメニューが切り替わるように見える。
        /// </param>
        /// <param name="carriedForeground">
        /// 連鎖の 1 枚目で覚えたウィンドウ。2 枚目以降はこれを引き継ぐ。
        /// 2 枚目の時点では前面が <see cref="MenuHostWindow"/> になっているため、
        /// ここで取り直すと「作業していたウィンドウ」を見失う。
        /// </param>
        /// <param name="restoreForegroundOnClose">
        /// 閉じたときに元のウィンドウへフォーカスを戻すかどうか。
        ///
        /// <para>
        /// <strong>連鎖の途中では false にする。</strong>
        /// <see cref="RestoreForeground"/> は「前面がまだ <see cref="MenuHostWindow"/> のままなら戻す」
        /// という条件で遅延実行するため、1 枚目を閉じた直後に 2 枚目を出すと
        /// 次のように 2 枚目のフォーカスを奪ってしまう:
        /// </para>
        /// <code>
        /// 1 枚目が閉じる → 復帰を予約
        /// 2 枚目を表示   → SetForegroundWindow(_menuHost) で前面は _menuHost
        /// 予約が走る     → 前面は _menuHost だ → 元のウィンドウへ戻す
        ///                → 2 枚目がキー入力を受け取れなくなる
        /// </code>
        /// </param>
        /// <returns>この表示で使った「戻す先」。連鎖の次の 1 枚へ引き継ぐ。</returns>
        private IntPtr ShowMenuAtCursor(
            ContextMenuStrip menu,
            bool fromHotKey,
            System.Drawing.Point? position = null,
            IntPtr carriedForeground = default,
            bool restoreForegroundOnClose = true)
        {
            // 前面化すると、それまで作業していたウィンドウからフォーカスが外れる。
            // このアプリは「コピーして、元の場所へ貼り付ける」ための道具なので、
            // 閉じたあとに戻しておかないと Ctrl+V の行き先が変わってしまう。
            //
            // 開く操作を受け取った時点で覚えたウィンドウがあればそちらを使う。
            // トレイをクリックした場合、ここへ来るまでにタスクバーへ前面が移っていることがあり、
            // 押し下げの時点で覚えたほうが「作業していたウィンドウ」に近い
            IntPtr previousForeground = carriedForeground != IntPtr.Zero
                ? carriedForeground
                : _menuContextWindow != IntPtr.Zero
                    ? _menuContextWindow
                    : GetForegroundWindow();

            if (previousForeground == _menuHost.Handle)
            {
                previousForeground = IntPtr.Zero;
            }

            // 表示より先に前面化する。ホットキー経路は WM_HOTKEY を受け取った直後なので、
            // Windows のフォアグラウンド制限を通過できる。
            // 通らなくてもメニュー自体は出るため、結果は見ない
            _ = SetForegroundWindow(_menuHost.Handle);

            // 「終了」を隠すのは、この 1 回の表示に対してだけ。
            // フィールドで状態を持たせると、表示中のメニュー作り直しや例外で true が残り、
            // 次にトレイをクリックして開いたときまで隠れてしまう
            void HideExitOnce(object? sender, CancelEventArgs e)
            {
                menu.Opening -= HideExitOnce;
                HideExitItems(menu);
            }

            void RestoreForegroundOnce(object? sender, ToolStripDropDownClosedEventArgs e)
            {
                menu.Closed -= RestoreForegroundOnce;
                RestoreForeground(previousForeground);
            }

            if (fromHotKey)
            {
                // RebuildMenu が登録した PopulateMenu より後に呼ばれるため、
                // 組み立て終わったあとの項目を隠せる
                menu.Opening += HideExitOnce;
            }

            try
            {
                menu.Show(position ?? System.Windows.Forms.Cursor.Position);
            }
            finally
            {
                // Opening が呼ばれないまま抜けた場合に備えて確実に外す（二重の解除は無害）
                menu.Opening -= HideExitOnce;
            }

            // 表示できたあとで購読する。Show が例外で抜けた場合に
            // 購読だけが残って、次に閉じたときへ持ち越されるのを避ける
            if (restoreForegroundOnClose)
            {
                menu.Closed += RestoreForegroundOnce;
            }

            // オーナーを与えないと、メニューが独立したウィンドウとみなされて
            // タスクバーにボタンが現れることがある
            SetMenuOwner(menu.Handle, _menuHost.Handle);

            // 前面化を確定させるための空メッセージ。これがないとメニューが閉じ残ることがある
            _ = PostMessage(_menuHost.Handle, WmNull, IntPtr.Zero, IntPtr.Zero);

            if (fromHotKey)
            {
                // ホットキーを押した手をマウスへ移さず、矢印キーと Enter で選べるようにする
                SelectFirstEnabledItem(menu.Items);
            }

            return previousForeground;
        }

        /// <summary>
        /// メニューを出す前に前面だったウィンドウへフォーカスを戻す。
        ///
        /// <para>
        /// 閉じる処理の途中では戻しきれないため、いったんメッセージを処理し終えてから行う。
        /// また、戻すのは「前面がまだ自分の見えない窓のまま」のときだけにする。
        /// 設定画面を開いた場合のように別のウィンドウが正当にフォーカスを取っていたり、
        /// Windows が自分で元のウィンドウへ戻していたりする場合は、何もしないほうが正しい。
        /// </para>
        /// </summary>
        private void RestoreForeground(IntPtr window)
        {
            if (window == IntPtr.Zero || !IsWindow(window))
            {
                return;
            }

            System.Windows.Application? app = System.Windows.Application.Current;
            if (app is null)
            {
                return;
            }

            IntPtr host = _menuHost.Handle;

            app.Dispatcher.BeginInvoke(
                DispatcherPriority.Background,
                new Action(() =>
                {
                    // {choice} の選択メニューを出している最中は戻さない。
                    //
                    // 項目をクリックしてトレイメニューが閉じると、そのメニューが
                    // 「閉じたら元へ戻す」をここへ予約する。ところが選択メニューは
                    // Normal 優先度で出るため、Background のこの処理より先に表示され、
                    // _menuHost を前面にする。その直後にこれが走ると
                    //
                    //     前面はまだ _menuHost だ → 元のウィンドウへ戻そう
                    //
                    // と判断してフォーカスを奪い、出たばかりの選択メニューが
                    // 活性を失って即座に閉じる（＝選択肢が出ないように見える）。
                    //
                    // 連鎖が終わったところで、こちらから明示的に戻している
                    if (_choiceChainActive)
                    {
                        return;
                    }

                    if (GetForegroundWindow() != host || !IsWindow(window))
                    {
                        return;
                    }

                    _ = SetForegroundWindow(window);
                }));
        }

        /// <summary>
        /// ホットキーから開いたメニューでは「終了」とその手前の区切り線を隠す。
        /// 取り除かずに <c>Available</c> だけを落とすので、次に開くときは元に戻る。
        /// </summary>
        private static void HideExitItems(ContextMenuStrip menu)
        {
            ToolStripItem? separator = menu.Items[ExitSeparatorName];
            separator?.Available = false;

            ToolStripItem? exit = menu.Items[ExitMenuItemName];
            exit?.Available = false;
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

            // 覚えた前面ウィンドウを次に開くときまで持ち越さない。
            // 古い情報で絞り込むと、原因の分からない「項目が消えた」になる
            menu.Closed += (_, _) => ClearMenuContext();

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

            AddActiveSessionItems(menu.Items);

            TrayActionContext actionContext = new(
                clipboard,
                _menuContext,
                _actionSessions,
                (id, defaultEnabled) => _settings.IsActionVisible(id, defaultEnabled));
            TrayMenuComposition actionMenu = _menuComposer.Compose(actionContext);
            IReadOnlyList<(TrayActionDefinition Action, TrayActionAvailability Availability)> contextualActions
                = actionMenu.ContextualActions;

            List<MenuEntry> regular = [];
            List<MenuEntry> smart = [];
            int hiddenByApp = 0;

            foreach (ClipItem item in _settings.Items)
            {
                // 前面アプリによる絞り込みは、スマートアクションかどうかに関わらず先に掛ける。
                // 判定できない場合は AppContextMatcher が表示する側に倒す
                if (!AppContextMatcher.Matches(item, _menuContext))
                {
                    hiddenByApp++;
                    continue;
                }

                if (item.IsSeparator || item.ClipboardCondition == ClipboardMatchKind.Always)
                {
                    regular.Add(new MenuEntry(item, EmptyCaptures, _menuContext));
                    continue;
                }

                ClipboardMatchResult result = ClipboardMatcher.Match(item, clipboard());
                if (result.IsMatch)
                {
                    smart.Add(new MenuEntry(item, result.Captures, _menuContext));
                }
            }

            if (smart.Count > 0 || contextualActions.Count > 0)
            {
                ToolStripMenuItem smartParent = new("この内容でできること")
                {
                    ToolTipText = "現在のクリップボードに合うスマートアクション",
                };
                if (smartParent.DropDown is ToolStripDropDownMenu smartDropDown)
                {
                    smartDropDown.ShowImageMargin = false;
                }

                if (smart.Count > 0)
                {
                    BuildClipItems(
                        smartParent.DropDownItems,
                        smart,
                        clipboard,
                        enabled: !_actionSessions.HasActiveSession);
                }

                if (contextualActions.Count > 0)
                {
                    if (smartParent.DropDownItems.Count > 0)
                    {
                        smartParent.DropDownItems.Add(new ToolStripSeparator());
                    }

                    AddBuiltInActionItems(
                        smartParent.DropDownItems,
                        contextualActions,
                        actionContext);
                }

                TrimEdgeSeparators(smartParent.DropDownItems);
                smartParent.Enabled = smartParent.DropDownItems
                    .OfType<ToolStripMenuItem>()
                    .Any(item => item.Enabled);
                if (!smartParent.Enabled)
                {
                    smartParent.ToolTipText = _actionSessions.HasActiveSession
                        ? BuildActiveSessionBlockedReason()
                        : contextualActions
                            .Select(entry => entry.Availability.DisabledReason)
                            .FirstOrDefault(reason => !string.IsNullOrWhiteSpace(reason))
                            ?? "現在の内容では使用できません";
                }
                menu.Items.Add(smartParent);
                menu.Items.Add(new ToolStripSeparator());
            }

            if (regular.Count == 0
                && smart.Count == 0
                && contextualActions.Count == 0
                && _settings.Items.Count > 0)
            {
                // 何も出ない理由が「クリップボードの内容」なのか「前面のアプリ」なのかで、
                // 次にやることが変わる。取り違えないよう文言を分ける
                string reason = hiddenByApp > 0 && _menuContext.IsKnown
                    ? $"({_menuContext.ProcessName} で表示する項目がありません)"
                    : "(現在の内容に合うアクションはありません)";

                menu.Items.Add(new ToolStripMenuItem(EscapeAmpersand(reason))
                {
                    Enabled = false,
                });
            }

            BuildClipItems(
                menu.Items,
                regular,
                clipboard,
                enabled: !_actionSessions.HasActiveSession);

            // 先頭・末尾・連続した区切り線を取り除く。
            // キャプチャ欄やスマートアクションとの境界は残したいため、通常項目を足したあとに整理する。
            TrimEdgeSeparators(menu.Items);

            AddWorkToolsMenu(menu.Items, actionMenu.WorkTools, actionContext);
            TrimEdgeSeparators(menu.Items);

            if (menu.Items.Count > 0)
            {
                menu.Items.Add(new ToolStripSeparator());
            }

            menu.Items.Add(CreateQuickAddItem());

            // 設定フォルダーを開く操作は設定画面に置いているため、メニューには出さない
            ToolStripMenuItem settingsItem = new("設定(&S)...");
            settingsItem.Click += (_, _) => ShowSettingsWindow();
            menu.Items.Add(settingsItem);

            menu.Items.Add(new ToolStripSeparator { Name = ExitSeparatorName });

            ToolStripMenuItem exitItem = new("終了(&X)") { Name = ExitMenuItemName };
            exitItem.Click += (_, _) => ExitApplication();
            menu.Items.Add(exitItem);

            // ホットキーから開いた場合は、この直後に ShowMenuAtCursor が「終了」を隠す

            // 動的に追加したサブメニューにも現在の配色を適用する。
            TrayMenuTheme.Apply(menu);
        }

        /// <summary>登録済みの組み込みアクションを「作業ツール」サブメニューへ追加する。</summary>
        private static void AddWorkToolsMenu(
            ToolStripItemCollection items,
            IReadOnlyList<(TrayActionDefinition Action, TrayActionAvailability Availability)> actions,
            TrayActionContext context)
        {
            if (actions.Count == 0)
            {
                return;
            }

            ToolStripMenuItem parent = new("作業ツール(&T)")
            {
                ToolTipText = "一時的な作業やクリップボード加工を開始します",
            };
            if (parent.DropDown is ToolStripDropDownMenu dropDown)
            {
                dropDown.ShowImageMargin = false;
            }

            AddBuiltInActionItems(parent.DropDownItems, actions, context);

            TrimEdgeSeparators(parent.DropDownItems);
            if (parent.DropDownItems.Count == 0)
            {
                parent.Dispose();
                return;
            }

            if (items.Count > 0 && items[items.Count - 1] is not ToolStripSeparator)
            {
                items.Add(new ToolStripSeparator());
            }

            items.Add(parent);
        }

        /// <summary>同じグループのまとまりを保ちながら、アクション項目を追加する。</summary>
        private static void AddBuiltInActionItems(
            ToolStripItemCollection items,
            IReadOnlyList<(TrayActionDefinition Action, TrayActionAvailability Availability)> actions,
            TrayActionContext context)
        {
            string? previousGroup = null;
            foreach ((TrayActionDefinition action, TrayActionAvailability availability) in actions)
            {
                if (previousGroup is not null
                    && !string.Equals(previousGroup, action.Group, StringComparison.Ordinal))
                {
                    items.Add(new ToolStripSeparator());
                }

                string text = $"{EscapeAmpersand(action.Label)}(&{char.ToUpperInvariant(action.AccessKey)})";
                ToolStripMenuItem actionItem = new(text)
                {
                    Enabled = availability.IsEnabled,
                    ToolTipText = availability.IsEnabled
                        ? action.ToolTip
                        : availability.DisabledReason,
                    Tag = action.Id,
                };
                actionItem.Click += (_, _) => ExecuteTrayAction(action, context);
                items.Add(actionItem);
                previousGroup = action.Group;
            }
        }

        private static void ExecuteTrayAction(TrayActionDefinition action, TrayActionContext context)
        {
            try
            {
                // メニューを開いたまま別経路で状態が変わることがあるため、クリック時にも再確認する。
                if (!context.IsActionVisible(action))
                {
                    ToastWindow.ShowToast(
                        "作業ツールを実行できません",
                        "設定でメニューに表示しない状態になっています");
                    return;
                }

                TrayActionAvailability availability = action.Evaluate(context);
                if (!availability.IsVisible || !availability.IsEnabled)
                {
                    ToastWindow.ShowToast(
                        "作業ツールを実行できません",
                        string.IsNullOrWhiteSpace(availability.DisabledReason)
                            ? "現在の状態では使用できません"
                            : availability.DisabledReason);
                    return;
                }

                if (context.Sessions.HasActiveSession && !action.AllowDuringSession)
                {
                    string running = context.Sessions.CurrentDisplayName ?? "別の作業モード";
                    ToastWindow.ShowToast(
                        "作業ツールを実行できません",
                        $"「{running}」を実行中のため使用できません");
                    return;
                }

                action.Execute(context);
            }
            catch (Exception)
            {
                ToastWindow.ShowToast(
                    "作業ツールを実行できません",
                    $"「{action.Label}」の実行中にエラーが発生しました");
            }
        }

        private string BuildActiveSessionBlockedReason()
            => string.IsNullOrWhiteSpace(_actionSessions.CurrentDisplayName)
                ? "別の作業モードを実行中のため使用できません"
                : $"「{_actionSessions.CurrentDisplayName}」を実行中のため使用できません";

        /// <summary>実行中の作業モードだけをメニュー先頭へ昇格して表示する。</summary>
        private void AddActiveSessionItems(ToolStripItemCollection items)
        {
            SequentialCopyPasteSession? sequential = _actionSessions.Get<SequentialCopyPasteSession>(
                TrayActionIds.SequentialCopyPaste);
            if (sequential is not null)
            {
                if (sequential.Phase == SequentialCopyPastePhase.Capturing)
                {
                    items.Add(new ToolStripMenuItem(
                        $"連続コピー: {sequential.CapturedCount} 件を収集中")
                    {
                        Enabled = false,
                        ToolTipText = "A でデータを順番にコピーしてください。B で最初の Ctrl+V を押すと収集を終えます",
                    });

                    ToolStripMenuItem beginPasting = new("収集を終えて貼り付けへ(&P)")
                    {
                        Enabled = sequential.CapturedCount > 0,
                        ToolTipText = "収集を終了します。次の Ctrl+V で 1 件目を貼り付けます",
                    };
                    beginPasting.Click += (_, _) => BeginSequentialPasting();
                    items.Add(beginPasting);

                    ToolStripMenuItem undo = new("最後のコピーを取り消す(&U)")
                    {
                        Enabled = sequential.CapturedCount > 0,
                    };
                    undo.Click += (_, _) => UndoSequentialCapture();
                    items.Add(undo);
                }
                else
                {
                    items.Add(new ToolStripMenuItem(
                        $"連続貼り付け: 次は {sequential.PastedCount + 1}/{sequential.CapturedCount}")
                    {
                        Enabled = false,
                        ToolTipText = "B で Ctrl+V を押すたびに次のデータを貼り付けます",
                    });
                }

                ToolStripMenuItem cancelSequential = new("連続コピー＆ペーストをキャンセル(&C)");
                cancelSequential.Click += (_, _) => CancelSequentialCopyPaste(
                    showToast: true,
                    rebuildMenu: true);
                items.Add(cancelSequential);
                items.Add(new ToolStripSeparator());
                return;
            }

            ClipboardCaptureSession? capture = _actionSessions.Get<ClipboardCaptureSession>(
                TrayActionIds.MultipleInput);
            if (capture is null)
            {
                return;
            }

            ClipboardCaptureProgress progress = capture.Progress;
            items.Add(new ToolStripMenuItem(
                $"入力待ち: {progress.CurrentName} ({progress.CapturedCount + 1}/{progress.TotalCount})")
            {
                Enabled = false,
            });

            ToolStripMenuItem cancelCapture = new("複数入力をキャンセル(&C)");
            cancelCapture.Click += (_, _) => CancelCapture(showToast: true, rebuildMenu: true);
            items.Add(cancelCapture);
            items.Add(new ToolStripSeparator());
        }

        private void StartSequentialCopyPaste()
        {
            if (_actionSessions.HasActiveSession)
            {
                ToastWindow.ShowToast("作業ツールを開始できません", BuildActiveSessionBlockedReason());
                return;
            }

            SequentialCopyPasteSession? session = null;
            try
            {
                session = new SequentialCopyPasteSession(
                    captured: (value, count) =>
                    {
                        if (!_actionSessions.IsCurrent(TrayActionIds.SequentialCopyPaste, session))
                        {
                            return;
                        }

                        RebuildMenu();
                        if (_settings.ShowCopyNotification)
                        {
                            ToastWindow.ShowToast(
                                $"連続コピー: {count} 件目を追加しました",
                                TemplateEngine.ToSingleLine(value, 100));
                        }
                    },
                    captureRejected: () =>
                    {
                        if (!_actionSessions.IsCurrent(TrayActionIds.SequentialCopyPaste, session))
                        {
                            return;
                        }

                        ToastWindow.ShowToast(
                            "連続コピーに追加できません",
                            "文字列をコピーしてください。空またはテキスト以外の内容は追加されません");
                    },
                    pasted: progress =>
                    {
                        if (!_actionSessions.IsCurrent(TrayActionIds.SequentialCopyPaste, session))
                        {
                            return;
                        }

                        if (progress.RemainingCount > 0)
                        {
                            RebuildMenu();
                            ToastWindow.ShowToast(
                                $"連続貼り付け: {progress.PastedCount}/{progress.TotalCount}",
                                $"残り {progress.RemainingCount} 件です");
                        }
                    },
                    pasteFailed: () =>
                    {
                        if (!_actionSessions.IsCurrent(TrayActionIds.SequentialCopyPaste, session))
                        {
                            return;
                        }

                        ToastWindow.ShowToast(
                            "今回の貼り付けを止めました",
                            "クリップボードを更新できませんでした。もう一度 Ctrl+V を押してください");
                    },
                    completed: () =>
                    {
                        if (!_actionSessions.IsCurrent(TrayActionIds.SequentialCopyPaste, session))
                        {
                            return;
                        }

                        int total = session?.CapturedCount ?? 0;
                        _actionSessions.Complete(TrayActionIds.SequentialCopyPaste, session);
                        RebuildMenu();
                        ToastWindow.ShowToast(
                            "連続貼り付けが完了しました",
                            $"{total} 件を順番に貼り付けました");
                    },
                    timedOut: () =>
                    {
                        if (!_actionSessions.IsCurrent(TrayActionIds.SequentialCopyPaste, session))
                        {
                            return;
                        }

                        _actionSessions.Complete(TrayActionIds.SequentialCopyPaste, session);
                        RebuildMenu();
                        ToastWindow.ShowToast(
                            "連続コピー＆ペーストを終了しました",
                            "10 分間コピーまたは貼り付けがなかったため、自動的にキャンセルしました");
                    });

                if (!_actionSessions.TryStart(
                    TrayActionIds.SequentialCopyPaste,
                    "連続コピー＆ペースト",
                    session))
                {
                    session.Dispose();
                    ToastWindow.ShowToast("作業ツールを開始できません", BuildActiveSessionBlockedReason());
                    return;
                }

                if (!session.Start())
                {
                    _actionSessions.Complete(TrayActionIds.SequentialCopyPaste, session);
                    ToastWindow.ShowToast(
                        "連続コピー＆ペーストを開始できません",
                        "Windows のクリップボードまたはキー入力を監視できませんでした");
                    return;
                }
            }
            catch (Exception)
            {
                _actionSessions.Complete(TrayActionIds.SequentialCopyPaste, session);
                session?.Dispose();
                ToastWindow.ShowToast(
                    "連続コピー＆ペーストを開始できません",
                    "Windows のクリップボードまたはキー入力を監視できませんでした");
                return;
            }

            RebuildMenu();
            ToastWindow.ShowToast(
                "連続コピーを開始しました",
                "A でデータを順番に Ctrl+C し、B で Ctrl+V を繰り返してください");
        }

        private void BeginSequentialPasting()
        {
            SequentialCopyPasteSession? session = _actionSessions.Get<SequentialCopyPasteSession>(
                TrayActionIds.SequentialCopyPaste);
            if (session is null || !session.TryBeginPasting())
            {
                return;
            }

            RebuildMenu();
            ToastWindow.ShowToast(
                "連続貼り付けの準備ができました",
                $"Ctrl+V を押すたびに、{session.CapturedCount} 件を順番に貼り付けます");
        }

        private void UndoSequentialCapture()
        {
            SequentialCopyPasteSession? session = _actionSessions.Get<SequentialCopyPasteSession>(
                TrayActionIds.SequentialCopyPaste);
            if (session is null || !session.TryUndoLastCapture(out string removed))
            {
                return;
            }

            RebuildMenu();
            ToastWindow.ShowToast(
                "最後のコピーを取り消しました",
                TemplateEngine.ToSingleLine(removed, 100));
        }

        private void CancelSequentialCopyPaste(bool showToast, bool rebuildMenu)
        {
            SequentialCopyPasteSession? session = _actionSessions.Get<SequentialCopyPasteSession>(
                TrayActionIds.SequentialCopyPaste);
            bool canceled = _actionSessions.Cancel(TrayActionIds.SequentialCopyPaste, session);

            if (rebuildMenu && !_disposed)
            {
                RebuildMenu();
            }

            if (showToast && canceled)
            {
                ToastWindow.ShowToast("連続コピー＆ペーストをキャンセルしました", string.Empty);
            }
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
            SequentialCopyPasteSession? sequential = _actionSessions.Get<SequentialCopyPasteSession>(
                TrayActionIds.SequentialCopyPaste);
            if (sequential is not null)
            {
                return sequential.Phase == SequentialCopyPastePhase.Capturing
                    ? $"{ToolTipText}（連続コピー: {sequential.CapturedCount} 件）"
                    : $"{ToolTipText}（連続貼り付け: 残り {sequential.RemainingCount} 件）";
            }

            ClipboardCaptureSession? capture = _actionSessions.Get<ClipboardCaptureSession>(
                TrayActionIds.MultipleInput);
            if (capture is not null)
            {
                ClipboardCaptureProgress progress = capture.Progress;
                return $"{ToolTipText}（複数入力: {progress.CapturedCount + 1}/{progress.TotalCount}）";
            }

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
            Func<string> clipboard,
            bool enabled)
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

            // 番号を振る対象。メニューにはこのメソッドが足すもの以外
            // （「設定」「終了」など）も並ぶため、ここで足したものだけを覚えておく
            List<ToolStripMenuItem> numbered = [];

            foreach (MenuEntry menuEntry in source)
            {
                ClipItem item = menuEntry.Item;
                ToolStripItem entry = item.IsSeparator
                    ? new ToolStripSeparator()
                    : CreateClipMenuItem(
                        item,
                        clipboard,
                        menuEntry.Captures,
                        menuEntry.AppContext,
                        enabled);

                // 「日付」と「日付 」（末尾に空白）が別のサブメニューになってしまわないよう、
                // 見た目で区別できない前後の空白は無視して同じカテゴリとして扱う
                string category = item.Category.Trim();

                if (string.IsNullOrEmpty(category))
                {
                    target.Add(entry);
                    if (entry is ToolStripMenuItem topLevelItem)
                    {
                        numbered.Add(topLevelItem);
                    }

                    continue;
                }

                if (!categories.TryGetValue(category, out ToolStripMenuItem? parent))
                {
                    parent = new ToolStripMenuItem(EscapeAmpersand(category))
                    {
                        Enabled = enabled,
                        ToolTipText = enabled ? string.Empty : BuildActiveSessionBlockedReason(),
                    };

                    // ShowImageMargin は ToolStripDropDownMenu 側のプロパティ
                    if (parent.DropDown is ToolStripDropDownMenu dropDownMenu)
                    {
                        dropDownMenu.ShowImageMargin = false;
                    }

                    categories[category] = parent;
                    target.Add(parent);
                    numbered.Add(parent);
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
                    continue;
                }

                // サブメニューの中は開いたときに改めて 1 から振り直す
                EnableNumberKeys(AssignNumberAccessKeys(parent.DropDownItems.OfType<ToolStripMenuItem>()));
            }

            // 中身が空でサブメニューが無効になった場合は番号を飛ばしたいので、上の整理のあとに振る
            EnableNumberKeys(AssignNumberAccessKeys(numbered));
        }

        /// <summary>
        /// 数字キーで項目を選べるようにする。
        ///
        /// <c>&amp;1</c> のアクセスキーは、<strong>親フォームを持たないポップアップでは処理されない</strong>。
        /// WinForms のニーモニック解決は所属するコンテナ（Form / ContainerControl）をたどる仕組みだが、
        /// このメニューは <see cref="MenuHostWindow"/>（NativeWindow）を持ち主にしていて
        /// Control の親子関係に載っていないため、たどる先が無い。
        /// 矢印キーと Enter が効くのは、そちらが <c>ToolStripManager</c> の
        /// モーダルフィルタで直接処理されていて、コンテナをたどらないため。
        ///
        /// そこでキー入力を自分で拾う。番号は
        /// <see cref="ToolStripMenuItem.ShortcutKeyDisplayString"/> で右端に表示していて、
        /// アクセスキー（<c>&amp;</c>）はもう使っていない。
        /// </summary>
        private static void EnableNumberKeys(List<ToolStripMenuItem> numbered)
        {
            if (numbered.Count == 0 || numbered[0].Owner is not ToolStripDropDown dropDown)
            {
                return;
            }

            NumberKeyBindings.GetValue(dropDown, static owner => new NumberKeyBinding(owner))
                .Update(numbered);
        }

        /// <summary>
        /// 数字キーに対応する項目を、マウスでクリックした場合と同じ経路で実行する。
        /// 選択メニューは中身だけを差し替えるため、呼び出すたびに現在の一覧を渡せる形にしている。
        /// </summary>
        private static void ActivateNumberedItem(
            IReadOnlyList<ToolStripMenuItem> numbered,
            KeyEventArgs e)
        {
            int index = NumberKeyToIndex(e.KeyCode);
            if (index < 0 || index >= numbered.Count)
            {
                return;
            }

            ToolStripMenuItem target = numbered[index];

            // 数字がメニューの先頭文字移動などに二重に使われないよう、ここで止める
            SuppressKey(e);

            if (target.HasDropDownItems)
            {
                // カテゴリは開くだけ。中の番号は開いた先で 1 から振り直してある
                target.Select();
                target.ShowDropDown();
                target.DropDownItems.OfType<ToolStripMenuItem>()
                    .FirstOrDefault(i => i.Enabled)?.Select();
                return;
            }

            // マウスでクリックした場合と同じ経路を通す。
            // メニューを閉じる処理とフォーカスの戻しも、これでそのまま効く
            target.PerformClick();
        }

        private static void SuppressKey(KeyEventArgs e)
        {
            e.Handled = true;
            e.SuppressKeyPress = true;
        }

        /// <summary>数字キーを 0 起点の番号に変換する。番号キーでなければ -1。</summary>
        private static int NumberKeyToIndex(Keys key) => key switch
        {
            >= Keys.D1 and <= Keys.D9 => key - Keys.D1,
            Keys.D0 => 9,
            >= Keys.NumPad1 and <= Keys.NumPad9 => key - Keys.NumPad1,
            Keys.NumPad0 => 9,
            _ => -1,
        };

        /// <summary>メニュー項目の決定に使われ、長押しを 1 回にまとめる必要があるキー。</summary>
        private static bool IsChoiceActivationKey(Keys key)
            => key is Keys.Enter or Keys.Space || NumberKeyToIndex(key) >= 0;

        /// <summary>
        /// メニュー項目の先頭に <c>1</c>〜<c>9</c>・<c>0</c> のアクセスキーを振る。
        /// ホットキーでメニューを出したあと、数字を 1 つ押すだけで選べるようにするため。
        ///
        /// アクセスキーは<strong>開いているドロップダウンの中だけ</strong>で解決されるため、
        /// トップレベルとサブメニューに同じ番号があっても衝突しない。
        /// サブメニューは開いた時点で 1 から振り直される。
        ///
        /// 番号は表示順に振る（並べ替えると番号も変わる）。
        /// これは「一覧の並び順がそのままメニューの順序になる」という既存の考え方と揃えている。
        /// 11 個目以降には振らない。矢印キーで選ぶ。
        /// </summary>
        /// <returns>
        /// 実際に番号を振った項目を、番号の順に並べたもの。
        /// 無効な項目を飛ばすので、この並びがそのまま「何番を押すとどれか」になる。
        /// </returns>
        private static List<ToolStripMenuItem> AssignNumberAccessKeys(IEnumerable<ToolStripMenuItem> items)
        {
            List<ToolStripMenuItem> assigned = [];

            foreach (ToolStripMenuItem item in items)
            {
                if (assigned.Count >= NumberAccessKeys.Length)
                {
                    break;
                }

                // 選べない項目に番号を使うと、その番号が無駄になる
                if (!item.Enabled)
                {
                    continue;
                }

                // 番号と名前のあいだは全角スペース。半角スペースだと数字が名前と地続きに見えて、
                // どこまでが番号でどこからが名前か目で切り分けにくい。
                //
                // 右端のショートカット欄（ShortcutKeyDisplayString）に出す手もあるが、
                // サブメニューを持つ項目は右端が開閉の矢印に使われるため WinForms が描画せず、
                // カテゴリだけ番号が消える。先頭に置けば両方を同じ見た目にできる。
                //
                // Text は EscapeAmpersand 済みなので、ここで足す分だけを考えればよい
                item.Text = $"{NumberAccessKeys[assigned.Count]}　{item.Text}";
                assigned.Add(item);
            }

            return assigned;
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

        /// <summary>
        /// 1 回のメニュー操作で記録した前面アプリを、差し込みエンジンへ渡す値に加える。
        /// タイトルを取れなかった場合は null とし、<c>{app:title}</c> を書いたまま残す。
        /// </summary>
        private static ExpandValues AddAppContext(ExpandValues values, ForegroundApp app)
            => values with
            {
                AppName = app.IsKnown && app.Name.Length > 0 ? app.Name : null,
                AppTitle = app.IsKnown && app.Title.Length > 0 ? app.Title : null,
            };

        private ToolStripMenuItem CreateClipMenuItem(
            ClipItem item,
            Func<string> clipboard,
            IReadOnlyDictionary<string, string> captures,
            ForegroundApp appContext,
            bool enabled)
        {
            string label = string.IsNullOrWhiteSpace(item.Name) ? item.Text : item.Name;

            // 名前もコピー文字列も空だと、クリックできるのに何も見えない行になってしまう
            if (string.IsNullOrWhiteSpace(label))
            {
                label = "(空の項目)";
            }

            ToolStripMenuItem menuItem = new(EscapeAmpersand(Truncate(label, MenuTextMaxLength)))
            {
                Enabled = enabled,
                ToolTipText = enabled
                    ? BuildToolTip(item, clipboard, _settings.Sprint, captures, appContext)
                    : BuildActiveSessionBlockedReason(),
                Tag = item,
            };
            menuItem.Click += (_, _) => ActivateClipItem(item, clipboard, captures, appContext);
            return menuItem;
        }

        /// <summary>差し込みを含む場合は、展開後の値もツールチップに出す。</summary>
        private static string BuildToolTip(
            ClipItem item,
            Func<string> clipboard,
            SprintSchedule? sprint,
            IReadOnlyDictionary<string, string> captures,
            ForegroundApp appContext)
        {
            string raw = Truncate(item.Text, 200);
            DateTime now = DateTime.Now;

            ExpandValues values = AddAppContext(new ExpandValues
            {
                Clipboard = clipboard,
                Sprint = sprint,
                Matches = captures,
            }, appContext);

            // 選択肢は、設定画面のプレビューと同じ規則で先頭のものを代表として出す
            string expanded = TemplateEngine.Expand(
                item.Text,
                now,
                item.SequenceValue,
                values with
                {
                    Choices = TemplateEngine.GetDefaultChoices(
                        item.Text, now, item.SequenceValue, values),
                });

            IReadOnlyList<string> inputNames = TemplateEngine.GetInputNames(item.Text);
            string inputHint = inputNames.Count == 0
                ? string.Empty
                : "\n入力: " + string.Join(" → ", inputNames);

            IReadOnlyList<ChoiceDefinition> choices = TemplateEngine.GetChoiceDefinitions(item.Text);
            string choiceHint = choices.Count == 0
                ? string.Empty
                : "\n選択: " + string.Join(" → ", choices.Select(c => c.Name))
                    + "（下は先頭の選択肢）";

            string hints = choiceHint + inputHint;

            if (string.Equals(raw, Truncate(expanded, 200), StringComparison.Ordinal))
            {
                return raw + hints;
            }

            return raw + "\n→ " + Truncate(expanded, 200) + hints;
        }

        /// <summary>
        /// 入力のない項目はそのままコピーし、<c>{input:名前}</c> があればキャプチャを開始する。
        /// <c>{choice:名前:…}</c> があれば、その前に選択肢のメニューを出す。
        /// スマートアクションの判定に使ったクリップボードとキャプチャは、完了まで同じ値を保持する。
        /// </summary>
        private void ActivateClipItem(
            ClipItem item,
            Func<string> clipboard,
            IReadOnlyDictionary<string, string> captures,
            ForegroundApp appContext)
        {
            // メニューを開いたあとに別経路でセッションが始まった場合も、
            // 実行中のデータを暗黙に破棄したり、収集内容へ定型文を混ぜたりしない。
            //
            // 選択肢のメニューを出すより先に確かめる。
            // 選ばせてから「使用できません」と言うのは筋が悪い
            if (_actionSessions.HasActiveSession)
            {
                ToastWindow.ShowToast("コピー項目を使用できません", BuildActiveSessionBlockedReason());
                return;
            }

            IReadOnlyList<ChoiceDefinition> choices = TemplateEngine.GetChoiceDefinitions(item.Text);
            if (choices.Count == 0)
            {
                ContinueActivateClipItem(item, clipboard, captures, appContext, null);
                return;
            }

            // 選択が先、入力が後。
            // {input:…} のキャプチャはクリップボードを 2 分間占有するため、
            // その最中にメニューを何枚も出すとタイマーが動き続ける。
            // 選択を先に済ませればセッションは中断なく進み、
            // 途中で中止した場合も、まだ始まっていないので巻き戻す状態が無い
            AskChoices(
                item,
                choices,
                clipboard,
                captures,
                appContext,
                selected => ContinueActivateClipItem(item, clipboard, captures, appContext, selected));
        }

        /// <summary>選択が済んだあと（または選択が要らない場合）のコピー処理。</summary>
        private void ContinueActivateClipItem(
            ClipItem item,
            Func<string> clipboard,
            IReadOnlyDictionary<string, string> captures,
            ForegroundApp appContext,
            IReadOnlyDictionary<string, string>? choices)
        {
            // 選択メニューを出しているあいだに別経路でセッションが始まっている可能性がある
            if (_actionSessions.HasActiveSession)
            {
                ToastWindow.ShowToast("コピー項目を使用できません", BuildActiveSessionBlockedReason());
                return;
            }

            IReadOnlyList<InputCaptureDefinition> inputs = TemplateEngine.GetInputDefinitions(item.Text);
            if (inputs.Count == 0)
            {
                CopyToClipboard(item, clipboard, null, captures, appContext, choices);
                return;
            }

            bool preserveClipboard = item.HasSmartCondition || TemplateEngine.ContainsClipboard(item.Text);
            string sourceClipboard = preserveClipboard ? clipboard() : string.Empty;
            StartCapture(item, inputs, sourceClipboard, captures, appContext, choices);
        }

        private void StartCapture(
            ClipItem item,
            IReadOnlyList<InputCaptureDefinition> inputs,
            string sourceClipboard,
            IReadOnlyDictionary<string, string> captures,
            ForegroundApp appContext,
            IReadOnlyDictionary<string, string>? choices)
        {
            if (_actionSessions.HasActiveSession)
            {
                ToastWindow.ShowToast("複数入力を開始できません", BuildActiveSessionBlockedReason());
                return;
            }

            foreach (InputCaptureDefinition input in inputs)
            {
                foreach (string pattern in input.Patterns)
                {
                    if (!TemplateEngine.TryValidateInputPattern(pattern, out string error))
                    {
                        ToastWindow.ShowToast(
                            $"入力「{input.Name}」の正規表現が正しくありません",
                            TemplateEngine.ToSingleLine(error, 100));
                        return;
                    }
                }
            }

            ClipboardCaptureSession? session = null;
            session = new ClipboardCaptureSession(
                inputs,
                progressed: progress =>
                {
                    if (!_actionSessions.IsCurrent(TrayActionIds.MultipleInput, session))
                    {
                        return;
                    }

                    RebuildMenu();
                    ShowCapturePrompt(progress, "入力を受け取りました");
                },
                completed: inputs =>
                {
                    if (!_actionSessions.IsCurrent(TrayActionIds.MultipleInput, session))
                    {
                        return;
                    }

                    _actionSessions.Complete(TrayActionIds.MultipleInput, session);
                    CopyToClipboard(item, () => sourceClipboard, inputs, captures, appContext, choices);
                    RebuildMenu();
                },
                timedOut: () =>
                {
                    if (!_actionSessions.IsCurrent(TrayActionIds.MultipleInput, session))
                    {
                        return;
                    }

                    _actionSessions.Complete(TrayActionIds.MultipleInput, session);
                    RebuildMenu();
                    ToastWindow.ShowToast("複数入力をキャンセルしました", "2 分間コピーがなかったため終了しました");
                },
                rejected: rejection =>
                {
                    string message = rejection.FailedPattern is null
                        ? "文字列をコピーしてください。空またはテキスト以外の内容は入力として使えません"
                        : $"正規表現 /{TemplateEngine.ToSingleLine(rejection.FailedPattern, 70)}/ "
                            + "に一致しません。別の文字列をコピーしてください";
                    ToastWindow.ShowToast($"入力: {rejection.Progress.CurrentName}", message);
                });

            if (!_actionSessions.TryStart(
                TrayActionIds.MultipleInput,
                "複数入力",
                session))
            {
                session.Dispose();
                ToastWindow.ShowToast("複数入力を開始できません", BuildActiveSessionBlockedReason());
                return;
            }

            if (!session.Start())
            {
                _actionSessions.Complete(TrayActionIds.MultipleInput, session);
                ToastWindow.ShowToast(
                    "複数入力を開始できません",
                    "Windows のクリップボード変更通知を受け取れませんでした");
                return;
            }

            RebuildMenu();
            ShowCapturePrompt(session.Progress, "複数入力を開始しました");
        }

        /// <summary>
        /// <c>{choice:名前:…}</c> の選択を 1 つずつ尋ねている途中の状態。
        /// メニューを 1 枚出すたびに <c>Index</c> が進む。
        /// </summary>
        /// <param name="Origin">
        /// 1 枚目を出した位置。以降も同じ場所に出して、その場で切り替わるように見せる。
        /// カーソルに追従させると、選ぶたびにメニューが右下へずれていく。
        /// </param>
        /// <param name="PreviousForeground">
        /// 連鎖の最後にフォーカスを戻す先。
        /// <strong>連鎖を始める時点で捕まえておく必要がある。</strong>
        /// 2 枚目以降は前面が <see cref="MenuHostWindow"/> になっているため、あとからは取り直せない。
        /// 取り出し元が <c>_menuContextWindow</c> ではなく <c>_focusReturnWindow</c> なのは、
        /// 前者がクリックより先に走る <see cref="ClearMenuContext"/> で消えてしまうから。
        /// </param>
        /// <param name="FromHotKey">ホットキー経路かどうか。連鎖のあいだ引き継ぐ。</param>
        /// <param name="Clipboard">ツールチップに完成形を出すためのクリップボード読み取り。</param>
        private sealed record ChoicePrompt(
            ClipItem Item,
            IReadOnlyList<ChoiceDefinition> Definitions,
            Dictionary<string, string> Selected,
            int Index,
            System.Drawing.Point Origin,
            IntPtr PreviousForeground,
            bool FromHotKey,
            Func<string> Clipboard,
            IReadOnlyDictionary<string, string> Captures,
            ForegroundApp AppContext,
            Action<IReadOnlyDictionary<string, string>> Completed);

        /// <summary>
        /// 選択肢を 1 つずつメニューで尋ね、すべて選び終えたら <paramref name="completed"/> を呼ぶ。
        /// 途中で中止した場合は何も呼ばない（コピーもしない）。
        /// </summary>
        private void AskChoices(
            ClipItem item,
            IReadOnlyList<ChoiceDefinition> definitions,
            Func<string> clipboard,
            IReadOnlyDictionary<string, string> captures,
            ForegroundApp appContext,
            Action<IReadOnlyDictionary<string, string>> completed)
        {
            // _menuContextWindow ではなくこちらを読む。
            // ここは項目の Click の中で、メニューの Closed（= ClearMenuContext）は
            // それより先に走り終えているため、_menuContextWindow は既に空になっている
            IntPtr previousForeground = _focusReturnWindow == _menuHost.Handle
                ? IntPtr.Zero
                : _focusReturnWindow;

            ChoicePrompt prompt = new(
                Item: item,
                Definitions: definitions,
                Selected: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
                Index: 0,
                Origin: System.Windows.Forms.Cursor.Position,
                PreviousForeground: previousForeground,
                FromHotKey: _menuOpenedFromHotKey,
                Clipboard: clipboard,
                Captures: captures,
                AppContext: appContext,
                Completed: completed);

            // ここで立てるのが要。いま呼び出し元のトレイメニューが閉じようとしていて、
            // その「閉じたら元のウィンドウへ戻す」が予約される。
            // 立てておかないと、その復帰が 1 枚目の選択メニューのフォーカスを奪う
            // （RestoreForeground() の説明を参照）
            _choiceChainActive = true;

            // 選択メニューはドロップダウンが入力を掴んでいるあいだだけの一時的なもので、
            // 閉じれば必ず終わる。ActionSessionManager へ登録すると、
            // 連鎖のあいだ HasActiveSession が全項目を無効にしてしまい噛み合わない。
            //
            // 1 枚目もいまは出さない。ここは項目をクリックしたメニューの Click の中で、
            // そのメニューはこれから閉じるところ。閉じる処理と重ねると後始末とぶつかる
            InvokeAfterMenuClose(() => ShowChoiceMenu(prompt));
        }

        /// <summary>
        /// 選択肢のメニューを 1 枚出す。
        ///
        /// <para>
        /// ここは <see cref="InvokeAfterMenuClose"/> から呼ばれる。ディスパッチャのコールバックで
        /// 例外を出すとアプリごと落ちるため、トレイメニューの表示
        /// （<see cref="ShowMenuFromHotKey"/>）と同じく、捕まえて通知に変える。
        /// </para>
        /// <para>
        /// あわせて連鎖の後始末をする。<c>_choiceChainActive</c> を立てたまま抜けると、
        /// 以降フォーカスの復帰が効かなくなり、メニューを閉じても
        /// 元のウィンドウへ戻らないアプリになってしまう。
        /// </para>
        /// </summary>
        private void ShowChoiceMenu(ChoicePrompt prompt)
        {
            if (_disposed)
            {
                _choiceChainActive = false;
                return;
            }

            try
            {
                ShowChoiceMenuCore(prompt);
            }
            catch (Exception ex)
            {
                _choiceChainActive = false;
                RestoreForeground(prompt.PreviousForeground);
                ToastWindow.ShowToast("選択肢を表示できませんでした", ex.Message);
            }
        }

        private void ShowChoiceMenuCore(ChoicePrompt initialPrompt)
        {
            ContextMenuStrip menu = new()
            {
                // トレイメニューと同じ設定にする。チェック欄の有無だけは、
                // 表示中の選択が単一か複数かに合わせて PopulateChoiceMenu() で切り替える
                ShowImageMargin = false,
            };

            List<ToolStripMenuItem> numbered = [];
            HashSet<Keys> pressedActivationKeys = [];
            ChoicePrompt? pendingPrompt = null;
            bool transitionDispatchPending = false;
            bool closedByChoice = false;

            // 1 つ選ぶたびにメニューを閉じて作り直すと、画面上では点滅して見える。
            // 項目クリックによる自動クローズを止め、同じメニューの中身だけを差し替える。
            menu.Closing += (_, e) =>
            {
                if (e.CloseReason == ToolStripDropDownCloseReason.ItemClicked)
                {
                    e.Cancel = true;
                }
            };

            // 中身を差し替えてもハンドラーを積み増さないよう、キー処理はメニューへ 1 回だけ付ける。
            // 数字・Enter・Space は、離すまで同じ押下として扱う。候補を切り替えた直後に
            // キーリピートが次の候補まで決定してしまうのを防ぐため。
            menu.KeyDown += (_, e) =>
            {
                if (!IsChoiceActivationKey(e.KeyCode))
                {
                    return;
                }

                if (!pressedActivationKeys.Add(e.KeyCode) || pendingPrompt is not null)
                {
                    SuppressKey(e);
                    return;
                }

                ActivateNumberedItem(numbered, e);
            };

            menu.KeyUp += (_, e) =>
            {
                _ = pressedActivationKeys.Remove(e.KeyCode);
                if (pressedActivationKeys.Count == 0 && pendingPrompt is not null)
                {
                    SchedulePendingMove();
                }
            };

            void CompleteChoice(
                ChoicePrompt completedPrompt,
                Dictionary<string, string> selected)
            {
                closedByChoice = true;
                menu.Close(ToolStripDropDownCloseReason.CloseCalled);

                // 連鎖の終わり。ここで初めてフォーカスを元のウィンドウへ戻す。
                _choiceChainActive = false;
                RestoreForeground(completedPrompt.PreviousForeground);

                // Close の後始末と重ならないよう、完了処理もメニューが落ち着いてから呼ぶ
                InvokeAfterMenuClose(() => completedPrompt.Completed(selected));
            }

            void AbortChoice(ChoicePrompt prompt, Exception ex)
            {
                closedByChoice = true;
                menu.Close(ToolStripDropDownCloseReason.CloseCalled);
                _choiceChainActive = false;
                RestoreForeground(prompt.PreviousForeground);
                ToastWindow.ShowToast("選択肢を表示できませんでした", ex.Message);
            }

            void SchedulePendingMove()
            {
                if (transitionDispatchPending)
                {
                    return;
                }

                transitionDispatchPending = true;
                InvokeAfterMenuItemClick(() =>
                {
                    transitionDispatchPending = false;

                    if (!menu.Visible || _disposed)
                    {
                        return;
                    }

                    // 決定に使ったキーをまだ押している間は、現在の画面を保つ。
                    // KeyUp が来た時点でもう一度ここへ予約される。
                    if (pressedActivationKeys.Count > 0 || pendingPrompt is null)
                    {
                        return;
                    }

                    ChoicePrompt prompt = pendingPrompt;
                    pendingPrompt = null;

                    try
                    {
                        PopulateChoiceMenu(prompt);
                        if (prompt.FromHotKey)
                        {
                            SelectFirstEnabledItem(menu.Items);
                        }
                    }
                    catch (Exception ex)
                    {
                        AbortChoice(prompt, ex);
                    }
                });
            }

            void MoveTo(ChoicePrompt prompt)
            {
                // Click の処理中に、その Click を送ってきた項目を破棄してはいけない。
                // メニュー自体は開いたまま、イベントを抜けたあと、決定キーが離されてから
                // 内容だけを入れ替える。
                pendingPrompt = prompt;
                SchedulePendingMove();
            }

            void AdvanceChoice(ChoicePrompt prompt, string name, string option)
            {
                Dictionary<string, string> selected = new(
                    prompt.Selected,
                    StringComparer.OrdinalIgnoreCase)
                {
                    [name] = option,
                };

                ChoicePrompt next = prompt with
                {
                    Selected = selected,
                    Index = prompt.Index + 1,
                };

                if (next.Index >= next.Definitions.Count)
                {
                    CompleteChoice(next, selected);
                    return;
                }

                MoveTo(next);
            }

            void GoBackChoice(ChoicePrompt prompt)
            {
                Dictionary<string, string> selected = new(
                    prompt.Selected,
                    StringComparer.OrdinalIgnoreCase);
                selected.Remove(prompt.Definitions[prompt.Index - 1].Name);

                MoveTo(prompt with
                {
                    Selected = selected,
                    Index = prompt.Index - 1,
                });
            }

            void PopulateChoiceMenu(ChoicePrompt prompt)
            {
                ChoiceDefinition definition = prompt.Definitions[prompt.Index];

                // 選択肢に書かれた差し込みは、この画面を作るときに 1 回だけ展開する。
                // 選んだあとに展開し直すと、{time} や {guid} のように評価のたびに変わるものが
                // メニューで見た値と食い違う。以降は表示にも値にも、この文字列だけを使う
                List<string> options = ResolveChoiceOptions(prompt, definition);
                List<ToolStripMenuItem> numberCandidates = [];

                menu.SuspendLayout();
                try
                {
                    numbered.Clear();
                    ClearAndDispose(menu.Items);

                    // ShowImageMargin と ShowCheckMargin の両方が false だと WinForms は
                    // Checked の印を描かないため、複数選択の画面だけチェック欄を出す
                    menu.ShowCheckMargin = definition.AllowMultiple;

                    string heading = definition.AllowMultiple ? "複数選択" : "選択";
                    heading += prompt.Definitions.Count > 1
                        ? $": {definition.Name} ({prompt.Index + 1}/{prompt.Definitions.Count})"
                        : $": {definition.Name}";

                    menu.Items.Add(new ToolStripMenuItem(EscapeAmpersand(heading))
                    {
                        Enabled = false,
                    });

                    if (definition.AllowMultiple)
                    {
                        AddMultipleChoiceItems(
                            menu,
                            prompt,
                            definition,
                            options,
                            numberCandidates,
                            selected => AdvanceChoice(prompt, definition.Name, selected));
                    }
                    else
                    {
                        foreach (string option in options)
                        {
                            ToolStripMenuItem entry = new(EscapeAmpersand(FormatChoiceOption(option)))
                            {
                                // 選んだ場合の完成形を出す。一覧で確認する代わりに、
                                // 選択肢ごとに結果が見えるようにする
                                ToolTipText = BuildChoicePreview(prompt, definition.Name, option),
                            };

                            entry.Click += (_, _) => AdvanceChoice(prompt, definition.Name, option);
                            menu.Items.Add(entry);
                            numberCandidates.Add(entry);
                        }
                    }

                    if (prompt.Index > 0)
                    {
                        menu.Items.Add(new ToolStripSeparator());

                        ToolStripMenuItem back = new("← 戻る");
                        back.Click += (_, _) => GoBackChoice(prompt);
                        menu.Items.Add(back);
                        numberCandidates.Add(back);
                    }

                    numbered.AddRange(AssignNumberAccessKeys(numberCandidates));

                    // 項目を全部追加したあとに呼ぶこと（Items をたどって配色を配るため）
                    TrayMenuTheme.Apply(menu);
                }
                finally
                {
                    menu.ResumeLayout(performLayout: true);
                }
            }

            void OnClosed(object? sender, ToolStripDropDownClosedEventArgs e)
            {
                menu.Closed -= OnClosed;
                DisposeMenuLater(menu);

                if (closedByChoice)
                {
                    return;
                }

                // 中止（Esc・別の場所をクリック）。連鎖はここで終わり
                _choiceChainActive = false;
                RestoreForeground(initialPrompt.PreviousForeground);
            }

            menu.Closed += OnClosed;

            try
            {
                PopulateChoiceMenu(initialPrompt);

                // 最初の 1 回だけウィンドウを表示する。以降は同じウィンドウの中身を差し替える。
                _ = ShowMenuAtCursor(
                    menu,
                    initialPrompt.FromHotKey,
                    position: initialPrompt.Origin,
                    carriedForeground: initialPrompt.PreviousForeground,
                    restoreForegroundOnClose: false);
            }
            catch
            {
                menu.Closed -= OnClosed;
                menu.Dispose();
                throw;
            }
        }

        /// <summary>
        /// <c>{choices:…}</c> の、いくつでも選べるメニューを組み立てる。
        ///
        /// <para>
        /// 1 つ選ぶごとに閉じてしまっては選べないので、
        /// <see cref="ToolStripDropDown.Closing"/> で「項目のクリックによる閉じ」を打ち消す。
        /// 代わりに「決定」を置き、そこで初めて閉じる。
        /// </para>
        /// <para>
        /// 番号キーは <see cref="ToolStripMenuItem.PerformClick"/> を通るため、
        /// マウスと同じくチェックの反転として効く（<see cref="EnableNumberKeys"/>）。
        /// </para>
        /// </summary>
        /// <param name="accept">「決定」を押したとき、連結した選択結果を呼び出し元へ渡す。</param>
        /// <param name="options">展開済みの選択肢。表示にも値にもこれを使う。</param>
        private void AddMultipleChoiceItems(
            ContextMenuStrip menu,
            ChoicePrompt prompt,
            ChoiceDefinition definition,
            List<string> options,
            List<ToolStripMenuItem> numbered,
            Action<string> accept)
        {
            // 同じ文字列の選択肢が 2 つ書かれていても取り違えないよう、値ではなく位置で覚える
            HashSet<int> selectedIndexes = [];
            List<ToolStripMenuItem> entries = [];

            // 「決定」の見出しに選んだ件数は入れない。
            // AssignNumberAccessKeys が Text の先頭へ番号を差し込むため、
            // あとから書き換えると番号ごと消えてしまう。
            // 件数は、すぐ上に並ぶチェックの数を見れば分かる
            ToolStripMenuItem commit = new("決定");

            void Refresh()
            {
                commit.ToolTipText = BuildMultipleChoiceTip(prompt, definition, options, selectedIndexes);

                // それぞれの選択肢には「いま押したらどうなるか」を出す。
                // 1 つ押すたびに全部の意味が変わるので、まとめて作り直す
                for (int i = 0; i < entries.Count; i++)
                {
                    HashSet<int> hypothetical = [.. selectedIndexes];
                    if (!hypothetical.Remove(i))
                    {
                        _ = hypothetical.Add(i);
                    }

                    entries[i].ToolTipText = BuildMultipleChoiceTip(
                        prompt, definition, options, hypothetical);
                }
            }

            for (int i = 0; i < options.Count; i++)
            {
                int index = i;

                ToolStripMenuItem entry = new(EscapeAmpersand(FormatChoiceOption(options[i])))
                {
                    // 番号キーは PerformClick を通るので、マウスと同じく反転として効く
                    CheckOnClick = true,
                };

                entry.Click += (_, _) =>
                {
                    // CheckOnClick は Click より先に反転するので、ここで読むのは反転後の状態
                    if (entry.Checked)
                    {
                        _ = selectedIndexes.Add(index);
                    }
                    else
                    {
                        _ = selectedIndexes.Remove(index);
                    }

                    Refresh();
                };

                menu.Items.Add(entry);
                entries.Add(entry);
                numbered.Add(entry);
            }

            menu.Items.Add(new ToolStripSeparator());

            commit.Click += (_, _) =>
            {
                accept(TemplateEngine.JoinChoices(options, selectedIndexes));
            };

            menu.Items.Add(commit);
            numbered.Add(commit);

            Refresh();
        }

        /// <summary>
        /// 複数選択の状態を 1 行で説明する。
        /// 1 つも選んでいない場合は、選び忘れと「あえて選ばない」を区別できるよう明示する。
        /// </summary>
        private string BuildMultipleChoiceTip(
            ChoicePrompt prompt,
            ChoiceDefinition definition,
            List<string> options,
            HashSet<int> selectedIndexes)
        {
            string joined = TemplateEngine.JoinChoices(options, selectedIndexes);
            string head = selectedIndexes.Count == 0
                ? "何も選んでいません（この差し込みは空になります）"
                : $"{selectedIndexes.Count} 件: {TemplateEngine.ToSingleLine(joined, 80)}";

            return head + "\n→ " + BuildChoicePreview(prompt, definition.Name, joined);
        }

        /// <summary>
        /// クリックを送ってきた項目を安全に破棄できるよう、現在の入力イベントを抜けてから実行する。
        /// メニューは閉じないため、待機は通常優先度の 1 回だけでよい。
        /// </summary>
        private static void InvokeAfterMenuItemClick(Action action)
        {
            System.Windows.Application? app = System.Windows.Application.Current;
            if (app is null)
            {
                action();
                return;
            }

            app.Dispatcher.BeginInvoke(DispatcherPriority.Normal, action);
        }

        /// <summary>
        /// メニューを閉じる処理の途中では次のメニューを出せないため、
        /// 入力・フォーカス・閉じる処理が落ち着いてから実行する。
        ///
        /// <para>
        /// 既定の <see cref="DispatcherPriority.Normal"/> で予約すると、元のメニューが
        /// 遅れて処理するフォーカス変更より先に次のメニューが開く。その直後に元の処理が
        /// 前面を切り替え、出たばかりの選択肢が <c>AppFocusChange</c> で閉じてしまう。
        /// <see cref="DispatcherPriority.ContextIdle"/> まで待つことで、元のメニューに属する
        /// 入力とフォーカス変更を先に処理し終える。
        /// </para>
        /// </summary>
        private static void InvokeAfterMenuClose(Action action)
        {
            System.Windows.Application? app = System.Windows.Application.Current;
            if (app is null)
            {
                action();
                return;
            }

            app.Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, action);
        }

        /// <summary>閉じる処理の途中で破棄すると例外になるため、戻してから破棄する。</summary>
        private static void DisposeMenuLater(ContextMenuStrip menu)
        {
            System.Windows.Application? app = System.Windows.Application.Current;
            if (app is null)
            {
                menu.Dispose();
                return;
            }

            app.Dispatcher.BeginInvoke(new Action(menu.Dispose));
        }

        /// <summary>
        /// 選択肢をメニューの 1 行にする。
        /// 空や空白だけの選択肢はそのまま出すと<strong>押せる行が見えなくなる</strong>ため、
        /// そうと分かる表示に置き換える（改行やタブは <see cref="TemplateEngine.ToSingleLine"/> が記号にする）。
        /// </summary>
        private static string FormatChoiceOption(string option)
        {
            if (option.Length == 0)
            {
                return "(空)";
            }

            // 元の文字ではなく「描いたあと」で判定する。
            // ToSingleLine は改行を ⏎ にする（見える）が、タブは空白 4 つにする（見えない）。
            // 元の文字で判定すると、この違いを取りこぼす
            string rendered = TemplateEngine.ToSingleLine(option, MenuTextMaxLength);

            return rendered.All(char.IsWhiteSpace)
                ? $"(空白 {option.Length} 文字)"
                : rendered;
        }

        /// <summary>
        /// 選択肢に書かれた差し込みを展開する。<strong>メニュー 1 枚につき 1 回だけ</strong>呼ぶ。
        ///
        /// <para>
        /// 時刻は全部の選択肢で同じ値を使う。1 つずつ <c>DateTime.Now</c> を読むと、
        /// <c>{time:HH:mm:ss}</c> を並べたときに選択肢どうしで秒がずれることがある。
        /// </para>
        /// </summary>
        private List<string> ResolveChoiceOptions(ChoicePrompt prompt, ChoiceDefinition definition)
        {
            DateTime now = DateTime.Now;
            ExpandValues values = AddAppContext(new ExpandValues
            {
                Clipboard = prompt.Clipboard,
                Sprint = _settings.Sprint,
                Matches = prompt.Captures,
            }, prompt.AppContext);

            List<string> resolved = new(definition.Options.Count);
            foreach (string option in definition.Options)
            {
                resolved.Add(TemplateEngine.ResolveChoiceOption(
                    option, now, prompt.Item.SequenceValue, values));
            }

            return resolved;
        }

        /// <summary>
        /// その選択肢を選んだ場合の完成形。ここまでに選んだものも反映する。
        /// まだ選んでいない選択肢は、書いたままの文字列として残る。
        /// </summary>
        private string BuildChoicePreview(ChoicePrompt prompt, string name, string option)
        {
            Dictionary<string, string> selected = new(prompt.Selected, StringComparer.OrdinalIgnoreCase)
            {
                [name] = option,
            };

            string expanded = TemplateEngine.Expand(
                prompt.Item.Text,
                DateTime.Now,
                prompt.Item.SequenceValue,
                AddAppContext(new ExpandValues
                {
                    Clipboard = prompt.Clipboard,
                    Sprint = _settings.Sprint,
                    Matches = prompt.Captures,
                    Choices = selected,
                }, prompt.AppContext));

            return TemplateEngine.ToSingleLine(expanded, 200);
        }

        private static void ShowCapturePrompt(ClipboardCaptureProgress progress, string title)
        {
            string condition = progress.Patterns.Count == 0
                ? string.Empty
                : $"\n条件: /{TemplateEngine.ToSingleLine(progress.Patterns[0], 70)}/"
                    + (progress.Patterns.Count > 1 ? $" ほか {progress.Patterns.Count - 1} 件" : string.Empty);

            ToastWindow.ShowToast(
                title,
                $"{progress.CapturedCount + 1}/{progress.TotalCount}: "
                    + $"「{progress.CurrentName}」に入れる文字列をコピーしてください"
                    + condition);
        }

        private void CancelCapture(bool showToast, bool rebuildMenu)
        {
            ClipboardCaptureSession? session = _actionSessions.Get<ClipboardCaptureSession>(
                TrayActionIds.MultipleInput);
            bool canceled = _actionSessions.Cancel(TrayActionIds.MultipleInput, session);

            if (rebuildMenu && !_disposed)
            {
                RebuildMenu();
            }

            if (showToast && canceled)
            {
                ToastWindow.ShowToast("複数入力をキャンセルしました", string.Empty);
            }
        }

        private void CopyToClipboard(
            ClipItem item,
            Func<string> clipboardReader,
            IReadOnlyDictionary<string, string>? inputs,
            IReadOnlyDictionary<string, string> captures,
            ForegroundApp appContext,
            IReadOnlyDictionary<string, string>? choices)
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
                AddAppContext(new ExpandValues
                {
                    Clipboard = () => clipboard,
                    Sprint = _settings.Sprint,
                    Inputs = inputs,
                    Matches = captures,
                    Choices = choices,

                    // HTML の項目では、差し込まれた値だけをエスケープする。
                    // 利用者が書いたタグは生かしたまま、{input:…} や {choice:…} に入った
                    // & や < が壊れた HTML にならないようにするため
                    ValueTransform = ClipboardService.GetValueTransform(item.Format),
                }, appContext));

            if (!ClipboardService.TryCopy(value, item.Format))
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

                    // プレーンテキスト以外で載せた場合は、その旨も伝える。
                    // 貼り付け先によって結果が変わるので、何で載せたかが分かるほうがよい
                    if (item.HasFormat)
                    {
                        label += $" ({item.FormatLabel})";
                    }

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

        /// <summary>
        /// いまコピーしてある文字列を、そのまま項目として登録する入口。
        /// 使えない状況でも項目自体は消さない。無いものを探させないため、無効にして理由を出す。
        /// </summary>
        private ToolStripMenuItem CreateQuickAddItem()
        {
            ToolStripMenuItem item = new("クリップボードを項目に追加(&A)...");

            // この状態で追加して保存すると、読めなかった元の設定を既定値で置き換えてしまう
            if (_settings.IsFallback)
            {
                item.Enabled = false;
                item.ToolTipText = "設定を読み込めていないため追加できません";
                return item;
            }

            if (!ClipboardService.HasText())
            {
                item.Enabled = false;
                item.ToolTipText = "登録したい文字列をコピーしてから、もう一度開いてください";
                return item;
            }

            item.ToolTipText = "いまコピーしてある文字列を、コピー項目として登録します";
            item.Click += (_, _) => ShowQuickAdd();
            return item;
        }

        /// <summary>クリップボードの内容を確かめ、名前を尋ねる窓を出す。</summary>
        private void ShowQuickAdd()
        {
            if (_settings.IsFallback)
            {
                ToastWindow.ShowToast(
                    "項目を追加できません",
                    "設定ファイルを読み込めていないため、追加すると元の設定を失うおそれがあります");
                return;
            }

            if (_quickAddWindow is not null)
            {
                _quickAddWindow.Activate();
                return;
            }

            // 前後の空白と改行は落とす。コピー操作は行末や改行を巻き込みやすく、
            // 見えない差で「同じ項目が 2 つ」になるのを避ける（{clip} の扱いとも揃う）
            string clipboard = ClipboardService.GetText().Trim();
            if (string.IsNullOrEmpty(clipboard))
            {
                ToastWindow.ShowToast(
                    "クリップボードが空です",
                    "登録したい文字列をコピーしてから、もう一度お試しください");
                return;
            }

            // 波かっこをそのままにすると、JSON やソースコードの一部が差し込みとして評価される
            string text = TemplateEngine.EscapeLiteral(clipboard);
            bool escaped = TemplateEngine.NeedsEscaping(clipboard);

            ClipItem? existing = FindItemByText(text);
            if (existing is not null)
            {
                ToastWindow.ShowToast(
                    "すでに同じ内容の項目があります",
                    string.IsNullOrWhiteSpace(existing.Name)
                        ? TemplateEngine.ToSingleLine(existing.Text, 60)
                        : existing.Name);
                return;
            }

            QuickAddWindow window = new(clipboard, escaped);
            _quickAddWindow = window;
            window.Closed += (_, _) =>
            {
                _quickAddWindow = null;
                if (window.Accepted)
                {
                    AddQuickItem(text, window.ItemName, escaped);
                }
            };

            window.Show();
            window.Activate();
        }

        /// <summary>
        /// 登録した項目を反映する。
        /// 設定画面が開いている場合は、画面が開いた時点の複製を持っていて
        /// 保存で上書きされてしまうため、ファイルではなく画面の一覧へ足す。
        /// </summary>
        private void AddQuickItem(string text, string name, bool escaped)
        {
            ClipItem item = new()
            {
                Id = ClipItem.NewId(),
                Name = name.Trim(),
                Text = text,
            };

            string escapeNote = escaped
                ? "\n{ } はそのままの文字として登録しました"
                : string.Empty;

            if (_settingsWindow is not null)
            {
                _settingsWindow.AddItem(item);
                ToastWindow.ShowToast(
                    "設定画面に追加しました",
                    "保存すると確定します" + escapeNote);
                return;
            }

            _settings.Items.Add(item);

            if (!TrySaveSettings())
            {
                _settings.Items.Remove(item);
                ToastWindow.ShowToast(
                    "項目を追加できませんでした",
                    "設定ファイルに保存できません。他のソフトが使用している可能性があります");
                return;
            }

            RebuildMenu();
            ToastWindow.ShowToast(
                "項目を追加しました",
                (string.IsNullOrWhiteSpace(item.Name)
                    ? TemplateEngine.ToSingleLine(item.Text, 60)
                    : item.Name)
                    + "\nメニューの末尾に追加しました。並べ替えは設定画面から行えます" + escapeNote);
        }

        /// <summary>同じ内容の項目が既にあるか探す。区切り線は対象外。</summary>
        private ClipItem? FindItemByText(string text)
            => _settings.Items.FirstOrDefault(
                i => !i.IsSeparator && string.Equals(i.Text, text, StringComparison.Ordinal));

        /// <summary>
        /// 設定をファイルへ書き出す。保存できたかどうかを返す。
        ///
        /// <para>
        /// 連番の保存では失敗してもコピー自体は成功しているため通知しないが、
        /// 項目の追加では保存できなければ何も起きていないのと同じなので、
        /// 呼び出し側が結果を見て知らせる。
        /// </para>
        /// </summary>
        private bool TrySaveSettings()
        {
            // 設定ファイルを読めずに既定値で動いている状態では、
            // 連番の自動保存で利用者の設定を既定値に置き換えてしまう
            if (_settings.IsFallback)
            {
                return false;
            }

            try
            {
                SettingsStore.Save(_settings);
                return true;
            }
            catch (Exception)
            {
                return false;
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
                _settingsWindow.NotifyAppContext(_lastKnownApp);
                _settingsWindow.Activate();
                if (_settingsWindow.WindowState == WindowState.Minimized)
                {
                    _settingsWindow.WindowState = WindowState.Normal;
                }
                return;
            }

            _settingsWindow = new SettingsWindow(
                _settings.Clone(),
                _recentApps,
                _menuComposer.Definitions,
                _lastKnownApp);
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

        /// <summary>
        /// メニューの所有者を、画面に出ないウィンドウに設定する。
        /// 32bit の user32.dll には SetWindowLongPtrW が無いため、呼び分ける。
        /// </summary>
        private static void SetMenuOwner(IntPtr menuHandle, IntPtr ownerHandle)
        {
            if (menuHandle == IntPtr.Zero || ownerHandle == IntPtr.Zero)
            {
                return;
            }

            if (IntPtr.Size == 8)
            {
                _ = SetWindowLongPtr(menuHandle, GwlHwndParent, ownerHandle);
                return;
            }

            _ = SetWindowLong(menuHandle, GwlHwndParent, ownerHandle.ToInt32());
        }

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool IsWindow(IntPtr hWnd);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool PostMessage(IntPtr hWnd, int message, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
        private static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int index, IntPtr value);

        [DllImport("user32.dll", EntryPoint = "SetWindowLongW", SetLastError = true)]
        private static extern int SetWindowLong(IntPtr hWnd, int index, int value);

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            ThemeManager.ThemeChanged -= OnThemeChanged;
            _actionSessions.Dispose();
            _menuHotKey?.Dispose();
            _menuHotKey = null;
            _notifyIcon.MouseUp -= OnIconMouseUp;
            _notifyIcon.MouseDown -= OnIconMouseDown;
            _notifyIcon.Visible = false;
            _notifyIcon.ContextMenuStrip?.Dispose();
            _notifyIcon.Dispose();
            _icon?.Dispose();
            _icon = null;
            _menuHost.Dispose();
        }
    }
}
