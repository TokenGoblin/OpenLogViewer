using OpenLogViewer.Core;
using Xunit;

namespace OpenLogViewer.Tests;

/// <summary>
/// Every role, against the channel lists real firmware really declares.
///
/// <para>
/// <see cref="ChannelRoleTests"/> checks the matching rules against names
/// written by hand, and that is what let three makes of controller go wrong at
/// once while nothing failed: a live Speeduino matched fourteen of these
/// twenty-one and a live rusEFI fourteen, having between them no mixture, no
/// target, no spark advance, no volumetric efficiency, no injector pulse width
/// or duty, no battery voltage and no road speed. The insights, the suggested
/// filters and the VE calibration are built on those.
/// </para>
///
/// <para>
/// Each firmware appears twice, because it names its channels twice over. A
/// <c>.channels</c> fixture holds the output-channel <em>field</em> names, which
/// is what a controller's own logs tend to carry — a rusEFI writes "RPMValue".
/// A <c>.logged</c> fixture holds its <em>datalog labels</em>, which is what
/// <see cref="TunerStudioSource"/> names a live session's channels by, and so
/// what a session recorded here carries — the same board writes "RPM". Both
/// reach <see cref="ChannelRoles"/> in the wild and the aliases have to cover
/// both.
/// </para>
///
/// <para>
/// The two <c>-bench</c> fixtures are not derived from a definition file at all:
/// they are the channel lists of logs this application wrote over the wire from
/// the boards on the bench, which is the only evidence here that owes nothing to
/// reading an INI correctly.
/// </para>
///
/// <para>
/// The expected channel is spelled out for every role, absences included, so
/// that adding a role or loosening an alias has to be answered for on all
/// twelve.
/// </para>
/// </summary>
public class FirmwareChannelRoleTests
{
    /// <summary>Every channel a Speeduino 202501 publishes, by field name. 17 of 21.</summary>
    [Fact]
    public void SpeeduinoPublished() => Check("speeduino-202501.channels", new()
    {
        [ChannelRole.EngineSpeed] = "rpm",
        [ChannelRole.Coolant] = "coolant",
        [ChannelRole.Throttle] = "tps",
        [ChannelRole.Mixture] = "afr",
        [ChannelRole.MixtureTarget] = "afrTarget",
        [ChannelRole.ManifoldPressure] = "map",
        [ChannelRole.FuelCut] = null,
        [ChannelRole.IntakeAir] = "iat",
        [ChannelRole.Barometric] = "baro",
        [ChannelRole.InjectorPulseWidth] = "pulseWidth",
        [ChannelRole.InjectorDuty] = null,
        [ChannelRole.FuelPressure] = "fuelPressure",
        [ChannelRole.MassAirFlow] = null,
        [ChannelRole.VolumetricEfficiency] = "VE1",
        [ChannelRole.VehicleSpeed] = "vss",
        [ChannelRole.SparkAdvance] = "advance",
        [ChannelRole.KnockRetard] = "knockCor",
        [ChannelRole.BatteryVoltage] = "batteryVoltage",
        [ChannelRole.MixtureCorrection] = "egoCorrection",
        [ChannelRole.WarmupCorrection] = "warmup",
        [ChannelRole.Boost] = null,
    });

    /// <summary>The datalog labels a Speeduino 202501 log carries. 18 of 21.</summary>
    [Fact]
    public void SpeeduinoLogged() => Check("speeduino-202501.logged", new()
    {
        [ChannelRole.EngineSpeed] = "RPM",
        [ChannelRole.Coolant] = "CLT",
        [ChannelRole.Throttle] = "TPS",
        [ChannelRole.Mixture] = "AFR",
        [ChannelRole.MixtureTarget] = "AFR Target",
        [ChannelRole.ManifoldPressure] = "MAP",
        [ChannelRole.FuelCut] = "DFCO",
        [ChannelRole.IntakeAir] = "IAT",
        [ChannelRole.Barometric] = "Baro Pressure",
        [ChannelRole.InjectorPulseWidth] = "PW",
        [ChannelRole.InjectorDuty] = null,
        [ChannelRole.FuelPressure] = "Fuel Pressure",
        [ChannelRole.MassAirFlow] = null,
        [ChannelRole.VolumetricEfficiency] = "VE1",
        [ChannelRole.VehicleSpeed] = "Wheel Speed (kph)",
        [ChannelRole.SparkAdvance] = "Advance 1",
        [ChannelRole.KnockRetard] = null,
        [ChannelRole.BatteryVoltage] = "Battery V",
        [ChannelRole.MixtureCorrection] = "Gego",
        [ChannelRole.WarmupCorrection] = "Gwarm",
        [ChannelRole.Boost] = "Boost PSI",
    });

    /// <summary>Straight off the board: COM14, 2026-09-04. 19 of 21.</summary>
    [Fact]
    public void SpeeduinoOnTheBench() => Check("speeduino-202501-bench.logged", new()
    {
        [ChannelRole.EngineSpeed] = "RPM",
        [ChannelRole.Coolant] = "CLT",
        [ChannelRole.Throttle] = "TPS",
        [ChannelRole.Mixture] = "AFR",
        [ChannelRole.MixtureTarget] = "AFR Target",
        [ChannelRole.ManifoldPressure] = "MAP",
        [ChannelRole.FuelCut] = "DFCO",
        [ChannelRole.IntakeAir] = "IAT",
        [ChannelRole.Barometric] = "Baro Pressure",
        [ChannelRole.InjectorPulseWidth] = "PW",
        [ChannelRole.InjectorDuty] = null,
        [ChannelRole.FuelPressure] = "Fuel Pressure",
        [ChannelRole.MassAirFlow] = null,
        [ChannelRole.VolumetricEfficiency] = "VE",
        [ChannelRole.VehicleSpeed] = "Wheel Speed (mph)",
        [ChannelRole.SparkAdvance] = "Advance",
        [ChannelRole.KnockRetard] = "Knock Retard",
        [ChannelRole.BatteryVoltage] = "Battery V",
        [ChannelRole.MixtureCorrection] = "Gego",
        [ChannelRole.WarmupCorrection] = "Gwarm",
        [ChannelRole.Boost] = "Boost PSI",
    });

    /// <summary>Every channel an MS2Extra 3.4.2h2 publishes, by field name. 17 of 21.</summary>
    [Fact]
    public void MS2ExtraPublished() => Check("ms2extra-342h2.channels", new()
    {
        [ChannelRole.EngineSpeed] = "rpm",
        [ChannelRole.Coolant] = "coolant",
        [ChannelRole.Throttle] = "tps",
        [ChannelRole.Mixture] = "afr1",
        [ChannelRole.MixtureTarget] = "afrtgt1",
        [ChannelRole.ManifoldPressure] = "map",
        [ChannelRole.FuelCut] = null,
        [ChannelRole.IntakeAir] = "mat",
        [ChannelRole.Barometric] = "barometer",
        [ChannelRole.InjectorPulseWidth] = "pulseWidth",
        [ChannelRole.InjectorDuty] = null,
        [ChannelRole.FuelPressure] = null,
        [ChannelRole.MassAirFlow] = "maf",
        [ChannelRole.VolumetricEfficiency] = "veCurr",
        [ChannelRole.VehicleSpeed] = null,
        [ChannelRole.SparkAdvance] = "advance",
        [ChannelRole.KnockRetard] = "knockRetard",
        [ChannelRole.BatteryVoltage] = "batteryVoltage",
        [ChannelRole.MixtureCorrection] = "egoCorrection",
        [ChannelRole.WarmupCorrection] = "warmup",
        [ChannelRole.Boost] = "boostpsig",
    });

    /// <summary>The datalog labels an MS2Extra log carries. 17 of 21.</summary>
    [Fact]
    public void MS2ExtraLogged() => Check("ms2extra-342h2.logged", new()
    {
        [ChannelRole.EngineSpeed] = "RPM",
        [ChannelRole.Coolant] = "CLT",
        [ChannelRole.Throttle] = "TPS",
        [ChannelRole.Mixture] = "AFR",
        [ChannelRole.MixtureTarget] = "AFR Target 1",
        [ChannelRole.ManifoldPressure] = "MAP",
        [ChannelRole.FuelCut] = null,
        [ChannelRole.IntakeAir] = "MAT",
        [ChannelRole.Barometric] = "Barometer",
        [ChannelRole.InjectorPulseWidth] = "PW",
        [ChannelRole.InjectorDuty] = null,
        [ChannelRole.FuelPressure] = null,
        [ChannelRole.MassAirFlow] = "MAF",
        [ChannelRole.VolumetricEfficiency] = "VE1",
        [ChannelRole.VehicleSpeed] = null,
        [ChannelRole.SparkAdvance] = "SPK: Spark Advance",
        [ChannelRole.KnockRetard] = "SPK: Knock retard",
        [ChannelRole.BatteryVoltage] = "Batt V",
        [ChannelRole.MixtureCorrection] = "EGO cor1",
        [ChannelRole.WarmupCorrection] = "Fuel: Warmup cor",
        [ChannelRole.Boost] = "Boost psi",
    });

    /// <summary>Every channel an MS3 0592.13P publishes, by field name. 20 of 21.</summary>
    [Fact]
    public void MS3Published() => Check("ms3-0592.13p.channels", new()
    {
        [ChannelRole.EngineSpeed] = "rpm",
        [ChannelRole.Coolant] = "coolant",
        [ChannelRole.Throttle] = "tps",
        [ChannelRole.Mixture] = "afr1",
        [ChannelRole.MixtureTarget] = "afrtgt1",
        [ChannelRole.ManifoldPressure] = "map",
        [ChannelRole.FuelCut] = "fuelCutActive",
        [ChannelRole.IntakeAir] = "mat",
        [ChannelRole.Barometric] = "barometer",
        [ChannelRole.InjectorPulseWidth] = "pulseWidth",
        [ChannelRole.InjectorDuty] = null,
        [ChannelRole.FuelPressure] = "fuel_press1",
        [ChannelRole.MassAirFlow] = "maf",
        [ChannelRole.VolumetricEfficiency] = "veCurr",
        [ChannelRole.VehicleSpeed] = "vss1",
        [ChannelRole.SparkAdvance] = "advance",
        [ChannelRole.KnockRetard] = "knockRetard",
        [ChannelRole.BatteryVoltage] = "batteryVoltage",
        [ChannelRole.MixtureCorrection] = "egocor1",
        [ChannelRole.WarmupCorrection] = "warmup",
        [ChannelRole.Boost] = "boostpsig",
    });

    /// <summary>The datalog labels an MS3 log carries. 20 of 21.</summary>
    [Fact]
    public void MS3Logged() => Check("ms3-0592.13p.logged", new()
    {
        [ChannelRole.EngineSpeed] = "RPM",
        [ChannelRole.Coolant] = "CLT",
        [ChannelRole.Throttle] = "TPS",
        [ChannelRole.Mixture] = "AFR",
        [ChannelRole.MixtureTarget] = "AFR 1 Target",
        [ChannelRole.ManifoldPressure] = "MAP",
        [ChannelRole.FuelCut] = "status: Fuel Cut",
        [ChannelRole.IntakeAir] = "MAT",
        [ChannelRole.Barometric] = "Barometer",
        [ChannelRole.InjectorPulseWidth] = "PW",
        [ChannelRole.InjectorDuty] = null,
        [ChannelRole.FuelPressure] = "Fuel Pressure1_kPa",
        [ChannelRole.MassAirFlow] = "MAF",
        [ChannelRole.VolumetricEfficiency] = "VE1",
        [ChannelRole.VehicleSpeed] = "VSS1",
        [ChannelRole.SparkAdvance] = "SPK: Spark Advance",
        [ChannelRole.KnockRetard] = "SPK: Knock retard",
        [ChannelRole.BatteryVoltage] = "Batt V",
        [ChannelRole.MixtureCorrection] = "EGO cor1",
        [ChannelRole.WarmupCorrection] = "Fuel: Warmup cor",
        [ChannelRole.Boost] = "Boost psi",
    });

    /// <summary>Every channel a rusEFI of November 2024 publishes, by field name. 19 of 21.</summary>
    [Fact]
    public void RusEfi2024Published() => Check("rusefi-2024.11.17-uaefi.channels", new()
    {
        [ChannelRole.EngineSpeed] = "RPMValue",
        [ChannelRole.Coolant] = "coolant",
        [ChannelRole.Throttle] = "TPSValue",
        [ChannelRole.Mixture] = "AFRValue",
        [ChannelRole.MixtureTarget] = "targetAFR",
        [ChannelRole.ManifoldPressure] = "MAPValue",
        [ChannelRole.FuelCut] = "fuelCutReason",
        [ChannelRole.IntakeAir] = "intake",
        [ChannelRole.Barometric] = "baroPressure",
        [ChannelRole.InjectorPulseWidth] = "actualLastInjection",
        [ChannelRole.InjectorDuty] = "injectorDutyCycle",
        [ChannelRole.FuelPressure] = "lowFuelPressure",
        [ChannelRole.MassAirFlow] = "mafMeasured",
        [ChannelRole.VolumetricEfficiency] = "veValue",
        [ChannelRole.VehicleSpeed] = "vehicleSpeedKph",
        [ChannelRole.SparkAdvance] = "correctedIgnitionAdvance",
        [ChannelRole.KnockRetard] = "m_knockRetard",
        [ChannelRole.BatteryVoltage] = "VBatt",
        [ChannelRole.MixtureCorrection] = "Gego",
        [ChannelRole.WarmupCorrection] = null,
        [ChannelRole.Boost] = null,
    });

    /// <summary>The datalog labels a rusEFI of November 2024 logs. 19 of 21.</summary>
    [Fact]
    public void RusEfi2024Logged() => Check("rusefi-2024.11.17-uaefi.logged", new()
    {
        [ChannelRole.EngineSpeed] = "RPM",
        [ChannelRole.Coolant] = "CLT",
        [ChannelRole.Throttle] = "TPS",
        [ChannelRole.Mixture] = "Lambda",
        [ChannelRole.MixtureTarget] = "Fuel: target lambda",
        [ChannelRole.ManifoldPressure] = "MAP",
        [ChannelRole.FuelCut] = "Fuel: Cut Code",
        [ChannelRole.IntakeAir] = "IAT",
        [ChannelRole.Barometric] = "baroPressure",
        [ChannelRole.InjectorPulseWidth] = "Fuel: Last inj pulse width",
        [ChannelRole.InjectorDuty] = "Fuel: injector duty cycle",
        [ChannelRole.FuelPressure] = "Fuel pressure (low)",
        [ChannelRole.MassAirFlow] = "MAF",
        [ChannelRole.VolumetricEfficiency] = "Fuel: VE",
        [ChannelRole.VehicleSpeed] = "Vehicle Speed",
        [ChannelRole.SparkAdvance] = "Timing: ignition",
        [ChannelRole.KnockRetard] = "Knock: Retard",
        [ChannelRole.BatteryVoltage] = "VBatt",
        [ChannelRole.MixtureCorrection] = "Gego",
        [ChannelRole.WarmupCorrection] = null,
        [ChannelRole.Boost] = null,
    });

    /// <summary>Every channel a rusEFI of September 2026 publishes, by field name. 19 of 21.</summary>
    [Fact]
    public void RusEfi2026Published() => Check("rusefi-2026.09.03-super-uaefi.channels", new()
    {
        [ChannelRole.EngineSpeed] = "RPMValue",
        [ChannelRole.Coolant] = "coolant",
        [ChannelRole.Throttle] = "TPSValue",
        [ChannelRole.Mixture] = "AFRValue",
        [ChannelRole.MixtureTarget] = "targetAFR",
        [ChannelRole.ManifoldPressure] = "MAPValue",
        [ChannelRole.FuelCut] = "fuelCutReason",
        [ChannelRole.IntakeAir] = "intake",
        [ChannelRole.Barometric] = "baroPressure",
        [ChannelRole.InjectorPulseWidth] = "actualLastInjection",
        [ChannelRole.InjectorDuty] = "injectorDutyCycle",
        [ChannelRole.FuelPressure] = "lowFuelPressure",
        [ChannelRole.MassAirFlow] = "mafMeasured",
        [ChannelRole.VolumetricEfficiency] = "veValue",
        [ChannelRole.VehicleSpeed] = "vehicleSpeedKph",
        [ChannelRole.SparkAdvance] = "correctedIgnitionAdvance",
        [ChannelRole.KnockRetard] = "m_knockRetard",
        [ChannelRole.BatteryVoltage] = "VBatt",
        [ChannelRole.MixtureCorrection] = "Gego",
        [ChannelRole.WarmupCorrection] = null,
        [ChannelRole.Boost] = null,
    });

    /// <summary>The datalog labels a rusEFI of September 2026 logs. 19 of 21.</summary>
    [Fact]
    public void RusEfi2026Logged() => Check("rusefi-2026.09.03-super-uaefi.logged", new()
    {
        [ChannelRole.EngineSpeed] = "RPM",
        [ChannelRole.Coolant] = "CLT",
        [ChannelRole.Throttle] = "TPS",
        [ChannelRole.Mixture] = "Lambda",
        [ChannelRole.MixtureTarget] = "Fuel: target lambda",
        [ChannelRole.ManifoldPressure] = "MAP",
        [ChannelRole.FuelCut] = "Fuel: Cut Code",
        [ChannelRole.IntakeAir] = "Intake Air IAT",
        [ChannelRole.Barometric] = "baroPressure",
        [ChannelRole.InjectorPulseWidth] = "Fuel: Last inj pulse width",
        [ChannelRole.InjectorDuty] = "Fuel: injector duty cycle",
        [ChannelRole.FuelPressure] = "Fuel pressure (low)",
        [ChannelRole.MassAirFlow] = "MAF",
        [ChannelRole.VolumetricEfficiency] = "Fuel: VE",
        [ChannelRole.VehicleSpeed] = "Vehicle Speed",
        [ChannelRole.SparkAdvance] = "Timing: ignition",
        [ChannelRole.KnockRetard] = "Knock: Retard",
        [ChannelRole.BatteryVoltage] = "VBatt",
        [ChannelRole.MixtureCorrection] = "Gego",
        [ChannelRole.WarmupCorrection] = null,
        [ChannelRole.Boost] = null,
    });

    /// <summary>Straight off the board: COM8, 2026-09-04. 19 of 21.</summary>
    [Fact]
    public void RusEfiOnTheBench() => Check("rusefi-bench.logged", new()
    {
        [ChannelRole.EngineSpeed] = "RPM",
        [ChannelRole.Coolant] = "CLT",
        [ChannelRole.Throttle] = "TPS",
        [ChannelRole.Mixture] = "Lambda",
        [ChannelRole.MixtureTarget] = "Fuel: target lambda",
        [ChannelRole.ManifoldPressure] = "MAP",
        [ChannelRole.FuelCut] = "Fuel: Cut Code",
        [ChannelRole.IntakeAir] = "IAT",
        [ChannelRole.Barometric] = "baroPressure",
        [ChannelRole.InjectorPulseWidth] = "Fuel: Last inj pulse width",
        [ChannelRole.InjectorDuty] = "Fuel: injector duty cycle",
        [ChannelRole.FuelPressure] = "Fuel pressure",
        [ChannelRole.MassAirFlow] = "MAF",
        [ChannelRole.VolumetricEfficiency] = "Fuel: VE",
        [ChannelRole.VehicleSpeed] = "Vehicle Speed",
        [ChannelRole.SparkAdvance] = "Timing: ignition",
        [ChannelRole.KnockRetard] = "Knock: Retard",
        [ChannelRole.BatteryVoltage] = "VBatt",
        [ChannelRole.MixtureCorrection] = "Gego",
        [ChannelRole.WarmupCorrection] = null,
        [ChannelRole.Boost] = null,
    });
    private static void Check(string fixture, Dictionary<ChannelRole, string?> expected)
    {
        LogDocument log = Load(fixture);

        // Every role accounted for, so a new one cannot be added without saying
        // what each of these controllers answers for it.
        Assert.Equal<IEnumerable<ChannelRole>>(
            [.. Enum.GetValues<ChannelRole>().Order()],
            [.. expected.Keys.Order()]);

        foreach ((ChannelRole role, string? name) in expected)
            Assert.Equal(name, ChannelRoles.Find(log, role)?.Name);
    }

    /// <summary>
    /// A fixture as a document: one sample per channel, since only the names and
    /// the declared units decide a role.
    /// </summary>
    private static LogDocument Load(string fixture)
    {
        string path = Path.Combine(AppContext.BaseDirectory, "Fixtures", fixture);
        var channels = new List<LogChannel>();

        foreach (string line in File.ReadAllLines(path))
        {
            if (line.Length == 0 || line[0] == '#') continue;

            string[] parts = line.Split('\t');
            channels.Add(new LogChannel(parts[0], parts.Length > 1 ? parts[1] : "", 2, [0]));
        }

        Assert.NotEmpty(channels);

        return new LogDocument
        {
            FilePath = fixture,
            FormatName = "Live",
            Channels = channels,
            Time = new LogChannel("Time", "s", 3, [0], preservePrecision: true),
        };
    }
}
