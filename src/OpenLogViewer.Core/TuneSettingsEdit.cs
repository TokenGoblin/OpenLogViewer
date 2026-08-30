namespace OpenLogViewer.Core;

/// <summary>One setting, changed.</summary>
/// <param name="Name">The constant's name.</param>
/// <param name="Was">What the ECU holds.</param>
/// <param name="Now">What it would be changed to.</param>
public sealed record SettingChange(string Name, double Was, double Now);

/// <summary>
/// Settings edited but not yet sent.
///
/// <para>
/// The counterpart of <see cref="TuneEdit"/> for everything that is not a table:
/// the eight hundred–odd scalars, bit fields and names that make up most of a
/// tune. Like that one, nothing here reaches the ECU — it decides what the bytes
/// would become, and sending them is somebody else's job.
/// </para>
/// <para>
/// <b>It works on a copy of the pages rather than on a list of changes, and that
/// is the whole design.</b> Bit fields share bytes: on an MS3, four unrelated
/// options can live in one. Encoding each change against the ECU's original
/// bytes and sending them in turn means the second write carries the first
/// field's old value and silently undoes it — two settings changed, one applied,
/// no error anywhere. Editing a copy and then asking which bytes differ cannot
/// produce that, because every edit lands on the same image the next one reads.
/// </para>
/// </summary>
public sealed class TuneSettingsEdit
{
    private readonly EcuTune _tune;
    private readonly byte[][] _working;
    private readonly Dictionary<string, double> _original = new(StringComparer.OrdinalIgnoreCase);

    // Ordered, because the change list is read back in the order things were
    // touched and a plain dictionary does not promise that.
    private readonly List<string> _order = [];
    private readonly Dictionary<string, double> _changed = new(StringComparer.OrdinalIgnoreCase);

    public TuneSettingsEdit(EcuTune tune)
    {
        ArgumentNullException.ThrowIfNull(tune);

        _tune = tune;
        _working = [.. tune.Pages.Select(p => p.ToArray())];
    }

    /// <summary>Settings changed and not yet sent, in the order they were touched.</summary>
    public IReadOnlyList<SettingChange> Changes =>
        [.. _order.Where(_changed.ContainsKey)
            .Select(k => new SettingChange(k, _original[k], _changed[k]))];

    /// <summary>
    /// What the ECU holds for a setting, as against what it would become.
    ///
    /// Read from the controller's own pages rather than the working copy, which
    /// is the whole point of it: a caller wanting to show was-and-now needs the
    /// two to come from different places.
    /// </summary>
    public double Original(string name, int element = 0) =>
        _tune.ValueIn(_tune.Pages, name, element) ?? double.NaN;

    /// <summary>The same, for a text setting.</summary>
    public string OriginalText(string name) => _tune.TextIn(_tune.Pages, name) ?? "";

    public int ChangedCount => _changed.Count;

    public bool HasChanges => _changed.Count > 0;

    /// <summary>What a setting would become, or what it is where it is untouched.</summary>
    public double Value(string name, int element = 0) =>
        _tune.ValueIn(_working, name, element) ?? double.NaN;

    /// <summary>Text settings, which hold a name rather than a number.</summary>
    public string Text(string name) => _tune.TextIn(_working, name) ?? "";

    /// <summary>
    /// Changes a setting, or reports why it cannot be.
    ///
    /// Values are held to the range the firmware declares, which is far tighter
    /// than the storage allows — the same discipline the table editor applies,
    /// and for the same reason.
    /// </summary>
    public bool Set(string name, double value, int element = 0)
    {
        if (_tune.Constant(name) is not { } constant || !constant.OnController) return false;
        if (constant.IsText) return false;
        if (!double.IsFinite(value)) return false;

        if (constant.HasRange && (value < constant.Low || value > constant.High)) return false;

        double? before = _tune.ValueIn(_working, name, element);
        if (before is null) return false;

        if (!_tune.PokeInto(_working, constant, element, value)) return false;

        Remember(Key(name, element), before.Value, value);
        return true;
    }

    /// <summary>Changes a text setting.</summary>
    public bool SetText(string name, string value)
    {
        if (_tune.Constant(name) is not { } constant || !constant.OnController || !constant.IsText)
            return false;

        if (!_tune.PokeTextInto(_working, constant, value ?? "")) return false;

        // Judged by the bytes, not by comparing the string against the one that
        // was here a moment ago. Two ways that goes wrong: typing a field back
        // to what the ECU holds leaves it counted as a pending change that
        // writes nothing — the Send button lit, then "nothing has been changed"
        // and the phantom stuck, because the reconcile that would clear it never
        // runs. And re-setting a field to the value it already has, which a
        // text box does on every focus change, drops a record whose bytes really
        // do still differ, so a genuine pending write greys the button out.
        // Comparing images is also what makes a field the controller pads with
        // spaces come out right, where reading it back gives a trimmed string
        // that no longer says what is stored.
        //
        // Text has no number to record, so the change is noted by name alone and
        // the count is what the header reports.
        if (!Differs(constant))
        {
            _changed.Remove(name);
            _original.Remove(name);
            _order.Remove(name);
        }
        else
        {
            _original.TryAdd(name, double.NaN);
            if (!_changed.ContainsKey(name)) _order.Add(name);
            _changed[name] = double.NaN;
        }

        return true;
    }

    /// <summary>Puts one setting back to what the ECU holds.</summary>
    public void Revert(string name, int element = 0)
    {
        string key = Key(name, element);
        if (!_changed.ContainsKey(key)) return;

        if (_tune.Constant(name) is { } constant && constant.OnController)
        {
            // From the ECU's own bytes, not from the remembered number: a bit
            // field's neighbours may have been edited since, and they must stay
            // as they now are.
            //
            // A text field goes back byte for byte rather than through its
            // string, because reading one gives it trimmed and writing one pads
            // with nulls. A name the controller stores padded with spaces would
            // otherwise come back differing from what is on it — the change
            // record cleared while the bytes still say otherwise, which is a
            // write nothing admits to.
            if (constant.IsText)
            {
                _tune.RestoreTextInto(_working, constant);
            }
            else if (_tune.ValueIn(_tune.Pages, name, element) is { } was)
            {
                _tune.PokeInto(_working, constant, element, was);
            }
        }

        _changed.Remove(key);
        _original.Remove(key);
        _order.Remove(key);
    }

    /// <summary>
    /// Takes a write the controller has already accepted from somewhere else.
    ///
    /// <para>
    /// A table is edited through <see cref="TuneEdit"/>, not through this, but
    /// both work on the same pages. When a table write lands, the controller and
    /// <see cref="EcuTune"/> both move and the copy held here does not — so the
    /// bytes it still holds differ from the ECU's, and <see cref="Writes"/>
    /// reports them as settings waiting to be sent. They carry the values from
    /// before the table write, so sending them would put the table back.
    /// </para>
    /// <para>
    /// Called after the write is acknowledged, never before: this records what
    /// happened rather than predicting it, exactly as
    /// <see cref="EcuTune.Accept"/> does.
    /// </para>
    /// </summary>
    public void Accept(TuneWrite write)
    {
        ArgumentNullException.ThrowIfNull(write);

        if (write.Page < 0 || write.Page >= _working.Length) return;

        byte[] page = _working[write.Page];
        if (write.Offset < 0 || write.Offset + write.Data.Length > page.Length) return;

        write.Data.CopyTo(page.AsSpan(write.Offset));
    }

    /// <summary>Puts everything back.</summary>
    public void RevertAll()
    {
        for (int i = 0; i < _working.Length; i++) _tune.Pages[i].CopyTo(_working[i], 0);

        _changed.Clear();
        _original.Clear();
        _order.Clear();
    }

    /// <summary>
    /// The bytes that differ from the ECU's, gathered into runs.
    ///
    /// Computed by comparing the images rather than accumulated as edits are
    /// made, which is what makes two settings sharing a byte come out as one
    /// write carrying both. Neighbouring changed bytes are joined into a single
    /// write, since a write costs a round trip and a run of four bytes is no
    /// dearer than one.
    /// </summary>
    public IReadOnlyList<TuneWrite> Writes()
    {
        var writes = new List<TuneWrite>();

        for (int page = 0; page < _working.Length; page++)
        {
            byte[] now = _working[page];
            byte[] was = _tune.Pages[page];

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

    /// <summary>
    /// How much of the ECU this would rewrite, for saying so before it is sent.
    /// </summary>
    public int BytesToWrite => Writes().Sum(w => w.Data.Length);

    /// <summary>
    /// Pages this would write to, which are the pages a burn would have to
    /// commit.
    /// </summary>
    public IReadOnlyList<int> PagesToWrite => [.. Writes().Select(w => w.Page).Distinct().Order()];

    /// <summary>
    /// Drops the changes the ECU has taken, keeping the ones it has not.
    ///
    /// Called after sending. A send is not one write but several, and one of
    /// them failing leaves the earlier ones applied — so what is still
    /// outstanding cannot be "all of it" or "none of it". Asking which settings
    /// still differ from the controller answers it exactly, and needs no record
    /// of which writes got through.
    /// </summary>
    public void Reconcile()
    {
        foreach (string key in _order.ToArray())
        {
            if (!_changed.ContainsKey(key)) continue;

            (string name, int element) = Split(key);

            // A text setting has no number to compare, so it is judged by its
            // bytes instead — for the reason SetText gives.
            bool settled = _tune.Constant(name) is { IsText: true } text
                ? !Differs(text)
                : Same(_tune.ValueIn(_working, name, element), _tune.ValueIn(_tune.Pages, name, element));

            if (!settled) continue;

            _changed.Remove(key);
            _original.Remove(key);
            _order.Remove(key);
        }
    }

    /// <summary>
    /// Whether a text field's bytes stand apart from the controller's.
    ///
    /// The same question <see cref="Writes"/> asks of the whole image, narrowed
    /// to one field — and the only honest way to ask it of text, since the
    /// string a field reads back as is not what it holds.
    /// </summary>
    private bool Differs(TuneConstant constant)
    {
        if (constant.Page < 0 || constant.Page >= _working.Length) return false;

        byte[] now = _working[constant.Page];
        byte[] was = _tune.Pages[constant.Page];

        int at = constant.Offset;
        int length = Math.Max(0, constant.Columns);

        if (at < 0 || at + length > now.Length || at + length > was.Length) return false;

        return !now.AsSpan(at, length).SequenceEqual(was.AsSpan(at, length));
    }

    private static bool Same(double? a, double? b) =>
        a is null || b is null ? a is null && b is null : Math.Abs(a.Value - b.Value) < 1e-12;

    private static (string Name, int Element) Split(string key)
    {
        int open = key.IndexOf('[', StringComparison.Ordinal);

        return open > 0 && key.EndsWith(']') && int.TryParse(key[(open + 1)..^1], out int element)
            ? (key[..open], element)
            : (key, 0);
    }

    private void Remember(string key, double before, double after)
    {
        _original.TryAdd(key, before);

        // Back to where it started is not a change. Otherwise a value nudged up
        // and down again would still be counted, and the header would offer to
        // send bytes identical to the ones already there.
        if (Math.Abs(_original[key] - after) < 1e-12)
        {
            _changed.Remove(key);
            _original.Remove(key);
            _order.Remove(key);
        }
        else
        {
            if (!_changed.ContainsKey(key)) _order.Add(key);
            _changed[key] = after;
        }
    }

    private static string Key(string name, int element) => element == 0 ? name : $"{name}[{element}]";
}
