using System.Globalization;

namespace OpenLogViewer.Oracle;

/// <summary>One set of reference outputs for one package.</summary>
public interface IEmitter
{
    /// <summary>The package id in rust/packages.toml these outputs check.</summary>
    string Package { get; }

    /// <summary>The file name, without .json.</summary>
    string Set { get; }

    /// <summary>True when the inputs are the personal corpus, so the output is gitignored.</summary>
    bool FromCorpus => false;

    IReadOnlyList<object> Emit(EmitContext context);
}

public sealed class EmitContext(string rust)
{
    public string Rust { get; } = rust;

    public string Corpus => Path.Combine(Rust, "corpus");

    public IEnumerable<string> CorpusFiles(string kind) =>
        Directory.Exists(Path.Combine(Corpus, kind))
            ? Directory.EnumerateFiles(Path.Combine(Corpus, kind)).Order(StringComparer.Ordinal)
            : [];
}

/// <summary>Helpers every emitter wants, matching olv_oracle on the Rust side.</summary>
public static class Exact
{
    /// <summary>A double as its IEEE bits, so the Rust side compares the exact value, not a printed one.</summary>
    public static string Bits(double v) => BitConverter.DoubleToInt64Bits(v).ToString("x16", CultureInfo.InvariantCulture);

    /// <summary>FNV-1a over the little-endian bits of every sample; olv_oracle::fnv1a_f32 is the same.</summary>
    public static string Hash(ReadOnlySpan<float> samples)
    {
        ulong h = 0xcbf29ce484222325;
        foreach (float s in samples)
        {
            uint bits = BitConverter.SingleToUInt32Bits(s);
            for (int i = 0; i < 4; i++)
            {
                h ^= (bits >> (8 * i)) & 0xFF;
                h *= 0x100000001b3;
            }
        }
        return h.ToString("x16", CultureInfo.InvariantCulture);
    }
}
