using System.Diagnostics;
using System.Text;
using OpenLogViewer.Core;

// Asks a vehicle what it will answer beyond the OBD2 standard, and writes down
// exactly what came back.
//
// This is a probe and not a feature. Nothing here is guessed at and then acted
// on: every candidate is sent, every reply is recorded verbatim, and what any of
// it means is decided afterwards by reading the transcript. That is the whole
// point — the questions below are plausible rather than known, and a tool that
// quietly interpreted them would turn a plausible guess into a confident wrong
// answer.
//
// READ ONLY, and structurally so. The SSM command set includes address writes
// (0xB8) and this sends none; every request below either reads or asks what is
// supported. See Guard() — anything that is not on the allowed list is refused
// rather than trusted to be harmless.

if (args.Contains("--help") || args.Contains("-h"))
{
    Console.WriteLine("""
        usage: olv-probe [COM port] [--baud N] [--out transcript.txt] [--sweep]

        With no port, lists what is available and exits.

        Asks, in order:
          - what the adapter is, and which OBD2 protocol the car settled on
          - a standard mode 01 request, to prove the link works at all
          - mode 22, ReadDataByIdentifier, which many makers answer
          - mode 21, readDataByLocalIdentifier, where Toyota keeps its extra data
          - SSM over CAN, in several framings, to see whether any is understood

        --sweep asks all 256 mode 21 identifiers rather than the first six, and
        prints only the ones that answer. Takes a minute or so on a K-line car.
        Worth it on a Toyota: the identifier list is the maker's and unpublished,
        so sweeping is the only way to find out what a given car will report.

        Read-only. Nothing here writes to the vehicle.
        """);

    return 0;
}

IReadOnlyList<string> ports = SerialEcuTransport.AvailablePorts();

string? port = PortIn(args);

/// <summary>
/// The port, which is the one argument that is neither a switch nor a switch's
/// value.
///
/// Worth doing properly rather than taking the first token without a leading
/// dash. "--out transcript.txt COM10" would otherwise open a serial port called
/// transcript.txt, and the failure reads as the adapter not answering — which is
/// the one thing this tool must never say when it is not true.
/// </summary>
static string? PortIn(string[] args)
{
    string[] takesAValue = ["--baud", "--out"];

    for (int i = 0; i < args.Length; i++)
    {
        if (args[i].StartsWith("--", StringComparison.Ordinal))
        {
            if (takesAValue.Contains(args[i], StringComparer.OrdinalIgnoreCase)) i++;
            continue;
        }

        return args[i];
    }

    return null;
}

if (port is null)
{
    Console.WriteLine(ports.Count > 0
        ? $"Serial ports: {string.Join(", ", ports)}\n\nGive one, e.g.  olv-probe {ports[0]}"
        : "No serial ports. A Bluetooth Classic adapter must be paired first; a BLE one\n"
          + "never becomes a COM port and cannot be reached by this tool.");

    return ports.Count > 0 ? 1 : 2;
}

int baud = Value(args, "--baud") is { } b && int.TryParse(b, out int parsed) ? parsed : 0;
string transcriptPath = Value(args, "--out") ?? $"probe-{DateTime.Now:yyyy-MM-dd_HH-mm-ss}.txt";

var transcript = new StringBuilder();

void Log(string line)
{
    Console.WriteLine(line);
    transcript.AppendLine(line);
}

Log($"# OpenLogViewer probe — {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
Log($"# port {port}{(baud > 0 ? $" at {baud}" : " (speed found automatically)")}");
Log("");

IEcuTransport? transport = null;
int exit = 0;

try
{
    // A Bluetooth adapter ignores the speed entirely, the radio having already
    // negotiated one; a wired one has to be found. Trying the list is what
    // Elm327Source does on connect, so this matches it.
    foreach (int speed in baud > 0 ? [baud] : Elm327Source.BaudRates)
    {
        transport = new SerialEcuTransport(port, speed) { OpenAttempts = 3 };

        try
        {
            transport.Open();

            var trial = new Elm327(transport);
            if (trial.Reset().Length > 0)
            {
                Log($"opened at {speed}");
                break;
            }
        }
        catch (Exception e)
        {
            Log($"  {speed}: {e.Message}");
        }

        transport.Dispose();
        transport = null;
    }

    if (transport is null)
    {
        Log("Nothing on this port answered as an OBD2 adapter.");
        return 3;
    }

    var elm = new Elm327(transport) { Timeout = TimeSpan.FromSeconds(3) };

    // ----- what we are talking to -------------------------------------------

    Section(Log, "THE ADAPTER AND THE LINK");

    Log($"reset      : {elm.Reset()}");
    Log($"identity   : {elm.Identify()}");
    Log($"protocol   : {elm.ProtocolName()}");
    Log($"is CAN     : {elm.IsCan()}");

    // ----- prove the ordinary path works ------------------------------------

    // Everything after this is speculative, so it is worth establishing first
    // that the car is awake and answering. A probe that reports "SSM did not
    // reply" on a vehicle with the key out has reported nothing at all.

    Section(Log, "ORDINARY OBD2 — DOES ANYTHING ANSWER");

    Ask(elm, Log, "0100", "supported PIDs 01-20");
    Ask(elm, Log, "010C", "engine speed");

    // ----- mode 22, ReadDataByIdentifier ------------------------------------

    // The likeliest of the three to work, and the least trouble: it is an
    // ordinary OBD service number, so the adapter passes it through untouched
    // and no headers have to be meddled with. A car that answers any of these
    // has a door open that needs no special hardware.

    Section(Log, "MODE 22 — ReadDataByIdentifier");
    Log("# A positive answer echoes 0x62. 0x7F means the service or the");
    Log("# identifier was refused, which is itself worth knowing: 7F 22 11 is");
    Log("# 'no such service', 7F 22 31 is 'service fine, wrong identifier'.");
    Log("");

    foreach ((string did, string what) in new[]
             {
                 ("22F190", "VIN, a standard identifier"),
                 ("22F18C", "ECU serial number"),
                 ("22F194", "ECU software number"),
                 ("22F1A0", "maker-defined, often a calibration id"),
             })
    {
        Ask(elm, Log, did, what);
    }

    // ----- mode 21, Toyota's enhanced data ----------------------------------

    // Where Toyota keeps everything the standard does not cover. KWP2000 calls
    // service 0x21 readDataByLocalIdentifier: one byte selects a block, and the
    // meaning of each block is the maker's own and is not published — Toyota
    // licenses the list to trade tools rather than documenting it.
    //
    // Which is exactly why sweeping is worth more than guessing. The identifier
    // is a single byte, so the whole space is 256 questions and a car will answer
    // to a handful of them. What comes back is a block of bytes whose meaning is
    // unknown, but knowing *which* blocks exist and how long each one is turns an
    // undocumented protocol into a short list to work through.
    //
    // Read-only by the definition of the service. Nothing here can change a
    // setting, and the routine-starting service that could (0x31) is refused by
    // Guard().

    Section(Log, "MODE 21 — readDataByLocalIdentifier (Toyota's enhanced data)");
    Log("# A positive answer echoes 0x61 then the identifier. 0x7F 21 11 means the");
    Log("# service does not exist here at all — on that reply the rest is pointless.");
    Log("# 0x7F 21 12 means the service is there and that identifier is not.");
    Log("");

    // Long messages allowed: Toyota's blocks routinely run past the seven bytes
    // an adapter will otherwise hand back, and a truncated block reads as a
    // shorter one rather than as an error.
    Setup(elm, Log, "ATAL", "allow replies longer than seven bytes");
    Log("");

    bool sweep = args.Contains("--sweep");
    int found = 0;

    foreach (int id in sweep ? Enumerable.Range(0, 256) : [0x00, 0x01, 0x02, 0x03, 0x04, 0x05])
    {
        string command = $"21{id:X2}";
        Guard(command);

        string reply = elm.Send(command, TimeSpan.FromSeconds(3), settle: !sweep);
        string text = Readable(reply);

        // On a sweep only the answers are worth printing: 250 lines of "NO DATA"
        // buries the six lines that matter.
        bool answered = text.Contains("61", StringComparison.OrdinalIgnoreCase)
                        && !text.Contains("7F", StringComparison.OrdinalIgnoreCase);

        if (answered) found++;

        if (answered || !sweep)
        {
            Log($"-> {command,-12}{(answered ? "ANSWERED" : "")}");
            Log($"<- {text}");
            Log("");
        }
    }

    if (sweep) Log($"# swept 256 identifiers, {found} answered");
    else Log("# only the first six were asked. Add --sweep to try all 256.");

    // ----- SSM over CAN ------------------------------------------------------

    // The interesting one and the least certain. On the older K-line cars SSM is
    // framed itself — 0x80, destination, source, length, command, checksum — and
    // an ELM327 cannot produce that. Over CAN the ISO-TP layer does the framing,
    // so in principle the command bytes alone are the payload and the adapter
    // can carry them. Whether a 2014 Subaru answers that way is exactly what is
    // not known, which is why several framings are tried rather than one.
    //
    // The addressing: SSM speaks to the engine module at 0x7E0 and is answered
    // on 0x7E8, the same pair OBD2 uses for a directed request.

    Section(Log, "SSM OVER CAN");
    Log("# Header set to 7E0, replies filtered to 7E8. A positive answer to the");
    Log("# init command echoes 0xFF and carries the ECU identifier followed by a");
    Log("# capability bitmap; a read echoes 0xE8. Anything else — NO DATA, ?,");
    Log("# silence — means this framing was not understood.");
    Log("");

    Setup(elm, Log, "ATSH7E0", "send to the engine module");
    Setup(elm, Log, "ATCRA7E8", "listen only to its replies");
    Setup(elm, Log, "ATFCSH7E0", "flow control header");
    Setup(elm, Log, "ATFCSD300000", "flow control data — send all, no delay");
    Setup(elm, Log, "ATFCSM1", "use the flow control set above");
    Log("");

    // Init. The single most valuable answer here: it names the ECU and returns
    // the bitmap of every address the car will report, which would remove all
    // the guesswork that follows.
    foreach ((string command, string what) in new[]
             {
                 ("BF", "SSM init, bare command"),
                 ("BF40", "SSM init with the padding byte the K-line form uses"),
                 ("A8000000", "read one address, the continuous-response form"),

                 // 0x00000E is where engine speed sits on a good many Subarus.
                 // A candidate rather than a fact, which is the point of asking.
                 ("A80000000E", "read address 0x00000E — engine speed on many"),
                 ("A80100000E", "the same, single-response form"),
             })
    {
        Ask(elm, Log, command, what);
    }

    // Put the adapter back the way it was found. A probe that leaves a header
    // set has changed what the next connection does, and the next connection is
    // the application.
    Section(Log, "RESTORING");
    Setup(elm, Log, "ATCRA", "clear the receive filter");
    Setup(elm, Log, "ATFCSM0", "flow control back to automatic");
    Setup(elm, Log, "ATSH7DF", "back to the broadcast header OBD2 uses");
    Log($"reset      : {elm.Reset()}");
}
catch (Exception e)
{
    Log($"\nThe probe stopped: {e.GetType().Name}: {e.Message}");
    exit = 4;
}
finally
{
    transport?.Dispose();

    try
    {
        File.WriteAllText(transcriptPath, transcript.ToString());
        Console.WriteLine($"\nTranscript written to {Path.GetFullPath(transcriptPath)}");
    }
    catch (Exception e) when (e is IOException or UnauthorizedAccessException)
    {
        Console.Error.WriteLine($"\nCould not write the transcript: {e.Message}");
    }
}

return exit;

static void Section(Action<string> log, string title)
{
    log("");
    log(new string('-', 68));
    log($"## {title}");
    log(new string('-', 68));
    log("");
}

/// <summary>
/// Sends one request and writes down exactly what came back.
///
/// Deliberately uninterpreted. The reply is printed as the adapter gave it,
/// control characters and all, because a probe whose job is to find out what a
/// car speaks must not decide in advance what the answer was going to look like.
/// </summary>
static void Ask(Elm327 elm, Action<string> log, string command, string what)
{
    Guard(command);

    var clock = Stopwatch.StartNew();
    string reply = elm.Send(command, TimeSpan.FromSeconds(4), settle: true);
    clock.Stop();

    log($"-> {command,-12} {what}");
    log($"<- {Readable(reply)}   [{clock.ElapsedMilliseconds} ms]");
    log("");
}

/// <summary>An adapter setting, whose only interesting outcome is OK or not.</summary>
static void Setup(Elm327 elm, Action<string> log, string command, string what)
{
    Guard(command);

    string reply = elm.Send(command, TimeSpan.FromSeconds(2));

    log($"-> {command,-12} {what}  ->  {Readable(reply)}");
}

/// <summary>
/// Refuses anything that is not a read.
///
/// The SSM command set can write to arbitrary addresses in a running ECU, and
/// this tool exists to find out what a car says rather than to change it. Rather
/// than trusting every string above to be harmless, the allowed commands are
/// listed and everything else throws — so a careless edit to this file fails
/// loudly instead of quietly sending a write to somebody's engine.
/// </summary>
static void Guard(string command)
{
    string text = command.ToUpperInvariant();

    if (text.StartsWith("AT", StringComparison.Ordinal)) return;

    // OBD2 service 01 and 09 read; 22 reads by identifier. KWP2000 service 21 is
    // readDataByLocalIdentifier, which is what Toyota puts its extra parameters
    // behind — read-only by definition of the service. SSM 0xA8 and 0xA0 read
    // addresses and blocks, 0xBF asks what is supported.
    //
    // Absent on purpose: SSM 0xB8 writes an address, and KWP 0x31 starts a
    // routine. Neither belongs in a tool for finding out what a car will say.
    bool reads =
        text.StartsWith("01", StringComparison.Ordinal)
        || text.StartsWith("09", StringComparison.Ordinal)
        || text.StartsWith("21", StringComparison.Ordinal)
        || text.StartsWith("22", StringComparison.Ordinal)
        || text.StartsWith("A8", StringComparison.Ordinal)
        || text.StartsWith("A0", StringComparison.Ordinal)
        || text.StartsWith("BF", StringComparison.Ordinal);

    if (!reads)
        throw new InvalidOperationException(
            $"'{command}' is not one of the read commands this tool is allowed to send.");
}

/// <summary>The reply on one line, with the line breaks shown rather than obeyed.</summary>
static string Readable(string reply)
{
    string text = reply.Replace("\r", " | ").Replace("\n", "").Trim();

    while (text.Contains("|  |", StringComparison.Ordinal))
        text = text.Replace("|  |", "|", StringComparison.Ordinal);

    return text.Trim(' ', '|') is { Length: > 0 } trimmed ? trimmed : "(nothing)";
}

static string? Value(string[] args, string name)
{
    int at = Array.IndexOf(args, name);

    return at >= 0 && at + 1 < args.Length ? args[at + 1] : null;
}
