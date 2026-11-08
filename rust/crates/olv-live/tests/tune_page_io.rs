//! The seam between a tune and a connection: `olv_core::tune::ecu_tune::TunePageIo`, implemented
//! over the real `EcuConnection` and driven against the byte-level ECU double.
//!
//! `olv-core` cannot depend on `olv-live`, so the C# `EcuTune.Read(connection, ...)` takes a
//! small trait there. This proves the trait can be satisfied by `EcuConnection`'s own
//! signatures with nothing but delegation (the wrapper below is what `olv-live` will carry),
//! and that a tune read through it, edited, written back through it and burned lands in the
//! ECU's memory exactly as the edit says.

mod common;

use std::sync::{Arc, Mutex};

use common::{FakeTransport, Misbehaviour, ecu_model};
use olv_core::Error;
use olv_core::ini::tune_layout::{self, TunePage};
use olv_core::tune::ecu_tune::{EcuTune, TunePageIo};
use olv_core::tune::tune_settings_edit::TuneSettingsEdit;
use olv_live::connection::ecu_connection::{EcuConnection, EcuError};

/// What `olv-live` will implement: every method delegates to the connection of the same name.
struct Io<'a>(&'a EcuConnection);

impl From<EcuError> for Wrapped {
    fn from(e: EcuError) -> Self {
        Wrapped(e)
    }
}

/// An `EcuError` that can also be built from an `olv_core::Error`, which the trait asks for.
struct Wrapped(EcuError);

impl From<Error> for Wrapped {
    fn from(e: Error) -> Self {
        Wrapped(EcuError::from(e))
    }
}

impl std::fmt::Debug for Wrapped {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        write!(f, "{}", self.0)
    }
}

impl TunePageIo for Io<'_> {
    type Error = Wrapped;

    fn protocol_error(message: String) -> Wrapped {
        Wrapped(EcuError::protocol(message))
    }

    fn read_tune_page(
        &self,
        page: &TunePage,
        blocking_factor: i32,
        little_endian: bool,
        progress: Option<&mut dyn FnMut(i32)>,
    ) -> Result<Vec<u8>, Wrapped> {
        Ok(self.0.read_tune_page(page, blocking_factor, little_endian, progress)?)
    }

    fn write_tune_page(
        &self,
        page: &TunePage,
        blocking_factor: i32,
        little_endian: bool,
        offset: i32,
        data: &[u8],
        inter_write_delay: i32,
    ) -> Result<(), Wrapped> {
        Ok(self
            .0
            .write_tune_page(page, blocking_factor, little_endian, offset, data, inter_write_delay)?)
    }

    fn burn_page(&self, page: &TunePage, little_endian: bool, after_burn_delay: i32) -> Result<(), Wrapped> {
        Ok(self.0.burn_page(page, little_endian, after_burn_delay)?)
    }
}

const INI: &str = r#"
[Constants]
endianness      = little
nPages          = 1
pageSize        = 64
pageIdentifier  = "\x00\x01"
pageReadCommand = "R%2i%2o%2c"
pageChunkWrite  = "C%2i%2o%2c%v"
burnCommand     = "B%2i"
blockingFactor  = 16

page = 1
rpmHardLimit = scalar, U16, 0, "rpm", 1, 0, 0, 20000, 0
dwell        = scalar, U08, 2, "ms", 0.1, 0, 0, 12, 1
optA         = bits,   U08, 3, [0:0], "off", "on"
optB         = bits,   U08, 3, [1:1], "off", "on"
veTable      = array,  U08, 8, [4x3], "%", 0.5, 0, 0, 120, 1
"#;

fn memory() -> Arc<Mutex<Vec<u8>>> {
    let mut page = vec![0u8; 64];
    page[0..2].copy_from_slice(&5500u16.to_le_bytes());
    page[2] = 35;
    page[3] = 0b01;
    for (i, cell) in page[8..20].iter_mut().enumerate() {
        *cell = 20 + 10 * i as u8;
    }
    Arc::new(Mutex::new(page))
}

#[test]
fn a_tune_is_read_edited_written_and_burned_through_the_connection() {
    let layout = Arc::new(tune_layout::read(INI, None));
    let memory = memory();
    let transport = FakeTransport::responding(ecu_model(Arc::clone(&memory), true, Misbehaviour::None));
    let connection = EcuConnection::with_defaults(Box::new(transport.clone()));
    let io = Io(&connection);

    // Read, with progress reported in bytes.
    let mut progress: Vec<(i32, i32)> = Vec::new();
    let mut report = |done: i32, total: i32| progress.push((done, total));
    let mut tune = EcuTune::read(&io, Arc::clone(&layout), Some(&mut report)).expect("reads");

    assert_eq!(tune.pages()[0], *memory.lock().unwrap());
    assert_eq!(progress.last(), Some(&(64, 64)));
    assert_eq!(tune.scalar("rpmHardLimit"), 5500.0);

    // Edit two options that share a byte, and the dwell.
    let mut edit = TuneSettingsEdit::new(&tune);
    assert!(edit.set(&tune, "optB", 1.0, 0));
    assert!(edit.set(&tune, "dwell", 4.5, 0));

    let writes = edit.writes(&tune);
    assert_eq!(
        writes.len(),
        1,
        "the dwell and the shared byte are neighbours, so one run"
    );
    assert_eq!(writes[0].data.len(), 2);

    // Sent through the connection, then recorded once it is acknowledged and read back.
    for write in &writes {
        let page = &layout.pages[write.page as usize];
        io.write_tune_page(
            page,
            layout.blocking_factor,
            layout.little_endian,
            write.offset,
            &write.data,
            layout.inter_write_delay,
        )
        .expect("the ECU takes it");
        tune.accept(write);
        edit.accept(write);
    }

    io.burn_page(&layout.pages[0], layout.little_endian, layout.after_burn_delay)
        .expect("burns");

    edit.reconcile(&tune);
    assert!(!edit.has_changes());

    let held = memory.lock().unwrap();
    assert_eq!(held[2], 45, "4.5 ms at a tenth of a millisecond");
    assert_eq!(held[3], 0b11, "optA kept, optB set");
    assert_eq!(tune.pages()[0], *held, "the tune holds what the controller holds");
}

#[test]
fn a_definition_with_no_pages_is_a_protocol_error_from_the_connection_itself() {
    let layout = Arc::new(tune_layout::read("[Constants]\n", None));
    let transport = FakeTransport::new();
    let connection = EcuConnection::with_defaults(Box::new(transport));

    let Err(error) = EcuTune::read(&Io(&connection), layout, None) else {
        panic!("a layout without pages cannot be read");
    };

    assert!(error.0.is_protocol());
    assert_eq!(
        error.0.message(),
        "This firmware definition declares no settings pages."
    );
}
