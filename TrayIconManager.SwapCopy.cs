using MyTaskTray.Services;

namespace MyTaskTray
{
    /// <summary>
    /// Swap コピー（選択中の文字列と、クリップボードの内容の入れ替え）を受け持つ。
    ///
    /// <para>
    /// 実行にはグローバルホットキーだけを使う。トレイメニューから呼ぶと、
    /// メニューを開いた時点で前面が移り、貼り付け先の選択が失われる。
    /// 判断の経緯は DESIGN_SWAP_COPY.md §2 を参照。
    /// </para>
    /// </summary>
    public sealed partial class TrayIconManager
    {
        /// <summary>通知に載せる、入れ替えた内容の見出しの長さ。</summary>
        private const int SwapCopyPreviewLength = 40;

        private GlobalHotKey? _swapCopyHotKey;

        /// <summary>
        /// 実行中かどうか。1 回の入れ替えのあいだキーを送るため、
        /// 重ねて走らせると Ctrl+C と Ctrl+V が混ざる。
        /// </summary>
        private bool _swapCopyRunning;

        /// <summary>
        /// 設定されている場合だけ Swap コピー用のホットキーを登録する。
        /// 空欄は明示的な無効状態で、他アプリのキーを既定で奪わない。
        /// </summary>
        private void RegisterSwapCopyHotKey()
        {
            _swapCopyHotKey?.Dispose();
            _swapCopyHotKey = null;

            string configured = _settings.SwapCopyHotKey?.Trim() ?? string.Empty;
            if (string.IsNullOrEmpty(configured))
            {
                return;
            }

            if (!HotKeyGesture.TryParse(configured, out HotKeyGesture gesture, out string error))
            {
                ToastWindow.ShowToast("Swap コピーのホットキーが不正です", error);
                return;
            }

            try
            {
                _swapCopyHotKey = new GlobalHotKey(
                    gesture, RunSwapCopy, GlobalHotKeyIds.SwapCopy);

                if (_swapCopyHotKey.IsRegistered)
                {
                    return;
                }

                _swapCopyHotKey.Dispose();
                _swapCopyHotKey = null;
            }
            catch (Exception)
            {
                _swapCopyHotKey?.Dispose();
                _swapCopyHotKey = null;
            }

            ToastWindow.ShowToast(
                "Swap コピーのホットキーを登録できません",
                $"{gesture.DisplayName} は別のアプリで使用されている可能性があります");
        }

        /// <summary>
        /// ホットキーから呼ばれる入口。
        /// <c>WM_HOTKEY</c> の処理を止めないよう、待ち時間のある本体は投げっぱなしにする。
        /// </summary>
        private void RunSwapCopy()
        {
            if (_disposed || _swapCopyRunning)
            {
                return;
            }

            // 連続コピーなどの作業モードは、クリップボードと Ctrl+V を自分で使う。
            // 重ねると収集内容が黙って壊れるため、実行中は受け付けない
            if (_actionSessions.HasActiveSession)
            {
                ToastWindow.ShowToast(
                    "Swap コピーを実行できません", BuildActiveSessionBlockedReason());
                return;
            }

            _swapCopyRunning = true;
            _ = RunSwapCopyAsync();
        }

        private async Task RunSwapCopyAsync()
        {
            // 例外で抜けても、通知だけは出す。何も起きないと、
            // 利用者にはクリップボードがどうなったのか分からない
            SwapCopyOutcome outcome = SwapCopyOutcome.Failed(SwapCopyResult.ClipboardUnavailable);

            try
            {
                outcome = await SwapCopy.RunAsync();
            }
            catch (Exception)
            {
                // 入れ替えの途中で落ちても、常駐しているアプリごと終わらせない
            }
            finally
            {
                _swapCopyRunning = false;
            }

            if (_disposed)
            {
                return;
            }

            ShowSwapCopyResult(outcome);
        }

        /// <summary>
        /// 結果を通知にする。
        ///
        /// <para>
        /// 成功したときだけ「コピー通知」の設定に従う。失敗と中断は、
        /// クリップボードや文書の状態が利用者の想定とずれるため必ず伝える。
        /// </para>
        /// </summary>
        private void ShowSwapCopyResult(SwapCopyOutcome outcome)
        {
            if (outcome.Result == SwapCopyResult.Success && !_settings.ShowCopyNotification)
            {
                return;
            }

            (string title, string body) = outcome.Result switch
            {
                SwapCopyResult.Success => (
                    "Swap コピーしました",
                    $"貼り付け: {DescribeSwapValue(outcome.PastedText)}"
                        + $" / 次に貼り付け: {DescribeSwapValue(outcome.StashedText)}"),

                SwapCopyResult.ClipboardEmpty => (
                    "Swap コピーできません",
                    "クリップボードが空です。貼り付ける内容をコピーしてから実行してください"),

                SwapCopyResult.NothingSelected => (
                    "Swap コピーできません",
                    "コピーが行われませんでした。入れ替えたい文字を選んでから実行してください"),

                SwapCopyResult.InputBlocked => (
                    "Swap コピーできません",
                    "Ctrl+C / Ctrl+V を送れませんでした。管理者として実行されているアプリが前面にある可能性があります"),

                SwapCopyResult.OriginalRestoreFailed => (
                    "Swap コピーを中断しました",
                    "元のクリップボードへ戻せなかったため、貼り付けは行っていません。"
                        + "いまクリップボードにあるのは選択していた内容です"),

                SwapCopyResult.SelectionOverwritten => (
                    "Swap コピーは貼り付けまで完了しました",
                    "待っているあいだに別のコピーが行われたため、選択していた内容はクリップボードへ戻していません"),

                SwapCopyResult.SelectionRestoreFailed => (
                    "Swap コピーは貼り付けまで完了しました",
                    "選択していた内容をクリップボードへ戻せませんでした"),

                _ => (
                    "Swap コピーできません",
                    "クリップボードを読み書きできませんでした。他のアプリが使用している可能性があります"),
            };

            ToastWindow.ShowToast(title, body);
        }

        /// <summary>
        /// 通知に出す見出し。文字列を持たない内容（画像だけなど）でも
        /// 「何も起きなかった」と読めないようにする。
        /// </summary>
        private static string DescribeSwapValue(string value)
            => string.IsNullOrEmpty(value)
                ? "文字列以外の内容"
                : TemplateEngine.ToSingleLine(value, SwapCopyPreviewLength);
    }
}
