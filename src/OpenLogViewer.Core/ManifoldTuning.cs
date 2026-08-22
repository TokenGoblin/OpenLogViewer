namespace OpenLogViewer.Core;

/// <summary>How the engine gets its air, which changes what the pipework is for.</summary>
public enum Induction
{
    NaturallyAspirated,

    Turbocharged,
}

/// <summary>
/// What the engine is being built to do, which is the only thing that decides
/// between two lengths that are both "correct".
/// </summary>
public enum ManifoldGoal
{
    /// <summary>Boost early, torque low, response over peak power.</summary>
    QuickSpool,

    /// <summary>A road engine that still has a top end.</summary>
    Balanced,

    /// <summary>Everything given to peak power, with the bottom end spent to get it.</summary>
    HighRpmRace,
}

/// <summary>
/// One harmonic of the tuned length, and where it puts the resonance.
/// </summary>
/// <param name="Order">
/// How many complete out-and-back trips the wave makes while the valve is open.
/// Lower is a longer pipe tuned lower.
/// </param>
/// <param name="EffectiveLengthMm">The acoustic length, end correction included.</param>
/// <param name="LengthMm">
/// The length to actually build, which is the acoustic length less the end
/// correction. This is what a tape measure would read.
/// </param>
/// <param name="Rpm">The engine speed this order is tuned for.</param>
public readonly record struct TuningOrder(
    int Order,
    double EffectiveLengthMm,
    double LengthMm,
    double Rpm)
{
    /// <summary>Whether this one can be built in an engine bay.</summary>
    public bool Practical { get; init; }
}

/// <summary>What is being designed, and for what.</summary>
public sealed record ManifoldSpec
{
    public double Litres { get; init; } = 2.0;

    public int Cylinders { get; init; } = 4;

    public Induction Induction { get; init; } = Induction.NaturallyAspirated;

    public ManifoldGoal Goal { get; init; } = ManifoldGoal.Balanced;

    /// <summary>
    /// The bottom of the range being designed for: where torque is wanted, and on
    /// a turbocharged engine where full boost is wanted by.
    /// </summary>
    public double PeakTorqueRpm { get; init; } = 4500;

    /// <summary>The top of the range, where the air peaks and the ports run out.</summary>
    public double PeakPowerRpm { get; init; } = 7000;

    /// <summary>
    /// How long the inlet valve is open, in crank degrees — the advertised or
    /// preferably the 0.050 in figure from the cam card.
    ///
    /// This is the window the ram wave has to make its trip in, so it sets the
    /// length as directly as the engine speed does.
    /// </summary>
    public double IntakeDurationDeg { get; init; } = 240;

    /// <summary>How long the exhaust valve is open, in crank degrees.</summary>
    public double ExhaustDurationDeg { get; init; } = 250;

    /// <summary>Air temperature in the runner — after the intercooler on a boosted engine.</summary>
    public double IntakeAirCelsius { get; init; } = 30;

    /// <summary>
    /// Mean gas temperature <em>along the primary</em>, which is not the exhaust
    /// temperature at the valve.
    ///
    /// Gas leaves the port far hotter than this and cools fast down the pipe, and
    /// the wave travels through the average rather than the peak. Six hundred is
    /// the default because it is what Bell's empirical primary length implies
    /// across a wide range of cams and engine speeds — see
    /// <see cref="BellPrimaryLengthInches"/>.
    /// </summary>
    public double ExhaustCelsius { get; init; } = 600;

    public double VolumetricEfficiency { get; init; } = 100;

    public double CompressionRatio { get; init; } = 10.5;

    /// <summary>Absolute manifold pressure at peak power, which on a turbo engine is the boost.</summary>
    public double ManifoldKpa { get; init; } = TuningMath.AtmosphericKpa;

    /// <summary>Mixture at full throttle, for the mass the exhaust has to pass.</summary>
    public double Afr { get; init; } = 12.5;

    /// <summary>
    /// Pressure in the exhaust manifold, absolute. A turbine is a restriction and
    /// sits well above atmospheric; an open header barely is.
    /// </summary>
    public double ExhaustBackPressureKpa { get; init; } = TuningMath.AtmosphericKpa;

    /// <summary>
    /// Runner diameter, if one is already fixed. Left at zero the calculator
    /// sizes it from the velocity target instead.
    /// </summary>
    public double IntakeRunnerDiameterMm { get; init; }

    /// <summary>Primary diameter, if already fixed. Zero sizes it from velocity.</summary>
    public double ExhaustPrimaryDiameterMm { get; init; }

    /// <summary>Plenum as a multiple of engine displacement. Zero takes the goal's default.</summary>
    public double PlenumMultiple { get; init; }
}

/// <summary>The intake side of the answer.</summary>
public sealed record IntakePlan
{
    public required double SpeedOfSound { get; init; }

    public required double TunedRpm { get; init; }

    public required IReadOnlyList<TuningOrder> Orders { get; init; }

    /// <summary>The one to build, chosen for fitting in a car as well as for being tuned.</summary>
    public required TuningOrder Recommended { get; init; }

    public required double RunnerDiameterMm { get; init; }

    public required double RunnerAreaMm2 { get; init; }

    public required double RunnerVolumeCc { get; init; }

    /// <summary>Mean port velocity at peak power, in metres per second.</summary>
    public required double VelocityAtPeakPower { get; init; }

    public required double VelocityAtPeakTorque { get; init; }

    /// <summary>Mean velocity at peak power as a fraction of the speed of sound.</summary>
    public required double MachAtPeakPower { get; init; }

    public required double PlenumVolumeCc { get; init; }

    public required double PlenumMultiple { get; init; }

    /// <summary>
    /// The plenum and every runner together — the charge this page can account
    /// for, in cc.
    ///
    /// Worth reporting on a turbocharged engine because it is the part of spool
    /// that manifold design controls. Worth reading with care for the same reason:
    /// it is not the whole tract, and on a normal front-mount installation it is
    /// not even most of it. See <see cref="ManifoldTuning.PlenumVerdict"/>.
    /// </summary>
    public required double TractVolumeCc { get; init; }

    /// <summary>Where the plenum and runners resonate together, in hertz.</summary>
    public required double HelmholtzHz { get; init; }

    /// <summary>
    /// That resonance against the rate the cylinder actually draws at the tuned
    /// speed. See <see cref="ManifoldTuning.HelmholtzRatio"/> for what to make of it.
    /// </summary>
    public required double HelmholtzRatio { get; init; }
}

/// <summary>The exhaust side of the answer.</summary>
public sealed record ExhaustPlan
{
    public required double SpeedOfSound { get; init; }

    public required double TunedRpm { get; init; }

    /// <summary>Crank degrees from the exhaust valve opening to overlap top dead centre.</summary>
    public required double WaveWindowDeg { get; init; }

    public required IReadOnlyList<TuningOrder> Orders { get; init; }

    public required TuningOrder Recommended { get; init; }

    public required double PrimaryDiameterMm { get; init; }

    public required double PrimaryVolumeCc { get; init; }

    /// <summary>Every primary together, which is what a turbine has to fill before it spins.</summary>
    public required double TotalPrimaryVolumeCc { get; init; }

    public required double VelocityAtPeakPower { get; init; }

    public required double CollectorDiameterMm { get; init; }

    public required double CollectorLengthMm { get; init; }

    /// <summary>Bell's published length for the same cam and speed, for comparison.</summary>
    public required double BellLengthMm { get; init; }
}

/// <summary>An intake and exhaust designed together, and what to worry about.</summary>
public sealed record ManifoldPlan
{
    public required IntakePlan Intake { get; init; }

    public required ExhaustPlan Exhaust { get; init; }

    public required IReadOnlyList<RecipeWarning> Warnings { get; init; }
}

/// <summary>
/// Runner and primary lengths, port sizes and plenum volume — the pipework, sized
/// by the pressure waves running up and down it.
///
/// The idea underneath all of it is that a manifold is not a plumbing problem. An
/// opening valve launches a pressure wave along the pipe; the far end reflects it
/// back inverted; and if it arrives at the valve before it shuts, it either packs
/// charge in or pulls exhaust out, worth ten per cent of volumetric efficiency
/// where it works. Length decides when it arrives, so length decides the engine
/// speed it works at, and that is a choice about what the engine is for rather
/// than a correct answer waiting to be found.
///
/// Two calculations run through everything here. The first is that timing:
///
///   the wave makes K round trips of the pipe in the time the valve is open,
///   so 2KL/a = θ/(6·rpm), and L = a·θ / (12·K·rpm)
///
/// with a the speed of sound in the gas, θ the valve open duration in crank
/// degrees, and K the harmonic — how many trips it makes. K is why one engine
/// has three answers: a lower order is a longer pipe with a stronger pulse, and
/// beyond about six hundred millimetres it stops fitting in the car.
///
/// The second is velocity. A port has to be small enough that the charge moves
/// fast and keeps its momentum at low speed, and large enough not to choke at
/// peak power, and mean velocity through the valve-open window is what balances
/// the two:
///
///   v = 6·rpm·V_swept·VE / (θ·A)
///
/// Both are derived rather than fitted, which matters because it means they can
/// be checked against something. The exhaust length agrees with A. Graham Bell's
/// published empirical formula to within about five per cent across cams from
/// 250° to 300° and speeds from 5,500 to 8,000 rpm, at a mean primary gas
/// temperature of 600 °C — and Bell's constant, worked backwards, implies 536 to
/// 676 °C over the same range. Two unrelated routes to the same number.
///
/// What this is not is a flow bench or a simulation. It sizes pipework from wave
/// timing and mean velocity, and it knows nothing about port shape, valve curtain
/// area, the short-side radius, or what a badly cast bend does to any of it. It
/// gets a design to the right size before any of that is worth measuring.
/// </summary>
public static class ManifoldTuning
{
    /// <summary>Ratio of specific heats for air.</summary>
    public const double GammaAir = 1.4;

    /// <summary>
    /// The same for burnt gas, which is lower — the products are triatomic and
    /// carry more of their energy in modes that do not push.
    /// </summary>
    public const double GammaExhaust = 1.33;

    /// <summary>Specific gas constant for air, J/kg·K.</summary>
    public const double GasConstantAir = 287.05;

    /// <summary>
    /// The same for exhaust. Close enough to air's that the difference is lost in
    /// the temperature estimate: burnt petrol and air differ by under one per cent
    /// in molar mass.
    /// </summary>
    public const double GasConstantExhaust = 287.0;

    /// <summary>
    /// How much acoustically longer an open pipe behaves than it measures.
    ///
    /// Air just outside the mouth moves with the column and has to be accelerated
    /// too, so the pipe resonates as if it ran on past its end. The flanged value
    /// is used because a runner opening into a plenum, and a primary into a
    /// collector, are both closer to a hole in a wall than to a pipe in free air.
    /// It is a fraction of the radius, so it is a few millimetres on a runner —
    /// small, but it is the difference between a length calculated and a length
    /// built.
    /// </summary>
    public const double FlangedEndCorrection = 0.82;

    /// <summary>The shortest runner worth calling one.</summary>
    public const double MinPracticalRunnerMm = 100;

    /// <summary>Past this, it does not fit under a bonnet.</summary>
    public const double MaxPracticalRunnerMm = 650;

    /// <summary>Past this, a primary is a packaging fight rather than a pipe.</summary>
    public const double MaxPracticalPrimaryMm = 1_100;

    // ----- the gas ---------------------------------------------------------------

    /// <summary>
    /// Speed of sound in a gas: √(γRT), the whole of the acoustics in one line.
    ///
    /// Temperature is the only thing that moves it, and it moves as the square
    /// root — so guessing the charge temperature twenty degrees wrong costs about
    /// three per cent of length, while guessing the exhaust temperature a hundred
    /// degrees wrong costs about six.
    /// </summary>
    public static double SpeedOfSound(double celsius, double gamma, double gasConstant)
    {
        double kelvin = celsius + 273.15;

        return kelvin > 0 && gamma > 0 && gasConstant > 0
            ? Math.Sqrt(gamma * gasConstant * kelvin)
            : double.NaN;
    }

    /// <summary>Speed of sound in the intake charge.</summary>
    public static double SpeedOfSoundAir(double celsius) =>
        SpeedOfSound(celsius, GammaAir, GasConstantAir);

    /// <summary>Speed of sound in the exhaust, which is half as fast again for being hot.</summary>
    public static double SpeedOfSoundExhaust(double celsius) =>
        SpeedOfSound(celsius, GammaExhaust, GasConstantExhaust);

    // ----- wave timing -----------------------------------------------------------

    /// <summary>
    /// The acoustic length that puts the returning wave at the valve on time.
    ///
    /// L = a·θ / (12·K·rpm), straight from 2KL/a = θ/(6·rpm). The twelve is the
    /// six degrees per second per rpm doubled for the round trip, and it is why
    /// the answer comes out in metres when the speed of sound is in metres per
    /// second — there is no fitted constant anywhere in it.
    /// </summary>
    public static double TunedLengthMm(double speedOfSound, double windowDeg, int order, double rpm) =>
        speedOfSound > 0 && windowDeg > 0 && order > 0 && rpm > 0
            ? 1_000 * speedOfSound * windowDeg / (12.0 * order * rpm)
            : double.NaN;

    /// <summary>
    /// The same relation read the other way: what an existing pipe is tuned for.
    ///
    /// The question anybody with a manifold already on the engine actually asks.
    /// </summary>
    public static double TunedRpm(double speedOfSound, double windowDeg, int order, double effectiveLengthMm) =>
        speedOfSound > 0 && windowDeg > 0 && order > 0 && effectiveLengthMm > 0
            ? 1_000 * speedOfSound * windowDeg / (12.0 * order * effectiveLengthMm)
            : double.NaN;

    /// <summary>
    /// The crank degrees an exhaust wave has to work in: from the valve cracking
    /// open to overlap top dead centre, where the returning suction is wanted.
    ///
    /// The exhaust valve opens before bottom dead centre by half of whatever the
    /// duration exceeds 180°, so the window is 180 + (ED−180)/2, which tidies to
    /// 90 + ED/2. Assumes the cam is symmetric about bottom dead centre; a cam
    /// ground with its lobe centre moved shifts this by however much it was moved.
    /// </summary>
    public static double ExhaustWaveWindowDeg(double exhaustDurationDeg) =>
        exhaustDurationDeg > 180 ? 90 + (exhaustDurationDeg / 2) : double.NaN;

    /// <summary>
    /// How much longer than its measured length a pipe behaves, from its bore.
    /// </summary>
    public static double EndCorrectionMm(double diameterMm) =>
        diameterMm > 0 ? FlangedEndCorrection * diameterMm / 2 : 0;

    /// <summary>
    /// A. Graham Bell's published primary length, in inches, for cross-checking.
    ///
    /// L = 850·ED/rpm − 3, with ED the exhaust duration. Empirical — the 850 is
    /// fitted to engines that were built and run, and the −3 is the port already
    /// in the head, which is part of the pipe whether or not it is part of the
    /// header. Kept here because a derived answer that agrees with the number the
    /// trade has used for forty years is worth more than either alone.
    /// </summary>
    public static double BellPrimaryLengthInches(double exhaustDurationDeg, double rpm) =>
        exhaustDurationDeg > 0 && rpm > 0 ? (850 * exhaustDurationDeg / rpm) - 3 : double.NaN;

    // ----- ports and velocity ----------------------------------------------------

    public static double AreaMm2(double diameterMm) =>
        diameterMm > 0 ? Math.PI * diameterMm * diameterMm / 4 : double.NaN;

    public static double DiameterMm(double areaMm2) =>
        areaMm2 > 0 ? Math.Sqrt(4 * areaMm2 / Math.PI) : double.NaN;

    /// <summary>
    /// Mean gas velocity through a port while the valve is open, in metres per
    /// second.
    ///
    /// The cylinder takes its swept volume times its filling, and it takes it in
    /// the θ degrees the valve is open rather than spread over the whole cycle —
    /// which is the point, because a velocity averaged over four strokes is a
    /// quarter of the truth and answers nothing.
    ///
    /// Mean, and the word carries weight: the instantaneous peak is somewhere
    /// near half again to twice this, around mid-lift. So a mean of 100 m/s is
    /// already touching Mach 0.5 at its worst moment, which is where a port
    /// starts to choke.
    /// </summary>
    public static double PortVelocity(
        double sweptCc, double volumetricEfficiency, double rpm, double windowDeg, double areaMm2)
    {
        if (!(sweptCc > 0 && volumetricEfficiency > 0 && rpm > 0 && windowDeg > 0 && areaMm2 > 0))
            return double.NaN;

        // cc to m³, mm² to m²; the 6·rpm is crank degrees per second.
        double sweptM3 = sweptCc / 1e6;
        double areaM2 = areaMm2 / 1e6;

        return 6 * rpm * sweptM3 * (volumetricEfficiency / 100) / (windowDeg * areaM2);
    }

    /// <summary>The port area that hits a wanted velocity, which is the sizing question.</summary>
    public static double AreaForVelocity(
        double sweptCc, double volumetricEfficiency, double rpm, double windowDeg, double velocity)
    {
        if (!(sweptCc > 0 && volumetricEfficiency > 0 && rpm > 0 && windowDeg > 0 && velocity > 0))
            return double.NaN;

        double sweptM3 = sweptCc / 1e6;

        return 1e6 * 6 * rpm * sweptM3 * (volumetricEfficiency / 100) / (windowDeg * velocity);
    }

    /// <summary>
    /// What a mean port velocity means, in words.
    ///
    /// Bands from what engines are actually built to, not limits anything fails
    /// at. The cost of being under is a soft bottom end and poor throttle
    /// response; the cost of being over is a top end that stops climbing.
    /// </summary>
    public static string VelocityVerdict(double metresPerSecond) => metresPerSecond switch
    {
        double.NaN or <= 0 => "—",
        < 60 => "oversized — lazy off boost, poor low-lift flow",
        < 80 => "large, biased to peak power",
        < 110 => "the usual compromise",
        < 130 => "small and fast — strong low end, tops out early",
        < 150 => "restrictive at peak power",
        _ => "choked; the port is the limit",
    };

    /// <summary>The gas inside a pipe, which on a turbo manifold is the thing to minimise.</summary>
    public static double PipeVolumeCc(double lengthMm, double diameterMm) =>
        lengthMm > 0 && diameterMm > 0
            ? EngineGeometry.CylinderVolumeCc(diameterMm, lengthMm)
            : double.NaN;

    // ----- the plenum ------------------------------------------------------------

    /// <summary>
    /// The cylinder's volume as the resonator sees it: the mean of what it holds
    /// through the stroke, rather than either end of it.
    ///
    /// Halfway between the volume at top dead centre and at bottom, which for a
    /// swept volume V and a ratio r is V(r+1) / 2(r−1). Engelman's effective
    /// volume, and the reason a high compression engine resonates higher — it has
    /// less gas in it to spring against.
    /// </summary>
    public static double EffectiveCylinderVolumeCc(double sweptCc, double compressionRatio) =>
        sweptCc > 0 && compressionRatio > 1
            ? sweptCc * (compressionRatio + 1) / (2 * (compressionRatio - 1))
            : double.NaN;

    /// <summary>
    /// Where the runner and the volume behind it resonate together, in hertz.
    ///
    /// A Helmholtz resonator: f = (a/2π)·√(A / V·L). The runner is the neck, the
    /// gas in the cylinder is the spring. It is the same system the quarter-wave
    /// calculation describes, seen as a lumped mass on a spring instead of as a
    /// travelling wave, and the two agree on which way to move a length even
    /// though they disagree on the exact number.
    /// </summary>
    public static double HelmholtzHz(
        double speedOfSound, double areaMm2, double effectiveLengthMm, double volumeCc)
    {
        if (!(speedOfSound > 0 && areaMm2 > 0 && effectiveLengthMm > 0 && volumeCc > 0))
            return double.NaN;

        double areaM2 = areaMm2 / 1e6;
        double lengthM = effectiveLengthMm / 1_000;
        double volumeM3 = volumeCc / 1e6;

        return speedOfSound / (2 * Math.PI) * Math.Sqrt(areaM2 / (volumeM3 * lengthM));
    }

    /// <summary>
    /// That resonance against how often the cylinder actually draws.
    ///
    /// A four-stroke inducts once every two revolutions, so the cylinder draws at
    /// rpm/120 hertz, and this is the ratio between the two.
    ///
    /// Treat it as a cross-check and not as a target. A design tuned by the wave
    /// calculation above lands between about 3 and 4 here, so a number far outside
    /// that says the plenum and the runners disagree about what engine they are
    /// on — most often a plenum far too small for the runner it feeds. It is not
    /// an independent optimum, and this calculator does not pretend it is one.
    /// </summary>
    public static double HelmholtzRatio(double helmholtzHz, double rpm) =>
        helmholtzHz > 0 && rpm > 0 ? helmholtzHz / (rpm / 120) : double.NaN;

    /// <summary>
    /// Plenum volume, as a multiple of what the engine displaces.
    ///
    /// The multiple is the whole of the judgement. A small plenum keeps velocity
    /// up and answers the throttle immediately, and runs out of air at the top; a
    /// large one feeds the top end and damps the pulses that let cylinders rob
    /// each other, at the cost of a soft pedal and, on a turbo engine, of every
    /// litre having to be filled before boost arrives.
    /// </summary>
    public static double PlenumVolumeCc(double displacementCc, double multiple) =>
        displacementCc > 0 && multiple > 0 ? displacementCc * multiple : double.NaN;

    /// <summary>
    /// The multiples worth putting side by side, so the choice is a slider rather
    /// than one of three presets.
    ///
    /// Quarter steps from half displacement to twice it, which is the whole of the
    /// range anybody builds in. Anything under about a half starves the top end
    /// and anything over about two is a plenum nobody has to fill.
    /// </summary>
    public static IReadOnlyList<double> PlenumMultiples { get; } =
        [0.50, 0.75, 1.00, 1.25, 1.50, 1.75, 2.00];

    /// <summary>
    /// What a plenum multiple does, in words, and it differs by induction.
    ///
    /// On an atmospheric engine the plenum is a reservoir and the trade is
    /// throttle response against top-end flow. On a turbocharged one it is also
    /// dead volume that has to be pressurised before there is any boost, so the
    /// same multiple reads differently — which is why this asks which it is.
    /// </summary>
    /// <remarks>
    /// The bands are a quarter of displacement wide, matching
    /// <see cref="PlenumMultiples"/>, so that every step in the table reads
    /// differently. Coarser bands made neighbouring rows identical, which is worth
    /// less than no table at all — the point of showing seven is that they are
    /// seven distinct choices.
    /// </remarks>
    public static string PlenumVerdict(double multiple, Induction induction) => (multiple, induction) switch
    {
        ( <= 0, _) => "—",

        ( < 0.65, Induction.Turbocharged) => "least to pressurise, sharpest spool — watch distribution",
        ( < 0.90, Induction.Turbocharged) => "lean, chosen for spool over steadiness",
        ( < 1.15, Induction.Turbocharged) => "a little more settled, slightly slower to fill",
        ( < 1.40, Induction.Turbocharged) => "middling — steadier under boost",
        ( < 1.65, Induction.Turbocharged) => "generous — smooth at full boost, slower to it",
        ( < 1.90, Induction.Turbocharged) => "large — noticeably slower to build",
        (_, Induction.Turbocharged) => "very large — the slowest of these to pressurise",

        ( < 0.65, _) => "very small — sharp off idle, gone up top",
        ( < 0.90, _) => "small — strong response, caps peak power",
        ( < 1.15, _) => "on the small side, favours the mid-range",
        ( < 1.40, _) => "a common road compromise",
        ( < 1.65, _) => "the usual compromise",
        ( < 1.90, _) => "generous — top end over pedal feel",
        _ => "large — peak power, and a soft pedal",
    };

    /// <summary>
    /// How much of the charge a turbocharger has to pressurise this page can see.
    ///
    /// The honest qualifier on every spool argument made from plenum volume, and
    /// the reason the page prints it. On an ordinary front-mount installation the
    /// intercooler core and its two pipe runs come to something like three
    /// quarters of the tract on their own — a 2.0 litre on 60 mm pipework and a
    /// medium core carries roughly 10 litres in them against 1.8 in the plenum and
    /// 1.9 in the runners.
    ///
    /// So the manifold is worth about a quarter of the volume, and taking a plenum
    /// from 0.9 to 0.75 of displacement moves perhaps two per cent of the total.
    /// It is a real lever and a small one, and anyone chasing spool through plenum
    /// volume alone is working on the wrong quarter of the problem.
    /// </summary>
    public static double TractShareOfTypicalInstallation(double tractCc, double interCoolerAndPipingCc) =>
        tractCc > 0 && interCoolerAndPipingCc > 0
            ? tractCc / (tractCc + interCoolerAndPipingCc)
            : double.NaN;

    // ----- the exhaust side ------------------------------------------------------

    /// <summary>
    /// The volume of gas one cylinder pushes out per cycle, at the temperature and
    /// pressure it is at in the primary — in cc.
    ///
    /// Mass first, from the charge in the cylinder plus the fuel that went in with
    /// it, then expanded to where it now is. This matters more than it looks:
    /// exhaust gas leaving at 600 °C occupies roughly three times what the intake
    /// charge did, which is why a primary is bigger than a runner on the same
    /// engine and why sizing it from displacement alone goes wrong.
    /// </summary>
    public static double ExhaustVolumePerCycleCc(
        double sweptCc,
        double volumetricEfficiency,
        double manifoldKpa,
        double chargeCelsius,
        double exhaustCelsius,
        double backPressureKpa,
        double afr)
    {
        if (!(sweptCc > 0 && volumetricEfficiency > 0 && manifoldKpa > 0 && backPressureKpa > 0))
            return double.NaN;

        double chargeK = chargeCelsius + 273.15;
        double exhaustK = exhaustCelsius + 273.15;

        if (chargeK <= 0 || exhaustK <= 0) return double.NaN;

        // Charge mass: ideal gas at the manifold, times how full the cylinder gets.
        double sweptM3 = sweptCc / 1e6;
        double airKg = manifoldKpa * 1_000 * sweptM3 * (volumetricEfficiency / 100)
                       / (GasConstantAir * chargeK);

        // The fuel leaves too, and on a rich full-throttle mixture it is not a
        // rounding error — an eighth of the air mass again at 12.5:1.
        double totalKg = afr > 0 ? airKg * (1 + (1 / afr)) : airKg;

        return 1e6 * totalKg * GasConstantExhaust * exhaustK / (backPressureKpa * 1_000);
    }

    /// <summary>
    /// Collector diameter for a four-into-one, from the primaries feeding it.
    ///
    /// Sized by area rather than by diameter: the collector wants to be a little
    /// larger than one primary but nothing like four, because the pulses arrive
    /// one after another rather than together. Around twice a primary's area is
    /// the long-standing practice, which is about 1.4 times its diameter.
    /// </summary>
    public static double CollectorDiameterMm(double primaryDiameterMm, int primaries) =>
        primaryDiameterMm > 0 && primaries > 1
            ? DiameterMm(AreaMm2(primaryDiameterMm) * 2)
            : double.NaN;

    /// <summary>
    /// How long the collector should be — about half a primary.
    ///
    /// It is a second tuned length: the step out into the collector reflects one
    /// wave and the end of the collector reflects another, and a collector around
    /// half the primary's length puts the second reflection where it helps rather
    /// than where it fights the first.
    /// </summary>
    public static double CollectorLengthMm(double primaryLengthMm) =>
        primaryLengthMm > 0 ? primaryLengthMm / 2 : double.NaN;

    // ----- goals -----------------------------------------------------------------

    /// <summary>
    /// The engine speed a goal tunes the intake for.
    ///
    /// Not a preference so much as the definition of the goal: tuning at the
    /// torque peak puts the resonance where the engine is being asked to pull,
    /// and tuning at the power peak spends the bottom end to hold the top.
    /// </summary>
    public static double IntakeTuningRpm(ManifoldGoal goal, double peakTorqueRpm, double peakPowerRpm) =>
        goal switch
        {
            ManifoldGoal.QuickSpool => peakTorqueRpm,
            ManifoldGoal.HighRpmRace => peakPowerRpm,
            _ => (peakTorqueRpm + peakPowerRpm) / 2,
        };

    /// <summary>
    /// Mean port velocity a goal aims at, at peak power.
    ///
    /// Higher means a smaller port: more velocity low down, more spool, and an
    /// earlier ceiling. The race figure is deliberately the lowest of the three —
    /// a race engine is allowed to be soft at 3,000 rpm because it is never there.
    /// </summary>
    public static double TargetIntakeVelocity(ManifoldGoal goal) => goal switch
    {
        ManifoldGoal.QuickSpool => 105,
        ManifoldGoal.HighRpmRace => 85,
        _ => 95,
    };

    /// <summary>
    /// The same for the exhaust, where the gas is three times the volume and moves
    /// twice as fast.
    ///
    /// These are calibrated rather than quoted, and the distinction matters. Figures
    /// published for "exhaust gas velocity" vary by a factor of three between
    /// sources because they are averaged over different things — the whole cycle,
    /// the valve-open window, the port or the pipe. Rather than import a number
    /// whose basis cannot be checked, these come from working the velocity
    /// backwards out of header sizes that are actually on engines: a 1.6 at 8,000
    /// on 1.5 in primaries, a 2.0 on 1.625, a 5.7 V8 on 1.75, a 6.2 on 1.875.
    /// On the basis used here — mean over the exhaust window, at gas conditions in
    /// the primary — all of them fall between 168 and 211 m/s, which is Mach 0.29
    /// to 0.37. The three goals are spaced across that measured band.
    /// </summary>
    public static double TargetExhaustVelocity(ManifoldGoal goal) => goal switch
    {
        ManifoldGoal.QuickSpool => 205,
        ManifoldGoal.HighRpmRace => 165,
        _ => 185,
    };

    /// <summary>
    /// What an exhaust velocity means, on the same basis as
    /// <see cref="TargetExhaustVelocity"/> and with bands from the same engines.
    ///
    /// Separate from <see cref="VelocityVerdict"/> because the two are not
    /// comparable: a primary at 190 m/s is ordinary where a runner at 190 would be
    /// solid.
    /// </summary>
    public static string ExhaustVelocityVerdict(double metresPerSecond) => metresPerSecond switch
    {
        double.NaN or <= 0 => "—",
        < 130 => "oversized — scavenging goes away, torque with it",
        < 165 => "large, biased to peak power",
        < 215 => "usual for a real header",
        < 260 => "small and fast, restrictive up top",
        _ => "choked; the primary is the limit",
    };

    /// <summary>
    /// Plenum size a goal wants, as a multiple of displacement.
    ///
    /// The turbo case is lower than it looks it should be: a plenum is dead volume
    /// that has to be pressurised before boost is anywhere, so on an engine chosen
    /// for spool the plenum is kept small for the same reason the manifold is.
    /// </summary>
    public static double DefaultPlenumMultiple(ManifoldGoal goal, Induction induction) => (goal, induction) switch
    {
        (ManifoldGoal.QuickSpool, Induction.Turbocharged) => 0.75,
        (ManifoldGoal.QuickSpool, _) => 1.0,
        (ManifoldGoal.HighRpmRace, Induction.Turbocharged) => 1.6,
        (ManifoldGoal.HighRpmRace, _) => 1.8,
        (_, Induction.Turbocharged) => 1.2,
        _ => 1.4,
    };

    /// <summary>The longest runner a goal will accept before going up an order.</summary>
    public static double MaxRunnerForGoal(ManifoldGoal goal) => goal switch
    {
        ManifoldGoal.QuickSpool => MaxPracticalRunnerMm,
        ManifoldGoal.HighRpmRace => 400,
        _ => 520,
    };

    // ----- putting it together ---------------------------------------------------

    /// <summary>
    /// Every harmonic that could be built, longest first.
    ///
    /// Handed back whole rather than reduced to one answer because the choice
    /// between them is the design. The recommendation picks the longest that still
    /// fits, since a lower order is a stronger pulse; anyone who would rather have
    /// the shorter pipe can read it off the same list.
    /// </summary>
    public static IReadOnlyList<TuningOrder> Orders(
        double speedOfSound, double windowDeg, double rpm, double diameterMm,
        double maxLengthMm, int highest = 6)
    {
        var orders = new List<TuningOrder>();

        double correction = EndCorrectionMm(diameterMm);

        for (int order = 1; order <= highest; order++)
        {
            double effective = TunedLengthMm(speedOfSound, windowDeg, order, rpm);

            if (double.IsNaN(effective)) continue;

            double physical = effective - correction;

            orders.Add(new TuningOrder(order, effective, physical, rpm)
            {
                Practical = physical >= MinPracticalRunnerMm && physical <= maxLengthMm,
            });
        }

        return orders;
    }

    /// <summary>
    /// The order to build: the longest practical one, or failing that the closest
    /// thing to it.
    /// </summary>
    /// <param name="preferShortest">
    /// Take the shortest pipe that still packages instead of the longest.
    ///
    /// What a turbocharged engine wants. Wave tuning is worth a few per cent of
    /// filling; boost is worth fifty to a hundred and fifty, so the resonance is a
    /// rounding error against it — while the volume of the pipe is charge the
    /// compressor has to pressurise before there is any boost at all. On a build
    /// chosen for response that trade goes the other way round from an atmospheric
    /// engine, where the pulse is the only help there is and the longest pipe wins.
    /// </param>
    public static TuningOrder Recommend(
        IReadOnlyList<TuningOrder> orders, double maxLengthMm, bool preferShortest = false)
    {
        ArgumentNullException.ThrowIfNull(orders);

        if (orders.Count == 0) return default;

        if (preferShortest)
        {
            for (int i = orders.Count - 1; i >= 0; i--)
                if (orders[i].Practical) return orders[i];
        }
        else
        {
            foreach (TuningOrder order in orders)
                if (order.Practical) return order;
        }

        // Nothing fits. The least bad is whichever is nearest the limit it missed,
        // which is almost always the shortest available.
        TuningOrder best = orders[0];
        double bestMiss = double.MaxValue;

        foreach (TuningOrder order in orders)
        {
            double miss = order.LengthMm > maxLengthMm
                ? order.LengthMm - maxLengthMm
                : MinPracticalRunnerMm - order.LengthMm;

            if (miss < bestMiss)
            {
                bestMiss = miss;
                best = order;
            }
        }

        return best;
    }

    /// <summary>
    /// The whole manifold, intake and exhaust, worked out together.
    ///
    /// Together rather than separately because they share the engine: the same
    /// filling, the same speeds, the same charge temperature. An intake tuned for
    /// 4,000 and a header tuned for 7,000 is a common enough mistake and neither
    /// calculation on its own would ever mention it — this one does, in the
    /// warnings.
    /// </summary>
    public static ManifoldPlan Plan(ManifoldSpec spec)
    {
        ArgumentNullException.ThrowIfNull(spec);

        var warnings = new List<RecipeWarning>();

        double displacementCc = spec.Litres * 1_000;
        double sweptCc = spec.Cylinders > 0 ? displacementCc / spec.Cylinders : double.NaN;

        // ----- intake -----
        double aIn = SpeedOfSoundAir(spec.IntakeAirCelsius);
        double intakeRpm = IntakeTuningRpm(spec.Goal, spec.PeakTorqueRpm, spec.PeakPowerRpm);
        double intakeWindow = spec.IntakeDurationDeg;

        double targetVelocity = TargetIntakeVelocity(spec.Goal);

        double runnerArea = spec.IntakeRunnerDiameterMm > 0
            ? AreaMm2(spec.IntakeRunnerDiameterMm)
            : AreaForVelocity(sweptCc, spec.VolumetricEfficiency, spec.PeakPowerRpm, intakeWindow, targetVelocity);

        double runnerDiameter = spec.IntakeRunnerDiameterMm > 0
            ? spec.IntakeRunnerDiameterMm
            : DiameterMm(runnerArea);

        double maxRunner = MaxRunnerForGoal(spec.Goal);

        IReadOnlyList<TuningOrder> intakeOrders =
            Orders(aIn, intakeWindow, intakeRpm, runnerDiameter, maxRunner);

        // A turbocharged build chosen for response takes the shortest pipe that
        // packages rather than the longest: the pulse it gives up is worth a few
        // per cent, and the volume it saves is charge that no longer has to be
        // pressurised before boost arrives.
        bool shortRunners = spec.Induction == Induction.Turbocharged
                            && spec.Goal == ManifoldGoal.QuickSpool;

        TuningOrder intakePick = Recommend(intakeOrders, maxRunner, shortRunners);

        double plenumMultiple = spec.PlenumMultiple > 0
            ? spec.PlenumMultiple
            : DefaultPlenumMultiple(spec.Goal, spec.Induction);

        double plenumCc = PlenumVolumeCc(displacementCc, plenumMultiple);

        double effectiveCylinder = EffectiveCylinderVolumeCc(sweptCc, spec.CompressionRatio);
        double helmholtz = HelmholtzHz(aIn, runnerArea, intakePick.EffectiveLengthMm, effectiveCylinder);

        var intake = new IntakePlan
        {
            SpeedOfSound = aIn,
            TunedRpm = intakeRpm,
            Orders = intakeOrders,
            Recommended = intakePick,
            RunnerDiameterMm = runnerDiameter,
            RunnerAreaMm2 = runnerArea,
            RunnerVolumeCc = PipeVolumeCc(intakePick.LengthMm, runnerDiameter),
            VelocityAtPeakPower = PortVelocity(
                sweptCc, spec.VolumetricEfficiency, spec.PeakPowerRpm, intakeWindow, runnerArea),
            VelocityAtPeakTorque = PortVelocity(
                sweptCc, spec.VolumetricEfficiency, spec.PeakTorqueRpm, intakeWindow, runnerArea),
            MachAtPeakPower = PortVelocity(
                sweptCc, spec.VolumetricEfficiency, spec.PeakPowerRpm, intakeWindow, runnerArea) / aIn,
            PlenumVolumeCc = plenumCc,
            PlenumMultiple = plenumMultiple,
            TractVolumeCc = plenumCc + (PipeVolumeCc(intakePick.LengthMm, runnerDiameter) * spec.Cylinders),
            HelmholtzHz = helmholtz,
            HelmholtzRatio = HelmholtzRatio(helmholtz, intakeRpm),
        };

        // ----- exhaust -----
        double aEx = SpeedOfSoundExhaust(spec.ExhaustCelsius);
        double exhaustWindow = ExhaustWaveWindowDeg(spec.ExhaustDurationDeg);

        // A turbine reflects nothing useful back down the primary, so a turbo
        // manifold is tuned for the shortest path and the least volume rather
        // than for a length. The wave answer is still shown, as information.
        double exhaustRpm = spec.Induction == Induction.Turbocharged
            ? spec.PeakTorqueRpm
            : IntakeTuningRpm(spec.Goal, spec.PeakTorqueRpm, spec.PeakPowerRpm);

        double exhaustVolumeCc = ExhaustVolumePerCycleCc(
            sweptCc, spec.VolumetricEfficiency, spec.ManifoldKpa, spec.IntakeAirCelsius,
            spec.ExhaustCelsius, spec.ExhaustBackPressureKpa, spec.Afr);

        // Velocity from the real gas volume rather than from displacement: the
        // window here is the exhaust duration, and the gas is three times the
        // volume it was on the way in.
        double exhaustTargetVelocity = TargetExhaustVelocity(spec.Goal);

        double primaryArea;

        if (spec.ExhaustPrimaryDiameterMm > 0)
        {
            primaryArea = AreaMm2(spec.ExhaustPrimaryDiameterMm);
        }
        else
        {
            // Same statement as AreaForVelocity, with the expanded volume standing
            // in for swept volume times filling.
            double m3 = exhaustVolumeCc / 1e6;
            primaryArea = spec.PeakPowerRpm > 0 && spec.ExhaustDurationDeg > 0
                ? 1e6 * 6 * spec.PeakPowerRpm * m3 / (spec.ExhaustDurationDeg * exhaustTargetVelocity)
                : double.NaN;
        }

        double primaryDiameter = spec.ExhaustPrimaryDiameterMm > 0
            ? spec.ExhaustPrimaryDiameterMm
            : DiameterMm(primaryArea);

        IReadOnlyList<TuningOrder> exhaustOrders =
            Orders(aEx, exhaustWindow, exhaustRpm, primaryDiameter, MaxPracticalPrimaryMm);

        TuningOrder exhaustPick = Recommend(exhaustOrders, MaxPracticalPrimaryMm);

        double primaryVolume = PipeVolumeCc(exhaustPick.LengthMm, primaryDiameter);

        double exhaustVelocity = primaryArea > 0 && spec.PeakPowerRpm > 0 && spec.ExhaustDurationDeg > 0
            ? 6 * spec.PeakPowerRpm * (exhaustVolumeCc / 1e6)
              / (spec.ExhaustDurationDeg * (primaryArea / 1e6))
            : double.NaN;

        var exhaust = new ExhaustPlan
        {
            SpeedOfSound = aEx,
            TunedRpm = exhaustRpm,
            WaveWindowDeg = exhaustWindow,
            Orders = exhaustOrders,
            Recommended = exhaustPick,
            PrimaryDiameterMm = primaryDiameter,
            PrimaryVolumeCc = primaryVolume,
            TotalPrimaryVolumeCc = primaryVolume * spec.Cylinders,
            VelocityAtPeakPower = exhaustVelocity,
            CollectorDiameterMm = CollectorDiameterMm(primaryDiameter, spec.Cylinders),
            CollectorLengthMm = CollectorLengthMm(exhaustPick.LengthMm),
            BellLengthMm = BellPrimaryLengthInches(spec.ExhaustDurationDeg, exhaustRpm) * 25.4,
        };

        AddWarnings(warnings, spec, intake, exhaust);

        return new ManifoldPlan { Intake = intake, Exhaust = exhaust, Warnings = warnings };
    }

    /// <summary>
    /// What is worth saying out loud about a design that the numbers alone do not.
    /// </summary>
    private static void AddWarnings(
        List<RecipeWarning> warnings, ManifoldSpec spec, IntakePlan intake, ExhaustPlan exhaust)
    {
        if (spec.PeakPowerRpm <= spec.PeakTorqueRpm)
            warnings.Add(new RecipeWarning(
                "stop",
                "Peak power is at or below peak torque. One of the two engine speeds is wrong, "
                + "and every length here is tuned off the pair."));

        if (spec.IntakeDurationDeg is < 180 or > 320)
            warnings.Add(new RecipeWarning(
                "stop",
                $"An inlet duration of {spec.IntakeDurationDeg:N0}° is outside anything a four-stroke "
                + "runs. Lengths scale directly with it, so the answer will be wrong by however much it is."));

        if (spec.ExhaustDurationDeg <= 180)
            warnings.Add(new RecipeWarning(
                "stop",
                "The exhaust duration has to exceed 180° — the valve opens before bottom dead centre. "
                + "No wave window can be worked out from this."));

        if (!intake.Recommended.Practical && intake.Recommended.LengthMm > 0)
            warnings.Add(new RecipeWarning(
                "watch",
                $"No harmonic gives a runner that fits: the closest is {intake.Recommended.LengthMm:N0} mm "
                + $"at order {intake.Recommended.Order}. Tuning this low usually means accepting a shorter "
                + "pipe at a higher order and a weaker pulse."));

        if (intake.VelocityAtPeakPower is > 0 and < 60)
            warnings.Add(new RecipeWarning(
                "watch",
                $"Mean port velocity at peak power is only {intake.VelocityAtPeakPower:N0} m/s. The runner "
                + "is larger than the engine can use, which costs low-speed torque and throttle response "
                + "for top end it may never reach."));

        if (intake.VelocityAtPeakPower > 130)
            warnings.Add(new RecipeWarning(
                "watch",
                $"Mean port velocity reaches {intake.VelocityAtPeakPower:N0} m/s at peak power, and the "
                + "instantaneous peak is around half again that. The port will be the limit before the "
                + "engine is."));

        if (intake.MachAtPeakPower > 0.35)
            warnings.Add(new RecipeWarning(
                "watch",
                $"Mean Mach {intake.MachAtPeakPower:N2} in the runner. Peaks run near twice the mean, so "
                + "this is close to choking at mid-lift."));

        if (exhaust.VelocityAtPeakPower is > 0 and < 130)
            warnings.Add(new RecipeWarning(
                "watch",
                $"Gas leaves the primary at only {exhaust.VelocityAtPeakPower:N0} m/s at peak power, where "
                + "headers on real engines run 168 to 211. A primary this large stops scavenging and takes "
                + "the mid-range with it."));

        if (exhaust.VelocityAtPeakPower > 260)
            warnings.Add(new RecipeWarning(
                "watch",
                $"Gas reaches {exhaust.VelocityAtPeakPower:N0} m/s in the primary. That is past anything "
                + "built, and the header will be the limit before the engine is."));

        if (intake.HelmholtzRatio is > 0 and (< 2.0 or > 5.5))
            warnings.Add(new RecipeWarning(
                "note",
                $"Plenum and runners resonate at a ratio of {intake.HelmholtzRatio:N1} against the tuned "
                + "speed, where a consistently tuned design sits between about 3 and 4. Usually the plenum "
                + "is the odd one out — check its volume before changing a length."));

        if (spec.Induction == Induction.Turbocharged)
        {
            warnings.Add(new RecipeWarning(
                "note",
                "On a turbocharged engine the exhaust length below is information rather than a target. A "
                + "turbine does not reflect a usable wave back down the primary, so the manifold is built "
                + $"short and equal-length instead — the {exhaust.TotalPrimaryVolumeCc:N0} cc total volume "
                + "is what decides how fast it spools."));

            // The short runner is a quick-spool choice and not a turbocharged
            // one — see shortRunners above, which asks for both. A turbo build
            // aiming at the top end or at a balance is given the longest runner
            // that packages, and a note telling it the opposite describes
            // somebody else's manifold.
            string lengthNote = spec.Goal == ManifoldGoal.QuickSpool
                ? "and a shorter runner is chosen here for that reason rather than for its pulse"
                : "which the longest runner that packages is adding to — at this goal the pulse is worth "
                  + "having and the volume is what it costs";

            warnings.Add(new RecipeWarning(
                "note",
                $"The plenum and runners come to {intake.TractVolumeCc:N0} cc, {lengthNote}. Keep it in "
                + "proportion though: on an ordinary front-mount installation the intercooler core and its "
                + "two pipe runs are around three quarters of what the compressor has to pressurise, and "
                + "this page cannot see any of it. Plenum multiple is a real lever on spool and a small one."));

            if (spec.ExhaustBackPressureKpa <= TuningMath.AtmosphericKpa * 1.05)
                warnings.Add(new RecipeWarning(
                    "watch",
                    "Exhaust back pressure is set at about atmospheric, which no turbine produces. Manifold "
                    + "pressure typically runs at or above boost, and the gas is denser for it — the primary "
                    + "sizing here will read large until that is set."));
        }
        else if (spec.ManifoldKpa > TuningMath.AtmosphericKpa * 1.05)
        {
            warnings.Add(new RecipeWarning(
                "watch",
                "Manifold pressure is above atmospheric on an engine marked naturally aspirated. The "
                + "exhaust sizing uses it for the mass flow, so it will read larger than it should."));
        }

        if (spec.Induction == Induction.NaturallyAspirated && exhaust.BellLengthMm > 0)
        {
            double gap = Math.Abs(exhaust.Recommended.LengthMm - exhaust.BellLengthMm);

            if (exhaust.Recommended.Order == 2 && gap / exhaust.BellLengthMm > 0.15)
                warnings.Add(new RecipeWarning(
                    "note",
                    $"This primary comes out {exhaust.Recommended.LengthMm:N0} mm where Bell's empirical "
                    + $"formula gives {exhaust.BellLengthMm:N0} mm. The two normally agree within a few per "
                    + "cent; the gap is almost always the mean gas temperature being set away from 600 °C."));
        }
    }
}
