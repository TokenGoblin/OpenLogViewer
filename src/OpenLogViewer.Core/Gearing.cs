using System.Globalization;
using System.Text.RegularExpressions;

namespace OpenLogViewer.Core;

/// <summary>
/// A tyre, as written on its sidewall.
/// </summary>
/// <param name="WidthMm">Section width, the first number.</param>
/// <param name="AspectPercent">Sidewall height as a percentage of the width, the second.</param>
/// <param name="RimInches">Wheel it fits, the last — and the one that is still in inches.</param>
public readonly record struct Tyre(double WidthMm, double AspectPercent, double RimInches)
{
    /// <summary>
    /// Overall diameter: the rim, plus a sidewall at the top and another at the
    /// bottom.
    ///
    /// The unit mixing is the sidewall's, not this file's — a 245/40R18 is 245
    /// millimetres wide on an eighteen inch wheel, and every tyre in the world
    /// is written that way.
    /// </summary>
    public double DiameterMm => (RimInches * 25.4) + (2 * WidthMm * AspectPercent / 100);

    public double DiameterInches => DiameterMm / 25.4;

    public override string ToString() =>
        $"{WidthMm:N0}/{AspectPercent:N0}R{RimInches:0.#}";
}

/// <summary>
/// What gear a car is in, how fast that is, and what the engine is doing about it.
///
/// The arithmetic is a chain of ratios and one circumference, and the only place
/// it is easy to get quietly wrong is the circumference: a rolling tyre is not a
/// circle of its own diameter. It squats under the car, so it travels rather
/// less than pi times its diameter per revolution — about three per cent less —
/// and a speed worked out from the geometry alone reads three per cent fast.
/// That is the same three per cent that makes a speedometer optimistic, and it
/// is worth having as an input rather than as a silent error.
/// </summary>
public static class Gearing
{
    /// <summary>
    /// How much less than its geometric circumference a loaded tyre actually
    /// travels, as a percentage.
    ///
    /// A convention rather than a measurement, and it varies with pressure, load
    /// and construction. Three per cent is where most published revolutions-per-
    /// mile figures sit against the geometry: a 245/40R18 measures 2,052 mm
    /// around and is quoted near 805 revolutions per mile, which is 1,999.
    /// </summary>
    public const double RollingDeflectionPercent = 3;

    public const double MmPerMile = 1_609_344;

    public const double MmPerKm = 1_000_000;

    /// <summary>
    /// The constant in the gear calculation everyone quotes: mph equals rpm times
    /// tyre diameter in inches, over ratio times final drive times 336.
    ///
    /// Kept only to check the arithmetic here against it, so it is computed
    /// rather than transcribed — 63,360 inches in a mile, divided by pi and by
    /// the sixty minutes in an hour. Writing down the 336.135 it is usually
    /// quoted as would make the cross-check agree to four figures instead of to
    /// all of them, and a check that only nearly agrees is not much of a check.
    ///
    /// Note what the rule assumes: that the tyre rolls its full geometric
    /// circumference, which is exactly the assumption this file declines to make.
    /// </summary>
    public static double ClassicMphConstant { get; } = 63_360 / (Math.PI * 60);

    private static readonly Regex SidewallPattern = new(
        @"^\s*[A-Z]{0,3}\s*(\d{2,3})\s*/\s*(\d{2,3})\s*(?:Z?R|-)\s*(\d{2}(?:\.\d)?)\s*$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>
    /// Reads a sidewall — "245/40R18", "P225/45ZR17", "245/40-18".
    /// </summary>
    public static bool TryParseTyre(string? text, out Tyre tyre)
    {
        tyre = default;

        if (string.IsNullOrWhiteSpace(text)) return false;

        Match m = SidewallPattern.Match(text);
        if (!m.Success) return false;

        tyre = new Tyre(
            double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture),
            double.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture),
            double.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture));

        return true;
    }

    /// <summary>
    /// What the tyre actually covers in one turn, in millimetres.
    /// </summary>
    public static double RollingCircumferenceMm(
        double diameterMm, double deflectionPercent = RollingDeflectionPercent)
    {
        if (!(diameterMm > 0)) return double.NaN;

        return Math.PI * diameterMm * (1 - (deflectionPercent / 100));
    }

    /// <summary>
    /// Road speed, in miles per hour.
    ///
    /// The chain: the engine turns, the gearbox divides it, the final drive
    /// divides it again, and whatever is left turns the tyre once per
    /// circumference of road.
    /// </summary>
    public static double Mph(
        double rpm, double gearRatio, double finalDrive, double rollingCircumferenceMm) =>
        MmPerMinute(rpm, gearRatio, finalDrive, rollingCircumferenceMm) * 60 / MmPerMile;

    public static double Kph(
        double rpm, double gearRatio, double finalDrive, double rollingCircumferenceMm) =>
        MmPerMinute(rpm, gearRatio, finalDrive, rollingCircumferenceMm) * 60 / MmPerKm;

    private static double MmPerMinute(
        double rpm, double gearRatio, double finalDrive, double rollingCircumferenceMm)
    {
        double drive = gearRatio * finalDrive;

        if (!(drive > 0) || !(rpm > 0) || !(rollingCircumferenceMm > 0)) return double.NaN;

        return rpm / drive * rollingCircumferenceMm;
    }

    /// <summary>What the engine is doing at a given road speed, which is the question in top gear.</summary>
    public static double RpmAt(
        double mph, double gearRatio, double finalDrive, double rollingCircumferenceMm)
    {
        if (!(mph > 0)) return double.NaN;

        double perRpm = Mph(1_000, gearRatio, finalDrive, rollingCircumferenceMm) / 1_000;

        return perRpm > 0 ? mph / perRpm : double.NaN;
    }

    /// <summary>One gear, and everything worth knowing about it.</summary>
    /// <param name="Gear">Its number, counting from one.</param>
    /// <param name="Ratio">Turns of the input per turn of the output.</param>
    /// <param name="Mph">Road speed at the redline.</param>
    /// <param name="Kph">The same in kilometres per hour.</param>
    /// <param name="MphPerThousandRpm">How long-legged the gear is.</param>
    /// <param name="RpmAfterUpshift">
    /// Where the engine lands on taking the next gear at the redline, which is
    /// what decides whether a shift drops it out of the power band.
    /// </param>
    /// <param name="RpmAtCruise">What it turns at the cruising speed asked for.</param>
    public readonly record struct GearStep(
        int Gear,
        double Ratio,
        double Mph,
        double Kph,
        double MphPerThousandRpm,
        double RpmAfterUpshift,
        double RpmAtCruise);

    /// <summary>
    /// Every gear, at the redline.
    /// </summary>
    /// <param name="ratios">Gear ratios in order, lowest gear first.</param>
    /// <param name="cruiseMph">A road speed to report engine speed at, alongside.</param>
    public static IReadOnlyList<GearStep> Table(
        IReadOnlyList<double> ratios,
        double finalDrive,
        double redlineRpm,
        double rollingCircumferenceMm,
        double cruiseMph = 0)
    {
        if (ratios.Count == 0 || !(finalDrive > 0) || !(redlineRpm > 0)) return [];

        List<GearStep> steps = [];

        for (int i = 0; i < ratios.Count; i++)
        {
            double ratio = ratios[i];
            if (!(ratio > 0)) continue;

            // Taking the next gear at the redline drops the engine in proportion
            // to how much taller that gear is. Nothing about the car matters here
            // but the two ratios.
            double afterUpshift = i + 1 < ratios.Count && ratios[i + 1] > 0
                ? redlineRpm * ratios[i + 1] / ratio
                : double.NaN;

            steps.Add(new GearStep(
                i + 1,
                ratio,
                Mph(redlineRpm, ratio, finalDrive, rollingCircumferenceMm),
                Kph(redlineRpm, ratio, finalDrive, rollingCircumferenceMm),
                Mph(1_000, ratio, finalDrive, rollingCircumferenceMm),
                afterUpshift,
                cruiseMph > 0
                    ? RpmAt(cruiseMph, ratio, finalDrive, rollingCircumferenceMm)
                    : double.NaN));
        }

        return steps;
    }

    /// <summary>
    /// The fastest the gearing will let the car go, which is rarely the fastest
    /// it actually goes.
    ///
    /// This is the tallest gear at the redline and nothing else — it assumes the
    /// engine can still pull that gear against the air at that speed, which above
    /// about a hundred and fifty miles an hour is a large assumption. A car
    /// geared past what its power can push simply never reaches the redline in
    /// top, and its real top speed is set by drag rather than by this number.
    /// </summary>
    public static double GearedTopSpeedMph(
        IReadOnlyList<double> ratios, double finalDrive, double redlineRpm, double rollingCircumferenceMm)
    {
        IReadOnlyList<GearStep> steps =
            Table(ratios, finalDrive, redlineRpm, rollingCircumferenceMm);

        return steps.Count > 0 ? steps.Max(s => s.Mph) : double.NaN;
    }

    /// <summary>
    /// The gearing table as the chart the tab shows.
    /// </summary>
    public static string Chart(IReadOnlyList<GearStep> steps, bool withCruise)
    {
        List<string> lines =
        [
            withCruise
                ? "gear   ratio     mph    km/h  /1000    next  @cruise"
                : "gear   ratio     mph    km/h  /1000    next",
        ];

        foreach (GearStep s in steps)
        {
            string next = double.IsNaN(s.RpmAfterUpshift) ? "—" : s.RpmAfterUpshift.ToString("N0");
            string cruise = double.IsNaN(s.RpmAtCruise) ? "—" : s.RpmAtCruise.ToString("N0");

            lines.Add(
                $"{s.Gear,4}  {s.Ratio,6:N3}  {s.Mph,6:N0}  {s.Kph,6:N0}  {s.MphPerThousandRpm,5:N1}  {next,6}"
                + (withCruise ? $"  {cruise,7}" : string.Empty));
        }

        return string.Join(Environment.NewLine, lines);
    }
}
