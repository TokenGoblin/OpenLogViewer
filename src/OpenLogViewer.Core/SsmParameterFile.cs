using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace OpenLogViewer.Core;

/// <summary>
/// The list of SSM addresses to read, which you supply.
///
/// The protocol is here; the addresses are not, and that is deliberate rather
/// than unfinished. What lives at which address is not something this can
/// discover: the two that are known were confirmed by reading them against the
/// same values over OBD2, and that method only works for parameters OBD2 already
/// has — which are precisely the ones not worth reaching SSM for. Knock
/// correction and the learnt fuelling trims have nothing to check them against.
///
/// The published maps that do have them belong to other projects under licences
/// this cannot take from: RomRaider is GPL-2.0 against this project's MIT, and
/// the widely-copied definition repository declares no licence at all, which
/// grants nothing to anybody. So the file is yours to fill in, from whatever
/// source you judge appropriate for your own vehicle — the same arrangement as
/// the ECU definition files this application already expects you to provide.
///
/// It also means this works on any Subaru rather than the one it was written
/// against.
/// </summary>
public static class SsmParameterFile
{
    /// <summary>What the file is called inside the definitions folder.</summary>
    public const string Name = "ssm-parameters.json";

    /// <summary>
    /// Reads a parameter list, keeping the entries that make sense.
    ///
    /// A bad entry is dropped rather than failing the file. Somebody filling this
    /// in by hand against a forum post will get one wrong, and losing every
    /// parameter because the fourth has a typo would be a poor trade — the ones
    /// that survive are reported, and the count is what tells you something went
    /// missing.
    /// </summary>
    public static IReadOnlyList<SsmParameter> Read(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];

        StoredFile? file;

        try
        {
            file = JsonSerializer.Deserialize<StoredFile>(json, Options);
        }
        catch (JsonException)
        {
            return [];
        }

        if (file?.Parameters is not { Count: > 0 } stored) return [];

        var parameters = new List<SsmParameter>(stored.Count);
        var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (StoredParameter entry in stored)
        {
            if (entry.Name is not { Length: > 0 } name) continue;
            if (ParseAddress(entry.Address) is not { } address) continue;

            var parameter = new SsmParameter(
                name.Trim(),
                address,
                entry.Bytes ?? 1,
                entry.Units?.Trim() ?? "",
                entry.Scale ?? 1,
                entry.Offset ?? 0,
                entry.Digits ?? 0,
                entry.Low ?? 0,
                entry.High ?? 255,
                entry.Enabled ?? true);

            // A duplicate name would give two channels the same column, and every
            // preset and filter matching on it would find whichever came first.
            if (!parameter.IsUsable || !taken.Add(parameter.Name)) continue;

            parameters.Add(parameter);
        }

        return parameters;
    }

    /// <summary>
    /// Only the ones switched on, which is what a session actually reads.
    ///
    /// The distinction earns its keep because one address per request makes the
    /// list a budget. A file can hold every parameter a car offers -- a hundred
    /// and sixty of them on this protocol -- while a dozen are switched on, and
    /// changing what you are watching is then a matter of moving a flag rather
    /// than finding an address and its scaling again.
    /// </summary>
    public static IReadOnlyList<SsmParameter> Enabled(string? json) =>
        [.. Read(json).Where(p => p.Enabled)];

    /// <summary>
    /// An address written the way people write them.
    ///
    /// Hex, because every published SSM address is hex and nobody writes 14 when
    /// they mean 0x0E. Accepted with or without the prefix; a bare decimal would
    /// be ambiguous with the same digits in hex, so it is not offered.
    /// </summary>
    internal static int? ParseAddress(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;

        string trimmed = text.Trim();

        if (trimmed.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) trimmed = trimmed[2..];

        return int.TryParse(trimmed, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int value)
               && value is >= 0 and <= 0xFFFFFF
            ? value
            : null;
    }

    /// <summary>
    /// A starting file, with the two addresses that were actually confirmed and
    /// nothing that was not.
    ///
    /// Written out the first time it is wanted so there is something to edit
    /// rather than a format to guess at. The two entries are the ones proved on a
    /// running car by reading them against OBD2; everything beyond that is left
    /// commented in the notes, because a plausible-looking address that turns out
    /// to be something else is worse than an empty file.
    /// </summary>
    public static string Template =>
        """
        {
          "version": 1,

          "_notes": [
            "Addresses this application reads over SSM, which is Subaru's own",
            "protocol and reaches what the ECU has learnt rather than what it is",
            "measuring — knock correction, fine knock learning, IAM, fuelling",
            "trims. None of that is in OBD2 at any speed.",
            "",
            "The two below were confirmed on a running 2014 Crosstrek by reading",
            "them against the same values over OBD2. They are here as worked",
            "examples of the format, not because they are the interesting ones.",
            "",
            "The interesting addresses are not shipped. What lives where is not",
            "something this can work out — the confirmation method only works for",
            "values OBD2 already has, and the published maps belong to projects",
            "under licences this cannot take from. Fill them in yourself, from a",
            "source you judge right for your own car.",
            "",
            "address : hex, with or without 0x",
            "bytes   : how many consecutive bytes, most significant first",
            "scale   : multiplied by the raw value",
            "offset  : added afterwards, so a temperature is raw minus 40",
            "",
            "Reading is one address per request, about 146 ms each, so keep the",
            "list to what you actually want to watch. Eight is roughly 0.85 Hz."
          ],

          "parameters": [
            {
              "name": "Engine Speed",
              "address": "0x00000E",
              "bytes": 2,
              "units": "rpm",
              "scale": 0.25,
              "digits": 0,
              "low": 0,
              "high": 8000
            },
            {
              "name": "Coolant",
              "address": "0x000008",
              "bytes": 1,
              "units": "°C",
              "scale": 1,
              "offset": -40,
              "digits": 0,
              "low": -40,
              "high": 215
            }
          ]
        }

        """;

    /// <summary>
    /// The parameter list from the definitions folder, writing the template out
    /// the first time so there is something to edit rather than a format to guess
    /// at.
    ///
    /// A missing or unreadable file is no parameters rather than a failure. This
    /// runs on the way to connecting, and a read-only folder should cost the SSM
    /// session and nothing else.
    /// </summary>
    public static IReadOnlyList<SsmParameter> ReadFrom(string definitionsFolder)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(definitionsFolder);

        string path = System.IO.Path.Combine(definitionsFolder, Name);

        try
        {
            if (!File.Exists(path))
            {
                Directory.CreateDirectory(definitionsFolder);
                File.WriteAllText(path, Template);
            }

            return Enabled(File.ReadAllText(path));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    /// <summary>Where the file lives, for pointing somebody at it.</summary>
    public static string PathIn(string definitionsFolder) =>
        System.IO.Path.Combine(definitionsFolder, Name);

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private sealed class StoredFile
    {
        public int Version { get; set; }

        public List<StoredParameter>? Parameters { get; set; }
    }

    private sealed class StoredParameter
    {
        public string? Name { get; set; }

        public string? Address { get; set; }

        public int? Bytes { get; set; }

        public string? Units { get; set; }

        public double? Scale { get; set; }

        public double? Offset { get; set; }

        public int? Digits { get; set; }

        public double? Low { get; set; }

        public double? High { get; set; }

        public bool? Enabled { get; set; }
    }
}
