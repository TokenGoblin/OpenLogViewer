using OpenLogViewer.Core;
using Xunit;

namespace OpenLogViewer.Tests;

/// <summary>
/// Deciding whether a setting applies to the tune in hand.
///
/// The operators are C's, not the calculated-channel language's, and the two
/// differ in ways that only show up when they are wrong — a settings page
/// missing the field somebody went looking for.
/// </summary>
public class DialogConditionTests
{
    /// <summary>A firmware with a few settings in it. Anything else is unknown.</summary>
    private static readonly Dictionary<string, double> Tune = new(StringComparer.OrdinalIgnoreCase)
    {
        ["knk_option"] = 1,
        ["knk_option_an"] = 1,
        ["off"] = 0,
        ["algorithm"] = 6,
        ["status8"] = 0x40,
        ["nCylinders"] = 8,
    };

    private static double Lookup(string name) =>
        Tune.TryGetValue(name, out double value) ? value : double.NaN;

    private static ConditionVerdict Verdict(string condition) =>
        DialogCondition.Evaluate(condition, Lookup);

    private static bool Shown(string condition) => Verdict(condition) == ConditionVerdict.Shown;

    private static bool Hidden(string condition) => Verdict(condition) == ConditionVerdict.Hidden;

    // ----- the basics -------------------------------------------------------

    [Fact]
    public void NoConditionMeansAlwaysShown()
    {
        Assert.Equal(ConditionVerdict.Shown, Verdict(""));
        Assert.Equal(ConditionVerdict.Shown, Verdict("   "));
    }

    [Theory]
    [InlineData("knk_option", true)]          // non-zero is true, C-style
    [InlineData("off", false)]
    [InlineData("!knk_option", false)]
    [InlineData("!off", true)]
    [InlineData("algorithm == 6", true)]
    [InlineData("algorithm != 6", false)]
    [InlineData("algorithm > 5", true)]
    [InlineData("algorithm >= 6", true)]
    [InlineData("algorithm < 6", false)]
    [InlineData("algorithm <= 6", true)]
    public void ComparisonsAndTruthinessFollowC(string condition, bool shown) =>
        Assert.Equal(shown, Shown(condition));

    [Theory]
    [InlineData("knk_option && knk_option_an", true)]
    [InlineData("knk_option && off", false)]
    [InlineData("off || knk_option", true)]
    [InlineData("off || off", false)]
    [InlineData("knk_option && (knk_option_an == 1)", true)]
    [InlineData("!(off || off)", true)]
    public void LogicalOperatorsCombineConditions(string condition, bool shown) =>
        Assert.Equal(shown, Shown(condition));

    // ----- the bitwise half, which is where a general parser goes wrong ------

    [Fact]
    public void SingleAmpersandIsBitwiseRatherThanLogical()
    {
        // status8 is 0x40. Masking it with 0x40 leaves 0x40, which is true;
        // masking with 0x03 leaves nothing, which is false. A parser treating &
        // as "and" would call both of them true, because both operands are
        // non-zero.
        Assert.True(Shown("status8 & 0x40"));
        Assert.True(Hidden("status8 & 0x03"));
    }

    [Fact]
    public void AMaskedComparisonReadsAsItDoesInC()
    {
        // Equality binds tighter than &, so this is (status8 & 0x40) == 0 only
        // because of the brackets the firmware wrote.
        Assert.True(Hidden("(status8 & 0x40) == 0"));
        Assert.True(Shown("(status8 & 0x03) == 0"));
    }

    [Theory]
    [InlineData("0x40", true)]
    [InlineData("0X40", true)]
    [InlineData("0x00", false)]
    public void HexLiteralsAreRead(string condition, bool shown) =>
        Assert.Equal(shown, Shown(condition));

    [Fact]
    public void TheOtherBitwiseOperatorsWork()
    {
        Assert.True(Shown("(status8 | 0x01) == 0x41"));
        Assert.True(Shown("(status8 ^ 0x40) == 0"));
        // 8 is 0b1000, so it meets a mask of 8 and misses one of 4.
        Assert.True(Shown("(nCylinders & 0x08) == 8"));
        Assert.True(Hidden("(nCylinders & 0x04) == 4"));
    }

    [Fact]
    public void DoubledOperatorsAreNotMistakenForSingleOnes()
    {
        // "off || knk_option" must not be read as a bitwise or of "off" and a
        // stray "| knk_option", and likewise for &&.
        Assert.True(Shown("off || knk_option"));
        Assert.True(Hidden("knk_option && off"));
    }

    // ----- what it does when it cannot say ----------------------------------

    [Fact]
    public void AConditionNamingSomethingUnknownIsNotDecided()
    {
        // This firmware has no such setting. Neither answer is right, and the
        // caller shows the field rather than hiding a setting nobody can then
        // reach.
        Assert.Equal(ConditionVerdict.Unknown, Verdict("notAThing"));
        Assert.Equal(ConditionVerdict.Unknown, Verdict("notAThing == 2"));
        Assert.True(DialogCondition.ShouldShow("notAThing", Lookup));
    }

    [Fact]
    public void ACallToSomethingUnsupportedIsNotDecided()
    {
        // TunerStudio provides functions this does not. Skipping the call and
        // reporting unknown keeps the rest of the expression parseable.
        Assert.Equal(ConditionVerdict.Unknown, Verdict("arrayValue( array.boardHasRTC, pinLayout ) > 0"));
        Assert.Equal(ConditionVerdict.Unknown, Verdict("bitStringValue( portLabels, x )"));
    }

    [Fact]
    public void NonsenseIsNotDecidedRatherThanThrowing()
    {
        Assert.Equal(ConditionVerdict.Unknown, Verdict("algorithm =="));
        Assert.Equal(ConditionVerdict.Unknown, Verdict("(((("));
        Assert.Equal(ConditionVerdict.Unknown, Verdict("@£$"));
        Assert.Equal(ConditionVerdict.Unknown, Verdict("algorithm 6"));
    }

    [Fact]
    public void KnowingOneHalfIsEnoughWhenItSettlesTheAnswer()
    {
        // False and anything is false; true or anything is true. A condition
        // half of which cannot be judged is still decidable when the other half
        // decides it — which keeps a field from appearing merely because some
        // unrelated firmware option was not recognised.
        Assert.Equal(ConditionVerdict.Hidden, Verdict("off && notAThing"));
        Assert.Equal(ConditionVerdict.Shown, Verdict("knk_option || notAThing"));

        // And when it does not settle it, the answer is unknown.
        Assert.Equal(ConditionVerdict.Unknown, Verdict("knk_option && notAThing"));
        Assert.Equal(ConditionVerdict.Unknown, Verdict("off || notAThing"));
    }

    [Fact]
    public void ShortCircuitingDoesNotAbandonTheRestOfTheExpression()
    {
        // Deciding the answer early must not stop the parse. Two operands hid
        // this: the early exit happened once everything had been read anyway.
        // Three or more is where it bites, and three or more is what real
        // definitions are full of.
        Assert.Equal(ConditionVerdict.Hidden, Verdict("off && knk_option && knk_option"));
        Assert.Equal(ConditionVerdict.Hidden, Verdict("off && notAThing && knk_option"));
        Assert.Equal(ConditionVerdict.Shown, Verdict("knk_option || off || off"));
        Assert.Equal(ConditionVerdict.Shown, Verdict("knk_option || notAThing || off"));

        // Four, mixed, and bracketed, as they really come.
        Assert.Equal(
            ConditionVerdict.Hidden,
            Verdict("off && (algorithm == 6) && knk_option && knk_option_an"));
    }

    // ----- shapes taken from real definitions -------------------------------

    [Theory]
    [InlineData("knk_option && (knk_option_an == 1)")]
    [InlineData("((algorithm == 6) || (algorithm == 6))")]
    [InlineData("!(off == 1 || ( !(algorithm == 2 || algorithm == 5 || algorithm == 6)))")]
    [InlineData("nCylinders > 4 && nCylinders <= 12")]
    public void RealConditionsParse(string condition) =>
        Assert.NotEqual(ConditionVerdict.Unknown, Verdict(condition));

    [Fact]
    public void ANameMayCarryDotsAndBrackets()
    {
        double Lookup2(string name) => name switch
        {
            "psEnabled[0]" => 1,
            "array.thing" => 0,
            _ => double.NaN,
        };

        Assert.Equal(ConditionVerdict.Shown, DialogCondition.Evaluate("psEnabled[0]", Lookup2));
        Assert.Equal(ConditionVerdict.Hidden, DialogCondition.Evaluate("array.thing", Lookup2));
    }

    [Fact]
    public void ArithmeticIsAvailableEvenThoughItIsRare()
    {
        Assert.True(Shown("nCylinders / 2 == 4"));
        Assert.True(Shown("nCylinders - 8 == 0"));
        Assert.True(Shown("nCylinders * 2 == 16"));
        Assert.True(Shown("nCylinders + 1 == 9"));
    }

    [Fact]
    public void DividingByNothingIsNotDecidedRatherThanInfinite()
    {
        // An infinity compares in ways that look like answers.
        Assert.Equal(ConditionVerdict.Unknown, Verdict("nCylinders / off > 1"));
    }

    [Fact]
    public void PrecedenceMatches()
    {
        // && binds tighter than ||, so this is (0 && 0) || 1 and not 0 && (0 || 1).
        Assert.True(Shown("off && off || knk_option"));

        // Comparison binds tighter than &&.
        Assert.True(Shown("algorithm == 6 && knk_option == 1"));

        // Unary ! binds tightest.
        Assert.True(Hidden("!knk_option && knk_option"));
    }
}
