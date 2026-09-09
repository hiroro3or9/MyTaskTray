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
    /// <summary>実行中の作業セッションと、連続コピー＆ペーストを管理する。</summary>
    public sealed partial class TrayIconManager
    {
        private SequentialProgressWindow? _sequentialProgressWindow;

        private void UpdateSequentialProgressPanel()
        {
            SequentialCopyPasteSession? session = _actionSessions.Get<SequentialCopyPasteSession>(
                TrayActionIds.SequentialCopyPaste);
            if (_disposed || session is null)
            {
                CloseSequentialProgressPanel();
                return;
            }

            if (_sequentialProgressWindow is null)
            {
                SequentialProgressWindow panel = new();
                panel.UndoRequested += UndoSequentialCapture;
                panel.BeginPastingRequested += BeginSequentialPasting;
                panel.CancelRequested += () => CancelSequentialCopyPaste(showToast: true, rebuildMenu: true);
                // 対象は行の位置ではなく ID。更新直前のクリックでも別のコピーを消さない。
                panel.RemoveRequested += id =>
                {
                    if (_actionSessions.IsCurrent(TrayActionIds.SequentialCopyPaste, session)
                        && session.TryRemoveCapture(id))
                    {
                        RebuildMenu();
                    }
                };
                panel.MoveRequested += (id, offset) =>
                {
                    if (_actionSessions.IsCurrent(TrayActionIds.SequentialCopyPaste, session)
                        && session.TryMoveCapture(id, offset))
                    {
                        RebuildMenu();
                    }
                };
                _sequentialProgressWindow = panel;
                panel.Update(session.Queue);
                panel.Show();
                return;
            }

            _sequentialProgressWindow.Update(session.Queue);
        }

        private void CloseSequentialProgressPanel()
        {
            SequentialProgressWindow? panel = _sequentialProgressWindow;
            _sequentialProgressWindow = null;
            panel?.Close();
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
                        ToolTipText = $"{DescribeCaptureAction()}てください。B で最初の Ctrl+V を押すと収集を終えます",
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
                    trigger: _settings.SequentialCaptureTrigger,
                    captured: (_, _) =>
                    {
                        if (!_actionSessions.IsCurrent(TrayActionIds.SequentialCopyPaste, session))
                        {
                            return;
                        }

                        RebuildMenu();
                    },
                    captureRejected: reason =>
                    {
                        if (!_actionSessions.IsCurrent(TrayActionIds.SequentialCopyPaste, session))
                        {
                            return;
                        }

                        // 理由ごとに文面を変える。「文字列をコピーしてください」だけでは、
                        // 保存を拒まれた場合に何が起きたのか分からない。
                        (string title, string body) = reason switch
                        {
                            SequentialCaptureRejection.MonitoringExcluded => (
                                "連続コピーに追加できません",
                                "コピー元が、他のアプリでの保存を許可していません（パスワード管理ソフトなど）"),
                            SequentialCaptureRejection.NothingCaptured => (
                                "まだ貼り付けるものがありません",
                                $"{DescribeCaptureAction()}、内容を集めてください"),
                            _ => (
                                "連続コピーに追加できません",
                                "文字列をコピーしてください。空またはテキスト以外の内容は追加されません"),
                        };

                        ToastWindow.ShowToast(title, body);
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
                        }
                    },
                    pasteFailed: () =>
                    {
                        if (!_actionSessions.IsCurrent(TrayActionIds.SequentialCopyPaste, session))
                        {
                            return;
                        }

                        // 最初の Ctrl+V で収集を終了した後に失敗した場合も、段階を同期する。
                        RebuildMenu();
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
        }

        /// <summary>
        /// 収集の設定に合わせた、集める操作の案内。
        /// Ctrl+C 限定のときだけキーを明示する。それ以外では
        /// 右クリックの「コピー」やアプリのコピーボタンでも集まるため、
        /// キーを書くとかえって狭く伝わる。
        /// </summary>
        private string DescribeCaptureAction()
        {
            // 実行中のセッションは開始時の設定で動いている。
            // 途中で設定を変えられても、案内と実際の動きがずれないようにする。
            SequentialCopyPasteSession? session = _actionSessions.Get<SequentialCopyPasteSession>(
                TrayActionIds.SequentialCopyPaste);
            SequentialCaptureTrigger trigger =
                session?.Trigger ?? _settings.SequentialCaptureTrigger;

            return trigger == SequentialCaptureTrigger.CopyKey
                ? "A でデータを順番に Ctrl+C し"
                : "A でデータを順番にコピーし";
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
        }

        private void UndoSequentialCapture()
        {
            SequentialCopyPasteSession? session = _actionSessions.Get<SequentialCopyPasteSession>(
                TrayActionIds.SequentialCopyPaste);
            if (session is null || !session.TryUndoLastCapture(out _))
            {
                return;
            }

            RebuildMenu();
        }

        private void CancelSequentialCopyPaste(bool showToast, bool rebuildMenu)
        {
            SequentialCopyPasteSession? session = _actionSessions.Get<SequentialCopyPasteSession>(
                TrayActionIds.SequentialCopyPaste);
            bool canceled = _actionSessions.Cancel(TrayActionIds.SequentialCopyPaste, session);
            CloseSequentialProgressPanel();

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
    }
}
