//! Ported from `CanFrame.cs`.

use crate::{Result, fmt};

/// One frame off a CAN bus.
///
/// Deliberately not a channel. Everything else live here is a named value sampled
/// over time; a frame is an identifier, some bytes whose meaning is unknown until
/// somebody works it out, and the moment it arrived.
#[derive(Clone, Debug, PartialEq, Eq)]
pub struct CanFrame {
    /// The identifier, 11-bit or 29-bit. Not masked to eleven bits, because an
    /// extended id does not fit and truncating one silently merges it with
    /// whatever shares its low bits.
    pub id: i32,
    /// Whether the id is a 29-bit one.
    pub is_extended: bool,
    /// The payload, nought to eight bytes.
    pub data: Vec<u8>,
    /// When it arrived, as the source reports it: microseconds since whatever the
    /// source counts from, not wall-clock time and not comparable between sources.
    pub at: i64,
}

impl CanFrame {
    /// How many bytes the frame declares, which is what is in `data`.
    pub fn length(&self) -> usize {
        self.data.len()
    }

    /// The id as it is written down and searched for: hex, and wide enough to sort.
    pub fn label(&self) -> String {
        fmt::int(self.id, if self.is_extended { "X8" } else { "X3" })
    }

    /// The payload as a person reads it: `Convert.ToHexString`, uppercase.
    pub fn hex(&self) -> String {
        self.data.iter().map(|b| format!("{b:02X}")).collect()
    }
}

/// Somewhere frames come from: an ECU relaying its bus, or an adapter that is
/// nothing but a bus.
///
/// The same shape as [`LiveSource`](super::LiveSource) and for the same reason.
/// What it adds is [`dropped`](Self::dropped), because a CAN source can lose
/// frames in a way a channel source cannot, and a capture that quietly loses them
/// is worse than no capture at all: somebody reading an unknown bus concludes an
/// identifier does not exist when in truth it was missed.
///
/// The C#'s `IDisposable` is `Drop`.
pub trait CanSource: Send {
    /// Starts the source, and whatever the device needs to begin listening.
    fn open(&mut self) -> Result<()>;

    /// Whatever has arrived since the last call, oldest first. Empty is the
    /// ordinary case on a quiet bus rather than a fault.
    fn read(&mut self) -> Result<Vec<CanFrame>>;

    /// Frames the device is known to have lost since the capture began, or `None`
    /// where it cannot say.
    ///
    /// `None` and `Some(0)` are different answers and must not be shown the same
    /// way: nought is "none lost", `None` is "this device does not count them", and
    /// only one of those licenses anybody to call a capture complete.
    fn dropped(&self) -> Option<i32>;

    /// What this source can honestly be said to do, for the interface to report
    /// rather than imply.
    fn kind(&self) -> &CanSourceKind;
}

/// What a CAN source is, in the terms that decide whether a capture can be trusted
/// for the job in hand.
#[derive(Clone, Debug, PartialEq, Eq)]
pub struct CanSourceKind {
    /// What to call it on screen.
    pub name: String,
    /// Whether it reports identifiers it was not configured for. False makes a
    /// source useless for discovering an unknown bus while leaving it perfectly
    /// good for watching traffic somebody already knows about.
    pub sees_every_id: bool,
    /// Whether [`CanSource::dropped`] means anything.
    pub counts_drops: bool,
    /// What is true about this source that the numbers do not say: the sentence
    /// somebody needs before they believe a capture. Empty by default in the C#.
    pub caveat: String,
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn labels_are_hex_and_wide_enough_to_sort() {
        let standard = CanFrame {
            id: 0x7E8,
            is_extended: false,
            data: vec![0x02, 0x41, 0xAB],
            at: 0,
        };
        assert_eq!(standard.label(), "7E8");
        assert_eq!(standard.hex(), "0241AB");
        let extended = CanFrame {
            id: 0x18DA_F110,
            is_extended: true,
            data: vec![],
            at: 0,
        };
        assert_eq!(extended.label(), "18DAF110");
        assert_eq!(extended.length(), 0);
    }
}
