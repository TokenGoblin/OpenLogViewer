using OpenLogViewer.Core;

namespace OpenLogViewer.Oracle;

/// <summary>
/// ManifoldTuning over grids of inputs: every double as IEEE bits, every string verbatim.
/// Each case is a named call with its inputs and outputs, and the Rust test replays the same call.
/// The "charts" of this calculator are its verdict strings and its warning text.
/// </summary>
public sealed class W2Manifold : IEmitter
{
    public string Package => "w2-manifold";

    public string Set => "manifold";

    private static string[] B(params double[] v) => [.. v.Select(Exact.Bits)];

    private static object Case(string name, double[] input, double[] output, string[]? text = null) =>
        new { name, input = B(input), output = B(output), text = text ?? [] };

    private static string Nl(string s) => s.Replace("\r\n", "\n");

    private static double[] Flat(TuningOrder o) =>
        [o.Order, o.EffectiveLengthMm, o.LengthMm, o.Rpm, o.Practical ? 1 : 0];

    private static double[] Flat(IReadOnlyList<TuningOrder> orders) =>
        [orders.Count, .. orders.SelectMany(Flat)];

    private static double[] SpecIn(ManifoldSpec s) =>
    [
        s.Litres, s.Cylinders, (int)s.Induction, (int)s.Goal, s.PeakTorqueRpm, s.PeakPowerRpm,
        s.IntakeDurationDeg, s.ExhaustDurationDeg, s.IntakeAirCelsius, s.ExhaustCelsius,
        s.VolumetricEfficiency, s.CompressionRatio, s.ManifoldKpa, s.Afr, s.ExhaustBackPressureKpa,
        s.IntakeRunnerDiameterMm, s.ExhaustPrimaryDiameterMm, s.PlenumMultiple,
    ];

    public IReadOnlyList<object> Emit(EmitContext context)
    {
        var cases = new List<object>();
        double atm = TuningMath.AtmosphericKpa;

        // ----- the gas and the wave ----------------------------------------------------
        foreach (double c in (double[])[double.NaN, -300, -273.15, -123.15, 0, 20, 30, 150, 600, 900])
            cases.Add(Case("sound", [c],
                [ManifoldTuning.SpeedOfSoundAir(c), ManifoldTuning.SpeedOfSoundExhaust(c),
                 ManifoldTuning.SpeedOfSound(c, 1.4, 287)]));

        foreach (double a in (double[])[0, 343.2, 345, 700])
            foreach (double w in (double[])[0, 180, 230, 240])
                foreach (int order in (int[])[-1, 0, 1, 2, 3, 6])
                    foreach (double rpm in (double[])[0, 3500, 6000, 8000])
                        cases.Add(Case("wave", [a, w, order, rpm],
                            [ManifoldTuning.TunedLengthMm(a, w, order, rpm), ManifoldTuning.TunedRpm(a, w, order, rpm)]));

        foreach (double d in (double[])[double.NaN, -1, 0, 28, 35.5, 40, 48, 60, 170, 181, 250, 300])
            cases.Add(Case("geometry", [d],
                [ManifoldTuning.EndCorrectionMm(d), ManifoldTuning.AreaMm2(d), ManifoldTuning.DiameterMm(d),
                 ManifoldTuning.CollectorDiameterMm(d, 4), ManifoldTuning.CollectorDiameterMm(d, 1),
                 ManifoldTuning.CollectorLengthMm(d), ManifoldTuning.PipeVolumeCc(500, d),
                 ManifoldTuning.PipeVolumeCc(d, 40), ManifoldTuning.ExhaustWaveWindowDeg(d),
                 ManifoldTuning.BellPrimaryLengthInches(d, 6500)]));

        // ----- velocity and its verdicts -----------------------------------------------
        foreach (double swept in (double[])[0, 500, 625])
            foreach (double ve in (double[])[0, 100, 105])
                foreach (double rpm in (double[])[0, 3500, 7000])
                    foreach (double win in (double[])[0, 240, 300])
                        foreach (double x in (double[])[0, 95, 972.2])
                        {
                            double v = ManifoldTuning.PortVelocity(swept, ve, rpm, win, x);
                            cases.Add(Case("velocity", [swept, ve, rpm, win, x],
                                [v, ManifoldTuning.AreaForVelocity(swept, ve, rpm, win, x)],
                                [ManifoldTuning.VelocityVerdict(v), ManifoldTuning.ExhaustVelocityVerdict(v)]));
                        }

        foreach (double v in (double[])[double.NaN, -1, 0, 59, 60, 79, 80, 100, 109, 110, 129, 130,
                     149, 150, 164, 165, 190, 214, 215, 259, 260, 300])
            cases.Add(Case("verdict", [v], [],
                [ManifoldTuning.VelocityVerdict(v), ManifoldTuning.ExhaustVelocityVerdict(v)]));

        // ----- the plenum --------------------------------------------------------------
        foreach (double swept in (double[])[0, 500])
            foreach (double cr in (double[])[0.5, 1, 8, 10, 14])
                cases.Add(Case("effective_cylinder", [swept, cr], [ManifoldTuning.EffectiveCylinderVolumeCc(swept, cr)]));

        foreach (double a in (double[])[0, 345])
            foreach (double area in (double[])[0, 500, 1000])
                foreach (double len in (double[])[0, 200, 400])
                    foreach (double vol in (double[])[0, 300, 305.56])
                    {
                        double hz = ManifoldTuning.HelmholtzHz(a, area, len, vol);
                        cases.Add(Case("helmholtz", [a, area, len, vol],
                            [hz, ManifoldTuning.HelmholtzRatio(hz, 6000), ManifoldTuning.HelmholtzRatio(hz, 0)]));
                    }

        foreach (double disp in (double[])[0, 2000])
            foreach (double mult in (double[])[0, 0.5, 1.4])
                cases.Add(Case("plenum_volume", [disp, mult],
                    [ManifoldTuning.PlenumVolumeCc(disp, mult),
                     ManifoldTuning.TractShareOfTypicalInstallation(disp, mult * 1000)]));

        foreach (Induction ind in Enum.GetValues<Induction>())
            foreach (double m in (double[])[double.NaN, -1, 0, 0.3, 0.5, 0.64, 0.65, 0.75, 0.89, 0.9, 1, 1.14,
                         1.15, 1.25, 1.39, 1.4, 1.5, 1.64, 1.65, 1.75, 1.89, 1.9, 2, 3])
                cases.Add(Case("plenum_verdict", [(int)ind, m], [], [ManifoldTuning.PlenumVerdict(m, ind)]));

        // ----- the exhaust side --------------------------------------------------------
        foreach (double swept in (double[])[0, 500])
            foreach (double ve in (double[])[0, 100])
                foreach (double map in (double[])[0, atm, atm * 2])
                    foreach (double charge in (double[])[-300, 30])
                        foreach (double exh in (double[])[-300, 600])
                            foreach (double bp in (double[])[0, atm, atm * 2])
                                foreach (double afr in (double[])[0, 12.5])
                                    cases.Add(Case("exhaust_volume", [swept, ve, map, charge, exh, bp, afr],
                                        [ManifoldTuning.ExhaustVolumePerCycleCc(swept, ve, map, charge, exh, bp, afr)]));

        // ----- goals -------------------------------------------------------------------
        foreach (ManifoldGoal g in Enum.GetValues<ManifoldGoal>())
            foreach (Induction ind in Enum.GetValues<Induction>())
                foreach ((double t, double p) in ((double, double)[])[(3500, 7500), (4500, 7000), (7000, 4000)])
                    cases.Add(Case("goals", [(int)g, (int)ind, t, p],
                        [ManifoldTuning.IntakeTuningRpm(g, t, p), ManifoldTuning.TargetIntakeVelocity(g),
                         ManifoldTuning.TargetExhaustVelocity(g), ManifoldTuning.DefaultPlenumMultiple(g, ind),
                         ManifoldTuning.MaxRunnerForGoal(g)]));

        // ----- orders and the pick -----------------------------------------------------
        foreach (double a in (double[])[0, 345, 700])
            foreach (double w in (double[])[0, 230, 240])
                foreach (double rpm in (double[])[0, 1800, 4000, 6000, 9000])
                    foreach (double d in (double[])[0, 34, 40])
                        foreach (double max in (double[])[400, 650, 1100])
                            foreach (int highest in (int[])[3, 6])
                            {
                                var list = ManifoldTuning.Orders(a, w, rpm, d, max, highest);
                                cases.Add(Case("orders", [a, w, rpm, d, max, highest],
                                    [.. Flat(list), .. Flat(ManifoldTuning.Recommend(list, max)),
                                     .. Flat(ManifoldTuning.Recommend(list, max, true))]));
                            }

        // ----- whole plans -------------------------------------------------------------
        var plans = new List<ManifoldSpec>();
        foreach (ManifoldGoal g in Enum.GetValues<ManifoldGoal>())
            foreach (Induction ind in Enum.GetValues<Induction>())
            {
                var b = new ManifoldSpec { Goal = g, Induction = ind };
                plans.Add(b);
                plans.Add(b with { PeakTorqueRpm = 3500, PeakPowerRpm = 7000 });
                plans.Add(b with
                {
                    PeakTorqueRpm = 3500, VolumetricEfficiency = 105, ManifoldKpa = atm + 150,
                    ExhaustBackPressureKpa = atm + 170,
                });
                plans.Add(b with { Litres = 5.7, Cylinders = 8, PeakPowerRpm = 6000, PeakTorqueRpm = 3900 });
                plans.Add(b with { PeakTorqueRpm = 1800, PeakPowerRpm = 5000 });
                plans.Add(b with { Litres = 1.0, IntakeRunnerDiameterMm = 60, PeakPowerRpm = 6000, PeakTorqueRpm = 4000 });
                plans.Add(b with { ExhaustPrimaryDiameterMm = 38, PlenumMultiple = 0.75, CompressionRatio = 14 });
                plans.Add(b with { PeakTorqueRpm = 7000, PeakPowerRpm = 4000 });
                plans.Add(b with { IntakeDurationDeg = 150, ExhaustDurationDeg = 170 });
                plans.Add(b with { IntakeDurationDeg = 340, ExhaustDurationDeg = 300 });
                plans.Add(b with { IntakeDurationDeg = 280, ExhaustDurationDeg = 290, IntakeAirCelsius = 20, ExhaustCelsius = 700 });
                plans.Add(b with { Litres = 0, Cylinders = 0 });
                plans.Add(b with { Cylinders = 1, Litres = 0.6, Afr = 0, ExhaustCelsius = -400 });
                plans.Add(b with { ManifoldKpa = atm + 100 });
                plans.Add(b with { ExhaustBackPressureKpa = atm * 2, ManifoldKpa = atm * 2 });
            }

        foreach (ManifoldSpec s in plans)
        {
            ManifoldPlan p = ManifoldTuning.Plan(s);
            IntakePlan i = p.Intake;
            ExhaustPlan e = p.Exhaust;

            cases.Add(Case("plan", SpecIn(s),
                [i.SpeedOfSound, i.TunedRpm, i.RunnerDiameterMm, i.RunnerAreaMm2, i.RunnerVolumeCc,
                 i.VelocityAtPeakPower, i.VelocityAtPeakTorque, i.MachAtPeakPower, i.PlenumVolumeCc,
                 i.PlenumMultiple, i.TractVolumeCc, i.HelmholtzHz, i.HelmholtzRatio, .. Flat(i.Recommended), .. Flat(i.Orders),
                 e.SpeedOfSound, e.TunedRpm, e.WaveWindowDeg, e.PrimaryDiameterMm, e.PrimaryVolumeCc,
                 e.TotalPrimaryVolumeCc, e.VelocityAtPeakPower, e.CollectorDiameterMm, e.CollectorLengthMm,
                 e.BellLengthMm, .. Flat(e.Recommended), .. Flat(e.Orders)],
                [.. p.Warnings.Select(w => Nl($"{w.Severity}|{w.Text}"))]));
        }

        return cases;
    }
}
