//! The MaxxECU's write ceiling, reached through the agent's own gates.
//!
//! A MaxxECU has no burn: a write is live and permanent the moment it lands. The protocol layer
//! refuses any write that would reach past `0xF2E7` (`MaxxTune::write`, tested in `olv-live`),
//! and refuses command `0x2F`, which erases the ECU's flash, outright. This proves the agent's
//! writer reaches that refusal rather than going round it, and that a refusal puts nothing on the
//! wire.

mod support;

use std::sync::{Arc, Mutex};
use std::time::Duration;

use olv_agent::agent::agent_writer::{AgentWriter, EcuTarget, TuneInHand};
use olv_core::Result;
use olv_core::tune::ecu_tune::EcuTune;
use olv_live::live::maxx_tune::{self, WRITE_CEILING};
use olv_live::live::maxx_tune_definitions::MaxxSettingDefinition;
use olv_live::live::maxx_usb_source::MaxxUsbSource;
use olv_protocol::ms::ms_protocol::EcuTransport;
use support::{Host, Scratch};

/// A cable that records what is put on it and never answers.
#[derive(Clone, Default)]
struct Silent(Arc<Mutex<Vec<Vec<u8>>>>);

impl Silent {
    fn sent(&self) -> Vec<Vec<u8>> {
        self.0.lock().unwrap().clone()
    }
}

impl EcuTransport for Silent {
    fn is_open(&self) -> bool {
        true
    }

    fn open(&mut self) -> Result<()> {
        Ok(())
    }

    fn close(&mut self) {}

    fn write(&mut self, data: &[u8]) -> Result<()> {
        self.0.lock().unwrap().push(data.to_vec());
        Ok(())
    }

    fn read(&mut self, _buffer: &mut [u8], _timeout: Duration) -> Result<usize> {
        Ok(0)
    }

    fn discard_input(&mut self) {}
}

struct Rig {
    writer: AgentWriter,
    cable: Silent,
    _scratch: Scratch,
}

fn rig() -> Rig {
    let scratch = Scratch::new("maxx-ceiling");
    let cable = Silent::default();

    // Two four-byte settings: one that ends exactly on the ceiling, one a byte past it. (A write
    // sends only the bytes that changed, so the tests set the top byte: that is the one at the
    // edge.)
    let definitions = vec![
        MaxxSettingDefinition::new(
            "Edge OnTheLine",
            "uint32",
            WRITE_CEILING - 4,
            1,
            1.0,
            0.0,
            4_000_000_000.0,
        ),
        MaxxSettingDefinition::new(
            "Edge OneOver",
            "uint32",
            WRITE_CEILING - 3,
            1,
            1.0,
            0.0,
            4_000_000_000.0,
        ),
    ];

    let blob = vec![0u8; maxx_tune::BLOB_SIZE];
    let model = maxx_tune::build(&blob, &definitions, None);

    let writer = AgentWriter::new(
        olv_store::store::workspace::Workspace::new(Some(&scratch.0)),
        Arc::new(Host::default()),
    );
    writer.connect(EcuTarget::Maxx(Arc::new(MaxxUsbSource::new(
        Box::new(cable.clone()),
        None,
    ))));
    writer.set_tune(Some(TuneInHand {
        tune: EcuTune::from_pages(model.layout, vec![blob]),
        definitions: model.tables,
        signature: "MaxxECU".to_string(),
        placeholder: false,
        from_file: false,
    }));
    writer.set_armed(true);

    Rig {
        writer,
        cable,
        _scratch: scratch,
    }
}

#[test]
fn a_write_that_would_reach_past_the_ceiling_is_refused_and_nothing_is_sent() {
    let rig = rig();

    let refused = rig
        .writer
        .set_setting("Edge OneOver", 16_777_216.0, "past the line", false)
        .expect("refused");

    assert_eq!(refused.reason, "the write did not go through");
    assert!(
        refused.detail.contains("will not be written above 62183"),
        "{}",
        refused.detail
    );
    assert!(
        rig.cable.sent().is_empty(),
        "the cable must be silent: {:?}",
        rig.cable.sent()
    );
}

#[test]
fn unarmed_the_cable_is_silent_whatever_is_asked() {
    let rig = rig();
    rig.writer.set_armed(false);

    for name in ["Edge OnTheLine", "Edge OneOver"] {
        let refused = rig.writer.set_setting(name, 5.0, "unarmed", true).expect("refused");
        assert!(refused.reason.contains("not armed"));
    }

    assert!(rig.cable.sent().is_empty());
}

#[test]
fn a_write_ending_on_the_ceiling_is_attempted_and_a_silent_ecu_is_reported_not_assumed() {
    // The ECU never answers: the header goes out, nothing acknowledges it, and the answer is that
    // nothing was sent, not that it worked.
    let rig = rig();

    let refused = rig
        .writer
        .set_setting("Edge OnTheLine", 16_777_216.0, "on the line", false)
        .expect("a silent ECU is not a success");

    assert_eq!(refused.reason, "the write did not go through");
    assert!(refused.detail.starts_with("Nothing was sent:"), "{}", refused.detail);
    assert!(refused.detail.contains("NoAnswer"), "{}", refused.detail);
}

#[test]
fn the_only_command_the_writer_ever_names_is_the_tune_command() {
    // Command 0x2F is the firmware flasher and erases the ECU. The writer cannot name a command at
    // all, and the protocol layer below it refuses that one outright; this checks that what the
    // writer does put on the cable is the tune command in the write direction and nothing else.
    let rig = rig();

    let _ = rig.writer.set_setting("Edge OnTheLine", 16_777_216.0, "x", false);

    let sent = rig.cable.sent();
    assert!(!sent.is_empty(), "the write should have been attempted");

    let header = &sent[0];
    assert_eq!(header[0], olv_protocol::ms::maxx_usb_protocol::WRITE);
    assert_eq!(header[1], maxx_tune::READ_TUNE);
    assert_ne!(header[1], 0x2F);
}
