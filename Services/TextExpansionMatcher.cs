using MyTaskTray.Models;

namespace MyTaskTray.Services;

/// <summary>合図の検証と、入力履歴を保存しない短い接頭辞の照合。</summary>
internal sealed class TextExpansionMatcher
{
    private readonly ClipItem[] _items;
    private string _prefix = string.Empty;

    public TextExpansionMatcher(IEnumerable<ClipItem> items) => _items = items.ToArray();

    public void Reset() => _prefix = string.Empty;

    public ClipItem? Push(char character)
    {
        _prefix = character == ';' ? ";" : _prefix + character;
        ClipItem? match = _items.FirstOrDefault(item => item.ExpansionTrigger == _prefix);
        if (match is not null || !_items.Any(item => item.ExpansionTrigger.StartsWith(_prefix, StringComparison.Ordinal)))
            Reset();
        return match;
    }

    public static bool TryValidate(IEnumerable<ClipItem> items, out ClipItem? invalidItem, out string error)
    {
        List<string> triggers = [];
        foreach (ClipItem item in items)
        {
            string trigger = item.ExpansionTrigger;
            if (trigger.Length == 0) continue;
            invalidItem = item;
            if (trigger.Length is < 2 or > 32 || trigger[0] != ';'
                || trigger.AsSpan(1).ContainsAnyExcept("abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789_-"))
            {
                error = "呼び出し文字列は ; で始まる 2～32 文字にしてください。続けて使えるのは半角英数字・_・- です。";
                return false;
            }
            if (item.IsSeparator || item.Format != ClipFormat.Plain || item.UsesInputs || item.UsesChoices
                || item.ClipboardCondition != ClipboardMatchKind.Always || item.ApplyToEachLine)
            {
                error = "自動展開は通常のテキスト項目で使えます。書式・入力／選択肢・クリップボード条件・複数行への適用を外してください。";
                return false;
            }
            if (triggers.Any(other => other.StartsWith(trigger, StringComparison.Ordinal)
                || trigger.StartsWith(other, StringComparison.Ordinal)))
            {
                error = "呼び出し文字列が重複しているか、別の合図の先頭と一致しています（例: ;sig と ;signature）。";
                return false;
            }
            triggers.Add(trigger);
        }
        invalidItem = null;
        error = string.Empty;
        return true;
    }
}
