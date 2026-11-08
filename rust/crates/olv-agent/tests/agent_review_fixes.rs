//! Each hole the review found in the write path, as a test that fails with the hole and passes
//! without it. See the module docs of `agent_writer` for the gate each one belongs to.

mod support;

use std::sync::Arc;
use std::time::{Duration, Instant};

use olv_agent::agent::agent_api::{ProposedCell, ProposedSetting};
use olv_agent::agent::agent_writer::{AgentWriter, EcuTarget, RpmReading, TuneInHand, send_to_maxx};
use olv_core::tune::ecu_tune::{EcuTune, TuneWrite};
use olv_live::live::maxx_tune;
use olv_live::live::maxx_tune_definitions::MaxxSettingDefinition;
use olv_live::live::maxx_usb_source::MaxxUsbSource;
use olv_protocol::ms::maxx_protocol;
use support::maxx_ecu::{self, FakeMaxx};
use support::*;

fn setting(name: &str, value: f64) -> ProposedSetting {
    ProposedSetting {
        name: name.to_string(),
        value,
    }
}

fn cell(table: &str, column: i32, row: i32, value: f64) -> ProposedCell {
    ProposedCell {
        table: table.to_string(),
        column,
        row,
        value,
    }
}

// ----- 1. connecting disarms and forgets the tune --------------------------------------------

#[test]
fn connecting_disarms_and_forgets_the_tune_so_nothing_is_written_until_one_is_read() {
    let rig = armed_rig("fix-connect");
    assert!(rig.writer.is_armed());

    // The next thing plugged in (or the same one again, after a reset).
    rig.writer
        .connect(EcuTarget::Pages(Arc::clone(&rig.connection)), SIGNATURE);

    assert!(!rig.writer.is_armed(), "a connect must clear the arming");
    assert!(
        rig.writer.with_tune(|t| t.is_none()),
        "and the tune read from the last controller"
    );
    assert!(rig.writer.tune_values().is_empty());

    let refused = rig
        .writer
        .set_setting("crankingRPM", 310.0, "reason", false)
        .expect("refused");
    assert!(refused.reason.contains("not armed"));

    // Even armed again, there is no tune until one is read after the connect.
    rig.writer.set_armed(true);
    let refused = rig
        .writer
        .set_setting("crankingRPM", 310.0, "reason", false)
        .expect("refused");
    assert_eq!(refused.reason, "no tune has been read");
    let cell_write = rig.writer.set_table_cell("VE Table", 0, 0, 5.0, "reason", false);
    assert!(cell_write.refusal.is_some());
    assert!(rig.board.writes().is_empty());

    set_tune(&rig.writer, Some(tune_in_hand(&rig.board)));
    assert!(rig.writer.set_setting("crankingRPM", 310.0, "reason", false).is_none());
}

#[test]
fn disconnecting_forgets_the_tune_as_well() {
    let rig = armed_rig("fix-disconnect");

    rig.writer.disconnect();

    assert!(rig.writer.with_tune(|t| t.is_none()));
    rig.writer.set_armed(true);
    assert!(rig.writer.set_setting("crankingRPM", 310.0, "reason", false).is_some());
}

// ----- 2. only the agent's own bytes, and not over the person's -------------------------------

#[test]
fn a_table_cell_write_sends_the_cell_and_not_the_table() {
    let rig = armed_rig("fix-own-cell");

    assert!(
        rig.writer
            .set_table_cell("VE Table", 1, 0, 60.0, "one cell", false)
            .refusal
            .is_none()
    );

    // veTable is four bytes at offset 16; cell (1,0) is the one byte at 17.
    assert_eq!(rig.board.writes(), vec![(17, 1)]);
}

#[test]
fn a_refused_write_leaves_nothing_behind_to_ride_along_with_the_next() {
    // The controller says no to the first write. The next, different, write must send only
    // itself: not the one that was refused.
    let rig = armed_rig("fix-ride-along");

    rig.board.refuse_writes(true);
    let failed = rig
        .writer
        .set_setting("crankingRPM", 310.0, "first", false)
        .expect("refused");
    assert_eq!(failed.reason, "the write did not go through");
    assert!(rig.board.writes().is_empty());

    rig.board.refuse_writes(false);
    assert!(rig.writer.set_setting("revLimit", 6600.0, "second", true).is_none());

    assert_eq!(&rig.board.page()[0..2], &[0x01, 0x2C], "crankingRPM must still be 300");
    assert_eq!(&rig.board.page()[2..4], &[0x19, 0xC8]);
    assert_eq!(
        rig.board.writes(),
        vec![(3, 1)],
        "only the one byte of revLimit that changed"
    );
}

#[test]
fn a_page_the_person_has_unsent_edits_on_is_not_written_to() {
    let rig = armed_rig("fix-human-page");
    rig.host.set_unsent(&[0]);

    let one = rig
        .writer
        .set_setting("crankingRPM", 310.0, "reason", false)
        .expect("refused");
    assert_eq!(one.reason, "there are unsent edits on that page");
    assert!(one.detail.contains("Page 0"));

    let cell_write = rig.writer.set_table_cell("VE Table", 0, 0, 5.0, "reason", false);
    assert_eq!(
        cell_write.refusal.expect("refused").reason,
        "there are unsent edits on that page"
    );

    let batch = rig
        .writer
        .apply_tune(&[setting("crankingRPM", 310.0)], &[], "reason", false);
    assert!(batch.applied.is_empty());
    assert!(
        batch.rejected.iter().any(|r| r.contains("unsent edits")),
        "{:?}",
        batch.rejected
    );

    assert!(rig.board.writes().is_empty());

    // A different page is none of its business.
    rig.host.set_unsent(&[3]);
    assert!(rig.writer.set_setting("crankingRPM", 310.0, "reason", false).is_none());
}

// ----- 3. a batch is no way round the limits -----------------------------------------------------

#[test]
fn two_half_steps_in_one_batch_are_one_whole_step_and_refused() {
    // crankingRPM runs 0-10000: half is 5000. 300 -> 3300 -> 6300 is two steps of 3000, each
    // within the limit measured from the last, and 6000 in all.
    let rig = armed_rig("fix-halves");

    let result = rig.writer.apply_tune(
        &[setting("crankingRPM", 3300.0), setting("crankingRPM", 6300.0)],
        &[],
        "sneaking up",
        false,
    );

    assert!(result.applied.is_empty(), "{:?}", result.applied);
    assert!(
        result.rejected.iter().any(|r| r.contains("too large")),
        "{:?}",
        result.rejected
    );
    assert!(rig.board.writes().is_empty());
    assert_eq!(&rig.board.page()[0..2], &[0x01, 0x2C]);
}

#[test]
fn two_half_steps_to_one_cell_are_refused_too() {
    let rig = armed_rig("fix-cell-halves");

    let result = rig.writer.apply_tune(
        &[],
        &[cell("VE Table", 0, 0, 100.0), cell("VE Table", 0, 0, 200.0)],
        "sneaking up",
        false,
    );

    assert!(result.applied.is_empty());
    assert!(
        result.rejected.iter().any(|r| r.contains("too large")),
        "{:?}",
        result.rejected
    );
    assert_eq!(rig.board.page()[16], 0);
}

#[test]
fn two_names_for_the_same_bytes_cannot_dodge_the_batch_baseline() {
    // crankingRPM and crankAlias are the same two bytes under different names, so deduplicating by
    // name does not merge them. Each half-step is within the limit measured from the last; measured
    // from where the controller was before the batch, they are one step of 6000 and refused.
    let rig = armed_rig("fix-alias");
    let aliased = FIRMWARE.replace(
        "   launchTable",
        "   crankAlias  = scalar, U16, 0, \"rpm\", 1, 0, 0, 10000, 0
   launchTable",
    );
    set_tune(&rig.writer, Some(tune_in_hand_for(&rig.board, &aliased)));

    let result = rig.writer.apply_tune(
        &[setting("crankingRPM", 3300.0), setting("crankAlias", 6300.0)],
        &[],
        "sneaking up by two names",
        false,
    );

    assert!(
        result
            .rejected
            .iter()
            .any(|r| r.starts_with("crankAlias:") && r.contains("too large")),
        "{:?}",
        result.rejected
    );
    assert_eq!(
        &rig.board.page()[0..2],
        &[0x0C, 0xE4],
        "only the first half-step (3300) landed"
    );
}

#[test]
fn two_tables_over_the_same_cells_cannot_dodge_the_batch_baseline_either() {
    let rig = armed_rig("fix-alias-cells");
    let copied = format!(
        "{FIRMWARE}
   table = veCopyTbl, veCopyMap, \"VE Copy\", 1
      xBins = rpmBins, rpm
      yBins = mapBins, clt
      zBins = veTable
"
    );
    set_tune(&rig.writer, Some(tune_in_hand_for(&rig.board, &copied)));

    // 0 -> 100 is within half of 255; 100 -> 200 is too; 0 -> 200 is not.
    let result = rig.writer.apply_tune(
        &[],
        &[cell("VE Table", 0, 0, 100.0), cell("VE Copy", 0, 0, 200.0)],
        "same bytes, two names",
        false,
    );

    assert!(
        result
            .rejected
            .iter()
            .any(|r| r.starts_with("VE Copy[0,0]:") && r.contains("too large")),
        "{:?}",
        result.rejected
    );
    assert_eq!(rig.board.page()[16], 100);
}

#[test]
fn the_same_target_twice_keeps_the_last_and_says_so() {
    let rig = armed_rig("fix-dedupe");

    let result = rig.writer.apply_tune(
        &[setting("crankingRPM", 310.0), setting("crankingRPM", 320.0)],
        &[],
        "twice",
        false,
    );

    assert_eq!(result.applied.len(), 1);
    assert!(
        result.rejected.iter().any(|r| r.contains("superseded")),
        "{:?}",
        result.rejected
    );
    assert_eq!(&rig.board.page()[0..2], &[0x01, 0x40]); // 320
    assert_eq!(rig.board.writes().len(), 1, "one place, one write");
}

#[test]
fn a_batch_has_a_size_limit() {
    let rig = armed_rig("fix-cap");

    let mut items: Vec<ProposedCell> = Vec::new();
    for table in ["VE Table", "Launch Table"] {
        for column in 0..2 {
            for row in 0..2 {
                items.push(cell(table, column, row, 7.0));
            }
        }
    }
    let settings = [
        setting("crankingRPM", 301.0),
        setting("revLimit", 6501.0),
        setting("noSuchThing", 1.0),
    ];
    // 8 cells + 3 settings = 11.
    let result = rig.writer.apply_tune(&settings, &items, "too many at once", true);

    assert_eq!(
        result.refusal.expect("refused").reason,
        "that batch is too large for one call"
    );
    assert!(rig.board.writes().is_empty());
}

#[test]
fn a_batch_of_exactly_the_limit_applies_whole() {
    let rig = armed_rig("fix-cap-edge");

    let mut items: Vec<ProposedCell> = Vec::new();
    for table in ["VE Table", "Launch Table"] {
        for column in 0..2 {
            for row in 0..2 {
                items.push(cell(table, column, row, 7.0));
            }
        }
    }
    let settings = [setting("crankingRPM", 301.0), setting("revLimit", 6501.0)];

    let result = rig.writer.apply_tune(&settings, &items, "ten at once", true);

    assert!(result.refusal.is_none(), "{:?}", result.refusal);
    assert!(result.rejected.is_empty(), "{:?}", result.rejected);
    assert_eq!(
        result.applied.len(),
        4,
        "one difference each for two tables and two settings"
    );
    assert_eq!(rig.board.writes().len(), 10, "and ten device writes, one for each item");
}

#[test]
fn every_device_write_of_a_batch_counts_toward_the_rate_limit() {
    let rig = armed_rig("fix-rate");

    // Six device writes in one call...
    let items: Vec<ProposedCell> = (0..2)
        .flat_map(|column| (0..2).map(move |row| cell("VE Table", column, row, 5.0)))
        .chain([cell("Launch Table", 0, 0, 5.0), cell("Launch Table", 1, 0, 5.0)])
        .collect();
    let first = rig.writer.apply_tune(&[], &items, "six", true);
    assert!(first.refusal.is_none(), "{:?}", first.refusal);
    assert_eq!(rig.board.writes().len(), 6);

    // ...leave room for four more, so a second batch of six is refused whole...
    let more: Vec<ProposedCell> = (0..2)
        .flat_map(|column| (0..2).map(move |row| cell("VE Table", column, row, 9.0)))
        .chain([cell("Launch Table", 0, 1, 5.0), cell("Launch Table", 1, 1, 5.0)])
        .collect();
    let second = rig.writer.apply_tune(&[], &more, "six more", true);
    assert_eq!(
        second.refusal.expect("refused").reason,
        "too many writes in a short time"
    );
    assert_eq!(rig.board.writes().len(), 6);

    // ...and four single writes fill the window.
    for i in 0..4 {
        assert!(
            rig.writer
                .set_setting("crankingRPM", 301.0 + f64::from(i), "s", false)
                .is_none()
        );
    }
    assert!(rig.writer.set_setting("crankingRPM", 320.0, "s", false).is_some());
}

// ----- 4. the engine speed gate fails closed -------------------------------------------------------

#[test]
fn an_engine_speed_that_cannot_be_trusted_refuses_every_write() {
    let cases = [
        (RpmReading::Unknown, "the engine speed is not known"),
        // The host's label is not trusted: a connected controller may be running.
        (RpmReading::NoSession, "the engine speed is not known"),
        (
            RpmReading::Sample {
                rpm: f64::NAN,
                age: Duration::ZERO,
            },
            "the engine speed is not a number",
        ),
        (
            RpmReading::Sample {
                rpm: f64::INFINITY,
                age: Duration::ZERO,
            },
            "the engine speed is not a number",
        ),
        (
            RpmReading::Sample {
                rpm: 800.0,
                age: Duration::from_millis(1500),
            },
            "the engine speed is out of date",
        ),
    ];

    for (reading, reason) in cases {
        let rig = armed_rig("fix-rpm");
        rig.host.set_reading(reading);

        let one = rig
            .writer
            .set_setting("crankingRPM", 310.0, "s", false)
            .expect("refused");
        assert_eq!(one.reason, reason, "{reading:?}");
        assert!(
            rig.writer
                .set_table_cell("VE Table", 0, 0, 5.0, "c", false)
                .refusal
                .is_some()
        );
        assert!(
            rig.writer
                .apply_tune(&[setting("crankingRPM", 310.0)], &[], "b", false)
                .refusal
                .is_some()
        );
        assert!(rig.board.writes().is_empty(), "{reading:?}");
    }
}

#[test]
fn a_fresh_reading_at_idle_lets_a_write_through() {
    let rig = armed_rig("fix-rpm-ok");

    rig.host.set_reading(RpmReading::Sample {
        rpm: 800.0,
        age: Duration::from_millis(900),
    });
    assert!(rig.writer.set_setting("crankingRPM", 310.0, "fresh", false).is_none());
}

// ----- 5. an agent's definition is staged, not installed --------------------------------------------

const EVIL: &str = r#"
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
   engineCeiling = scalar, U16, 2, "rpm", 1, 0, 0, 10000, 0
"#;

fn identity() -> Vec<String> {
    vec![SIGNATURE.to_string()]
}

#[test]
fn an_agents_definition_is_staged_and_installs_nothing_unless_a_person_says_so() {
    let rig = armed_rig("fix-import");
    let definitions = rig.workspace.definitions();

    let result = rig
        .writer
        .import_definition("", EVIL, "https://example.invalid/x.ini", "evil.ini", &identity());

    assert!(!result.accepted(), "{result:?}");
    assert!(result.problem.contains("staged"), "{}", result.problem);
    assert!(result.problem.contains("Nothing was installed"));
    assert_eq!(
        rig.host.confirmations(),
        vec!["evil.ini".to_string()],
        "the person was asked"
    );

    // Staged, where the staging list shows it; not in the definitions folder.
    assert!(rig.workspace.staging().join("definitions").join("evil.ini").exists());
    let installed = std::fs::read_dir(&definitions)
        .map(|d| d.filter_map(Result::ok).any(|e| e.file_name() == "evil.ini"))
        .unwrap_or(false);
    assert!(
        !installed,
        "an unconfirmed import must not reach the definitions folder"
    );
}

#[test]
fn a_staged_or_even_installed_definition_changes_nothing_the_gates_judge_by() {
    let rig = armed_rig("fix-import-gates");

    // The evil definition renames the rev limiter to something innocent.
    rig.writer.import_definition("", EVIL, "x", "evil.ini", &identity());
    rig.host.answer_imports_with(true);
    let installed = rig.writer.import_definition("", EVIL, "x", "evil2.ini", &identity());
    assert!(installed.accepted(), "{installed:?}");
    assert!(std::path::Path::new(&installed.path).starts_with(rig.workspace.definitions()));

    // The tune in hand was read with the real one: revLimit is still the rev limiter and still
    // needs its confirmation, and the ranges are still the declared ones.
    let refused = rig
        .writer
        .set_setting("revLimit", 6600.0, "reason", false)
        .expect("refused");
    assert!(refused.reason.contains("confirmDangerous"));
    assert!(rig.writer.set_setting("engineCeiling", 1.0, "reason", false).is_some());
    assert!(rig.board.writes().is_empty());
}

#[test]
fn a_definition_for_other_firmware_is_refused_before_it_is_even_staged() {
    let rig = armed_rig("fix-import-wrong");

    let result = rig
        .writer
        .import_definition("", EVIL, "x", "wrong.ini", &["speeduino 202501".to_string()]);

    assert!(!result.accepted());
    assert!(result.problem.contains("TEST Format 0001.00"));
    assert!(
        rig.host.confirmations().is_empty(),
        "nobody is asked about a file that failed the checks"
    );
}

// ----- 8. a MaxxECU write is cut to what the ECU takes, with its checksum last -----------------------

fn maxx_rig(what: &str) -> (AgentWriter, FakeMaxx, maxx_tune::MaxxTable, Scratch) {
    let scratch = Scratch::new(what);
    let blob = maxx_ecu::blob_with_ve_table();
    let definitions = vec![MaxxSettingDefinition::new(
        "VE Table 1 data",
        "dynamicTable",
        maxx_ecu::CONFIG as i32,
        22 * 22,
        0.1,
        0.0,
        2500.0,
    )];
    let model = maxx_tune::build(&blob, &definitions, None);
    let ecu = FakeMaxx::holding(blob.clone());

    let writer = AgentWriter::new(
        olv_store::store::workspace::Workspace::new(Some(&scratch.0)),
        Arc::new(Host::default()),
    );
    writer.connect(
        EcuTarget::Maxx(Arc::new(MaxxUsbSource::new(Box::new(ecu.clone()), None))),
        "MaxxECU",
    );
    writer.set_maxx_definitions(definitions);
    writer.set_maxx_tables(model.maps.clone());
    set_tune(
        &writer,
        Some(TuneInHand {
            tune: EcuTune::from_pages(model.layout, vec![blob]),
            definitions: model.tables,
            signature: "MaxxECU".to_string(),
            placeholder: false,
            from_file: false,
        }),
    );
    writer.set_armed(true);

    (writer, ecu, model.maps[0].clone(), scratch)
}

#[test]
fn a_maxx_table_cell_goes_with_the_checksum_the_ecu_keeps_for_the_table_and_the_checksum_goes_last() {
    let (writer, ecu, table, _scratch) = maxx_rig("fix-maxx-table");
    let before = ecu.tune();

    let landed = writer.set_table_cell("VE Table 1", 0, 0, 30.0, "one cell", false);
    assert!(landed.refusal.is_none(), "{:?}", landed.refusal);

    let count = table.columns * table.rows;
    let crc_at = table.cells_at + count * 2;
    let after = ecu.tune();

    // The cell took (30.0 at a scale of a tenth is 300), nothing else in the grid moved...
    assert_eq!(
        i16::from_le_bytes([after[table.cells_at], after[table.cells_at + 1]]),
        300
    );
    assert_eq!(&after[table.cells_at + 2..crc_at], &before[table.cells_at + 2..crc_at]);

    // ...and the table's checksum was brought up to date, over the byte in front of the grid too.
    let mut covered = vec![after[table.cells_at - 1]];
    covered.extend_from_slice(&after[table.cells_at..crc_at]);
    let expected = maxx_protocol::crc32(&covered);
    assert_eq!(
        u32::from_le_bytes(after[crc_at..crc_at + 4].try_into().unwrap()),
        expected
    );

    // Only the byte of the cell that changed and the checksum were sent, the checksum last.
    assert_eq!(ecu.writes(), vec![(table.cells_at, 1), (crc_at, 4)]);
}

#[test]
fn a_maxx_table_with_no_map_is_refused_rather_than_sent_without_its_checksum() {
    let (writer, ecu, _table, _scratch) = maxx_rig("fix-maxx-nomap");
    writer.set_maxx_tables(Vec::new());

    let refused = writer
        .set_table_cell("VE Table 1", 0, 0, 30.0, "x", false)
        .refusal
        .expect("refused");

    assert!(refused.detail.contains("checksum"), "{}", refused.detail);
    assert_eq!(ecu.frames_sent(), 0);
}

#[test]
fn a_maxx_write_longer_than_the_ecu_takes_is_cut_into_pieces_in_order() {
    let ecu = FakeMaxx::holding(vec![0; maxx_tune::BLOB_SIZE]);
    let source = MaxxUsbSource::new(Box::new(ecu.clone()), None);

    send_to_maxx(&source, &TuneWrite::new(0, 2000, vec![7; 600])).expect("sent");

    assert_eq!(ecu.writes(), vec![(2000, 256), (2256, 256), (2512, 88)]);
    assert!(ecu.tune()[2000..2600].iter().all(|b| *b == 7));
}

#[test]
fn a_maxx_write_that_would_cross_the_ceiling_sends_none_of_it() {
    // A uint32 starting three bytes below the ceiling, its top byte set: the one changed byte is the
    // one past the line, and the whole request is refused before any frame goes out.
    let definitions = vec![MaxxSettingDefinition::new(
        "Edge Straddles",
        "uint32",
        maxx_tune::WRITE_CEILING - 3,
        1,
        1.0,
        0.0,
        4_000_000_000.0,
    )];
    let blob = vec![0u8; maxx_tune::BLOB_SIZE];
    let model = maxx_tune::build(&blob, &definitions, None);
    let scratch = Scratch::new("fix-maxx-straddle");
    let fresh = FakeMaxx::holding(blob.clone());
    let w = AgentWriter::new(
        olv_store::store::workspace::Workspace::new(Some(&scratch.0)),
        Arc::new(Host::default()),
    );
    w.connect(
        EcuTarget::Maxx(Arc::new(MaxxUsbSource::new(Box::new(fresh.clone()), None))),
        "MaxxECU",
    );
    set_tune(
        &w,
        Some(TuneInHand {
            tune: EcuTune::from_pages(model.layout, vec![blob]),
            definitions: model.tables,
            signature: "MaxxECU".to_string(),
            placeholder: false,
            from_file: false,
        }),
    );
    w.set_armed(true);

    let refused = w
        .set_setting("Edge Straddles", 16_777_217.0, "x", false)
        .expect("refused");

    assert!(
        refused.detail.contains("will not be written above 62183"),
        "{}",
        refused.detail
    );
    assert_eq!(fresh.frames_sent(), 0, "nothing at all may have gone out");
}

// ----- 9. reads never wait on the device -------------------------------------------------------------

#[test]
fn reading_the_tune_does_not_wait_for_a_write_to_the_device() {
    let rig = armed_rig("fix-snapshot");
    rig.board.slow_writes(Duration::from_millis(400));

    let writer = Arc::clone(&rig.writer);
    let writing = std::thread::spawn(move || writer.set_setting("crankingRPM", 310.0, "slow", false));

    // Let the write get into the device.
    std::thread::sleep(Duration::from_millis(100));

    let started = Instant::now();
    let values = rig.writer.tune_values();
    let tables = rig.writer.tables();
    let seen = rig.writer.with_tune(|t| t.map(|h| h.signature.clone()));
    let table = rig.writer.table("VE Table");
    let full = rig.writer.tune_full();
    let waited = started.elapsed();

    assert!(!values.is_empty() && !tables.is_empty() && seen.is_some() && table.is_some());
    assert!(!full.settings.is_empty());
    assert!(
        waited < Duration::from_millis(150),
        "reads waited {waited:?} for the device"
    );

    // The old value, until the write lands; then the new one.
    assert!(values.iter().any(|(n, v)| n == "crankingRPM" && *v == 300.0));
    assert!(writing.join().unwrap().is_none());
    assert!(
        rig.writer
            .tune_values()
            .iter()
            .any(|(n, v)| n == "crankingRPM" && *v == 310.0)
    );
}

// ===== the second review ==========================================================================

use sha2::{Digest, Sha256};

fn sha_of(bytes: &[u8]) -> String {
    Sha256::digest(bytes).iter().map(|b| format!("{b:02x}")).collect()
}

// ----- 2. a tune must belong to the connection it was read for -------------------------------

#[test]
fn a_tune_read_for_an_earlier_connection_is_ignored() {
    let rig = rig("r2-generation");
    let first = rig.writer.generation();

    // The controller changes while a slow read of the old one is still finishing.
    let second = rig
        .writer
        .connect(EcuTarget::Pages(Arc::clone(&rig.connection)), SIGNATURE);
    assert_ne!(first, second);

    assert!(
        !rig.writer.set_tune(first, Some(tune_in_hand(&rig.board))),
        "the late read must be refused"
    );
    assert!(rig.writer.with_tune(|t| t.is_none()));

    assert!(rig.writer.set_tune(second, Some(tune_in_hand(&rig.board))));
    assert!(rig.writer.with_tune(|t| t.is_some()));

    // A disconnect is a change of controller too.
    rig.writer.disconnect();
    assert!(!rig.writer.set_tune(second, Some(tune_in_hand(&rig.board))));
    assert!(rig.writer.with_tune(|t| t.is_none()));
}

#[test]
fn a_tune_for_other_firmware_than_the_connected_controller_reported_is_ignored() {
    let rig = rig("r2-signature");
    let generation = rig
        .writer
        .connect(EcuTarget::Pages(Arc::clone(&rig.connection)), SIGNATURE);

    let other = TuneInHand {
        signature: "SOME OTHER FIRMWARE 9.9".to_string(),
        ..tune_in_hand(&rig.board)
    };

    assert!(!rig.writer.set_tune(generation, Some(other)));
    assert!(rig.writer.with_tune(|t| t.is_none()));

    // Case and padding do not matter.
    let same = TuneInHand {
        signature: format!(" {} ", SIGNATURE.to_lowercase()),
        ..tune_in_hand(&rig.board)
    };
    assert!(rig.writer.set_tune(generation, Some(same)));
}

// ----- 3. an agent's definition: one at a time, hashed, only the file that was shown ----------

fn sha_of_staged(rig: &Rig, name: &str) -> String {
    sha_of(&std::fs::read(rig.workspace.staging().join("definitions").join(name)).expect("staged"))
}

#[test]
fn the_person_is_shown_the_hash_of_the_file_that_would_be_installed() {
    let rig = armed_rig("r2-hash");
    rig.host.answer_imports_with(true);

    let result = rig.writer.import_definition("", EVIL, "x", "shown.ini", &identity());
    assert!(result.accepted(), "{result:?}");

    let requests = rig.host.requests();
    assert_eq!(requests.len(), 1);
    assert_eq!(requests[0].0, sha_of_staged(&rig, "shown.ini"));
    assert!(!requests[0].1, "there was an identity to check against");
}

#[test]
fn a_staged_file_that_changes_while_the_person_decides_is_not_installed() {
    let rig = armed_rig("r2-swap");
    rig.host.answer_imports_with(true);

    let staged = rig.workspace.staging().join("definitions").join("swapped.ini");
    *rig.host.while_asking.lock().unwrap() = Some(Box::new(move || {
        std::fs::write(&staged, "[MegaTune]\nsignature = \"TEST Format 0001.00\"\n; tampered\n").unwrap();
    }));

    let result = rig.writer.import_definition("", EVIL, "x", "swapped.ini", &identity());

    assert!(!result.accepted());
    assert!(
        result.problem.contains("changed after it was shown"),
        "{}",
        result.problem
    );
    let installed = std::fs::read_dir(rig.workspace.definitions())
        .map(|d| d.filter_map(Result::ok).any(|e| e.file_name() == "swapped.ini"))
        .unwrap_or(false);
    assert!(!installed);
}

#[test]
fn an_import_with_no_ecu_to_check_against_says_so_and_needs_an_explicit_yes() {
    let rig = armed_rig("r2-unverified");

    let refused = rig.writer.import_definition("", EVIL, "x", "loose.ini", &[]);
    assert!(!refused.accepted());
    assert_eq!(rig.host.requests().len(), 1);
    assert!(
        rig.host.requests()[0].1,
        "the request must say it could not be verified"
    );

    rig.host.answer_imports_with(true);
    assert!(rig.writer.import_definition("", EVIL, "x", "loose.ini", &[]).accepted());
}

#[test]
fn a_path_outside_the_staging_folder_gets_one_answer_whether_or_not_it_exists() {
    let rig = armed_rig("r2-path");
    let outside = rig.workspace.root().join("outside.ini");
    std::fs::create_dir_all(rig.workspace.root()).unwrap();
    std::fs::write(&outside, EVIL).unwrap();
    let missing = rig.workspace.root().join("missing.ini");

    let present = rig
        .writer
        .import_definition(&outside.to_string_lossy(), "", "x", "", &identity());
    let absent = rig
        .writer
        .import_definition(&missing.to_string_lossy(), "", "x", "", &identity());
    let nonsense = rig
        .writer
        .import_definition("C:/definitely/not/a/path.ini", "", "x", "", &identity());

    assert!(!present.accepted());
    assert!(present.problem.starts_with("Only a file inside"), "{}", present.problem);
    assert_eq!(present.problem, absent.problem, "existence must not show");
    assert_eq!(present.problem, nonsense.problem);
    assert!(
        rig.host.confirmations().is_empty(),
        "nothing outside staging was even considered"
    );

    // A file in the staging folder is fine.
    std::fs::create_dir_all(rig.workspace.staging()).unwrap();
    let inside = rig.workspace.staging().join("mine.ini");
    std::fs::write(&inside, EVIL).unwrap();
    let ok = rig
        .writer
        .import_definition(&inside.to_string_lossy(), "", "x", "", &identity());
    assert!(ok.problem.contains("staged"), "{}", ok.problem);
    assert_eq!(rig.host.confirmations().len(), 1);
}

#[test]
fn two_imports_at_once_take_turns_from_staging_to_install() {
    let rig = armed_rig("r2-lock");
    rig.host.answer_imports_with(true);
    *rig.host.think_for.lock().unwrap() = Duration::from_millis(150);

    let threads: Vec<_> = ["a.ini", "b.ini"]
        .into_iter()
        .map(|name| {
            let writer = Arc::clone(&rig.writer);
            std::thread::spawn(move || writer.import_definition("", EVIL, "x", name, &identity()))
        })
        .collect();

    for t in threads {
        assert!(t.join().unwrap().accepted());
    }

    assert_eq!(rig.host.asking_most.load(std::sync::atomic::Ordering::SeqCst), 1);
}

// ----- 4. a MaxxECU table that is left half-written ---------------------------------------------

fn maxx_rig_with(
    what: &str,
    extra: Vec<MaxxSettingDefinition>,
) -> (AgentWriter, FakeMaxx, maxx_tune::MaxxTable, Scratch) {
    let scratch = Scratch::new(what);
    let blob = maxx_ecu::blob_with_ve_table();
    let mut definitions = vec![MaxxSettingDefinition::new(
        "VE Table 1 data",
        "dynamicTable",
        maxx_ecu::CONFIG as i32,
        22 * 22,
        0.1,
        0.0,
        2500.0,
    )];
    definitions.extend(extra);
    let model = maxx_tune::build(&blob, &definitions, None);
    let ecu = FakeMaxx::holding(blob.clone());

    let writer = AgentWriter::new(
        olv_store::store::workspace::Workspace::new(Some(&scratch.0)),
        Arc::new(Host::default()),
    );
    let generation = writer.connect(
        EcuTarget::Maxx(Arc::new(MaxxUsbSource::new(Box::new(ecu.clone()), None))),
        "MaxxECU",
    );
    writer.set_maxx_definitions(definitions);
    writer.set_maxx_tables(model.maps.clone());
    writer.set_tune(
        generation,
        Some(TuneInHand {
            tune: EcuTune::from_pages(model.layout, vec![blob]),
            definitions: model.tables,
            signature: "MaxxECU".to_string(),
            placeholder: false,
            from_file: false,
        }),
    );
    writer.set_armed(true);

    (writer, ecu, model.maps[0].clone(), scratch)
}

#[test]
fn a_checksum_write_that_fails_once_is_tried_again() {
    let (writer, ecu, table, _scratch) = maxx_rig_with("r2-crc-retry", vec![]);
    ecu.refuse_payloads_after(1, 1);

    let landed = writer.set_table_cell("VE Table 1", 0, 0, 30.0, "one cell", false);

    assert!(landed.refusal.is_none(), "{:?}", landed.refusal);
    let crc_at = table.cells_at + table.columns * table.rows * 2;
    assert_eq!(ecu.writes(), vec![(table.cells_at, 1), (crc_at, 4)]);
}

#[test]
fn a_table_whose_checksum_could_not_be_written_is_never_reported_as_done() {
    let (writer, ecu, table, _scratch) = maxx_rig_with("r2-crc-fails", vec![]);
    ecu.refuse_payloads_after(1, 100);

    let result = writer.set_table_cell("VE Table 1", 0, 0, 30.0, "one cell", false);

    let refusal = result.refusal.expect("never success");
    assert_eq!(refusal.reason, "the table's checksum is now invalid");
    assert!(refusal.detail.contains("checksum"), "{}", refusal.detail);
    assert!(
        refusal.detail.contains("Send the same cell again"),
        "{}",
        refusal.detail
    );
    // The cell did land; that is exactly why it must be said.
    assert_eq!(ecu.writes(), vec![(table.cells_at, 1)]);
}

#[test]
fn a_maxx_setting_that_overlaps_a_table_is_refused() {
    let inside = (maxx_tune::TABLE_DATA + maxx_ecu::DATA_OFFSET + 1 + 54 + 9 + 4) as i32;
    let (writer, ecu, _table, _scratch) = maxx_rig_with(
        "r2-overlap",
        vec![MaxxSettingDefinition::new(
            "Overlap Setting",
            "uint16",
            inside,
            1,
            1.0,
            0.0,
            65535.0,
        )],
    );

    let refused = writer
        .set_setting("Overlap Setting", 1000.0, "x", false)
        .expect("refused");

    assert_eq!(refused.reason, "that setting shares bytes with a table");
    assert_eq!(ecu.frames_sent(), 0);
}

// ----- 5. the rate pre-check counts device writes -----------------------------------------------

#[test]
fn a_maxx_batch_counts_the_checksum_write_of_every_cell() {
    let (writer, ecu, _table, _scratch) = maxx_rig_with("r2-rate", vec![]);

    // Six cells are twelve device writes (a cell and its table's checksum each), over the ten.
    let cells: Vec<ProposedCell> = (0..6).map(|c| cell("VE Table 1", c, 0, 40.0)).collect();
    let result = writer.apply_tune(&[], &cells, "six cells", false);

    assert_eq!(
        result.refusal.expect("refused").reason,
        "too many writes in a short time"
    );
    assert_eq!(ecu.frames_sent(), 0);

    // Five are ten, which fits.
    let five = writer.apply_tune(&[], &cells[..5], "five cells", false);
    assert!(five.refusal.is_none(), "{:?}", five.refusal);
}

// ----- 8. a page a write may have partly reached is not trusted --------------------------------

#[test]
fn a_write_whose_acknowledgement_was_lost_marks_the_page_until_it_is_read_again() {
    let rig = armed_rig("r2-stale");
    rig.board.silent_writes(true);

    let failed = rig
        .writer
        .set_setting("crankingRPM", 310.0, "first", false)
        .expect("refused");
    assert_eq!(failed.reason, "the write did not go through");
    assert_eq!(rig.writer.stale_pages(), vec![0]);

    // The controller is fine again, but what is held of the page is a guess.
    rig.board.silent_writes(false);
    let stale = rig
        .writer
        .set_setting("revLimit", 6600.0, "second", true)
        .expect("refused");
    assert_eq!(stale.reason, "that page needs reading again");
    let cell_write = rig.writer.set_table_cell("VE Table", 0, 0, 5.0, "c", false);
    assert_eq!(
        cell_write.refusal.expect("refused").reason,
        "that page needs reading again"
    );

    // Reading the tune again clears it.
    assert!(set_tune(&rig.writer, Some(tune_in_hand(&rig.board))));
    assert!(rig.writer.stale_pages().is_empty());
    assert!(rig.writer.set_setting("revLimit", 6600.0, "third", true).is_none());
}

#[test]
fn a_controller_that_simply_said_no_leaves_nothing_in_doubt() {
    let rig = armed_rig("r2-clean-no");
    rig.board.refuse_writes(true);

    assert!(rig.writer.set_setting("crankingRPM", 310.0, "first", false).is_some());

    assert!(rig.writer.stale_pages().is_empty());
}

// ----- 9. settings that other settings are scaled by --------------------------------------------

#[test]
fn a_setting_that_other_settings_are_scaled_by_is_not_the_agents_to_change() {
    let direct = FIRMWARE
        .replace(
            "veTable     = array,  U08, 16, [2x2], \"%\", 1,",
            "veTable     = array,  U08, 16, [2x2], \"%\", {rpmDiv},",
        )
        .replace(
            "   crankingRPM",
            "   rpmDiv      = scalar, U08, 26, \"\", 1, 0, 1, 10, 0\n   crankingRPM",
        );
    assert!(direct.contains("{rpmDiv}"), "the scale was not put in");

    let rig = armed_rig("r2-scale-direct");
    set_tune(&rig.writer, Some(tune_in_hand_for(&rig.board, &direct)));

    for confirm in [false, true] {
        let refused = rig
            .writer
            .set_setting("rpmDiv", 2.0, "retune", confirm)
            .expect("refused");
        assert_eq!(
            refused.reason, "this setting rescales other settings",
            "confirm {confirm}"
        );
    }
    assert!(rig.board.writes().is_empty());

    // Not everything is refused.
    assert!(rig.writer.set_setting("crankingRPM", 310.0, "fine", false).is_none());
}

#[test]
fn nor_one_the_scale_is_derived_from() {
    // Speeduino scales every load axis by {fuelLoadRes}, which is worked out from `algo`.
    let derived = FIRMWARE
        .replace(
            "veTable     = array,  U08, 16, [2x2], \"%\", 1,",
            "veTable     = array,  U08, 16, [2x2], \"%\", {fuelLoadRes},",
        )
        .replace(
            "   crankingRPM",
            "   algo        = scalar, U08, 27, \"\", 1, 0, 0, 3, 0\n   crankingRPM",
        )
        .replace(
            "   rpm  = scalar, U16, 0, \"rpm\", 1, 0\n",
            "   rpm  = scalar, U16, 0, \"rpm\", 1, 0\n   fuelLoadRes = { (algo == 0) || (algo == 2) ? 2.000 : 0.500 }\n",
        );
    assert!(derived.contains("fuelLoadRes = {"));

    let rig = armed_rig("r2-scale-derived");
    set_tune(&rig.writer, Some(tune_in_hand_for(&rig.board, &derived)));

    let refused = rig.writer.set_setting("algo", 1.0, "retune", true).expect("refused");
    assert_eq!(refused.reason, "this setting rescales other settings");
    assert!(rig.board.writes().is_empty());
}
