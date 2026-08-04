namespace OpenLogViewer.Core;

/// <summary>
/// A turbocharger as a catalogue lists it.
/// </summary>
/// <param name="Family">The series it belongs to, for grouping.</param>
/// <param name="Model">What it is called.</param>
/// <param name="InducerMm">
/// Compressor wheel inducer — the diameter air is drawn in at, and the number a
/// turbo is usually described by.
/// </param>
/// <param name="ExducerMm">Compressor wheel exducer, the diameter it leaves at.</param>
/// <param name="RatedHorsepower">
/// What the maker rates it for. On the G series this is the model number itself:
/// a G30-770 is rated at 770.
/// </param>
public readonly record struct Turbo(
    string Family,
    string Model,
    double InducerMm,
    double ExducerMm,
    double RatedHorsepower)
{
    /// <summary>
    /// The air it can pass, worked out from its rating rather than transcribed.
    ///
    /// Deliberate. A compressor map's flow axis has no single "maximum" — the
    /// figure depends on which island and which pressure ratio you read it at,
    /// so quoted numbers for the same turbo differ by ten per cent between
    /// sources. The horsepower rating does not: it is the maker's own claim and
    /// it is printed in the model number.
    ///
    /// Converted with the maker's own airflow equation at the mixture and fuel
    /// consumption their worked example uses. Checked against independently
    /// reported flow figures where any exist: this puts the G30-770 at 68 lb/min
    /// against a reported 69, and the G30-900 at 79 against a reported 81.
    /// </summary>
    public double MaxFlowLbPerMinute =>
        TurboSizing.AirForHorsepower(RatedHorsepower, TurboSizing.RatedAfr, TurboSizing.RatedBsfc);

    public override string ToString() => Model;
}

/// <summary>One turbo against a requirement, and how much room it has left.</summary>
/// <param name="Turbo">The turbocharger.</param>
/// <param name="Headroom">
/// Flow it has spare, as a fraction of what is needed. Zero is exactly on the
/// limit; 0.2 is a fifth in hand.
/// </param>
public readonly record struct TurboMatch(Turbo Turbo, double Headroom)
{
    /// <summary>Two of the same turbo, where the requirement wants a pair.</summary>
    public int Count { get; init; } = 1;

    public string Label => Count > 1 ? $"{Count} × {Turbo.Model}" : Turbo.Model;
}

/// <summary>What an engine needs of a turbocharger, and which ones can do it.</summary>
/// <param name="AirLbPerMinute">Air the target power takes.</param>
/// <param name="ManifoldKpa">Absolute manifold pressure that airflow needs.</param>
/// <param name="BoostKpa">The same as a gauge reading.</param>
/// <param name="PressureRatio">What the compressor is asked to work at, losses included.</param>
/// <param name="CompressorInletKpa">Absolute pressure at the compressor inlet.</param>
/// <param name="CompressorOutletKpa">Absolute pressure it must deliver.</param>
public readonly record struct TurboRequirement(
    double AirLbPerMinute,
    double ManifoldKpa,
    double BoostKpa,
    double PressureRatio,
    double CompressorInletKpa,
    double CompressorOutletKpa);

/// <summary>
/// Choosing a turbocharger for a power target — the sizing worked backwards.
///
/// Every other airflow calculation here starts from an engine and asks what it
/// makes. This starts from the number a person actually has in mind and asks
/// what it would take: the air that much power needs, the manifold pressure that
/// much air needs at the engine's own displacement and filling, and the pressure
/// ratio the compressor is therefore asked to work at once the filter and the
/// intercooler have taken their share.
///
/// The equations are the turbocharger maker's own rather than a reworking of
/// them, so the answers can be checked against the tool everybody already uses.
/// Garrett's published worked example — 650 crank horsepower on a 5.7 litre at
/// 6,000 rpm — is a test here, and comes out at their 57.3 lb/min and their
/// pressure ratio of 2.0.
///
/// What this is not is a compressor map. A turbo that flows enough may still be
/// wrong for the engine: it may be choked at low speed, or past the edge of its
/// island at the pressure ratio wanted, or simply too large to spool on a small
/// engine. Flow and pressure ratio narrow the field to the ones worth looking
/// at; the map decides between them, and there is no substitute for reading one.
/// </summary>
public static class TurboSizing
{
    /// <summary>
    /// Mixture the catalogue's horsepower ratings are taken at.
    ///
    /// From the maker's own worked example. It matters only for turning a rating
    /// back into airflow — an engine running something else is still compared
    /// air against air, so its own mixture is what decides the requirement.
    /// </summary>
    public const double RatedAfr = 11.5;

    /// <summary>Fuel consumption those ratings are taken at, from the same example.</summary>
    public const double RatedBsfc = 0.46;

    /// <summary>
    /// The air a power target needs, in pounds per minute.
    ///
    /// The maker's equation exactly: power times the mixture times the fuel
    /// consumption, over the sixty minutes in an hour. It is the same statement
    /// as <see cref="TuningMath.HorsepowerFromAir"/> turned around, and the two
    /// are checked against each other.
    /// </summary>
    public static double AirForHorsepower(double horsepower, double afr, double bsfc) =>
        horsepower > 0 && afr > 0 && bsfc > 0 ? horsepower * afr * bsfc / 60 : double.NaN;

    /// <summary>
    /// The absolute manifold pressure a given airflow needs.
    ///
    /// The gas law again, run backwards: the engine swallows a fixed volume per
    /// revolution, so getting a given mass through it is a question of how dense
    /// the charge has to be, and therefore how hard it has to be pushed.
    ///
    /// Worked in kilopascals and litres, sharing its constant with
    /// <see cref="PowerEstimate"/> so the two cannot drift apart. The maker
    /// publishes the same thing in cubic inches and degrees Rankine with a
    /// constant of 639.6; that form is a test here rather than the
    /// implementation, since agreeing through a different unit system is worth
    /// more than restating one.
    /// </summary>
    public static double ManifoldKpaFor(
        double airLbPerMinute, double litres, double rpm, double vePercent, double chargeCelsius)
    {
        double ve = vePercent / 100;

        if (!(airLbPerMinute > 0) || !(litres > 0) || !(rpm > 0) || !(ve > 0)) return double.NaN;

        double kgPerMinute = airLbPerMinute * 0.45359237;
        double kelvin = chargeCelsius + 273.15;

        if (!(kelvin > 0)) return double.NaN;

        return kgPerMinute * 574 * kelvin / (litres * ve * rpm);
    }

    /// <summary>
    /// Everything the engine asks of a compressor.
    /// </summary>
    /// <param name="horsepower">Target, at the crank.</param>
    /// <param name="afr">Mixture at full throttle.</param>
    /// <param name="bsfc">Fuel consumption to assume.</param>
    /// <param name="litres">Displacement.</param>
    /// <param name="rpm">Engine speed at peak power.</param>
    /// <param name="vePercent">Volumetric efficiency there.</param>
    /// <param name="chargeCelsius">Charge temperature in the manifold, after the intercooler.</param>
    /// <param name="barometricKpa">The air the engine is breathing.</param>
    /// <param name="inletLossKpa">Filter and the pipe to the compressor.</param>
    /// <param name="chargeLossKpa">Intercooler and the pipe from it.</param>
    public static TurboRequirement Required(
        double horsepower,
        double afr,
        double bsfc,
        double litres,
        double rpm,
        double vePercent,
        double chargeCelsius,
        double barometricKpa = TuningMath.AtmosphericKpa,
        double inletLossKpa = 0,
        double chargeLossKpa = 0)
    {
        double air = AirForHorsepower(horsepower, afr, bsfc);
        double manifold = ManifoldKpaFor(air, litres, rpm, vePercent, chargeCelsius);

        if (double.IsNaN(manifold))
            return new TurboRequirement(air, double.NaN, double.NaN, double.NaN, double.NaN, double.NaN);

        double boost = manifold - barometricKpa;

        TuningMath.Compressor compressor =
            TuningMath.CompressorPressures(boost, barometricKpa, inletLossKpa, chargeLossKpa);

        return new TurboRequirement(
            air, manifold, boost, compressor.Ratio, compressor.InletKpa, compressor.OutletKpa);
    }

    /// <summary>
    /// A short catalogue of Garrett's performance turbochargers.
    ///
    /// The inducer and exducer are the compressor wheel's own dimensions, and the
    /// horsepower is the maker's rating — which on the G series is the model
    /// number, so it cannot drift from what the box says.
    ///
    /// Short on purpose, and the same reasoning as the fuel pumps: a longer list
    /// would be more useful and far harder to keep true, and a suggestion made
    /// from a stale figure is worse than no suggestion. Makers revise and
    /// supersede — the G series has already been through a second generation with
    /// different numbers on the same frames. Check against the maker's current
    /// catalogue before buying anything on the strength of this.
    /// </summary>
    public static IReadOnlyList<Turbo> Catalogue { get; } =
    [
        new("G Series", "G25-550", 48, 60, 550),
        new("G Series", "G25-660", 54, 67, 660),
        new("G Series", "G30-660", 58, 71, 660),
        new("G Series", "G30-770", 58, 71, 770),
        new("G Series", "G30-900", 62, 76, 900),
        new("G Series", "G35-900", 62, 76, 900),
        new("G Series", "G35-1050", 68, 84, 1050),
        new("G Series", "G40-1150", 71, 88, 1150),
        new("G Series", "G42-1200", 73, 91, 1200),
        new("G Series", "G42-1450", 79, 98, 1450),
        new("G Series", "G45-1475", 79, 98, 1475),
    ];

    /// <summary>
    /// Headroom worth having over the flow actually needed.
    ///
    /// A compressor run at the very right-hand edge of its map is one that has
    /// nothing left for a hot day, a worn filter, or the engine turning out
    /// better than the target — and the edge is where efficiency has already
    /// fallen away, so the air it does pass arrives hotter than it should.
    /// </summary>
    public const double SensibleHeadroom = 0.10;

    /// <summary>
    /// Turbochargers that could pass the air, smallest first.
    ///
    /// Smallest rather than largest, deliberately. Anything on the list can flow
    /// the number; the one worth having is the smallest that can, because it is
    /// the one that will spool soonest and spend the most of its life near the
    /// middle of its map rather than off the left edge of it.
    ///
    /// A pair is offered as well where a single one cannot do it, since two
    /// smaller turbochargers is a real answer to a large engine and not a
    /// consolation prize.
    /// </summary>
    public static IReadOnlyList<TurboMatch> Suggest(
        double airLbPerMinute, double headroom = SensibleHeadroom, int most = 4)
    {
        if (!(airLbPerMinute > 0)) return [];

        double wanted = airLbPerMinute * (1 + headroom);

        List<TurboMatch> singles =
        [
            .. Catalogue
                .Where(t => t.MaxFlowLbPerMinute >= wanted)
                .OrderBy(t => t.MaxFlowLbPerMinute)
                .Take(most)
                .Select(t => new TurboMatch(t, (t.MaxFlowLbPerMinute / airLbPerMinute) - 1)),
        ];

        if (singles.Count > 0) return singles;

        // Nothing single-handedly. Two of something, which is how a large engine
        // is usually done anyway.
        return
        [
            .. Catalogue
                .Where(t => t.MaxFlowLbPerMinute * 2 >= wanted)
                .OrderBy(t => t.MaxFlowLbPerMinute)
                .Take(most)
                .Select(t => new TurboMatch(t, (t.MaxFlowLbPerMinute * 2 / airLbPerMinute) - 1)
                {
                    Count = 2,
                }),
        ];
    }
}
