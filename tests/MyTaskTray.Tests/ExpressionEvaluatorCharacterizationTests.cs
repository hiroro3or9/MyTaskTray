using MyTaskTray.Services;
using Xunit;

namespace MyTaskTray.Tests;

public sealed class ExpressionEvaluatorCharacterizationTests
{
    [Theory]
    [InlineData("2 + 3 * 4", "14")]
    [InlineData("2^3^2", "512")]
    [InlineData("-2^2", "4")]
    [InlineData("-(2^2)", "-4")]
    [InlineData("200*10%", "20")]
    [InlineData("1_000.5 + 0.5", "1001")]
    [InlineData("sum(1,2,3)+avg(2,4)", "9")]
    [InlineData("round(2.55,1)", "2.6")]
    public void EvaluatePreservesCurrentOperatorAndFunctionRules(
        string expression,
        string expected)
    {
        decimal result = ExpressionEvaluator.Evaluate(expression);

        Assert.Equal(decimal.Parse(expected, System.Globalization.CultureInfo.InvariantCulture), result);
    }

    [Fact]
    public void EvaluateWrapsDivisionByZeroAsExpressionException()
    {
        ExpressionException error = Assert.Throws<ExpressionException>(
            () => ExpressionEvaluator.Evaluate("1/0"));

        Assert.Contains("0 で割る", error.Message);
    }

    [Fact]
    public void EvaluateRejectsUnknownFunctions()
    {
        ExpressionException error = Assert.Throws<ExpressionException>(
            () => ExpressionEvaluator.Evaluate("unknown(1)"));

        Assert.Contains("関数はありません", error.Message);
    }

    [Fact]
    public void EvaluateRejectsExpressionsThatAreTooDeep()
    {
        string expression = new string('(', 70) + "1" + new string(')', 70);

        ExpressionException error = Assert.Throws<ExpressionException>(
            () => ExpressionEvaluator.Evaluate(expression));

        Assert.Contains("深すぎます", error.Message);
    }

    [Fact]
    public void FormatWithoutPatternUsesInvariantCompactRepresentation()
    {
        Assert.Equal("1.23", ExpressionEvaluator.Format(1.2300m, string.Empty));
        Assert.Equal("2", ExpressionEvaluator.Format(2m, string.Empty));
    }
}
