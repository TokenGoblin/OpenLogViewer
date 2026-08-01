using System.Buffers.Binary;
using System.Text;
using OpenLogViewer.Core;

namespace OpenLogViewer.Tests;

/// <summary>
/// Builds a well-formed MLG file in memory so the reader can be exercised
/// without depending on sample logs outside the repository.
/// </summary>
internal sealed class MlgBuilder
{
    private const int FieldDescriptorSize = 89;

    private readonly List<Field> _fields = [];

    private record struct Field(
        MlgDataType Type, string Name, string Units, float Scale, float Transform, byte Digits);

    public MlgBuilder Add(
        MlgDataType type, string name, string units = "",
        float scale = 1f, float transform = 0f, byte digits = 0)
    {
        _fields.Add(new Field(type, name, units, scale, transform, digits));
        return this;
    }

    public int PayloadSize => _fields.Sum(f => SizeOf(f.Type));

    /// <summary>
    /// Serialises the log. <paramref name="raw"/> supplies the pre-scaling value
    /// for (field index, sample index). Markers are emitted after the sample
    /// index they are keyed to.
    /// </summary>
    public byte[] Build(
        int sampleCount,
        Func<int, int, double> raw,
        (int AfterSample, string Text)[]? markers = null,
        int? declaredPayloadOverride = null,
        string? embeddedTune = null)
    {
        markers ??= [];

        int payload = PayloadSize;
        int infoStart = 24 + _fields.Count * FieldDescriptorSize;
        byte[] info = Encoding.Latin1.GetBytes(
            "\"TEST ECU signature\"\0\"Capture Date: test\"\0" + (embeddedTune ?? ""));
        int dataStart = infoStart + info.Length;

        var buffer = new MemoryStream();
        var head = new byte[24];
        Encoding.ASCII.GetBytes("MLVLG").CopyTo(head, 0);
        head[5] = 0;
        BinaryPrimitives.WriteUInt16BigEndian(head.AsSpan(6), 2);
        BinaryPrimitives.WriteUInt32BigEndian(head.AsSpan(8), 1_700_000_000);
        BinaryPrimitives.WriteInt32BigEndian(head.AsSpan(12), infoStart);
        BinaryPrimitives.WriteInt32BigEndian(head.AsSpan(16), dataStart);
        BinaryPrimitives.WriteUInt16BigEndian(head.AsSpan(20), (ushort)(declaredPayloadOverride ?? payload));
        BinaryPrimitives.WriteUInt16BigEndian(head.AsSpan(22), (ushort)_fields.Count);
        buffer.Write(head);

        foreach (Field f in _fields)
        {
            var d = new byte[FieldDescriptorSize];
            d[0] = (byte)f.Type;
            WriteFixed(d, 1, 34, f.Name);
            WriteFixed(d, 35, 11, f.Units);
            BinaryPrimitives.WriteSingleBigEndian(d.AsSpan(46), f.Scale);
            BinaryPrimitives.WriteSingleBigEndian(d.AsSpan(50), f.Transform);
            d[54] = f.Digits;
            buffer.Write(d);
        }

        buffer.Write(info);

        byte counter = 0;
        for (int s = 0; s < sampleCount; s++)
        {
            var record = new byte[4 + payload + 1];
            record[0] = 0;
            record[1] = counter++;
            BinaryPrimitives.WriteUInt16BigEndian(record.AsSpan(2), (ushort)(s * 100));

            int offset = 4;
            for (int f = 0; f < _fields.Count; f++)
            {
                WriteValue(record.AsSpan(offset), _fields[f].Type, raw(f, s));
                offset += SizeOf(_fields[f].Type);
            }
            buffer.Write(record);

            foreach ((int after, string text) in markers.Where(m => m.AfterSample == s))
            {
                var marker = new byte[54];
                marker[0] = 1;
                marker[1] = counter++;
                BinaryPrimitives.WriteUInt16BigEndian(marker.AsSpan(2), (ushort)(s * 100));
                WriteFixed(marker, 4, 50, text);
                buffer.Write(marker);
            }
        }

        return buffer.ToArray();
    }

    public string BuildFile(
        int sampleCount,
        Func<int, int, double> raw,
        (int AfterSample, string Text)[]? markers = null,
        int? declaredPayloadOverride = null,
        string? embeddedTune = null)
    {
        string path = Path.Combine(Path.GetTempPath(), $"olv-{Guid.NewGuid():N}.mlg");
        File.WriteAllBytes(path, Build(sampleCount, raw, markers, declaredPayloadOverride, embeddedTune));
        return path;
    }

    private static void WriteFixed(byte[] target, int offset, int length, string value)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(value);
        int n = Math.Min(bytes.Length, length);
        Array.Copy(bytes, 0, target, offset, n);
    }

    private static void WriteValue(Span<byte> span, MlgDataType type, double value)
    {
        switch (type)
        {
            case MlgDataType.U08:
            case MlgDataType.Bits: span[0] = (byte)value; break;
            case MlgDataType.S08: span[0] = (byte)(sbyte)value; break;
            case MlgDataType.U16: BinaryPrimitives.WriteUInt16BigEndian(span, (ushort)value); break;
            case MlgDataType.S16: BinaryPrimitives.WriteInt16BigEndian(span, (short)value); break;
            case MlgDataType.U32: BinaryPrimitives.WriteUInt32BigEndian(span, (uint)value); break;
            case MlgDataType.S32: BinaryPrimitives.WriteInt32BigEndian(span, (int)value); break;
            case MlgDataType.S64: BinaryPrimitives.WriteInt64BigEndian(span, (long)value); break;
            case MlgDataType.F32: BinaryPrimitives.WriteSingleBigEndian(span, (float)value); break;
            default: throw new ArgumentOutOfRangeException(nameof(type));
        }
    }

    private static int SizeOf(MlgDataType t) => t switch
    {
        MlgDataType.U08 or MlgDataType.S08 or MlgDataType.Bits => 1,
        MlgDataType.U16 or MlgDataType.S16 => 2,
        MlgDataType.U32 or MlgDataType.S32 or MlgDataType.F32 => 4,
        MlgDataType.S64 => 8,
        _ => throw new ArgumentOutOfRangeException(nameof(t)),
    };
}
