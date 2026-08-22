using OpenLogViewer.App;
using OpenLogViewer.Core;

namespace OpenLogViewer.App.Tests;

/// <summary>
/// Where the columns of the calculator tables actually land.
///
/// These are monospaced blocks built from fixed field widths, and each one writes
/// its headings out separately from its rows — two sets of numbers that have to
/// agree and nothing that makes them. They drift the moment a unit moves into a
/// cell: a row of <c>{value,8}{" cc"}</c> is eleven wide against a heading of
/// nine, and the heading then sits two characters left of every number it names.
///
/// That is invisible to every other kind of test. The values are right, the
/// warnings are right, the plan is right, and the block is subtly wrong in a way
/// only a reader notices — so what is measured here is position rather than
/// content: the right edge of a heading against the right edge of the column
/// underneath it.
/// </summary>
public class CalculatorTableTests
{
    /// <summary>A 2.0 four that produces a full table on every page here.</summary>
    private static ManifoldSpec Engine(
        Induction induction = Induction.NaturallyAspirated,
        ManifoldGoal goal = ManifoldGoal.Balanced) => new()
    {
        Litres = 2.0,
        Cylinders = 4,
        Induction = induction,
        Goal = goal,
        PeakTorqueRpm = 4_500,
        PeakPowerRpm = 7_000,
        VolumetricEfficiency = 95,
    };

    private static string[] Lines(string table) =>
        table.Split('\n').Select(l => l.TrimEnd('\r')).ToArray();

    /// <summary>Where a heading ends, counted from the start of the line.</summary>
    private static int EndOf(string line, string label)
    {
        int at = line.IndexOf(label, StringComparison.Ordinal);
        Assert.True(at >= 0, $"'{label}' is not in \"{line}\"");

        return at + label.Length;
    }

    /// <summary>Where the nth occurrence of a cell's tail ends.</summary>
    private static int EndOfNth(string line, string tail, int nth)
    {
        int at = -1;

        for (int found = 0; found <= nth; found++)
        {
            at = line.IndexOf(tail, at + 1, StringComparison.Ordinal);
            Assert.True(at >= 0, $"'{tail}' does not occur {nth + 1} times in \"{line}\"");
        }

        return at + tail.Length;
    }

    // ----- plenum -------------------------------------------------------------

    [Fact]
    public void ThePlenumHeadingsSitOverTheirOwnNumbers()
    {
        // The one that was wrong: the "cc" belongs to the cell, so a heading
        // measured against the number alone lands short by the width of a unit.
        ManifoldPlan plan = ManifoldTuning.Plan(Engine());
        string[] lines = Lines(CalculatorsWindow.PlenumTable(Engine(), plan.Intake, Induction.NaturallyAspirated));

        string header = lines[0];
        string[] rows = [.. lines.Skip(2).Where(l => l.Contains("cc", StringComparison.Ordinal))];

        Assert.NotEmpty(rows);

        foreach (string row in rows)
        {
            Assert.Equal(EndOf(header, "volume"), EndOfNth(row, " cc", 0));
            Assert.Equal(EndOf(header, "+ runners"), EndOfNth(row, " cc", 1));

            // And the last column starts where its heading does, rather than
            // being pushed right by the two before it.
            Assert.Equal(
                header.IndexOf("what it does", StringComparison.Ordinal),
                EndOfNth(row, " cc", 1) + 3);
        }
    }

    [Fact]
    public void ThePlenumRuleIsAtLeastAsWideAsTheHeading()
    {
        // A rule shorter than the block it divides reads as a column that ends
        // where it does not.
        ManifoldPlan plan = ManifoldTuning.Plan(Engine());
        string[] lines = Lines(CalculatorsWindow.PlenumTable(Engine(), plan.Intake, Induction.NaturallyAspirated));

        Assert.All(lines[1], c => Assert.Equal('-', c));
        Assert.True(lines[1].Length >= lines[0].Length,
            $"the rule is {lines[1].Length} wide under a heading of {lines[0].Length}");
    }

    // ----- the harmonics ------------------------------------------------------

    [Fact]
    public void TheOrderTableIsOneWidthThroughout()
    {
        // Two heading lines, a rule and the rows, all built from the same three
        // fields. Any of them disagreeing is a column boundary that moves partway
        // down the block.
        ManifoldPlan plan = ManifoldTuning.Plan(Engine());
        string[] lines = Lines(CalculatorsWindow.OrderTable(plan.Intake, plan.Exhaust, Induction.NaturallyAspirated));

        int width = lines[2].Length;

        Assert.All(lines[2], c => Assert.Equal('-', c));

        // The two headings and every row, up to the blank line before the legend.
        foreach (string line in lines.TakeWhile(l => l.Length > 0).Where(l => !l.StartsWith('-')))
            Assert.Equal(width, line.Length);
    }

    [Fact]
    public void TheOrderHeadingsSitOverTheirOwnColumns()
    {
        ManifoldPlan plan = ManifoldTuning.Plan(Engine());
        string[] lines = Lines(CalculatorsWindow.OrderTable(plan.Intake, plan.Exhaust, Induction.NaturallyAspirated));

        // "intake runner" and the "mm" beneath it end together, and so does every
        // length in that column — the mark is what follows, and it is one field.
        Assert.Equal(EndOf(lines[0], "intake runner"), EndOf(lines[1], "mm"));

        Assert.Equal(
            EndOf(lines[0], "exhaust primary"),
            EndOfNth(lines[1], "mm", 1));
    }

    // ----- the spray fluids ---------------------------------------------------

    [Fact]
    public void TheFluidHeadingsSitOverTheirOwnColumns()
    {
        string[] lines = Lines(CalculatorsWindow.FluidTable(600, 60, 250, 1.0));

        string header = lines[0];
        string[] rows = [.. lines.Skip(1).Where(l => l.Length > 0)];

        Assert.NotEmpty(rows);

        // Every numeric column is right-aligned to the same width in both, so the
        // four of them together are the tell: the block of fixed fields has to
        // end at the same place in a row as it does in the heading, whatever the
        // last column then does with its own padding.
        int burns = header.IndexOf("burns", StringComparison.Ordinal);

        // Three spaces separate the last fixed column from "burns", in the
        // heading and in every row alike.
        Assert.Equal(burns - 3, EndOf(header, "gal/hr"));

        foreach (string row in rows)
        {
            Assert.True(row.Length > burns,
                $"a row is {row.Length} wide where \"burns\" begins at {burns}");

            Assert.Equal("   ", row[(burns - 3)..burns]);

            // And something in the column before the gap, so the fields have not
            // simply all run short.
            Assert.NotEqual(' ', row[burns - 4]);
        }
    }
}
