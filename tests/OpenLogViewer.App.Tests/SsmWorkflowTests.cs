using System.IO;
using OpenLogViewer.Core;
using OpenLogViewer.Tests;

namespace OpenLogViewer.App.Tests;

/// <summary>
/// An SSM session end to end, from the parameter file to the dashboard.
///
/// The wire is covered by the core tests and was proven on a running car. What
/// is covered here is the part that has gone wrong before on another link: a
/// gauge whose column does not match a recorded channel shows a face and never a
/// number, which to whoever is looking at the screen is indistinguishable from a
/// parameter that is not being read at all.
/// </summary>
public class SsmWorkflowTests : IDisposable
{
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

    private MainViewModel NewViewModel()
    {
        _folder = Path.Combine(Path.GetTempPath(), $"olv-ssm-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_folder);
        _temp.Add(_folder);

        var settings = new SettingsStore(Path.Combine(_folder, "settings.json"));
        settings.SetDataFolder(Path.Combine(_folder, "workspace"));

        _vm = new MainViewModel(
            new PresetStore(Path.Combine(_folder, "presets.json")),
            new FilterStore(Path.Combine(_folder, "filters.json")),
            settings,
            new MathChannelStore(Path.Combine(_folder, "math.json")));

        return _vm;
    }

    /// <summary>
    /// Connecting with no file present writes the template and reads it, so a
    /// first run has something rather than nothing.
    /// </summary>
    [Fact]
    public void AFirstConnectionWritesTheTemplateAndUsesIt()
    {
        MainViewModel vm = NewViewModel();

        vm.ConnectSsm(new FakeSubaru(), "COM3");

        Assert.True(vm.IsLive);
        Assert.True(File.Exists(vm.SsmParameterPath), "the template should have been written");
        Assert.Equal(["Engine Speed", "Coolant"], vm.LiveChannelNames);
    }

    /// <summary>
    /// The bug this guards against: a gauge fed by a column no channel carries
    /// draws a face and never a number, which looks exactly like a parameter the
    /// car is not reporting.
    /// </summary>
    [Fact]
    public void EveryGaugeIsFedByAChannelThatIsActuallyBeingRead()
    {
        MainViewModel vm = NewViewModel();

        vm.ConnectSsm(new FakeSubaru(), "COM3");

        Assert.NotEmpty(vm.AllGauges);
        Assert.All(vm.AllGauges, g =>
        {
            Assert.True(g.IsConnected, $"{g.Title} is fed by nothing");
            Assert.Contains(g.Column, vm.LiveChannelNames);
        });
    }

    /// <summary>
    /// Every parameter reaches the dashboard, unlike OBD2 where a car reporting
    /// thirty would open as thirty dials. A list somebody wrote by hand is already
    /// the short list.
    /// </summary>
    [Fact]
    public void EveryParameterReachesTheDashboard()
    {
        MainViewModel vm = NewViewModel();

        vm.ConnectSsm(new FakeSubaru(), "COM3");

        Assert.Equal(vm.AllGauges.Count, vm.Dashboard.Count);
    }

    /// <summary>Each dial is drawn over the range the file declares.</summary>
    [Fact]
    public void EveryDialHasARangeFromTheFile()
    {
        MainViewModel vm = NewViewModel();

        vm.ConnectSsm(new FakeSubaru(), "COM3");

        Assert.All(vm.Dashboard, g => Assert.True(g.Spec.HasScale, $"{g.Title} has no range"));
    }

    /// <summary>
    /// Named for the address as well as the parameter. The address is what a
    /// definition file or a forum post quotes; the name is whatever the person
    /// filling in the file happened to type.
    /// </summary>
    [Fact]
    public void GaugesAreGroupedByTheAddressTheyCameFrom()
    {
        MainViewModel vm = NewViewModel();

        vm.ConnectSsm(new FakeSubaru(), "COM3");

        Assert.All(vm.AllGauges, g =>
            Assert.StartsWith("SSM · 0x", g.Spec.Category, StringComparison.Ordinal));
    }

    /// <summary>
    /// How slow it is gets said rather than left to be discovered. Somebody used
    /// to a tuning cable at 25 Hz should be told, not left to conclude the link
    /// is faulty.
    /// </summary>
    [Fact]
    public void HowSlowItIsIsSaidRatherThanLeftToBeDiscovered()
    {
        MainViewModel vm = NewViewModel();

        vm.ConnectSsm(new FakeSubaru(), "COM3");

        Assert.Contains("once a second", vm.Hint, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(SsmParameterFile.Name, vm.Hint, StringComparison.Ordinal);
    }

    /// <summary>
    /// A file the user has edited is what gets read, not the template. Verified
    /// against the addresses confirmed on a real car.
    /// </summary>
    [Fact]
    public void AnEditedFileIsWhatGetsRead()
    {
        MainViewModel vm = NewViewModel();

        Directory.CreateDirectory(Path.GetDirectoryName(vm.SsmParameterPath)!);
        File.WriteAllText(vm.SsmParameterPath, """
            {
              "version": 1,
              "parameters": [
                { "name": "Coolant", "address": "0x000008", "offset": -40, "units": "°C" }
              ]
            }
            """);

        vm.ConnectSsm(new FakeSubaru(), "COM3");

        Assert.Equal(["Coolant"], vm.LiveChannelNames);
    }

    /// <summary>
    /// The readings reach the gauges, not merely the recording.
    ///
    /// The bug this exists for: the session polled, the recording filled with
    /// correct values, and every dial stayed blank -- because the timer that
    /// pushes readings into the view is started by the code that opens a
    /// connection, and a third way in had been added that did not start it. A
    /// recording full of data was taken as proof the whole thing worked, and it
    /// only ever proved the source did.
    ///
    /// So this asserts the path the recording cannot: a value taken off the
    /// source, through a snapshot, onto the gauge.
    /// </summary>
    [Fact]
    public void ReadingsReachTheGaugesAndNotOnlyTheRecording()
    {
        MainViewModel vm = NewViewModel();
        vm.ConnectSsm(new FakeSubaru(), "COM3");

        DateTime deadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < deadline && !vm.RefreshLive()) Thread.Sleep(10);

        GaugeItem rpm = vm.Dashboard.Single(g => g.Title == "Engine Speed");

        // 0x0E36 over the wire, quarter-scaled: the reading the real car gave.
        Assert.Equal(909.5, rpm.Value, 3);

        Assert.All(vm.Dashboard, g =>
            Assert.False(double.IsNaN(g.Value), $"{g.Title} never received a reading"));
    }

    /// <summary>
    /// Every address in the file may be wrong, and a session that starts happily
    /// and shows a screen of dashes is a worse outcome than one that refuses and
    /// names the address.
    /// </summary>
    [Fact]
    public void AWrongAddressRefusesTheSessionRatherThanShowingDashes()
    {
        MainViewModel vm = NewViewModel();

        Directory.CreateDirectory(Path.GetDirectoryName(vm.SsmParameterPath)!);
        File.WriteAllText(vm.SsmParameterPath, """
            {"parameters":[{"name":"Invented","address":"0x123456"}]}
            """);

        var car = new FakeSubaru();

        Assert.Throws<EcuProtocolException>(() => { vm.ConnectSsm(car, "COM3"); });
        Assert.False(vm.IsLive);
    }
}
