namespace OpenLogViewer.Core;

/// <summary>
/// A camshaft as a catalogue describes it, together with the gas conditions the
/// engine it belongs on tends to run.
/// </summary>
/// <param name="Name">What to call it in a list.</param>
/// <param name="IntakeDurationDeg">
/// Inlet valve open duration in crank degrees, seat to seat — the advertised
/// figure, not the one at 0.050 in. See <see cref="CamProfiles"/> for why the
/// distinction matters more here than anywhere else on the page.
/// </param>
/// <param name="ExhaustDurationDeg">The same for the exhaust valve.</param>
/// <param name="IntakeAtFiftyDeg">
/// Inlet duration at 0.050 in lift, which is the number cam cards lead with and
/// the one a person is most likely to be holding. Carried so the list can show
/// both and the right one still ends up in the box.
/// </param>
/// <param name="ExhaustAtFiftyDeg">The same for the exhaust valve.</param>
/// <param name="LobeSeparationDeg">
/// Lobe separation angle, in cam degrees. Not used by any calculation here — it
/// is carried to work out the overlap, which is what explains how the engine
/// behaves.
/// </param>
/// <param name="ChargeCelsius">Charge temperature in the runner this build tends to see.</param>
/// <param name="ExhaustCelsius">Mean gas temperature along the primary.</param>
/// <param name="Note">What the engine is like to live with.</param>
public readonly record struct CamProfile(
    string Name,
    double IntakeDurationDeg,
    double ExhaustDurationDeg,
    double IntakeAtFiftyDeg,
    double ExhaustAtFiftyDeg,
    double LobeSeparationDeg,
    double ChargeCelsius,
    double ExhaustCelsius,
    string Note)
{
    /// <summary>True for the entry that stands for "whatever was typed in".</summary>
    public bool IsCustom => IntakeDurationDeg <= 0;

    /// <summary>
    /// Crank degrees both valves are open together.
    ///
    /// Half of each duration, less the two lobe centres pushed apart:
    /// (DI + DE)/2 − 2·LSA. It is the single number that says most about what an
    /// engine is like to own — it is where idle quality, manifold vacuum and the
    /// whole possibility of exhaust scavenging come from — and it is why a cam is
    /// a decision about the car rather than about peak power.
    /// </summary>
    public double OverlapDeg =>
        IsCustom
            ? double.NaN
            : ((IntakeDurationDeg + ExhaustDurationDeg) / 2) - (2 * LobeSeparationDeg);

    public override string ToString() => Name;
}

/// <summary>
/// Camshafts by what the engine is for, so the page can be used without a cam
/// card in hand.
///
/// Duration drives every length on this page — it is the window the pressure wave
/// has to make its trip in — and it is the number somebody planning a build is
/// least likely to have to hand. Asking for it as a bare figure in degrees gets
/// either the default left alone or a number invented, and both produce lengths
/// that look authoritative and are wrong.
///
/// What a person does know is roughly what they are building. So the list offers
/// that, and turns it into a cam.
///
/// **These are seat-to-seat durations, and that is the trap this list exists to
/// avoid.** A cam card leads with duration at 0.050 in lift, because that is the
/// figure grinds are compared on — and it runs 44 to 48 crank degrees shorter
/// than the seat-to-seat number. The wave does not care where 0.050 in is; it
/// cares when the valve opened. Typing a 0.050 in figure into a box that wants
/// seat-to-seat shortens every runner and primary by something like a fifth, and
/// nothing about the answer looks wrong. Each entry therefore carries both, and
/// the one at 0.050 in is shown alongside so a cam card can be recognised.
///
/// The figures are conventions drawn from what reputable grinders actually sell,
/// checked for internal consistency: the overlap each one implies comes out where
/// catalogue grinds of that description come out, and the gap between the two
/// duration figures stays in the 44 to 48 degrees real cams show. Any particular
/// cam beats all of them, which is why the boxes stay editable and typing in one
/// moves the list to "from your cam card".
/// </summary>
public static class CamProfiles
{
    /// <summary>
    /// The five worth offering, mildest first.
    ///
    /// The ladder is deliberately about the whole car and not about duration
    /// alone. Every step up adds overlap, and overlap is what takes away idle
    /// quality and manifold vacuum — so the descriptions talk about brakes and
    /// idle rather than about horsepower, because that is what actually decides
    /// whether somebody can live with a cam.
    ///
    /// The gas conditions move with the cam for reasons that are real but small.
    /// A stock engine breathes through a heat-soaked manifold in a closed bay and
    /// a race engine through a cold-air box, which is worth fifteen degrees or so
    /// of charge temperature; and a bigger cam at higher output puts more heat
    /// into the primary and gives it less time to leave. Length follows the square
    /// root of absolute temperature, so the whole spread from stock to race is
    /// about five per cent of a pipe — real, but nothing to agonise over.
    /// </summary>
    public static IReadOnlyList<CamProfile> All { get; } =
    [
        new("Stock", 252, 262, 204, 214, 112, 45, 560,
            "OEM or a stock replacement — smooth idle, full vacuum for the brakes, "
            + "pulls from idle and works with the standard converter and gearing"),

        new("Stock +", 262, 270, 218, 224, 110, 40, 580,
            "a mild street grind — the first one you can hear, still idles cleanly "
            + "and keeps enough vacuum for everything the car already has"),

        new("Performance", 274, 286, 230, 236, 110, 35, 600,
            "street and strip — a definite lope, wants a little more compression and "
            + "converter, and the bottom of the rev range starts to go hollow"),

        new("Performance +", 288, 300, 242, 248, 108, 32, 620,
            "serious street and strip — poor idle vacuum, needs a high-stall converter "
            + "and gearing to suit, and power arrives well off idle"),

        new("Full race", 310, 318, 262, 268, 106, 28, 650,
            "no idle worth the name and no vacuum at all — a narrow band up top, on an "
            + "engine that is never asked to do anything else"),

        new("From your cam card", 0, 0, 0, 0, 0, 0, 0,
            "your own figures — use duration seat to seat, not at 0.050 in"),
    ];

    /// <summary>
    /// The profile a pair of durations corresponds to, or the custom entry.
    ///
    /// Matched on the durations alone and not on the temperatures, because the
    /// durations are what make a cam that cam. Somebody who picks a profile and
    /// then sets their own charge temperature has still got that cam, and the list
    /// saying otherwise would be a lie about which one they chose.
    /// </summary>
    public static CamProfile For(double intakeDurationDeg, double exhaustDurationDeg)
    {
        if (!(intakeDurationDeg > 0 && exhaustDurationDeg > 0)) return All[^1];

        foreach (CamProfile cam in All)
        {
            if (cam.IsCustom) continue;

            if (Math.Abs(cam.IntakeDurationDeg - intakeDurationDeg) < 0.5
                && Math.Abs(cam.ExhaustDurationDeg - exhaustDurationDeg) < 0.5)
            {
                return cam;
            }
        }

        return All[^1];
    }

    /// <summary>Where that profile sits in the list, for a combo box.</summary>
    public static int IndexFor(double intakeDurationDeg, double exhaustDurationDeg)
    {
        CamProfile cam = For(intakeDurationDeg, exhaustDurationDeg);

        for (int i = 0; i < All.Count; i++)
            if (All[i].Name == cam.Name) return i;

        return All.Count - 1;
    }

    /// <summary>
    /// Where a profile sits in the ladder, from 0 for stock to 4 for full race,
    /// or −1 for somebody's own cam.
    /// </summary>
    public static int LevelOf(CamProfile cam)
    {
        for (int i = 0; i < All.Count; i++)
            if (All[i].Name == cam.Name && !All[i].IsCustom) return i;

        return -1;
    }

    /// <summary>
    /// How much peak volumetric efficiency one step up the cam ladder is worth.
    ///
    /// A convention, like every figure in <see cref="EngineFamilies"/>, and quoted
    /// with the same warning: two engines answering the same description differ by
    /// more than this on port work alone. Four points a step puts the whole spread
    /// from a stock grind to a full race one at sixteen, which is about what a cam
    /// swap is worth on an engine that is otherwise left alone.
    /// </summary>
    public const double PointsPerCamLevel = 4;

    /// <summary>
    /// The most a cam can add to a head, in points of volumetric efficiency.
    ///
    /// There has to be a ceiling because the port is the limit and a camshaft
    /// cannot make a small one flow more air — it can only hold it open longer. An
    /// old two-valve head given a full race grind gains, but it does not turn into
    /// a four-valve. Thirteen points is about three steps' worth, and it is what
    /// stops the arithmetic promising a smog-era head 91 per cent or a road
    /// four-valve 113.
    /// </summary>
    public const double MostACamCanAdd = 13;

    /// <summary>
    /// Peak volumetric efficiency for a head and a cam together.
    ///
    /// Neither alone answers it. The family says what the ports and the manifold
    /// can flow, the cam says how long they are held open, and the figure quoted
    /// for a family already assumes a particular cam — so this counts the
    /// difference from that assumption rather than adding the cam twice.
    ///
    /// The direction is the point. A modern pushrod V8 quoted at 85 keeps 85 with
    /// the cam its description assumes, reaches the high nineties on a full race
    /// grind, and drops to the seventies if a milder one goes in; a race intake
    /// quoted at 105 only earns that figure with the race cam it was quoted for,
    /// and loses ground as the cam comes back. What it will not do is let a cam
    /// carry a head past what it flows.
    /// </summary>
    public static double VolumetricEfficiency(EngineFamily family, CamProfile cam)
    {
        if (family.IsCustom || cam.IsCustom) return double.NaN;

        int level = LevelOf(cam);
        if (level < 0) return double.NaN;

        double moved = (level - family.ImpliedCamLevel) * PointsPerCamLevel;
        double ceiling = family.VolumetricEfficiency + MostACamCanAdd;

        return Math.Clamp(family.VolumetricEfficiency + moved, 40, ceiling);
    }

    /// <summary>
    /// The family a volumetric efficiency corresponds to given the cam in front of
    /// it, or the custom entry.
    ///
    /// The reverse of the above, and it cannot be <see cref="EngineFamilies.For"/>
    /// because the figure in the box is no longer any family's own number — a
    /// modern two-valve on a performance cam reads 93, which is nobody's baseline.
    /// Each family is asked what it would produce with this cam, and the one that
    /// agrees is the one to show.
    ///
    /// Two heads can land on the same figure — a four-valve on fixed cams and a
    /// race intake both reach 105 with a full race grind, by different routes —
    /// so the answer is not always unique. <paramref name="preferred"/> is the
    /// selection already showing, and it wins whenever it still fits. Without it
    /// the list would jump from the head somebody chose to whichever other one
    /// happens to be listed first, which looks like the page overruling them.
    /// </summary>
    /// <param name="preferred">
    /// Index of the family already selected, or −1 for none.
    /// </param>
    public static int FamilyIndexFor(double vePercent, CamProfile cam, int preferred = -1)
    {
        if (vePercent > 0 && !cam.IsCustom)
        {
            if (preferred >= 0 && preferred < EngineFamilies.All.Count && Fits(preferred))
                return preferred;

            for (int i = 0; i < EngineFamilies.All.Count; i++)
                if (Fits(i)) return i;
        }

        return EngineFamilies.All.Count - 1;

        bool Fits(int index)
        {
            double ve = VolumetricEfficiency(EngineFamilies.All[index], cam);

            return !double.IsNaN(ve) && Math.Abs(ve - vePercent) < 0.5;
        }
    }

    /// <summary>
    /// What an overlap figure means for living with the engine.
    ///
    /// Overlap is the part of a cam choice that is felt from the driver's seat
    /// rather than read off a dyno sheet, and it is the part somebody without a
    /// background in this has no way to anticipate from a duration in degrees.
    /// </summary>
    public static string OverlapVerdict(double overlapDeg) => overlapDeg switch
    {
        double.NaN => "—",
        < 20 => "idles like a standard car",
        < 40 => "clean idle, keeps full vacuum",
        < 55 => "just audible at idle, vacuum still fine",
        < 70 => "a proper lope, vacuum getting thin",
        < 90 => "rough idle, brake booster wants a pump",
        _ => "no usable idle or vacuum — race only",
    };
}
