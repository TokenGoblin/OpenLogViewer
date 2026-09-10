using OpenLogViewer.Core;
using Xunit;

namespace OpenLogViewer.Tests;

/// <summary>
/// What the dyno needs, and how much of it the recording already knows.
///
/// The list is long and most of it is written down somewhere already. The
/// channels say what the engine did; the tune the recording carries says how the
/// controller was set up. What nobody but the driver knows is the car.
///
/// Reading the tune is not a convenience. The injector timing on the car these
/// were written against cannot be inferred from any channel — the controller
/// reports the same duty figure whether an injector opens once a cycle or twice
/// — and getting it wrong is a factor of two on the fuel.
/// </summary>
public class DynoSetupTests
{
    private static VehicleSpec Car() => new()
    {
        KerbMassKg = 1450,
        OccupantMassKg = 85,
        GearRatios = [3.82, 2.20, 1.40, 1.00, 0.81],
        FinalDrive = 3.25,
        Tyre = new Tyre(205, 60, 15),
        DrivetrainLossPercent = 15,
    };

    private static EngineSpec Entered() => new()
    {
        Litres = 3.43, Cylinders = 4, Fuel = Fuel.Petrol,
        OpeningsPerEngineCycle = 2, InjectorDeadTimeMs = 1.0,
    };

    /// <summary>A tune with just the fuelling constants that matter.</summary>
    private static string Tune(
        string cylinders = "6", string divider = "3",
        string alternate = "\"Alternating\"", string timed = "\"Untimed injection\"",
        string injOpen = "1.51", string battFac = "0.12") =>
        $"""
         <msq>
           <versionInfo fileFormat="5.0" signature="MS2Extra comms340vU"/>
           <page>
             <constant name="nCylinders">{cylinders}</constant>
             <constant name="divider">{divider}</constant>
             <constant name="alternate">{alternate}</constant>
             <constant name="seq_inj">{timed}</constant>
             <constant digits="3" name="injOpen" units="ms">{injOpen}</constant>
             <constant digits="3" name="battFac" units="ms/v">{battFac}</constant>
           </page>
         </msq>
         """;

    private static LogDocument Log(string? tune, bool roadSpeed = false, bool flatRoadSpeed = false)
    {
        double[] rpm = [1000, 2000, 3000, 4000];

        List<LogChannel> channels =
        [
            new LogChannel("RPM", "rpm", 0, rpm),
            new LogChannel("TPS", "%", 1, [10, 50, 90, 98]),
            new LogChannel("MAP", "kPa", 1, [40, 90, 140, 160]),
            new LogChannel("MAT", "°F", 1, [90, 95, 100, 105]),
            new LogChannel("AFR", "afr", 2, [14.7, 13, 12, 11.8]),
            new LogChannel("PW", "ms", 2, [3, 5, 7, 8.5]),
            new LogChannel("Batt V", "v", 1, [13.8, 13.2, 12.8, 12.4]),
        ];

        if (roadSpeed || flatRoadSpeed)
        {
            channels.Add(new LogChannel(
                "VSS", "MPH", 1, flatRoadSpeed ? [0, 0, 0, 0] : [20, 40, 60, 80]));
        }

        return new LogDocument
        {
            FilePath = "x",
            Time = new LogChannel("Time", "s", 3, [0, 0.1, 0.2, 0.3], preservePrecision: true),
            Channels = channels,
            FormatName = "test",
            EmbeddedTune = tune,
        };
    }

    // ----- what the tune says --------------------------------------------------------

    [Fact]
    public void TheTuneOverridesWhatSomebodyTypedIn()
    {
        // It is the controller's own account of itself, and the person is working
        // from memory.
        DynoInputs setup = DynoSetup.Read(Log(Tune()), Car(), Entered());

        Assert.Equal(6, setup.Engine.Cylinders);
        Assert.Equal(1.51, setup.Engine.InjectorDeadTimeMs, 3);
        Assert.Equal(0.12, setup.Engine.DeadTimeMsPerVolt, 3);

        Assert.Contains(setup.Measured, i => i.Name == "Cylinders");
        Assert.Contains(setup.Measured, i => i.Name == "Injector dead time");
    }

    [Fact]
    public void AlternatingBanksHalveTheSquirtCount()
    {
        // Six cylinders over a divider of three is two squirts a cycle. Fired
        // simultaneously every injector opens on both; split into alternating
        // banks each takes every other one, so each opens once.
        //
        // That is a factor of two on the fuel, and no channel in the recording
        // can tell you which it is — the controller reports the same duty either
        // way.
        DynoInputs alternating = DynoSetup.Read(Log(Tune()), Car(), Entered());
        DynoInputs simultaneous = DynoSetup.Read(
            Log(Tune(alternate: "\"Simultaneous\"")), Car(), Entered());

        Assert.Equal(1, alternating.Engine.OpeningsPerEngineCycle, 3);
        Assert.Equal(2, simultaneous.Engine.OpeningsPerEngineCycle, 3);
    }

    [Fact]
    public void TimedInjectionIsOneOpeningWhateverTheDividerSays()
    {
        DynoInputs setup = DynoSetup.Read(
            Log(Tune(divider: "1", alternate: "\"Simultaneous\"", timed: "\"Timed injection\"")),
            Car(), Entered());

        Assert.Equal(1, setup.Engine.OpeningsPerEngineCycle, 3);
    }

    [Fact]
    public void ARecordingWithNoTuneLeavesWhatWasTypedInAndSaysSo()
    {
        DynoInputs setup = DynoSetup.Read(Log(null), Car(), Entered());

        Assert.Equal(Entered().Cylinders, setup.Engine.Cylinders);
        Assert.Equal(Entered().OpeningsPerEngineCycle, setup.Engine.OpeningsPerEngineCycle, 3);

        DynoInput timing = Assert.Single(setup.Inputs, i => i.Name == "Injector timing");

        Assert.Equal(InputSource.Entered, timing.Source);
        Assert.Contains("no tune", timing.Note, StringComparison.Ordinal);
        Assert.Contains("factor of two", timing.Note, StringComparison.Ordinal);
    }

    [Fact]
    public void SomethingThatIsNotATuneIsIgnoredRatherThanThrown()
    {
        DynoInputs setup = DynoSetup.Read(Log("not xml at all <<<"), Car(), Entered());

        Assert.Equal(Entered().Cylinders, setup.Engine.Cylinders);
        Assert.True(setup.CanDrawAnything);
    }

    // ----- what the channels say -------------------------------------------------------

    [Fact]
    public void ARoadSpeedChannelReadingNothingCountsAsMissing()
    {
        // Every log this was written against has one, wired to nothing, reading a
        // flat zero for twenty-five minutes. Present is not the same as usable,
        // and reporting it as present would say the gear had been checked.
        DynoInput wired = Assert.Single(
            DynoSetup.Read(Log(Tune(), roadSpeed: true), Car(), Entered()).Inputs,
            i => i.Name == "Road speed");

        DynoInput flat = Assert.Single(
            DynoSetup.Read(Log(Tune(), flatRoadSpeed: true), Car(), Entered()).Inputs,
            i => i.Name == "Road speed");

        Assert.Equal(InputSource.Log, wired.Source);
        Assert.Equal(InputSource.Missing, flat.Source);
    }

    [Fact]
    public void EveryRouteThisRecordingCanSupportIsOffered()
    {
        DynoInputs setup = DynoSetup.Read(Log(Tune()), Car(), Entered());

        Assert.Contains(PowerMethodKind.RoadLoad, setup.Available);
        Assert.Contains(PowerMethodKind.SpeedDensity, setup.Available);
        Assert.Contains(PowerMethodKind.Injectors, setup.Available);

        // And the one it cannot, with what it wanted.
        Assert.Contains(setup.Unavailable, u => u.Method == PowerMethodKind.MassAirFlow);
        Assert.Contains(setup.Unavailable, u => u.Needs.Contains("mass air flow", StringComparison.Ordinal));
    }

    [Fact]
    public void ARecordingWithNothingUsableSaysWhatEachRouteWanted()
    {
        var bare = new LogDocument
        {
            FilePath = "x",
            Time = new LogChannel("Time", "s", 3, [0, 0.1, 0.2], preservePrecision: true),
            Channels = [new LogChannel("CLT", "C", 0, [80, 81, 82])],
            FormatName = "test",
        };

        DynoInputs setup = DynoSetup.Read(bare, Car() with { GearRatios = [] }, Entered());

        Assert.False(setup.CanDrawAnything);
        Assert.Contains("Nothing can be drawn", setup.Summary, StringComparison.Ordinal);
        Assert.Contains("manifold pressure", setup.Summary, StringComparison.Ordinal);
    }

    // ----- what is known against what was guessed ----------------------------------------

    [Fact]
    public void WhatWasMeasuredIsKeptApartFromWhatWasGuessed()
    {
        // The whole point. A figure resting on six measurements and one guess is
        // worth quoting; the same figure resting on one measurement and six
        // guesses is not, and from the outside they look identical.
        DynoInputs setup = DynoSetup.Read(Log(Tune()), Car(), Entered());

        Assert.True(setup.Measured.Count() >= 10, $"only {setup.Measured.Count()} came off the recording");

        // The car is nobody's business but the driver's, and says so.
        Assert.Contains(setup.Inputs, i => i.Name == "Mass" && i.Source == InputSource.Entered);

        // The two nobody has measured are called out as guesses rather than
        // sitting quietly among the rest.
        Assert.Contains(setup.Assumed, i => i.Name.StartsWith("Drag area", StringComparison.Ordinal));
        Assert.Contains(setup.Assumed, i => i.Name == "Driveline loss");

        Assert.Contains(setup.Assumed, i => i.Note.Contains("No road test can measure it", StringComparison.Ordinal));
    }

    [Fact]
    public void ACoastdownMovesTheRoadLoadOutOfTheGuesses()
    {
        VehicleSpec measured = Car() with { RoadLoadMeasured = true };

        DynoInputs setup = DynoSetup.Read(Log(Tune()), measured, Entered());

        Assert.DoesNotContain(setup.Assumed, i => i.Name.StartsWith("Drag area", StringComparison.Ordinal));
    }
}
