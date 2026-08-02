using OpenLogViewer.Core;
using Xunit;

namespace OpenLogViewer.Tests;

public class MathExpressionTests
{
    private static readonly string[] Channels =
        ["RPM", "MAP", "AFR", "AFR Target 1", "CLT", "Boost psi", "Fuel: total"];

    private static double Eval(string text, params (string Channel, double Value)[] values)
    {
        MathExpression expression = MathExpression.Parse(text, Channels);

        var inputs = new double[expression.References.Count];
        for (int i = 0; i < inputs.Length; i++)
        {
            string name = expression.References[i];
            inputs[i] = values.FirstOrDefault(
                v => v.Channel.Equals(name, StringComparison.OrdinalIgnoreCase),
                (Channel: name, Value: double.NaN)).Value;
        }

        return expression.Evaluate(inputs);
    }

    private static string? ErrorFor(string text)
    {
        MathExpression.TryParse(text, Channels, out _, out string? error);
        return error;
    }

    [Theory]
    [InlineData("1 + 2", 3)]
    [InlineData("2 * 3 + 1", 7)]
    [InlineData("1 + 2 * 3", 7)]
    [InlineData("(1 + 2) * 3", 9)]
    [InlineData("10 / 4", 2.5)]
    [InlineData("10 % 3", 1)]
    [InlineData("-5 + 2", -3)]
    [InlineData("--5", 5)]
    [InlineData("2 ^ 10", 1024)]
    [InlineData("1e-3", 0.001)]
    [InlineData("0.5", 0.5)]
    public void ArithmeticFollowsTheUsualPrecedence(string text, double expected) =>
        Assert.Equal(expected, Eval(text), 9);

    [Fact]
    public void PowerIsRightAssociative() =>
        Assert.Equal(512, Eval("2 ^ 3 ^ 2"), 9);   // 2^9, not (2^3)^2

    [Fact]
    public void AChannelNameWithSpacesNeedsNoQuoting()
    {
        // ECUs name channels this way. Requiring brackets would make the common
        // case the awkward one.
        double v = Eval("AFR - AFR Target 1", ("AFR", 13.0), ("AFR Target 1", 14.0));

        Assert.Equal(-1.0, v, 9);
    }

    [Fact]
    public void TheLongestMatchingChannelNameWins()
    {
        // "AFR Target 1" starts with "AFR"; taking the short one would leave
        // " Target 1" behind as a syntax error.
        MathExpression expression = MathExpression.Parse("AFR Target 1", Channels);

        Assert.Equal(["AFR Target 1"], expression.References);
    }

    [Fact]
    public void AChannelNameIsNotMatchedInsideALongerWord() =>
        Assert.Contains("MAPX", ErrorFor("MAPX + 1"));

    [Fact]
    public void PunctuationInAChannelNameIsHandled()
    {
        // MS3 emits names like "Fuel: total".
        double v = Eval("Fuel: total * 2", ("Fuel: total", 21));
        Assert.Equal(42, v, 9);
    }

    [Fact]
    public void EachChannelIsListedOnceHoweverOftenItAppears()
    {
        MathExpression expression = MathExpression.Parse("RPM + RPM * RPM", Channels);

        Assert.Equal(["RPM"], expression.References);
        Assert.Equal(12, expression.Evaluate([3]), 9);
    }

    [Theory]
    [InlineData("abs(-3)", 3)]
    [InlineData("sqrt(16)", 4)]
    [InlineData("min(3, 1, 2)", 1)]
    [InlineData("max(3, 1, 2)", 3)]
    [InlineData("clamp(9, 0, 5)", 5)]
    [InlineData("floor(1.8)", 1)]
    [InlineData("ceil(1.2)", 2)]
    [InlineData("round(1.5)", 2)]
    [InlineData("round(1.234, 2)", 1.23)]
    [InlineData("sign(-9)", -1)]
    [InlineData("pow(2, 8)", 256)]
    [InlineData("exp(0)", 1)]
    [InlineData("log10(1000)", 3)]
    public void FunctionsEvaluate(string text, double expected) =>
        Assert.Equal(expected, Eval(text), 9);

    [Theory]
    [InlineData("1 < 2", 1)]
    [InlineData("2 < 1", 0)]
    [InlineData("2 >= 2", 1)]
    [InlineData("2 != 2", 0)]
    [InlineData("1 && 0", 0)]
    [InlineData("1 || 0", 1)]
    [InlineData("!0", 1)]
    public void ComparisonsYieldOneOrZero(string text, double expected) =>
        Assert.Equal(expected, Eval(text), 9);

    [Fact]
    public void IfPicksABranch()
    {
        Assert.Equal(10, Eval("if(RPM > 500, 10, 20)", ("RPM", 800)), 9);
        Assert.Equal(20, Eval("if(RPM > 500, 10, 20)", ("RPM", 100)), 9);
    }

    [Fact]
    public void IfDoesNotEvaluateTheBranchItDoesNotTake()
    {
        // Guarding a division is the main reason to reach for "if"; evaluating
        // both branches would defeat it.
        Assert.Equal(0, Eval("if(RPM == 0, 0, 100 / RPM)", ("RPM", 0)), 9);
    }

    [Fact]
    public void ConstantsAreAvailable() => Assert.Equal(Math.PI, Eval("pi"), 9);

    [Theory]
    [InlineData("1 ? 10 : 20", 10)]
    [InlineData("0 ? 10 : 20", 20)]
    [InlineData("RPM > 500 ? 1 : 2", 1)]
    [InlineData("1 ? 2 ? 3 : 4 : 5", 3)]
    public void TheConditionalOperatorWorksLikeIf(string text, double expected) =>
        Assert.Equal(expected, Eval(text, ("RPM", 800)), 9);

    [Fact]
    public void TheConditionalOperatorAlsoGuardsItsBranches()
    {
        // Firmware INIs are written this way — "rpm ? 60000.0 / rpm : 0" — and
        // it only works if the untaken branch is not evaluated.
        Assert.Equal(0, Eval("RPM ? 60000 / RPM : 0", ("RPM", 0)), 9);
    }

    [Fact]
    public void AConditionalWithoutItsColonIsRejected() =>
        Assert.Contains(":", ErrorFor("1 ? 2"));

    [Theory]
    [InlineData("6 & 3", 2)]
    [InlineData("6 | 1", 7)]
    [InlineData("5 & 1", 1)]
    public void BitwiseOperatorsWorkOnTheIntegerValue(string text, double expected) =>
        Assert.Equal(expected, Eval(text), 9);

    [Fact]
    public void ASingleAmpersandIsNotTakenFromALogicalAnd()
    {
        // "1 && 0" must stay a logical and, not parse as "1 & (& 0)".
        Assert.Equal(0, Eval("1 && 0"), 9);
        Assert.Equal(1, Eval("1 || 0"), 9);
    }

    [Fact]
    public void BitwiseAndBindsLooserThanComparison()
    {
        // C's precedence, which is what firmware INIs are written against:
        // "6 & 3 == 2" is 6 & (3 == 2), not (6 & 3) == 2. Worth pinning, because
        // the other reading gives 1 here and would quietly change a flag test.
        Assert.Equal(0, Eval("6 & 3 == 2"), 9);
        Assert.Equal(1, Eval("(6 & 3) == 2"), 9);
    }

    // ----- missing readings -------------------------------------------------

    [Fact]
    public void AMissingReadingPropagatesThroughArithmetic() =>
        Assert.True(double.IsNaN(Eval("RPM + 1")));

    [Fact]
    public void AMissingReadingIsNotTreatedAsFalseByAComparison()
    {
        // Returning 0 would turn a dropout into a confident "no", and "if" would
        // then take a branch on the strength of a reading that was never taken.
        Assert.True(double.IsNaN(Eval("RPM > 500")));
        Assert.True(double.IsNaN(Eval("if(RPM > 500, 1, 2)")));
    }

    [Fact]
    public void AMissingReadingPropagatesThroughFunctions() =>
        Assert.True(double.IsNaN(Eval("max(RPM, 1000)")));

    // ----- errors -----------------------------------------------------------

    [Fact]
    public void AnUnknownNameIsReportedByName() =>
        Assert.Contains("Torque", ErrorFor("Torque * 2"));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("1 +")]
    [InlineData("(1 + 2")]
    [InlineData("1 + 2)")]
    [InlineData("* 3")]
    [InlineData("abs")]
    [InlineData("abs(1, 2)")]
    [InlineData("clamp(1, 2)")]
    [InlineData("RPM RPM")]
    public void MalformedExpressionsAreRejected(string text) =>
        Assert.False(MathExpression.TryParse(text, Channels, out _, out _));

    [Fact]
    public void TheWrongArgumentCountSaysHowManyAreWanted()
    {
        string? error = ErrorFor("clamp(1, 2)");

        Assert.Contains("clamp", error);
        Assert.Contains("3", error);
    }

    [Fact]
    public void ParseReportsWhereTheProblemIs()
    {
        var thrown = Assert.Throws<MathExpressionException>(
            () => MathExpression.Parse("RPM + + * 2", Channels));

        Assert.True(thrown.Position > 0);
    }

    [Fact]
    public void ALogChannelWinsOverAFunctionOfTheSameName()
    {
        // A log is free to name a channel "min"; its own names should win.
        MathExpression expression = MathExpression.Parse("min + 1", ["min"]);

        Assert.Equal(["min"], expression.References);
        Assert.Equal(4, expression.Evaluate([3]), 9);
    }

    [Fact]
    public void CaseDoesNotMatterWhenNamingAChannel()
    {
        MathExpression expression = MathExpression.Parse("rpm * 2", Channels);

        Assert.Equal(["RPM"], expression.References);   // reported as the log spells it
        Assert.Equal(200, expression.Evaluate([100]), 9);
    }
}
