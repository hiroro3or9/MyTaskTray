using MyTaskTray.Services;
using MyTaskTray.ViewModels;
using Xunit;

namespace MyTaskTray.Tests;

public sealed class SequentialProgressTests
{
    [Fact]
    public void EmptyQueueCannotStartPastingOrUndo()
    {
        SequentialCopyPasteQueue queue = new();

        Assert.False(queue.TryBeginPasting());
        Assert.False(queue.TryUndoLastCapture(out _));
        Assert.Null(queue.Next);
        SequentialProgressViewModel panel = new(queue);
        Assert.True(panel.IsCapturing);
        Assert.False(panel.CanEdit);
        Assert.Equal("0 件を収集済み", panel.Status);
        Assert.Empty(panel.Items);
    }

    [Fact]
    public void DuplicateCopiesHaveIndependentIdentityAndCanBeRemovedSeparately()
    {
        SequentialCopyPasteQueue queue = new();
        queue.Capture("同じ内容");
        queue.Capture("同じ内容");
        int firstId = queue.Items[0].Id;
        int secondId = queue.Items[1].Id;

        Assert.NotEqual(firstId, secondId);
        Assert.True(queue.TryRemove(firstId));
        Assert.False(queue.TryRemove(firstId));
        Assert.Equal(secondId, Assert.Single(queue.Items).Id);
    }

    [Fact]
    public void UndoAfterReorderingRemovesMostRecentlyCapturedItem()
    {
        SequentialCopyPasteQueue queue = CreateQueue();
        int lastId = queue.Items[2].Id;
        Assert.True(queue.TryMove(lastId, -1));
        Assert.True(queue.TryMove(lastId, -1));
        Assert.Equal("C", new SequentialProgressViewModel(queue).Preview);

        Assert.True(queue.TryUndoLastCapture(out string removed));

        Assert.Equal("C", removed);
        Assert.Equal(new[] { "A", "B" }, queue.Items.Select(item => item.Value));
        Assert.Equal("B", new SequentialProgressViewModel(queue).Preview);
    }

    [Fact]
    public void PastingUsesEditedOrderAndFreezesEditing()
    {
        SequentialCopyPasteQueue queue = CreateQueue();
        int firstId = queue.Items[0].Id;
        int secondId = queue.Items[1].Id;
        Assert.True(queue.TryMove(firstId, 1));
        Assert.True(queue.TryRemove(secondId));
        Assert.True(queue.TryMove(firstId, 1));
        Assert.True(queue.TryBeginPasting());

        Assert.False(queue.TryRemove(firstId));
        Assert.False(queue.TryMove(firstId, -1));
        Assert.False(queue.TryUndoLastCapture(out _));
        Assert.False(queue.TryBeginPasting());
        Assert.Throws<InvalidOperationException>(() => queue.Capture("D"));
        Assert.Equal("C", queue.Next!.Value);
        queue.Advance();
        Assert.Equal("A", queue.Next!.Value);
        queue.Advance();
        Assert.Null(queue.Next);
        Assert.Equal(2, queue.PastedCount);
        Assert.Throws<InvalidOperationException>(queue.Advance);
    }

    [Fact]
    public void FailedClipboardWriteCanLeaveNextItemAndProgressUnchanged()
    {
        SequentialCopyPasteQueue queue = CreateQueue();
        queue.TryBeginPasting();
        SequentialCopyItem pending = queue.Next!;

        // 書き込み失敗時は Advance を呼ばず、同じ項目を再試行する。
        SequentialProgressViewModel panel = new(queue);
        Assert.Equal(pending, queue.Next);
        Assert.Equal(0, panel.PastedCount);
        Assert.Equal("次は 1 / 3 件目", panel.Status);
        Assert.Equal("A", panel.Preview);
        Assert.False(panel.CanEdit);
    }

    [Fact]
    public void PanelHighlightsNextItemAndMarksOnlySentItems()
    {
        SequentialCopyPasteQueue queue = CreateQueue();
        queue.TryBeginPasting();
        queue.Advance();

        SequentialProgressViewModel panel = new(queue);

        Assert.Equal("次は 2 / 3 件目", panel.Status);
        Assert.Equal("B", panel.Preview);
        Assert.Equal("Ctrl+V で次へ · 残り 2 件", panel.Hint);
        Assert.Equal("✓", panel.Items[0].Number);
        Assert.False(panel.Items[0].IsNext);
        Assert.True(panel.Items[1].IsNext);
        Assert.False(panel.Items[2].IsNext);
        Assert.All(panel.Items, row => Assert.False(row.CanRemove || row.CanMoveDown || row.CanMoveUp));

        queue.Advance();
        queue.Advance();
        panel = new SequentialProgressViewModel(queue);
        Assert.DoesNotContain(panel.Items, row => row.IsNext);
        Assert.All(panel.Items, row => Assert.Equal("✓", row.Number));
        Assert.DoesNotContain("4 / 3", panel.Status);
    }

    [Fact]
    public void InvalidMovesDoNotChangeQueueAndDeletingEverythingRestoresEmptyState()
    {
        SequentialCopyPasteQueue queue = CreateQueue();
        Assert.False(queue.TryMove(queue.Items[0].Id, -1));
        Assert.False(queue.TryMove(queue.Items[2].Id, 1));
        Assert.False(queue.TryMove(queue.Items[0].Id, 2));
        Assert.False(queue.TryMove(-1, 1));
        Assert.Equal(new[] { "A", "B", "C" }, queue.Items.Select(item => item.Value));

        foreach (int id in queue.Items.Select(item => item.Id).ToArray())
        {
            Assert.True(queue.TryRemove(id));
        }

        Assert.False(queue.TryBeginPasting());
        Assert.False(new SequentialProgressViewModel(queue).CanEdit);
        Assert.Null(queue.LastCaptured);
    }

    [Fact]
    public void PreviewIsBoundedWithoutModifyingMultilinePayload()
    {
        SequentialCopyPasteQueue queue = new();
        string value = "1 行目\r\n" + new string('あ', 5000);
        queue.Capture(value);
        SequentialProgressViewModel panel = new(queue);

        Assert.DoesNotContain("\n", panel.Preview);
        Assert.True(panel.Preview.Length <= 181);
        Assert.True(panel.Items[0].Preview.Length <= 161);
        queue.TryBeginPasting();
        Assert.Equal(value, queue.Next!.Value);
    }

    private static SequentialCopyPasteQueue CreateQueue()
    {
        SequentialCopyPasteQueue queue = new();
        queue.Capture("A");
        queue.Capture("B");
        queue.Capture("C");
        return queue;
    }
}
