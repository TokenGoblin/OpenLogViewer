namespace OpenLogViewer.Core;

/// <summary>What kind of thing an incoming Sniper CAN frame is, by its id alone.</summary>
public enum SniperFrameKind
{
    /// <summary>A status/data single-frame message. <c>SNIPER_ECU_CLIENT_SPEC.md</c> §2 calls this the app's "code 5" path.</summary>
    Data,

    /// <summary>One fragment of a multi-frame block transfer — reassemble per §3.2.</summary>
    Bulk,

    /// <summary>Part of the node-discovery handshake — learn the ECU's node id from this.</summary>
    NodeDiscovery,

    /// <summary>Matches none of the three rules below. Not something the spec describes.</summary>
    Unknown,
}

/// <summary>
/// The CAN-id encoding a Sniper dongle and ECU use to route and classify every
/// frame — Layer 2 of <c>SNIPER_ECU_CLIENT_SPEC.md</c> (§2). Pure bit
/// arithmetic, no I/O, so this is fully provable without hardware — which is
/// exactly what it is: every formula here is marked ✅ in the spec (verified in
/// the decompiler/bytes), in contrast to the node id <i>values</i> themselves,
/// which are 🟡 "obtained during discovery" and unconfirmed on real hardware.
/// </summary>
public static class SniperCan
{
    /// <summary>
    /// The fixed bit pattern under every bulk/block-transfer id — bit 31 set,
    /// among others. §2. ✅
    /// </summary>
    private const uint BulkBase = 0xA0014004;

    /// <summary>The fixed bit pattern under every simple single-frame command id. §2. ✅</summary>
    private const uint SimpleBase = 0x70004004;

    /// <summary>
    /// Builds a bulk/block-transfer CAN id for a request from <paramref name="src"/>
    /// to <paramref name="dst"/>. §2: <c>(((dst&amp;0x7FF)&lt;&lt;14)|(src&amp;0x7FF))&lt;&lt;3 | 0xA0014004</c>. ✅
    /// </summary>
    public static uint BulkId(int dst, int src) =>
        (uint)((((dst & 0x7FF) << 14) | (src & 0x7FF)) << 3) | BulkBase;

    /// <summary>
    /// Builds a simple single-frame command id for <paramref name="node"/>, with
    /// <paramref name="command"/> selecting which one. §2:
    /// <c>((node&amp;0x7FF)&lt;&lt;3) | 0x7NN4004</c>, where the spec's "NN nibble" is
    /// actually a full byte inserted at bits 16-23 — confirmed by working the three
    /// concrete examples backwards: <c>0x70014004</c>, <c>0x700D4004</c>,
    /// <c>0x700F4004</c> only differ from <see cref="SimpleBase"/> by
    /// <c>command &lt;&lt; 16</c> for <c>command</c> = 0x01, 0x0D, 0x0F respectively.
    /// </summary>
    public static uint SimpleId(int node, byte command) =>
        (uint)((node & 0x7FF) << 3) | SimpleBase | ((uint)command << 16);

    /// <summary>
    /// Three concrete simple-command ids the spec's evidence actually shows
    /// (§2, payloads ✅ — but what each one <i>means</i> beyond "sent at these
    /// points" is not stated, so these are named by what triggers them, not by
    /// a confirmed purpose).
    /// </summary>
    public static class SimpleCommand
    {
        /// <summary>Zero payload — the app sends this on "ECU detected" (ping/enumerate).</summary>
        public const byte PingOrEnumerate = 0x01;

        /// <summary>4-byte payload = echoed node id + 0x0200.</summary>
        public const byte NodeAck = 0x0D;

        /// <summary>Zero payload. Purpose beyond "seen sent" is unknown.</summary>
        public const byte Unnamed0F = 0x0F;
    }

    /// <summary>
    /// Classifies an incoming frame by its id alone — §2's RX dispatch rules,
    /// checked in the exact order the spec gives them as an if/elif chain. ✅
    ///
    /// Order matters and is not interchangeable: both <see cref="BulkId"/> and
    /// <see cref="SimpleId"/>'s base patterns have bit 2 set, so the first check
    /// (<c>(id &amp; 4) == 0</c>) is false for both and control falls through to
    /// the bulk check (true only for <see cref="BulkId"/>, which alone sets bit
    /// 31), and from there to the node-discovery check — which a plain
    /// <see cref="SimpleId"/> with a zero node also matches, since its base
    /// pattern's top nibble is 0x7. That is not a bug in this reproduction: the
    /// spec's own three-way split only names DATA and BULK confidently; anything
    /// with top-nibble 7 that is neither is grouped under NODE-DISCOVERY, real
    /// handshake replies included.
    /// </summary>
    public static SniperFrameKind Classify(uint canId)
    {
        if ((canId & 4) == 0) return SniperFrameKind.Data;
        if ((canId & 0x80000000) != 0) return SniperFrameKind.Bulk;
        if (((canId >> 28) & 7) == 7) return SniperFrameKind.NodeDiscovery;

        return SniperFrameKind.Unknown;
    }
}
