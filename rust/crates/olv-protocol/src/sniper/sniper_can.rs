//! Ported from `SniperCan.cs`. Owned by package `w1-sniper`.
//!
//! Layer 2 of `SNIPER_ECU_CLIENT_SPEC.md` (section 2): the CAN-id encoding a Sniper dongle
//! and ECU use to route and classify every frame. Pure bit arithmetic, no I/O, so this is
//! fully provable without hardware. Every formula here is marked verified in the spec,
//! in contrast to the node id *values* themselves, which are "obtained during discovery"
//! and unconfirmed on real hardware.

/// What kind of thing an incoming Sniper CAN frame is, by its id alone.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum SniperFrameKind {
    /// A status/data single-frame message. Spec section 2 calls this the app's "code 5" path.
    Data,
    /// One fragment of a multi-frame block transfer; reassemble per section 3.2.
    Bulk,
    /// Part of the node-discovery handshake; learn the ECU's node id from this.
    NodeDiscovery,
    /// Matches none of the three rules. Not something the spec describes.
    Unknown,
}

/// The fixed bit pattern under every bulk/block-transfer id: bit 31 set, among others.
const BULK_BASE: u32 = 0xA001_4004;

/// The fixed bit pattern under every simple single-frame command id.
const SIMPLE_BASE: u32 = 0x7000_4004;

/// Builds a bulk/block-transfer CAN id for a request from `src` to `dst`.
///
/// Spec section 2: `(((dst&0x7FF)<<14)|(src&0x7FF))<<3 | 0xA0014004`.
pub fn bulk_id(dst: i32, src: i32) -> u32 {
    ((((dst & 0x7FF) << 14) | (src & 0x7FF)) << 3) as u32 | BULK_BASE
}

/// Builds a simple single-frame command id for `node`, with `command` selecting which one.
///
/// Spec section 2: `((node&0x7FF)<<3) | 0x7NN4004`, where the spec's "NN nibble" is actually
/// a full byte inserted at bits 16-23 (confirmed by working the three concrete examples
/// backwards: `0x70014004`, `0x700D4004`, `0x700F4004` differ from the simple base only by
/// `command << 16` for command 0x01, 0x0D, 0x0F).
pub fn simple_id(node: i32, command: u8) -> u32 {
    (((node & 0x7FF) << 3) as u32) | SIMPLE_BASE | (u32::from(command) << 16)
}

/// Three concrete simple-command ids the spec's evidence shows. What each one *means*
/// beyond "sent at these points" is not stated, so these are named by what triggers them.
pub mod simple_command {
    /// Zero payload; the app sends this on "ECU detected" (ping/enumerate).
    pub const PING_OR_ENUMERATE: u8 = 0x01;
    /// 4-byte payload = echoed node id + 0x0200.
    pub const NODE_ACK: u8 = 0x0D;
    /// Zero payload. Purpose beyond "seen sent" is unknown.
    pub const UNNAMED_0F: u8 = 0x0F;
}

/// Classifies an incoming frame by its id alone: spec section 2's RX dispatch rules, checked
/// in the exact order the spec gives them as an if/elif chain.
///
/// Order matters. Both [`bulk_id`]'s and [`simple_id`]'s base patterns have bit 2 set, so the
/// first check (`id & 4 == 0`) is false for both and control falls through to the bulk check
/// (true only for bulk ids, which alone set bit 31), and from there to the node-discovery
/// check, which a plain simple id also matches, since its base pattern's top nibble is 0x7.
/// That is not a bug in this reproduction: anything with top nibble 7 that is neither data
/// nor bulk is grouped under node-discovery, real handshake replies included.
pub fn classify(can_id: u32) -> SniperFrameKind {
    if can_id & 4 == 0 {
        return SniperFrameKind::Data;
    }
    if can_id & 0x8000_0000 != 0 {
        return SniperFrameKind::Bulk;
    }
    if (can_id >> 28) & 7 == 7 {
        return SniperFrameKind::NodeDiscovery;
    }
    SniperFrameKind::Unknown
}

#[cfg(test)]
mod tests {
    use super::*;

    /// Reproduces the spec's three concrete simple-command examples exactly, working
    /// backwards from the observed ids to confirm the formula produces them for node 0.
    #[test]
    fn simple_command_ids_match_the_specs_worked_examples() {
        assert_eq!(simple_id(0, simple_command::PING_OR_ENUMERATE), 0x7001_4004);
        assert_eq!(simple_id(0, simple_command::NODE_ACK), 0x700D_4004);
        assert_eq!(simple_id(0, simple_command::UNNAMED_0F), 0x700F_4004);
    }

    #[test]
    fn simple_id_folds_in_the_node_at_bit_three() {
        let baseline = simple_id(0, simple_command::PING_OR_ENUMERATE);
        let with_node = simple_id(5, simple_command::PING_OR_ENUMERATE);
        assert_eq!(baseline | (5u32 << 3), with_node);
    }

    #[test]
    fn bulk_id_sets_bit_thirty_one_and_folds_in_both_nodes() {
        let id = bulk_id(0x12, 0x34);
        assert_ne!(id & 0x8000_0000, 0);
        let expected = ((((0x12 & 0x7FF) << 14) | (0x34 & 0x7FF)) << 3) as u32 | 0xA001_4004;
        assert_eq!(id, expected);
        // Byte-level: dst 0x12 -> 0x12<<17, src 0x34 -> 0x34<<3, over the base.
        assert_eq!(id, 0xA001_4004 | (0x12 << 17) | (0x34 << 3));
    }

    #[test]
    fn bulk_id_masks_nodes_to_eleven_bits() {
        // A node id past 0x7FF must not bleed into the bits either node folds into, or two
        // different out-of-range nodes would collide.
        let low = bulk_id(0x7FF, 0);
        let high = bulk_id(0x7FF + 0x800, 0);
        assert_eq!(low, high);
    }

    #[test]
    fn a_bulk_id_classifies_as_bulk() {
        assert_eq!(classify(bulk_id(1, 2)), SniperFrameKind::Bulk);
    }

    /// A plain simple-command id (node 0) does not classify as Data (its base pattern has
    /// bit 2 set) and does not classify as Bulk either, so it falls to NodeDiscovery by the
    /// spec's own three-way split. Pinned so a future edit cannot silently change it.
    #[test]
    fn a_plain_simple_command_id_falls_through_to_node_discovery() {
        assert_eq!(
            classify(simple_id(0, simple_command::PING_OR_ENUMERATE)),
            SniperFrameKind::NodeDiscovery
        );
    }

    #[test]
    fn ids_with_bit_two_clear_are_data() {
        assert_eq!(classify(0x0000_0000), SniperFrameKind::Data);
        assert_eq!(classify(0x0000_0001), SniperFrameKind::Data);
        // Top nibble 7, but bit 2 clear -> still Data; the first check wins.
        assert_eq!(classify(0x7000_0000), SniperFrameKind::Data);
    }

    #[test]
    fn an_id_matching_none_of_the_three_rules_is_unknown() {
        // Bit 2 set (not Data), bit 31 clear (not Bulk), top nibble not 7 (not NodeDiscovery).
        assert_eq!(classify(0x0000_0004), SniperFrameKind::Unknown);
    }
}
