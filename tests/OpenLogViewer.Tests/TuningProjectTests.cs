using OpenLogViewer.Core;
using Xunit;

namespace OpenLogViewer.Tests;

/// <summary>
/// The record of what is wrong with a tune and what has been done about it.
///
/// The analysis is the easy half. A log says the mixture is lean above 150 kPa;
/// what it cannot say is that this was known three weeks ago, four per cent was
/// added to the top of the VE table, and it got better but not right. That half
/// lives in somebody's head and is gone by the next session — and it is exactly
/// the half an assistant needs to be useful rather than start over each time.
/// </summary>
public class TuningProjectTests : IDisposable
{
    private readonly List<string> _temp = [];

    public void Dispose()
    {
        foreach (string path in _temp)
        {
            try { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); }
            catch (IOException) { }
        }
    }

    private TuningProjectStore Store()
    {
        string root = Path.Combine(Path.GetTempPath(), $"olv-projects-{Guid.NewGuid():N}");
        _temp.Add(root);

        return new TuningProjectStore(root);
    }

    private static TuningProject Project(string vehicle = "The E28") =>
        new() { Vehicle = vehicle, Engine = "M30B35", Signature = "MS3 Format 0592.12P" };

    // ----- keeping it ---------------------------------------------------------

    [Fact]
    public void AProjectSurvivesBeingWrittenAndReadBack()
    {
        TuningProjectStore store = Store();

        TuningProject project = Project()
            .With(new TuningFix { Id = "lean-under-load", Title = "Lean above 150 kPa" });

        store.Write(project);

        TuningProject? back = store.Read("The E28");

        Assert.NotNull(back);
        Assert.Equal("M30B35", back!.Engine);
        Assert.Equal("Lean above 150 kPa", back.Fix("lean-under-load")?.Title);
    }

    [Fact]
    public void AVehicleWithNoProjectReadsAsNothingRatherThanThrowing()
    {
        Assert.Null(Store().Read("A car nobody has"));
    }

    [Fact]
    public void ANameWithASlashInItDoesNotBecomeAPath()
    {
        // "5VZ-FE / 4Runner" is a perfectly reasonable thing to call a project
        // and would otherwise put the file two folders away.
        TuningProjectStore store = Store();

        store.Write(Project("4Runner / spare") with { Vehicle = "4Runner / spare" });

        Assert.Contains("4Runner - spare", store.Vehicles());
    }

    [Fact]
    public void EveryVehicleWithAProjectIsListed()
    {
        TuningProjectStore store = Store();

        store.Write(Project("The E28"));
        store.Write(Project("The 4Runner"));

        Assert.Equal(["The 4Runner", "The E28"], store.Vehicles());
    }

    // ----- fixes --------------------------------------------------------------

    [Fact]
    public void ChangingAFixReplacesItRatherThanLeavingTwo()
    {
        TuningProject project = Project()
            .With(new TuningFix { Id = "lean", Title = "Lean above 150 kPa" });

        project = project.With(project.Fix("lean")! with
        {
            State = FixState.Applied,
            Change = "VE +4% above 150 kPa",
        });

        Assert.Single(project.Fixes);
        Assert.Equal(FixState.Applied, project.Fix("lean")!.State);
        Assert.Equal("VE +4% above 150 kPa", project.Fix("lean")!.Change);
    }

    [Fact]
    public void AnIdIsMadeOfWordsSoItCanBeQuotedFromMemory()
    {
        // These are referred to across sessions and read aloud in notes.
        // "lean-under-load" survives that and "fix-7" does not.
        TuningProject project = Project();

        Assert.Equal("lean-under-load", project.NewId("Lean under load"));
    }

    [Fact]
    public void AndIsNotHandedOutTwice()
    {
        TuningProject project = Project()
            .With(new TuningFix { Id = "lean-under-load", Title = "First" });

        Assert.Equal("lean-under-load-2", project.NewId("Lean under load"));
    }

    [Fact]
    public void OpenMeansSeenOrTriedButNotYetSettled()
    {
        TuningProject project = Project()
            .With(new TuningFix { Id = "a", Title = "A", State = FixState.Open })
            .With(new TuningFix { Id = "b", Title = "B", State = FixState.Applied })
            .With(new TuningFix { Id = "c", Title = "C", State = FixState.Verified })
            .With(new TuningFix { Id = "d", Title = "D", State = FixState.Abandoned });

        Assert.Equal(["a", "b"], project.Open.Select(f => f.Id).Order());
    }

    // ----- recording a sitting ------------------------------------------------

    /// <summary>
    /// A log shaped like a real pull: idle, then load, then back.
    ///
    /// Everything moves, deliberately. A log whose every channel holds one value
    /// is flagged — correctly — as a rig full of dead sensors, so a fixture built
    /// that way tests the wrong thing: it can never produce a clean run to
    /// compare a bad one against.
    /// </summary>
    private static LogDocument Log(double afr, double target = 13.0, int samples = 400)
    {
        double[] Over(Func<int, double> shape) =>
            [.. Enumerable.Range(0, samples).Select(shape)];

        // A third idling, a third climbing into load, a third back down.
        double Load(int i) => i < samples / 3 ? 0.0 : i < 2 * samples / 3 ? 1.0 : 0.2;

        return new LogDocument
        {
            FormatName = "test",
            FilePath = "session.mlg",
            Time = new LogChannel("Time", "s", 2, Over(i => i * 0.1)),
            Channels =
            [
                new LogChannel("RPM", "rpm", 0, Over(i => 850 + (Load(i) * 4200))),
                new LogChannel("MAP", "kPa", 1, Over(i => 32 + (Load(i) * 148))),
                new LogChannel("TPS", "%", 1, Over(i => Load(i) * 92)),

                // Only under load does the mixture take the value being tested;
                // at idle it sits on target, which is what a real log does.
                new LogChannel("AFR", "AFR", 2, Over(i => Load(i) > 0.5 ? afr : 14.6)),
                new LogChannel("AFR 1 Target", "AFR", 2, Over(i => Load(i) > 0.5 ? target : 14.6)),

                new LogChannel("CLT", "°F", 1, Over(i => 188 + (i / (double)samples * 6))),
                new LogChannel("MAT", "°F", 1, Over(i => 95 + (Load(i) * 14))),
                new LogChannel("Batt V", "v", 2, Over(i => 13.8 + (Load(i) * 0.4))),
            ],
        };
    }

    [Fact]
    public void ASittingKeepsEveryFindingIncludingTheGoodOnes()
    {
        // A run where nothing was wrong is evidence too: it is what a fix gets
        // verified against, and a project recording only bad days cannot show
        // anything getting better.
        ProjectSession sitting = TuningProjectRecorder.Sitting(Log(afr: 13.0), "MS3");

        Assert.NotEmpty(sitting.Findings);
        Assert.Equal("session.mlg", sitting.Log);
        Assert.Equal(400, sitting.Samples);
        Assert.Contains(sitting.Findings, f => f.Level == "Good");
    }

    [Fact]
    public void AWarningRaisesAFixTheFirstTimeItIsSeen()
    {
        TuningProject project = TuningProjectRecorder.Record(
            Project(), TuningProjectRecorder.Sitting(Log(afr: 16.5), "MS3"));

        Assert.NotEmpty(project.Open);
        Assert.All(project.Open, f => Assert.NotEmpty(f.Evidence));
    }

    [Fact]
    public void TheSameFaultSeenAgainIsNotRaisedTwice()
    {
        // The wording carries the numbers, and those move every run while the
        // fault stays the same. Matching on them would open a fresh item for the
        // same problem every time, which is how a tracker becomes noise.
        TuningProject project = Project();

        project = TuningProjectRecorder.Record(project, TuningProjectRecorder.Sitting(Log(afr: 16.5)));
        int after = project.Open.Count();

        project = TuningProjectRecorder.Record(project, TuningProjectRecorder.Sitting(Log(afr: 16.9)));

        Assert.Equal(after, project.Open.Count());
    }

    [Fact]
    public void ButItIsNotedAgainstTheFixThatIsAlreadyOpen()
    {
        // Which on an applied fix is the evidence the change did not work.
        TuningProject project = TuningProjectRecorder.Record(
            Project(), TuningProjectRecorder.Sitting(Log(afr: 16.5)));

        TuningFix first = project.Open.First();
        project = project.With(first with { State = FixState.Applied, Change = "VE +4%" });

        project = TuningProjectRecorder.Record(project, TuningProjectRecorder.Sitting(Log(afr: 16.4)));

        TuningFix now = project.Fix(first.Id)!;

        Assert.True(now.Evidence.Count > first.Evidence.Count);
        Assert.Equal(FixState.Applied, now.State);
    }

    [Fact]
    public void AFixSettledIsNoLongerAddedTo()
    {
        TuningProject project = TuningProjectRecorder.Record(
            Project(), TuningProjectRecorder.Sitting(Log(afr: 16.5)));

        TuningFix first = project.Open.First();
        project = project.With(first with { State = FixState.Abandoned, Settled = DateTimeOffset.Now });

        project = TuningProjectRecorder.Record(project, TuningProjectRecorder.Sitting(Log(afr: 16.5)));

        // A new one is raised instead, because the old one was closed
        // deliberately and reopening it behind somebody's back would be worse.
        Assert.NotEmpty(project.Open);
        Assert.DoesNotContain(project.Open, f => f.Id == first.Id);
    }

    // ----- how it reads -------------------------------------------------------

    [Fact]
    public void TheBriefLeadsWithWhatIsStillWrong()
    {
        // A reader who stops after the first screen should still have the part
        // that changes what they do next.
        TuningProject project = Project()
            .With(new TuningFix
            {
                Id = "lean-under-load",
                Title = "Lean above 150 kPa",
                State = FixState.Applied,
                Change = "VE +4% above 150 kPa",
                Evidence = ["2026-08-01: 46% short", "2026-08-20: 12% short, better"],
            })
            .With(new TuningFix
            {
                Id = "iat-sensor", Title = "IAT never moves", State = FixState.Verified,
                Settled = DateTimeOffset.Now,
            });

        string brief = TuningProjectStore.Brief(project);

        Assert.Contains("# The E28", brief, StringComparison.Ordinal);
        Assert.Contains("## Still open", brief, StringComparison.Ordinal);

        // The open one, with its history, before the settled one.
        Assert.True(
            brief.IndexOf("lean-under-load", StringComparison.Ordinal)
            < brief.IndexOf("iat-sensor", StringComparison.Ordinal));

        Assert.Contains("VE +4% above 150 kPa", brief, StringComparison.Ordinal);
        Assert.Contains("12% short, better", brief, StringComparison.Ordinal);
    }

    [Fact]
    public void ASittingWhereNothingWasFlaggedSaysSoInOneLine()
    {
        TuningProject project = Project().With(TuningProjectRecorder.Sitting(Log(afr: 13.0)));

        string brief = TuningProjectStore.Brief(project);

        Assert.Contains("Nothing flagged.", brief, StringComparison.Ordinal);
    }

    [Fact]
    public void AndAProjectWithNothingOutstandingSaysThatToo()
    {
        Assert.Contains("Nothing outstanding.", TuningProjectStore.Brief(Project()),
                        StringComparison.Ordinal);
    }

    [Fact]
    public void OnlyTheRecentSittingsAreRenderedAndTheRestAreCounted()
    {
        // Forty sittings in front of a model buries the three fixes that are
        // actually open. The whole history stays in the file.
        TuningProject project = Project();

        for (int i = 0; i < 12; i++)
            project = project.With(TuningProjectRecorder.Sitting(Log(afr: 13.0)));

        string brief = TuningProjectStore.Brief(project, sessions: 3);

        Assert.Contains("9 earlier sittings are in the file.", brief, StringComparison.Ordinal);
    }
}
