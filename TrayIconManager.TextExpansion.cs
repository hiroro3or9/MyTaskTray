using MyTaskTray.Models;
using MyTaskTray.Services;

namespace MyTaskTray;

public partial class TrayIconManager
{
    private TextExpansionSession? _textExpansion;

    private void RegisterTextExpansion()
    {
        _textExpansion?.Dispose();
        _textExpansion = null;
        if (!TextExpansionMatcher.TryValidate(_settings.Items, out _, out string error))
        {
            ToastWindow.ShowToast("自動展開を開始できません", error);
            return;
        }
        ClipItem[] items = _settings.Items.Where(item => item.ExpansionTrigger.Length > 0).ToArray();
        if (items.Length == 0) return;
        try
        {
            _textExpansion = new(items,
                () => !_disposed && !_swapCopyRunning && !_actionSessions.HasActiveSession
                    && _settingsWindow is null && !(_notifyIcon.ContextMenuStrip?.Visible ?? false),
                ExpandTypedTrigger);
        }
        catch (Exception)
        {
            ToastWindow.ShowToast("自動展開を開始できません", "入力の監視を登録できませんでした。設定を保存し直すと再試行します。");
        }
    }

    private void ExpandTypedTrigger(ClipItem item, ForegroundApp app, Func<bool> stillValid)
    {
        string clipboard = ClipboardService.GetText();
        if (TemplateEngine.ContainsClipboardDate(item.Text) && !TemplateEngine.CanParseClipboardDate(clipboard)) return;
        string text = TemplateEngine.Expand(item.Text, DateTime.Now, item.SequenceValue, new ExpandValues
        {
            Clipboard = () => clipboard,
            Sprint = _settings.Sprint,
            AppName = app.Name,
            AppTitle = app.Title,
        });
        if (text.Length == 0 || !stillValid()) return;
        if (!ClipboardService.TryCopy(text))
        {
            ToastWindow.ShowToast("自動展開できません", "クリップボードを更新できませんでした。呼び出し文字列は残しています。");
            return;
        }
        if (!stillValid()) return;
        if (!InputInjector.TryReplaceTrigger(item.ExpansionTrigger.Length))
        {
            ToastWindow.ShowToast("自動展開の入力を送信できません", "入力先を確認してください。本文はクリップボードにあります。");
            return;
        }
        if (item.UsesSequence)
        {
            item.AdvanceSequence();
            TrySaveSettings();
            _settingsWindow?.NotifySequenceAdvanced(item.Id, item.SequenceValue);
        }
    }
}
