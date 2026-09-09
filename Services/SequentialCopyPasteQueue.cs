using System.Collections.ObjectModel;

namespace MyTaskTray.Services;

internal sealed record SequentialCopyItem(int Id, string Value);

/// <summary>収集順の識別子と貼り付け順を分け、貼り付け開始後は編集を禁止する。</summary>
internal sealed class SequentialCopyPasteQueue
{
    private readonly List<SequentialCopyItem> _items = [];
    private int _nextId;

    public SequentialCopyPasteQueue() => Items = _items.AsReadOnly();

    public ReadOnlyCollection<SequentialCopyItem> Items { get; }
    public SequentialCopyPastePhase Phase { get; private set; }
    public int PastedCount { get; private set; }
    public SequentialCopyItem? LastCaptured => _items.MaxBy(item => item.Id);
    public SequentialCopyItem? Next => Phase == SequentialCopyPastePhase.Pasting && PastedCount < _items.Count
        ? _items[PastedCount] : null;

    public void Capture(string value)
    {
        if (Phase != SequentialCopyPastePhase.Capturing)
        {
            throw new InvalidOperationException("貼り付け開始後は収集できません。");
        }

        _items.Add(new SequentialCopyItem(++_nextId, value));
    }

    public bool TryBeginPasting()
    {
        if (Phase != SequentialCopyPastePhase.Capturing || _items.Count == 0)
        {
            return false;
        }

        Phase = SequentialCopyPastePhase.Pasting;
        return true;
    }

    // クリップボードの書き込みに成功した後だけ呼ぶ。
    public void Advance()
    {
        if (Next is null)
        {
            throw new InvalidOperationException("次の貼り付け項目がありません。");
        }

        PastedCount++;
    }

    public bool TryRemove(int id)
    {
        if (Phase != SequentialCopyPastePhase.Capturing)
        {
            return false;
        }

        return _items.RemoveAll(item => item.Id == id) > 0;
    }

    public bool TryUndoLastCapture(out string removed)
    {
        removed = string.Empty;
        if (LastCaptured is not { } last || !TryRemove(last.Id))
        {
            return false;
        }

        removed = last.Value;
        return true;
    }

    public bool TryMove(int id, int offset)
    {
        if (Phase != SequentialCopyPastePhase.Capturing || offset is not (-1 or 1))
        {
            return false;
        }

        int index = _items.FindIndex(item => item.Id == id);
        int target = index + offset;
        if (index < 0 || target < 0 || target >= _items.Count)
        {
            return false;
        }

        (_items[index], _items[target]) = (_items[target], _items[index]);
        return true;
    }
}
