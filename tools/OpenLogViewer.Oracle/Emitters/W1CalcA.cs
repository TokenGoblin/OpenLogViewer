using OpenLogViewer.Core;

namespace OpenLogViewer.Oracle;

/// <summary>
/// The calculators of w1-calc-a over grids of inputs: every double as IEEE bits, every string verbatim.
/// Each case is a named call with its inputs and outputs, and the Rust test replays the same call.
/// </summary>
public sealed class W1CalcA : IEmitter
{
    public string Package => "w1-calc-a";

    public string Set => "calc";

    private static string[] B(params double[] v) => [.. v.Select(Exact.Bits)];

    private static object Case(string name, double[] input, double[] output, string[]? text = null) =>
        new { name, input = B(input), output = B(output), text = text ?? [] };

    private static string Nl(string s) => s.Replace("\r\n", "\n");

    public IReadOnlyList<object> Emit(EmitContext context)
    {
        var cases = new List<object>();

        // ----- turbo sizing ------------------------------------------------------------
        double[] hps = [0, 300, 650, 1200];
        double[] litres = [2.0, 5.7];
        double[] charges = [30, 54.44444444444444];
        foreach (double hp in hps)
            foreach (double l in litres)
                foreach (double c in charges)
                {
                    TurboRequirement r = TurboSizing.Required(hp, 11.5, 0.46, l, 6000, 80, c, 101.325, 6.9, 13.8);
                    cases.Add(Case("turbo_required", [hp, 11.5, 0.46, l, 6000, 80, c, 101.325, 6.9, 13.8],
                        [r.AirLbPerMinute, r.ManifoldKpa, r.BoostKpa, r.PressureRatio,
                         r.CompressorInletKpa, r.CompressorOutletKpa]));
                }

        foreach (double air in (double[])[double.NaN, -1, 0, 20, 45, 57.3, 60, 90, 120, 200])
            foreach (double headroom in (double[])[0, 0.1, 0.4])
            {
                var s = TurboSizing.Suggest(air, headroom, 4);
                cases.Add(Case("turbo_suggest", [air, headroom],
                    [.. s.SelectMany(m => new[] { m.Headroom, m.Count })],
                    [.. s.Select(m => m.Label)]));
            }

        foreach (Turbo t in TurboSizing.Catalogue)
            cases.Add(Case("turbo_flow", [t.RatedHorsepower], [t.MaxFlowLbPerMinute], [t.Model]));

        // ----- octane ------------------------------------------------------------------
        foreach (Blendstock stock in Enum.GetValues<Blendstock>())
            foreach (double aki in (double[])[double.NaN, 85, 87, 91, 95])
                foreach (double sens in (double[])[6, 8, 12])
                    foreach (double f in (double[])[double.NaN, -0.5, 0, 0.1, 0.15, 0.3, 0.5, 0.85, 1, 3])
                    {
                        OctaneResult r = OctaneBlend.Blend(aki, sens, stock, f);
                        cases.Add(Case("octane_blend", [(double)(int)stock, aki, sens, f],
                            [r.Ron, r.Mon, r.AntiKnockIndex, r.EthanolByVolume, r.AlcoholMoleFraction,
                             r.HeatOfVaporisationKjPerKg, r.CoolingKjPerKgAir]));
                    }

        foreach (Blendstock stock in Enum.GetValues<Blendstock>())
            cases.Add(Case("octane_charts", [(double)(int)stock], [],
                [Nl(OctaneBlend.Chart(stock)), Nl(OctaneBlend.CoolingChart(stock)), OctaneBlend.Name(stock)]));

        // ----- recipe ------------------------------------------------------------------
        RecipeSpec[] specs =
        [
            new(),
            new() { TargetHorsepower = 650, Fuel = Fuel.E85, VolumetricEfficiency = 95 },
            new() { TargetHorsepower = 1400, PeakPowerRpm = 11000 },
            new() { Litres = 8.0, TargetHorsepower = 300 },
            new() { TargetHorsepower = 500, PeakTorqueRpm = 1200, Fuel = Fuel.Methanol },
            new() { TargetHorsepower = 800, PeakTorqueRpm = 7000, PeakPowerRpm = 6000, Cylinders = 6, Litres = 3.0 },
            new() { TargetHorsepower = 0 },
            new() { Litres = 0 },
        ];
        foreach (RecipeSpec s in specs)
        {
            Recipe r = EngineRecipe.Build(s);
            cases.Add(Case("recipe",
                [s.Litres, s.Cylinders, s.TargetHorsepower, s.PeakTorqueRpm, s.PeakPowerRpm, (double)(int)s.Fuel,
                 s.Lambda, s.PetrolBsfc, s.VolumetricEfficiency, s.ChargeCelsius, s.InjectorDutyLimit, s.RailPsi,
                 s.PumpHeadroomPercent, s.BarometricKpa, s.InletLossKpa, s.ChargeLossKpa],
                [r.AirAtPeakPower, r.AirAtPeakTorque, r.Afr, r.Bsfc, r.ManifoldKpa, r.BoostKpa, r.PressureRatio,
                 r.InjectorCcEach, r.InjectorLbHrEach, r.FuelLitresPerHour, r.PumpLitresPerHour,
                 r.RailUnderBoostPsi, r.MeanPistonSpeed, r.SpecificOutput, r.Turbos.Count, r.Pumps.Count,
                 .. r.Turbos.SelectMany(m => new[] { m.Headroom, m.Count }),
                 .. r.Pumps.SelectMany(p => new[] { (double)p.Count, p.DeliveredLitresPerHour })],
                [.. r.Turbos.Select(m => m.Label), .. r.Warnings.Select(w => w.Severity + "|" + w.Text)]));
        }

        // ----- geometry ----------------------------------------------------------------
        foreach (double bore in (double[])[0, 70, 86, 101.6])
            foreach (double stroke in (double[])[0, 64, 86, 88.4])
            {
                double swept = EngineGeometry.SweptVolumeCc(bore, stroke);
                double clearance = EngineGeometry.ClearanceVolumeCc(bore, 42, 87, 1.0, 0.5, 5);
                cases.Add(Case("geometry", [bore, stroke],
                    [swept, EngineGeometry.DisplacementCc(bore, stroke, 4), EngineGeometry.BoreToStroke(bore, stroke),
                     EngineGeometry.MeanPistonSpeed(stroke, 7000),
                     EngineGeometry.MeanPistonSpeedFeetPerMinute(stroke, 7000), clearance,
                     EngineGeometry.CompressionRatio(swept, clearance), EngineGeometry.ClearanceForRatio(swept, 10),
                     EngineGeometry.BoostedCompressionIndex(10, 200)],
                    [EngineGeometry.PistonSpeedVerdict(EngineGeometry.MeanPistonSpeed(stroke, 7000))]));
            }
        foreach (double v in (double[])[double.NaN, -1, 0, 5, 12, 15, 18, 21, 22, 24, 25, 27, 28, 40])
            cases.Add(Case("piston_verdict", [v], [], [EngineGeometry.PistonSpeedVerdict(v)]));

        // ----- charge air --------------------------------------------------------------
        foreach (double pr in (double[])[0.5, 1, 1.5, 2.36, 3.5])
            foreach (double eff in (double[])[double.NaN, -1, 0, 0.7, 0.78, 1, 1.5])
                cases.Add(Case("compressor_outlet", [70, pr, eff], [ChargeAir.CompressorOutletF(70, pr, eff)]));
        foreach (double e in (double[])[-0.1, 0, 0.7, 1, 1.1])
            cases.Add(Case("outlet", [285, 90, e],
                [ChargeAir.OutletF(285, 90, e), ChargeAir.EffectivenessFrom(285, 148.5, 90),
                 ChargeAir.DensityRatio(285, 148.5), ChargeAir.PressureRatio(20), ChargeAir.HeatLoadBtuPerMin(40, 150),
                 ChargeAir.PressureLossFraction(2, 20)]));
        foreach (CoreMaterial m in CoreMaterials.All)
            cases.Add(Case("metal_share", [m.ConductivityWmK],
                [CoreMaterials.MetalShareOfResistance(m), CoreMaterials.MetalShareOfResistance(m, 0.8, 50)],
                [m.Name]));
        var core = new IntercoolerCore(24, 12, 3);
        cases.Add(Case("core", [24, 12, 3],
            [core.FrontalAreaSqIn, core.VolumeCuIn, core.LoadingPerCuIn(1440), core.LoadingPerFrontalSqIn(1440)]));

        // ----- sprayables --------------------------------------------------------------
        foreach (double mv in (double[])[-1, 0, 0.3, 0.5, 0.7, 1, 2])
        {
            Sprayable b = Sprayables.Blend("b", mv);
            cases.Add(Case("spray_blend", [mv],
                [b.LatentBtuPerLb, b.DensityGPerCc, b.SpecificHeatBtuPerLbF, b.CombustibleFraction, b.FuelLhvBtuPerLb],
                [b.Note]));
        }
        foreach (Sprayable f in Sprayables.All)
            foreach (double air in (double[])[0, 40])
                foreach (double evap in (double[])[0.01, 0.5, 0.75, 1, 2])
                {
                    double cc = ChemicalIntercooling.FlowCcPerMin(air, 100, f, 250, 70, evap);
                    cases.Add(Case("spray_flow", [air, evap],
                        [ChemicalIntercooling.FlowLbPerMin(air, 100, f, 250, 70, evap), cc,
                         ChemicalIntercooling.DropFFor(500, air, f, 250, 70, evap),
                         f.CoolingBtuPerLb(40, 300), f.CoolingBtuPerLb(300, 250),
                         ChemicalIntercooling.PetrolDisplacedLbPerMin(2, f),
                         ChemicalIntercooling.TankMinutes(5, 500), ChemicalIntercooling.GallonsPerHour(500),
                         ChemicalIntercooling.PercentOfFuel(1.5, 5), f.LbPerCc, f.CcPerMinFromLbPerMin(1.5)],
                        [f.Name, f.Note]));
                }

        return cases;
    }
}
