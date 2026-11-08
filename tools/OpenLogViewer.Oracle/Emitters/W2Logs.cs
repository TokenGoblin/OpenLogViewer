using OpenLogViewer.Core;

namespace OpenLogViewer.Oracle;

/// <summary>
/// Every log in the corpus as LogReaderFactory reads it: the document's facts, and
/// per channel its statistics and a hash of every sample, so a reader that decodes
/// one sample differently fails without the samples themselves being shipped.
/// </summary>
public sealed class W2Logs : IEmitter
{
    public string Package => "w2-logs";

    public string Set => "logs";

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
            catch (Exception ex)
            {
                // A file the C# refuses is a case too: the Rust must refuse it as well.
                cases.Add(new { file, error = ex.GetType().Name, message = ex.Message });
                continue;
            }

            cases.Add(new
            {
                file,
                format_name = log.FormatName,
                sample_count = log.SampleCount,
                duration = log.Duration,
                median_sample_interval = log.MedianSampleInterval,
                signature = log.Signature,
                capture_info = log.CaptureInfo,
                recorded_at = log.RecordedAt?.ToString("O"),
                has_embedded_tune = log.EmbeddedTune is not null,
                unreadable_tune = log.UnreadableTune,
                time_channel = log.Time.Name,
                markers = log.Markers.Select(m => new { time = m.Time, text = m.Text }).ToArray(),
                channels = log.Channels.Select(c => new
                {
                    name = c.Name,
                    units = c.Units,
                    digits = c.Digits,
                    length = c.Length,
                    min = c.Min,
                    max = c.Max,
                    min_index = c.MinIndex,
                    max_index = c.MaxIndex,
                    is_flat = c.IsFlat,
                    hash = Exact.Hash(c.Samples),
                    first = c.Samples[..Math.Min(3, c.Length)].ToArray(),
                }).ToArray(),
            });
        }
        return cases;
    }
}
