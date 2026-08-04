namespace OpenLogViewer.Core;

/// <summary>
/// One of the published power-to-weight correlations.
/// </summary>
/// <param name="Name">Whose it is.</param>
/// <param name="EtConstant">Multiplies the cube root of weight over power.</param>
/// <param name="MphConstant">Multiplies the cube root of power over weight.</param>
/// <param name="Note">What sort of run it assumes.</param>
public readonly record struct DragFormula(
    string Name, double EtConstant, double MphConstant, string Note)
{
    public override string ToString() => Name;
}

/// <summary>What a timeslip says about the car that made it.</summary>
/// <param name="PowerFromTrap">Power the trap speed implies.</param>
/// <param name="PowerFromEt">Power the elapsed time implies.</param>
/// <param name="EtTheTrapDeserved">The time that trap speed should have come with.</param>
/// <param name="LaunchCost">
/// Seconds the run lost against the time its trap speed deserved. Positive means
/// the car went slower than its own speed says it should have.
/// </param>
public readonly record struct SlipReading(
    double PowerFromTrap,
    double PowerFromEt,
    double EtTheTrapDeserved,
    double LaunchCost);

/// <summary>
/// Elapsed time and trap speed against power and weight.
///
/// These are correlations fitted to thousands of real runs, not physics. There
/// is no term here for traction, for how the car leaves, for gearing, for the
/// air, or for the driver — all of which move a quarter-mile time by more than
/// any of the differences between the three formulas below. Treat every number
/// as the middle of a wide distribution.
///
/// The distinction worth understanding is between the two figures, because they
/// are not equally trustworthy. Trap speed is very nearly a measure of power to
/// weight: by the time a car is at the far end it has had a quarter of a mile to
/// forget a bad launch, so the speed it arrives at depends mostly on what is
/// under the bonnet. Elapsed time does not forget — a wheelspin in the first
/// sixty feet is in the time for good.
///
/// That is what makes a timeslip diagnostic rather than merely a score. Read
/// power from the trap; then ask what time that trap ought to have come with. If
/// the car ran slower than that, the difference is the launch, and the fix is at
/// the start line rather than in the tune.
/// </summary>
public static class DragStrip
{
    /// <summary>
    /// The three published correlations, quickest first.
    ///
    /// They differ by about eight per cent in time and four in speed, which is
    /// not disagreement so much as different assumptions about how well the car
    /// leaves. Hale describes a run that hooked up; Huntington one that did not
    /// much. A street car on street tyres usually lands nearer Huntington, and a
    /// car that trips Hale's numbers is launching properly.
    /// </summary>
    public static IReadOnlyList<DragFormula> Formulas { get; } =
    [
        new("Hale", 5.825, 234,
            "a run that hooked up — sticky tyres and a launch that worked"),

        new("Fox", 6.269, 230,
            "between the two, and the usual middle answer"),

        new("Huntington", 6.290, 224,
            "the oldest and most forgiving — where a street car on street tyres lands"),
    ];

    /// <summary>The middle formula, for anyone who has no reason to prefer one.</summary>
    public static DragFormula Default => Formulas[1];

    /// <summary>
    /// How much longer the quarter takes than the eighth.
    ///
    /// Published as anything from 1.55 to 1.57 and used here at the middle of
    /// that. Note it is not the 1.414 that constant acceleration would give: a
    /// car covers the second half of the strip with less acceleration left than
    /// the first, because drag has grown and the gearing has run out. The gap
    /// between 1.414 and 1.57 is exactly that falling-away.
    /// </summary>
    public const double QuarterOverEighthEt = 1.5657;

    /// <summary>
    /// How much faster the car is going at the quarter than at the eighth.
    ///
    /// Shakier than the time ratio and quoted more loosely — anywhere from 1.25
    /// to 1.28, because how much speed is left to gain depends on where the
    /// power runs out.
    /// </summary>
    public const double QuarterOverEighthMph = 1.27;

    /// <summary>Elapsed time over the quarter, in seconds.</summary>
    public static double QuarterEt(double horsepower, double weightLb, DragFormula formula) =>
        Usable(horsepower, weightLb)
            ? formula.EtConstant * Math.Cbrt(weightLb / horsepower)
            : double.NaN;

    /// <summary>Speed at the far end of the quarter, in miles per hour.</summary>
    public static double QuarterMph(double horsepower, double weightLb, DragFormula formula) =>
        Usable(horsepower, weightLb)
            ? formula.MphConstant * Math.Cbrt(horsepower / weightLb)
            : double.NaN;

    public static double EighthEt(double horsepower, double weightLb, DragFormula formula) =>
        QuarterEt(horsepower, weightLb, formula) / QuarterOverEighthEt;

    public static double EighthMph(double horsepower, double weightLb, DragFormula formula) =>
        QuarterMph(horsepower, weightLb, formula) / QuarterOverEighthMph;

    /// <summary>
    /// The power a trap speed implies.
    ///
    /// The reliable direction, and the reason to care about trap speed at all.
    /// </summary>
    public static double HorsepowerFromTrap(double mph, double weightLb, DragFormula formula) =>
        mph > 0 && weightLb > 0 && formula.MphConstant > 0
            ? weightLb * Math.Pow(mph / formula.MphConstant, 3)
            : double.NaN;

    /// <summary>
    /// The power an elapsed time implies.
    ///
    /// Much less to be trusted than the trap. A time carries the launch in it, so
    /// this understates a car that spun and flatters one that left perfectly —
    /// which is the whole of why the two are compared rather than averaged.
    /// </summary>
    public static double HorsepowerFromEt(double seconds, double weightLb, DragFormula formula) =>
        seconds > 0 && weightLb > 0
            ? weightLb * Math.Pow(formula.EtConstant / seconds, 3)
            : double.NaN;

    /// <summary>
    /// What a timeslip says, reading the trap for power and the time for the
    /// launch.
    /// </summary>
    public static SlipReading Read(
        double trapMph, double seconds, double weightLb, DragFormula formula)
    {
        double fromTrap = HorsepowerFromTrap(trapMph, weightLb, formula);
        double fromEt = HorsepowerFromEt(seconds, weightLb, formula);

        double deserved = QuarterEt(fromTrap, weightLb, formula);

        return new SlipReading(
            fromTrap,
            fromEt,
            deserved,
            double.IsNaN(deserved) || seconds <= 0 ? double.NaN : seconds - deserved);
    }

    /// <summary>
    /// What a launch is worth saying about.
    ///
    /// A tenth either way is the formula's own scatter and means nothing. Past
    /// about three tenths the car is leaving badly enough that it is the first
    /// thing to fix, and no amount of tuning will show up until it is.
    /// </summary>
    public const double LaunchWorthMentioning = 0.30;

    private static bool Usable(double horsepower, double weightLb) =>
        horsepower > 0 && weightLb > 0;
}
