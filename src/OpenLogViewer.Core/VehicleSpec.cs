namespace OpenLogViewer.Core;

/// <summary>
/// The car, as far as working power out of how hard it accelerated needs to know.
///
/// <para>
/// Every field here is a term in the same equation, but they are not equally
/// uncertain and it is worth knowing which is which. Mass is nearly exact — a
/// weighbridge or a handbook, plus what is in the seats. The two road-load
/// coefficients are the guesses, and they are the ones
/// a coastdown exists to replace with measurements. The inertias sit
/// in between: they can be looked up, they are rarely wrong by much, and one of
/// them is multiplied by the square of the gear ratio, which is why the gear a
/// pull was made in has to be known rather than assumed.
/// </para>
/// <para>
/// <b>Driveline loss belongs to the car, not to the engine.</b>
/// <see cref="EngineSpec"/> carries one too, for the fuel-based estimates that
/// predate this; the two must not be set independently for the same vehicle or
/// the same loss is taken twice.
/// </para>
/// </summary>
public sealed record VehicleSpec
{
    /// <summary>What the car weighs with its fluids in it and nobody aboard.</summary>
    public double KerbMassKg { get; init; } = 1400;

    /// <summary>Everybody and everything aboard for the run.</summary>
    public double OccupantMassKg { get; init; } = 80;

    /// <summary>Kerb plus what was aboard: the mass actually being accelerated.</summary>
    public double MassKg => KerbMassKg + OccupantMassKg;

    /// <summary>
    /// Drag coefficient times frontal area, in square metres.
    ///
    /// The two are always multiplied and are never separately knowable from a
    /// road test, so they are carried as the product rather than as a pair of
    /// numbers that invite being looked up independently and multiplied wrongly.
    /// A saloon is near 0.65, a low sports car near 0.55, a tall estate or a
    /// pickup past 1.0.
    /// </summary>
    public double DragAreaM2 { get; init; } = 0.68;

    /// <summary>
    /// Rolling resistance coefficient — the fraction of the car's weight the
    /// tyres cost to roll.
    ///
    /// Around 0.010 for a hard touring tyre on smooth asphalt, 0.015 or more for
    /// something soft and grippy, and it rises with speed and falls with
    /// pressure. Constant here, because a road test cannot separate the speed
    /// dependence from the aerodynamic term it looks exactly like.
    /// </summary>
    public double RollingResistance { get; init; } = 0.0125;

    /// <summary>
    /// The rotational inertia of everything turning at wheel speed — all four
    /// wheels, their tyres, the brake discs and the hubs — in kilogram square
    /// metres, added together.
    ///
    /// Around one to one and a half per corner on an eighteen inch wheel, so four
    /// to six for the car. Worth something like three per cent of effective mass,
    /// and unlike the next one it does not change with the gear.
    /// </summary>
    public double WheelInertiaKgM2 { get; init; } = 4.5;

    /// <summary>
    /// The rotational inertia of everything turning at engine speed — the
    /// crankshaft, flywheel, clutch and gearbox input — in kilogram square metres.
    ///
    /// A fifth of a unit is typical, which sounds negligible and is not: it is
    /// multiplied by the square of the total gear ratio before it is added to the
    /// mass, so a fifth becomes six in fourth gear and fourteen in second. That
    /// squaring is the whole reason a pull's gear has to be entered, and the
    /// whole reason pulls are conventionally made in the gear nearest one to one.
    /// </summary>
    public double EngineInertiaKgM2 { get; init; } = 0.20;

    /// <summary>Gear ratios in order, lowest gear first.</summary>
    public IReadOnlyList<double> GearRatios { get; init; } = [3.27, 2.05, 1.62, 1.35, 1.03, 0.84];

    public double FinalDrive { get; init; } = 4.10;

    /// <summary>The tyre on the driven wheels, as written on its sidewall.</summary>
    public Tyre Tyre { get; init; } = new(245, 40, 18);

    /// <summary>
    /// The tyre's overall diameter in millimetres, where that is known directly
    /// rather than through a sidewall.
    ///
    /// A catalogue quotes one, and so does a tuning program: TunerStudio keeps
    /// the figure in inches and never asks for a sidewall at all. Given here it
    /// wins over <see cref="Tyre"/>, because it is the measurement and the
    /// sidewall is a description that has to be converted.
    /// </summary>
    public double? OverallDiameterMm { get; init; }

    /// <summary>The diameter actually used: the stated one, or the sidewall's.</summary>
    public double TyreDiameterMm => OverallDiameterMm ?? Tyre.DiameterMm;

    /// <summary>
    /// How much less than its geometric circumference the loaded tyre travels.
    /// See <see cref="Gearing.RollingDeflectionPercent"/> — three per cent, and
    /// the difference between a speed that is right and one that is optimistic.
    /// </summary>
    public double RollingDeflectionPercent { get; init; } = Gearing.RollingDeflectionPercent;

    /// <summary>
    /// Road gradient over the measured stretch, in per cent, rising positive.
    ///
    /// One per cent of gradient on a sixteen hundred kilogram car at a hundred
    /// miles an hour is about ten horsepower, so a road that looks flat is not
    /// good enough to ignore. The way to be rid of it is to run in both
    /// directions and average, which is what a coastdown measurement asks for.
    /// </summary>
    public double GradePercent { get; init; }

    /// <summary>
    /// What the driveline costs, in per cent, for stating a figure at the crank.
    ///
    /// Zero leaves everything at the wheels, which is where a road test measures
    /// and the only place it measures honestly. A number here does not make the
    /// crank figure a measurement — it makes it the wheel measurement divided by
    /// somebody's opinion.
    /// </summary>
    public double DrivetrainLossPercent { get; init; }

    /// <summary>Whether the engine is force fed, which decides the default correction.</summary>
    public bool Boosted { get; init; }

    /// <summary>
    /// Whether the drag area and rolling resistance came off a coastdown rather
    /// than out of a table.
    ///
    /// Set by <see cref="CoastdownFit.ApplyTo"/> and by nothing else. It changes
    /// no arithmetic; it exists so a figure can say which of its inputs were
    /// measured, which is the difference between a number worth quoting and a
    /// number worth comparing against yesterday's.
    /// </summary>
    public bool RoadLoadMeasured { get; init; }

    // ----- derived ---------------------------------------------------------------

    /// <summary>What the tyre actually covers in one turn, in millimetres.</summary>
    public double RollingCircumferenceMm =>
        Gearing.RollingCircumferenceMm(TyreDiameterMm, RollingDeflectionPercent);

    /// <summary>
    /// The radius that turns wheel rotation into road distance, in metres.
    ///
    /// From the rolling circumference rather than from half the tyre's diameter,
    /// because it is the distance actually covered per turn that relates the
    /// wheel's spin to the car's speed — and therefore the wheel's rotational
    /// inertia to an equivalent mass.
    /// </summary>
    public double WheelRadiusM => RollingCircumferenceMm / (2 * Math.PI) / 1000;

    /// <summary>
    /// The driveline disconnected — what a coastdown is measured in.
    ///
    /// <b>Deliberately not zero.</b> Zero is what pull detection hands back when
    /// the ratio matched no gear the car has, and the two meanings must not share
    /// a value: one of them wants the neutral effective mass and the other wants
    /// to be refused. They did share it, and the result was that the single case
    /// the refusal below was written for — a pull whose gear could not be
    /// recognised — quietly got the neutral figure instead, understating
    /// effective mass by up to a fifth.
    /// </summary>
    public const int Neutral = -1;

    /// <summary>The gear ratio of a gear counted from one, or NaN if there is no such gear.</summary>
    public double RatioOf(int gear) =>
        gear >= 1 && gear <= GearRatios.Count ? GearRatios[gear - 1] : double.NaN;

    /// <summary>Engine turns per turn of the wheel, in a given gear.</summary>
    public double TotalRatio(int gear) => RatioOf(gear) * FinalDrive;

    /// <summary>
    /// The mass the engine has to accelerate, including everything that has to be
    /// spun up as well as pushed along.
    ///
    /// <para>
    /// A rotating part resists being accelerated in addition to its weight, and
    /// the resistance can be written as an equivalent mass: its inertia divided
    /// by the square of the radius at which it is geared to the road. For the
    /// wheels that radius is the tyre's. For the engine it is the tyre's divided
    /// by the total gear ratio, and dividing by a square means multiplying by the
    /// ratio squared.
    /// </para>
    /// <para>
    /// That squaring is the thing to understand about a road dyno. On the car in
    /// the tests the engine's own inertia adds about two per cent of the mass in
    /// fifth gear, four in fourth, nine in second and twenty-two in first. Tell
    /// the arithmetic the wrong gear and the answer moves by the difference —
    /// which is why a pull in an unknown gear is not a pull, and why first gear
    /// is no place to measure anything.
    /// </para>
    /// </summary>
    public double EffectiveMassKg(int gear)
    {
        double r = WheelRadiusM;

        if (!(r > 0)) return double.NaN;

        double rSquared = r * r;
        double wheels = WheelInertiaKgM2 / rSquared;

        // Neutral is a state, not an absence of information. With the driveline
        // disconnected the engine's inertia genuinely is not being accelerated,
        // which is the condition a coastdown is measured in.
        if (gear == Neutral) return MassKg + wheels;

        double ratio = TotalRatio(gear);

        // Any other gear the vehicle does not have is refused rather than
        // answered — zero included, which is what pull detection hands back when
        // the ratio matched nothing. Quietly dropping the engine term, which is
        // what returning the neutral figure for an unknown gear amounts to,
        // understates effective mass by four per cent in the tallest gear and
        // twenty-two in the lowest, and a power figure that much low reads as a
        // number rather than as a mistake.
        if (!(ratio > 0)) return double.NaN;

        return MassKg + wheels + (EngineInertiaKgM2 * ratio * ratio / rSquared);
    }

    /// <summary>
    /// Effective mass as a multiple of the real mass — the figure quoted as a
    /// rotating-mass factor, and the one worth showing beside a gear selector.
    /// </summary>
    public double MassFactor(int gear) => EffectiveMassKg(gear) / MassKg;

    /// <summary>Road speed at a given engine speed in a given gear, in metres a second.</summary>
    public double SpeedMsFromRpm(double rpm, int gear)
    {
        double ratio = TotalRatio(gear);

        if (!(ratio > 0) || !(rpm > 0)) return double.NaN;

        return rpm / 60 / ratio * (RollingCircumferenceMm / 1000);
    }

    /// <summary>And the other way, which is how a gear is recognised from a log.</summary>
    public double RpmFromSpeedMs(double speedMs, int gear)
    {
        double perRpm = SpeedMsFromRpm(1000, gear) / 1000;

        return perRpm > 0 ? speedMs / perRpm : double.NaN;
    }

    /// <summary>The gradient as an angle, which is what the force terms want.</summary>
    public double GradeRadians => Math.Atan(GradePercent / 100);
}
