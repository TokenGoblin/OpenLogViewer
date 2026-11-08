//! Ported from `SsmSource.cs`. Owned by package `w3-connection`.
//!
//! A live source that reads a Subaru's own parameters over SSM.
//!
//! Everything downstream of a [`LiveSource`] already works (the plot, the gauges, filters,
//! calculated channels, recording), so the whole of this is getting bytes off the ECU and
//! giving them names.
//!
//! What it is for is the values OBD2 does not carry at any speed: what the controller has
//! *learnt* rather than what it is measuring this instant. One byte per request at about
//! 146 ms, which is slow and suits the job, since those values move over minutes.
//!
//! The addresses come from a file the user supplies. See
//! [`olv_protocol::obd::ssm_parameter_file`] for why they are not shipped.
//!
//! Read-only: SSM's only command used here is the address read (0xA8). The adapter is put
//! into SSM addressing on the way in and back to ordinary OBD2 addressing on the way out.

use olv_core::model::LiveSource;
use olv_core::{Error, Result};
use olv_protocol::ms::ms_protocol::EcuTransport;
use olv_protocol::obd::elm327::Elm327;
use olv_protocol::obd::ssm::{self, SsmParameter};
use olv_protocol::obd::ssm_parameter_file;

use super::elm327_source::SharedTransport;

/// Consecutive silences before an address is left out of the rotation.
///
/// More forgiving than the OBD2 equivalent, because an address that never answers here
/// means somebody typed it rather than that the car is busy, and dropping it is the only
/// way the rest keep their update rate. It is reported rather than merely dropped.
const GIVE_UP_AFTER: i32 = 8;

pub struct SsmSource {
    transport: SharedTransport,
    elm: Elm327,
    parameters: Vec<SsmParameter>,
    values: Vec<f64>,
    silences: Vec<i32>,
    adapter: String,
    names: Vec<String>,
    units: Vec<String>,
    digits: Vec<i32>,
    retries: i32,
}

impl SsmSource {
    fn new(transport: SharedTransport, elm: Elm327, parameters: Vec<SsmParameter>, adapter: String) -> Self {
        Self {
            transport,
            elm,
            silences: vec![0; parameters.len()],
            names: parameters.iter().map(|p| p.name.clone()).collect(),
            units: parameters.iter().map(|p| p.units.clone()).collect(),
            digits: parameters.iter().map(|p| p.digits).collect(),
            // Nothing has been read yet, and zero is a reading.
            values: vec![f64::NAN; parameters.len()],
            parameters,
            adapter,
            retries: 0,
        }
    }

    /// Opens the adapter, puts it into SSM addressing, and proves the car answers.
    ///
    /// The proving matters. Every address in the file may be wrong, and a session that
    /// starts happily and shows a screen of dashes is a worse outcome than one that
    /// refuses to start and says why.
    pub fn connect(transport: Box<dyn EcuTransport>, parameters: Vec<SsmParameter>) -> Result<SsmSource> {
        if parameters.is_empty() {
            return Err(Error::Protocol(format!(
                "No SSM parameters are defined. Put addresses in {} \
                 under the definitions folder \u{2014} this ships the protocol, not the address map.",
                ssm_parameter_file::NAME
            )));
        }

        let transport = SharedTransport::new(transport);
        let mut link = transport.clone();
        link.open()?;

        let mut elm = Elm327::new(Box::new(transport.clone()));
        let reset = elm.reset(None)?;

        let mut adapter = if reset.is_empty() {
            String::new()
        } else {
            elm.identify()?
        };
        if adapter.is_empty() {
            adapter = reset;
        }

        if adapter.is_empty() {
            return Err(Error::Protocol("Nothing here answered as an OBD2 adapter.".to_string()));
        }

        // The protocol has to be found before anything is addressed, and the first
        // request after a reset is answered with "SEARCHING..." while the adapter works
        // through nine of them. Asked patiently, because giving up early aborts the
        // search and every request after it fails in a way that looks exactly like a car
        // with the key out.
        settle(&mut elm)?;

        let timeout = elm.timeout;
        for command in ssm::SETUP {
            elm.send(command, timeout, false, 0)?;
        }

        // One real read, so a session only starts when the car has actually spoken SSM.
        let reply = elm.send(&ssm::read_request(&[parameters[0].address])?, timeout, true, 0)?;

        if ssm::read_reply(Some(&reply), 1).is_empty() {
            return Err(Error::Protocol(if ssm::refused(Some(&reply)) {
                format!(
                    "The ECU understood the request and refused it. Address \
                     0x{:06X} is probably wrong for this vehicle.",
                    parameters[0].address
                )
            } else {
                "The vehicle did not answer an SSM request. It may not speak SSM over CAN, \
                 or the ignition may be off."
                    .to_string()
            }));
        }

        Ok(SsmSource::new(transport, elm, parameters, adapter))
    }

    /// What the adapter calls itself.
    pub fn adapter(&self) -> &str {
        &self.adapter
    }

    /// The parameters being read, in order.
    pub fn parameters(&self) -> &[SsmParameter] {
        &self.parameters
    }

    /// Parameters that have stopped answering and been dropped.
    pub fn dropped(&self) -> Vec<String> {
        self.parameters
            .iter()
            .zip(&self.silences)
            .filter(|(_, silent)| **silent >= GIVE_UP_AFTER)
            .map(|(p, _)| p.name.clone())
            .collect()
    }

    fn reopen(&mut self) -> Result<()> {
        self.elm.reset(None)?;
        settle(&mut self.elm)?;

        let timeout = self.elm.timeout;
        for command in ssm::SETUP {
            self.elm.send(command, timeout, false, 0)?;
        }

        Ok(())
    }

    fn poll(&mut self, index: usize) -> Result<bool> {
        // One address per request on the hardware this was proven against, so a
        // multi-byte value is read a byte at a time. The bytes are not sampled at quite
        // the same instant, which for a value that moves as fast as engine speed puts a
        // few rpm of noise on it and for a learnt value costs nothing at all.
        let mut raw = [0u8; 4];
        let mut got = 0;
        let timeout = self.elm.timeout;

        for address in self.parameters[index].addresses() {
            let reply = self.elm.send(&ssm::read_request(&[address])?, timeout, true, 0)?;
            let data = ssm::read_reply(Some(&reply), 1);

            if data.is_empty() || got >= raw.len() {
                break;
            }

            raw[got] = data[0];
            got += 1;
        }

        if got as i32 != self.parameters[index].bytes {
            self.silences[index] += 1;
            return Ok(false);
        }

        self.silences[index] = 0;
        self.values[index] = self.parameters[index].read(&raw[..got]);

        Ok(true)
    }
}

/// The first request after a reset starts the protocol search, which the adapter answers
/// with "SEARCHING..." for as long as it takes.
fn settle(elm: &mut Elm327) -> Result<()> {
    for _ in 0..4 {
        let reset_timeout = elm.reset_timeout;
        let reply = elm.send("0100", reset_timeout, true, 0)?;

        let upper = reply.to_uppercase();
        if upper.contains("41") && !upper.contains("SEARCHING") {
            return Ok(());
        }
    }

    Ok(())
}

impl LiveSource for SsmSource {
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
        if self.transport.is_open() {
            return Ok(());
        }

        self.transport.open()?;
        self.reopen()
    }

    /// Reads every parameter once.
    ///
    /// All of them every round, unlike the OBD2 source, which rotates. There is no
    /// rotation to do here: the list is one somebody chose deliberately rather than
    /// everything a car happens to support, so there is nothing in it that is not wanted.
    fn read(&mut self) -> Result<Vec<f64>> {
        let mut answered = false;
        let mut asked = false;

        for i in 0..self.parameters.len() {
            if self.silences[i] >= GIVE_UP_AFTER {
                continue;
            }

            asked = true;
            answered |= self.poll(i)?;
        }

        // Everything has been given up on. That is a dead link, not a healthy quiet one, and
        // returning the last values as if they were readings hid it. (The C# returned the
        // rows without an error.)
        if !asked && !self.parameters.is_empty() {
            self.retries += 1;

            return Err(Error::Protocol(
                "Every SSM parameter has stopped answering and been dropped, so there is nothing left to read. The link may have died; reconnecting asks for them again."
                    .to_string(),
            ));
        }

        if asked && !answered {
            self.retries += 1;

            return Err(Error::Protocol(
                "The ECU answered nothing this round. If the engine was switched off, \
                 most cars stop responding with the key out."
                    .to_string(),
            ));
        }

        Ok(self.values.clone())
    }

    fn recover(&mut self) -> Result<()> {
        // A link whose adapter has gone cannot be closed politely.
        self.transport.close();

        self.transport.open()?;
        self.reopen()?;

        // A fresh link is a fresh start: parameters given up on over the one that died are
        // asked for again, or a car switched back on would never be read.
        if self.dropped().len() == self.parameters.len() {
            self.silences.fill(0);
        }

        self.read()?;
        Ok(())
    }

    fn retries(&self) -> i32 {
        self.retries
    }
}

impl Drop for SsmSource {
    fn drop(&mut self) {
        // Put the adapter back to ordinary addressing, so whatever connects next is not
        // talking to the engine module by mistake.
        //
        // Best effort: the link may already have gone, and this is tidying. The first
        // failure ends it, as the C#'s single try block does.
        let timeout = self.elm.timeout;
        for command in ssm::RESTORE {
            if self.elm.send(command, timeout, false, 0).is_err() {
                break;
            }
        }
    }
}
