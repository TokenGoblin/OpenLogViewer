namespace OpenLogViewer.Core;

/// <summary>How one candidate gear fared.</summary>
/// <param name="Gear">The gear counting from one.</param>
/// <param name="Ratio">
/// What road load says the engine made in this gear, over what the air says it
/// made. One is agreement.
/// </param>
/// <param name="RoadLoadHp">
/// Peak power in this gear, at the crank. Context only — the ratio is taken across
/// the whole shared range and not from this figure, and on a turbocharged pull the
/// two routes peak hundreds of rpm apart.
/// </param>
public readonly record struct GearCandidate(int Gear, double Ratio, double RoadLoadHp)
{
    /// <summary>
    /// How far from agreement, measured so that too much and too little count the
    /// same.
    ///
    /// The log of the ratio, not its distance from one. Half and double are the
    /// same factor apart, but as plain distances they score 0.5 and 1.0 — which
    /// quietly prefers whichever gear reads low, and on a five-speed the gears
    /// below the right one always read low.
    /// </summary>
    public double Off => Ratio > 0 ? Math.Abs(Math.Log(Ratio)) : double.PositiveInfinity;

    /// <summary>The same, as a percentage, for saying out loud.</summary>
    public double OffPercent => Math.Abs(Ratio - 1);
}

/// <summary>What the two methods concluded about which gear a pull was in.</summary>
public sealed record GearVerdict
{
    /// <summary>The gear that reconciles them, or zero if none does convincingly.</summary>
    public required int Gear { get; init; }

    /// <summary>Every gear tried, in order.</summary>
    public required IReadOnlyList<GearCandidate> Candidates { get; init; }

    /// <summary>What the air said the engine made, at the crank.</summary>
    public required double SpeedDensityHp { get; init; }

    public required string Summary { get; init; }

    public bool Resolved => Gear > 0;

    /// <summary>The candidate that won, whether or not it convinced.</summary>
    public GearCandidate Best =>
        Candidates.Count == 0 ? default : Candidates.MinBy(c => c.Off);

    public override string ToString() => Summary;
}

/// <summary>
/// Working out which gear a pull was made in, on a car that cannot say.
///
/// <para>
/// A road speed sensor settles it, and a great many cars have never had one
/// wired: across twenty logs off one MegaSquirt the channel exists, reads a flat
/// zero all session, and has clearly never been connected to anything. Without
/// it, road load is stuck — it needs the gear to know what mass it is
/// accelerating, and the answer moves by a factor of eleven between first and
/// fifth. Nothing in the recording says which.
/// </para>
/// <para>
/// Except that something does, indirectly. The air the manifold implies is a
/// completely separate route to the same horsepower, and it has never heard of
/// the gearbox. Take the gear road load was told, and the two figures either
/// agree or they do not. Only one gear makes them agree, because adjacent gears
/// are the better part of twice as far apart in implied power as the two methods
/// are in accuracy.
/// </para>
/// <para>
/// <b>That gap is why this works despite resting on guesses.</b> Both the
/// volumetric efficiency and the fuel consumption behind the air figure are
/// assumed, so the number it produces is worth perhaps a quarter either way. A
/// quarter cannot move you a gear. On the three longest pulls in that same
/// folder — a car with no speed sensor at all — this lands on third gear at nine
/// per cent, one per cent, and nought.
/// </para>
/// <para>
/// It resolves the gear. It does <em>not</em> make the horsepower any better
/// known than the assumptions behind it, and where two gears both come close it
/// says so rather than choosing.
/// </para>
/// </summary>
public static class GearAgreement
{
    /// <summary>
    /// How near one is near enough to be worth calling, as a log ratio.
    ///
    /// About a third either way, which is roughly what the volumetric efficiency
    /// and the fuel consumption can be wrong by between them. Past that the two
    /// methods are not describing the same event and the gear is not the thing to
    /// fix.
    /// </summary>
    public const double CloseEnough = 0.30;

    /// <summary>
    /// How near the winner has to fit, as a share of the distance to its
    /// neighbour.
    ///
    /// <para>
    /// Against this gearbox's own spacing rather than against a fixed figure,
    /// because the spacing is what makes the answer possible at all and it
    /// differs from car to car. Road load scales with roughly the square of the
    /// gear ratio, so on the five-speed these were written against, first and
    /// second imply powers 2.4 times apart while fourth and fifth are only 1.6 —
    /// the top of a close-ratio box is far harder to call than the bottom of a
    /// wide one, and one threshold would be too strict at one end and too loose
    /// at the other.
    /// </para>
    /// <para>
    /// Half, so the winner has to sit nearer its own gear than the midpoint
    /// between that gear and the next. Past the midpoint the two routes disagree
    /// by more than the thing being measured, and the nearest gear is only the
    /// smaller rounding error.
    /// </para>
    /// </summary>
    public const double WithinSpacing = 0.5;

    /// <summary>
    /// Which gear reconciles the two methods on this pull.
    /// </summary>
    public static GearVerdict Resolve(
        LogDocument log,
        VehicleSpec vehicle,
        EngineSpec engine,
        DynoPull pull,
        Ambient air,
        DynoSettings? settings = null)
    {
        ArgumentNullException.ThrowIfNull(log);
        ArgumentNullException.ThrowIfNull(vehicle);
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentNullException.ThrowIfNull(pull);

        // A pull that is too brief or too narrow to be a curve is too brief and
        // narrow to settle a gear from either. The two methods would be compared
        // over a handful of rungs at one end of the rev range, where they are
        // least alike even when everything is right.
        foreach (PullFault fault in pull.Faults)
        {
            if (fault is not (PullFault.TooShort or PullFault.TooNarrow or PullFault.TooSlowlySampled))
            {
                continue;
            }

            return None(
                $"This pull is not enough to settle a gear from: {DynoRun.Describe(fault)}.");
        }

        DynoCurve fromAir = DynoCurve.FromSpeedDensity(log, engine, pull, air, settings: settings);

        if (fromAir.IsEmpty)
        {
            return None(
                "The air route could not be worked out on this log, so there is nothing to hold the "
                + $"gears against. {fromAir.Basis}");
        }

        double target = fromAir.PeakPower.Horsepower;

        if (!(target > 0)) return None("The air route came to nothing on this pull.");

        List<GearCandidate> candidates = [];

        for (int gear = 1; gear <= vehicle.GearRatios.Count; gear++)
        {
            DynoPull assumed = pull with
            {
                Gearing = pull.Gearing with { Gear = gear },
                Speed = SpeedSource.EngineSpeedAndGear,
            };

            DynoCurve fromRoad = DynoCurve
                .FromRoadLoad(log, vehicle, assumed, air, settings: settings)
                .At(PowerReference.Crank, vehicle);

            if (fromRoad.IsEmpty) continue;

            double ratio = Agreement(fromRoad, fromAir);

            if (!(ratio > 0)) continue;

            candidates.Add(new GearCandidate(gear, ratio, fromRoad.PeakPower.Horsepower));
        }

        if (candidates.Count == 0)
        {
            return None("Road load could not be worked out in any gear on this pull.")
                with { SpeedDensityHp = target };
        }

        GearCandidate[] ranked = [.. candidates.OrderBy(c => c.Off)];
        GearCandidate best = ranked[0];

        string peaks =
            $"(peaks, both at the crank: air {target:N0} hp, road load {{0:N0}})";

        if (best.Off > CloseEnough)
        {
            return new GearVerdict
            {
                Gear = 0,
                Candidates = candidates,
                SpeedDensityHp = target,
                Summary =
                    $"No gear reconciles the two. The nearest is {best.Gear}, where road load runs at "
                    + $"{best.Ratio:P0} of what the air says across the range they share — "
                    + $"{best.OffPercent:P0} out. Either the gearing, the mass, or the engine's "
                    + $"displacement is not what was entered. "
                    + string.Format(peaks, best.RoadLoadHp),
            };
        }

        double spacing = SpacingAround(vehicle, best.Gear);

        if (double.IsFinite(spacing) && best.Off > spacing * WithinSpacing)
        {
            return new GearVerdict
            {
                Gear = 0,
                Candidates = candidates,
                SpeedDensityHp = target,
                Summary =
                    $"No gear is near enough to call. The closest is {best.Gear} at "
                    + $"{best.OffPercent:P0}, but this gearbox only puts {Math.Exp(spacing) - 1:P0} "
                    + "between that gear and its neighbour — so the two routes disagree by more than "
                    + "the thing being measured, and the nearest gear is only the smaller rounding "
                    + "error.",
            };
        }

        return new GearVerdict
        {
            Gear = best.Gear,
            Candidates = candidates,
            SpeedDensityHp = target,
            Summary =
                $"Gear {best.Gear}: road load in that gear runs at {best.Ratio:P0} of what the air "
                + $"says, taken across the range they share — {best.OffPercent:P0} out. The next "
                + $"nearest gear is {ranked[1].OffPercent:P0} out, so the choice is not close. "
                + string.Format(peaks, best.RoadLoadHp),
        };
    }

    /// <summary>
    /// The pull with its gear filled in, where the two methods agreed on one.
    ///
    /// Marked as having been worked out rather than measured: the ratio was never
    /// compared against a road speed, because there was none to compare against.
    /// </summary>
    public static DynoPull Apply(DynoPull pull, GearVerdict verdict)
    {
        ArgumentNullException.ThrowIfNull(pull);
        ArgumentNullException.ThrowIfNull(verdict);

        if (!verdict.Resolved) return pull;

        return pull with
        {
            Gearing = pull.Gearing with { Gear = verdict.Gear },
            Speed = SpeedSource.EngineSpeedAndGear,
            Faults = [.. pull.Faults.Where(f => f != PullFault.GearNotChecked), PullFault.GearFromAgreement],
        };
    }

    /// <summary>
    /// How far apart the powers implied by a gear and its nearest neighbour are,
    /// as a log ratio.
    ///
    /// Road load scales with about the square of the total ratio, so this is
    /// twice the log of the ratio between the two gears. It is the resolution the
    /// gearbox itself offers, and nothing can be told apart more finely.
    /// </summary>
    private static double SpacingAround(VehicleSpec vehicle, int gear)
    {
        double here = vehicle.RatioOf(gear);

        if (!(here > 0)) return double.NaN;

        double nearest = double.PositiveInfinity;

        foreach (int other in new[] { gear - 1, gear + 1 })
        {
            double there = vehicle.RatioOf(other);

            if (!(there > 0)) continue;

            nearest = Math.Min(nearest, Math.Abs(2 * Math.Log(here / there)));
        }

        return double.IsFinite(nearest) ? nearest : double.NaN;
    }
    /// <summary>
    /// How far apart two curves run, taken rung by rung over the range they share.
    ///
    /// Not peak against peak, which is what this did first and which compares two
    /// different moments: the air curve climbs to the end of a pull because the
    /// manifold does, while road load peaks in the middle where the engine does,
    /// and on a real pull those sat five hundred rpm apart.
    ///
    /// The median rather than the mean, so that one rung where a curve is thin
    /// does not decide it — and it has to be robust, because the bottom of a
    /// turbocharged pull is genuinely poor evidence. There is little boost, little
    /// power, and an accelerator pump dumping fuel that makes none of it, which
    /// this arithmetic reads as power because all it can do is divide the air by
    /// the mixture. Weighting the top of the pull more heavily was tried and could
    /// not be shown to change any verdict, so it is not here: the gears are far
    /// enough apart that the median survives the pump shot on its own.
    /// </summary>
    private static double Agreement(DynoCurve road, DynoCurve air)
    {
        List<double> ratios = [];

        foreach (DynoPoint p in road.Drawn)
        {
            double other = air.HorsepowerAt(p.Rpm);

            if (!(other > 0) || !(p.Horsepower > 0)) continue;

            ratios.Add(p.Horsepower / other);
        }

        if (ratios.Count < 3) return double.NaN;

        ratios.Sort();

        return ratios[ratios.Count / 2];
    }

    private static GearVerdict None(string why) => new()
    {
        Gear = 0,
        Candidates = [],
        SpeedDensityHp = double.NaN,
        Summary = why,
    };
}
