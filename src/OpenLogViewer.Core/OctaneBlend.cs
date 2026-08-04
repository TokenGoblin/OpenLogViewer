namespace OpenLogViewer.Core;

/// <summary>What gets blended into petrol to raise its octane.</summary>
public enum Blendstock
{
    Ethanol,
    Methanol,
    E85,
}

/// <summary>
/// What a blend came out at.
/// </summary>
/// <param name="Ron">Research octane number, which is what most of the world quotes.</param>
/// <param name="Mon">Motor octane number, measured hotter and harder.</param>
/// <param name="AntiKnockIndex">The average of the two, which is what a US pump quotes.</param>
/// <param name="EthanolByVolume">
/// Alcohol in the finished mixture, which is not the number on the jug when the
/// jug held E85.
/// </param>
/// <param name="AlcoholMoleFraction">
/// The same quantity by molecule, which is the one the octane actually follows.
/// </param>
/// <param name="HeatOfVaporisationKjPerKg">Energy the fuel takes to evaporate.</param>
/// <param name="CoolingKjPerKgAir">
/// The same, per kilogram of air it evaporates into — the figure that describes
/// what the charge actually feels.
/// </param>
public readonly record struct OctaneResult(
    double Ron,
    double Mon,
    double AntiKnockIndex,
    double EthanolByVolume,
    double AlcoholMoleFraction,
    double HeatOfVaporisationKjPerKg,
    double CoolingKjPerKgAir);

/// <summary>
/// What blending alcohol into petrol does to its octane, and to how cold it runs
/// the charge.
///
/// The thing everyone notices about ethanol is that the first splash of it is
/// worth far more octane than the last, so that ten per cent by volume in an
/// 88 RON blendstock buys four and a half points while the ninety per cent after
/// it buys sixteen. That looks like a mysterious non-linearity and is not one.
///
/// Octane blends very nearly linearly by <em>molecule</em>. Ethanol's molar mass
/// is 46 against petrol's 105, and it is denser, so ten per cent of the volume is
/// twenty-one per cent of the molecules. Convert to a mole fraction first and the
/// curve straightens out — which is Anderson and colleagues' result (SAE
/// 2012-01-1274, and in Energy &amp; Fuels for methanol as well), and the reason
/// the volumetric "blending octane number" of ethanol is quoted anywhere between
/// 110 and 135 depending only on how much of it the author was blending.
///
/// Checked against published measurements rather than against the model
/// restated: on an 88 RON blendstock this gives 92.4 at E10, 94.3 at E15 and 98.7
/// at E30, where the measured figures are 92.4 to 92.5, 94.3 and 98.6.
/// </summary>
public static class OctaneBlend
{
    /// <summary>
    /// Average molar mass of petrol, which is a distribution rather than a
    /// number: the literature puts it between 100 and 120 g/mol depending on the
    /// crude and the season.
    ///
    /// 105 is used here because it is what fits the published blend
    /// measurements, and it sits inside that range rather than being reached for
    /// outside it.
    /// </summary>
    public const double PetrolMolarMass = 105;

    public const double EthanolMolarMass = 46.068;

    public const double MethanolMolarMass = 32.042;

    // Neat octane numbers, which is what the molar model wants. A volumetric
    // blending octane number is not a property of the alcohol at all — it is a
    // property of the alcohol and the amount of it, and belongs nowhere near a
    // calculation like this.
    public const double EthanolRon = 109;

    public const double EthanolMon = 90;

    public const double MethanolRon = 109;

    public const double MethanolMon = 89;

    /// <summary>
    /// Heat of vaporisation in kilojoules per kilogram.
    ///
    /// Petrol is a range rather than a figure — published values run from about
    /// 305 to 350 depending on the blend and how volatile it is — so everything
    /// downstream of it is worth about a tenth either way. The alcohols are
    /// compounds and are much better pinned: ethanol near 840, methanol near
    /// 1,100, which is where the three-times and four-times comparisons come
    /// from.
    /// </summary>
    public const double PetrolHov = 350;

    public const double EthanolHov = 840;

    public const double MethanolHov = 1_100;

    /// <summary>How much of E85 is actually ethanol, by volume.</summary>
    public const double E85EthanolFraction = 0.85;

    /// <summary>The base octanes a pump offers, as the rows of the chart.</summary>
    public static IReadOnlyList<double> PumpGrades { get; } = [85, 87, 89, 91, 93, 95];

    /// <summary>The blend fractions worth showing, as the columns of it.</summary>
    public static IReadOnlyList<double> ChartFractions { get; } =
        [0, 0.10, 0.20, 0.30, 0.50, 0.70, 0.85, 1.00];

    /// <summary>
    /// Sensitivity worth assuming: how far RON runs ahead of MON on pump petrol.
    ///
    /// A convention, and the one figure here that an octane number alone cannot
    /// tell you. A US pump quotes the average of the two, so 91 at the pump is
    /// 95 RON and 87 MON at a sensitivity of eight — and the same 91 is a
    /// different pair of numbers on a fuel with a different sensitivity, which
    /// changes what the blend comes out at.
    /// </summary>
    public const double TypicalSensitivity = 8;

    public static string Name(Blendstock stock) => stock switch
    {
        Blendstock.Ethanol => "Ethanol (E100)",
        Blendstock.Methanol => "Methanol",
        Blendstock.E85 => "E85",
        _ => stock.ToString(),
    };

    /// <summary>
    /// The octane and the charge cooling of petrol blended with an alcohol.
    /// </summary>
    /// <param name="baseAntiKnockIndex">What the pump said, before anything was added.</param>
    /// <param name="sensitivity">RON minus MON of the base petrol.</param>
    /// <param name="byVolume">
    /// How much of the finished mixture is the blendstock — so blending E85 at
    /// 0.5 leaves a mixture that is 42.5 per cent ethanol, not 85.
    /// </param>
    public static OctaneResult Blend(
        double baseAntiKnockIndex, double sensitivity, Blendstock stock, double byVolume)
    {
        if (double.IsNaN(baseAntiKnockIndex) || double.IsNaN(sensitivity) || double.IsNaN(byVolume))
            return default;

        double fraction = Math.Clamp(byVolume, 0, 1);

        // E85 is not a blendstock so much as a pre-made blend, so it is reduced
        // to the alcohol it carries and the petrol that came with it.
        double alcohol = stock == Blendstock.E85 ? fraction * E85EthanolFraction : fraction;
        double petrol = 1 - alcohol;

        (double molarMass, double ron, double mon, double hov, double density, double stoich) =
            stock == Blendstock.Methanol
                ? (MethanolMolarMass, MethanolRon, MethanolMon, MethanolHov,
                   TuningMath.Density(Fuel.Methanol), TuningMath.Stoichiometric(Fuel.Methanol))
                : (EthanolMolarMass, EthanolRon, EthanolMon, EthanolHov,
                   TuningMath.Density(Fuel.Ethanol), TuningMath.Stoichiometric(Fuel.Ethanol));

        double petrolDensity = TuningMath.Density(Fuel.Petrol);

        // ----- octane, by molecule rather than by volume -------------------------

        double alcoholMoles = alcohol * density / molarMass;
        double petrolMoles = petrol * petrolDensity / PetrolMolarMass;
        double totalMoles = alcoholMoles + petrolMoles;

        double x = totalMoles > 0 ? alcoholMoles / totalMoles : 0;

        double baseRon = baseAntiKnockIndex + (sensitivity / 2);
        double baseMon = baseAntiKnockIndex - (sensitivity / 2);

        double blendRon = (x * ron) + ((1 - x) * baseRon);
        double blendMon = (x * mon) + ((1 - x) * baseMon);

        // ----- cooling, by mass --------------------------------------------------

        double alcoholMass = alcohol * density;
        double petrolMass = petrol * petrolDensity;
        double totalMass = alcoholMass + petrolMass;

        double blendHov = totalMass > 0
            ? ((alcoholMass * hov) + (petrolMass * PetrolHov)) / totalMass
            : PetrolHov;

        double blendStoich = totalMass > 0
            ? ((alcoholMass * stoich) + (petrolMass * TuningMath.Stoichiometric(Fuel.Petrol))) / totalMass
            : TuningMath.Stoichiometric(Fuel.Petrol);

        return new OctaneResult(
            blendRon,
            blendMon,
            (blendRon + blendMon) / 2,
            alcohol,
            x,
            blendHov,
            blendStoich > 0 ? blendHov / blendStoich : double.NaN);
    }

    /// <summary>
    /// Cooling of neat petrol, as the thing every other figure is compared to.
    /// </summary>
    public static double PetrolCoolingKjPerKgAir =>
        PetrolHov / TuningMath.Stoichiometric(Fuel.Petrol);

    /// <summary>
    /// The chart: what each pump grade becomes at each blend fraction.
    ///
    /// Anti-knock index, because that is the number on the pump the base grade
    /// came from. Europe quotes RON, where these same fuels read four or five
    /// points higher.
    ///
    /// No sensitivity argument, and not by oversight. Splitting a grade into RON
    /// and MON adds a half-sensitivity to one and takes it off the other, and
    /// averaging them back together cancels it exactly — so the blended index
    /// does not depend on the sensitivity assumed for the base fuel at all. It
    /// falls out of the arithmetic rather than being arranged, and it is worth
    /// having: the chart rests on one fewer figure that nobody knows.
    ///
    /// Sensitivity does still decide the blend's own RON and MON separately,
    /// which is why <see cref="Blend"/> asks for it.
    /// </summary>
    public static string Chart(Blendstock stock)
    {
        List<string> lines =
        [
            "base" + string.Concat(ChartFractions.Select(f => $"{f * 100,7:N0}%")),
        ];

        foreach (double grade in PumpGrades)
        {
            string row = string.Concat(ChartFractions.Select(
                f => $"{Blend(grade, TypicalSensitivity, stock, f).AntiKnockIndex,8:N1}"));

            lines.Add($"{grade,4:N0}{row}");
        }

        return string.Join(Environment.NewLine, lines);
    }

    /// <summary>
    /// The other half of the chart: what the blend does to charge cooling, which
    /// does not depend on the base grade and so is one row rather than six.
    /// </summary>
    public static string CoolingChart(Blendstock stock)
    {
        OctaneResult[] row = [.. ChartFractions.Select(f => Blend(91, TypicalSensitivity, stock, f))];

        return string.Join(Environment.NewLine,
        [
            "     " + string.Concat(ChartFractions.Select(f => $"{f * 100,7:N0}%")),
            "kJ/kg" + string.Concat(row.Select(r => $"{r.HeatOfVaporisationKjPerKg,8:N0}")),
            "/air " + string.Concat(row.Select(r => $"{r.CoolingKjPerKgAir,8:N0}")),
            "×    " + string.Concat(row.Select(
                r => $"{r.CoolingKjPerKgAir / PetrolCoolingKjPerKgAir,8:N2}")),
        ]);
    }
}
