using System.Runtime.ExceptionServices;
using System.Text.Json;
using System.Threading;
using MyTaskTray.Models;
using MyTaskTray.Services;
using MyTaskTray.ViewModels;
using Xunit;

namespace MyTaskTray.Tests;

public sealed class TextExpansionTests
{
    [Fact]
    public void ExpandsOnlyAfterTheCompleteCaseSensitiveTrigger()
    {
        ClipItem item = Item(";sig");
        TextExpansionMatcher matcher = new([item]);
        foreach (char c in "ordinary text ;siX ;SIG ") Assert.Null(matcher.Push(c));
        Assert.Null(matcher.Push(';'));
        Assert.Null(matcher.Push('s'));
        Assert.Null(matcher.Push('i'));
        Assert.Same(item, matcher.Push('g'));
        Assert.Null(matcher.Push('g'));
    }

    [Fact]
    public void ResetCannotCompleteATriggerFromThePreviousInputLocation()
    {
        TextExpansionMatcher matcher = new([Item(";sig")]);
        foreach (char c in ";si") Assert.Null(matcher.Push(c));
        matcher.Reset();
        Assert.Null(matcher.Push('g'));
        foreach (char c in ";si") Assert.Null(matcher.Push(c));
        Assert.NotNull(matcher.Push('g'));
    }

    [Fact]
    public void NewSemicolonRestartsAnIncompleteTrigger()
    {
        TextExpansionMatcher matcher = new([Item(";sig")]);
        foreach (char c in ";s;si") Assert.Null(matcher.Push(c));
        Assert.NotNull(matcher.Push('g'));
    }

    [Theory]
    [InlineData("sig")]
    [InlineData(";")]
    [InlineData(";署名")]
    [InlineData(";a b")]
    [InlineData(";a\nb")]
    [InlineData(";a;b")]
    [InlineData(";abcdefghijklmnopqrstuvwxyz123456")]
    public void RejectsUnsafeOrUnsupportedTriggers(string trigger)
        => Assert.False(TextExpansionMatcher.TryValidate([Item(trigger)], out _, out _));

    [Theory]
    [InlineData(";sig", ";sig")]
    [InlineData(";sig", ";signature")]
    [InlineData(";signature", ";sig")]
    public void RejectsAmbiguousImmediateExpansion(string first, string second)
        => Assert.False(TextExpansionMatcher.TryValidate([Item(first), Item(second)], out _, out _));

    [Fact]
    public void AcceptsIndependentTriggersAndDisabledItems()
        => Assert.True(TextExpansionMatcher.TryValidate(
            [Item(""), Item(";sig"), Item(";Sig"), Item(";date_1-2")], out _, out _));

    [Theory]
    [InlineData("{input:名前}")]
    [InlineData("{choice:宛先:A|B}")]
    public void InteractiveTemplatesCannotAutomaticallyReplaceTypedText(string text)
    {
        ClipItem item = Item(";sig");
        item.Text = text;
        Assert.False(TextExpansionMatcher.TryValidate([item], out _, out _));
    }

    [Fact]
    public void RejectsFormatsAndConditionsOutsideInitialScope()
    {
        foreach (ClipItem item in new[]
        {
            new ClipItem { ExpansionTrigger = ";a", Format = ClipFormat.Html },
            new ClipItem { ExpansionTrigger = ";a", ClipboardCondition = ClipboardMatchKind.HasText },
            new ClipItem { ExpansionTrigger = ";a", ApplyToEachLine = true },
            new ClipItem { ExpansionTrigger = ";a", IsSeparator = true },
        }) Assert.False(TextExpansionMatcher.TryValidate([item], out _, out _));
    }

    [Fact]
    public void OldSettingsRemainDisabledAndNullIsNormalized()
    {
        Assert.Empty(JsonSerializer.Deserialize<ClipItem>("{\"Text\":\"hello\"}")!.ExpansionTrigger);
        Assert.Empty(JsonSerializer.Deserialize<ClipItem>("{\"ExpansionTrigger\":null}")!.ExpansionTrigger);
        Assert.All(AppSettings.CreateDefault().Items, item => Assert.Empty(item.ExpansionTrigger));
    }

    [Fact]
    public void JapaneseMultilineTemplateAndTriggerSurviveEditingAndJsonRoundTrip()
    {
        RunSta(() =>
        {
            ClipItem item = Item("");
            item.Text = "お世話になっております。\r\n{date:yyyy/MM/dd}";
            SettingsViewModel viewModel = new(new AppSettings { Items = [item] });
            viewModel.MarkSaved();
            viewModel.Items[0].ExpansionTrigger = ";sig";
            Assert.True(viewModel.IsDirty);
            AppSettings settings = viewModel.ToSettings("", "", null).Clone();
            AppSettings loaded = JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(settings))!;
            Assert.Equal(";sig", loaded.Items[0].ExpansionTrigger);
            Assert.Equal("お世話になっております。\r\n2026/09/14",
                TemplateEngine.Expand(loaded.Items[0].Text, new DateTime(2026, 9, 14), 1, new ExpandValues()));
        });
    }

    private static ClipItem Item(string trigger) => new() { Name = "署名", Text = "本文", ExpansionTrigger = trigger };

    private static void RunSta(Action action)
    {
        Exception? failure = null;
        Thread thread = new(() => { try { action(); } catch (Exception ex) { failure = ex; } });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(10)));
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }
}
