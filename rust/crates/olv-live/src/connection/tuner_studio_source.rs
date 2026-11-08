//! Ported from `TunerStudioSource.cs`. Owned by package `w3-connection`.
//!
//! A live source over the TunerStudio protocol: MegaSquirt, rusEFI, anything with a
//! firmware INI.
//!
//! Reads the realtime block and hands back only the channels the firmware's datalog
//! definition names, under the names it gives them. Taking every decoded value instead
//! would produce a few hundred internal names no preset or filter would ever match, but
//! the full decode is still done every poll regardless, so it costs nothing extra to
//! keep it around for whoever asks for it through
//! [`RawTelemetrySource`].

use std::sync::{Arc, Mutex, PoisonError};

use olv_core::Result;
use olv_core::ini::msq_ini::DatalogEntry;
use olv_core::model::{LiveSource, RawTelemetrySource, eq_ignore_case};
use olv_protocol::obd::realtime_decoder::RealtimeDecoder;

use super::ecu_connection::EcuConnection;

pub struct TunerStudioSource {
    connection: Arc<EcuConnection>,
    decoder: RealtimeDecoder,
    source_index: Vec<usize>,
    names: Vec<String>,
    units: Vec<String>,
    digits: Vec<i32>,
    raw_units: Vec<String>,

    /// The full block from the most recent read. The whole array is replaced rather
    /// than mutated and the lock is held only for the swap, so a reader on another
    /// thread gets either the previous frame or this one, never a partly written one,
    /// and the poll thread never waits on it for longer than a pointer copy (the C#
    /// used `Volatile` for the same rule).
    last_raw_frame: Mutex<Option<Arc<[f64]>>>,
}

impl TunerStudioSource {
    pub fn new(connection: Arc<EcuConnection>, decoder: RealtimeDecoder, datalog: &[DatalogEntry]) -> Self {
        let decoder_units = decoder.units();

        let mut indices = Vec::new();
        let mut names: Vec<String> = Vec::new();
        let mut units = Vec::new();
        let mut digits = Vec::new();

        for entry in datalog {
            let Some(at) = last_index_of(decoder.names(), &entry.channel) else {
                continue;
            };

            let label = if entry.label.is_empty() {
                entry.channel.clone()
            } else {
                entry.label.clone()
            };

            // `HashSet<string>(OrdinalIgnoreCase).Add` is false for a label already taken.
            if names.iter().any(|taken| eq_ignore_case(taken, &label)) {
                continue;
            }

            indices.push(at);
            names.push(label);
            units.push(decoder_units[at].clone());
            digits.push(entry.digits);
        }

        Self {
            connection,
            decoder,
            source_index: indices,
            names,
            units,
            digits,
            raw_units: decoder_units,
            last_raw_frame: Mutex::new(None),
        }
    }

    /// The connection this reads through, shared with whatever reads or writes the tune.
    pub fn connection(&self) -> &Arc<EcuConnection> {
        &self.connection
    }
}

impl LiveSource for TunerStudioSource {
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
        Ok(self.connection.open()?)
    }

    fn read(&mut self) -> Result<Vec<f64>> {
        let block = self.connection.read_realtime(self.decoder.layout().block_size)?;
        let decoded: Arc<[f64]> = self.decoder.decode(&block).into();

        *self.last_raw_frame.lock().unwrap_or_else(PoisonError::into_inner) = Some(Arc::clone(&decoded));

        Ok(self.source_index.iter().map(|&i| decoded[i]).collect())
    }

    /// Closes and reopens the link, then proves it by reading a block.
    ///
    /// The port has to be closed before it is reopened: a handle whose device has gone
    /// still reports itself open, so reopening alone does nothing and every read
    /// afterwards fails identically. Opening also proves nothing on its own: an adapter
    /// can enumerate before the ECU behind it is answering.
    ///
    /// Reopening a Speeduino's port resets the board; anything written and not burned
    /// is lost.
    fn recover(&mut self) -> Result<()> {
        self.connection.reopen()?;
        self.connection.read_realtime(self.decoder.layout().block_size)?;
        Ok(())
    }

    fn retries(&self) -> i32 {
        self.connection.retries()
    }

    fn raw_telemetry(&self) -> Option<&dyn RawTelemetrySource> {
        Some(self)
    }
}

impl RawTelemetrySource for TunerStudioSource {
    /// Every channel the firmware's INI decodes, whether or not its datalog names it.
    fn raw_names(&self) -> &[String] {
        self.decoder.names()
    }

    fn raw_units(&self) -> &[String] {
        &self.raw_units
    }

    fn last_raw_frame(&self) -> Option<Arc<[f64]>> {
        self.last_raw_frame
            .lock()
            .unwrap_or_else(PoisonError::into_inner)
            .clone()
    }
}

impl Drop for TunerStudioSource {
    /// `Dispose`: closes the link, even when the connection is also held elsewhere.
    fn drop(&mut self) {
        self.connection.dispose();
    }
}

/// Searched backwards because an INI may define a name twice (three do in the MS3
/// firmware) and the later definition is the one in force.
fn last_index_of(names: &[String], name: &str) -> Option<usize> {
    names.iter().rposition(|candidate| eq_ignore_case(candidate, name))
}
