using System.IO;
using OpenLogViewer.Core;
using OpenLogViewer.Tests;

namespace OpenLogViewer.App.Tests;

/// <summary>
/// Choosing when a log starts, what it is called and where it goes.
///
/// Recording used to be welded to the session: it began at connect, ended at
/// disconnect, and took a name nobody picked. That is the wrong unit of work —
/// the interesting part of a session is a pull, or a lap, or the two minutes
/// after a change, and none of those begin when the cable goes in.
///
/// Connecting now watches; recording is asked for. The risk that carries is the
/// one worth testing hardest — somebody believing a file is being written when
/// none is, or meaning to capture a run and finding nothing afterwards — so what
/// the application says about its own state is asserted everywhere it says it.
/// </summary>
public class RecordingWorkflowTests : IDisposable
{

    /// <summary>Answers the confirmation before anything reaches a controller.</summary>
    private FakeWriteConfirmation Confirmation { get; } = new();
    private readonly List<string> _temp = [];
    private MainViewModel? _vm;

    public void Dispose()
    {
        _vm?.Disconnect();

        foreach (string path in _temp)
        {
            try { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); }
            catch (IOException) { }
        }
    }

    private string _folder = "";

    /// <param name="recordOnConnect">
    /// Null leaves the preference untouched, which is the only way to test what
    /// it actually defaults to — setting it either way first would assert the
    /// setter rather than the default.
    /// </param>
    private MainViewModel NewViewModel(bool? recordOnConnect = false)
    {
        _folder = Path.Combine(Path.GetTempPath(), $"olv-rec-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_folder);
        _temp.Add(_folder);

        // Devices remembered from a previous connection live in SerialPortNames,
        // which is static and therefore shared with every other test in the run.
        // A test that connects a fake ELM327 leaves it remembered, and a test
        // that asks what a fresh install looks like would then see it — so each
        // starts from the blank slate it is describing rather than from whatever
        // ran before it.
        SerialPortNames.Forget();

        var settings = new SettingsStore(Path.Combine(_folder, "settings.json"));
        settings.SetDataFolder(Path.Combine(_folder, "workspace"));
        if (recordOnConnect is { } wanted) settings.SetRecordOnConnect(wanted);

        _vm = new MainViewModel(
            new PresetStore(Path.Combine(_folder, "presets.json")),
            new FilterStore(Path.Combine(_folder, "filters.json")),
            settings,
            new MathChannelStore(Path.Combine(_folder, "math.json")),
            confirmation: Confirmation);

        return _vm;
    }

    private static FakeElm Car()
    {
        var car = new FakeElm();

        car.Answers[0x00] = [0b1001_1000, 0b0011_1010, 0b1000_0000, 0b0000_0001];
        car.Answers[0x01] = [0x00, 0x07, 0x65, 0x04];
        car.Answers[0x05] = [0x5A];
        car.Answers[0x0B] = [0x64];
        car.Answers[0x0C] = [0x1A, 0xF8];
        car.Answers[0x0D] = [0x40];
        car.Answers[0x11] = [0x33];

        return car;
    }

    private static void UntilRows(MainViewModel vm, int rows)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            if (File.Exists(vm.RecordingPath) && Lines(vm.RecordingPath) >= rows) return;
            Thread.Sleep(10);
        }
    }

    private static int Lines(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream);

        return reader.ReadToEnd().Split('\n', StringSplitOptions.RemoveEmptyEntries).Length;
    }

    // ----- getting back to the last ECU ---------------------------------------

    /// <summary>
    /// A device remembered before use times were kept still offers the shortcut.
    ///
    /// Caught by looking at the screen rather than by a test: every profile that
    /// predates the timestamps has signatures and no times, so requiring a time
    /// hid the shortcut from precisely the people who had been using this
    /// longest. Worse, it would have come back on its own after the next
    /// connection — which reads as intermittent rather than broken, and nobody
    /// reports intermittent.
    /// </summary>
    [Fact]
    public void ADeviceRememberedBeforeTimesWereKeptStillCounts()
    {
        string path = Path.Combine(Path.GetTempPath(), $"olv-old-{Guid.NewGuid():N}.json");

        File.WriteAllText(path, """
            {"version":1,"knownEcus":{"USB\\VID_2341":"Speeduino 2025.01.7"}}
            """);

        try
        {
            var settings = new SettingsStore(path);

            Assert.NotEmpty(settings.KnownEcus);
            Assert.Empty(settings.EcuLastUsed);

            // The signature is what the label is built from, and it survives
            // being shortened to something that fits on a button.
            Assert.Equal("Speeduino 2025.01.7", settings.KnownEcus[@"USB\VID_2341"]);
        }
        finally
        {
            try { File.Delete(path); } catch (IOException) { }
        }
    }

    /// <summary>
    /// With nothing ever connected there is no shortcut and no label pretending
    /// there is one — a new install shows the plain Connect button.
    /// </summary>
    [Fact]
    public void AFreshInstallOffersNoShortcut()
    {
        MainViewModel vm = NewViewModel();

        Assert.Equal("Connect", vm.ReconnectLabel);
    }

    /// <summary>Forgetting reports what went, and says what it did not touch.</summary>
    [Fact]
    public void ForgettingSaysWhatItDidAndWhatItLeftAlone()
    {
        MainViewModel vm = NewViewModel();

        Assert.Contains("No devices", vm.ForgetKnownEcus(), StringComparison.OrdinalIgnoreCase);
    }

    // ----- the default ---------------------------------------------------------

    /// <summary>
    /// Out of the box, connecting watches and writes nothing.
    ///
    /// The preference is left untouched here rather than set to false, so this
    /// asserts the default itself and not the setter.
    /// </summary>
    [Fact]
    public void ConnectingDoesNotRecordByDefault()
    {
        MainViewModel vm = NewViewModel(recordOnConnect: null);

        vm.ConnectObd2(Car(), "COM3");

        Assert.True(vm.IsLive);
        Assert.False(vm.IsRecording);
        Assert.Contains("nothing is being written", vm.Hint, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Turned on, it behaves the way a session always used to.</summary>
    [Fact]
    public void TurnedOnItRecordsFromTheMomentItConnects()
    {
        MainViewModel vm = NewViewModel(recordOnConnect: true);

        vm.ConnectObd2(Car(), "COM3");

        Assert.True(vm.IsRecording);
        Assert.NotEqual("", vm.RecordingPath);
        Assert.Contains("Recording to", vm.Hint, StringComparison.Ordinal);
    }

    /// <summary>
    /// With the preference off, nothing is written — and the application says so
    /// rather than staying quiet, because silence reads as "not got round to it".
    /// </summary>
    [Fact]
    public void WithThePreferenceOffNothingIsWrittenAndItSaysSo()
    {
        MainViewModel vm = NewViewModel(recordOnConnect: false);

        vm.ConnectObd2(Car(), "COM3");

        Assert.True(vm.IsLive);
        Assert.False(vm.IsRecording);
        Assert.Contains("nothing is being written", vm.Hint, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Turning it on is a decision somebody makes once. Asserted in the direction
    /// away from the default, so a setter that quietly did nothing would fail.
    /// </summary>
    [Fact]
    public void ThePreferenceSurvivesARestart()
    {
        MainViewModel vm = NewViewModel(recordOnConnect: null);
        vm.RecordOnConnect = true;

        var reopened = new SettingsStore(Path.Combine(_folder, "settings.json"));

        Assert.True(reopened.RecordOnConnect);
    }

    /// <summary>
    /// A settings file written before this was a choice takes the new default
    /// rather than the behaviour it was written under.
    ///
    /// This does change what an existing install does, and that is the intent
    /// rather than an oversight — the alternative is two populations of users
    /// with different defaults and no way to tell which one you are in.
    /// </summary>
    [Fact]
    public void AnOlderSettingsFileTakesTheNewDefault()
    {
        string path = Path.Combine(
            Path.GetTempPath(), $"olv-old-{Guid.NewGuid():N}.json");

        File.WriteAllText(path, """{"Version":1,"LiveRate":25}""");

        try
        {
            Assert.False(new SettingsStore(path).RecordOnConnect);
        }
        finally
        {
            try { File.Delete(path); } catch (IOException) { }
        }
    }

    // ----- driving it by hand --------------------------------------------------

    [Fact]
    public void ARecordingCanBeStartedAndStoppedWithoutEndingTheSession()
    {
        MainViewModel vm = NewViewModel(recordOnConnect: false);
        vm.ConnectObd2(Car(), "COM3");

        string path = Path.Combine(_folder, "a-pull.csv");

        Assert.Contains("Recording to", vm.StartRecording(path), StringComparison.Ordinal);
        Assert.True(vm.IsRecording);

        UntilRows(vm, 3);

        string outcome = vm.StopRecording();

        Assert.False(vm.IsRecording);
        Assert.True(vm.IsLive, "stopping a recording must not end the session");
        Assert.Contains("still live", outcome, StringComparison.OrdinalIgnoreCase);
        Assert.True(File.Exists(path));
    }

    /// <summary>The name is the caller's, not a timestamp nobody chose.</summary>
    [Fact]
    public void TheFileIsCalledWhatWasAskedFor()
    {
        MainViewModel vm = NewViewModel(recordOnConnect: false);
        vm.ConnectObd2(Car(), "COM3");

        string path = Path.Combine(_folder, "third-gear-pull.csv");
        vm.StartRecording(path);
        UntilRows(vm, 3);
        vm.StopRecording();

        Assert.Equal("third-gear-pull.csv", Path.GetFileName(vm.RecordingPath));
        Assert.True(File.Exists(path));
    }

    /// <summary>
    /// The suggestion names the ECU and the moment. A dialog opening on an empty
    /// name is how recordings end up called "log", "log2" and "log (final)".
    /// </summary>
    [Fact]
    public void TheSuggestedNameSaysWhatAndWhen()
    {
        MainViewModel vm = NewViewModel(recordOnConnect: false);
        vm.ConnectObd2(Car(), "COM3");

        string suggested = Path.GetFileName(vm.SuggestedRecordingPath());

        Assert.EndsWith(".csv", suggested, StringComparison.Ordinal);
        Assert.Contains(DateTime.Now.ToString("yyyy-MM-dd"), suggested, StringComparison.Ordinal);

        // Whatever the adapter called itself, reduced to something a file system
        // will take — an ELM327 reports "ELM327 v1.5", and the space and the dot
        // both have to survive being put in a name.
        Assert.DoesNotContain(' ', suggested);
        Assert.Equal(-1, suggested.IndexOfAny(Path.GetInvalidFileNameChars()));
    }

    /// <summary>
    /// Where the last one went is where the next one is offered, because the
    /// workspace default is a good first answer and a poor second one.
    /// </summary>
    [Fact]
    public void TheNextRecordingIsOfferedTheLastFolder()
    {
        MainViewModel vm = NewViewModel(recordOnConnect: false);
        vm.ConnectObd2(Car(), "COM3");

        string chosen = Path.Combine(_folder, "elsewhere");
        Directory.CreateDirectory(chosen);

        vm.StartRecording(Path.Combine(chosen, "run-1.csv"));
        UntilRows(vm, 3);
        vm.StopRecording();

        Assert.Equal(chosen, Path.GetDirectoryName(vm.SuggestedRecordingPath()));
    }

    [Fact]
    public void TwoRunsInOneSessionAreTwoFiles()
    {
        MainViewModel vm = NewViewModel(recordOnConnect: false);
        vm.ConnectObd2(Car(), "COM3");

        string first = Path.Combine(_folder, "run-1.csv");
        string second = Path.Combine(_folder, "run-2.csv");

        vm.StartRecording(first);
        UntilRows(vm, 3);
        vm.StopRecording();

        vm.StartRecording(second);
        UntilRows(vm, 3);
        vm.StopRecording();

        Assert.True(File.Exists(first));
        Assert.True(File.Exists(second));
    }

    // ----- what the interface says --------------------------------------------

    /// <summary>
    /// One button, and its label is its state. Two buttons would mean one of them
    /// is always dead.
    /// </summary>
    [Fact]
    public void TheButtonSaysWhatItWillDo()
    {
        MainViewModel vm = NewViewModel(recordOnConnect: false);
        vm.ConnectObd2(Car(), "COM3");

        Assert.Contains("Record", vm.RecordLabel, StringComparison.Ordinal);
        Assert.DoesNotContain("Stop", vm.RecordLabel, StringComparison.Ordinal);

        vm.StartRecording(Path.Combine(_folder, "run.csv"));

        Assert.Contains("Stop", vm.RecordLabel, StringComparison.Ordinal);
    }

    /// <summary>
    /// The one misreading that matters is a live session mistaken for a recording
    /// one, so the status line has to state the quiet case out loud.
    /// </summary>
    [Fact]
    public void TheStatusLineSaysWhenNothingIsBeingWritten()
    {
        MainViewModel vm = NewViewModel(recordOnConnect: false);
        vm.ConnectObd2(Car(), "COM3");

        DateTime deadline = DateTime.UtcNow.AddSeconds(5);
        while (vm.LiveStatus.Length == 0 && DateTime.UtcNow < deadline)
        {
            vm.RefreshLive();
            Thread.Sleep(10);
        }

        Assert.Contains("not recording", vm.LiveStatus, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("not recording", vm.LiveDetail, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void RecordingIsNotOfferedWithNothingConnected()
    {
        MainViewModel vm = NewViewModel();

        Assert.False(vm.CanRecord);
        Assert.False(vm.IsRecording);
        Assert.Equal("Not connected.", vm.StartRecording(Path.Combine(_folder, "x.csv")));
        Assert.Equal("Not connected.", vm.StopRecording());
    }

    [Fact]
    public void StoppingWhenNothingIsRecordingSaysSoRatherThanPretending()
    {
        MainViewModel vm = NewViewModel(recordOnConnect: false);
        vm.ConnectObd2(Car(), "COM3");

        Assert.Contains("Nothing was being recorded", vm.StopRecording(), StringComparison.Ordinal);
    }

    /// <summary>
    /// A path that cannot be written is reported rather than thrown. A folder that
    /// has gone — an unplugged drive, a share that is not there — should cost the
    /// recording and not the session.
    /// </summary>
    [Fact]
    public void AnUnwritablePathIsReportedAndLeavesTheSessionAlone()
    {
        MainViewModel vm = NewViewModel(recordOnConnect: false);
        vm.ConnectObd2(Car(), "COM3");

        string outcome = vm.StartRecording(Path.Combine(_folder, "no\0such", "run.csv"));

        Assert.Contains("Could not start recording", outcome, StringComparison.Ordinal);
        Assert.False(vm.IsRecording);
        Assert.True(vm.IsLive, "a bad path must not end the session");
    }
}
