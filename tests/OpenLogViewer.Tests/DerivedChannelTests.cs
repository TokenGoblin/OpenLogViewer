using OpenLogViewer.Core;
using Xunit;

namespace OpenLogViewer.Tests;

/// <summary>
/// Values a firmware defines in terms of other values, which its dialogs are
/// then written against.
///
/// Without these, rusEFI's throttle pages cannot be judged at all: every one of
/// their conditions is a name defined this way and nothing else.
/// </summary>
public class DerivedChannelTests
{
    private const string Ini = """
        [OutputChannels]
           ochBlockSize = 128
           rpm            = scalar, U16,  0, "rpm", 1, 0
           tps1_1AdcChannel = scalar, U08, 2, "", 1, 0

           isTps1Primary = { tps1_1AdcChannel != 0 }
           isEtb1Enabled = { etb1 == 1 }
           isEtb2Enabled = { etb2 == 1 }
           isEtbEnabled  = { isEtb1Enabled || isEtb2Enabled }
           blank         = {  }
        """;

    private static IReadOnlyDictionary<string, string> Read() => DerivedChannels.Read(Ini);

    private static Func<string, double> Resolver(params (string Name, double Value)[] known)
    {
        var values = known.ToDictionary(k => k.Name, k => k.Value, StringComparer.OrdinalIgnoreCase);

        return DerivedChannels.Resolving(
            Read(), n => values.TryGetValue(n, out double v) ? v : double.NaN);
    }

    // ----- reading them -----------------------------------------------------

    [Fact]
    public void OnlyTheBracedDefinitionsAreTakenAsDerived()
    {
        IReadOnlyDictionary<string, string> derived = Read();

        // The ordinary fields have an offset and are read from the ECU.
        Assert.DoesNotContain("rpm", derived.Keys);
        Assert.DoesNotContain("tps1_1AdcChannel", derived.Keys);

        Assert.Contains("isTps1Primary", derived.Keys);
        Assert.Equal("tps1_1AdcChannel != 0", derived["isTps1Primary"]);
    }

    [Fact]
    public void AnEmptyDefinitionIsNotOne() => Assert.DoesNotContain("blank", Read().Keys);

    [Fact]
    public void NamesAreMatchedWithoutRegardToCase() =>
        Assert.True(Read().ContainsKey("ISETBENABLED"));

    // ----- resolving them ---------------------------------------------------

    [Fact]
    public void ADerivedValueIsWorkedOutFromTheOnesBehindIt()
    {
        Func<string, double> resolve = Resolver(("tps1_1AdcChannel", 3));

        Assert.Equal(1, resolve("isTps1Primary"));
        Assert.Equal(0, Resolver(("tps1_1AdcChannel", 0))("isTps1Primary"));
    }

    [Fact]
    public void OneMayBeDefinedInTermsOfAnother()
    {
        // isEtbEnabled is isEtb1Enabled or isEtb2Enabled, and neither of those
        // is a real field either.
        Assert.Equal(1, Resolver(("etb1", 1), ("etb2", 0))("isEtbEnabled"));
        Assert.Equal(1, Resolver(("etb1", 0), ("etb2", 1))("isEtbEnabled"));
        Assert.Equal(0, Resolver(("etb1", 0), ("etb2", 0))("isEtbEnabled"));
    }

    [Fact]
    public void ARealFieldWinsOverADerivedNameOfTheSameName()
    {
        // A definition declaring both means the one the ECU actually sends.
        Func<string, double> resolve = Resolver(("isTps1Primary", 42), ("tps1_1AdcChannel", 0));

        Assert.Equal(42, resolve("isTps1Primary"));
    }

    [Fact]
    public void SomethingNeitherRealNorDerivedIsStillUnknown() =>
        Assert.True(double.IsNaN(Resolver()("noSuchThing")));

    [Fact]
    public void ADerivedValueRestingOnSomethingUnknownIsUnknown()
    {
        // etb1 is not supplied, so isEtb1Enabled cannot be judged — and neither
        // can the one built on it, since the other half is false.
        Assert.True(double.IsNaN(Resolver(("etb2", 0))("isEtbEnabled")));
    }

    [Fact]
    public void KnowingOneHalfStillSettlesIt()
    {
        // etb2 on makes isEtbEnabled true whatever etb1 turns out to be.
        Assert.Equal(1, Resolver(("etb2", 1))("isEtbEnabled"));
    }

    [Fact]
    public void TwoThatReferToEachOtherDoNotHangTheProgram()
    {
        // A definition with a mistake in it should give an answer of "cannot
        // say", not run until the stack is gone.
        const string circular = """
            [OutputChannels]
               a = { b }
               b = { a }
            """;

        Func<string, double> resolve = DerivedChannels.Resolving(
            DerivedChannels.Read(circular), _ => double.NaN);

        Assert.True(double.IsNaN(resolve("a")));
    }

    [Fact]
    public void OneReachedTwiceOnTheWayToAnAnswerIsStillResolved()
    {
        // The guard is against a cycle, not against a value being used more
        // than once — which is the common case, not the broken one.
        const string shared = """
            [OutputChannels]
               base  = { x == 1 }
               left  = { base }
               right = { base }
               both  = { left && right }
            """;

        Func<string, double> resolve = DerivedChannels.Resolving(
            DerivedChannels.Read(shared), n => n == "x" ? 1 : double.NaN);

        Assert.Equal(1, resolve("both"));
    }

    [Fact]
    public void AFirmwareWithNoneOfTheseCostsNothing()
    {
        // The same lookup back, not a wrapper around it.
        Func<string, double> original = _ => 1;

        Assert.Same(original, DerivedChannels.Resolving(
            new Dictionary<string, string>(), original));
    }

    // ----- what it does for a real condition --------------------------------

    [Fact]
    public void AConditionWrittenAgainstADerivedNameCanNowBeJudged()
    {
        Func<string, double> resolve = Resolver(("etb1", 1), ("etb2", 0));

        Assert.Equal(
            ConditionVerdict.Shown,
            DialogCondition.Evaluate("isEtbEnabled && isTps1Primary", Resolver(
                ("etb1", 1), ("etb2", 0), ("tps1_1AdcChannel", 3))));

        Assert.Equal(ConditionVerdict.Shown, DialogCondition.Evaluate("isEtbEnabled", resolve));
    }
}
