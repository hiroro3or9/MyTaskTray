using System.ComponentModel;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
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
    public sealed partial class TrayIconManager : IDisposable
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

        /// <summary>
        /// 表示条件を持たない項目（常に表示）に渡す「空の 1 件」。
        /// 中身が空の辞書なので、<c>{match:…}</c> は書いたままの文字列として残る。
        /// </summary>
        private static readonly IReadOnlyList<IReadOnlyDictionary<string, string>> EmptyCaptureRows
            = [EmptyCaptures];

        // 同じ ContextMenuStrip は開くたびに中身を作り直す。
        // KeyDown をそのたびに追加すると、1 回の押下で過去のハンドラーまで全部動くため、
        // ドロップダウンごとに 1 個だけ持ち、現在の番号一覧だけを更新する。
        private static readonly ConditionalWeakTable<ToolStripDropDown, NumberKeyBinding>
            NumberKeyBindings = [];

        private sealed class NumberKeyBinding
        {
            private List<ToolStripMenuItem> _numbered = [];
            private readonly HashSet<Keys> _pressed = [];

            public NumberKeyBinding(ToolStripDropDown dropDown)
            {
                dropDown.KeyDown += OnKeyDown;
                dropDown.KeyUp += OnKeyUp;
                dropDown.Closed += (_, _) => _pressed.Clear();
            }

            public void Update(List<ToolStripMenuItem> numbered)
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

        /// <summary>
        /// メニューに並べる 1 項目と、その項目に差し込む <c>{match:…}</c> の値。
        /// </summary>
        /// <param name="Captures">
        /// 差し込む値。通常は 1 件で、項目の「複数行にも適用する」が効いたときだけ
        /// 行の数だけ並ぶ。件の数だけコピー文字列を展開して改行でつなぐ。
        /// </param>
        /// <param name="BulkHint">
        /// 複数行に適用したときにツールチップへ添える説明（件数・1 かたまりの行数・
        /// 打ち切りや端数で対象外になった行）。従来どおりの 1 件なら空文字。
        /// メニューを組み立てる場所にしか材料が揃わないため、ここで作って持ち回る。
        /// </param>
        private readonly record struct MenuEntry(
            ClipItem Item,
            IReadOnlyList<IReadOnlyDictionary<string, string>> Captures,
            ForegroundApp AppContext,
            string BulkHint);

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
        private string _lastQuickAddCategory = string.Empty;
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
            UpdateSequentialProgressPanel();
        }

        /// <summary>現在のクリップボードとキャプチャ状態からメニュー内容を作る。</summary>
        private void PopulateMenu(ContextMenuStrip menu)
        {
            ClearAndDispose(menu.Items);
            menu.ShowImageMargin = false;

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
                    regular.Add(new MenuEntry(item, EmptyCaptureRows, _menuContext, string.Empty));
                    continue;
                }

                ClipboardMatchRows rows = ClipboardMatcher.MatchEach(item, clipboard());
                if (rows.IsMatch)
                {
                    smart.Add(new MenuEntry(
                        item,
                        [.. rows.Rows.Select(static row => row.Captures)],
                        _menuContext,
                        DescribeBulk(item, rows)));
                }
            }

            // 表示条件で一部の項目が抜けても、残った項目は保存済みのメニュー配置順を保つ。
            regular = OrderEntriesByLayout(regular, _settings.RegularMenu);
            smart = OrderEntriesByLayout(smart, _settings.ContextualMenu);

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

            ToolStripMenuItem parent = new("作業ツール(&T)");
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
                        ? (HidesMenuToolTip(action.Id) ? string.Empty : action.ToolTip)
                        : availability.DisabledReason,
                    Tag = action.Id,
                };
                actionItem.Click += (_, _) => ExecuteTrayAction(action, context);
                items.Add(actionItem);
                previousGroup = action.Group;
            }
        }

        /// <summary>
        /// ツールチップがクリックの邪魔になる項目は、メニューでの表示を省く。
        /// </summary>
        private static bool HidesMenuToolTip(string actionId)
            => string.Equals(actionId, TrayActionIds.SequentialCopyPaste, StringComparison.Ordinal);

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
        /// 明示的なメニュー配置に従い、現在表示できる項目だけを同じ相対順で並べる。
        /// 未知の項目は後方へ残すため、手編集された古い設定でも項目を失わない。
        /// </summary>
        private static List<MenuEntry> OrderEntriesByLayout(
            IEnumerable<MenuEntry> entries,
            IReadOnlyList<MenuLayoutNode> layout)
        {
            List<MenuEntry> source = [.. entries];
            Dictionary<string, MenuEntry> byId = source.ToDictionary(
                entry => entry.Item.Id,
                StringComparer.Ordinal);
            HashSet<string> used = new(StringComparer.Ordinal);
            List<MenuEntry> result = [];

            foreach (MenuLayoutNode node in layout)
            {
                IEnumerable<string> ids = node.Kind == MenuLayoutNodeKind.Item
                    ? [node.Id]
                    : node.Children;
                foreach (string id in ids)
                {
                    if (byId.TryGetValue(id, out MenuEntry entry) && used.Add(id))
                    {
                        result.Add(entry);
                    }
                }
            }

            result.AddRange(source.Where(entry => used.Add(entry.Item.Id)));
            return result;
        }

        /// <summary>
        /// 配置順を保ちながら、同じカテゴリ ID の項目をサブメニューへ振り分ける。
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
                        menuEntry.BulkHint,
                        enabled);

                string categoryId = item.CategoryId.Trim();
                ClipCategory? categoryDefinition = categoryId.Length > 0
                    ? _settings.Categories.FirstOrDefault(candidate => string.Equals(
                        candidate.Id,
                        categoryId,
                        StringComparison.Ordinal))
                    : null;
                string category = categoryDefinition?.Name ?? item.Category.Trim();
                string categoryKey = categoryId.Length > 0 ? categoryId : category;

                if (string.IsNullOrEmpty(category))
                {
                    target.Add(entry);
                    if (entry is ToolStripMenuItem topLevelItem)
                    {
                        numbered.Add(topLevelItem);
                    }

                    continue;
                }

                if (!categories.TryGetValue(categoryKey, out ToolStripMenuItem? parent))
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

                    Image? decoration = CreateCategoryMenuImage(categoryDefinition);
                    if (decoration is not null)
                    {
                        parent.Image = decoration;
                        parent.ImageScaling = ToolStripItemImageScaling.SizeToFit;
                        parent.Disposed += (_, _) => decoration.Dispose();
                    }

                    categories[categoryKey] = parent;
                    target.Add(parent);
                    if (decoration is not null && parent.Owner is ToolStripDropDownMenu owner)
                    {
                        owner.ShowImageMargin = true;
                    }
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

        /// <summary>カテゴリの色・アイコンを、WinForms メニュー用の小さな画像へ描画する。</summary>
        private static Bitmap? CreateCategoryMenuImage(ClipCategory? category)
        {
            if (category is null)
            {
                return null;
            }

            string colorValue = CategoryAppearanceCatalog.NormalizeColor(category.Color);
            string glyph = CategoryAppearanceCatalog.GetIconGlyph(category.Icon);
            if (colorValue.Length == 0 && glyph.Length == 0)
            {
                return null;
            }

            Color color = colorValue.Length > 0
                ? Color.FromArgb(
                    Convert.ToByte(colorValue.Substring(1, 2), 16),
                    Convert.ToByte(colorValue.Substring(3, 2), 16),
                    Convert.ToByte(colorValue.Substring(5, 2), 16))
                : ThemeManager.IsDark
                    ? ThemeManager.TrayMenuColors.Text
                    : System.Drawing.SystemColors.MenuText;
            Bitmap bitmap = new(16, 16, System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
            using Graphics graphics = Graphics.FromImage(bitmap);
            graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;

            if (glyph.Length == 0)
            {
                using SolidBrush dotBrush = new(color);
                graphics.FillEllipse(dotBrush, 4, 4, 8, 8);
                return bitmap;
            }

            graphics.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
            using SolidBrush iconBrush = new(color);
            using System.Drawing.Font iconFont = new(
                CategoryAppearanceCatalog.IconFontFamily,
                12,
                System.Drawing.FontStyle.Regular,
                GraphicsUnit.Pixel);
            using StringFormat format = new()
            {
                Alignment = StringAlignment.Center,
                LineAlignment = StringAlignment.Center,
                FormatFlags = StringFormatFlags.NoWrap,
            };
            graphics.DrawString(glyph, iconFont, iconBrush, new RectangleF(0, 0, 16, 16), format);
            return bitmap;
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
            List<ToolStripMenuItem> numbered,
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
        /// これは設定画面で作った明示的なメニュー配置順と揃えている。
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

            IReadOnlyList<ClipCategory> categories = _settingsWindow?.GetKnownCategories()
                ?? [.. _settings.Categories
                    .OrderBy(category => category.Name, StringComparer.CurrentCulture)
                    .Select(category => category.Clone())];
            string initialCategory = categories.Any(category => string.Equals(
                category.Name,
                _lastQuickAddCategory,
                StringComparison.Ordinal))
                ? _lastQuickAddCategory
                : string.Empty;

            QuickAddWindow window = new(clipboard, escaped, categories, initialCategory);
            _quickAddWindow = window;
            window.Closed += (_, _) =>
            {
                _quickAddWindow = null;
                if (window.Accepted)
                {
                    _lastQuickAddCategory = window.ItemCategory;
                    AddQuickItem(text, window.ItemName, window.ItemCategory, escaped);
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
        private void AddQuickItem(string text, string name, string category, bool escaped)
        {
            ClipItem item = new()
            {
                Id = ClipItem.NewId(),
                Name = name.Trim(),
                Text = text,
                Category = category.Trim(),
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
                    + (item.Category.Length == 0
                        ? "\nトップレベルの末尾に追加しました"
                        : $"\nカテゴリ「{item.Category}」に追加しました")
                    + "。並べ替えは設定画面から行えます" + escapeNote);
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
            CloseSequentialProgressPanel();
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
