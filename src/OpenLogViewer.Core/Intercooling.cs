namespace OpenLogViewer.Core;

/// <summary>
/// What a compressor does to the air, and what it costs to undo.
///
/// Compressing air heats it, and hot air is thin — so a turbocharger delivering
/// 20 psi of hot air delivers considerably less oxygen than 20 psi suggests, and
/// the engine also gets closer to knocking. An intercooler exists to take that
/// heat back out, and every figure here is one of the two questions worth asking
/// about one: how much heat is there to move, and how much cooler does the charge
/// actually end up.
/// </summary>
public static class ChargeAir
{
    /// <summary>Specific heat of air at constant pressure, BTU per pound per °F.</summary>
    public const double CpAir = 0.24;

    /// <summary>
    /// (γ−1)/γ for air, the exponent in the compression temperature rise.
    ///
    /// γ is 1.4 for a diatomic gas, so this is 0.4/1.4. It is what makes the
    /// temperature rise depend on pressure <em>ratio</em> rather than on boost:
    /// the first ten psi cost far less heat than the second ten.
    /// </summary>
    public const double GammaExponent = 0.4 / 1.4;

    /// <summary>Absolute zero on the Fahrenheit scale, for anything that needs Rankine.</summary>
    public const double RankineOffset = 459.67;

    /// <summary>Sea level, near enough, in psi absolute.</summary>
    public const double StandardBaroPsi = 14.696;

    /// <summary>
    /// Air out of the compressor, before anything has cooled it.
    ///
    /// The ideal rise is what perfect compression would cost; a real compressor
    /// is worse than that and the shortfall all becomes heat, which is why the
    /// rise is divided by efficiency rather than multiplied. A compressor at 70%
    /// puts in roughly half again the temperature rise of a perfect one.
    /// </summary>
    /// <param name="efficiency">Isentropic efficiency, 0 to 1 — 0.70 is ordinary, 0.78 is good.</param>
    public static double CompressorOutletF(double inletF, double pressureRatio, double efficiency)
    {
        if (pressureRatio < 1 || efficiency is <= 0 or > 1) return double.NaN;

        double inletR = inletF + RankineOffset;
        double idealRise = inletR * (Math.Pow(pressureRatio, GammaExponent) - 1);

        return inletF + (idealRise / efficiency);
    }

    /// <summary>Pressure ratio from gauge boost and the barometer.</summary>
    public static double PressureRatio(double boostPsi, double baroPsi = StandardBaroPsi) =>
        baroPsi > 0 ? (boostPsi + baroPsi) / baroPsi : double.NaN;

    /// <summary>
    /// Heat that has to leave the charge, in BTU per minute.
    ///
    /// Mass flow times specific heat times the drop. The whole of the air-side
    /// arithmetic is this one line; everything else on the page is about whether
    /// a given core can actually move it.
    /// </summary>
    public static double HeatLoadBtuPerMin(double airLbPerMin, double dropF) =>
        airLbPerMin > 0 && dropF > 0 ? airLbPerMin * CpAir * dropF : double.NaN;

    /// <summary>
    /// What comes out, given how good the core is.
    ///
    /// Effectiveness is the fraction of the available temperature difference the
    /// core actually takes: 1.0 would leave the charge at ambient, which no core
    /// does. A good air-to-air runs about 0.70, and the returns fall away sharply
    /// — doubling a well-sized core buys a few points, not another seventy.
    /// </summary>
    public static double OutletF(double inletF, double ambientF, double effectiveness) =>
        effectiveness is >= 0 and <= 1
            ? inletF - (effectiveness * (inletF - ambientF))
            : double.NaN;

    /// <summary>What a core is achieving, from temperatures actually measured.</summary>
    public static double EffectivenessFrom(double inletF, double outletF, double ambientF) =>
        Math.Abs(inletF - ambientF) > 0.001
            ? (inletF - outletF) / (inletF - ambientF)
            : double.NaN;

    /// <summary>
    /// How much denser the charge is after cooling, at the same pressure.
    ///
    /// The point of the exercise, and the number that turns degrees into power.
    /// Density goes as absolute temperature, so cooling 250 °F air to 120 °F is
    /// about a fifth more oxygen through the same valves — worth roughly a fifth
    /// more torque, before anything is done about the timing the cooler charge now
    /// tolerates.
    /// </summary>
    public static double DensityRatio(double fromF, double toF)
    {
        double from = fromF + RankineOffset;
        double to = toF + RankineOffset;

        return to > 0 && from > 0 ? from / to : double.NaN;
    }

    /// <summary>
    /// Pressure lost across the core, as a fraction of what went in.
    ///
    /// Worth setting against the cooling rather than admiring the cooling alone.
    /// A core that drops 2 psi out of 20 has given back a tenth of the boost to
    /// buy its temperature drop, and on a well-matched turbocharger that trade is
    /// not always a good one.
    /// </summary>
    public static double PressureLossFraction(double dropPsi, double boostPsi, double baroPsi = StandardBaroPsi) =>
        boostPsi + baroPsi > 0 ? dropPsi / (boostPsi + baroPsi) : double.NaN;
}

/// <summary>A core's outside dimensions, in inches.</summary>
public sealed record IntercoolerCore(double WidthIn, double HeightIn, double ThicknessIn)
{
    /// <summary>What the oncoming air sees.</summary>
    public double FrontalAreaSqIn => WidthIn * HeightIn;

    public double VolumeCuIn => WidthIn * HeightIn * ThicknessIn;

    public bool IsUsable => WidthIn > 0 && HeightIn > 0 && ThicknessIn > 0;

    /// <summary>
    /// Heat the core is being asked to move per cubic inch of itself.
    ///
    /// Not a pass or a fail — there is no honest way to predict a core's
    /// effectiveness from its outside dimensions, because that depends on the fin
    /// density, the internal geometry and how much air is actually reaching it.
    /// What it is good for is comparing two candidates: the same duty spread over
    /// half the core is twice the loading, and the smaller one will run hotter.
    /// </summary>
    public double LoadingPerCuIn(double heatBtuPerMin) =>
        IsUsable && heatBtuPerMin > 0 ? heatBtuPerMin / VolumeCuIn : double.NaN;

    public double LoadingPerFrontalSqIn(double heatBtuPerMin) =>
        IsUsable && heatBtuPerMin > 0 ? heatBtuPerMin / FrontalAreaSqIn : double.NaN;
}

/// <summary>What a core is made of, and how little that turns out to matter.</summary>
public sealed record CoreMaterial(
    string Name, double ConductivityWmK, double RelativeDensity, string Note);

/// <summary>
/// Core materials, and an honest account of what choosing between them does.
///
/// Very little, thermally. A heat exchanger's resistance is overwhelmingly on
/// the air side — the film of slow-moving air clinging to the fins — and the
/// metal between the two streams is a fraction of a millimetre of something that
/// conducts hundreds of times better than that film. Doubling the conductivity of
/// the metal changes the total resistance by a fraction of a per cent, which is
/// why copper cores are not twice as good as aluminium ones and why nobody sells
/// them.
///
/// What the material actually decides is weight, cost, corrosion and whether the
/// thing can be brazed at all. Those are real reasons to choose; heat transfer is
/// not one, and a calculator that offered a performance multiplier per material
/// would be inventing a difference that is not there.
/// </summary>
public static class CoreMaterials
{
    public static IReadOnlyList<CoreMaterial> All { get; } =
    [
        new("Aluminium", 205, 1.0,
            "What essentially every intercooler is. Light, cheap, brazes well, and "
            + "conducts far better than the air film that is actually holding things up."),

        new("Copper / brass", 385, 3.3,
            "Nearly twice the conductivity and more than three times the weight. The "
            + "conductivity buys almost nothing, because the metal was never the "
            + "restriction; the weight is charged in full."),

        new("Stainless steel", 16, 2.9,
            "An eighth of aluminium's conductivity and three times its weight. Chosen "
            + "for corrosion or pressure, never for cooling."),
    ];

    /// <summary>
    /// The share of the total resistance that is the metal, for a wall this thick.
    ///
    /// The figure that settles the argument. With a typical air-side coefficient a
    /// half-millimetre aluminium wall is well under one per cent of the resistance
    /// between the two streams, so the metal is not what is stopping the heat.
    /// </summary>
    /// <param name="wallMm">Wall thickness in millimetres — 0.3 to 0.8 is usual.</param>
    /// <param name="airSideWm2K">
    /// Air-side heat transfer coefficient, W/m²K. Forced convection over finned
    /// surfaces at road speed is roughly 50 to 150.
    /// </param>
    public static double MetalShareOfResistance(
        CoreMaterial material, double wallMm = 0.5, double airSideWm2K = 100)
    {
        ArgumentNullException.ThrowIfNull(material);

        if (material.ConductivityWmK <= 0 || wallMm <= 0 || airSideWm2K <= 0) return double.NaN;

        // Per square metre: the wall's resistance against the two air films.
        double wall = wallMm / 1000 / material.ConductivityWmK;
        double films = 2 / airSideWm2K;

        return wall / (wall + films);
    }
}
