namespace OpenLogViewer.Core;

/// <summary>
/// Whether a decoded column can serve as a log's time base.
///
/// Shared by the readers rather than written twice, because they had drifted
/// into disagreeing about the same question. Both tested a time column against
/// its own first and last entries, and both were wrong about a hole in it: the
/// MLG reader asked whether the last value exceeded the first, which is false
/// when the first is NaN, and threw away a sound column; the delimited reader
/// asked whether any value fell below the one before it, which is *also* false
/// across a NaN, and accepted a column it should have refused.
///
/// A hole at either end is ordinary. TunerStudio writes NaN into the first
/// sample of a recording often enough that three of seven logs to hand carry
/// one, and a recording cut off by the ignition leaves the last sample short.
/// Neither says anything about whether time runs forwards in between.
/// </summary>
internal static class TimeBase
{
    /// <summary>
    /// Whether these values run forwards: at least two are real numbers, and the
    /// last real one is later than the first.
    /// </summary>
    public static bool Rises(IReadOnlyList<double> values)
    {
        if (values is null) return false;

        int first = FirstReal(values);
        if (first < 0) return false;

        int last = LastReal(values);

        return last > first && values[last] > values[first];
    }

    /// <summary>
    /// Whether these values never go backwards, holes ignored. Each real value
    /// is held against the last real one before it rather than its immediate
    /// neighbour, so a NaN neither breaks the run nor hides a step back across
    /// it.
    /// </summary>
    public static bool NeverFalls(IReadOnlyList<double> values)
    {
        if (values is null) return false;

        double previous = double.NaN;

        for (int i = 0; i < values.Count; i++)
        {
            double v = values[i];
            if (!double.IsFinite(v)) continue;

            if (double.IsFinite(previous) && v < previous) return false;
            previous = v;
        }

        return true;
    }

    /// <summary>
    /// The same readings with the holes closed, so that the base itself is a
    /// clean run of seconds.
    ///
    /// Accepting a column with a hole in it is only half the job. A time base is
    /// not data, it is the axis everything else is measured against, and
    /// downstream there is no sensible answer to "what time is sample 0" — the
    /// view range comes back NaN, the plot draws nothing and the readout says
    /// "NaN s". The recorded Time channel keeps its hole, because that is what
    /// the file says; the axis built from it does not.
    ///
    /// Interior holes are interpolated between the readings either side. Holes at
    /// the ends are carried out at the rate of the nearest pair, which keeps the
    /// base strictly rising — repeating the neighbouring value instead would put
    /// two samples at the same instant and hand a zero interval to everything
    /// that divides by one.
    /// </summary>
    public static double[] Fill(double[] values)
    {
        ArgumentNullException.ThrowIfNull(values);

        int first = FirstReal(values);

        // Nothing to place them against, or nothing missing.
        if (first < 0 || Array.TrueForAll(values, double.IsFinite)) return values;

        int last = LastReal(values);
        var filled = (double[])values.Clone();

        // A single reading says nothing about the rate, so the best that can be
        // done is to hold it: there is no second point to draw a line through.
        double step = Step(values, first, last);

        for (int i = first - 1; i >= 0; i--) filled[i] = filled[i + 1] - step;
        for (int i = last + 1; i < filled.Length; i++) filled[i] = filled[i - 1] + step;

        // Interior holes: straight line between the readings that bracket them.
        int at = first;
        while (at < last)
        {
            int next = at + 1;
            while (next <= last && !double.IsFinite(values[next])) next++;

            if (next > at + 1)
            {
                double across = (filled[next] - filled[at]) / (next - at);
                for (int i = at + 1; i < next; i++) filled[i] = filled[at] + across * (i - at);
            }

            at = next;
        }

        return filled;
    }

    /// <summary>The typical spacing of the readings, for carrying the ends out.</summary>
    private static double Step(double[] values, int first, int last)
    {
        if (last <= first) return 0;

        double span = values[last] - values[first];
        int across = last - first;

        double step = span / across;

        // Guards a column whose readings are all the same instant, which Rises
        // would have refused anyway — but Fill is public and need not assume it.
        return double.IsFinite(step) && step > 0 ? step : 0;
    }

    /// <summary>The first index holding a real number, or -1 where none does.</summary>
    public static int FirstReal(IReadOnlyList<double> values)
    {
        for (int i = 0; i < values.Count; i++)
            if (double.IsFinite(values[i])) return i;

        return -1;
    }

    /// <summary>The last index holding a real number, or -1 where none does.</summary>
    public static int LastReal(IReadOnlyList<double> values)
    {
        for (int i = values.Count - 1; i >= 0; i--)
            if (double.IsFinite(values[i])) return i;

        return -1;
    }
}
