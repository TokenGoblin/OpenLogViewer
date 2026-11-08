//! Ported from `EcuWriteTests.cs`: encoding values back into the bytes an array occupies, and
//! the write and burn path.
//!
//! The C# drives `EcuConnection` against fake transports. `EcuConnection` lives in `olv-live`,
//! which `olv-core` cannot depend on (it would be a cycle), so the 22 tests that exercise the
//! connection itself live in `olv-live/tests/tune_write.rs`, under the same names:
//!
//! what_is_written_can_be_read_back, a_write_that_lands_in_the_wrong_place_is_caught,
//! a_write_that_is_acknowledged_and_discarded_is_caught, the_failure_says_nothing_has_been_burned,
//! a_write_is_split_to_fit_the_blocking_factor,
//! a_write_running_past_the_end_of_the_page_is_refused_before_it_is_sent, writing_does_not_burn,
//! burning_is_its_own_call, a_firmware_declaring_no_write_command_cannot_be_written_to,
//! a_reply_of_the_wrong_length_is_not_taken_as_the_answer,
//! the_link_is_drained_before_anything_is_sent_again, a_write_is_the_bytes_the_firmware_declares,
//! a_burn_is_the_bytes_the_firmware_declares,
//! a_write_larger_than_the_blocking_factor_is_split_and_still_lands, and the EcuBurnReplyTests:
//! every_way_of_saying_yes_is_taken_for_a_yes, and_every_way_of_saying_no_still_means_no,
//! a_port_that_will_not_take_the_bytes_is_reported_rather_than_thrown_raw,
//! a_burn_that_cannot_be_confirmed_says_it_may_still_have_happened, a_burn_is_never_sent_twice,
//! a_burn_is_given_longer_than_everything_else_is,
//! and_the_long_allowance_is_put_back_afterwards, a_refusal_is_left_to_speak_for_itself.
//!
//! Everything about the *tune* side is ported here and runs.

use olv_core::Error;
use olv_core::ini::tune_layout::{self, TuneLayout, TunePage};
use olv_core::model::{Array2, RealtimeCommand};
use olv_core::tune::ecu_tune::{EcuTune, TunePageIo, TuneWrite};
use std::cell::RefCell;
use std::sync::Arc;

const INI: &str = r#"[Constants]
endianness      = little
nPages          = 1
pageSize        = 256
pageIdentifier  = "\x00\x00"
pageReadCommand = "R%2o%2c"
pageChunkWrite  = "C%2o%2c%v"
burnCommand     = "B"
blockingFactor  = 64

page = 1
veTable    = array, U16, 0,  [4x2], "%", 0.1, 0, 0, 999, 1
veRpmBins  = array, U16, 16, [4],   "RPM", 1, 0, 0, 18000, 0
veLoadBins = array, U16, 24, [2],   "kPa", 1, 0, 0, 255, 0
idleTrim   = scalar, S16, 40, "%", 0.1, 0, -100, 100, 1
"#;

fn layout() -> TuneLayout {
    tune_layout::read(INI, None)
}

fn page() -> TunePage {
    layout().pages[0].clone()
}

fn u16_at(data: &[u8], at: usize) -> u16 {
    u16::from_le_bytes([data[at], data[at + 1]])
}

/// A board holding one page in memory, behind the same seam the connection fills in `olv-live`.
/// It takes a write exactly as asked and answers a read from what it holds; the connection's
/// own read-back verification is not modelled (see the module docs).
struct Board {
    page: RefCell<Vec<u8>>,
}

impl TunePageIo for Board {
    type Error = Error;

    fn protocol_error(message: String) -> Error {
        Error::Protocol(message)
    }

    fn read_tune_page(
        &self,
        page: &TunePage,
        _blocking_factor: i32,
        _little_endian: bool,
        progress: Option<&mut dyn FnMut(i32)>,
    ) -> Result<Vec<u8>, Error> {
        let held = self.page.borrow();
        let image = held[..page.size as usize].to_vec();
        if let Some(progress) = progress {
            progress(page.size);
        }
        Ok(image)
    }

    fn write_tune_page(
        &self,
        _page: &TunePage,
        _blocking_factor: i32,
        _little_endian: bool,
        offset: i32,
        data: &[u8],
        _inter_write_delay: i32,
    ) -> Result<(), Error> {
        let at = offset as usize;
        self.page.borrow_mut()[at..at + data.len()].copy_from_slice(data);
        Ok(())
    }

    fn burn_page(&self, _page: &TunePage, _little_endian: bool, _after_burn_delay: i32) -> Result<(), Error> {
        Ok(())
    }
}

fn blank_tune() -> EcuTune {
    EcuTune::from_pages(layout(), vec![vec![0u8; 256]])
}

// ----- encoding ---------------------------------------------------------

#[test]
fn values_are_scaled_back_into_bytes() {
    let tune = blank_tune();

    let write = tune
        .encode_array("veTable", &[10.0, 20.0, 30.0, 40.0, 50.0, 60.0, 70.0, 80.0])
        .expect("a write");

    assert_eq!(write.page, 0);
    assert_eq!(write.offset, 0);
    assert_eq!(write.data.len(), 16);
    assert_eq!(u16_at(&write.data, 0), 100); // 10 % at a scale of 0.1
    assert_eq!(u16_at(&write.data, 14), 800);
}

#[test]
fn scaling_rounds_rather_than_truncates() {
    // 84.7 over a scale of 0.1 is 846.99999... in binary floating point.
    // Truncating drops a tenth off every cell of a table.
    let tune = blank_tune();

    let write = tune.encode_array("idleTrim", &[84.7]).expect("a write");

    assert_eq!(i16::from_le_bytes([write.data[0], write.data[1]]), 847);
}

#[test]
fn a_value_the_type_cannot_hold_is_refused() {
    // Wrapping it into whatever it becomes is the worst available response.
    let tune = blank_tune();

    assert!(
        tune.encode_array("veTable", &[10.0, 20.0, 30.0, 40.0, 50.0, 60.0, 70.0, 99999.0])
            .is_none()
    );
}

#[test]
fn the_wrong_number_of_values_is_refused() {
    let tune = blank_tune();

    assert!(tune.encode_array("veTable", &[1.0, 2.0, 3.0]).is_none());
}

#[test]
fn a_table_encodes_row_major_the_way_the_page_holds_it() {
    let tune = blank_tune();

    let mut cells: Array2<f64> = Array2::new(4, 2);
    for column in 0..4 {
        for row in 0..2 {
            cells[(column, row)] = ((row * 4 + column + 1) * 10) as f64;
        }
    }

    let write = tune.encode_table("veTable", &cells).expect("a write");

    assert_eq!(u16_at(&write.data, 0), 100); // row 0, column 0
    assert_eq!(u16_at(&write.data, 6), 400); // row 0, column 3
    assert_eq!(u16_at(&write.data, 8), 500); // row 1, column 0
}

// ----- the command ------------------------------------------------------

#[test]
fn the_write_command_carries_its_payload() {
    let request = RealtimeCommand::parse("C%2o%2c%v")
        .build(16, 3, 0, true, &[], &[0xAA, 0xBB, 0xCC])
        .unwrap();

    assert_eq!(request, vec![b'C', 16, 0, 3, 0, 0xAA, 0xBB, 0xCC]);
}

#[test]
fn a_megasquirt_write_names_its_page_too() {
    let identifier = RealtimeCommand::parse(r"\$tsCanId\x06")
        .build(0, 1, 1, false, &[], &[])
        .unwrap();

    let request = RealtimeCommand::parse("w%2i%2o%2c%v")
        .build(2, 2, 1, false, &identifier, &[0x12, 0x34])
        .unwrap();

    assert_eq!(request, vec![b'w', 1, 0x06, 0, 2, 0, 2, 0x12, 0x34]);
}

#[test]
fn the_write_and_burn_commands_are_read_from_the_ini() {
    let page = page();

    assert_eq!(page.chunk_write_command, "C%2o%2c%v");
    assert_eq!(page.burn_command, "B");
}

/// End to end: change a cell, write it, read the tune back.
///
/// Through the in-memory [`Board`] rather than `EcuConnection` + `WritableEcu`: it proves the
/// tune's encode, the `TunePageIo` write and `EcuTune::read` agree, not the wire.
#[test]
fn a_table_change_survives_a_write_and_a_reread() {
    let board = Board {
        page: RefCell::new(vec![0u8; 256]),
    };

    let layout = Arc::new(layout());
    let before = EcuTune::read(&board, Arc::clone(&layout), None).unwrap();

    let mut cells: Array2<f64> = Array2::new(4, 2);
    for column in 0..4 {
        for row in 0..2 {
            cells[(column, row)] = (50 + column + row * 10) as f64;
        }
    }

    let write: TuneWrite = before.encode_table("veTable", &cells).expect("a write");
    board
        .write_tune_page(
            &layout.pages[write.page as usize],
            layout.blocking_factor,
            layout.little_endian,
            write.offset,
            &write.data,
            0,
        )
        .unwrap();

    let after = EcuTune::read(&board, layout, None).unwrap();
    let read = after.array("veTable").expect("an array");

    assert_eq!(read, vec![50.0, 51.0, 52.0, 53.0, 60.0, 61.0, 62.0, 63.0]);
}

// ----- the same method TunerStudio uses -----------------------------------

/// MS2Extra's own declarations, copied from the file the MicroSquirt matched.
/// Big-endian, the CAN id and the page byte as literals in every command, and
/// the two timings the firmware asks for.
const MEGASQUIRT: &str = r#"[Constants]
endianness          = big
nPages              = 1
pageSize            = 1024
pageIdentifier      = "\$tsCanId"
pageReadCommand     = "r\$tsCanId%2o%2c"
pageChunkWrite      = "w\$tsCanId%2o%2c%v"
burnCommand         = "b\$tsCanId"
blockingFactor      = 256
interWriteDelay     = 1
pageActivationDelay = 10

page = 1
veTable = array, U08, 0, [4x4], "%", 1, 0, 0, 255, 0
"#;

#[test]
fn the_firmwares_own_timings_are_read() {
    // Declared by the file and observed by TunerStudio, so ignoring them is
    // not "the same method".
    let layout = tune_layout::read(MEGASQUIRT, None);

    assert_eq!(layout.inter_write_delay, 1);
    assert_eq!(layout.after_burn_delay, 10);
}
