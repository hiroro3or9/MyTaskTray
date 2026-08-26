using MyTaskTray.Services;
using Xunit;

namespace MyTaskTray.Tests;

public sealed class TemplateEngineCharacterizationTests
{
    private static readonly DateTime FixedNow = new(2026, 8, 27, 14, 5, 6);

    [Fact]
    public void ExpandHandlesEscapesKnownTokensAndUnknownTokensTogether()
    {
        string result = TemplateEngine.Expand(
            "{{date}}|{date:yyyyMMdd}|{seq:000}|{unknown}",
            FixedNow,
            7,
            new ExpandValues());

        Assert.Equal("{date}|20260827|007|{unknown}", result);
    }

    [Fact]
    public void ExpandReadsClipboardLazilyAndOnlyOncePerExpansion()
    {
        int reads = 0;
        ExpandValues values = new()
        {
            Clipboard = () =>
            {
                reads++;
                return " 2026-08-30 ";
            },
        };

        Assert.Equal(
            "20260827",
            TemplateEngine.Expand("{date:yyyyMMdd}", FixedNow, 1, values));
        Assert.Equal(0, reads);

        string result = TemplateEngine.Expand(
            "{clip}|{clip:upper}|{date@clip:yyyyMMdd}",
            FixedNow,
            1,
            values);

        Assert.Equal("2026-08-30|2026-08-30|20260830", result);
        Assert.Equal(1, reads);
    }

    [Fact]
    public void ExpandSupportsNestedCalculationAndLiteralPipeReplacement()
    {
        string result = TemplateEngine.Expand(
            @"{calc:{seq}*1.5|0.0}|{replace:{input:name}|\||/}",
            FixedNow,
            7,
            new ExpandValues
            {
                Inputs = new Dictionary<string, string> { ["name"] = "A|B" },
            });

        Assert.Equal("10.5|A/B", result);
    }

    [Fact]
    public void ValueTransformAppliesOnlyToInsertedValues()
    {
        string result = TemplateEngine.Expand(
            "<b>{input:name}</b> {replace:{input:name}|&|and}",
            FixedNow,
            1,
            new ExpandValues
            {
                Inputs = new Dictionary<string, string> { ["name"] = "A&B" },
                ValueTransform = value => $"[{value}]",
            });

        Assert.Equal("<b>[A&B]</b> [AandB]", result);
    }

    [Fact]
    public void InvalidOrUnavailableTokensRemainVisible()
    {
        const string template = "{calc:1/0}|{seq+1d}|{clip}|{date@missing}";

        string result = TemplateEngine.Expand(template, FixedNow, 1, new ExpandValues());

        Assert.Equal(template, result);
    }

    [Fact]
    public void AnalyzeChoicesKeepsFirstDefinitionAndReportsProblems()
    {
        ChoiceAnalysis analysis = TemplateEngine.AnalyzeChoices(
            "{choice:敬称:様|御中}/{choice:敬称} "
            + "{choice:重複:A|B}{choices:重複:C|D} {choice:未定義}");

        Assert.Collection(
            analysis.Definitions,
            definition =>
            {
                Assert.Equal("敬称", definition.Name);
                Assert.Equal(["様", "御中"], definition.Options);
                Assert.False(definition.AllowMultiple);
            },
            definition =>
            {
                Assert.Equal("重複", definition.Name);
                Assert.Equal(["A", "B"], definition.Options);
                Assert.False(definition.AllowMultiple);
            });
        Assert.Contains(
            analysis.Issues,
            issue => issue.Kind == ChoiceIssueKind.Duplicate && issue.Name == "重複");
        Assert.Contains(
            analysis.Issues,
            issue => issue.Kind == ChoiceIssueKind.Undefined && issue.Name == "未定義");
    }

    [Fact]
    public void ChoiceHelpersUseWrittenOrderAndResolveOptionPlaceholders()
    {
        const string template = "{choice:日付:{date:yyyyMMdd}|未定}/{choice:日付}";
        IReadOnlyDictionary<string, string>? defaults = TemplateEngine.GetDefaultChoices(
            template,
            FixedNow,
            1,
            new ExpandValues());

        Assert.NotNull(defaults);
        Assert.Equal("20260827", defaults["日付"]);
        Assert.Equal(
            "確定/確定",
            TemplateEngine.Expand(
                template,
                FixedNow,
                1,
                new ExpandValues
                {
                    Choices = new Dictionary<string, string> { ["日付"] = "確定" },
                }));
        Assert.Equal(
            "田中、佐藤",
            TemplateEngine.JoinChoices(["田中", "", "佐藤"], [2, 0, 1]));
    }

    [Fact]
    public void InputDefinitionsMergeCaseInsensitiveNamesAndCollectEveryPattern()
    {
        IReadOnlyList<InputCaptureDefinition> definitions = TemplateEngine.GetInputDefinitions(
            @"{input:Name:/^(\w+)$/} {input:name:/\d+/} "
            + @"{replace:{input:Other}|{input:ignored}|x}");

        Assert.Collection(
            definitions,
            definition =>
            {
                Assert.Equal("Name", definition.Name);
                Assert.Equal([@"^(\w+)$", @"\d+"], definition.Patterns);
            },
            definition =>
            {
                Assert.Equal("Other", definition.Name);
                Assert.Empty(definition.Patterns);
            });
    }
}
