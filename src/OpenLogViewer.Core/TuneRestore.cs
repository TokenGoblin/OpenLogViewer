namespace OpenLogViewer.Core;

/// <summary>
/// What restoring a saved tune to a controller would actually do.
///
/// <para>
/// Worked out and handed back before anything is sent, because this is the
/// largest thing this application can do to an engine and the only honest way to
/// ask for it is to say what it would change. "Restore this tune" is not a
/// question anybody can answer; "change 47 settings, one of them the rev limit,
/// and leave 900 alone" is.
/// </para>
/// <para>
/// <b>Only what differs is written, and only what the file actually carried.</b>
/// The file is laid over the controller's own bytes rather than over nothing, so
/// a setting the file never mentioned keeps whatever the ECU has — and then the
/// two images are compared byte for byte, so those settings produce no write at
/// all. A tune saved from a neighbouring firmware revision is missing a handful
/// of constants, and the difference between leaving those alone and writing
/// zeros over them is the difference between a restore and a wrecked tune.
/// </para>
/// </summary>
/// <param name="Target">The controller's pages as they would become.</param>
/// <param name="Writes">The bytes to send, gathered into runs.</param>
/// <param name="Differences">
/// Which settings would change, and to what. This is what a person is really
/// being asked about.
/// </param>
/// <param name="Missing">
/// Settings the firmware declares that the file never mentioned. Left exactly as
/// the ECU has them, and counted so the shortfall is visible rather than
/// discovered later.
/// </param>
/// <param name="Unknown">Names in the file this firmware has no constant for.</param>
/// <param name="FileSignature">Which firmware the file says it is for.</param>
/// <param name="EcuSignature">Which firmware the controller answered with.</param>
public sealed record TuneRestorePlan(
    EcuTune Target,
    IReadOnlyList<TuneWrite> Writes,
    IReadOnlyList<TuneDifference> Differences,
    IReadOnlyList<MsqComplaint> Missing,
    IReadOnlyList<string> Unknown,
    string FileSignature,
    string EcuSignature)
{
    /// <summary>Nothing to do: the controller already holds this tune.</summary>
    public bool IsEmpty => Writes.Count == 0;

    public int Bytes => Writes.Sum(w => w.Data.Length);

    /// <summary>Pages this would touch, which are the pages a burn must commit.</summary>
    public IReadOnlyList<int> Pages => [.. Writes.Select(w => w.Page).Distinct().Order()];

    /// <summary>
    /// Whether the file and the controller agree about which firmware this is.
    ///
    /// A mismatch is not fatal on its own — a tune saved from revision 3.4.2 and
    /// a controller running 3.4.3 have almost everything in common — but it is
    /// the single fact most worth putting in front of somebody before they send
    /// eight hundred settings to an engine.
    /// </summary>
    public bool SignaturesAgree =>
        FileSignature.Length == 0 || EcuSignature.Length == 0
        || FileSignature.Equals(EcuSignature, StringComparison.OrdinalIgnoreCase);

    /// <summary>One line saying what this would do.</summary>
    public string Summary =>
        IsEmpty
            ? "The ECU already holds this tune, setting for setting."
            : $"{Differences.Count:N0} setting{(Differences.Count == 1 ? "" : "s")} would change, "
              + $"{Bytes:N0} bytes across {Pages.Count} page{(Pages.Count == 1 ? "" : "s")}."
              + (Missing.Count > 0
                  ? $" {Missing.Count:N0} the firmware declares are not in the file and would be "
                    + "left as the ECU has them."
                  : "");
}

/// <summary>Working out a restore, without doing any of it.</summary>
public static class TuneRestore
{
    /// <summary>
    /// Plans a restore of a saved tune onto a controller.
    /// </summary>
    /// <param name="ecu">The tune as read off the controller.</param>
    /// <param name="file">The saved tune to lay over it.</param>
    /// <param name="ecuSignature">What the controller answered when asked who it is.</param>
    public static TuneRestorePlan Plan(EcuTune ecu, MsqFile file, string ecuSignature = "")
    {
        ArgumentNullException.ThrowIfNull(ecu);
        ArgumentNullException.ThrowIfNull(file);

        // Over the controller's own bytes. Everything the file does not mention
        // keeps what the ECU has, which is what makes the byte comparison below
        // produce no write for it.
        MsqLoad load = MsqApply.Load(ecu.Layout, file, ecu);

        return new TuneRestorePlan(
            load.Tune,
            Differing(ecu, load.Tune),
            TuneCompare.Compare(load.Tune, ecu),
            load.Missing,
            load.Unknown,
            file.Signature,
            ecuSignature);
    }

    /// <summary>
    /// The bytes that differ, gathered into runs.
    ///
    /// The same shape a settings edit produces, and for the same reason: a write
    /// costs a round trip and a run of four bytes is no dearer than one.
    /// </summary>
    private static IReadOnlyList<TuneWrite> Differing(EcuTune ecu, EcuTune target)
    {
        var writes = new List<TuneWrite>();

        for (int page = 0; page < ecu.Pages.Count && page < target.Pages.Count; page++)
        {
            byte[] was = ecu.Pages[page];
            byte[] now = target.Pages[page];

            int start = -1;

            for (int i = 0; i <= now.Length; i++)
            {
                bool differs = i < now.Length && i < was.Length && now[i] != was[i];

                if (differs && start < 0) start = i;
                else if (!differs && start >= 0)
                {
                    writes.Add(new TuneWrite(page, start, now[start..i]));
                    start = -1;
                }
            }
        }

        return writes;
    }
}
