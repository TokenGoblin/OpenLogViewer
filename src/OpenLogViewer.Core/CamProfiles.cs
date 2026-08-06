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
