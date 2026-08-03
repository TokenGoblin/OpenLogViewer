using System.Xml;
using OpenLogViewer.Core;
using Xunit;

namespace OpenLogViewer.Tests;

/// <summary>
/// What happens when a file was not written by a friend.
///
/// Every input here arrives from somewhere else — a tune emailed by a tuner, a
/// datalog from a forum, a firmware .ini downloaded from the internet and
/// dropped into the definitions folder this application asks people to fill.
/// None of it can be assumed well-meaning, and the failure mode that matters is
/// not a wrong reading but a process that stops existing.
/// </summary>
public class UntrustedInputTests
{
    // ----- XML -----------------------------------------------------------------

    [Fact]
    public void AnEntityBombIsRefusedRatherThanExpanded()
    {
        // Three levels expand to ten thousand characters from a few hundred
        // bytes, and each further level multiplies by ten. Ten of them is a
        // gigabyte, which is a crash rather than a slow load.
        const string bomb = """
            <?xml version="1.0"?>
            <!DOCTYPE msq [
              <!ENTITY a "aaaaaaaaaa">
              <!ENTITY b "&a;&a;&a;&a;&a;&a;&a;&a;&a;&a;">
              <!ENTITY c "&b;&b;&b;&b;&b;&b;&b;&b;&b;&b;">
              <!ENTITY d "&c;&c;&c;&c;&c;&c;&c;&c;&c;&c;">
            ]>
            <msq>&d;</msq>
            """;

        Assert.Throws<XmlException>(() => SafeXml.Parse(bomb));
    }

    [Fact]
    public void AnExternalEntityReadsNoFile()
    {
        // Measured rather than assumed, and asserted so it stays true: a
        // document naming a local file must not be able to read it.
        string canary = Path.Combine(Path.GetTempPath(), $"olv-canary-{Guid.NewGuid():N}.txt");
        File.WriteAllText(canary, "CANARY");

        try
        {
            string xxe = $"""
                <?xml version="1.0"?>
                <!DOCTYPE msq [ <!ENTITY leak SYSTEM "file:///{canary.Replace('\\', '/')}"> ]>
                <msq><page><pcVariable name="rpmhigh">&leak;</pcVariable></page></msq>
                """;

            // Either refused outright or read with nothing in it. What must not
            // happen is the file's contents arriving in the document.
            try
            {
                Assert.DoesNotContain("CANARY", SafeXml.Parse(xxe).ToString(), StringComparison.Ordinal);
            }
            catch (XmlException)
            {
            }
        }
        finally
        {
            File.Delete(canary);
        }
    }

    [Fact]
    public void AnOrdinaryTuneStillLoads()
    {
        const string tune = """
            <?xml version="1.0"?>
            <msq><page><pcVariable name="rpmhigh">9000</pcVariable></page></msq>
            """;

        Assert.Equal("9000", TuningContext.ReadPcVariables(tune)["rpmhigh"]);
    }

    [Fact]
    public void ADocumentTypeOnItsOwnIsHarmlessAndAccepted()
    {
        // Ignoring the definition rather than prohibiting it: prohibiting would
        // reject this file, which is perfectly ordinary and which some writers
        // emit.
        const string declared = """
            <?xml version="1.0"?><!DOCTYPE msq>
            <msq><page><pcVariable name="rpmhigh">9000</pcVariable></page></msq>
            """;

        Assert.Equal("9000", TuningContext.ReadPcVariables(declared)["rpmhigh"]);
    }

    // ----- expressions ---------------------------------------------------------

    [Fact]
    public void AnExpressionCannotNestDeeplyEnoughToEndTheProcess()
    {
        // This one was not theoretical. Before the depth limit, ten thousand
        // brackets exited with 0xC00000FD — a stack overflow, which takes the
        // process down where nothing can catch it: no message, no log line, no
        // way to say which file did it.
        //
        // Reachable from a firmware .ini's [OutputChannels], which are
        // downloaded and which this application invites people to install.
        string nested = new string('(', 10_000) + "rpm" + new string(')', 10_000);

        Assert.False(MathExpression.TryParse(nested, ["rpm"], out _, out string? error));
        Assert.Contains("deep", error!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ExpressionsPeopleActuallyWriteAreUnaffected()
    {
        // The deepest in any firmware INI here nests three.
        Assert.True(MathExpression.TryParse(
            "rpm ? (60000.0 / (rpm * (twoStroke == 1 ? 1 : 2))) : 0",
            ["rpm", "twoStroke"], out _, out string? error), error);
    }

    [Theory]
    [InlineData("(rpm ? 1 : 0)")]
    [InlineData("(rpm ? 1 : 0) * 2")]
    [InlineData("1 + (twoStroke == 1 ? 1 : 2)")]
    [InlineData("min(rpm ? 1 : 2, 3)")]
    public void AConditionalIsAllowedWhereverAValueIs(string expression)
    {
        // It was not. A bracket and a function argument each began parsing below
        // the conditional operator, so "rpm ? 1 : 0" parsed on its own and
        // "(rpm ? 1 : 0)" did not — reported as a missing ')', which says
        // nothing about what was wrong. Found by an audit rather than by anyone
        // hitting it, which is the only reason it is not still there.
        Assert.True(
            MathExpression.TryParse(expression, ["rpm", "twoStroke"], out _, out string? error),
            $"{expression} → {error}");
    }

    [Fact]
    public void ABracketedConditionalEvaluatesToWhatItSays()
    {
        // Parsing it is not enough; the arms have to end up the right way round.
        Assert.True(MathExpression.TryParse(
            "(rpm ? 10 : 20) + 1", ["rpm"], out MathExpression? parsed, out _));

        Assert.Equal(11, parsed!.Evaluate([1]));
        Assert.Equal(21, parsed.Evaluate([0]));
    }

    [Fact]
    public void ALongExpressionIsNotMistakenForADeepOne()
    {
        // Length is not depth: a chain of operators is parsed as a loop and
        // costs no stack at all, so the limit must not catch it.
        string chain = string.Join(" + ", Enumerable.Repeat("rpm", 5_000));

        Assert.True(MathExpression.TryParse(chain, ["rpm"], out _, out string? error), error);
    }
}
