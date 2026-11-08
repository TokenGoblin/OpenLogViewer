using OpenLogViewer.Core;

namespace OpenLogViewer.Oracle;

/// <summary>
/// Every tune in the corpus as <see cref="MsqFile"/> reads it (signature, header
/// fields, every constant and PC variable in order), the definition
/// <see cref="DefinitionNeeds"/> and <see cref="IniCatalog"/> pick for it from the
/// corpus INIs, and what <see cref="DangerousConstants"/> flags in it against each
/// INI. Plus, per INI, every constant of the layout it flags.
/// </summary>
public sealed class W2Definitions : IEmitter
{
    public string Package => "w2-definitions";

    public string Set => "definitions";

    public bool FromCorpus => true;

    public IReadOnlyList<object> Emit(EmitContext context)
    {
        var inis = new List<(string File, string Text, TuneLayout Layout, DefinitionInspection Inspection)>();
        var catalogue = new List<IniFile>();

        foreach (string path in context.CorpusFiles("definitions"))
        {
            if (!path.EndsWith(".ini", StringComparison.OrdinalIgnoreCase)) continue;

            string text = TuningText.Read(path);
            inis.Add((Path.GetFileName(path), text, TuneLayoutReader.Read(text), DefinitionFile.Inspect(text)));
            catalogue.Add(new IniFile(path, IniCatalog.ReadSignature(path) ?? ""));
        }

        var cases = new List<object>();

        foreach (var ini in inis)
        {
            cases.Add(new
            {
                kind = "ini",
                file = ini.File,
                signature = ini.Inspection.Signature,
                pages = ini.Inspection.Pages,
                channels = ini.Inspection.Channels,
                problem = ini.Inspection.Problem,
                name_for = DefinitionFile.NameFor(ini.Inspection.Signature),
                flagged = Flagged(ini.Layout, ini.Layout.Constants.Select(c => c.Name)),
            });
        }

        foreach (string path in context.CorpusFiles("tunes"))
        {
            string text = TuningText.Read(path);
            MsqFile msq = MsqFile.Read(text);
            IniFile? match = IniCatalog.Match(msq.Signature, catalogue);
            DefinitionNeed need = DefinitionNeeds.Describe([msq.Signature], catalogue, "defs");

            cases.Add(new
            {
                kind = "tune",
                file = Path.GetFileName(path),
                signature = msq.Signature,
                firmware = msq.Firmware,
                author = msq.Author,
                comment = msq.Comment,
                written_at = msq.WrittenAt,
                page_count = msq.PageCount,
                symbols = msq.Symbols.Order(StringComparer.Ordinal).ToArray(),
                is_empty = msq.IsEmpty,
                values = msq.Values.Select(v => new[] { v.Key, v.Value }).ToArray(),
                pc_variables = msq.PcVariables.Select(v => new[] { v.Key, v.Value }).ToArray(),
                ini_match = match is null ? null : Path.GetFileName(match.Path),
                need = new
                {
                    family = need.Family,
                    signature = need.Signature,
                    version = need.Version,
                    filename = need.Filename,
                    where = need.Where,
                    sources = need.Sources.ToArray(),
                    nearby = need.Nearby.Select(n => new { name = n.Name, signature = n.Signature }).ToArray(),
                },
                against = inis.Select(ini => new
                {
                    ini = ini.File,
                    flagged = Flagged(ini.Layout, msq.Values.Keys),
                }).ToArray(),
            });
        }

        return cases;
    }

    private static object[] Flagged(TuneLayout layout, IEnumerable<string> names) =>
    [
        .. names
            .Select(n => (Name: n, Role: DangerousConstants.Find(layout, n)))
            .Where(f => f.Role is not null)
            .Select(f => (object)new { name = f.Name, role = f.Role!.Value.ToString() }),
    ];
}
