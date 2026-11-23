namespace OpenLogViewer.Core;

/// <summary>
/// Where the engine's effort went at one instant, in newtons.
/// </summary>
/// <param name="Inertia">Accelerating the car and everything spinning in it.</param>
/// <param name="Aero">Pushing the air out of the way.</param>
/// <param name="Rolling">Deforming the tyres and turning the bearings.</param>
/// <param name="Grade">Lifting the car up the hill, negative going down one.</param>
public readonly record struct RoadForces(double Inertia, double Aero, double Rolling, double Grade)
{
    public double Total => Inertia + Aero + Rolling + Grade;

    /// <summary>
    /// The share of the total each term accounts for, which is what says whether
    /// a run is measuring the engine or measuring the guesses.
    ///
    /// Under hard acceleration the inertia term dominates throughout: at forty
    /// miles an hour the air is around three per cent of the effort and at a
    /// hundred and thirty still only a fifth, so an error in the drag area is
    /// worth a fifth as much as it looks. Coasting, the same term is the whole of
    /// it — which is exactly why a coastdown is what measures the drag area, and
    /// why the top of a pull is the least trustworthy part of the curve.
    /// </summary>
    public double AeroShare => Total > 0 ? Aero / Total : double.NaN;
}

/// <summary>
/// Power from how hard the car was accelerating, which is what a dyno measures.
///
/// <para>
/// Nothing in here knows anything about combustion. It does not care what fuel is
/// burning, how much of it there is, what the mixture was or how efficiently any
/// of it went — it asks only how much force it took to do what the car was
/// observed to do, and multiplies by how fast it was going. That is the whole
/// reason to have it alongside the estimates in <see cref="PowerEstimate"/>,
/// which are entirely about combustion and rest on a fuel consumption nobody
/// measured.
/// </para>
/// <para>
/// Four things resist the car and they are added: getting the mass and everything
/// rotating up to speed, shoving the air aside, deforming the tyres, and climbing
/// whatever slope the road has. Multiply the sum by the speed and the answer is
/// in watts.
/// </para>
/// <para>
/// <b>This measures at the wheels and cannot measure anywhere else.</b> What the
/// road sees is what is left after the gearbox, the differential and the bearings
/// have taken their share, and no arrangement of a road test can separate that
/// share from the engine's output — the loss and the output only ever appear
/// added together. A crank figure is therefore this number divided by an opinion,
/// and <see cref="AtCrank"/> is named to say so. It matters when comparing
/// against the fuel-based estimates, which are crank figures by construction: the
/// difference between them is <em>not</em> purely an error in the fuelling model,
/// because the driveline is sitting in the middle of it.
/// </para>
/// </summary>
public static class RoadLoad
{
    /// <summary>Standard gravity, in metres per second squared.</summary>
    public const double Gravity = 9.80665;

    /// <summary>
    /// Watts in one horsepower — the mechanical horse of 550 foot-pounds a
    /// second, not the metric one, which is about one and a half per cent smaller
    /// and is where a good many disagreements between two figures come from.
    /// </summary>
    public const double WattsPerHorsepower = 745.6998715822702;

    /// <summary>
    /// The constant relating power, torque and speed in imperial units: 33,000
    /// foot-pounds a minute in a horsepower, over the 2π radians in a turn.
    ///
    /// Computed rather than written down as 5252, for the same reason
    /// <see cref="Gearing.ClassicMphConstant"/> is: a cross-check that only nearly
    /// agrees is not much of a cross-check. The rounded figure is what makes power
    /// and torque cross at 5,252 rpm on every dyno sheet ever printed.
    /// </summary>
    public static double TorqueConstant { get; } = 33_000 / (2 * Math.PI);

    // ----- the four forces -------------------------------------------------------

    /// <summary>
    /// What it takes to accelerate the car, including spinning up everything that
    /// turns. Negative under braking or when lifting off.
    /// </summary>
    public static double InertiaForce(double effectiveMassKg, double accelerationMs2) =>
        effectiveMassKg * accelerationMs2;

    /// <summary>
    /// What it takes to push the air aside — the term that grows with the square
    /// of speed, and therefore the term that decides a car's top end.
    /// </summary>
    public static double AeroForce(double dragAreaM2, double densityKgM3, double speedMs) =>
        0.5 * densityKgM3 * dragAreaM2 * speedMs * speedMs;

    /// <summary>
    /// What the tyres and bearings cost, which is very nearly constant with speed
    /// and proportional to the weight on them.
    /// </summary>
    public static double RollingForce(double massKg, double rollingResistance, double gradeRadians) =>
        rollingResistance * massKg * Gravity * Math.Cos(gradeRadians);

    /// <summary>What the hill costs, which on a road that looks flat is not nothing.</summary>
    public static double GradeForce(double massKg, double gradeRadians) =>
        massKg * Gravity * Math.Sin(gradeRadians);

    /// <summary>Every term at one instant, kept apart so the split can be shown.</summary>
    public static RoadForces Forces(
        VehicleSpec vehicle, int gear, double speedMs, double accelerationMs2, double densityKgM3)
    {
        ArgumentNullException.ThrowIfNull(vehicle);

        double grade = vehicle.GradeRadians;

        return new RoadForces(
            InertiaForce(vehicle.EffectiveMassKg(gear), accelerationMs2),
            AeroForce(vehicle.DragAreaM2, densityKgM3, speedMs),
            RollingForce(vehicle.MassKg, vehicle.RollingResistance, grade),
            GradeForce(vehicle.MassKg, grade));
    }

    // ----- power -----------------------------------------------------------------

    /// <summary>
    /// Power at the wheels, in watts.
    ///
    /// <para>
    /// Zero rather than a negative number where the car is slowing faster than the
    /// road load alone would slow it: that is a lift or a brake, not an engine
    /// making negative power, and letting it through as a negative reading puts a
    /// spike on the chart wherever the driver breathed. A pull with any of these
    /// in it should be rejected rather than clamped, which is what pull detection
    /// is for; the clamp is the second line.
    /// </para>
    /// </summary>
    public static double Watts(
        VehicleSpec vehicle, int gear, double speedMs, double accelerationMs2, double densityKgM3)
    {
        if (!double.IsFinite(speedMs) || !double.IsFinite(accelerationMs2) || speedMs <= 0)
            return double.NaN;

        double watts = Forces(vehicle, gear, speedMs, accelerationMs2, densityKgM3).Total * speedMs;

        // Unknown stays unknown. The clamp below reads NaN as "not greater than
        // zero" and would hand back a confident nought — the silent wrong answer
        // this codebase forbids everywhere else, and reachable here through an
        // effective mass that refused to be computed.
        if (double.IsNaN(watts)) return double.NaN;

        return watts > 0 ? watts : 0;
    }

    /// <summary>Power at the wheels, in horsepower.</summary>
    public static double Horsepower(
        VehicleSpec vehicle, int gear, double speedMs, double accelerationMs2, double densityKgM3) =>
        Watts(vehicle, gear, speedMs, accelerationMs2, densityKgM3) / WattsPerHorsepower;

    /// <summary>
    /// A wheel figure restated at the crank, by dividing out the loss the vehicle
    /// declares.
    ///
    /// Named for what it is. The division is exact; the number divided by is
    /// somebody's estimate of a quantity a road test cannot see, and it does not
    /// become a measurement by being applied to one.
    /// </summary>
    public static double AtCrank(double atWheels, VehicleSpec vehicle)
    {
        ArgumentNullException.ThrowIfNull(vehicle);

        double remaining = 1 - (vehicle.DrivetrainLossPercent / 100);

        return remaining > 0 ? atWheels / remaining : double.NaN;
    }

    /// <summary>
    /// Torque in pound-feet, from power in horsepower and engine speed.
    ///
    /// Referenced to engine speed, which is the convention every dyno sheet uses
    /// and is why the two curves cross where they do. It is not the torque at the
    /// wheels — that is larger by the gearing, and is a different quantity that
    /// happens to share a name.
    /// </summary>
    public static double PoundFeet(double horsepower, double rpm) =>
        rpm > 0 ? horsepower * TorqueConstant / rpm : double.NaN;

    /// <summary>Newton metres, for anyone stating a figure in metric.</summary>
    public static double NewtonMetres(double watts, double rpm) =>
        rpm > 0 ? watts * 60 / (2 * Math.PI * rpm) : double.NaN;

    // ----- the inverse, which is what the tests turn on --------------------------

    /// <summary>
    /// The acceleration a given wheel power would produce at a given speed.
    ///
    /// <para>
    /// The arithmetic above run backwards, and it earns its place twice over.
    /// A synthetic pull is built with it — start from a power curve that is known
    /// because it was written down, integrate this forward, and the speed trace
    /// that comes out is what a car with that engine would really have done. Feed
    /// that trace back through <see cref="Watts"/> and the original curve has to
    /// come back, which tests the differentiation, the force model and the units
    /// in one go against an answer that cannot be argued with.
    /// </para>
    /// <para>
    /// It is also what a coastdown is fitted with, where the power is zero and
    /// the deceleration is entirely the road load.
    /// </para>
    /// </summary>
    public static double AccelerationMs2(
        VehicleSpec vehicle, int gear, double speedMs, double wheelWatts, double densityKgM3)
    {
        ArgumentNullException.ThrowIfNull(vehicle);

        if (!(speedMs > 0)) return double.NaN;

        double grade = vehicle.GradeRadians;

        double resisting =
            AeroForce(vehicle.DragAreaM2, densityKgM3, speedMs)
            + RollingForce(vehicle.MassKg, vehicle.RollingResistance, grade)
            + GradeForce(vehicle.MassKg, grade);

        double driving = wheelWatts / speedMs;
        double mass = vehicle.EffectiveMassKg(gear);

        return mass > 0 ? (driving - resisting) / mass : double.NaN;
    }
}
