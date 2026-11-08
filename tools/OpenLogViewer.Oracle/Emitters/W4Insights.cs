using OpenLogViewer.Core;

namespace OpenLogViewer.Oracle;

/// <summary>
/// What LogInsights says about every log in the corpus: per finding its kind (the topic),
/// severity, title, the full detail text, the evidence text with the arithmetic in it, and
/// the sample count it rests on. Compared string for string, so a threshold that moves, a
/// number formatted differently or a word changed all fail.
/// </summary>
public sealed class W4Insights : IEmitter
{
    public string Package => "w4-insights";

    public string Set => "insights";

    public bool FromCorpus => true;

    public IReadOnlyList<object> Emit(EmitContext context)
    {
        var cases = new List<object>();
        foreach (string path in context.CorpusFiles("logs"))
        {
            string file = Path.GetFileName(path);
            LogDocument log;
            try
            {
                log = LogReaderFactory.Load(path);
            }
            catch (Exception)
            {
                // Unreadable logs are w2-logs' case; there is nothing to analyse here.
                continue;
            }

            IReadOnlyList<LogInsight> found;
            try
            {
                found = LogInsights.From(log);
            }
            catch (Exception ex)
            {
                cases.Add(new { file, error = ex.GetType().Name, message = ex.Message });
                continue;
            }

            cases.Add(new
            {
                file,
                findings = found.Select(f => new
                {
                    kind = f.Topic,
                    severity = f.Level.ToString(),
                    title = f.Title,
                    detail = f.Detail,
                    evidence = f.Evidence,
                    samples = f.Samples,
                    text = f.ToString(),
                }).ToArray(),
            });
        }
        return cases;
    }
}
