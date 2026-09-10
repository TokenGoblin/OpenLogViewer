namespace OpenLogViewer.Core;

/// <summary>The air the engine was actually breathing.</summary>
/// <param name="PressureKpa">Absolute ambient pressure, not corrected to sea level.</param>
/// <param name="Celsius">Ambient temperature — the air going in, not the coolant.</param>
/// <param name="HumidityPercent">Relative humidity. Unknown is worse than a guess here; see the class.</param>
public readonly record struct Ambient(
    double PressureKpa,
    double Celsius,
    double HumidityPercent = AirDensity.AssumedHumidityPercent)
{
    /// <summary>A standard day, for anything that has to have an answer.</summary>
    public static Ambient Standard => new(TuningMath.AtmosphericKpa, 15, AssumedHumidity);

    private const double AssumedHumidity = AirDensity.AssumedHumidityPercent;

    public override string ToString() =>
        $"{PressureKpa:N1} kPa, {Celsius:N0} °C, {HumidityPercent:N0}% rh";
}

/// <summary>Which published correction to state a figure against.</summary>
public enum PowerCorrection
{
    /// <summary>As measured, on the day, in the air that was there.</summary>
    None,

    /// <summary>SAE J1349: 990 mbar dry, 25 °C.</summary>
    SaeJ1349,

    /// <summary>DIN 70020: 1013 mbar dry, 20 °C. Reads higher than SAE, always.</summary>
    Din70020,
}

/// <summary>
/// How much of the air was there, and what the published standards do about it.
///
/// <para>
/// An engine makes power in proportion to the mass of air it can swallow, and the
/// mass in a given volume moves with the weather. A cold, high-pressure morning
/// carries roughly ten per cent more air per litre than a hot, low-pressure
/// afternoon at altitude, which is ten per cent of the power — larger than most
/// of the changes anyone makes a run to measure. That is what the correction
/// factors are for: they state what the engine would have made on a nominated
/// standard day, so two runs a season apart can be compared.
/// </para>
/// <para>
/// <b>Water displaces air.</b> Humidity is the term people leave out, and leaving
/// it out biases every answer the same way. Water vapour is lighter than the
/// nitrogen and oxygen it pushes aside, so damp air is <em>less</em> dense than
/// dry air at the same pressure and temperature — the opposite of the intuition
/// that humid air feels heavy. Both standards below are defined against the dry
/// part of the pressure for exactly that reason, and feeding them the barometer
/// reading unchanged overstates the air on a muggy day by about a per cent.
/// </para>
/// <para>
/// <b>The correction is derived for engines that breathe what the sky gives
/// them.</b> A naturally aspirated engine on a thin day is genuinely short of
/// air, and scaling its figure back up is a fair statement of what it would have
/// done. A turbocharged engine is not in that position: it is holding a manifold
/// pressure the tuner asked for, and on a thin day it closes the wastegate a
/// little further and holds it anyway. Correcting that engine credits it for a
/// handicap the turbo already cancelled, and the number comes out flattered.
/// This is why <see cref="Recommended"/> answers <see cref="PowerCorrection.None"/>
/// for a boosted engine — the factor is still computed and still shown, because
/// the reader is entitled to know what the standard would have said, but it is
/// not applied behind their back.
/// </para>
/// </summary>
public static class AirDensity
{
    /// <summary>The specific gas constant for dry air, in joules per kilogram kelvin.</summary>
    public const double DryAirGasConstant = 287.05;

    /// <summary>The same for water vapour, which is lighter and so has a larger one.</summary>
    public const double VapourGasConstant = 461.495;

    /// <summary>
    /// Humidity to assume where the log carries none.
    ///
    /// Nothing records it, in practice. Half saturation is the middle of the
    /// range most testing happens in, and being wrong by the whole of it is worth
    /// only about half a per cent of density — far less than being wrong about
    /// the temperature by five degrees, which nobody worries about.
    /// </summary>
    public const double AssumedHumidityPercent = 50;

    /// <summary>
    /// The band SAE J1349 declares itself valid over.
    ///
    /// Outside it the linear form stops describing the engine, and the standard
    /// says not to apply it rather than to apply it further. Roughly: below about
    /// nine hundred metres of density altitude at one end, and a thin hot day at
    /// high altitude at the other.
    /// </summary>
    public const double SaeLowerLimit = 0.93;

    public const double SaeUpperLimit = 1.07;

    /// <summary>
    /// The pressure water vapour exerts when the air will hold no more, in kPa.
    ///
    /// The Magnus form, with the coefficients fitted over roughly -40 to 50 °C —
    /// which covers every condition anybody runs a car in, and a good deal more.
    /// </summary>
    public static double SaturationVapourPressureKpa(double celsius) =>
        0.61094 * Math.Exp(17.625 * celsius / (celsius + 243.04));

    /// <summary>The part of the ambient pressure that is not water.</summary>
    public static double DryPressureKpa(Ambient air)
    {
        double vapour = VapourPressureKpa(air);

        // A barometer reading below the vapour pressure is not a damp day, it is
        // a bad reading. Clamping keeps a nonsense input from turning into a
        // negative density that then propagates as a negative correction.
        return Math.Max(0, air.PressureKpa - vapour);
    }

    /// <summary>What the water in the air is contributing to the pressure, in kPa.</summary>
    public static double VapourPressureKpa(Ambient air)
    {
        double humidity = Math.Clamp(air.HumidityPercent, 0, 100);

        return SaturationVapourPressureKpa(air.Celsius) * humidity / 100;
    }

    /// <summary>
    /// Density in kilograms per cubic metre.
    ///
    /// Dry air and water vapour each treated as an ideal gas and added, which is
    /// the standard treatment and good to well under a tenth of a per cent at any
    /// condition a car sees.
    /// </summary>
    public static double KgPerCubicMetre(Ambient air)
    {
        double kelvin = air.Celsius + 273.15;

        if (!(kelvin > 0) || !(air.PressureKpa > 0)) return double.NaN;

        double dry = DryPressureKpa(air) * 1000;
        double vapour = VapourPressureKpa(air) * 1000;

        return (dry / (DryAirGasConstant * kelvin)) + (vapour / (VapourGasConstant * kelvin));
    }

    /// <summary>
    /// The factor a measured figure is multiplied by to state it on a standard day.
    ///
    /// One where no correction is asked for, so a caller can multiply
    /// unconditionally rather than branching.
    /// </summary>
    public static double Factor(PowerCorrection correction, Ambient air) => correction switch
    {
        PowerCorrection.SaeJ1349 => SaeJ1349(air),
        PowerCorrection.Din70020 => Din70020(air),
        _ => 1,
    };

    /// <summary>
    /// SAE J1349, against 990 mbar of dry air at 25 °C.
    ///
    /// The two constants are the standard's own and are not a density ratio: the
    /// 1.18 and the 0.18 between them assume the engine's friction does not fall
    /// with the air, so only part of the loss is recovered. That is why this
    /// reads lower than a plain density correction, and why it is the one most
    /// American figures are quoted against.
    ///
    /// The 273 rather than 273.15 is the standard's, kept deliberately: matching
    /// the published arithmetic matters more here than the fiftieth of a per cent
    /// it costs, because the point of quoting a standard is that two people get
    /// the same number from it.
    /// </summary>
    public static double SaeJ1349(Ambient air)
    {
        double dryMbar = DryPressureKpa(air) * 10;

        if (!(dryMbar > 0)) return double.NaN;

        return (1.180 * (990 / dryMbar) * Math.Sqrt((air.Celsius + 273) / 298)) - 0.18;
    }

    /// <summary>
    /// DIN 70020, against 1013 mbar of dry air at 20 °C.
    ///
    /// A plain density ratio, with no allowance for friction, referenced to a
    /// fuller and cooler standard day than SAE. Both of those push the same way,
    /// so a DIN figure is always the larger — by around three per cent on an
    /// ordinary day. A car advertised in DIN and dynoed in SAE has not lost
    /// anything.
    /// </summary>
    public static double Din70020(Ambient air)
    {
        double dryMbar = DryPressureKpa(air) * 10;

        if (!(dryMbar > 0)) return double.NaN;

        return 1013 / dryMbar * Math.Sqrt((air.Celsius + 273.15) / 293.15);
    }

    /// <summary>Whether SAE J1349 declares itself applicable at these conditions.</summary>
    public static bool WithinSaeLimits(Ambient air)
    {
        double cf = SaeJ1349(air);

        return double.IsFinite(cf) && cf >= SaeLowerLimit && cf <= SaeUpperLimit;
    }

    /// <summary>
    /// What to correct against, given what the engine is.
    ///
    /// Nothing, for a boosted engine, for the reason in the class summary. The
    /// factor is still there to be read; it is only not applied by default.
    /// </summary>
    public static PowerCorrection Recommended(bool boosted, Ambient air) =>
        boosted || !WithinSaeLimits(air) ? PowerCorrection.None : PowerCorrection.SaeJ1349;

    /// <summary>What to call a correction on a menu.</summary>
    public static string Name(PowerCorrection correction) => correction switch
    {
        PowerCorrection.SaeJ1349 => "SAE J1349",
        PowerCorrection.Din70020 => "DIN 70020",
        _ => "None",
    };

    /// <summary>
    /// A sentence saying what was and was not applied, for the footer of a sheet.
    ///
    /// A corrected figure with nothing next to it saying so is the single most
    /// common way dyno numbers mislead, so this is written to be shown rather
    /// than to be available.
    /// </summary>
    public static string Describe(PowerCorrection correction, Ambient air)
    {
        double sae = SaeJ1349(air);

        string conditions = $"{air}, air {KgPerCubicMetre(air):N3} kg/m³";

        if (correction == PowerCorrection.None)
        {
            return double.IsFinite(sae)
                ? $"Uncorrected — {conditions}. SAE J1349 would have been {sae:N3}."
                : $"Uncorrected — {conditions}.";
        }

        double factor = Factor(correction, air);
        string note = correction == PowerCorrection.SaeJ1349 && !WithinSaeLimits(air)
            ? " — outside the band the standard declares itself valid over"
            : string.Empty;

        return $"{Name(correction)} ×{factor:N3} — {conditions}{note}.";
    }
}
