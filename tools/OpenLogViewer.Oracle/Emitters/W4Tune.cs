using System.Text;
using System.Text.RegularExpressions;
using OpenLogViewer.Core;

namespace OpenLogViewer.Oracle;

/// <summary>
/// The tune operations over every real tune in the corpus, each against the INI that matches
/// its signature (read with the tune's own conditional symbols, as the application does):
///
/// <list type="bullet">
/// <item>the page bytes <see cref="MsqApply"/> builds from the file (hex, so a mismatch can be
/// located), with the load's counts and complaints;</item>
/// <item>the <c>.msq</c> <see cref="MsqWriter"/> writes back, with the bibliography's author
/// and date replaced by fixed text (they are the machine's clock), as a hash and a length, and
/// whether reading it back gives the same pages;</item>
/// <item><see cref="TuneCompare"/> of the tune against itself and against the next tune that
/// shares its INI, line by line;</item>
/// <item><see cref="TuneRestore"/>'s plan of putting that next tune onto this one, and of a
/// tune onto itself;</item>
/// <item>scripted edits and the bytes they encode to: a table scaled, nudged and interpolated,
/// a curve dragged, settings changed.</item>
/// </list>
///
/// Doubles go out as IEEE bits so the Rust side compares the value, not a printed one.
/// </summary>
public sealed class W4Tune : IEmitter
{
    public string Package => "w4-tune";

    public string Set => "tune";

    public bool FromCorpus => true;

    /// <summary>The comment given to every write, chosen to need escaping.</summary>
    private const string Comment = "round & trip <test> \"q\"";

    public IReadOnlyList<object> Emit(EmitContext context)
    {
        var catalogue = new List<IniFile>();
        var iniTexts = new Dictionary<string, string>();

        foreach (string path in context.CorpusFiles("definitions"))
        {
            if (!path.EndsWith(".ini", StringComparison.OrdinalIgnoreCase)) continue;

            catalogue.Add(new IniFile(path, IniCatalog.ReadSignature(path) ?? ""));
            iniTexts[path] = TuningText.Read(path);
        }

        var loaded = new List<Loaded>();

        foreach (string path in context.CorpusFiles("tunes"))
        {
            string text = TuningText.Read(path);
            MsqFile msq = MsqFile.Read(text);

            // The INI whose signature matches, and failing that the way the application
            // chooses (SavedTuneCommands.BestDefinitionFor): of the definitions that do not
            // look like another firmware, the one this file fills most. A tune from a
            // neighbouring revision (comms340vU against the 342h2 definition) still gets one.
            IniFile? match = IniCatalog.Match(msq.Signature, catalogue);
            TuneLayout? layout = null;
            MsqLoad? load = null;

            if (match is not null)
            {
                layout = TuneLayoutReader.Read(iniTexts[match.Path], msq.Symbols);
                load = MsqApply.Load(layout, msq);
            }
            else
            {
                foreach (IniFile candidate in catalogue)
                {
                    TuneLayout candidateLayout = TuneLayoutReader.Read(iniTexts[candidate.Path], msq.Symbols);
                    if (candidateLayout.Pages.Count == 0) continue;

                    MsqLoad candidateLoad = MsqApply.Load(candidateLayout, msq);
                    if (candidateLoad.LooksLikeAnotherFirmware) continue;

                    if (load is null || candidateLoad.Applied > load.Applied)
                    {
                        (match, layout, load) = (candidate, candidateLayout, candidateLoad);
                    }
                }
            }

            loaded.Add(new Loaded(path, msq, match, layout, load));
        }

        var cases = new List<object>();

        for (int i = 0; i < loaded.Count; i++)
        {
            Loaded one = loaded[i];
            string file = Path.GetFileName(one.Path);

            if (one.Ini is null)
            {
                cases.Add(new { file, ini = (string?)null });
                continue;
            }

            // The next tune on the same INI, wrapping round, or this one when it is alone.
            Loaded other = Enumerable.Range(1, loaded.Count)
                .Select(k => loaded[(i + k) % loaded.Count])
                .First(l => l.Ini?.Path == one.Ini.Path);

            try
            {
                cases.Add(Describe(one, other, iniTexts[one.Ini.Path]));
            }
            catch (Exception ex)
            {
                cases.Add(new { file, ini = Path.GetFileName(one.Ini.Path), error = ex.GetType().Name, message = ex.Message });
            }
        }

        return cases;
    }

    private static object Describe(Loaded one, Loaded other, string iniText)
    {
        TuneLayout layout = one.Layout!;
        MsqLoad load = one.Load!;
        EcuTune tune = load.Tune;

        // ----- the page bytes --------------------------------------------------------------

        object loadRecord = new
        {
            applied = load.Applied,
            missing = load.Missing.Select(c => c.ToString()).ToArray(),
            rejected = load.Rejected.Select(c => c.ToString()).ToArray(),
            unknown = load.Unknown.ToArray(),
            is_complete = load.IsComplete,
            looks_like_another_firmware = load.LooksLikeAnotherFirmware,
            summary = load.Summary,
            pages = tune.Pages.Select(Hex).ToArray(),
        };

        // ----- the .msq written back -------------------------------------------------------

        string withSource = Normalise(
            MsqWriter.Write(tune, one.Msq.Signature, one.Msq.Symbols, Comment, one.Msq));
        string bare = Normalise(MsqWriter.Write(tune, one.Msq.Signature));

        MsqLoad again = MsqApply.Load(layout, MsqFile.Read(withSource));
        int differing = 0;
        for (int p = 0; p < tune.Pages.Count; p++)
            for (int b = 0; b < tune.Pages[p].Length; b++)
                if (again.Tune.Pages[p][b] != tune.Pages[p][b]) differing++;

        object msqRecord = new
        {
            with_source = Text(withSource),
            bare = Text(bare),
            round_trip_differing_bytes = differing,
            round_trip_applied = again.Applied,
            round_trip_complete = again.IsComplete,
            saved_hash = Fnv(withSource.All(c => c <= 0xFF)
                ? Encoding.Latin1.GetBytes(withSource)
                : new UTF8Encoding(false).GetBytes(
                    withSource.Replace("encoding=\"ISO-8859-1\"", "encoding=\"UTF-8\"", StringComparison.Ordinal))),
        };

        // ----- compare ---------------------------------------------------------------------

        EcuTune theirs = other.Load!.Tune;
        object compareRecord = new
        {
            other = Path.GetFileName(other.Path),
            with_self = TuneCompare.Compare(tune, tune).Select(Difference).ToArray(),
            with_other = TuneCompare.Compare(tune, theirs).Select(Difference).ToArray(),
            other_with_this = TuneCompare.Compare(theirs, tune).Select(Difference).ToArray(),
        };

        // ----- restore ---------------------------------------------------------------------

        object restoreRecord = new
        {
            other = Path.GetFileName(other.Path),
            plan = Plan(TuneRestore.Plan(tune, other.Msq, one.Msq.Signature)),
            onto_itself = Plan(TuneRestore.Plan(tune, one.Msq, one.Msq.Signature)),
            mismatched = Plan(TuneRestore.Plan(tune, other.Msq, "some other firmware")),
        };

        // ----- scripted edits --------------------------------------------------------------

        var edits = new List<object>();

        IReadOnlyList<TableDefinition> definitions = TableEditorReader.Read(iniText, one.Msq.Symbols);
        int tables = 0;
        foreach (TableDefinition definition in definitions)
        {
            if (tune.Table(definition.Title, definition.Values, definition.XBins, definition.YBins) is not { } table)
                continue;
            if (tables++ >= 4) break;

            TuneConstant? constant = tune.Constant(definition.Values);
            int c = table.Columns, r = table.Rows;
            var all = new TuneSelection(0, 0, c - 1, r - 1);

            var scaled = new TuneEdit(table, constant);
            scaled.Scale(all, 5);
            edits.Add(TableRecord("table_scale_5_percent", definition.Values, scaled, tune, all));

            var nudged = new TuneEdit(table, constant);
            var cell = TuneSelection.Cell(Math.Min(1, c - 1), Math.Min(1, r - 1));
            nudged.Add(cell, nudged.Step * 3);
            edits.Add(TableRecord("table_nudge_cell", definition.Values, nudged, tune, cell));

            var smoothed = new TuneEdit(table, constant);
            smoothed.Scale(new TuneSelection(0, 0, c - 1, 0), 10);
            smoothed.Scale(new TuneSelection(0, r - 1, c - 1, r - 1), -10);
            bool interpolated = smoothed.Interpolate(all);
            edits.Add(TableRecord("table_interpolate", definition.Values, smoothed, tune, all, interpolated));

            var overshoot = new TuneEdit(table, constant);
            overshoot.Set(all, 1e9);
            edits.Add(TableRecord("table_set_huge", definition.Values, overshoot, tune, all));
        }

        int curves = 0;
        foreach (TuneCurve curve in TuneCurveReader.Read(iniText, one.Msq.Symbols).Values)
        {
            if (TuneCurveEdit.For(curve, tune) is not { } edit) continue;
            if (curves++ >= 4) break;

            int last = edit.Count - 1;
            edit.SetY(last / 2, edit.Y(last / 2) * 1.1);
            edit.AddY(0, 1);
            edit.ScaleY(last, 90);
            bool curveInterpolated = edit.Interpolate(0, last);
            edit.SetX(last / 2, edit.X(last / 2) + 1);

            IReadOnlyList<TuneWrite>? writes = edit.Encode(tune);
            edits.Add(new
            {
                kind = "curve",
                name = curve.Name,
                count = edit.Count,
                interpolated = curveInterpolated,
                changed = edit.ChangedCount,
                x = edit.Points.Select(p => Bits(p.X)).ToArray(),
                y = edit.Points.Select(p => Bits(p.Y)).ToArray(),
                writes = writes?.Select(Write).ToArray(),
            });
        }

        edits.Add(Settings(tune));

        return new
        {
            file = Path.GetFileName(one.Path),
            ini = Path.GetFileName(one.Ini!.Path),
            signature = one.Msq.Signature,
            page_sizes = layout.Pages.Select(p => p.Size).ToArray(),
            constants = layout.Constants.Count,
            load = loadRecord,
            msq = msqRecord,
            compare = compareRecord,
            restore = restoreRecord,
            edits = edits.ToArray(),
        };
    }

    private static object TableRecord(
        string kind, string name, TuneEdit edit, EcuTune tune, TuneSelection selection, bool? interpolated = null)
    {
        TuneChange change = edit.Preview(selection);
        TuneWrite? write = edit.Encode(tune);

        return new
        {
            kind,
            name,
            interpolated,
            changed = edit.ChangedCount,
            has_changes = edit.HasChanges,
            step = Bits(edit.Step),
            preview = new
            {
                cells = change.Cells,
                changed = change.Changed,
                from_low = Bits(change.FromLow),
                from_high = Bits(change.FromHigh),
                to_low = Bits(change.ToLow),
                to_high = Bits(change.ToHigh),
                delta_low = Bits(change.DeltaLow),
                delta_high = Bits(change.DeltaHigh),
                percent = Bits(change.Percent),
                uniform = change.Uniform,
            },
            values = Enumerable.Range(0, edit.Rows)
                .SelectMany(row => Enumerable.Range(0, edit.Columns).Select(col => Bits(edit[col, row])))
                .ToArray(),
            write = write is null ? null : Write(write),
        };
    }

    /// <summary>Changes a handful of settings of each kind and records what would be sent.</summary>
    private static object Settings(EcuTune tune)
    {
        var edit = new TuneSettingsEdit(tune);
        var results = new List<object>();

        int scalars = 0, bits = 0, texts = 0;

        foreach (TuneConstant constant in tune.Layout.Constants)
        {
            if (!constant.OnController) continue;
            if (tune.Constant(constant.Name) is null) continue;

            if (constant.IsText)
            {
                if (texts++ >= 2) continue;
                bool ok = edit.SetText(constant.Name, "OLV");
                results.Add(new { name = constant.Name, kind = "text", ok });
            }
            else if (constant.IsBitField)
            {
                if (bits++ >= 6) continue;
                int target = constant.HasOptions ? Math.Min(1, constant.Options.Count - 1) : 1;
                bool ok = edit.Set(constant.Name, target);
                results.Add(new { name = constant.Name, kind = "bits", target, ok });
            }
            else if (!constant.IsArray)
            {
                if (scalars++ >= 8) continue;
                double target = constant.HasRange ? (constant.Low + constant.High) / 2 : edit.Original(constant.Name) + 1;
                bool ok = edit.Set(constant.Name, target);
                results.Add(new { name = constant.Name, kind = "scalar", target = Bits(target), ok });
            }
        }

        // One that is refused, so the refusal is on record.
        bool refused = edit.Set("noSuchSettingAnywhere", 1);

        return new
        {
            kind = "settings",
            results = results.ToArray(),
            refused,
            changed = edit.ChangedCount,
            changes = edit.Changes.Select(c => new
            {
                name = c.Name,
                was = Bits(c.Was),
                now = Bits(c.Now),
            }).ToArray(),
            writes = edit.Writes().Select(Write).ToArray(),
            bytes = edit.BytesToWrite,
            pages = edit.PagesToWrite.ToArray(),
        };
    }

    private static object Difference(TuneDifference d) => new
    {
        name = d.Name,
        cells = d.Cells,
        mine = d.Mine is { } m ? Bits(m) : null,
        theirs = d.Theirs is { } t ? Bits(t) : null,
        mine_text = d.MineText,
        theirs_text = d.TheirsText,
        mine_shown = d.MineShown,
        theirs_shown = d.TheirsShown,
        is_array = d.IsArray,
        summary = d.Summary,
    };

    private static object Plan(TuneRestorePlan plan) => new
    {
        is_empty = plan.IsEmpty,
        bytes = plan.Bytes,
        pages = plan.Pages.ToArray(),
        summary = plan.Summary,
        shortfall = plan.Shortfall,
        complete = plan.Complete,
        signatures_agree = plan.SignaturesAgree,
        file_signature = plan.FileSignature,
        ecu_signature = plan.EcuSignature,
        writes = plan.Writes.Select(Write).ToArray(),
        differences = plan.Differences.Select(Difference).ToArray(),
        missing = plan.Missing.Select(c => c.ToString()).ToArray(),
        rejected = plan.Rejected.Select(c => c.ToString()).ToArray(),
        unknown = plan.Unknown.ToArray(),
        target = plan.Target.Pages.Select(Fnv).ToArray(),
    };

    private static object Write(TuneWrite w) => new
    {
        page = w.Page,
        offset = w.Offset,
        length = w.Data.Length,
        hash = Fnv(w.Data),
        hex = w.Data.Length <= 4096 ? Hex(w.Data) : null,
    };

    /// <summary>The text itself when small, else just its length and hash.</summary>
    private static object Text(string text)
    {
        byte[] utf8 = Encoding.UTF8.GetBytes(text);
        return new
        {
            length = utf8.Length,
            hash = Fnv(utf8),
            lines = text.Count(c => c == '\n'),
            text = text.Length <= 4096 ? text : null,
        };
    }

    /// <summary>
    /// The bibliography's author and date are the machine's own, so they are replaced by fixed
    /// text before anything is recorded; the Rust side writes the same fixed text.
    /// </summary>
    private static string Normalise(string xml) =>
        Regex.Replace(
            xml,
            "<bibliography author=\"[^\"]*\" tuneComment=\"([^\"]*)\" writeDate=\"[^\"]*\"/>",
            "<bibliography author=\"AUTHOR\" tuneComment=\"$1\" writeDate=\"DATE\"/>");

    /// <summary>IEEE bits, or "nan" for any NaN: the sign and payload of a NaN are not a value.</summary>
    private static string Bits(double v) => double.IsNaN(v) ? "nan" : Exact.Bits(v);

    private static string Hex(byte[] bytes) => Convert.ToHexString(bytes).ToLowerInvariant();

    /// <summary>FNV-1a over bytes; the Rust side's is the same.</summary>
    private static string Fnv(byte[] bytes)
    {
        ulong h = 0xcbf29ce484222325;
        foreach (byte b in bytes)
        {
            h ^= b;
            h *= 0x100000001b3;
        }

        return h.ToString("x16", System.Globalization.CultureInfo.InvariantCulture);
    }

    private sealed record Loaded(string Path, MsqFile Msq, IniFile? Ini, TuneLayout? Layout, MsqLoad? Load);
}
