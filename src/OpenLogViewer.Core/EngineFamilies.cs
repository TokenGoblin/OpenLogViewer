namespace OpenLogViewer.Core;

/// <summary>
/// A kind of engine, and how well it breathes.
/// </summary>
/// <param name="Name">What to call it in a list.</param>
/// <param name="VolumetricEfficiency">Peak volumetric efficiency, per cent.</param>
/// <param name="Note">Why it breathes the way it does.</param>
public readonly record struct EngineFamily(string Name, double VolumetricEfficiency, string Note)
{
    /// <summary>True for the entry that stands for "whatever was typed in".</summary>
    public bool IsCustom => VolumetricEfficiency <= 0;

    /// <summary>
    /// Which of <see cref="CamProfiles"/> this description already assumes, as an
    /// index into that list.
    ///
    /// Every figure here is quoted for an engine with a particular sort of cam in
    /// it — "small ports and a mild cam" is a stock grind, "inlet lengths tuned to
    /// resonate" is a race one — and until now that assumption was buried in the
    /// prose. Naming it lets a page that also knows the cam work out what changing
    /// it would do, instead of counting the cam twice.
    ///
    /// Zero for everything but the race entry, because the rest describe the head
    /// and the manifold on an otherwise standard engine. Unused by the pages that
    /// do not ask about a cam.
    /// </summary>
    public int ImpliedCamLevel { get; init; }

    public override string ToString() => Name;
}

/// <summary>
/// Volumetric efficiency by the kind of engine it is.
///
/// Volumetric efficiency is the figure a sizing calculation is least sure of and
/// most sensitive to — it multiplies straight into the air, so ten per cent of it
/// is ten per cent of the turbocharger — and it is the one number a person
/// planning a build is least likely to know. Asking for it as a bare percentage
/// invites either the default being left alone or a figure being invented.
///
/// What somebody does know is what sort of engine they have. So the list offers
/// that instead, and turns it into a number.
///
/// Every figure is a convention with a wide spread around it. Two engines of the
/// same description differ by ten points on cam, port and manifold alone, and a
/// particular engine's own measured figure beats any of these — which is why the
/// box stays editable and typing in it moves the list to "measured or known".
/// </summary>
public static class EngineFamilies
{
    /// <summary>
    /// The kinds worth offering, breathing worst first.
    ///
    /// Peak volumetric efficiency, at the power peak, referenced to manifold
    /// conditions — so a boosted engine uses the same figure as the naturally
    /// aspirated version of itself, and the boost is accounted for separately by
    /// the pressure rather than by inflating this.
    /// </summary>
    public static IReadOnlyList<EngineFamily> All { get; } =
    [
        new("Older two-valve", 75,
            "small ports and a mild cam — a 70s or 80s engine"),

        new("Modern two-valve", 85,
            "a current pushrod V8 — better than its valve count suggests"),

        new("Four-valve, fixed cams", 92,
            "a twin-cam four-valve head without phasing"),

        new("Four-valve with cam phasing", 97,
            "phasing broadens the peak as well as raising it"),

        new("Race, tuned intake", 105,
            "throttle bodies and inlet lengths tuned to resonate")
        {
            // The only entry whose description already has a big cam in it. A page
            // that also asks for the cam therefore starts from a full race grind
            // here and takes VE away as milder ones are chosen, rather than adding
            // to a figure that assumed one all along.
            ImpliedCamLevel = 4,
        },

        new("Measured or known", 0,
            "your own figure, off a dyno or a log"),
    ];

    /// <summary>
    /// The family a volumetric efficiency corresponds to, or the custom entry.
    ///
    /// Matched loosely, because the point is to show which description a typed
    /// figure sits at rather than to insist it be exact. Anything more than a
    /// couple of points from every entry is somebody's own measurement and is
    /// labelled as such.
    /// </summary>
    public static EngineFamily For(double vePercent)
    {
        if (!(vePercent > 0)) return All[^1];

        foreach (EngineFamily family in All)
            if (!family.IsCustom && Math.Abs(family.VolumetricEfficiency - vePercent) < 0.5)
                return family;

        return All[^1];
    }

    /// <summary>Where that family sits in the list, for a combo box.</summary>
    public static int IndexFor(double vePercent)
    {
        EngineFamily family = For(vePercent);

        for (int i = 0; i < All.Count; i++)
            if (All[i].Name == family.Name) return i;

        return All.Count - 1;
    }
}
