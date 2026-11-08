//! Ported from `MaxxCanSource.cs`. Owned by package `w4-live`.
//!
//! A MaxxECU's CAN bus, read over the USB cable that is already carrying the telemetry.
//!
//! The ECU is the interface. There is no adapter and no second cable: frames are collected by
//! the controller's own driver, put in a ring, and handed over on request, so whatever the ECU
//! is plugged into, this can watch. Its hardware filter accepts everything and the ring is fed
//! before the ECU does any of its own identifier matching, so unknown identifiers arrive too,
//! which is what makes it worth pointing at a bus nobody has documented.
//!
//! The ring holds sixty frames and a read takes fifteen, so a busy bus needs asking often. What
//! stops that being a guess is the drop counter: the ECU counts every frame it could not keep,
//! across both the driver's buffer and the ring, so a capture can say how much it missed
//! instead of looking complete.

use olv_core::Result;
use olv_core::model::{CanFrame, CanSource, CanSourceKind};

use super::maxx_can;
use super::maxx_tune_definitions::MaxxSettingDefinition;
use super::maxx_usb_source::MaxxUsbLink;

pub struct MaxxCanSource {
    ecu: MaxxUsbLink,
    owns_ecu: bool,
    last_count: Option<i32>,
    dropped: Option<i32>,
    kind: CanSourceKind,
}

impl MaxxCanSource {
    /// Watches the bus of an ECU something else is already talking to.
    ///
    /// The session owns the cable and serialises what goes on it, so this asks it rather than
    /// opening the device again: two programs cannot hold a MaxxECU, and neither can two parts
    /// of this one. `owns_ecu` is the C#'s default `false`; when true, dropping this closes the
    /// cable, which waits for the cable lock and so must not happen on a path a window waits
    /// on.
    pub fn new(ecu: MaxxUsbLink, owns_ecu: bool) -> Self {
        Self {
            ecu,
            owns_ecu,
            last_count: None,
            dropped: None,
            kind: CanSourceKind {
                name: "MaxxECU".to_string(),
                sees_every_id: true,
                counts_drops: true,
                caveat: "Frames arrive at whatever CAN speed the ECU itself is set to, so a bus running \
                         at another speed reads as silence rather than as an error."
                    .to_string(),
            },
        }
    }

    /// Whether the ECU is set to put frames in the ring at all.
    ///
    /// Read from the tune rather than assumed, because the ECU clears the flag whenever the
    /// link drops, so an analyzer armed in MTune an hour ago is not armed now, and a silent bus
    /// and an unarmed ECU look identical. False where the definitions do not place the setting
    /// at all, which is the same as not knowing: there is no cable-side way to tell "off" from
    /// "unheard of".
    pub fn is_armed(&self, tune: &[u8], definitions: &[MaxxSettingDefinition]) -> bool {
        maxx_can::enable_at(definitions)
            .and_then(|at| usize::try_from(at).ok())
            .is_some_and(|at| at < tune.len() && tune[at] != 0)
    }

    /// What the ECU says it has lost, or `None` when it will not say.
    fn drop_count(&self) -> Result<Option<i32>> {
        self.ecu.can_drop_count()
    }
}

impl CanSource for MaxxCanSource {
    /// Arms the analyzer and starts counting from where the ECU is now.
    ///
    /// Nothing reaches the ring until the tune's `maxx_can::ENABLE_SETTING` is set, and **this
    /// does not set it**. Turning it on means writing to the tune, and a MaxxECU write is
    /// permanent the moment it lands, which is not something to do as a side effect of opening
    /// a window. It is offered deliberately, or ticked in MTune, and
    /// [`is_armed`](Self::is_armed) says which.
    fn open(&mut self) -> Result<()> {
        // Whatever the ECU had already lost is not this capture's loss, so the count starts
        // from here rather than from whenever the ECU booted: the counter is free-running and
        // nothing resets it, arming the analyzer included.
        self.last_count = self.drop_count()?;
        self.dropped = self.last_count.map(|_| 0);

        Ok(())
    }

    fn read(&mut self) -> Result<Vec<CanFrame>> {
        let mut frames = Vec::new();

        // Until the ring gives back less than a full read, which is how it says it is empty. A
        // busy bus otherwise falls behind one read at a time.
        loop {
            let some = self.ecu.read_can_frames(maxx_can::MOST_PER_READ)?;
            let count = some.len();

            frames.extend(some);

            if count < maxx_can::MOST_PER_READ {
                break;
            }
        }

        let now = self.drop_count()?;

        // A counter that would not answer at open is counted from the first time it does, so
        // a capture can still say how much it missed from there on. (The C# never started
        // counting, and `dropped` stayed unknown for the whole capture.)
        if let (Some(now), None) = (now, self.last_count) {
            self.last_count = Some(now);
            self.dropped = Some(0);
        } else if let (Some(now), Some(last)) = (now, self.last_count) {
            // Added up rather than subtracted from the start.
            //
            // The counter is sixteen bits and free-running, so on a bus losing frames steadily
            // it comes all the way round in well under a minute. Subtracting the baseline
            // would then report the remainder: a capture that lost seventy thousand frames
            // would claim it lost four and a half thousand, which is worse than admitting
            // ignorance because it looks like a measurement.
            self.dropped = Some(self.dropped.unwrap_or(0).wrapping_add(maxx_can::since(last, now)));
            self.last_count = Some(now);
        }

        Ok(frames)
    }

    /// Frames the ECU has counted as lost since this began, or `None` before it has been
    /// asked.
    ///
    /// Counted across the driver's buffer and the ring together, which is one number and not
    /// two, so nought here really does mean nothing was missed, short of the controller's own
    /// hardware queue overrunning, which needs the receive interrupt starved rather than
    /// merely a busy bus.
    fn dropped(&self) -> Option<i32> {
        self.dropped
    }

    fn kind(&self) -> &CanSourceKind {
        &self.kind
    }
}

impl Drop for MaxxCanSource {
    fn drop(&mut self) {
        if self.owns_ecu {
            self.ecu.close();
        }
    }
}
