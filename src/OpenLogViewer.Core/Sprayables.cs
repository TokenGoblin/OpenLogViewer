namespace OpenLogViewer.Core;

/// <summary>
/// A liquid sprayed into the charge to cool it, and what it is made of.
/// </summary>
/// <param name="LatentBtuPerLb">
/// Heat absorbed turning one pound of it from liquid to vapour. The whole
/// mechanism: the charge pays for that heat and gets colder doing it.
/// </param>
/// <param name="DensityGPerCc">
/// What a cc weighs, which is what turns a nozzle rating into a mass flow. Nozzles
/// are sold in cc/min and the physics is in pounds, so this conversion is the
/// difference between a right answer and one out by a quarter.
/// </param>
/// <param name="SpecificHeatBtuPerLbF">Liquid specific heat, for the sensible heating on the way up.</param>
/// <param name="CombustibleFraction">
/// The share by mass that burns. Water contributes none; methanol and ethanol all
/// of theirs, which is why a big spray has to be paid for out of the fuel table.
/// </param>
/// <param name="FuelLhvBtuPerLb">Energy in the combustible part, for working out what it displaces.</param>
public sealed record Sprayable(
    string Name,
    double LatentBtuPerLb,
    double DensityGPerCc,
    double SpecificHeatBtuPerLbF,
    double CombustibleFraction,
    double FuelLhvBtuPerLb,
    string Note)
{
    /// <summary>Grams per cc is the same number as kilograms per litre; pounds per cc for the arithmetic.</summary>
    public double LbPerCc => DensityGPerCc / 453.59237;

    /// <summary>A nozzle's cc/min rating as a mass flow.</summary>
    public double LbPerMinFromCcPerMin(double ccPerMin) => ccPerMin * LbPerCc;

    /// <summary>And back, because nozzles are bought in cc/min.</summary>
    public double CcPerMinFromLbPerMin(double lbPerMin) =>
        LbPerCc > 0 ? lbPerMin / LbPerCc : double.NaN;

    /// <summary>
    /// Cooling from one pound of it, including the sensible heating on the way to
    /// vaporising.
    ///
    /// The latent heat dominates and is what everyone quotes, but the liquid also
    /// arrives cold and has to be warmed, and that heat comes out of the charge
    /// too. For water it is worth another 12 to 15 per cent, which is not nothing.
    /// </summary>
    public double CoolingBtuPerLb(double liquidF, double chargeF)
    {
        double sensible = chargeF > liquidF
            ? SpecificHeatBtuPerLbF * (chargeF - liquidF)
            : 0;

        return LatentBtuPerLb + sensible;
    }
}

/// <summary>
/// The liquids people spray into a charge, and what each is worth.
///
/// The numbers that matter are the latent heat and the density, and they pull in
/// opposite directions. Water absorbs more than twice what methanol does per
/// pound — 970 BTU against 473 — so on cooling alone water wins easily. Methanol
/// is there because it also burns, which means it cannot lean the mixture out
/// when the spray is doing the work, because it is fuel; and because it does not
/// freeze, which matters if the car lives somewhere cold.
///
/// Fifty-fifty is the usual answer, and it is a compromise rather than an
/// optimum: most of water's cooling, enough methanol to stop the tank freezing
/// and to keep the mixture from going lean, and a fluid that will not corrode a
/// pump.
///
/// Latent heats are from published thermophysical data — water 2,260 kJ/kg,
/// methanol 1,100, ethanol 850, petrol about 310 — converted at 0.4299 BTU/lb per
/// kJ/kg. Blends are combined by mass fraction, which is why a fifty-fifty *by
/// volume* is not fifty-fifty by weight: methanol is lighter, so half a litre of
/// it is only 44 per cent of the mass.
/// </summary>
public static class Sprayables
{
    /// <summary>BTU per pound for one kJ per kilogram.</summary>
    public const double BtuPerLbPerKjPerKg = 0.429923;

    /// <summary>Water: the best coolant of the lot, and it does not burn.</summary>
    public static Sprayable Water { get; } = new(
        "100% water", 971.6, 1.000, 1.00, 0, 0,
        "Twice methanol's cooling per pound and no fuel value at all, so a big spray "
        + "does not enrich anything — which cuts both ways: it cools best and it is the "
        + "one that leans the mixture if you were relying on the spray. Freezes, so it "
        + "is a poor choice anywhere that gets cold, and needs something in it to stop "
        + "the pump corroding.");

    public static Sprayable Methanol { get; } = new(
        "100% methanol", 472.9, 0.792, 0.60, 1.0, 8_640,
        "Half water's cooling per pound, and it burns — which is the point. At full "
        + "strength the spray is a second fuel supply and has to be accounted for in "
        + "the tune, or the engine runs rich with it and lean the moment it stops. "
        + "Does not freeze. Attacks some plastics and rubbers.");

    /// <summary>
    /// E85 as a sprayable, which is a different thing from E85 in the tank.
    ///
    /// Almost all fuel and only some coolant: 332 BTU per pound against water's
    /// 972. Sprayed upstream it does cool the charge, but every drop is fuel, so
    /// it moves the mixture a long way and the tune has to expect it.
    /// </summary>
    public static Sprayable E85 { get; } = new(
        "E85", 332, 0.7825, 0.58, 1.0, 11_500,
        "A third of water's cooling per pound and entirely combustible. Sprayed into "
        + "the charge it is a fuel delivery with a cooling side effect rather than the "
        + "other way round — size it as fuel first.");

    /// <summary>
    /// A water-methanol blend at any strength, mixed by volume the way it is sold.
    ///
    /// The conversion people get wrong. Methanol is a fifth lighter than water, so
    /// a mixture that is half methanol by volume is only 44 per cent methanol by
    /// weight — and every property here is a mass average, so using the volume
    /// fraction directly understates the cooling by several per cent.
    /// </summary>
    public static Sprayable Blend(string name, double methanolByVolume)
    {
        double m = Math.Clamp(methanolByVolume, 0, 1);
        double w = 1 - m;

        double methanolMass = m * Methanol.DensityGPerCc;
        double waterMass = w * Water.DensityGPerCc;
        double totalMass = methanolMass + waterMass;

        if (totalMass <= 0) return Water;

        double methanolFraction = methanolMass / totalMass;
        double waterFraction = waterMass / totalMass;

        return new Sprayable(
            name,
            (methanolFraction * Methanol.LatentBtuPerLb) + (waterFraction * Water.LatentBtuPerLb),
            totalMass,
            (methanolFraction * Methanol.SpecificHeatBtuPerLbF) + (waterFraction * Water.SpecificHeatBtuPerLbF),
            methanolFraction,
            Methanol.FuelLhvBtuPerLb,
            $"{m:P0} methanol by volume, which is {methanolFraction:P0} by weight — the "
            + "figure the cooling is worked out from. Methanol is lighter than water, so "
            + "the two are never the same number.");
    }

    /// <summary>
    /// The usual answer: half and half by volume, which is 44% methanol by mass.
    ///
    /// Worth stating in both because the mixture is sold by volume and the physics
    /// is by mass, and taking the volume figure into the arithmetic overstates the
    /// methanol and understates the cooling.
    ///
    /// A property rather than a field, and deliberately. It is derived from the two
    /// neat fluids, and a static field initialised from other static fields depends
    /// on the order they happen to be written in — which this did, and which read a
    /// null methanol until a test caught it. A property cannot be caught out that
    /// way whatever the file is reordered to.
    /// </summary>
    public static Sprayable FiftyFifty => Blend("50/50 water-methanol", 0.5);

    /// <summary>The mixtures worth offering, in the order somebody would consider them.</summary>
    public static IReadOnlyList<Sprayable> All { get; } =
    [
        Water,
        Blend("70/30 water-methanol", 0.30),
        FiftyFifty,
        Blend("30/70 water-methanol", 0.70),
        Methanol,
        E85,
    ];
}

/// <summary>
/// Cooling a charge by spraying something into it, and what that costs in flow.
///
/// The arithmetic is a balance: the heat the air has to lose equals the heat the
/// liquid takes away turning into vapour. Everything else is unit conversion and
/// honesty about what does not evaporate.
///
/// It is worth being clear what this is not. Spraying water into a charge is not
/// a substitute for an intercooler that is too small — it is a way of going
/// further than an intercooler alone, and it stops working the moment the tank is
/// empty. An engine tuned to lean on it, with timing that only the cooler charge
/// tolerates, will find its detonation limit within a few seconds of the pump
/// failing. Every serious system therefore has a flow sensor and a boost or
/// timing fallback, and a calculator that sized a nozzle without saying so would
/// be doing half a job.
/// </summary>
public static class ChemicalIntercooling
{
    /// <summary>
    /// How much of the spray actually vaporises in the charge, by default.
    ///
    /// Not all of it does. Some lands on the walls of the pipe, some is still
    /// liquid when it reaches the valve and evaporates in the cylinder, where it
    /// still does useful work against knock but does nothing for charge density.
    /// Three-quarters is a fair assumption for a decent nozzle well upstream of
    /// the throttle; a nozzle sitting on the manifold does worse.
    /// </summary>
    public const double TypicalEvaporated = 0.75;

    /// <summary>
    /// Spray needed to take a given amount of heat out of the charge, in lb/min.
    ///
    /// Air's heat load divided by what a pound of the liquid removes, then divided
    /// again by how much of it actually evaporates where it needs to.
    /// </summary>
    public static double FlowLbPerMin(
        double airLbPerMin,
        double dropF,
        Sprayable fluid,
        double chargeF,
        double liquidF = 70,
        double evaporated = TypicalEvaporated)
    {
        ArgumentNullException.ThrowIfNull(fluid);

        double heat = ChargeAir.HeatLoadBtuPerMin(airLbPerMin, dropF);
        double perPound = fluid.CoolingBtuPerLb(liquidF, chargeF);
        double share = Math.Clamp(evaporated, 0.05, 1);

        return double.IsNaN(heat) || perPound <= 0 ? double.NaN : heat / perPound / share;
    }

    /// <summary>The same, as a nozzle rating.</summary>
    public static double FlowCcPerMin(
        double airLbPerMin,
        double dropF,
        Sprayable fluid,
        double chargeF,
        double liquidF = 70,
        double evaporated = TypicalEvaporated)
    {
        ArgumentNullException.ThrowIfNull(fluid);

        return fluid.CcPerMinFromLbPerMin(
            FlowLbPerMin(airLbPerMin, dropF, fluid, chargeF, liquidF, evaporated));
    }

    /// <summary>
    /// The drop a given nozzle actually buys, which is the question the other way
    /// round and the one somebody with a nozzle already fitted is asking.
    /// </summary>
    public static double DropFFor(
        double ccPerMin,
        double airLbPerMin,
        Sprayable fluid,
        double chargeF,
        double liquidF = 70,
        double evaporated = TypicalEvaporated)
    {
        ArgumentNullException.ThrowIfNull(fluid);

        if (airLbPerMin <= 0 || ccPerMin <= 0) return double.NaN;

        double lbPerMin = fluid.LbPerMinFromCcPerMin(ccPerMin) * Math.Clamp(evaporated, 0.05, 1);

        return lbPerMin * fluid.CoolingBtuPerLb(liquidF, chargeF) / (airLbPerMin * ChargeAir.CpAir);
    }

    /// <summary>Litres an hour, for sizing a tank.</summary>
    public static double LitresPerHour(double ccPerMin) => ccPerMin * 60 / 1000;

    /// <summary>US gallons an hour, likewise.</summary>
    public static double GallonsPerHour(double ccPerMin) => LitresPerHour(ccPerMin) / 3.785411784;

    /// <summary>How long a tank lasts at full flow, in minutes.</summary>
    public static double TankMinutes(double tankGallons, double ccPerMin) =>
        ccPerMin > 0 ? tankGallons * 3785.411784 / ccPerMin : double.NaN;

    /// <summary>
    /// Petrol this spray displaces, in lb/min, from the combustible part of it.
    ///
    /// The half of the job a cooling calculation leaves out. Methanol carries
    /// about 8,640 BTU per pound against petrol's 18,400, so a pound of methanol
    /// replaces a little under half a pound of petrol — and an engine given both
    /// without anyone taking the petrol out runs rich, then leans out sharply the
    /// moment the spray stops. That transition is what damages engines, not the
    /// richness.
    /// </summary>
    public static double PetrolDisplacedLbPerMin(double sprayLbPerMin, Sprayable fluid)
    {
        ArgumentNullException.ThrowIfNull(fluid);

        const double petrolLhv = 18_400;

        return sprayLbPerMin > 0 && fluid.CombustibleFraction > 0
            ? sprayLbPerMin * fluid.CombustibleFraction * fluid.FuelLhvBtuPerLb / petrolLhv
            : 0;
    }

    /// <summary>
    /// Spray as a percentage of fuel flow, which is how systems are usually talked
    /// about and how their controllers are set up.
    ///
    /// Most tables are written as a share of injector duty or of fuel mass, and
    /// the usual working range is 20 to 50 per cent. Much beyond that and the
    /// engine is running on the spray rather than being helped by it.
    /// </summary>
    public static double PercentOfFuel(double sprayLbPerMin, double fuelLbPerMin) =>
        fuelLbPerMin > 0 ? sprayLbPerMin / fuelLbPerMin * 100 : double.NaN;
}
