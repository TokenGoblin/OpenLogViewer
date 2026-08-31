using OpenLogViewer.Core;
using Xunit;

namespace OpenLogViewer.Tests;

/// <summary>
/// Keeping track of which tune is which.
///
/// The thing this replaces is a folder holding claude01.msq through claude7.msq,
/// one of them spelled claud02, beside a "Before Fuel Cleanup.msq" and a
/// "CurrentTune.msq" that cannot say whether it is what the ECU is running.
/// Every one of those names is somebody trying to record <em>why</em> in the
/// only field a filesystem offers.
/// </summary>
public class TuneVersionTests : IDisposable
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

    private const string Ini = """
        [Constants]
        page = 1
        nPages = 1
        pageSize = 16
        pageIdentifier = "\x01"
        pageReadCommand = "r%2o%2c"
        pageChunkWrite  = "w%2o%2c%v"
        burnCommand     = "b%2i"
           crankingRPM = scalar, U16, 0, "rpm", 1, 0, 0, 10000, 0
           revLimit    = scalar, U16, 2, "rpm", 1, 0, 0, 10000, 0
           veTable     = array,  U08, 4, [4], "%", 1, 0, 0, 255, 0
        """;

    private static TuneLayout Layout() => TuneLayoutReader.Read(Ini);

    private string Folder()
    {
        string path = Path.Combine(Path.GetTempPath(), $"olv-versions-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        _temp.Add(path);

        return path;
    }

    private static EcuTune Tune(int cranking = 300, int rev = 6500, int ve = 50)
    {
        TuneLayout layout = Layout();
        var tune = EcuTune.FromPages(layout, new byte[16]);

        tune.PokeInto(tune.Pages, tune.Constant("crankingRPM")!, 0, cranking);
        tune.PokeInto(tune.Pages, tune.Constant("revLimit")!, 0, rev);

        for (int i = 0; i < 4; i++) tune.PokeInto(tune.Pages, tune.Constant("veTable")!, i, ve);

        return tune;
    }

    private static TuningProject Project() => new() { Vehicle = "The E28" };

    // ----- identity -----------------------------------------------------------

    [Fact]
    public void TheSameTuneRecordedTwiceIsOneVersion()
    {
        // Reading the tune at the start of two sessions that changed nothing,
        // or pressing burn twice, must not branch history. What is worth
        // recording is that the controller held something different.
        string folder = Folder();

        (TuningProject project, TuneVersion first, bool wasNew) =
            TuneHistory.Capture(Project(), folder, Tune(), "firmware", "baseline");

        Assert.True(wasNew);

        (project, TuneVersion again, bool second) =
            TuneHistory.Capture(project, folder, Tune(), "firmware");

        Assert.False(second);
        Assert.Equal(first.Id, again.Id);
        Assert.Single(project.Versions);
    }

    [Fact]
    public void ADifferentTuneIsANewVersion()
    {
        string folder = Folder();

        (TuningProject project, _, _) =
            TuneHistory.Capture(Project(), folder, Tune(), "firmware", "baseline");

        (project, TuneVersion second, bool wasNew) =
            TuneHistory.Capture(project, folder, Tune(rev: 7000), "firmware", "raised the limiter");

        Assert.True(wasNew);
        Assert.Equal(2, project.Versions.Count);
        Assert.Equal("v2", second.Id);
        Assert.Equal("v1", second.Parent);
    }

    [Fact]
    public void BurningAVersionAlreadyRecordedIsNewsWithoutBeingANewVersion()
    {
        // Written and then burned is the ordinary sequence, and it is one tune.
        string folder = Folder();

        (TuningProject project, _, _) = TuneHistory.Capture(Project(), folder, Tune(), "firmware");
        Assert.False(project.Latest!.Burned);

        (project, TuneVersion burned, bool wasNew) =
            TuneHistory.Capture(project, folder, Tune(), "firmware", burned: true);

        Assert.False(wasNew);
        Assert.True(burned.Burned);
        Assert.Single(project.Versions);
    }

    // ----- what is kept -------------------------------------------------------

    [Fact]
    public void AVersionIsAnOrdinaryMsqAnyoneElseCanOpen()
    {
        // A version-control system nobody else can read is a trap. The file
        // beside the metadata is the same .msq TunerStudio takes.
        string folder = Folder();

        (_, TuneVersion version, _) =
            TuneHistory.Capture(Project(), folder, Tune(), "firmware", "baseline");

        string path = Path.Combine(folder, version.File);

        Assert.True(File.Exists(path));

        MsqFile read = MsqFile.ReadFile(path);
        Assert.Equal("firmware", read.Signature);
        Assert.Equal("6500.0", read.Value("revLimit"));
    }

    [Fact]
    public void AndReadsBackAsTheTuneItWas()
    {
        string folder = Folder();

        (TuningProject project, TuneVersion version, _) =
            TuneHistory.Capture(Project(), folder, Tune(rev: 7200), "firmware");

        EcuTune? back = TuneHistory.Read(project, folder, version.Id, Layout());

        Assert.NotNull(back);
        Assert.Equal(7200, back!.Scalar("revLimit"));
    }

    [Fact]
    public void AVersionWhoseFileHasGoneReadsAsNothingRatherThanThrowing()
    {
        string folder = Folder();

        (TuningProject project, TuneVersion version, _) =
            TuneHistory.Capture(Project(), folder, Tune(), "firmware");

        File.Delete(Path.Combine(folder, version.File));

        Assert.Null(TuneHistory.Read(project, folder, version.Id, Layout()));
    }

    // ----- what changed -------------------------------------------------------

    [Fact]
    public void TwoVersionsAreComparedBySettingRatherThanByBytes()
    {
        // Which is the only comparison that means anything to a person: two
        // tunes can differ in bits no constant declares and be the same tune.
        string folder = Folder();

        (TuningProject project, _, _) = TuneHistory.Capture(Project(), folder, Tune(), "firmware");
        (project, _, _) = TuneHistory.Capture(project, folder, Tune(rev: 7000, ve: 54), "firmware");

        VersionDifference? diff = TuneHistory.Compare(project, folder, "v1", "v2", Layout());

        Assert.NotNull(diff);
        Assert.Contains(diff!.Differences, d => d.Name == "revLimit");
        Assert.Contains(diff.Differences, d => d.Name == "veTable");
        Assert.Contains("differ", diff.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public void AndSayNothingChangedWhenNothingDid()
    {
        string folder = Folder();

        (TuningProject project, _, _) = TuneHistory.Capture(Project(), folder, Tune(), "firmware");

        VersionDifference? diff = TuneHistory.Compare(project, folder, "v1", "v1", Layout());

        Assert.NotNull(diff);
        Assert.True(diff!.IsEmpty);
        Assert.Contains("same tune", diff.Summary, StringComparison.Ordinal);
    }

    // ----- the whole point ----------------------------------------------------

    [Fact]
    public void AFixIsAnsweredByLogsRecordedOnTheTuneThatCarriedTheChange()
    {
        // The question everybody actually asks, and the reason a sitting names
        // its version. A run on the old tune that happens to look clean proves
        // nothing, and neither does one on the new tune from before it existed.
        string folder = Folder();
        TuningProject project = Project();

        (project, TuneVersion before, _) =
            TuneHistory.Capture(project, folder, Tune(), "firmware", "baseline");

        // Something is wrong, and it is being tracked.
        project = project.With(new TuningFix
        {
            Id = "mixture-under-load",
            Title = "Lean above 150 kPa",
            State = FixState.Applied,
            Change = "VE +4% above 150 kPa",
        });

        Assert.Contains("no tune version claims it",
                        TuningProjectRecorder.Verdict(project, project.Fix("mixture-under-load")!),
                        StringComparison.Ordinal);

        // The change is made, and the version says what it was for.
        (project, TuneVersion after, _) = TuneHistory.Capture(
            project, folder, Tune(ve: 54), "firmware", "VE +4% up top",
            addresses: ["mixture-under-load"], burned: true);

        Assert.Contains("nothing has been recorded on it yet",
                        TuningProjectRecorder.Verdict(project, project.Fix("mixture-under-load")!),
                        StringComparison.Ordinal);

        // A clean run, on the new tune.
        project = project.With(new ProjectSession
        {
            Log = "after.mlg",
            Version = after.Id,
            Findings = [new SessionFinding("Good", "Mixture under load", "Fuelling holds target")],
        });

        string verdict = TuningProjectRecorder.Verdict(project, project.Fix("mixture-under-load")!);

        Assert.Contains("none of them complained", verdict, StringComparison.Ordinal);
        Assert.NotEqual(before.Id, after.Id);
    }

    [Fact]
    public void AndStillSaysSoWhenTheChangeDidNotWork()
    {
        string folder = Folder();
        TuningProject project = Project();

        (project, _, _) = TuneHistory.Capture(project, folder, Tune(), "firmware");

        project = project.With(new TuningFix
        {
            Id = "mixture-under-load", Title = "Lean above 150 kPa", State = FixState.Applied,
        });

        (project, TuneVersion after, _) = TuneHistory.Capture(
            project, folder, Tune(ve: 54), "firmware", addresses: ["mixture-under-load"]);

        project = project.With(new ProjectSession
        {
            Log = "after.mlg",
            Version = after.Id,
            Findings = [new SessionFinding("Warning", "Mixture under load", "Still 12% short")],
        });

        Assert.Contains("has not settled it",
                        TuningProjectRecorder.Verdict(project, project.Fix("mixture-under-load")!),
                        StringComparison.Ordinal);
    }

    [Fact]
    public void ACleanRunOnTheOldTuneProvesNothing()
    {
        // The trap this exists to close. Without the version on the sitting,
        // any clean log after the change looks like evidence for it.
        string folder = Folder();
        TuningProject project = Project();

        (project, TuneVersion old, _) = TuneHistory.Capture(project, folder, Tune(), "firmware");

        project = project.With(new TuningFix
        {
            Id = "mixture-under-load", Title = "Lean above 150 kPa", State = FixState.Applied,
        });

        (project, _, _) = TuneHistory.Capture(
            project, folder, Tune(ve: 54), "firmware", addresses: ["mixture-under-load"]);

        // Recorded afterwards, but on the tune from before the change.
        project = project.With(new ProjectSession
        {
            Log = "old-tune.mlg",
            Version = old.Id,
            Findings = [new SessionFinding("Good", "Mixture under load", "Looks fine")],
        });

        Assert.Contains("nothing has been recorded on it yet",
                        TuningProjectRecorder.Verdict(project, project.Fix("mixture-under-load")!),
                        StringComparison.Ordinal);
    }
}
