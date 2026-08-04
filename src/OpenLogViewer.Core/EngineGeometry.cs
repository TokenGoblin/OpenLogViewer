namespace OpenLogViewer.Core;

/// <summary>
/// The engine as a set of dimensions: how big it is, how fast its pistons are
/// going, and how hard it squeezes.
///
/// One file rather than three because they are one calculation wearing three
/// hats — bore and stroke decide the displacement, the stroke and the engine
/// speed decide the piston speed, and the bore and stroke together with what is
/// left above the piston decide the compression. Ask for any of them and you
/// have already typed most of what the others need.
/// </summary>
public static class EngineGeometry
{
    public const double CubicCentimetresPerCubicInch = 16.387064;

    public const double FeetPerMetre = 3.280839895013123;

    /// <summary>
    /// The volume of a cylinder, in cc, from a diameter and a height in
    /// millimetres.
    ///
    /// Used for the swept volume and for every part of the clearance volume as
    /// well — a head gasket and the gap above a piston at top dead centre are
    /// both just short cylinders.
    /// </summary>
    public static double CylinderVolumeCc(double diameterMm, double heightMm) =>
        Math.PI * diameterMm * diameterMm * heightMm / 4_000;

    /// <summary>What one cylinder sweeps.</summary>
    public static double SweptVolumeCc(double boreMm, double strokeMm) =>
        boreMm > 0 && strokeMm > 0 ? CylinderVolumeCc(boreMm, strokeMm) : double.NaN;

    /// <summary>What the whole engine sweeps, which is the number on the badge.</summary>
    public static double DisplacementCc(double boreMm, double strokeMm, int cylinders) =>
        cylinders > 0 ? SweptVolumeCc(boreMm, strokeMm) * cylinders : double.NaN;

    public static double CubicInches(double cc) => cc / CubicCentimetresPerCubicInch;

    /// <summary>
    /// Bore divided by stroke: over one is oversquare, under it undersquare.
    ///
    /// Not a verdict on anything by itself, but it is the shape behind the
    /// piston speed — an oversquare engine has a short stroke and so can be spun
    /// harder for the same speed at the rings.
    /// </summary>
    public static double BoreToStroke(double boreMm, double strokeMm) =>
        strokeMm > 0 ? boreMm / strokeMm : double.NaN;

    // ----- piston speed --------------------------------------------------------

    /// <summary>
    /// Mean piston speed, in metres per second.
    ///
    /// Two strokes per revolution — up and down — so the piston covers twice the
    /// stroke every turn, and the mean is that times the engine speed. It is a
    /// mean and not a maximum: the piston stops dead twice per revolution and
    /// passes through something like 1.6 times this figure in the middle, which
    /// is where the rod is doing its worst work.
    ///
    /// Worth more than the rpm figure it comes from, because rpm on its own says
    /// nothing about what the rings and the rod are being asked to survive. Seven
    /// thousand is a gentle afternoon in a short-stroke engine and the end of a
    /// long-stroke one.
    /// </summary>
    public static double MeanPistonSpeed(double strokeMm, double rpm) =>
        strokeMm > 0 && rpm > 0 ? 2 * strokeMm * rpm / 60_000 : double.NaN;

    /// <summary>The same in feet per minute, which is the unit the old rules of thumb use.</summary>
    public static double MeanPistonSpeedFeetPerMinute(double strokeMm, double rpm) =>
        MeanPistonSpeed(strokeMm, rpm) * FeetPerMetre * 60;

    /// <summary>
    /// What a piston speed means, in words.
    ///
    /// The bands are conventions drawn from what production and race engines
    /// actually run, not thresholds anything fails at — a well built engine
    /// survives above them and a badly built one lets go below.
    /// </summary>
    public static string PistonSpeedVerdict(double metresPerSecond) => metresPerSecond switch
    {
        double.NaN or <= 0 => "—",
        < 12 => "gentle, well short of any limit",
        < 18 => "ordinary for a road engine",
        < 22 => "a performance redline",
        < 25 => "race territory, parts are consumables",
        < 28 => "serious race engine, nothing production",
        _ => "beyond production practice",
    };

    // ----- compression ---------------------------------------------------------

    /// <summary>
    /// Everything still above the piston at top dead centre, in cc.
    /// </summary>
    /// <param name="chamberCc">The head's combustion chamber, as cast or as measured.</param>
    /// <param name="gasketBoreMm">The gasket's bore, which is usually a little over the block's.</param>
    /// <param name="gasketThicknessMm">Compressed thickness, not the thickness in the box.</param>
    /// <param name="deckClearanceMm">
    /// How far the crown sits below the deck at top dead centre. Negative if the
    /// piston comes out of the hole, which takes volume away rather than adding
    /// it.
    /// </param>
    /// <param name="pistonCc">
    /// The crown's own volume: positive for a dish, negative for a dome. A dish
    /// adds space and lowers the ratio; a dome fills it and raises it.
    /// </param>
    public static double ClearanceVolumeCc(
        double boreMm,
        double chamberCc,
        double gasketBoreMm,
        double gasketThicknessMm,
        double deckClearanceMm,
        double pistonCc)
    {
        if (!(boreMm > 0)) return double.NaN;

        double gasket = gasketBoreMm > 0 && gasketThicknessMm > 0
            ? CylinderVolumeCc(gasketBoreMm, gasketThicknessMm)
            : 0;

        // Signed on purpose: a piston out of the hole is a negative height and
        // must come off the total.
        double deck = CylinderVolumeCc(boreMm, deckClearanceMm);

        return chamberCc + gasket + deck + pistonCc;
    }

    /// <summary>
    /// Static compression ratio: everything the cylinder holds at the bottom,
    /// over what is left at the top.
    ///
    /// Static, and the word matters — this is geometry, and says nothing about
    /// when the inlet valve shuts. An engine with a long-duration cam holds the
    /// inlet open well past bottom dead centre and pushes some of the charge back
    /// out, so it behaves like less compression than this at low speed and comes
    /// back to it as the ports start to fill.
    /// </summary>
    public static double CompressionRatio(double sweptCc, double clearanceCc) =>
        clearanceCc > 0 && sweptCc > 0 ? (sweptCc + clearanceCc) / clearanceCc : double.NaN;

    /// <summary>
    /// The clearance volume a wanted ratio needs, which is the question asked
    /// when the parts are still on the bench.
    /// </summary>
    public static double ClearanceForRatio(double sweptCc, double ratio) =>
        ratio > 1 && sweptCc > 0 ? sweptCc / (ratio - 1) : double.NaN;

    /// <summary>
    /// Compression and boost taken together, as an index of how hard the cylinder
    /// is being worked.
    ///
    /// Static ratio times the pressure ratio at the manifold: ten to one on
    /// fifteen psi of boost indexes about the same as twenty to one without it,
    /// which is why a compression ratio that is fine naturally aspirated is not
    /// fine boosted.
    ///
    /// An index and not a compression ratio. It ignores the charge temperature
    /// that boost brings with it, which pushes the real knock limit the wrong
    /// way, and it ignores the cam timing that <see cref="CompressionRatio"/>
    /// ignores too. Useful for comparing one combination against another;
    /// worthless as a number in its own right.
    /// </summary>
    public static double BoostedCompressionIndex(
        double staticRatio,
        double manifoldAbsoluteKpa,
        double atmosphericKpa = TuningMath.AtmosphericKpa) =>
        staticRatio > 0 && manifoldAbsoluteKpa > 0 && atmosphericKpa > 0
            ? staticRatio * manifoldAbsoluteKpa / atmosphericKpa
            : double.NaN;
}
