using OpenLogViewer.Core;

namespace OpenLogViewer.Oracle;

/// <summary>UnitConvert over every spelling of a unit the logs are known to use, both ways.</summary>
public sealed class W0Units : IEmitter
{
    public string Package => "w0-model";

    public string Set => "units";

    private static readonly string[] Units =
    [
        "°C", "C", "degC", "deg C", "celsius", "Celsius", "°F", "F", "degF", "deg F", "fahrenheit",
        "kPa", "kpa", "KPA", "psi", "PSI", "psig", "km/h", "kph", "kmh", "KPH", "mph", "MPH",
        "AFR", "lambda", "%", "ms", "rpm", "deg", "V", "", " ",
    ];

    private static readonly double[] Values = [-40, 0, 14.7, 100, 101.325, 250, -0.0, double.NaN];

    public IReadOnlyList<object> Emit(EmitContext context)
    {
        var cases = new List<object>();
        foreach (string units in Units)
        {
            foreach (UnitSystem system in Enum.GetValues<UnitSystem>())
            {
                cases.Add(new
                {
                    units,
                    system = system.ToString(),
                    label = UnitConvert.Label(units, system),
                    converts = UnitConvert.Converts(units, system),
                    values = Values.Select(v => new
                    {
                        input = Exact.Bits(v),
                        value = Exact.Bits(UnitConvert.Value(v, units, system)),
                        to_reported = Exact.Bits(UnitConvert.ToReported(v, units, system)),
                    }).ToArray(),
                });
            }
        }
        return cases;
    }
}
