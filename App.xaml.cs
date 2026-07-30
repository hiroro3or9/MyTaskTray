using System.Threading;
using System.Windows;

namespace MyTaskTray
{
    /// <summary>
    /// アプリケーションのエントリポイント。ウィンドウを持たずタスクトレイに常駐する。
    /// </summary>
    public partial class App : System.Windows.Application
    {
        private const string MutexName = "MyTaskTray.SingleInstance.{8F3A6C1E-5B2D-4A77-9C10-2E7B41D9A6F2}";

        private Mutex? _instanceMutex;
        private TrayIconManager? _tray;

        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            // 二重起動を防止する
            _instanceMutex = new Mutex(true, MutexName, out bool isFirstInstance);
            if (!isFirstInstance)
            {
                System.Windows.MessageBox.Show(
                    "MyTaskTray はすでに起動しています。タスクトレイのアイコンをご確認ください。",
                    "MyTaskTray",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
                Shutdown();
                return;
            }

            // WinForms のメニューを現在の Windows テーマで描画させる
            System.Windows.Forms.Application.EnableVisualStyles();

            // Windows のライト / ダークとアクセント色に合わせる（設定変更にも追従）
            Services.ThemeManager.Initialize();

            _tray = new TrayIconManager();
            _tray.Start();
        }

        protected override void OnExit(ExitEventArgs e)
        {
            _tray?.Dispose();
            _instanceMutex?.Dispose();
            base.OnExit(e);
        }
    }
}
