//! A MaxxECU in software (the oracle's `ModelEcu`), and the tables the oracle composes.

use std::collections::VecDeque;
use std::sync::{Arc, Mutex};
use std::time::Duration;

use olv_core::Result;
use olv_live::live::{maxx_can, maxx_tune};
use olv_protocol::ms::maxx_protocol;
use olv_protocol::ms::maxx_usb_protocol as usb;
use olv_protocol::ms::ms_protocol::EcuTransport;

pub struct ModelState {
    reply: Vec<u8>,
    expecting: Option<(usize, usize)>,
    ring: VecDeque<Vec<u8>>,
    pub tune: Vec<u8>,
    pub header_status: u8,
    pub answer_header: bool,
    pub answer_payload: bool,
    /// Requests to leave unanswered before answering normally.
    pub silent_replies: i32,
    pub drop_counter: u16,
    pub written: Vec<Vec<u8>>,
    open: bool,
}

/// A MaxxECU in software: a 64 KB tune, a CAN ring, a drop counter, and a way to misbehave. The
/// same model the C# oracle runs (`W4Live.cs`'s `ModelEcu`); a cloneable handle, so the tune code
/// owns one clone and the test keeps another.
#[derive(Clone)]
pub struct ModelEcu(pub Arc<Mutex<ModelState>>);

pub fn pattern() -> Vec<u8> {
    (0..maxx_tune::BLOB_SIZE).map(|i| ((i * 7) + (i >> 8)) as u8).collect()
}

pub fn answer(data: &[u8]) -> Vec<u8> {
    let mut reply = vec![usb::OK];
    reply.extend_from_slice(data);
    reply.extend_from_slice(&maxx_protocol::crc32(&reply).to_le_bytes());
    reply
}

pub fn hex(bytes: &[u8]) -> String {
    bytes.iter().map(|b| format!("{b:02X}")).collect()
}

impl ModelEcu {
    pub fn new() -> Self {
        Self(Arc::new(Mutex::new(ModelState {
            reply: Vec::new(),
            expecting: None,
            ring: VecDeque::new(),
            tune: pattern(),
            header_status: usb::OK,
            answer_header: true,
            answer_payload: true,
            silent_replies: 0,
            drop_counter: 0x1234,
            written: Vec::new(),
            open: false,
        })))
    }

    pub fn with<R>(&self, f: impl FnOnce(&mut ModelState) -> R) -> R {
        f(&mut self.0.lock().unwrap())
    }

    pub fn enqueue(&self, record: Vec<u8>) {
        self.with(|s| s.ring.push_back(record));
    }

    pub fn written_hex(&self) -> Vec<String> {
        self.with(|s| s.written.iter().map(|w| hex(w)).collect())
    }

    pub fn tune_crc(&self) -> String {
        self.with(|s| format!("{:08X}", maxx_protocol::crc32(&s.tune)))
    }
}

impl ModelState {
    fn checksum_bytes(&self, covers: usize, length: usize) -> Vec<u8> {
        let mut bytes = vec![0u8; length];
        let sums = maxx_tune::checksums_of(&self.tune, covers as i32).unwrap();

        for (i, sum) in sums.iter().enumerate() {
            if (i * 4) + 4 > length {
                break;
            }
            bytes[i * 4..i * 4 + 4].copy_from_slice(&sum.to_le_bytes());
        }

        bytes
    }

    fn ring(&mut self, length: usize) -> Vec<u8> {
        let mut bytes = vec![0u8; length];

        let mut at = 0;
        while at + maxx_can::RECORD_LENGTH <= length {
            let Some(record) = self.ring.pop_front() else { break };
            bytes[at..at + maxx_can::RECORD_LENGTH].copy_from_slice(&record);
            at += maxx_can::RECORD_LENGTH;
        }

        bytes
    }

    fn snapshot(&self, offset: usize, length: usize) -> Vec<u8> {
        let mut window: Vec<u8> = (0..maxx_can::SNAPSHOT_WINDOW as usize).map(|i| (i * 3) as u8).collect();
        let at = maxx_can::DROP_COUNT_AT as usize;
        window[at..at + 2].copy_from_slice(&self.drop_counter.to_le_bytes());

        window[offset..offset + length].to_vec()
    }
}

impl EcuTransport for ModelEcu {
    fn is_open(&self) -> bool {
        self.with(|s| s.open)
    }

    fn open(&mut self) -> Result<()> {
        self.with(|s| s.open = true);
        Ok(())
    }

    fn close(&mut self) {
        self.with(|s| s.open = false);
    }

    fn write(&mut self, data: &[u8]) -> Result<()> {
        self.with(|s| {
            s.written.push(data.to_vec());

            if let Some((offset, length)) = s.expecting.take() {
                let sent = u32::from_le_bytes(data[length..length + 4].try_into().unwrap());

                if sent == maxx_protocol::crc32(&data[..length]) {
                    s.tune[offset..offset + length].copy_from_slice(&data[..length]);
                    s.reply = if s.answer_payload { vec![usb::OK] } else { Vec::new() };
                } else {
                    s.reply = vec![maxx_tune::MaxxWriteStatus::OutOfRange.value()];
                }
                return;
            }

            let command = data[1];
            let offset = usize::from(data[2]) | (usize::from(data[3]) << 8);
            let length = usize::from(data[4]) | (usize::from(data[5]) << 8);

            if s.silent_replies > 0 {
                s.silent_replies -= 1;
                s.reply = Vec::new();
                return;
            }

            if data[0] == usb::WRITE {
                if !s.answer_header {
                    s.reply = Vec::new();
                    return;
                }
                if s.header_status != usb::OK {
                    s.reply = vec![s.header_status];
                    return;
                }

                s.expecting = Some((offset, length));
                s.reply = vec![usb::OK];
                return;
            }

            let body = match command {
                maxx_tune::READ_TUNE => s.tune[offset..offset + length].to_vec(),
                0x37 => s.checksum_bytes(offset, length),
                maxx_can::TAKE => s.ring(length),
                maxx_can::SNAPSHOT => s.snapshot(offset, length),
                _ => vec![0; length],
            };

            s.reply = answer(&body);
        });

        Ok(())
    }

    fn read(&mut self, buffer: &mut [u8], _timeout: Duration) -> Result<usize> {
        Ok(self.with(|s| {
            let taken = buffer.len().min(s.reply.len());
            buffer[..taken].copy_from_slice(&s.reply[..taken]);
            s.reply.drain(..taken);
            taken
        }))
    }

    fn discard_input(&mut self) {
        self.with(|s| s.reply.clear());
    }
}

/// A model ECU that loses the replies to some requests (by count) and counts discards.
pub struct LossyEcu {
    pub inner: ModelEcu,
    lose: Vec<usize>,
    requests: usize,
    pub discards: Arc<Mutex<i32>>,
}

impl LossyEcu {
    pub fn new(lose: &[usize]) -> Self {
        Self {
            inner: ModelEcu::new(),
            lose: lose.to_vec(),
            requests: 0,
            discards: Arc::new(Mutex::new(0)),
        }
    }
}

impl EcuTransport for LossyEcu {
    fn is_open(&self) -> bool {
        self.inner.is_open()
    }

    fn open(&mut self) -> Result<()> {
        self.inner.open()
    }

    fn close(&mut self) {
        self.inner.close();
    }

    fn write(&mut self, data: &[u8]) -> Result<()> {
        self.inner.write(data)?;
        if self.lose.contains(&self.requests) {
            self.inner.discard_input();
        }
        self.requests += 1;
        Ok(())
    }

    fn read(&mut self, buffer: &mut [u8], timeout: Duration) -> Result<usize> {
        self.inner.read(buffer, timeout)
    }

    fn discard_input(&mut self) {
        *self.discards.lock().unwrap() += 1;
        self.inner.discard_input();
    }
}

// ----- tables, composed the way the oracle composes them -----------------------------

/// Lays a table into a blob the way a MaxxECU does (the oracle's `W4.Compose`: a pad byte of 0x5A
/// in front of the axes, and nine bytes between the axes and the cells).
#[allow(clippy::too_many_arguments)]
pub fn compose(
    blob: &mut [u8],
    config: usize,
    data_offset: usize,
    x_channel: u16,
    y_channel: u16,
    x: &[i16],
    y: &[i16],
    cell: &dyn Fn(usize, usize) -> i16,
) {
    blob[config..config + 2].copy_from_slice(&x_channel.to_le_bytes());
    blob[config + 0x02..config + 0x04].copy_from_slice(&y_channel.to_le_bytes());
    blob[config + 0x06] = x.len() as u8;
    blob[config + 0x07] = y.len() as u8;
    blob[config + 0x08] = 1;
    blob[config + 0x0B..config + 0x0D].copy_from_slice(&(data_offset as u16).to_le_bytes());

    let mut cursor = maxx_tune::TABLE_DATA + data_offset;

    blob[cursor] = 0x5A;
    cursor += 1;

    for value in x.iter().chain(y) {
        blob[cursor..cursor + 2].copy_from_slice(&value.to_le_bytes());
        cursor += 2;
    }

    cursor += 9;

    for row in 0..y.len() {
        for column in 0..x.len() {
            blob[cursor..cursor + 2].copy_from_slice(&cell(row, column).to_le_bytes());
            cursor += 2;
        }
    }
}

pub const RPM: [i16; 16] = [
    200, 800, 1000, 1200, 1600, 2000, 2500, 3000, 3500, 4000, 4500, 5000, 5500, 6000, 6500, 7000,
];
pub const KPA: [i16; 11] = [200, 400, 503, 600, 800, 999, 1199, 1399, 1599, 1799, 1999];

pub fn with_ve_table(config: usize, data_offset: usize) -> Vec<u8> {
    let mut blob = vec![0u8; maxx_tune::BLOB_SIZE];
    compose(&mut blob, config, data_offset, 61, 20, &RPM, &KPA, &|row, column| {
        (280 + (row * 80) + (column * 2)) as i16
    });
    blob
}

pub fn with_small_table(config: usize, data_offset: usize) -> Vec<u8> {
    let mut blob = vec![0u8; maxx_tune::BLOB_SIZE];
    let axis = [10i16, 20, 30, 40, 50, 60];
    compose(&mut blob, config, data_offset, 61, 20, &axis, &axis, &|row, column| {
        ((row * 100) + column) as i16
    });
    blob
}
