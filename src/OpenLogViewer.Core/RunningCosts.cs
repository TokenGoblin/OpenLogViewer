namespace OpenLogViewer.Core;

/// <summary>What a vehicle runs on.</summary>
public enum FuelKind
{
    Petrol,

    /// <summary>
    /// Not a fuel — a drivetrain that burns petrol, priced as petrol.
    ///
    /// Listed separately because people compare cars that way, and separating it
    /// is what lets the note about plug-in hybrids sit somewhere it will be read.
    /// </summary>
    Hybrid,

    E85,
    Diesel,

    /// <summary>Compressed natural gas, sold by the gasoline gallon equivalent.</summary>
    Cng,

    Electricity,
}

/// <summary>
/// A fuel as you buy it: what it is sold by, and how much energy is in it.
///
/// Named apart from <see cref="Fuel"/>, which is the tuning side of the same
/// word — that one is about stoichiometry and knock resistance, this one is
/// about what the pump charges and what you get for it.
/// </summary>
/// <param name="Unit">What a unit of it is called when you buy it.</param>
/// <param name="EfficiencyUnit">How economy is quoted for it.</param>
/// <param name="KwhPerUnit">
/// Energy in one unit. What makes fuels comparable at all: a gallon of E85 and a
/// gallon of diesel are the same volume and not remotely the same amount of
/// energy, so miles per gallon means something different for each.
/// </param>
public sealed record FuelSpec(
    FuelKind Kind,
    string Name,
    string Unit,
    string EfficiencyUnit,
    double KwhPerUnit,
    string Note)
{
    /// <summary>
    /// The unit in the plural, because "3,810 kWhs" is not how anybody writes it.
    ///
    /// A rule rather than a field: gallons take an s, and the abbreviations do
    /// not. Kept here so the interface never has to guess.
    /// </summary>
    public string UnitPlural => Unit is "gallon" ? "gallons" : Unit;

    /// <summary>Whether economy is quoted as miles per unit, which is all of them but one.</summary>
    public bool PerUnit => Kind != FuelKind.Electricity;
}

/// <summary>
/// The fuels this compares, and what is actually in them.
///
/// The energy figures are the EPA's, and they are the whole reason a comparison
/// like this can be made honestly. A gallon is a volume, not an amount of energy:
/// E85 holds about three-quarters of what petrol does and diesel about an eighth
/// more, so three cars quoting the same miles per gallon are not equally
/// efficient and are not equally cheap to run. Everything here that looks like a
/// judgement is really this arithmetic.
/// </summary>
public static class Fuels
{
    /// <summary>
    /// Energy in a US gallon of petrol, and the definition everything else is
    /// measured against.
    ///
    /// 33.7 kWh is the EPA's figure, and it is what makes MPGe mean anything —
    /// "miles per gallon equivalent" is miles per 33.7 kWh whatever the vehicle
    /// actually drinks. It is also what a gasoline gallon equivalent of CNG is
    /// defined to be, which is why CNG can be quoted in miles per gallon at all.
    /// </summary>
    public const double PetrolKwhPerGallon = 33.7;

    public static IReadOnlyList<FuelSpec> All { get; } =
    [
        new(FuelKind.Petrol, "Petrol", "gallon", "mpg", PetrolKwhPerGallon,
            "Regular unleaded."),

        new(FuelKind.Hybrid, "Hybrid (petrol)", "gallon", "mpg", PetrolKwhPerGallon,
            "A petrol car that uses less of it — same fuel, same price, better economy. "
            + "A plug-in hybrid is a different thing and is not modelled here: what it "
            + "costs depends on how much of your driving is done on the battery, which "
            + "only you know."),

        // 25.2 kWh: E85 is 51 to 83 per cent ethanol depending on the season and
        // the region, and the pump does not tell you which. This is the middle of
        // that, which is the honest thing to assume and is still an assumption.
        new(FuelKind.E85, "E85", "gallon", "mpg", 25.2,
            "About three-quarters the energy of petrol, so a car on E85 does roughly "
            + "25% fewer miles to the gallon than the same car on petrol. If you have "
            + "typed the same economy for both, one of them is wrong."),

        new(FuelKind.Diesel, "Diesel", "gallon", "mpg", 37.7,
            "About an eighth more energy per gallon than petrol, and diesel engines are "
            + "more efficient again — which is why the economy looks so much better "
            + "before you look at the price."),

        new(FuelKind.Cng, "CNG", "GGE", "mpg", PetrolKwhPerGallon,
            "Sold by the gasoline gallon equivalent, which is defined as the same energy "
            + "as a gallon of petrol — so miles per GGE compares directly with mpg."),

        new(FuelKind.Electricity, "Electric", "kWh", "mi/kWh", 1,
            "Charging is not free of losses: about a tenth of what the meter bills never "
            + "reaches the battery, and you are billed for it either way. The cost here "
            + "includes that; the MPGe does not, being what arrives at the wheels."),
    ];

    public static FuelSpec For(FuelKind kind) => All.First(f => f.Kind == kind);
}

/// <summary>
/// National average pump prices, and a warning attached to them.
///
/// These are a starting point and nothing more. Fuel prices move weekly, differ
/// by dollars between states, and the figures below were true on one particular
/// morning — the date is carried with them so nobody has to guess how stale they
/// are. Anyone comparing cars seriously should type in what they actually pay,
/// which is why every one of these is editable.
/// </summary>
public static class FuelPrices
{
    /// <summary>When these were taken, so their age is visible rather than assumed.</summary>
    public static DateOnly CapturedOn { get; } = new(2026, 8, 4);

    /// <summary>Where each came from, because a price with no source is a guess.</summary>
    public static string Source =>
        $"US national averages, {CapturedOn:d MMMM yyyy}. Petrol, diesel and E85 from AAA. "
        + "CNG from the Alternative Fuels Data Center, whose last national figure is older "
        + "than the rest and is the one most worth replacing. Electricity is the US "
        + "residential average.";

    private static readonly Dictionary<FuelKind, double> Prices = new()
    {
        [FuelKind.Petrol] = 4.089,
        [FuelKind.Hybrid] = 4.089,
        [FuelKind.E85] = 3.133,
        [FuelKind.Diesel] = 5.372,

        // October 2025, the most recent national average published. Petrol has
        // moved a long way since, so this is the least current figure here and
        // the note says so.
        [FuelKind.Cng] = 2.96,

        [FuelKind.Electricity] = 0.1883,
    };

    public static double For(FuelKind kind) => Prices.GetValueOrDefault(kind, 0);
}

/// <summary>
/// One vehicle, what it drinks and what that costs.
/// </summary>
/// <param name="Economy">
/// Miles per unit for anything burnt, miles per kWh for anything plugged in.
/// </param>
/// <param name="Price">Per unit — per gallon, per GGE, per kWh.</param>
/// <param name="ChargingLoss">
/// Per cent lost between the meter and the battery, for an electric vehicle.
///
/// Not a rounding detail. Ten to fifteen per cent goes to heat in the charger and
/// the battery, and it is billed to you — an electric car costed from what
/// reaches the wheels looks about an eighth cheaper to run than it is. Ignored
/// for anything that does not plug in.
/// </param>
public sealed record VehicleCost(
    string Name,
    FuelKind Kind,
    double Economy,
    double Price,
    double ChargingLoss = 10)
{
    public FuelSpec Fuel => Fuels.For(Kind);

    /// <summary>Whether there is enough here to cost anything.</summary>
    public bool IsUsable => Economy > 0 && Price >= 0;

    /// <summary>
    /// Units bought per mile, which is where the charging loss lands.
    ///
    /// For everything burnt this is simply one over the economy. For an electric
    /// car the battery takes one thing and the meter charges for more, and it is
    /// the meter that sends the bill.
    /// </summary>
    public double UnitsPerMile
    {
        get
        {
            if (!IsUsable) return double.NaN;

            double atTheWheels = 1 / Economy;

            return Kind == FuelKind.Electricity
                ? atTheWheels / (1 - (Math.Clamp(ChargingLoss, 0, 90) / 100))
                : atTheWheels;
        }
    }

    public double CostPerMile => UnitsPerMile * Price;

    public double UnitsPer(double miles) => UnitsPerMile * miles;

    public double CostPer(double miles) => CostPerMile * miles;

    /// <summary>
    /// Miles per gallon equivalent — miles per 33.7 kWh, whatever it runs on.
    ///
    /// The only figure on the page that compares three different fuels honestly.
    /// Miles per gallon cannot: a gallon of E85 and a gallon of diesel are the
    /// same volume and a third apart in energy, so the same number means different
    /// things. This puts them on one scale.
    ///
    /// Energy at the wheels, not at the meter, so an electric car is not charged
    /// twice for its charging loss — that belongs in the cost, which is where it
    /// is.
    /// </summary>
    public double Mpge => IsUsable
        ? Economy * Fuels.PetrolKwhPerGallon / Fuel.KwhPerUnit
        : double.NaN;

    /// <summary>Energy actually paid for, per mile, including what charging loses.</summary>
    public double KwhPerMile => IsUsable ? UnitsPerMile * Fuel.KwhPerUnit : double.NaN;
}

/// <summary>
/// What it costs to run a car, and what it would cost to run a different one.
///
/// The arithmetic is not the hard part — miles, divided by economy, times price.
/// What this is really for is the comparison, and the comparison is where the
/// traps are. Three of them, all handled rather than hidden:
///
/// A gallon is a volume. E85 holds three-quarters of petrol's energy and diesel
/// an eighth more, so the same miles per gallon on two fuels is not the same
/// efficiency, and typing the same figure for both makes the cheaper fuel look
/// far better than it is.
///
/// An electric car is billed at the meter and not at the battery. The ten to
/// fifteen per cent lost to charging is real money and is easy to leave out.
///
/// And a saving per mile is nearly invisible while a saving per year is not,
/// which is why the answer is given by the week, the month and the year rather
/// than in cents.
/// </summary>
public static class RunningCosts
{
    /// <summary>What a year's driving looks like when nobody has said.</summary>
    public const double TypicalAnnualMiles = 12_000;

    /// <summary>Weeks in a year, for turning an annual figure into a weekly one.</summary>
    public const double WeeksPerYear = 365.25 / 7;

    /// <summary>Months, likewise — twelfths of a year rather than four-week blocks.</summary>
    public const double MonthsPerYear = 12;

    public static double PerWeek(VehicleCost vehicle, double annualMiles) =>
        Cost(vehicle, annualMiles) / WeeksPerYear;

    public static double PerMonth(VehicleCost vehicle, double annualMiles) =>
        Cost(vehicle, annualMiles) / MonthsPerYear;

    public static double PerYear(VehicleCost vehicle, double annualMiles) =>
        Cost(vehicle, annualMiles);

    private static double Cost(VehicleCost vehicle, double annualMiles)
    {
        ArgumentNullException.ThrowIfNull(vehicle);

        return annualMiles > 0 ? vehicle.CostPer(annualMiles) : double.NaN;
    }

    /// <summary>
    /// The cheapest of several to run, or null when none of them can be costed.
    /// </summary>
    public static VehicleCost? Cheapest(IEnumerable<VehicleCost> vehicles)
    {
        ArgumentNullException.ThrowIfNull(vehicles);

        return vehicles.Where(v => v.IsUsable).MinBy(v => v.CostPerMile);
    }

    /// <summary>
    /// What one vehicle saves against another over a year, positive when the
    /// first is cheaper.
    /// </summary>
    public static double AnnualSaving(VehicleCost cheaper, VehicleCost dearer, double annualMiles)
    {
        ArgumentNullException.ThrowIfNull(cheaper);
        ArgumentNullException.ThrowIfNull(dearer);

        if (!cheaper.IsUsable || !dearer.IsUsable || annualMiles <= 0) return double.NaN;

        return dearer.CostPer(annualMiles) - cheaper.CostPer(annualMiles);
    }

    /// <summary>
    /// How long a price difference takes to pay back, in years.
    ///
    /// The question anyone comparing two cars is actually asking, and the one a
    /// running-cost figure on its own cannot answer: a car that saves six hundred
    /// a year and costs eight thousand more has not saved anybody anything for
    /// thirteen years. NaN where it never pays back, which is not a failure — it
    /// is the answer.
    /// </summary>
    public static double YearsToPayBack(
        VehicleCost cheaper, VehicleCost dearer, double annualMiles, double extraPurchasePrice)
    {
        double saving = AnnualSaving(cheaper, dearer, annualMiles);

        if (double.IsNaN(saving) || saving <= 0 || extraPurchasePrice <= 0) return double.NaN;

        return extraPurchasePrice / saving;
    }

    /// <summary>
    /// Whether an economy figure looks like it was copied from the petrol column.
    ///
    /// The single likeliest mistake on a page like this. E85 holds about
    /// three-quarters of petrol's energy, so a car doing 30 mpg on petrol does
    /// about 22 on E85 — and E85 being much cheaper a gallon, entering 30 for
    /// both makes it look like a large saving when it is close to a wash. Worth
    /// saying out loud rather than quietly costing what was typed.
    /// </summary>
    public static bool LooksCopiedFromPetrol(VehicleCost vehicle, IEnumerable<VehicleCost> others)
    {
        ArgumentNullException.ThrowIfNull(vehicle);
        ArgumentNullException.ThrowIfNull(others);

        if (vehicle.Kind != FuelKind.E85 || !vehicle.IsUsable) return false;

        // A petrol car in the comparison quoting an economy this one could only
        // reach on petrol. Ten per cent of slack, because two different cars
        // legitimately differ.
        return others.Any(o =>
            o.IsUsable
            && o.Kind is FuelKind.Petrol or FuelKind.Hybrid
            && vehicle.Economy > o.Economy * 0.9);
    }
}
