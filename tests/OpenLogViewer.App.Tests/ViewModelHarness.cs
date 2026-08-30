using System.Globalization;
using System.IO;
using OpenLogViewer.Core;

namespace OpenLogViewer.App.Tests;

/// <summary>
/// Builds a view model over a synthetic log written to disk, so the whole path a
/// user takes — open a file, decode it, populate the channel list — is exercised
/// rather than a hand-assembled object graph.
/// </summary>
public sealed class ViewModelHarness : IDisposable
{
    private readonly List<string> _temp = [];

    /// <summary>
    /// Writes a CSV log. Channels are given as name/unit pairs against columns of
    /// values; a "Time" column is added automatically at 10 Hz.
    /// </summary>
    public string WriteCsv(params (string Name, double[] Values)[] channels)
    {
        int rows = channels.Max(c => c.Values.Length);
        var lines = new List<string> { string.Join(',', new[] { "Time" }.Concat(channels.Select(c => c.Name))) };

        for (int r = 0; r < rows; r++)
        {
            var cells = new List<string> { (r * 0.1).ToString(CultureInfo.InvariantCulture) };
            cells.AddRange(channels.Select(c =>
                r < c.Values.Length ? c.Values[r].ToString(CultureInfo.InvariantCulture) : ""));
            lines.Add(string.Join(',', cells));
        }

        string path = Path.Combine(Path.GetTempPath(), $"olv-vm-{Guid.NewGuid():N}.csv");
        File.WriteAllLines(path, lines);
        _temp.Add(path);
        return path;
    }

    /// <summary>A log with the channels a tuner expects, so defaults engage.</summary>
    public string WriteTypicalLog(int samples = 40)
    {
        var rpm = new double[samples];
        var map = new double[samples];
        var afr = new double[samples];
        var clt = new double[samples];
        var target = new double[samples];
        var flat = new double[samples];

        for (int i = 0; i < samples; i++)
        {
            rpm[i] = 800 + i * 100;
            map[i] = 30 + i % 10 * 7;
            afr[i] = 13 + i % 5 * 0.5;
            clt[i] = 120 + i * 2;          // crosses the 160 warm threshold
            target[i] = 14.7;              // constant, as targets usually are
            flat[i] = 42;                  // never moves
        }

        return WriteCsv(
            ("RPM", rpm), ("MAP", map), ("AFR", afr),
            ("CLT", clt), ("AFR Target", target), ("Dead Channel", flat));
    }

    /// <summary>
    /// A view model with settings stored in a temporary directory, so tests never
    /// read or write the user's real presets and filters.
    /// </summary>
    /// <summary>
    /// A view model whose stores are all temporary, for a test that does not
    /// care where they point.
    ///
    /// Constructing one with no arguments at all resolves every store to
    /// %APPDATA%, which reads the user's own presets, filters and remembered
    /// ECUs — and the last of those is recalled into <c>SerialPortNames</c>,
    /// which is static, so a single such view model leaves them visible to every
    /// other test in the run.
    /// </summary>
    public MainViewModel NewViewModel() => NewViewModel(out _);

    public MainViewModel NewViewModel(out string settingsDirectory)
    {
        settingsDirectory = Path.Combine(Path.GetTempPath(), $"olv-settings-{Guid.NewGuid():N}");
        Directory.CreateDirectory(settingsDirectory);
        _temp.Add(settingsDirectory);

        // Every store is pointed at the temporary directory. Left to their
        // defaults they resolve to %APPDATA%, and a test run would read — and
        // save over — the presets, filters and theme the user actually has.
        string directory = settingsDirectory;
        return new MainViewModel(
            new PresetStore(Path.Combine(directory, "presets.json")),
            new FilterStore(Path.Combine(directory, "filters.json")),
            new SettingsStore(Path.Combine(directory, "settings.json")),
            new MathChannelStore(Path.Combine(directory, "math.json")));
    }

    public void Dispose()
    {
        foreach (string path in _temp)
        {
            try
            {
                if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
                else if (File.Exists(path)) File.Delete(path);
            }
            catch (IOException)
            {
                // A leftover temp file is not worth failing a test over.
            }
        }
    }
}
