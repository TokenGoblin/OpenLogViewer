using System.Text;
using OpenLogViewer.Core;

namespace OpenLogViewer.Oracle;

/// <summary>
/// Every INI in the corpus as the C# parsers read it: signature, pages, constants,
/// PC variables, output channels, datalog, option lists, tables, curves, dialogs and
/// gauges, in the order the parsers return them.
///
/// Then each INI against each tune: the lookup TuningContext builds, the gauges that
/// resolve against it and the verdict of every dialog condition, as a count and a
/// hash of a canonical text, since the lists themselves run to thousands of lines.
/// </summary>
public sealed class W1Ini : IEmitter
{
    public string Package => "w1-ini";

    public string Set => "ini";

    public bool FromCorpus => true;

    public IReadOnlyList<object> Emit(EmitContext context)
    {
        var tunes = new List<(string File, string Xml)>();
        foreach (string path in context.CorpusFiles("tunes"))
            tunes.Add((Path.GetFileName(path), TuningText.Read(path)));

        var cases = new List<object>();
        foreach (string path in context.CorpusFiles("definitions"))
        {
            if (!path.EndsWith(".ini", StringComparison.OrdinalIgnoreCase)) continue;

            string text = TuningText.Read(path);
            TuneLayout layout = TuneLayoutReader.Read(text);
            RealtimeLayout realtime = MsqIni.ReadOutputChannels(text);
            TuneInterface ui = TuneInterfaceReader.Read(text);
            IReadOnlyDictionary<string, double> known = TuningContext.Build(text, null);

            cases.Add(new
            {
                file = Path.GetFileName(path),
                signature = IniCatalog.ReadSignature(path),
                signature_in_text = IniCatalog.SignatureIn(text),
                pages = layout.Pages.Select(p => new
                {
                    index = p.Index,
                    size = p.Size,
                    identifier = p.Identifier,
                    read_command = p.ReadCommand,
                    chunk_write_command = p.ChunkWriteCommand,
                    burn_command = p.BurnCommand,
                }).ToArray(),
                constants = layout.Constants.Select(Constant).ToArray(),
                pc_variables = layout.PcVariables.Select(Constant).ToArray(),
                derived = layout.Derived.Select(d => new { name = d.Key, expression = d.Value }).ToArray(),
                little_endian = layout.LittleEndian,
                blocking_factor = layout.BlockingFactor,
                inter_write_delay = layout.InterWriteDelay,
                after_burn_delay = layout.AfterBurnDelay,
                realtime = new
                {
                    block_size = realtime.BlockSize,
                    get_command = realtime.GetCommand,
                    little_endian = realtime.LittleEndian,
                    blocking_factor = realtime.BlockingFactor,
                    uses_simple_command = realtime.UsesSimpleCommand,
                    fields = realtime.Fields.Select(f => new
                    {
                        name = f.Name,
                        units = f.Units,
                        type = f.Type.ToString(),
                        offset = f.Offset,
                        scale = Exact.Bits(f.Scale),
                        transform = Exact.Bits(f.Transform),
                        digits = f.Digits,
                        bit_low = f.BitLow,
                        bit_high = f.BitHigh,
                    }).ToArray(),
                    expressions = realtime.Expressions.Select(e => new
                    {
                        name = e.Name,
                        units = e.Units,
                        expression = e.Expression,
                    }).ToArray(),
                    skipped = realtime.Skipped.ToArray(),
                },
                aliases = MsqIni.ReadAliases(text).Select(a => new { name = a.Key, target = a.Value }).ToArray(),
                datalog = MsqIni.ReadDatalog(text).Select(d => new { channel = d.Channel, label = d.Label, digits = d.Digits }).ToArray(),
                defines = IniDefines.Read(text).Select(d => new { name = d.Key, values = d.Value.ToArray() }).ToArray(),
                tables = TableEditorReader.Read(text).Select(t => new
                {
                    id = t.Id,
                    title = t.Title,
                    map = t.Map,
                    values = t.Values,
                    x_bins = t.XBins,
                    y_bins = t.YBins,
                    x_channel = t.XChannel,
                    y_channel = t.YChannel,
                }).ToArray(),
                curves = TuneCurveReader.Read(text).Select(c => new
                {
                    key = c.Key,
                    name = c.Value.Name,
                    title = c.Value.Title,
                    x_bins = c.Value.XBins,
                    y_bins = c.Value.YBins,
                    x_label = c.Value.XLabel,
                    y_label = c.Value.YLabel,
                    x_low = c.Value.XLow,
                    x_high = c.Value.XHigh,
                    y_low = c.Value.YLow,
                    y_high = c.Value.YHigh,
                    help = c.Value.Help,
                }).ToArray(),
                menus = ui.Menus.Select(m => new
                {
                    title = m.Title,
                    entries = m.Entries.Select(e => new { dialog = e.Dialog, title = e.Title, condition = e.Condition }).ToArray(),
                }).ToArray(),
                dialogs = ui.Dialogs.Select(d => new
                {
                    key = d.Key,
                    name = d.Value.Name,
                    title = d.Value.Title,
                    layout = d.Value.Layout,
                    help = d.Value.Help,
                    items = d.Value.Items.Select(i => new
                    {
                        kind = i.Kind.ToString(),
                        label = i.Label,
                        target = i.Target,
                        condition = i.Condition,
                        position = i.Position,
                    }).ToArray(),
                }).ToArray(),
                gauges = GaugeCatalog.Read(text, known).Select(Gauge).ToArray(),
                front_page = GaugeCatalog.ReadFrontPage(text).ToArray(),
                known_count = known.Count,
                known_hash = TextHash(KnownText(known)),
                with_tunes = tunes.Select(t => WithTune(text, ui, t)).ToArray(),
            });
        }

        return cases;
    }

    private static object WithTune(string ini, TuneInterface ui, (string File, string Xml) tune)
    {
        IReadOnlyDictionary<string, double> known = TuningContext.Build(ini, tune.Xml);
        double Lookup(string name) => known.TryGetValue(name, out double v) ? v : double.NaN;
        IReadOnlyList<GaugeSpec> gauges = GaugeCatalog.Read(ini, known);

        var verdicts = new StringBuilder();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        void Judge(string condition)
        {
            if (condition.Length == 0 || !seen.Add(condition)) return;
            verdicts.Append(condition).Append('|')
                .Append(DialogCondition.Evaluate(condition, Lookup)).Append('|')
                .Append(Canon(DialogCondition.Number(condition, Lookup))).Append('\n');
        }

        foreach (TuneMenu menu in ui.Menus)
            foreach (MenuEntry entry in menu.Entries)
                Judge(entry.Condition);
        foreach (TuneDialog dialog in ui.Dialogs.Values)
            foreach (DialogItem item in dialog.Items)
                Judge(item.Condition);

        var gaugeText = new StringBuilder();
        foreach (GaugeSpec g in gauges)
        {
            gaugeText.Append(string.Join('|',
                g.Name, g.Channel, g.Title, g.Units, g.Category,
                Canon(g.Low), Canon(g.High), Canon(g.LowDanger), Canon(g.LowWarning),
                Canon(g.HighWarning), Canon(g.HighDanger), g.ValueDigits, g.LabelDigits)).Append('\n');
        }

        return new
        {
            tune = tune.File,
            pc_variables = TuningContext.ReadPcVariables(tune.Xml).Count,
            known_count = known.Count,
            known_hash = TextHash(KnownText(known)),
            conditions = seen.Count,
            conditions_hash = TextHash(verdicts.ToString()),
            gauges = gauges.Count,
            gauges_hash = TextHash(gaugeText.ToString()),
        };
    }

    private static object Constant(TuneConstant c) => new
    {
        name = c.Name,
        page = c.Page,
        offset = c.Offset,
        type = c.Type.ToString(),
        units = c.Units,
        scale = Exact.Bits(c.Scale),
        transform = Exact.Bits(c.Transform),
        scale_expression = c.ScaleExpression,
        transform_expression = c.TransformExpression,
        digits = c.Digits,
        low = Exact.Bits(c.Low),
        high = Exact.Bits(c.High),
        columns = c.Columns,
        rows = c.Rows,
        bit_low = c.BitLow,
        bit_high = c.BitHigh,
        is_text = c.IsText,
        size = c.Size,
        options = c.Options.ToArray(),
    };

    private static object Gauge(GaugeSpec g) => new
    {
        name = g.Name,
        channel = g.Channel,
        title = g.Title,
        units = g.Units,
        category = g.Category,
        low = Exact.Bits(g.Low),
        high = Exact.Bits(g.High),
        low_danger = Exact.Bits(g.LowDanger),
        low_warning = Exact.Bits(g.LowWarning),
        high_warning = Exact.Bits(g.HighWarning),
        high_danger = Exact.Bits(g.HighDanger),
        value_digits = g.ValueDigits,
        label_digits = g.LabelDigits,
    };

    /// <summary>A double as text, with every NaN the same: the languages differ in which one they make.</summary>
    private static string Canon(double v) => double.IsNaN(v) ? "nan" : Exact.Bits(v);

    /// <summary>The lookup as sorted <c>name=bits</c> lines, so enumeration order cannot matter.</summary>
    private static string KnownText(IReadOnlyDictionary<string, double> known)
    {
        var text = new StringBuilder();
        foreach ((string name, double value) in known.OrderBy(k => k.Key, StringComparer.Ordinal))
            text.Append(name).Append('=').Append(Canon(value)).Append('\n');
        return text.ToString();
    }

    /// <summary>FNV-1a over the UTF-8 bytes; the Rust test computes the same.</summary>
    private static string TextHash(string text)
    {
        ulong h = 0xcbf29ce484222325;
        foreach (byte b in Encoding.UTF8.GetBytes(text))
        {
            h ^= b;
            h *= 0x100000001b3;
        }
        return h.ToString("x16", System.Globalization.CultureInfo.InvariantCulture);
    }
}
