//! A MaxxECU on the far end of a USB cable, well enough to be written to and read back: the header
//! acknowledged on its own, then the payload with its checksum, and a read of the tune to verify.
//! After `FakeEcu` in `olv-live`'s MaxxTune tests. A cloneable handle, so the test keeps one.

#![allow(dead_code)]

use std::sync::{Arc, Mutex};
use std::time::Duration;

use olv_core::Result;
use olv_live::live::maxx_tune;
use olv_protocol::ms::maxx_protocol;
use olv_protocol::ms::maxx_usb_protocol as usb;
use olv_protocol::ms::ms_protocol::EcuTransport;

struct State {
    reply: Vec<u8>,
    tune: Vec<u8>,
    expecting: Option<(usize, usize)>,
    /// Every accepted payload: offset and length.
    writes: Vec<(usize, usize)>,
    sent: Vec<Vec<u8>>,
    /// Once this many payloads have been accepted, refuse the next `refusals_left` of them.
    refuse_after: usize,
    refusals_left: usize,
}

#[derive(Clone)]
pub struct FakeMaxx(Arc<Mutex<State>>);

impl FakeMaxx {
    pub fn holding(tune: Vec<u8>) -> Self {
        Self(Arc::new(Mutex::new(State {
            reply: Vec::new(),
            tune,
            expecting: None,
            writes: Vec::new(),
            sent: Vec::new(),
            refuse_after: usize::MAX,
            refusals_left: 0,
        })))
    }

    /// After `accepted` payloads have landed, answer the next `times` with a refusal.
    pub fn refuse_payloads_after(&self, accepted: usize, times: usize) {
        let mut s = self.0.lock().unwrap();
        s.refuse_after = accepted;
        s.refusals_left = times;
    }

    pub fn tune(&self) -> Vec<u8> {
        self.0.lock().unwrap().tune.clone()
    }

    pub fn writes(&self) -> Vec<(usize, usize)> {
        self.0.lock().unwrap().writes.clone()
    }

    pub fn frames_sent(&self) -> usize {
        self.0.lock().unwrap().sent.len()
    }
}

impl EcuTransport for FakeMaxx {
    fn is_open(&self) -> bool {
        true
    }

    fn open(&mut self) -> Result<()> {
        Ok(())
    }

    fn close(&mut self) {}

    fn write(&mut self, data: &[u8]) -> Result<()> {
        let mut s = self.0.lock().unwrap();
        s.sent.push(data.to_vec());

        if let Some((offset, length)) = s.expecting.take() {
            if s.writes.len() >= s.refuse_after && s.refusals_left > 0 {
                s.refusals_left -= 1;
                s.reply = vec![0x30];
                return Ok(());
            }

            let sent = u32::from_le_bytes(data[length..length + 4].try_into().unwrap());

            if sent == maxx_protocol::crc32(&data[..length]) {
                s.tune[offset..offset + length].copy_from_slice(&data[..length]);
                s.writes.push((offset, length));
                s.reply = vec![usb::OK];
            } else {
                s.reply = vec![0x30];
            }
            return Ok(());
        }

        let offset = usize::from(data[2]) | (usize::from(data[3]) << 8);
        let length = usize::from(data[4]) | (usize::from(data[5]) << 8);

        if data[0] == usb::WRITE {
            s.expecting = Some((offset, length));
            s.reply = vec![usb::OK];
            return Ok(());
        }

        // A read of the tune, which is how a write is checked.
        let mut reply = vec![usb::OK];
        reply.extend_from_slice(&s.tune[offset..offset + length]);
        reply.extend_from_slice(&maxx_protocol::crc32(&reply).to_le_bytes());
        s.reply = reply;

        Ok(())
    }

    fn read(&mut self, buffer: &mut [u8], _timeout: Duration) -> Result<usize> {
        let mut s = self.0.lock().unwrap();
        let taken = buffer.len().min(s.reply.len());
        buffer[..taken].copy_from_slice(&s.reply[..taken]);
        s.reply.drain(..taken);
        Ok(taken)
    }

    fn discard_input(&mut self) {
        self.0.lock().unwrap().reply.clear();
    }
}

// ----- a tune with a VE table in it ----------------------------------------------------------

const RPM: [i16; 16] = [
    200, 800, 1000, 1200, 1600, 2000, 2500, 3000, 3500, 4000, 4500, 5000, 5500, 6000, 6500, 7000,
];
const KPA: [i16; 11] = [200, 400, 503, 600, 800, 999, 1199, 1399, 1599, 1799, 1999];

/// Where the table's config struct is, and where its data begins after `TABLE_DATA`.
pub const CONFIG: usize = 1578;
pub const DATA_OFFSET: usize = 496;

/// A VE table 16 wide and 11 deep laid into a blob the way a MaxxECU does (after the composer in
/// `olv-live`'s tests): config struct, axes, nine bytes that are not the grid, then the cells.
pub fn blob_with_ve_table() -> Vec<u8> {
    let mut blob = vec![0u8; maxx_tune::BLOB_SIZE];

    blob[CONFIG..CONFIG + 2].copy_from_slice(&61u16.to_le_bytes());
    blob[CONFIG + 0x02..CONFIG + 0x04].copy_from_slice(&20u16.to_le_bytes());
    blob[CONFIG + 0x06] = RPM.len() as u8;
    blob[CONFIG + 0x07] = KPA.len() as u8;
    blob[CONFIG + 0x08] = 1;
    blob[CONFIG + 0x0B..CONFIG + 0x0D].copy_from_slice(&(DATA_OFFSET as u16).to_le_bytes());

    let at = maxx_tune::TABLE_DATA + DATA_OFFSET;
    blob[at] = 0;
    let mut cursor = at + 1;

    for value in RPM.iter().chain(&KPA) {
        blob[cursor..cursor + 2].copy_from_slice(&value.to_le_bytes());
        cursor += 2;
    }

    cursor += 9;

    for row in 0..KPA.len() {
        for column in 0..RPM.len() {
            let cell = (280 + row * 80 + column * 2) as i16;
            blob[cursor..cursor + 2].copy_from_slice(&cell.to_le_bytes());
            cursor += 2;
        }
    }

    blob
}
