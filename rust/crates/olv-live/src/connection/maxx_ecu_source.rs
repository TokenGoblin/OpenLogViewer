//! Ported from `MaxxEcuSource.cs`, with `MaxxGauges.For` (which could not live in
//! `olv-core`: it names `MaxxChannel`, and `olv-protocol` depends on `olv-core`).
//! Owned by package `w3-connection`.
//!
//! A live source over a MaxxECU's own link, whether that is the USB port on the ECU or
//! a paired Bluetooth one.
//!
//! The two are the same conversation. A MaxxECU's Bluetooth and Wi-Fi are an ESP32
//! sitting in front of the controller that actually runs the engine, and it relays
//! application messages without adding anything of its own; USB reaches that same
//! controller directly. So everything below (the activation, the subscription, the
//! framing) is transport-agnostic, and the only thing that differs is how long the first
//! frame takes to appear.
//!
//! Unlike the TunerStudio path, nothing is requested per sample: the ECU is told once
//! what to send and then pushes frames of its own accord. So a read here waits for the
//! next telemetry frame rather than asking for one.
//!
//! Two things have to happen first, in order, and neither is optional.
//!
//! A MaxxECU that has not seen an mDash session since it was powered on accepts a
//! Bluetooth socket, reports itself connected, and sends nothing at all, for as long as
//! you care to wait. The activation frames unlock it. Confirmed on a cold ECU: two
//! seconds of silence before, 17,544 bytes in twelve seconds after.
//!
//! Then it has to be told which channels to send. Activation alone produces
//! configuration and label dumps and never a reading.
//!
//! Nothing here writes to the ECU's tune: the only bytes sent are the two replayed
//! activation and subscription messages. (Command 0x2F, which erases flash, is refused
//! by `olv_protocol::ms::maxx_usb_protocol` and is never reachable from this file.)

use std::time::{Duration, Instant};

use olv_core::model::LiveSource;
use olv_core::{Error, Result, fmt};
use olv_protocol::ms::ms_protocol::EcuTransport;
use olv_protocol::ms::{maxx_frame_reader::MaxxFrameReader, maxx_protocol};

/// How long to wait for a reading before calling it a failure.
const FRAME_TIMEOUT: Duration = Duration::from_secs(2);

/// The longer allowance the first reading gets.
///
/// A running session is two seconds from its last frame to the next one. The first
/// frame after arming is a different matter: the ECU answers the activation with its
/// configuration and label dumps (17,544 bytes over twelve seconds on a cold one) and
/// the first actual reading arrives somewhere inside that. Two seconds is comfortably
/// enough over Bluetooth, where the ECU has usually been awake for a while, and is
/// exactly the window a cold ECU on a freshly plugged USB cable misses.
const FIRST_FRAME_TIMEOUT: Duration = Duration::from_secs(8);

pub struct MaxxEcuSource {
    transport: Box<dyn EcuTransport>,
    reader: MaxxFrameReader,
    buffer: [u8; 4096],
    values: Vec<f64>,
    other_frames: i32,
    names: Vec<String>,
    units: Vec<String>,
    digits: Vec<i32>,
}

impl MaxxEcuSource {
    pub fn new(transport: Box<dyn EcuTransport>) -> Result<Self> {
        if !maxx_protocol::verify() {
            return Err(Error::Protocol(
                "The MaxxECU subscription does not match the table it is decoded with, \
                 so every channel after the disagreement would read as its neighbour."
                    .to_string(),
            ));
        }

        let subscribed = maxx_protocol::SUBSCRIBED;

        Ok(Self {
            transport,
            reader: MaxxFrameReader::new(),
            buffer: [0; 4096],
            values: vec![0.0; subscribed.len()],
            other_frames: 0,
            names: subscribed.iter().map(|c| c.name.to_string()).collect(),
            units: subscribed.iter().map(|c| c.units.to_string()).collect(),
            digits: subscribed.iter().map(|c| c.digits).collect(),
        })
    }

    /// Frames that were not readings: normal traffic, not failures.
    pub fn other_frames(&self) -> i32 {
        self.other_frames
    }

    /// Sends the activation and the subscription.
    ///
    /// Both are replayed byte for byte, because their checksum algorithm is
    /// unidentified, so they cannot be composed, only repeated. The activation was
    /// captured identically across nine separate sessions, which is what makes
    /// repeating it sound rather than a guess.
    fn arm(&mut self) -> Result<()> {
        self.transport.write(maxx_protocol::ACTIVATION)?;
        std::thread::sleep(Duration::from_millis(300));
        self.transport.write(maxx_protocol::SUBSCRIPTION)
    }

    fn read_within(&mut self, within: Duration) -> Result<Vec<f64>> {
        let deadline = Instant::now() + within;

        while Instant::now() < deadline {
            // Anything already reassembled, before waiting on the link again: one read
            // can carry several frames.
            while let Some(frame) = self.reader.try_take() {
                if maxx_protocol::try_decode(&frame, &mut self.values) {
                    return Ok(self.values.clone());
                }

                // Configuration, labels and heartbeats all arrive on the same link and
                // are perfectly normal.
                self.other_frames += 1;
            }

            let got = self.transport.read(&mut self.buffer, Duration::from_millis(200))?;
            if got > 0 {
                self.reader.feed(&self.buffer[..got]);
            }
        }

        let seconds = fmt::f64(within.as_secs_f64(), "N0");

        let detail = if self.other_frames > 0 {
            format!(
                "A MaxxECU is there and talking \u{2014} {} other frames arrived \u{2014} but it \
                 sent no telemetry, which is what an ECU does when it did not take the \
                 subscription.",
                self.other_frames
            )
        } else {
            "Nothing that speaks MaxxECU answered. Over a cable, check it is the MaxxECU's \
             own USB port rather than another device on the same machine; over Bluetooth, \
             check the ECU has power, since a paired port is listed either way."
                .to_string()
        };

        Err(Error::Protocol(format!(
            "No reading arrived within {seconds} seconds. {detail}"
        )))
    }
}

impl LiveSource for MaxxEcuSource {
    fn names(&self) -> &[String] {
        &self.names
    }

    fn units(&self) -> &[String] {
        &self.units
    }

    fn digits(&self) -> &[i32] {
        &self.digits
    }

    fn open(&mut self) -> Result<()> {
        self.transport.open()?;
        self.arm()?;

        // And prove it, before anything reports a session as running.
        //
        // Opening and arming are not evidence that anything is coming back; `recover`
        // has said so since it was written, and the way in did not. A port that reaches
        // nothing opens perfectly happily: a Bluetooth one that is paired to a
        // switched-off ECU, a USB one that is some other device entirely. Without this
        // the window said "Live: MaxxECU", drew fourteen empty gauges, and dropped the
        // session a couple of seconds later from a background thread, which reads as a
        // link that worked and then broke rather than one that was never there.
        self.read_within(FIRST_FRAME_TIMEOUT)?;
        Ok(())
    }

    fn read(&mut self) -> Result<Vec<f64>> {
        self.read_within(FRAME_TIMEOUT)
    }

    /// Reopens the link and arms it again.
    ///
    /// The ECU forgets its subscription when the socket goes, so coming back means
    /// repeating both messages: reconnecting without them gives a healthy socket and
    /// silence, which is the same symptom as never having connected.
    fn recover(&mut self) -> Result<()> {
        // A link whose device has gone cannot be closed politely.
        self.transport.close();

        // Which proves it on the way: opening and arming are not evidence that anything
        // is coming back.
        self.open()
    }

    /// Always zero: nothing is ever asked for twice here.
    ///
    /// The ECU pushes frames once it has been told what to send, so there is no request
    /// to repeat. Counting the configuration and label frames that arrive alongside the
    /// readings would report a healthy link as one making hundreds of retries, which is
    /// what it did.
    fn retries(&self) -> i32 {
        0
    }
}

/// `MaxxGauges.For`: gauges for the channels a MaxxECU is subscribed to.
pub mod maxx_gauges {
    use std::path::Path;

    use olv_core::ini::gauge_catalog::GaugeSpec;
    use olv_core::logs::maxx_gauges::read_limits;
    use olv_protocol::ms::maxx_protocol::MaxxChannel;

    /// Builds a gauge for each subscribed channel.
    ///
    /// Falls back to a bare readout where MTune is absent or says nothing useful,
    /// rather than inventing a range: a dial with a made-up scale reads as a
    /// measurement. No warning or danger bands, for the reason
    /// [`read_limits`] gives.
    pub fn for_channels(channels: &[MaxxChannel], definitions_path: Option<&Path>) -> Vec<GaugeSpec> {
        let limits = read_limits(definitions_path);
        let mut gauges = Vec::with_capacity(channels.len());

        for channel in channels {
            let (low, high) = limits.get(&channel.id).copied().unwrap_or((0.0, 0.0));

            let mut gauge = GaugeSpec::new(channel.name, channel.name, channel.name);
            gauge.units = channel.units.to_string();
            gauge.category = "MaxxECU".to_string();
            gauge.low = low;
            gauge.high = high;
            gauge.value_digits = channel.digits;
            gauge.label_digits = if channel.digits > 1 { 1 } else { channel.digits };

            gauges.push(gauge);
        }

        gauges
    }
}
