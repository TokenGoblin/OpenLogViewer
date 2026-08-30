using System.Buffers.Binary;
using System.Globalization;
using System.Text;

namespace OpenLogViewer.Core;

/// <summary>
/// Reader for the binary "MLVLG" datalog written by TunerStudio / MegaSquirt.
///
/// The layout below was derived clean-room by analysing sample .mlg files. The
/// container is self-describing, so no third-party code was consulted.
///
///   File header (24 bytes, big-endian throughout)
///     0   char[6]  "MLVLG\0"
///     6   u16      format version (observed: 2)
///     8   u32      capture timestamp (Unix seconds)
///     12  u32      offset of the info/metadata block
///     16  u32      offset of the first record
///     20  u16      payload size of one data record, in bytes
///     22  u16      channel descriptor count
///
///   Channel descriptors follow immediately, 89 bytes each
///     0   u8       data type (see <see cref="MlgDataType"/>)
///     1   char[34] name, NUL-padded UTF-8
///     35  char[11] units, NUL-padded UTF-8
///     46  f32      scale
///     50  f32      transform (added after scaling)
///     54  u8       display decimal places
///     55  byte[34] reserved
///
///   Records share a 4-byte header: type, an 8-bit sequence counter that
///   increments across every record, and a u16 logger tick.
///
///     type 0 - sample     4 + payload + 1 trailing checksum byte
///     type 1 - marker     4 + char[50] annotation text, fixed 54 bytes
///
///   Marker records are interleaved with samples, so records must be walked
///   rather than indexed at a fixed stride.
///
/// Channel values are <c>raw * scale + transform</c>.
/// </summary>
public sealed class MlgReader : ILogReader
{
    private const int FileHeaderSize = 24;
    private const int FieldDescriptorSize = 89;
    private const int RecordHeaderSize = 4;
    private const int MarkerTextLength = 50;
    private const int MarkerRecordSize = RecordHeaderSize + MarkerTextLength;

    private const byte RecordTypeSample = 0;
    private const byte RecordTypeMarker = 1;

    private static ReadOnlySpan<byte> Magic => "MLVLG"u8;

    public string FormatName => "MLG";

    public bool CanRead(string path)
    {
        try
        {
            using var fs = File.OpenRead(path);
            Span<byte> head = stackalloc byte[5];
            return fs.ReadAtLeast(head, 5, throwOnEndOfStream: false) == 5
                   && head.SequenceEqual(Magic);
        }
        catch (IOException)
        {
            return false;
        }
    }

    public LogDocument Read(string path)
    {
        byte[] data = File.ReadAllBytes(path);
        if (data.Length < FileHeaderSize || !data.AsSpan(0, 5).SequenceEqual(Magic))
            throw new LogFormatException("Not an MLVLG file: bad magic.");

        int version = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(6));
        long stamp = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(8));
        int infoStart = BinaryPrimitives.ReadInt32BigEndian(data.AsSpan(12));
        int dataStart = BinaryPrimitives.ReadInt32BigEndian(data.AsSpan(16));
        int declaredPayload = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(20));
        int fieldCount = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(22));

        if (fieldCount <= 0 || dataStart <= 0 || dataStart > data.Length)
            throw new LogFormatException(
                $"MLG header is not usable (channels={fieldCount}, dataStart={dataStart}).");

        if (FileHeaderSize + fieldCount * FieldDescriptorSize > data.Length)
            throw new LogFormatException("MLG channel descriptors run past end of file.");

        MlgField[] fields = ReadDescriptors(data, fieldCount, out int payloadSize);

        // The descriptor table and the declared record length must agree. If they
        // diverge, channel offsets are wrong and every value past the mismatch
        // would be silently garbage, so fail loudly instead.
        if (declaredPayload != payloadSize)
            throw new LogFormatException(
                $"MLG channel table sums to {payloadSize} bytes but the header declares " +
                $"{declaredPayload}. The file uses an unsupported channel layout.");

        int stride = ResolveRecordStride(data, dataStart, payloadSize);
        Walk(data, dataStart, stride, out int sampleCount, out List<int> markerOffsets);

        // The time field is decoded a second time at full precision; every other
        // channel goes straight into its float column, so a log is never staged
        // as doubles.
        int timeField = FindTimeField(fields);

        var columns = new float[fieldCount][];
        for (int i = 0; i < fieldCount; i++) columns[i] = new float[sampleCount];
        double[]? preciseTime = timeField >= 0 ? new double[sampleCount] : null;

        DecodeSamples(data, dataStart, stride, fields, columns, preciseTime, timeField, sampleCount);

        var channels = new List<LogChannel>(fieldCount);
        for (int i = 0; i < fieldCount; i++)
            channels.Add(LogChannel.Adopt(fields[i].Name, fields[i].Units, fields[i].Digits, columns[i]));

        LogChannel time = ResolveTimeBase(fields, preciseTime, timeField, sampleCount);
        var (signature, info) = ReadInfoBlock(data, infoStart, dataStart);

        return new LogDocument
        {
            FilePath = path,
            Channels = channels,
            Time = time,
            Markers = ReadMarkers(data, dataStart, stride, markerOffsets, time),
            Signature = signature,
            CaptureInfo = info,
            EmbeddedTune = ReadEmbeddedTune(data, infoStart, dataStart),
            RecordedAt = stamp > 0 ? DateTimeOffset.FromUnixTimeSeconds(stamp) : null,
            FormatName = $"MLG v{version}",
        };
    }

    private static void DecodeSamples(
        byte[] data, int dataStart, int stride, MlgField[] fields,
        float[][] columns, double[]? preciseTime, int timeField, int sampleCount)
    {
        int row = 0;
        int o = dataStart;
        while (row < sampleCount && o < data.Length)
        {
            byte type = data[o];
            if (type == RecordTypeMarker) { o += MarkerRecordSize; continue; }
            if (type != RecordTypeSample || o + stride > data.Length) break;

            int payload = o + RecordHeaderSize;
            for (int f = 0; f < fields.Length; f++)
            {
                MlgField fd = fields[f];
                columns[f][row] = (float)((ReadRaw(data, payload + fd.Offset, fd.Type) + fd.Transform) * fd.Scale);
            }

            // Repeated rather than branched on inside the loop above: one extra
            // field read per row costs less than a test against every field.
            if (preciseTime is not null)
            {
                MlgField tf = fields[timeField];
                preciseTime[row] = (ReadRaw(data, payload + tf.Offset, tf.Type) + tf.Transform) * tf.Scale;
            }

            row++;
            o += stride;
        }
    }

    /// <summary>
    /// Walks the record stream, counting samples and noting marker positions.
    /// Stops cleanly at an unrecognised record type so truncated logs still load.
    /// </summary>
    private static int Walk(byte[] data, int dataStart, int stride, out int sampleCount, out List<int> markers)
    {
        sampleCount = 0;
        markers = [];

        int o = dataStart;
        while (o < data.Length)
        {
            byte type = data[o];
            if (type == RecordTypeSample)
            {
                if (o + stride > data.Length) break;
                sampleCount++;
                o += stride;
            }
            else if (type == RecordTypeMarker)
            {
                if (o + MarkerRecordSize > data.Length) break;
                markers.Add(o);
                o += MarkerRecordSize;
            }
            else
            {
                break;
            }
        }
        return data.Length - o;
    }

    /// <summary>
    /// The header's record-length field counts only the payload; observed files
    /// append a trailing checksum byte. Rather than hard-coding that, pick the
    /// stride whose record walk consumes the file most completely.
    /// </summary>
    private static int ResolveRecordStride(byte[] data, int dataStart, int payloadSize)
    {
        int bestStride = -1, bestSamples = 0, bestLeftover = int.MaxValue;

        for (int extra = 0; extra <= 4; extra++)
        {
            int stride = RecordHeaderSize + payloadSize + extra;
            int leftover = Walk(data, dataStart, stride, out int samples, out _);
            if (samples == 0) continue;

            if (leftover < bestLeftover || (leftover == bestLeftover && samples > bestSamples))
            {
                bestLeftover = leftover;
                bestSamples = samples;
                bestStride = stride;
            }
        }

        if (bestStride < 0)
            throw new LogFormatException("Could not determine the MLG record stride.");
        return bestStride;
    }

    private static MlgField[] ReadDescriptors(byte[] data, int count, out int payloadSize)
    {
        var fields = new MlgField[count];
        int offset = 0;
        int bitFieldSeq = 0;

        for (int i = 0; i < count; i++)
        {
            int o = FileHeaderSize + i * FieldDescriptorSize;
            var type = (MlgDataType)data[o];
            int size = SizeOf(type);
            if (size == 0)
                throw new LogFormatException($"Channel {i} declares unknown data type {data[o]}.");

            // Packed flag bytes carry no descriptor name, but still occupy a
            // payload byte and must be numbered so later offsets stay aligned.
            bool isBits = type == MlgDataType.Bits;

            fields[i] = new MlgField
            {
                Type = type,
                Name = ReadString(data, o + 1, 34, isBits ? $"Flags {++bitFieldSeq}" : $"Channel {i}"),
                Units = ReadString(data, o + 35, 11, isBits ? "bits" : ""),
                Scale = isBits ? 1 : Decimalise(BinaryPrimitives.ReadSingleBigEndian(data.AsSpan(o + 46))),
                Transform = isBits ? 0 : Decimalise(BinaryPrimitives.ReadSingleBigEndian(data.AsSpan(o + 50))),
                Digits = isBits ? 0 : data[o + 54],
                Offset = offset,
            };
            offset += size;
        }

        payloadSize = offset;
        return fields;
    }

    /// <summary>
    /// Markers carry a logger tick rather than a seconds value, so each is dated
    /// from the sample that immediately precedes it.
    /// </summary>
    private static List<LogMarker> ReadMarkers(
        byte[] data, int dataStart, int stride, List<int> offsets, LogChannel time)
    {
        var markers = new List<LogMarker>(offsets.Count);
        if (offsets.Count == 0) return markers;

        int row = 0, next = 0, o = dataStart;
        while (o < data.Length && next < offsets.Count)
        {
            if (o == offsets[next])
            {
                string text = Encoding.UTF8
                    .GetString(data, o + RecordHeaderSize,
                               Math.Min(MarkerTextLength, data.Length - o - RecordHeaderSize))
                    .TrimEnd('\0').Trim();

                if (text.Length > 0)
                    markers.Add(new LogMarker(time.At(Math.Max(0, row - 1)), text));

                next++;
                o += MarkerRecordSize;
            }
            else if (data[o] == RecordTypeSample && o + stride <= data.Length)
            {
                row++;
                o += stride;
            }
            else if (data[o] == RecordTypeMarker && o + MarkerRecordSize <= data.Length)
            {
                o += MarkerRecordSize;
            }
            else
            {
                break;
            }
        }
        return markers;
    }

    private static int FindTimeField(MlgField[] fields)
    {
        for (int i = 0; i < fields.Length; i++)
            if (fields[i].Name.Equals("Time", StringComparison.OrdinalIgnoreCase))
                return i;
        return -1;
    }

    /// <summary>
    /// Built from the separately decoded doubles rather than a stored channel: a
    /// time base keeps full precision, because it accumulates over the recording.
    /// </summary>
    private static LogChannel ResolveTimeBase(
        MlgField[] fields, double[]? seconds, int timeField, int sampleCount)
    {
        // A time column that never moves is no use as a time base.
        if (seconds is { Length: > 1 } && seconds[^1] > seconds[0])
            return new LogChannel(fields[timeField].Name, fields[timeField].Units,
                                  fields[timeField].Digits, seconds, preservePrecision: true);

        // Fall back to a synthetic index when the log has no usable Time column.
        var synthetic = new double[sampleCount];
        for (int i = 0; i < sampleCount; i++) synthetic[i] = i;
        return new LogChannel("Sample", "#", 0, synthetic, preservePrecision: true);
    }

    /// <summary>
    /// The info block holds quoted metadata strings followed by an embedded copy
    /// of the tune; only the leading quoted strings are of interest here.
    /// </summary>
    private static (string? Signature, string? Info) ReadInfoBlock(byte[] data, int start, int end)
    {
        if (start <= 0 || start >= end || end > data.Length) return (null, null);

        string text = Encoding.UTF8.GetString(data, start, Math.Min(end - start, 4096));

        var quoted = new List<string>();
        int i = 0;
        while (quoted.Count < 2)
        {
            int open = text.IndexOf('"', i);
            if (open < 0) break;
            int close = text.IndexOf('"', open + 1);
            if (close < 0) break;
            quoted.Add(text[(open + 1)..close].Trim());
            i = close + 1;
        }

        return (quoted.Count > 0 ? quoted[0] : null, quoted.Count > 1 ? quoted[1] : null);
    }

    /// <summary>
    /// Pulls the MSQ tune out of the info block. It is declared as ISO-8859-1
    /// and sits between the metadata strings and the first data record.
    /// </summary>
    private static string? ReadEmbeddedTune(byte[] data, int start, int end)
    {
        if (start <= 0 || start >= end || end > data.Length) return null;

        string text = Encoding.Latin1.GetString(data, start, end - start);

        int open = text.IndexOf("<?xml", StringComparison.Ordinal);
        if (open < 0) return null;

        int close = text.IndexOf("</msq>", open, StringComparison.Ordinal);
        return close < 0 ? null : text[open..(close + 6)];
    }

    /// <summary>
    /// Recovers the decimal a scale was authored as.
    ///
    /// Descriptors store the scale as a 32-bit float, so a channel scaled by 0.1
    /// holds 0.100000001490116. Widening that to double and multiplying carries
    /// the error into every sample: a raw 341 decodes to 34.10000228 rather than
    /// the 34.1 the logger meant, which is a rounding step away and reads as
    /// noise once it is written to a file. Round-tripping through the float's
    /// shortest decimal gives back 0.1, and the sample lands on the nearest
    /// float to 34.1.
    /// </summary>
    private static double Decimalise(float value) =>
        double.Parse(value.ToString("R", CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);

    private static double ReadRaw(byte[] b, int o, MlgDataType type) => type switch
    {
        MlgDataType.U08 => b[o],
        MlgDataType.S08 => (sbyte)b[o],
        MlgDataType.U16 => BinaryPrimitives.ReadUInt16BigEndian(b.AsSpan(o)),
        MlgDataType.S16 => BinaryPrimitives.ReadInt16BigEndian(b.AsSpan(o)),
        MlgDataType.U32 => BinaryPrimitives.ReadUInt32BigEndian(b.AsSpan(o)),
        MlgDataType.S32 => BinaryPrimitives.ReadInt32BigEndian(b.AsSpan(o)),
        MlgDataType.S64 => BinaryPrimitives.ReadInt64BigEndian(b.AsSpan(o)),
        MlgDataType.F32 => BinaryPrimitives.ReadSingleBigEndian(b.AsSpan(o)),
        MlgDataType.Bits => b[o],
        _ => double.NaN,
    };

    private static int SizeOf(MlgDataType t) => t switch
    {
        MlgDataType.U08 or MlgDataType.S08 or MlgDataType.Bits => 1,
        MlgDataType.U16 or MlgDataType.S16 => 2,
        MlgDataType.U32 or MlgDataType.S32 or MlgDataType.F32 => 4,
        MlgDataType.S64 => 8,
        _ => 0,
    };

    private static string ReadString(byte[] b, int offset, int maxLength, string fallback)
    {
        int n = 0;
        while (n < maxLength && b[offset + n] != 0) n++;
        string s = Encoding.UTF8.GetString(b, offset, n).Trim();
        return s.Length == 0 ? fallback : s;
    }

    private struct MlgField
    {
        public MlgDataType Type;
        public string Name;
        public string Units;
        public double Scale;
        public double Transform;
        public int Digits;
        public int Offset;
    }
}

public enum MlgDataType : byte
{
    U08 = 0,
    S08 = 1,
    U16 = 2,
    S16 = 3,
    U32 = 4,
    S32 = 5,
    S64 = 6,
    F32 = 7,

    /// <summary>
    /// A packed byte of boolean flags. Descriptors carry no name, and the entry
    /// occupies a single payload byte.
    /// </summary>
    Bits = 16,
}


