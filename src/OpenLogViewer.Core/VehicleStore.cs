namespace OpenLogViewer.Core;

/// <summary>
/// What somebody typed about their car, as they typed it.
///
/// <para>
/// Text rather than numbers, and every field able to be absent. Text because
/// this is a record of what a person wrote, and "3.82, 2.20, 1.40, 1.00, 0.81"
/// survives a round trip through it unchanged, ready to be corrected rather than
/// re-entered. Absent because absent and zero are different answers: a field
/// nobody has touched must go on following the tune, and a field somebody set to
/// nothing must stay empty.
/// </para>
/// </summary>
public sealed record CarEntry
{
    public string? Mass { get; init; }

    public string? FinalDrive { get; init; }

    public string? GearRatios { get; init; }

    public string? Tyre { get; init; }

    public string? DrivetrainLoss { get; init; }

    public string? Litres { get; init; }

    public string? InjectorCcPerMinute { get; init; }

    public string? Bsfc { get; init; }

    public string? VolumetricEfficiency { get; init; }

    /// <summary>Whether anything at all was entered.</summary>
    public bool IsEmpty =>
        Mass is null && FinalDrive is null && GearRatios is null && Tyre is null
        && DrivetrainLoss is null && Litres is null && InjectorCcPerMinute is null
        && Bsfc is null && VolumetricEfficiency is null;
}

/// <summary>
/// Remembers the car between sittings, so the gearbox and the differential are
/// typed once rather than once per log.
///
/// <para>
/// <b>Only what somebody changed is kept.</b> A dyno reads a good deal about the
/// car out of the tune the log carries — its weight, its rolling diameter — and
/// those figures follow the log they came from. If this store held every field
/// it would hold that tune's figures too, and the next log, from a different car
/// with a different tune, would silently be worked out against the last one's
/// weight. Storing only the corrections means a field nobody has touched keeps
/// following whatever tune is in front of it, and a field somebody has corrected
/// stays corrected — which is the whole reason for correcting it.
/// </para>
/// <para>
/// One car, not a garage. Somebody with two cars is better served by noticing
/// that the wrong differential is on screen than by choosing between saved
/// profiles every time they open a log, and the fields are in plain view.
/// </para>
/// </summary>
public sealed class VehicleStore
{
    public VehicleStore(string? path = null)
    {
        Path = path ?? JsonSettingsFile.InAppData("vehicle.json");
        Reload();
    }

    public string Path { get; }

    /// <summary>The corrections on file. Never null; empty when there are none.</summary>
    public CarEntry Car { get; private set; } = new();

    public void Reload() =>
        Car = JsonSettingsFile.Read<CarFile>(Path)?.Car ?? new CarEntry();

    /// <summary>
    /// Keeps <paramref name="typed"/> where it differs from <paramref name="seeded"/>,
    /// and forgets it where it does not.
    /// </summary>
    public void Save(CarEntry typed, CarEntry seeded)
    {
        ArgumentNullException.ThrowIfNull(typed);
        ArgumentNullException.ThrowIfNull(seeded);

        Car = Differences(typed, seeded);

        JsonSettingsFile.Write(Path, new CarFile { Version = 1, Car = Car });
    }

    /// <summary>Drops the lot, so every field goes back to following the tune.</summary>
    public void Clear() => Save(new CarEntry(), new CarEntry());

    /// <summary>
    /// What is on screen, made from the tune's seed and then the corrections over
    /// the top of it.
    /// </summary>
    public CarEntry Over(CarEntry seeded)
    {
        ArgumentNullException.ThrowIfNull(seeded);

        return new CarEntry
        {
            Mass = Car.Mass ?? seeded.Mass,
            FinalDrive = Car.FinalDrive ?? seeded.FinalDrive,
            GearRatios = Car.GearRatios ?? seeded.GearRatios,
            Tyre = Car.Tyre ?? seeded.Tyre,
            DrivetrainLoss = Car.DrivetrainLoss ?? seeded.DrivetrainLoss,
            Litres = Car.Litres ?? seeded.Litres,
            InjectorCcPerMinute = Car.InjectorCcPerMinute ?? seeded.InjectorCcPerMinute,
            Bsfc = Car.Bsfc ?? seeded.Bsfc,
            VolumetricEfficiency = Car.VolumetricEfficiency ?? seeded.VolumetricEfficiency,
        };
    }

    private static CarEntry Differences(CarEntry typed, CarEntry seeded) => new()
    {
        Mass = Changed(typed.Mass, seeded.Mass),
        FinalDrive = Changed(typed.FinalDrive, seeded.FinalDrive),
        GearRatios = Changed(typed.GearRatios, seeded.GearRatios),
        Tyre = Changed(typed.Tyre, seeded.Tyre),
        DrivetrainLoss = Changed(typed.DrivetrainLoss, seeded.DrivetrainLoss),
        Litres = Changed(typed.Litres, seeded.Litres),
        InjectorCcPerMinute = Changed(typed.InjectorCcPerMinute, seeded.InjectorCcPerMinute),
        Bsfc = Changed(typed.Bsfc, seeded.Bsfc),
        VolumetricEfficiency = Changed(typed.VolumetricEfficiency, seeded.VolumetricEfficiency),
    };

    /// <summary>
    /// The typed value, unless it is the seed again.
    ///
    /// Trimmed on both sides before comparing: a trailing space is not a
    /// correction, and keeping one would pin the field against the tune for good.
    /// </summary>
    private static string? Changed(string? typed, string? seeded)
    {
        if (typed is null) return null;

        return (typed.Trim(), (seeded ?? "").Trim()) switch
        {
            var (t, s) when t == s => null,
            var (t, _) => t,
        };
    }

    private sealed class CarFile
    {
        public int Version { get; set; }

        public CarEntry? Car { get; set; }
    }
}
