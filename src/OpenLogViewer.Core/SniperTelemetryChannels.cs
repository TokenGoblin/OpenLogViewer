namespace OpenLogViewer.Core;

/// <summary>
/// One entry in a Sniper ECU's fixed, 119-channel telemetry catalog —
/// <c>SNIPER_ECU_CLIENT_SPEC.md</c> §5 / <c>telemetry_channels.csv</c>. Name,
/// units and declared range only: <b>this does not say where a channel's
/// value lives in a polled reply</b> — see <see cref="SniperTelemetryChannels"/>'s
/// own header comment for why that is a separate, still-open problem.
/// </summary>
/// <param name="Index">
/// 0-based position in the ECU's own master table — what a <c>.dm</c> gauge
/// file's <c>ChannelID</c> is. Stable and firmware-defined, not invented here.
/// </param>
/// <param name="Name">The ECU's own display name for the channel.</param>
/// <param name="Units">Empty where the channel is unitless or dimensionless (a percentage, a flag, a count).</param>
/// <param name="Min">The declared minimum, from the master table.</param>
/// <param name="Max">The declared maximum.</param>
/// <param name="DefaultVisibilityFlags">
/// The raw flag word the catalog carries per entry. Bit 0 is documented
/// (§5: "flags(bit0=hidden default)") — see <see cref="HiddenByDefault"/> —
/// the remaining bits are uninterpreted; recorded rather than discarded in
/// case they turn out to matter.
/// </param>
public sealed record SniperTelemetryChannel(
    int Index, string Name, string Units, double Min, double Max, int DefaultVisibilityFlags)
{
    /// <summary>Whether Sniper.exe's own Data Monitor hides this channel by default. §5, bit 0.</summary>
    public bool HiddenByDefault => (DefaultVisibilityFlags & 1) != 0;
}

/// <summary>
/// The Sniper ECU's fixed 119-channel telemetry catalog — transcribed from
/// <c>telemetry_channels.csv</c> (the spec's own companion file), which in
/// turn was read from the master table at <c>DAT_0059db14</c> in the
/// decompiled app. ✅ for every field here.
///
/// <para>
/// <b>What this catalog cannot do: turn a polled reply into a value.</b>
/// <c>telemetry_channels.csv</c>'s own <c>source_offset_or_pid</c> column
/// says, for every single one of the 119 rows, "runtime accessor switch (val
/// getter keyed by index)" — i.e. the value for a channel is fetched by
/// code, not read from a fixed struct offset the catalog records. §5 says
/// this outright: "The exact cache offset per channel index is not yet
/// mapped" — <b>🔴 [CONFIRM ON HW]</b>, and recovering it needs either
/// tracing the accessor switch in a decompiler or, faster, a live capture
/// (rev the engine, watch which bytes of a polled reply move for channel 2 =
/// RPM). Until then, this catalog is names, units and ranges only — useful
/// for whatever eventually maps offsets to indices, not a working decoder on
/// its own. See <see cref="SniperSession.PollTelemetryRaw"/>, which returns
/// exactly the undecoded bytes this catalog cannot yet turn into channels.
/// </para>
/// </summary>
public static class SniperTelemetryChannels
{
    public static readonly IReadOnlyList<SniperTelemetryChannel> All =
    [
        new(0, "Placeholder", "", 0, 1, 0),
        new(1, "RTC", "sec", -1000000, 1000000, 0),
        new(2, "RPM", "RPM", 0, 20000, 256),
        new(3, "Inj PW", "msec", 0, 70, 256),
        new(4, "Duty Cycle", "", 0, 100, 256),
        new(5, "CL Comp", "", 0, 999, 0),
        new(6, "Target AFR", "A/F", 2, 20, 256),
        new(7, "AFR", "A/F", 2, 20, 256),
        new(8, "Air Temp Enr", "", 0, 600, 256),
        new(9, "Coolant Enr", "", 70, 200, 256),
        new(10, "Coolant AFR Offset", "A/F", -5, 5, 256),
        new(11, "Afterstart Enr", "", 75, 300, 256),
        new(12, "Current Learn", "", -999, 999, 256),
        new(13, "CL Status", "", 0, 1, 257),
        new(14, "Learn Status", "", 0, 1, 257),
        new(15, "Fuel Flow", "lb/hr", 0, 9999.9004, 256),
        new(16, "MAP RoC", "kpa/sec x10", 0, 100, 256),
        new(17, "TPS RoC", "", 0, 1000, 256),
        new(18, "Estimated VE", "", 0, 999, 256),
        new(19, "Ignition Timing", "", 0, 60, 256),
        new(20, "Main Rev Limit", "", 0, 1, 257),
        new(21, "Rev Limit #1", "", 0, 1, 257),
        new(22, "Launch Retard", "", 0, 60, 256),
        new(23, "IAC Position", "", 0, 100, 256),
        new(24, "MAP", "kPa", 0, 999, 256),
        new(25, "TPS", "", 0, 100, 256),
        new(26, "MAT", "", -40, 999, 256),
        new(27, "CTS", "", -40, 999, 256),
        new(28, "Battery", "Volts", 0, 20, 256),
        new(29, "Custom 1", "", -99999, 99999, 256),
        new(30, "Custom 2", "", -99999, 99999, 256),
        new(31, "Custom 3", "", -99999, 99999, 256),
        new(32, "AC Kick", "", 0, 1, 257),
        new(33, "Fan #1", "", 0, 1, 257),
        new(34, "Fan #2", "", 0, 1, 257),
        new(35, "#2 Fuel Pump", "", 0, 1, 257),
        new(36, "AC Shutdown", "", 0, 1, 257),
        new(37, "Sensor Warning", "", 0, 1, 257),
        new(38, "Sensor Caution", "", 0, 1, 257),
        new(39, "Base Fuel lb/hr", "lb/hr", 0, 9999, 256),
        new(40, "Base Fuel VE", "", 0, 9999, 256),
        new(41, "Base Timing", "", 0, 60, 256),
        new(42, "Base Target AFR", "A/F", 2, 20, 256),
        new(43, "Base Ign Dwell", "msec", 0, 10, 256),
        new(44, "Vol Comp Ign Dwell", "msec", 0, 10, 256),
        new(45, "Timing vs Air", "", -20, 20, 256),
        new(46, "Timing vs Cool", "", -20, 20, 256),
        new(47, "Status 1", "", -99999, 99999, 0),
        new(48, "Status 2", "", -99999, 99999, 0),
        new(49, "Status 3", "", -99999, 99999, 0),
        new(50, "Status 4", "", -99999, 99999, 0),
        new(51, "Status 5", "", -99999, 99999, 0),
        new(52, "Status 6", "", -99999, 99999, 0),
        new(53, "Status 7", "", -99999, 99999, 0),
        new(54, "Status 8", "", -99999, 99999, 0),
        new(55, "Inj Set#1 PPH", "lb/hr", 0, 5000, 0),
        new(56, "Inj Set#2 PPH", "lb/hr", 0, 5000, 0),
        new(57, "Inj Set#1 PW", "msec", 0, 999, 0),
        new(58, "Inj Set#2 PW", "msec", 0, 999, 0),
        new(59, "Boost PSIG", "psig", 0, 999, 256),
        new(60, "Boost Time", "sec", 0, 99, 256),
        new(61, "Target Boost", "psig", 0, 58, 256),
        new(62, "Trans Brake", "", 0, 1, 257),
        new(63, "Boost Solenoid Duty", "", -100, 100, 256),
        new(64, "Boost Safety", "", 0, 1, 256),
        new(65, "Boost Master Enbl", "", 0, 1, 257),
        new(66, "Boost Fill Sol DC", "", 0, 100, 256),
        new(67, "Boost Vent Sol DC", "", 0, 100, 256),
        new(68, "N2O Stage 1", "", 0, 100, 256),
        new(69, "N2O Enabled", "", 0, 1, 257),
        new(70, "N2O Input #1", "", 0, 1, 257),
        new(71, "N2O Lean Cutoff", "", 0, 1, 257),
        new(72, "N2O Rich Cutoff", "", 0, 1, 257),
        new(73, "N2O RPM Cutoff", "", 0, 1, 257),
        new(74, "N2O MAP Cutoff", "", 0, 1, 257),
        new(75, "N2O Dry Fuel #1", "lb/hr", 0, 600, 256),
        new(76, "N2O Tmg Mod #1", "", 0, 60, 256),
        new(77, "N2O Timer #1", "sec", 0, 999, 256),
        new(78, "Diag #1", "", -10000, 10000, 0),
        new(79, "Diag #2", "", -10000, 10000, 0),
        new(80, "Diag #3", "", -10000, 10000, 0),
        new(81, "Diag #4", "", -10000, 10000, 0),
        new(82, "Diag #5", "", -10000, 10000, 0),
        new(83, "Diag #6", "", -10000, 10000, 0),
        new(84, "Diag #7", "", -10000, 10000, 0),
        new(85, "Diag #8", "", -10000, 10000, 0),
        new(86, "Diag #9", "", -10000, 10000, 0),
        new(87, "Diag #10", "", -10000, 10000, 0),
        new(88, "Diag #11", "", -10000, 10000, 0),
        new(89, "Diag #12", "", -10000, 10000, 0),
        new(90, "Diag #13", "", -10000, 10000, 0),
        new(91, "Diag #14", "", -10000, 10000, 0),
        new(92, "Diag #15", "", -10000, 10000, 0),
        new(93, "Diag #16", "", -10000, 10000, 0),
        new(94, "Diag #17", "", -10000, 10000, 0),
        new(95, "Diag #18", "", -10000, 10000, 0),
        new(96, "Diag #19", "", -10000, 10000, 0),
        new(97, "Diag #20", "", -10000, 10000, 0),
        new(98, "AT Launch Input", "", 0, 1, 257),
        new(99, "AT 1D #1", "", 0, 100, 0),
        new(100, "AT 1D #2", "", 0, 100, 0),
        new(101, "AT 1D #3", "", 0, 100, 0),
        new(102, "AT 1D #4", "", 0, 100, 0),
        new(103, "AT 2D #1", "", 0, 100, 0),
        new(104, "AT 2D #2", "", 0, 100, 0),
        new(105, "TCC Lockup", "", 0, 1, 257),
        new(106, "Gear", "", 0, 8, 256),
        new(107, "Speed", "MPH", 0, 300, 256),
        new(108, "Line Pressure", "", 0, 100, 256),
        new(109, "Input Shaft Speed", "RPM", 0, 20000, 256),
        new(110, "Accum Pressure", "", 0, 100, 256),
        new(111, "TCC Duty Cycle", "", 0, 100, 256),
        new(112, "Line Temp", "", -50, 325, 256),
        new(113, "Trans Man US Input", "", 0, 1, 257),
        new(114, "Trans Man DS Input", "", 0, 1, 257),
        new(115, "Trans Auto/Man In", "", 0, 1, 257),
        new(116, "Trans Range Pos", "", -2, 4, 256),
        new(117, "Line Pressure PSI", "psi", 0, 500, 256),
        new(118, "Trans Fan", "", 0, 1, 256),
    ];

    /// <summary>Looks a channel up by its master-table index, or null if out of range.</summary>
    public static SniperTelemetryChannel? ByIndex(int index) =>
        index >= 0 && index < All.Count ? All[index] : null;
}
