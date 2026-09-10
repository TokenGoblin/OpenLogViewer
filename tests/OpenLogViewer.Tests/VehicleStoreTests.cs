using OpenLogViewer.Core;
using Xunit;

namespace OpenLogViewer.Tests;

public class VehicleStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"olv-vehicle-{Guid.NewGuid():N}");

    private string File_ => Path.Combine(_dir, "vehicle.json");

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);

        GC.SuppressFinalize(this);
    }

    /// <summary>What a tune might have offered before anyone corrected it.</summary>
    private static CarEntry Seed() => new()
    {
        Mass = "1,565",
        FinalDrive = "4.10",
        GearRatios = "3.27, 2.05, 1.62, 1.35, 1.03, 0.84",
        Tyre = "648 mm",
        DrivetrainLoss = "15",
        Litres = "2.00",
        InjectorCcPerMinute = "550",
        Bsfc = "0.60",
        VolumetricEfficiency = "85",
    };

    [Fact]
    public void ACorrectionSurvivesAReload()
    {
        var store = new VehicleStore(File_);

        store.Save(Seed() with { FinalDrive = "3.25" }, Seed());

        var reopened = new VehicleStore(File_);

        Assert.Equal("3.25", reopened.Car.FinalDrive);
        Assert.Equal("3.25", reopened.Over(Seed()).FinalDrive);
    }

    /// <summary>
    /// The point of the whole class. A field nobody touched must not be written
    /// down, or the next log's tune would be overruled by the last log's tune.
    /// </summary>
    [Fact]
    public void FieldsLeftAloneAreNotKept()
    {
        var store = new VehicleStore(File_);

        store.Save(Seed() with { FinalDrive = "3.25" }, Seed());

        Assert.Equal("3.25", store.Car.FinalDrive);
        Assert.Null(store.Car.Mass);
        Assert.Null(store.Car.Tyre);
        Assert.Null(store.Car.GearRatios);
    }

    [Fact]
    public void AnUntouchedFieldGoesOnFollowingTheNextTune()
    {
        var store = new VehicleStore(File_);

        store.Save(Seed() with { GearRatios = "3.82, 2.20, 1.40, 1.00, 0.81" }, Seed());

        // A different car's log, whose tune weighs something else entirely.
        CarEntry other = Seed() with { Mass = "1,180", Tyre = "590 mm" };
        CarEntry shown = new VehicleStore(File_).Over(other);

        Assert.Equal("1,180", shown.Mass);
        Assert.Equal("590 mm", shown.Tyre);

        // While the gearbox somebody typed is still theirs.
        Assert.Equal("3.82, 2.20, 1.40, 1.00, 0.81", shown.GearRatios);
    }

    [Fact]
    public void CorrectingAFieldBackToTheSeedForgetsIt()
    {
        var store = new VehicleStore(File_);

        store.Save(Seed() with { Mass = "1,700" }, Seed());
        Assert.Equal("1,700", store.Car.Mass);

        store.Save(Seed(), Seed());

        Assert.Null(store.Car.Mass);
        Assert.True(store.Car.IsEmpty);
        Assert.Equal("1,565", new VehicleStore(File_).Over(Seed()).Mass);
    }

    /// <summary>
    /// A trailing space is not a correction. Kept as one it would pin the field
    /// against every future tune, for a difference nobody can see on screen.
    /// </summary>
    [Fact]
    public void WhitespaceAloneIsNotACorrection()
    {
        var store = new VehicleStore(File_);

        store.Save(Seed() with { Bsfc = "  0.60 " }, Seed());

        Assert.Null(store.Car.Bsfc);
        Assert.True(store.Car.IsEmpty);
    }

    [Fact]
    public void ACorrectionIsTrimmedBeforeItIsKept()
    {
        var store = new VehicleStore(File_);

        store.Save(Seed() with { Bsfc = "  0.55 " }, Seed());

        Assert.Equal("0.55", store.Car.Bsfc);
    }

    /// <summary>
    /// Emptying a field is an answer, and a different one from never having
    /// touched it: the first must be kept, the second must not.
    /// </summary>
    [Fact]
    public void EmptyingAFieldIsKeptAsAnEmptyField()
    {
        var store = new VehicleStore(File_);

        store.Save(Seed() with { Litres = "" }, Seed());

        Assert.Equal("", store.Car.Litres);
        Assert.False(store.Car.IsEmpty);
        Assert.Equal("", new VehicleStore(File_).Over(Seed()).Litres);
    }

    [Fact]
    public void ForgettingPutsEveryFieldBackToTheTune()
    {
        var store = new VehicleStore(File_);

        store.Save(Seed() with { FinalDrive = "3.25", Litres = "3.43" }, Seed());
        store.Clear();

        Assert.True(store.Car.IsEmpty);
        Assert.True(new VehicleStore(File_).Car.IsEmpty);
        Assert.Equal("4.10", new VehicleStore(File_).Over(Seed()).FinalDrive);
    }

    [Fact]
    public void NothingOnFileIsNotAFailure()
    {
        var store = new VehicleStore(File_);

        Assert.True(store.Car.IsEmpty);
        Assert.Equal(Seed(), store.Over(Seed()));
    }

    [Fact]
    public void AMangledFileIsTreatedAsNoFile()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(File_, "{ this is not json");

        var store = new VehicleStore(File_);

        Assert.True(store.Car.IsEmpty);
        Assert.Equal("4.10", store.Over(Seed()).FinalDrive);
    }

    [Fact]
    public void EverySeparateFieldCanBeCorrected()
    {
        var store = new VehicleStore(File_);

        store.Save(
            new CarEntry
            {
                Mass = "1,700",
                FinalDrive = "3.25",
                GearRatios = "3.82, 2.20, 1.40, 1.00, 0.81",
                Tyre = "610 mm",
                DrivetrainLoss = "18",
                Litres = "3.43",
                InjectorCcPerMinute = "440",
                Bsfc = "0.55",
                VolumetricEfficiency = "83",
            },
            Seed());

        CarEntry back = new VehicleStore(File_).Car;

        Assert.Equal("1,700", back.Mass);
        Assert.Equal("3.25", back.FinalDrive);
        Assert.Equal("3.82, 2.20, 1.40, 1.00, 0.81", back.GearRatios);
        Assert.Equal("610 mm", back.Tyre);
        Assert.Equal("18", back.DrivetrainLoss);
        Assert.Equal("3.43", back.Litres);
        Assert.Equal("440", back.InjectorCcPerMinute);
        Assert.Equal("0.55", back.Bsfc);
        Assert.Equal("83", back.VolumetricEfficiency);
    }
}
