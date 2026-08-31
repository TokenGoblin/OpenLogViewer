using System.IO;
using OpenLogViewer.Core;

namespace OpenLogViewer.App;

/// <summary>
/// Opening, saving and comparing <c>.msq</c> tunes.
///
/// <para>
/// Kept beside the rest of the view model rather than in it because it is one
/// subject: a tune in a file. The three things anybody does with one are back
/// the ECU up, open somebody else's tune to look at, and ask whether the file
/// and the controller still agree.
/// </para>
/// </summary>
public sealed partial class MainViewModel
{
    /// <summary>The signature the connected ECU answered with, for writing into a file.</summary>
    private string _ecuSignature = "";

    /// <summary>
    /// Which build the tune on show came from, so a definition is read the same
    /// way it was written. Empty means whatever the reader assumes.
    /// </summary>
    private IReadOnlySet<string>? _tuneSymbols;

    /// <summary>The file the tune on show came from, whose PC variables belong to it.</summary>
    private MsqFile? _tuneFile;

    /// <summary>
    /// True while the tune on show was opened from a file rather than read off
    /// the controller.
    ///
    /// It is a real tune — worth looking at, worth saving again, worth comparing
    /// against what is attached — but nothing here may be sent. Sending would
    /// write every setting the file carries and, for every setting it does not,
    /// a zero. Restoring a whole tune to an engine is a deliberate act, not
    /// something that falls out of having opened a file.
    /// </summary>
    public bool TuneIsFromFile { get; private set; }

    /// <summary>Whether there is a tune worth writing to a file.</summary>
    public bool CanSaveTune => _ecuTune is not null && _tuneLayout is not null && !TuneIsPlaceholder;

    /// <summary>
    /// Writes the tune on show to an <c>.msq</c>.
    ///
    /// <para>
    /// The point of it: a tune that exists only in an ECU is one power supply
    /// away from being gone, and this is the format every other tool in this
    /// world reads, so the file opens in TunerStudio and can be sent to whoever
    /// is helping.
    /// </para>
    /// <para>
    /// Refused for a tune opened from a definition file, whose every value is a
    /// zero standing in for one. Writing that out would produce a file that
    /// looks like a tune and is not.
    /// </para>
    /// </summary>
    public string SaveTuneToFile(string path, string comment = "")
    {
        if (_ecuTune is not { } tune) return "There is no tune to save.";
        if (TuneIsPlaceholder)
            return "This is a firmware definition rather than a tune — every value in it reads as "
                   + "zero, so there is nothing worth saving.";

        try
        {
            MsqWriter.Save(path, tune, _ecuSignature, _tuneSymbols, comment, _tuneFile);

            return $"Saved {tune.Pages.Sum(p => p.Length):N0} bytes of settings to "
                   + $"{Path.GetFileName(path)}.";
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return $"Could not write {Path.GetFileName(path)}: {e.Message}";
        }
    }

    /// <summary>
    /// Opens a saved tune, finding the firmware definition it belongs to.
    ///
    /// <para>
    /// The file names its own firmware, so the definition is found by that
    /// rather than asked for. It also names the build it came from, and those
    /// symbols are used to read the definition — the same file describes a
    /// Fahrenheit build and a Celsius one, and reading it the wrong way scales
    /// every temperature in the tune while still looking like a number.
    /// </para>
    /// <para>
    /// What did not fit is said out loud. A tune from a neighbouring revision
    /// leaves a handful of settings at zero and is worth opening anyway; one
    /// from another firmware leaves most of them at zero and is not.
    /// </para>
    /// </summary>
    public bool OpenSavedTune(string path)
    {
        try
        {
            MsqFile file = MsqFile.ReadFile(path);

            if (file.Signature.Length == 0)
            {
                EcuTuneSummary =
                    $"{Path.GetFileName(path)} does not say which firmware it is for, so there is "
                    + "no way to know what its numbers mean.";
                return false;
            }

            // The tune's own symbols where it states them. A file that names
            // none leaves the reader's assumption alone rather than replacing it
            // with nothing, which would take the wrong branch everywhere.
            IReadOnlySet<string>? symbols = file.Symbols.Count > 0 ? file.Symbols : null;

            if (BestDefinitionFor(file, symbols) is not var (ini, iniText, layout, load))
            {
                Workspace.EnsureDefinitions([file.Signature]);

                EcuTuneSummary =
                    $"{Path.GetFileName(path)} is a tune for \"{file.Signature}\", and no definition "
                    + $"here fits it. Put its .ini in {Workspace.Definitions} and try again.";
                return false;
            }

            _tuneLayout = layout;
            _ecuTune = load.Tune;
            // Every reader gets the tune's own build symbols. Two of the five
            // were given the defaults instead, so a definition declaring its
            // tables or its derived channels inside an #if — an MS3 has about a
            // hundred conditionals and a Speeduino most of that — resolved the
            // wrong branch: a table list and axis constants from a build the
            // tune was never saved from, decoded through a layout from the
            // right one. Nothing about that looks wrong on screen. The axes are
            // simply somebody else's.
            _ecuTableDefinitions = TableEditorReader.Read(iniText, symbols);
            _ecuInterface = TuneInterfaceReader.Read(iniText, symbols);
            _ecuCurves = TuneCurveReader.Read(iniText, symbols);
            _curveNames = Named(_ecuCurves, _ecuTune);
            _derived = DerivedChannels.Read(iniText, symbols);
            _settingsEdit = new TuneSettingsEdit(_ecuTune);

            _tuneFile = file;
            _tuneSymbols = symbols;
            _ecuSignature = file.Signature;

            // A real tune rather than a definition standing in for one, so it is
            // worth saving and worth comparing — but not sendable. Sending would
            // mean writing every setting the file carries to the controller, and
            // every one it does not carry as a zero. Restoring a whole tune is a
            // deliberate act with its own confirmation, not something to fall
            // out of having opened a file.
            TuneIsPlaceholder = false;
            TuneIsFromFile = true;
            _settingsPagesWritten.Clear();
            OpenDialog = null;
            OpenCurves = [];
            _openMenuEntry = null;

            EcuTables.Clear();
            foreach (TuneTable table in Ordered(_ecuTune.Tables(_ecuTableDefinitions))) EcuTables.Add(table);
            EcuTableChoices.Refresh();

            BuildSettingsMenu();

            // Says which definition was used where it was not the one the file
            // named, because that is the difference between "this is your tune"
            // and "this is your tune read through a neighbouring revision".
            string against = ini.Signature.Equals(file.Signature, StringComparison.OrdinalIgnoreCase)
                ? ""
                : $" Read against {Path.GetFileName(ini.Path)} (\"{ini.Signature}\"), which is not "
                  + $"the \"{file.Signature}\" this tune was saved from.";

            EcuTuneSummary =
                $"{Path.GetFileName(path)} — {EcuTables.Count} tables · "
                + $"{SettingsMenu.Count(m => !m.IsHeading):N0} pages. {load.Summary}{against}";

            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or LogFormatException)
        {
            EcuTuneSummary = $"Could not read {Path.GetFileName(path)}: {e.Message}";
            return false;
        }
        finally
        {
            Raise(nameof(HasEcuTune));
            Raise(nameof(NoEcuTune));
            Raise(nameof(ShowNoTuneNotice));
            Raise(nameof(EcuTableSummary));
            Raise(nameof(EcuTuneSummary));
            Raise(nameof(HasSettingsPages));
            Raise(nameof(SettingsSummary));
            Raise(nameof(OpenDialog));
            Raise(nameof(OpenMenuEntry));
            CurveChanged();
            RaiseWriteGates();
        }
    }

    /// <summary>
    /// The definition this tune fits best, or nothing if none of them do.
    ///
    /// <para>
    /// Chosen by trying rather than by matching the signature exactly, because
    /// exactly is too strict to be useful: a tune saved from MS2Extra
    /// comms342a2 is opened against comms342h2 all the time, and 951 of its 955
    /// settings land correctly. Refusing that leaves a tuner with a file they
    /// cannot open and no way to say what is in it.
    /// </para>
    /// <para>
    /// A definition whose signature matches exactly is preferred over any other
    /// whatever the count says — the firmware's own word beats an inference. The
    /// rest are ranked by how much of the definition the file actually fills,
    /// which is a measurement rather than a guess, and a definition the file
    /// leaves more than half empty is not offered at all.
    /// </para>
    /// </summary>
    private (IniFile Ini, string Text, TuneLayout Layout, MsqLoad Load)? BestDefinitionFor(
        MsqFile file, IReadOnlySet<string>? symbols)
    {
        (IniFile Ini, string Text, TuneLayout Layout, MsqLoad Load)? best = null;
        bool bestIsExact = false;

        foreach (IniFile candidate in IniCatalog.Scan(Workspace.DefinitionSearchPaths))
        {
            string text;
            TuneLayout layout;

            try
            {
                text = TuningText.Read(candidate.Path);
                layout = TuneLayoutReader.Read(text, symbols);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or LogFormatException)
            {
                continue;
            }

            if (layout.Pages.Count == 0) continue;

            MsqLoad load = MsqApply.Load(layout, file);
            if (load.LooksLikeAnotherFirmware) continue;

            bool exact = candidate.Signature.Equals(file.Signature, StringComparison.OrdinalIgnoreCase);

            if (bestIsExact && !exact) continue;
            if (best is null || (exact && !bestIsExact) || load.Applied > best.Value.Load.Applied)
            {
                best = (candidate, text, layout, load);
                bestIsExact = exact;
            }
        }

        return best;
    }

    /// <summary>
    /// A restore that has been worked out and not yet done.
    ///
    /// Held between the two halves of the act deliberately: what would change is
    /// worked out first and put in front of somebody, and only then is it sent.
    /// "Restore this tune" is not a question anyone can answer; "change 47
    /// settings, one of them the rev limit, and leave 900 alone" is.
    /// </summary>
    public TuneRestorePlan? PendingRestore { get; private set; }

    /// <summary>
    /// Works out what restoring a saved tune onto the connected ECU would do.
    ///
    /// Nothing is sent. The plan is kept so <see cref="ApplyRestore"/> can carry
    /// out the very thing that was described, rather than working it out a
    /// second time against bytes that may have moved in between.
    /// </summary>
    public string PlanRestore(string path)
    {
        PendingRestore = null;
        Raise(nameof(PendingRestore));
        Raise(nameof(CanApplyRestore));

        // What the tune is comes before whether there is a link, as it does
        // everywhere else that writes: the tune in hand has to be the
        // controller's own. Planning against a placeholder or against another
        // file's values works out the difference between two things that are
        // both not the ECU, and says nothing about either.
        if (TuneIsPlaceholder || TuneIsFromFile)
            return "Read the ECU's own tune first. A restore is worked out against what the "
                   + "controller actually holds, and this is not that.";

        if (_ecuConnection is null) return "Not connected to an ECU.";
        if (_ecuTune is not { } ecu || _tuneLayout is null) return "No tune has been read.";

        try
        {
            MsqFile file = MsqFile.ReadFile(path);
            TuneRestorePlan plan = TuneRestore.Plan(ecu, file, _ecuSignature);

            PendingRestore = plan;
            Raise(nameof(PendingRestore));
            Raise(nameof(CanApplyRestore));

            string whose = Path.GetFileName(path);

            if (!plan.SignaturesAgree)
                return $"{whose} is a tune for \"{plan.FileSignature}\" and this ECU says it is "
                       + $"\"{plan.EcuSignature}\". {plan.Summary}";

            return $"{whose}: {plan.Summary}";
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or LogFormatException)
        {
            return $"Could not read {Path.GetFileName(path)}: {e.Message}";
        }
    }

    public bool CanApplyRestore =>
        PendingRestore is { IsEmpty: false } && _ecuConnection is not null
        && !TuneIsPlaceholder && !TuneIsFromFile;

    /// <summary>Forgets a planned restore without doing any of it.</summary>
    public void CancelRestore()
    {
        PendingRestore = null;
        Raise(nameof(PendingRestore));
        Raise(nameof(CanApplyRestore));
    }

    /// <summary>
    /// Carries out the restore that was planned.
    ///
    /// <para>
    /// Every page is checked before the first byte goes out, because a restore
    /// that stops half way is a tune that is neither the old one nor the new
    /// one — and unlike a single setting there is no reading the result off the
    /// screen to see which.
    /// </para>
    /// <para>
    /// Nothing is burned. What lands is in the controller's working memory and a
    /// power cycle undoes all of it, which is the right footing for the largest
    /// change this can make: it can be tried and walked away from.
    /// </para>
    /// </summary>
    public string ApplyRestore()
    {
        if (TuneIsPlaceholder || TuneIsFromFile)
            return "Read the ECU's own tune first. A restore is worked out against what the "
                   + "controller actually holds, and this is not that.";

        if (PendingRestore is not { } plan) return "Nothing has been planned.";
        if (_ecuConnection is not { } connection) return "Not connected to an ECU.";
        if (_ecuTune is not { } tune || _tuneLayout is not { } layout) return "No tune has been read.";

        if (plan.IsEmpty) return "The ECU already holds this tune, so nothing was sent.";

        foreach (TuneWrite write in plan.Writes)
        {
            TunePage? page = layout.Pages.FirstOrDefault(p => p.Index == write.Page);

            if (page is null || page.ChunkWriteCommand.Length == 0)
                return $"This firmware declares no way to write page {write.Page}, "
                       + "so nothing was sent.";
        }

        int settings = plan.Differences.Count;
        int done = 0;

        try
        {
            foreach (TuneWrite write in plan.Writes)
            {
                TunePage page = layout.Pages.First(p => p.Index == write.Page);

                connection.WriteTunePage(
                    page, layout.BlockingFactor, layout.LittleEndian, write.Offset, write.Data,
                    layout.InterWriteDelay);

                tune.Accept(write);
                _settingsEdit?.Accept(write);
                _settingsPagesWritten.Add(write.Page);
                done++;
            }

            return $"Restored {settings:N0} setting{(settings == 1 ? "" : "s")} to the ECU — "
                   + $"{plan.Bytes:N0} bytes across {plan.Pages.Count} "
                   + $"page{(plan.Pages.Count == 1 ? "" : "s")}. It is running this now, and will "
                   + "forget it at the next power cycle unless you burn it.";
        }
        catch (Exception e) when (e is EcuProtocolException or IOException or InvalidOperationException)
        {
            return $"The restore stopped after {done} of {plan.Writes.Count} writes: {e.Message} "
                   + "The ECU is now holding part of this tune and part of the old one. Nothing "
                   + "has been burned, so a power cycle puts back what was last burned.";
        }
        finally
        {
            // Done or half done, the plan described bytes that have moved. It is
            // not something to press again.
            CancelRestore();

            // Everything a settings write does afterwards. The tables and the
            // curves on screen came off the controller too, and the controller
            // has moved.
            _settingsEdit?.Reconcile();
            OnSettingChanged();
            Raise(nameof(CanWriteSettings));
            Raise(nameof(CanBurnSettings));
            RefreshOpenTune();
        }
    }

    /// <summary>What the last comparison found, most recently asked first.</summary>
    public IReadOnlyList<TuneDifference> TuneDifferences { get; private set; } = [];

    /// <summary>
    /// Says what a saved tune and the tune on show disagree about.
    ///
    /// The question a tuner asks constantly and no log can answer: is this
    /// really what the ECU is running? A file and a controller drift apart the
    /// moment somebody changes something without saving, and both remain pages
    /// of plausible numbers.
    /// </summary>
    public string CompareWithSavedTune(string path)
    {
        if (_ecuTune is not { } mine || _tuneLayout is not { } layout)
            return "There is no tune to compare against. Connect to an ECU or open a tune first.";

        try
        {
            MsqFile file = MsqFile.ReadFile(path);

            // Laid over the tune in hand rather than over nothing, so that bits
            // no constant declares — which a file cannot carry — do not come
            // back as differences. A setting missing from the file is reported
            // as missing, not as a change to zero.
            MsqLoad load = MsqApply.Load(layout, file, mine);

            TuneDifferences = TuneCompare.Compare(load.Tune, mine);
            Raise(nameof(TuneDifferences));

            string whose = Path.GetFileName(path);

            if (file.Signature.Length > 0 && _ecuSignature.Length > 0
                && !file.Signature.Equals(_ecuSignature, StringComparison.OrdinalIgnoreCase))
            {
                return $"{whose} is a tune for \"{file.Signature}\" and this is \"{_ecuSignature}\". "
                       + "They are different firmwares, so comparing them says little.";
            }

            if (TuneDifferences.Count == 0)
                return load.IsComplete
                    ? $"{whose} matches, setting for setting."
                    : $"{whose} matches every setting it carries. {load.Summary}";

            return $"{TuneDifferences.Count:N0} setting"
                   + (TuneDifferences.Count == 1 ? "" : "s")
                   + $" differ from {whose}: "
                   + string.Join("; ", TuneDifferences.Take(3).Select(d => d.Summary))
                   + (TuneDifferences.Count > 3 ? ", and more." : ".");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or LogFormatException)
        {
            return $"Could not read {Path.GetFileName(path)}: {e.Message}";
        }
    }
}
