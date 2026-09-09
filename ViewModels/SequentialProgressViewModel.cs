using MyTaskTray.Services;

namespace MyTaskTray.ViewModels;

/// <summary>クリップボードを読み直さず、セッションの現在値だけを表示する。</summary>
internal sealed class SequentialProgressViewModel
{
    public SequentialProgressViewModel(SequentialCopyPasteQueue queue)
    {
        IsCapturing = queue.Phase == SequentialCopyPastePhase.Capturing;
        TotalCount = queue.Items.Count;
        PastedCount = queue.PastedCount;
        CanEdit = IsCapturing && TotalCount > 0;
        Title = IsCapturing ? "連続コピー" : "連続貼り付け";
        Status = IsCapturing
            ? $"{TotalCount} 件を収集済み"
            : queue.Next is null ? "貼り付けを完了しています…" : $"次は {PastedCount + 1} / {TotalCount} 件目";
        PreviewLabel = IsCapturing ? "最後にコピーした内容" : "次に貼り付ける内容";
        string? value = (IsCapturing ? queue.LastCaptured : queue.Next)?.Value;
        Preview = value is null
            ? IsCapturing ? "コピーすると、ここに内容が表示されます" : "すべての内容を送りました"
            : TemplateEngine.ToSingleLine(value, 180);
        Hint = IsCapturing
            ? "貼り付け先で Ctrl+V を押すと、1 件目から貼り付けます。"
            : $"Ctrl+V で次へ · 残り {TotalCount - PastedCount} 件";
        Items = queue.Items.Select((item, index) => new SequentialProgressRow(
            item.Id,
            index < PastedCount ? "✓" : (index + 1).ToString(),
            TemplateEngine.ToSingleLine(item.Value, 160),
            !IsCapturing && index == PastedCount,
            IsCapturing,
            IsCapturing && index > 0,
            IsCapturing && index < TotalCount - 1)).ToArray();
    }

    public bool IsCapturing { get; }
    public bool CanEdit { get; }
    public string Title { get; }
    public string Status { get; }
    public string PreviewLabel { get; }
    public string Preview { get; }
    public string Hint { get; }
    public int TotalCount { get; }
    public int PastedCount { get; }
    public IReadOnlyList<SequentialProgressRow> Items { get; }
}

internal sealed record SequentialProgressRow(
    int Id, string Number, string Preview, bool IsNext, bool CanRemove, bool CanMoveUp, bool CanMoveDown);
