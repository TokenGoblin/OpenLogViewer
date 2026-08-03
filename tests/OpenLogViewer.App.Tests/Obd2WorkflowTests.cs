using System.IO;
using OpenLogViewer.Core;
using OpenLogViewer.Tests;

namespace OpenLogViewer.App.Tests;

/// <summary>
/// Connecting to a car through an OBD2 adapter, end to end.
///
/// The wire is covered by the core tests. What is covered here is the part that
/// has actually gone wrong twice: which gauges reach the dashboard. A channel
/// that arrives perfectly and never appears is indistinguishable, to whoever is
/// looking at the screen, from one that was never read.
/// </summary>
public class Obd2WorkflowTests : IDisposable
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

    /// <summary>
    /// A view model whose recordings land in a temporary folder rather than in
    /// the user's own.
    /// </summary>
    private MainViewModel NewViewModel()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"olv-obd2-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        _temp.Add(directory);

        var settings = new SettingsStore(Path.Combine(directory, "settings.json"));
        settings.SetDataFolder(Path.Combine(directory, "workspace"));

        _vm = new MainViewModel(
            new PresetStore(Path.Combine(directory, "presets.json")),
            new FilterStore(Path.Combine(directory, "filters.json")),
            settings,
            new MathChannelStore(Path.Combine(directory, "math.json")));

        return _vm;
    }

    /// <summary>A car reporting the parameters nearly all of them do.</summary>
    private static FakeElm Car()
    {
        var car = new FakeElm();

        car.Answers[0x00] = [0b1001_1000, 0b0011_1010, 0b1000_0000, 0b0000_0001];
        car.Answers[0x20] = [0b0000_0000, 0b0000_0000, 0b0000_0000, 0b0000_0001];
        car.Answers[0x40] = [0b0100_0000, 0b0000_0000, 0b0000_0000, 0b0000_0000];

        car.Answers[0x01] = [0x00, 0x07, 0x65, 0x04];
        car.Answers[0x04] = [0x7F];
        car.Answers[0x05] = [0x5A];
        car.Answers[0x0B] = [0x64];
        car.Answers[0x0C] = [0x1A, 0xF8];
        car.Answers[0x0D] = [0x40];
        car.Answers[0x0F] = [0x46];
        car.Answers[0x11] = [0x33];
        car.Answers[0x42] = [0x37, 0x1A];

        return car;
    }

    [Fact]
    public void ConnectingToACarProducesNamedChannelsWithNoDefinitionFile()
    {
        // The whole point of OBD2 here: no .ini, nothing installed, nothing
        // downloaded — the standard fixes what every parameter means and the car
        // says which ones it has.
        MainViewModel vm = NewViewModel();

        vm.ConnectObd2(Car(), "COM3");

        Assert.True(vm.IsLive);
        Assert.Contains("RPM", vm.AllGauges.Select(g => g.Title));
        Assert.Contains("Coolant", vm.AllGauges.Select(g => g.Title));
    }

    [Fact]
    public void EveryGaugeIsFedByAChannelThatIsActuallyBeingRead()
    {
        // The Speeduino bug, guarded against here: a gauge whose column does not
        // match a recorded channel shows a face and never a number.
        MainViewModel vm = NewViewModel();

        vm.ConnectObd2(Car(), "COM3");

        Assert.All(vm.AllGauges, g =>
        {
            Assert.True(g.IsConnected, $"{g.Title} is fed by nothing");
            Assert.Contains(g.Column, vm.LiveChannelNames);
        });
    }

    [Fact]
    public void TheDashboardOpensAsADashboardRatherThanAsEverythingAtOnce()
    {
        MainViewModel vm = NewViewModel();

        vm.ConnectObd2(Car(), "COM3");

        Assert.NotEmpty(vm.Dashboard);
        Assert.True(vm.Dashboard.Count < vm.AllGauges.Count);
        Assert.Contains("RPM", vm.Dashboard.Select(g => g.Title));
    }

    [Fact]
    public void EveryDialHasARangeToDrawItOver()
    {
        // Unlike every other ECU here, there is no case where the range is
        // unknown — the standard states one for each parameter.
        MainViewModel vm = NewViewModel();

        vm.ConnectObd2(Car(), "COM3");

        Assert.All(vm.Dashboard, g => Assert.True(g.Spec.HasScale, $"{g.Title} has no range"));
    }

    [Fact]
    public void TheAdapterIsNamedRatherThanTheChipInIt()
    {
        MainViewModel vm = NewViewModel();

        vm.ConnectObd2(Car(), "COM3");

        Assert.Contains("ELM327", vm.Status, StringComparison.Ordinal);
    }

    [Fact]
    public void HowSlowItIsIsSaidRatherThanLeftToBeDiscovered()
    {
        // One request per parameter against a whole block in one exchange. A
        // tuner used to 25 Hz should be told, not left to conclude the link is
        // faulty.
        MainViewModel vm = NewViewModel();

        vm.ConnectObd2(Car(), "COM3");

        // "Twice a second" is measured, not guessed: 2.19 Hz over 25 seconds on a
        // live vehicle through a BLE ELM327 v1.5.
        Assert.Contains("twice a second", vm.Hint, StringComparison.OrdinalIgnoreCase);
    }
}
