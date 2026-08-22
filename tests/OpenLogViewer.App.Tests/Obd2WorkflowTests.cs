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
    private MainViewModel NewViewModel() => NewViewModel(out _);

    /// <summary>The same, handing back the settings it was built on.</summary>
    private MainViewModel NewViewModel(out SettingsStore settings)
    {
        string directory = Path.Combine(Path.GetTempPath(), $"olv-obd2-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        _temp.Add(directory);

        settings = new SettingsStore(Path.Combine(directory, "settings.json"));
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
    public void AWiFiDongleReachesTheDashboardLikeAnyOtherAdapter()
    {
        // Nothing about the gauges changes over Wi-Fi — it is the same ELM327
        // conversation down a socket — so what this proves is that the address
        // route arrives at the same place the port and the radio do, rather than
        // at a session with no dashboard.
        MainViewModel vm = NewViewModel();

        using var dongle = new FakeElmOverTcp(Car());

        vm.ConnectObd2Wifi(dongle.Address);

        Assert.True(vm.IsLive);
        Assert.Contains("RPM", vm.Dashboard.Select(g => g.Title));

        // Named by the address that answered, which is the only handle a Wi-Fi
        // adapter has: nothing else lists one.
        Assert.Contains(dongle.Address, vm.LiveDetail, StringComparison.Ordinal);
    }

    [Fact]
    public void AWiFiDongleWithFormIsNotProbedForBatchingAgain()
    {
        // What the settings remember has to reach the Wi-Fi route, and this is
        // the route the window takes. Connected without it, every drive re-probes
        // batching — and the probe is itself a batched request, so on the dongle
        // the memory exists for it re-kills the session too, once per drive, for
        // ever.
        using var dongle = new FakeElmOverTcp(Car());

        MainViewModel vm = NewViewModel(out SettingsStore settings);

        for (int drive = 0; drive < Elm327Source.BatchDeathsBeforeGivingUp; drive++)
            settings.RecordObd2BatchDeath(dongle.Address);

        using Elm327Source source = vm.OpenWifiAdapter(dongle.Address);

        Assert.False(source.Batching, "an adapter with form was probed anyway");
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

    // ----- fault codes --------------------------------------------------------

    /// <summary>
    /// Fault scanning is offered only where there is something to scan. Every
    /// other link here is an aftermarket ECU with no such thing, and a menu item
    /// that is enabled and then reports "not supported" is worse than one that is
    /// visibly unavailable.
    /// </summary>
    [Fact]
    public void FaultScanningIsOfferedOnlyToAStandardVehicle()
    {
        MainViewModel vm = NewViewModel();

        Assert.False(vm.IsObd2Live);

        vm.ConnectObd2(Car(), "COM3");
        Assert.True(vm.IsObd2Live);

        vm.Disconnect();
        Assert.False(vm.IsObd2Live);
    }

    /// <summary>
    /// A car that offers more than this can read says so.
    ///
    /// The car enumerates what it will answer; this keeps the ones it has a
    /// decoder for, and the rest used to be dropped with nothing said. A channel
    /// the vehicle was willing to send and this discarded looks, on screen,
    /// exactly like a car that never had it — and the difference between those
    /// two is the difference between a dead end and a morning's work.
    /// </summary>
    [Fact]
    public void WhatTheCarOffersAndThisCannotReadIsReported()
    {
        FakeElm car = Car();

        // Bit 2 of the first mask is PID 0x03, fuel system status: a real
        // parameter, in the standard, that this has no decoder for.
        car.Answers[0x00] = [0b1011_1000, 0b0011_1010, 0b1000_0000, 0b0000_0001];
        car.Answers[0x03] = [0x02, 0x00];

        MainViewModel vm = NewViewModel();
        vm.ConnectObd2(car, "COM3");

        Assert.Contains("0x03", vm.Obd2Gaps, StringComparison.Ordinal);
        Assert.Contains("cannot decode yet", vm.Obd2Gaps, StringComparison.Ordinal);

        // And it does not claim a gap that is not there.
        Assert.DoesNotContain("0x0C", vm.Obd2Gaps, StringComparison.Ordinal);
    }

    /// <summary>A car whose parameters are all readable says nothing about gaps.</summary>
    [Fact]
    public void NoGapMeansNoNoise()
    {
        MainViewModel vm = NewViewModel();
        vm.ConnectObd2(Car(), "COM3");

        Assert.Equal("", vm.Obd2Gaps);
    }

    /// <summary>
    /// Calibration means fault codes on a standard vehicle.
    ///
    /// An OBD2 car has no tune and never will, so the tab used to tell somebody
    /// who was plainly connected to "connect to an ECU" — the view describing a
    /// state the application was not in. The notice and the fault panel are
    /// mutually exclusive, and both are asserted: showing neither would leave the
    /// tab blank, and showing both would be worse.
    /// </summary>
    [Fact]
    public void CalibrationOffersFaultCodesRatherThanATuneItCannotHave()
    {
        MainViewModel vm = NewViewModel();

        // Nothing connected: the notice is the right thing to show.
        Assert.True(vm.ShowNoTuneNotice);
        Assert.False(vm.IsObd2Live);

        vm.ConnectObd2(Car(), "COM3");

        Assert.True(vm.IsObd2Live, "the fault panel is shown on this");
        Assert.False(vm.ShowNoTuneNotice, "and the 'connect to an ECU' notice is not");
        Assert.True(vm.NoEcuTune, "there is still no tune — that has not changed");

        vm.Disconnect();

        Assert.False(vm.IsObd2Live);
        Assert.True(vm.ShowNoTuneNotice);
    }

    /// <summary>
    /// The whole path, with the session already polling. This is the part the core
    /// tests cannot reach: there, a scan has the adapter to itself; here the poll
    /// loop is running against the same one throughout.
    /// </summary>
    [Fact]
    public void ScansTheCarWhileItIsBeingPolled()
    {
        FakeElm car = Car();
        car.StoredCodes.AddRange(["P0301", "P0171"]);
        car.PendingCodes.Add("P0420");

        MainViewModel vm = NewViewModel();
        vm.ConnectObd2(car, "COM3");

        FaultScan? scan = vm.ScanFaults();

        Assert.NotNull(scan);
        Assert.Equal(["P0301", "P0171"], scan.Stored.Select(f => f.Code));
        Assert.Equal(["P0420"], scan.Pending.Select(f => f.Code));
        Assert.Equal("Cylinder 1 misfire detected", scan.Stored[0].Description);
    }

    [Fact]
    public void ErasingLeavesTheCarWithNothingStored()
    {
        FakeElm car = Car();
        car.StoredCodes.Add("P0301");

        MainViewModel vm = NewViewModel();
        vm.ConnectObd2(car, "COM3");

        FaultClear? cleared = vm.ClearFaults();

        Assert.NotNull(cleared);
        Assert.True(cleared.Erased);
        Assert.Empty(vm.ScanFaults()!.Stored);
    }

    /// <summary>
    /// Asking with nothing connected returns nothing rather than throwing. The
    /// window can be left open while the cable is pulled.
    /// </summary>
    [Fact]
    public void AskingWithNothingConnectedIsNotAnError()
    {
        MainViewModel vm = NewViewModel();

        Assert.Null(vm.ScanFaults());
        Assert.Null(vm.ClearFaults());
    }
}
