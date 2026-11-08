//! Test doubles shared by the agent tests: a controller on the other end of a transport, a host,
//! and a writer wired to both. The C# tests build the same thing out of `ViewModelHarness` and
//! `FakeController`; the view model is not ported yet, so the writer, the only thing that can
//! send an agent's bytes, is connected to the fake directly.

#![allow(dead_code)]

use std::path::PathBuf;
use std::sync::atomic::{AtomicU32, Ordering};
use std::sync::{Arc, Mutex, MutexGuard, PoisonError};
use std::time::Duration;

use olv_agent::agent::agent_writer::{AgentWriter, EcuTarget, TuneInHand, WriterHost};
use olv_core::Result;
use olv_core::ini::{table_editor_reader, tune_layout};
use olv_core::model::WireTrace;
use olv_core::tune::ecu_tune::EcuTune;
use olv_live::connection::ecu_connection::{EcuConnection, EcuConnectionSettings};
use olv_protocol::ms::ms_protocol::{self, EcuTransport};
use olv_store::store::workspace::Workspace;

pub const SIGNATURE: &str = "TEST Format 0001.00";

/// The firmware the C# agent tests use, plus a second table whose constant is a dangerous one.
pub const FIRMWARE: &str = r#"
[MegaTune]
   signature = "TEST Format 0001.00"

[Constants]
page = 1
nPages = 1
pageSize = 32
pageIdentifier = "\$tsCanId\x01"
pageReadCommand = "r%2i%2o%2c"
pageChunkWrite  = "w%2i%2o%2c%v"
burnCommand     = "b%2i"
   crankingRPM = scalar, U16, 0, "rpm", 1, 0, 0, 10000, 0
   revLimit    = scalar, U16, 2, "rpm", 1, 0, 0, 10000, 0
   vehicleName = string, ASCII, 4, 12
   veTable     = array,  U08, 16, [2x2], "%", 1, 0, 0, 255, 0
   rpmBins     = array,  U08, 20, [2],   "rpm", 100, 0, 0, 25500, 0
   mapBins     = array,  U08, 22, [2],   "kPa", 1, 0, 0, 255, 0
   launchTable = array,  U08, 24, [2x2], "%", 1, 0, 0, 255, 0

[OutputChannels]
ochBlockSize = 8
ochGetCommand = "r\x00\x07%2o%2c"
   rpm  = scalar, U16, 0, "rpm", 1, 0
   clt  = scalar, U16, 2, "deg C", 1, 0

[Datalog]
   entry = rpm, "RPM", int, "%d"
   entry = clt, "CLT", int, "%d"

[TableEditor]
   table = veTableTbl, veTableMap, "VE Table", 1
      xBins = rpmBins, rpm
      yBins = mapBins, clt
      zBins = veTable
   table = launchTbl, launchMap, "Launch Table", 1
      xBins = rpmBins, rpm
      yBins = mapBins, clt
      zBins = launchTable
"#;

fn lock<T>(m: &Mutex<T>) -> MutexGuard<'_, T> {
    m.lock().unwrap_or_else(PoisonError::into_inner)
}

struct BoardState {
    pending: Vec<u8>,
    open: bool,
    page: Vec<u8>,
    burns: usize,
    flash: Option<Vec<u8>>,
    /// Every chunk write that reached it: offset and count.
    writes: Vec<(usize, usize)>,
}

/// A controller answering the MegaSquirt page protocol well enough to be written to and read
/// back (the C# `FakeController`). Cloneable: the connection owns one handle, the test another.
#[derive(Clone)]
pub struct Board(Arc<Mutex<BoardState>>);

impl Board {
    pub fn new() -> Self {
        let mut page = vec![0u8; 32];
        // Something other than noughts, so a tune read off it is distinguishable from the
        // placeholder a definition alone produces.
        page[0] = 0x01;
        page[1] = 0x2C; // crankingRPM = 300
        page[2] = 0x19;
        page[3] = 0x64; // revLimit = 6500

        Self(Arc::new(Mutex::new(BoardState {
            pending: Vec::new(),
            open: false,
            page,
            burns: 0,
            flash: None,
            writes: Vec::new(),
        })))
    }

    pub fn page(&self) -> Vec<u8> {
        lock(&self.0).page.clone()
    }

    pub fn burns(&self) -> usize {
        lock(&self.0).burns
    }

    pub fn flash(&self) -> Option<Vec<u8>> {
        lock(&self.0).flash.clone()
    }

    pub fn writes(&self) -> Vec<(usize, usize)> {
        lock(&self.0).writes.clone()
    }
}

fn payload_of(data: &[u8]) -> Vec<u8> {
    if data.len() < 7 {
        return Vec::new();
    }

    let length = (usize::from(data[0]) << 8) | usize::from(data[1]);
    data.get(2..2 + length).map(<[u8]>::to_vec).unwrap_or_default()
}

fn reply(status: u8, data: &[u8]) -> Vec<u8> {
    let mut body = vec![status];
    body.extend_from_slice(data);
    ms_protocol::frame(&body)
}

impl BoardState {
    fn answer(&mut self, payload: &[u8]) -> Vec<u8> {
        let refusal = |status: u8| reply(status, &[]);

        match payload[0] as char {
            'Q' | 'S' | 'V' => reply(0, SIGNATURE.as_bytes()),

            // r <canid> <page> <offset:2 BE> <count:2 BE>
            'r' => {
                if payload.len() < 7 {
                    return refusal(0x80);
                }

                let offset = (usize::from(payload[3]) << 8) | usize::from(payload[4]);
                let count = (usize::from(payload[5]) << 8) | usize::from(payload[6]);

                if count < 1 || offset + count > self.page.len() {
                    return refusal(0x84);
                }

                reply(0, &self.page[offset..offset + count])
            }

            // w <canid> <page> <offset:2 BE> <count:2 BE> <data>
            'w' => {
                if payload.len() < 7 {
                    return refusal(0x80);
                }

                let offset = (usize::from(payload[3]) << 8) | usize::from(payload[4]);
                let count = (usize::from(payload[5]) << 8) | usize::from(payload[6]);

                if offset + count > self.page.len() || payload.len() < 7 + count {
                    return refusal(0x84);
                }

                self.writes.push((offset, count));
                self.page[offset..offset + count].copy_from_slice(&payload[7..7 + count]);

                reply(0, &[])
            }

            'b' | 'B' => {
                self.burns += 1;
                self.flash = Some(self.page.clone());
                reply(0x04, &[])
            }

            _ => refusal(0x83),
        }
    }
}

impl EcuTransport for Board {
    fn is_open(&self) -> bool {
        lock(&self.0).open
    }

    fn open(&mut self) -> Result<()> {
        lock(&self.0).open = true;
        Ok(())
    }

    fn close(&mut self) {
        lock(&self.0).open = false;
    }

    fn write(&mut self, data: &[u8]) -> Result<()> {
        let payload = payload_of(data);
        let mut state = lock(&self.0);
        state.pending = if payload.is_empty() {
            Vec::new()
        } else {
            state.answer(&payload)
        };
        Ok(())
    }

    fn read(&mut self, buffer: &mut [u8], _timeout: Duration) -> Result<usize> {
        let mut state = lock(&self.0);
        let take = buffer.len().min(state.pending.len());
        buffer[..take].copy_from_slice(&state.pending[..take]);
        state.pending.drain(..take);
        Ok(take)
    }

    fn discard_input(&mut self) {}
}

/// The application, as far as the writer needs it: a live RPM that a test can set, and a record
/// of the versions kept.
#[derive(Default)]
pub struct Host {
    pub rpm: Mutex<Option<f64>>,
    pub kept: Mutex<Vec<String>>,
}

impl Host {
    pub fn set_rpm(&self, rpm: Option<f64>) {
        *lock(&self.rpm) = rpm;
    }

    pub fn kept(&self) -> Vec<String> {
        lock(&self.kept).clone()
    }
}

impl WriterHost for Host {
    fn live_rpm(&self) -> Option<f64> {
        *lock(&self.rpm)
    }

    fn keep_tune(&self, note: &str) {
        lock(&self.kept).push(note.to_string());
    }
}

/// A scratch folder that goes away with the test.
pub struct Scratch(pub PathBuf);

impl Scratch {
    pub fn new(what: &str) -> Self {
        static NEXT: AtomicU32 = AtomicU32::new(0);
        let dir = std::env::temp_dir().join(format!(
            "olv-agent-{what}-{}-{}",
            std::process::id(),
            NEXT.fetch_add(1, Ordering::SeqCst)
        ));
        let _ = std::fs::remove_dir_all(&dir);
        Self(dir)
    }
}

impl Drop for Scratch {
    fn drop(&mut self) {
        let _ = std::fs::remove_dir_all(&self.0);
    }
}

/// A writer connected to a fake controller, not armed.
pub struct Rig {
    pub writer: Arc<AgentWriter>,
    pub board: Board,
    pub host: Arc<Host>,
    pub workspace: Workspace,
    pub connection: Arc<EcuConnection>,
    pub trace: Arc<WireTrace>,
    _scratch: Scratch,
}

pub fn tune_of(board: &Board) -> EcuTune {
    let layout = tune_layout::read(FIRMWARE, None);
    EcuTune::from_pages(layout, vec![board.page()])
}

pub fn tune_in_hand(board: &Board) -> TuneInHand {
    TuneInHand {
        tune: tune_of(board),
        definitions: table_editor_reader::read(FIRMWARE, None),
        signature: SIGNATURE.to_string(),
        placeholder: false,
        from_file: false,
    }
}

pub fn rig(what: &str) -> Rig {
    let scratch = Scratch::new(what);
    let workspace = Workspace::new(Some(&scratch.0));
    let board = Board::new();
    let host = Arc::new(Host::default());

    let trace = Arc::new(WireTrace::new(200));
    let connection = Arc::new(
        EcuConnection::new(
            Box::new(board.clone()),
            EcuConnectionSettings {
                retries: 1,
                timeout: Duration::from_millis(50),
                retry_pause: Duration::ZERO,
                ..EcuConnectionSettings::default()
            },
        )
        .with_trace(Arc::clone(&trace)),
    );
    connection.open().expect("the fake opens");

    let writer = Arc::new(AgentWriter::new(workspace.clone(), host.clone()));
    writer.connect(EcuTarget::Pages(Arc::clone(&connection)));
    writer.set_tune(Some(tune_in_hand(&board)));

    Rig {
        writer,
        board,
        host,
        workspace,
        connection,
        trace,
        _scratch: scratch,
    }
}

/// The same, armed for writing, as the C# tests' `Connected`.
pub fn armed_rig(what: &str) -> Rig {
    let rig = rig(what);
    rig.writer.set_armed(true);
    rig
}

pub mod bench;
pub mod http;

pub mod writer_bridge;
