using MyTaskTray.Models;
using MyTaskTray.Services;
using Xunit;

namespace MyTaskTray.Tests;

public sealed class ClipboardMatcherCharacterizationTests
{
    [Theory]
    [InlineData(ClipboardMatchKind.Date, "リリース日: 2026/08/27 まで")]
    [InlineData(ClipboardMatchKind.Url, "https://example.com/path?q=1")]
    [InlineData(ClipboardMatchKind.Number, "1,234.5")]
    [InlineData(ClipboardMatchKind.Json, "{\"value\":1}")]
    [InlineData(ClipboardMatchKind.FilePath, @"C:\Temp\file.txt")]
    [InlineData(ClipboardMatchKind.Email, "user@example.com")]
    public void MatchRecognizesBuiltInClipboardKinds(ClipboardMatchKind kind, string value)
    {
        ClipboardMatchResult result = ClipboardMatcher.Match(
            new ClipItem { ClipboardCondition = kind },
            value);

        Assert.True(result.IsMatch);
        Assert.Equal(value, result.Captures["value"]);
    }

    [Fact]
    public void MatchUrlProvidesStructuredCaptures()
    {
        ClipboardMatchResult result = ClipboardMatcher.Match(
            new ClipItem { ClipboardCondition = ClipboardMatchKind.Url },
            " https://Example.com/path/to?q=1 ");

        Assert.True(result.IsMatch);
        Assert.Equal("https", result.Captures["scheme"]);
        Assert.Equal("example.com", result.Captures["host"]);
        Assert.Equal("/path/to", result.Captures["path"]);
        Assert.Equal("q=1", result.Captures["query"]);
    }

    [Fact]
    public void MatchRegexKeepsWholeValueAndAddsNumberedAndNamedCaptures()
    {
        ClipboardMatchResult result = ClipboardMatcher.Match(
            new ClipItem
            {
                ClipboardCondition = ClipboardMatchKind.Regex,
                ClipboardPattern = @"ID-(?<number>\d+)",
            },
            "prefix ID-42 suffix");

        Assert.True(result.IsMatch);
        Assert.Equal("prefix ID-42 suffix", result.Captures["value"]);
        Assert.Equal("ID-42", result.Captures["0"]);
        Assert.Equal("42", result.Captures["1"]);
        Assert.Equal("42", result.Captures["number"]);
    }

    [Fact]
    public void MatchEachForNonRegexConditionsUsesNonEmptyLinesAndKeepsLeadingSpace()
    {
        ClipItem item = new()
        {
            ClipboardCondition = ClipboardMatchKind.HasText,
            ApplyToEachLine = true,
        };

        ClipboardMatchRows result = ClipboardMatcher.MatchEach(
            item,
            " first  \r\n\r\n second\r\n");

        Assert.False(result.Truncated);
        Assert.Equal([" first", " second"], result.Rows.Select(row => row.Captures["value"]));
    }

    [Fact]
    public void MatchEachRegexNormalizesCrLfAndUsesMultilineAnchors()
    {
        ClipItem item = new()
        {
            ClipboardCondition = ClipboardMatchKind.Regex,
            ClipboardPattern = @"^(?<word>\w+)$",
            ApplyToEachLine = true,
        };

        ClipboardMatchRows result = ClipboardMatcher.MatchEach(item, "one\r\ntwo\r\n--");

        Assert.False(result.Truncated);
        Assert.Equal(["one", "two"], result.Rows.Select(row => row.Captures["value"]));
        Assert.Equal(["one", "two"], result.Rows.Select(row => row.Captures["word"]));
    }

    [Fact]
    public void MatchEachStopsAtTheBulkRowLimitAndReportsTruncation()
    {
        ClipItem item = new()
        {
            ClipboardCondition = ClipboardMatchKind.HasText,
            ApplyToEachLine = true,
        };
        string clipboard = string.Join('\n', Enumerable.Range(1, ClipboardMatcher.MaxBulkRows + 1));

        ClipboardMatchRows result = ClipboardMatcher.MatchEach(item, clipboard);

        Assert.Equal(ClipboardMatcher.MaxBulkRows, result.Count);
        Assert.True(result.Truncated);
        Assert.Equal("1", result.Rows[0].Captures["value"]);
        Assert.Equal(ClipboardMatcher.MaxBulkRows.ToString(), result.Rows[^1].Captures["value"]);
    }

    [Fact]
    public void TryValidateRegexDistinguishesEmptyInvalidAndValidPatterns()
    {
        Assert.False(ClipboardMatcher.TryValidateRegex("", out string emptyError));
        Assert.Contains("入力", emptyError);

        Assert.False(ClipboardMatcher.TryValidateRegex("[", out string invalidError));
        Assert.Contains("正しくありません", invalidError);

        Assert.True(ClipboardMatcher.TryValidateRegex(@"^\d+$", out string validError));
        Assert.Empty(validError);
    }
}
