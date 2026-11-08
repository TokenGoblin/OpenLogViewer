//! `olv-probe`: asks a vehicle what it will answer beyond the OBD2 standard, and writes down
//! exactly what came back.
//!
//! Ported from `tools/OpenLogViewer.Probe/Program.cs`. This is a probe and not a feature. Nothing
//! here is guessed at and then acted on: every candidate is sent, every reply is recorded
//! verbatim, and what any of it means is decided afterwards by reading the transcript. That is the
//! whole point: the questions below are plausible rather than known, and a tool that quietly
//! interpreted them would turn a plausible guess into a confident wrong answer.
//!
//! **READ ONLY, and structurally so.** The SSM command set includes address writes (0xB8) and this
//! sends none; every request below either reads or asks what is supported. See [`guard`]:
//! anything that is not on the allowed list is refused rather than trusted to be harmless. The
//! output format is the C#'s, line for line.

use std::path::Path;
use std::process::ExitCode;
use std::time::{Duration, Instant};

use olv_core::Error;
use olv_live::connection::elm327_source::BAUD_RATES;
use olv_protocol::ms::ms_protocol::EcuTransport;
use olv_protocol::obd::elm327::Elm327;
use olv_transport::transport::serial_ecu_transport::SerialEcuTransport;
use olv_transport::transport::wifi_ecu_transport::{KNOWN_ADDRESSES, WifiEcuTransport};

const HELP: &str = "\
usage: olv-probe [COM port] [--baud N] [--out transcript.txt] [--sweep]
       olv-probe --wifi [address|auto] [--out transcript.txt] [--sweep]

With no port, lists what is available and exits.

--wifi reaches a Wi-Fi dongle \u{2014} a Vgate iCar Pro and the like \u{2014} which is
its own access point rather than a COM port: join its network first
(V-LINK on a Vgate). \"auto\" tries the addresses these are known to use;
otherwise give one, e.g. 192.168.0.10:35000.

Asks, in order:
  - what the adapter is, and which OBD2 protocol the car settled on
  - a standard mode 01 request, to prove the link works at all
  - mode 22, ReadDataByIdentifier, which many makers answer
  - mode 21, readDataByLocalIdentifier, where Toyota keeps its extra data
  - SSM over CAN, in several framings, to see whether any is understood

--sweep asks all 256 mode 21 identifiers rather than the first six, and
prints only the ones that answer. Takes a minute or so on a K-line car.
Worth it on a Toyota: the identifier list is the maker's and unpublished,
so sweeping is the only way to find out what a given car will report.

Read-only. Nothing here writes to the vehicle.";

fn main() -> ExitCode {
    let args: Vec<String> = std::env::args().skip(1).collect();

    if args.iter().any(|a| a == "--help" || a == "-h") {
        println!("{HELP}");
        return ExitCode::SUCCESS;
    }

    ExitCode::from(run(&args))
}

/// The port, which is the one argument that is neither a switch nor a switch's value.
///
/// Worth doing properly rather than taking the first token without a leading dash. `--out
/// transcript.txt COM10` would otherwise open a serial port called transcript.txt, and the failure
/// reads as the adapter not answering, which is the one thing this tool must never say when it
/// is not true.
fn port_in(args: &[String]) -> Option<String> {
    const TAKES_A_VALUE: [&str; 4] = ["--baud", "--out", "--raw", "--wifi"];

    let mut i = 0;
    while i < args.len() {
        if args[i].starts_with("--") {
            if TAKES_A_VALUE.iter().any(|t| t.eq_ignore_ascii_case(&args[i])) {
                i += 1;
            }
        } else {
            return Some(args[i].clone());
        }

        i += 1;
    }

    None
}

fn value(args: &[String], name: &str) -> Option<String> {
    let at = args.iter().position(|a| a == name)?;

    args.get(at + 1).cloned()
}

fn is_auto(address: &str) -> bool {
    address.is_empty() || address.eq_ignore_ascii_case("auto")
}

/// `DateTime.Now`: the local clock where the system will say, UTC where it will not.
fn now() -> time::OffsetDateTime {
    let utc = time::OffsetDateTime::now_utc();

    time::UtcOffset::local_offset_at(utc).map_or(utc, |offset| utc.to_offset(offset))
}

fn run(args: &[String]) -> u8 {
    // Read before any thread exists: the system will not give the local zone to a process that
    // has more than one.
    let started = now();

    let ports = SerialEcuTransport::available_ports();
    let port = port_in(args);

    // A Wi-Fi dongle is reached by address and by nothing else: it becomes no COM port and
    // appears in no list, so it cannot be offered above and cannot be found by looking.
    let wifi = value(args, "--wifi");

    if port.is_none() && wifi.is_none() {
        if ports.is_empty() {
            println!(
                "No serial ports. A Bluetooth Classic adapter must be paired first; a BLE one\n\
                 never becomes a COM port and cannot be reached by this tool.\n\
                 A Wi-Fi dongle can: join its network and use --wifi auto."
            );
        } else {
            println!(
                "Serial ports: {}\n\nGive one, e.g.  olv-probe {}\nOr --wifi auto for a Wi-Fi dongle, once you have joined its network.",
                ports.join(", "),
                ports[0]
            );
        }

        return if ports.is_empty() { 2 } else { 1 };
    }

    let baud: u32 = value(args, "--baud").and_then(|b| b.parse().ok()).unwrap_or(0);
    let transcript_path = value(args, "--out").unwrap_or_else(|| {
        format!(
            "probe-{:04}-{:02}-{:02}_{:02}-{:02}-{:02}.txt",
            started.year(),
            u8::from(started.month()),
            started.day(),
            started.hour(),
            started.minute(),
            started.second()
        )
    });

    let mut log = Log::default();

    log.line(&format!(
        "# OpenLogViewer probe \u{2014} {:04}-{:02}-{:02} {:02}:{:02}:{:02}",
        started.year(),
        u8::from(started.month()),
        started.day(),
        started.hour(),
        started.minute(),
        started.second()
    ));

    log.line(&match &wifi {
        Some(w) => format!(
            "# Wi-Fi {}",
            if is_auto(w) {
                "(the usual addresses)".to_string()
            } else {
                w.clone()
            }
        ),
        None => format!(
            "# port {}{}",
            port.as_deref().unwrap_or(""),
            if baud > 0 {
                format!(" at {baud}")
            } else {
                " (speed found automatically)".to_string()
            }
        ),
    });

    log.line("");

    let exit = match probe(args, &mut log, port.as_deref(), wifi.as_deref(), baud) {
        Ok(code) => code,
        Err(e) => {
            log.line(&format!("\nThe probe stopped: {}: {e}", exception_name(&e)));
            4
        }
    };

    match std::fs::write(&transcript_path, log.transcript.as_bytes()) {
        Ok(()) => {
            let full =
                std::path::absolute(&transcript_path).unwrap_or_else(|_| Path::new(&transcript_path).to_path_buf());
            println!("\nTranscript written to {}", full.display());
        }
        Err(e) => eprintln!("\nCould not write the transcript: {e}"),
    }

    exit
}

/// The name the C# exception would have carried.
fn exception_name(e: &Error) -> &'static str {
    match e {
        Error::Io(_) => "IOException",
        Error::Timeout(_) => "TimeoutException",
        Error::Protocol(_) => "EcuProtocolException",
        Error::InvalidOperation(_) => "InvalidOperationException",
        Error::Argument(_) => "ArgumentException",
        _ => "Exception",
    }
}

#[derive(Default)]
struct Log {
    transcript: String,
}

impl Log {
    fn line(&mut self, text: &str) {
        println!("{text}");
        self.transcript.push_str(text);
        self.transcript.push('\n');
    }
}

fn section(log: &mut Log, title: &str) {
    log.line("");
    log.line(&"-".repeat(68));
    log.line(&format!("## {title}"));
    log.line(&"-".repeat(68));
    log.line("");
}

/// Everything between "which port" and "put the adapter back": the C#'s `try` block. Returns the
/// exit code.
fn probe(args: &[String], log: &mut Log, port: Option<&str>, wifi: Option<&str>, baud: u32) -> Result<u8, Error> {
    let mut elm: Option<Elm327> = None;

    if let Some(wifi) = wifi {
        // No speed to find: the radio settled that, as it does on Bluetooth. What has to be found
        // instead is the address, and only because the makers did not agree on one.
        let addresses: Vec<&str> = if is_auto(wifi) {
            KNOWN_ADDRESSES.to_vec()
        } else {
            vec![wifi]
        };

        for address in addresses {
            let attempt = WifiEcuTransport::at(address).and_then(|mut transport| {
                transport.open()?;
                let mut trial = Elm327::new(Box::new(transport));
                let said = trial.reset(None)?;
                Ok((trial, said))
            });

            match attempt {
                Ok((trial, said)) if !said.is_empty() => {
                    log.line(&format!("opened at {address}"));
                    elm = Some(trial);
                    break;
                }
                Ok(_) => log.line(&format!("  {address}: connected, but nothing answered as an ELM327")),
                Err(e) => log.line(&format!("  {address}: {e}")),
            }
        }

        if elm.is_none() {
            log.line(
                "No Wi-Fi OBD2 adapter answered. Check this computer is still on the dongle's \
                 own network \u{2014} Windows leaves one that has no internet.",
            );

            return Ok(3);
        }
    } else {
        // A Bluetooth adapter ignores the speed entirely, the radio having already negotiated
        // one; a wired one has to be found. Trying the list is what Elm327Source does on
        // connect, so this matches it.
        let speeds: Vec<u32> = if baud > 0 { vec![baud] } else { BAUD_RATES.to_vec() };

        for speed in speeds {
            let mut transport = SerialEcuTransport::new(port.unwrap_or(""), speed).with_open_attempts(3);

            let attempt = transport.open().and_then(|()| {
                let mut trial = Elm327::new(Box::new(transport));
                let said = trial.reset(None)?;
                Ok((trial, said))
            });

            match attempt {
                Ok((trial, said)) if !said.is_empty() => {
                    log.line(&format!("opened at {speed}"));
                    elm = Some(trial);
                    break;
                }
                Ok(_) => {}
                Err(e) => log.line(&format!("  {speed}: {e}")),
            }
        }

        if elm.is_none() {
            log.line("Nothing on this port answered as an OBD2 adapter.");
            return Ok(3);
        }
    }

    let Some(mut elm) = elm else {
        return Ok(3);
    };
    elm.timeout = Duration::from_secs(3);

    // ----- what we are talking to ---------------------------------------------------------------

    section(log, "THE ADAPTER AND THE LINK");

    let reset = elm.reset(None)?;
    log.line(&format!("reset      : {reset}"));
    let identity = elm.identify()?;
    log.line(&format!("identity   : {identity}"));

    // ----- wait for the protocol to be found ------------------------------------------------------

    // The first request after a reset is not like the others. The adapter has been told to find
    // the protocol itself and works through nine of them, answering "SEARCHING..." meanwhile:
    // seconds, where a settled link answers in milliseconds. Give up on it too early and the next
    // request aborts the search that was still running, which is reported as "STOPPED"; ask a
    // third time and the whole run is SEARCHING and STOPPED alternating, with nothing learnt about
    // the car and every appearance of a vehicle that is switched off. That is exactly what the
    // first run of this tool did on a car that was running perfectly.
    //
    // So the protocol is settled first and patiently, and nothing else is asked until it has
    // been, including which protocol it is, since that question has no answer until the search
    // finishes.

    section(log, "ORDINARY OBD2 \u{2014} DOES ANYTHING ANSWER");

    // Far longer than a reading is given, because this is not a reading: the adapter is working
    // through nine protocols and only the last one answers.
    let search_timeout = Duration::from_secs(12);
    const SEARCH_ATTEMPTS: u32 = 4;

    let mut settled = false;

    let mut attempt = 1;
    while attempt <= SEARCH_ATTEMPTS && !settled {
        let clock = Instant::now();
        let reply = elm.send("0100", search_timeout, true, 0)?;
        let elapsed = clock.elapsed();

        settled = reply.to_ascii_lowercase().contains("41") && !reply.to_ascii_lowercase().contains("searching");

        log.line(&format!(
            "-> 0100         supported PIDs 01-20, attempt {attempt} of {SEARCH_ATTEMPTS}"
        ));
        log.line(&format!("<- {}   [{} ms]", readable(&reply), elapsed.as_millis()));
        log.line("");

        attempt += 1;
    }

    if !settled {
        log.line("The vehicle never answered a standard request, so nothing below would");
        log.line("mean anything. Check the ignition is on and the adapter is seated in");
        log.line("the socket -- most cars stop answering OBD2 entirely with the key out.");

        return Ok(5);
    }

    // Only now, because until the search finished the adapter would have said "AUTO" and
    // protocol zero, which is not a protocol.
    let name = elm.protocol_name()?;
    log.line(&format!("protocol   : {name}"));
    let can = elm.is_can()?;
    log.line(&format!("is CAN     : {}", if can { "True" } else { "False" }));
    log.line("");

    ask(&mut elm, log, "010C", "engine speed")?;

    // ----- whatever was asked for on the command line ----------------------------------------------

    // For following a thread the moment it appears. The probe's fixed questions are the opening
    // move; the interesting ones are always the follow-ups, and a car is rarely connected for
    // long enough to rebuild a tool between them.
    if let Some(raw) = value(args, "--raw").filter(|r| !r.is_empty()) {
        section(log, "AS ASKED");

        // SSM lives behind a directed header rather than the broadcast one, so anything beginning
        // A8 needs the engine module addressed first.
        if raw.to_ascii_uppercase().contains("A8") {
            setup(&mut elm, log, "ATSH7E0", "send to the engine module")?;
            setup(&mut elm, log, "ATCRA7E8", "listen only to its replies")?;
            setup(&mut elm, log, "ATFCSH7E0", "flow control header")?;
            setup(&mut elm, log, "ATFCSD300000", "flow control data")?;
            setup(&mut elm, log, "ATFCSM1", "use it")?;
            setup(
                &mut elm,
                log,
                "ATAL",
                "allow long messages \u{2014} the 7-byte cap may be this",
            )?;
            log.line("");
        }

        for command in raw.split(';').filter(|c| !c.is_empty()) {
            ask(&mut elm, log, command.trim(), "asked for")?;
        }

        setup(&mut elm, log, "ATSH7DF", "back to the broadcast header")?;
        setup(&mut elm, log, "ATCRA", "clear the filter")?;

        return Ok(0);
    }

    // ----- mode 22, ReadDataByIdentifier -------------------------------------------------------------

    // The likeliest of the three to work, and the least trouble: it is an ordinary OBD service
    // number, so the adapter passes it through untouched and no headers have to be meddled with.
    // A car that answers any of these has a door open that needs no special hardware.

    section(log, "MODE 22 \u{2014} ReadDataByIdentifier");
    log.line("# A positive answer echoes 0x62. 0x7F means the service or the");
    log.line("# identifier was refused, which is itself worth knowing: 7F 22 11 is");
    log.line("# 'no such service', 7F 22 31 is 'service fine, wrong identifier'.");
    log.line("");

    for (did, what) in [
        ("22F190", "VIN, a standard identifier"),
        ("22F18C", "ECU serial number"),
        ("22F194", "ECU software number"),
        ("22F1A0", "maker-defined, often a calibration id"),
    ] {
        ask(&mut elm, log, did, what)?;
    }

    // ----- mode 21, Toyota's enhanced data -------------------------------------------------------------

    // Where Toyota keeps everything the standard does not cover. KWP2000 calls service 0x21
    // readDataByLocalIdentifier: one byte selects a block, and the meaning of each block is the
    // maker's own and is not published; Toyota licenses the list to trade tools rather than
    // documenting it.
    //
    // Which is exactly why sweeping is worth more than guessing. The identifier is a single byte,
    // so the whole space is 256 questions and a car will answer to a handful of them. What comes
    // back is a block of bytes whose meaning is unknown, but knowing *which* blocks exist and how
    // long each one is turns an undocumented protocol into a short list to work through.
    //
    // Read-only by the definition of the service. Nothing here can change a setting, and the
    // routine-starting service that could (0x31) is refused by `guard`.

    section(
        log,
        "MODE 21 \u{2014} readDataByLocalIdentifier (Toyota's enhanced data)",
    );
    log.line("# A positive answer echoes 0x61 then the identifier. 0x7F 21 11 means the");
    log.line("# service does not exist here at all \u{2014} on that reply the rest is pointless.");
    log.line("# 0x7F 21 12 means the service is there and that identifier is not.");
    log.line("");

    // Long messages allowed: Toyota's blocks routinely run past the seven bytes an adapter will
    // otherwise hand back, and a truncated block reads as a shorter one rather than as an error.
    setup(&mut elm, log, "ATAL", "allow replies longer than seven bytes")?;
    log.line("");

    let sweep = args.iter().any(|a| a == "--sweep");
    let mut found = 0;

    let ids: Vec<u32> = if sweep {
        (0..256).collect()
    } else {
        vec![0, 1, 2, 3, 4, 5]
    };

    for id in ids {
        let command = format!("21{id:02X}");
        guard(&command)?;

        let reply = elm.send(&command, Duration::from_secs(5), !sweep, 0)?;
        let text = readable(&reply);

        // On a sweep only the answers are worth printing: 250 lines of "NO DATA" buries the six
        // lines that matter.
        let lower = text.to_ascii_lowercase();
        let answered = lower.contains("61") && !lower.contains("7f");

        if answered {
            found += 1;
        }

        if answered || !sweep {
            log.line(&format!("-> {command:<12}{}", if answered { "ANSWERED" } else { "" }));
            log.line(&format!("<- {text}"));
            log.line("");
        }
    }

    if sweep {
        log.line(&format!("# swept 256 identifiers, {found} answered"));
    } else {
        log.line("# only the first six were asked. Add --sweep to try all 256.");
    }

    // ----- SSM over CAN ----------------------------------------------------------------------------------

    // The interesting one and the least certain. On the older K-line cars SSM is framed itself:
    // 0x80, destination, source, length, command, checksum, and an ELM327 cannot produce that.
    // Over CAN the ISO-TP layer does the framing, so in principle the command bytes alone are the
    // payload and the adapter can carry them. Whether a 2014 Subaru answers that way is exactly
    // what is not known, which is why several framings are tried rather than one.
    //
    // The addressing: SSM speaks to the engine module at 0x7E0 and is answered on 0x7E8, the same
    // pair OBD2 uses for a directed request.

    section(log, "SSM OVER CAN");
    log.line("# Header set to 7E0, replies filtered to 7E8. A positive answer to the");
    log.line("# init command echoes 0xFF and carries the ECU identifier followed by a");
    log.line("# capability bitmap; a read echoes 0xE8. Anything else \u{2014} NO DATA, ?,");
    log.line("# silence \u{2014} means this framing was not understood.");
    log.line("");

    setup(&mut elm, log, "ATSH7E0", "send to the engine module")?;
    setup(&mut elm, log, "ATCRA7E8", "listen only to its replies")?;
    setup(&mut elm, log, "ATFCSH7E0", "flow control header")?;
    setup(
        &mut elm,
        log,
        "ATFCSD300000",
        "flow control data \u{2014} send all, no delay",
    )?;
    setup(&mut elm, log, "ATFCSM1", "use the flow control set above")?;
    log.line("");

    // Init. The single most valuable answer here: it names the ECU and returns the bitmap of every
    // address the car will report, which would remove all the guesswork that follows.
    for (command, what) in [
        ("BF", "SSM init, bare command"),
        ("BF40", "SSM init with the padding byte the K-line form uses"),
        ("A8000000", "read one address, the continuous-response form"),
        // 0x00000E is where engine speed sits on a good many Subarus. A candidate rather than a
        // fact, which is the point of asking.
        ("A80000000E", "read address 0x00000E \u{2014} engine speed on many"),
        ("A80100000E", "the same, single-response form"),
    ] {
        ask(&mut elm, log, command, what)?;
    }

    // Put the adapter back the way it was found. A probe that leaves a header set has changed what
    // the next connection does, and the next connection is the application.
    section(log, "RESTORING");
    setup(&mut elm, log, "ATCRA", "clear the receive filter")?;
    setup(&mut elm, log, "ATFCSM0", "flow control back to automatic")?;
    setup(&mut elm, log, "ATSH7DF", "back to the broadcast header OBD2 uses")?;
    let reset = elm.reset(None)?;
    log.line(&format!("reset      : {reset}"));

    Ok(0)
}

/// Sends one request and writes down exactly what came back.
///
/// Deliberately uninterpreted. The reply is printed as the adapter gave it, control characters and
/// all, because a probe whose job is to find out what a car speaks must not decide in advance what
/// the answer was going to look like.
fn ask(elm: &mut Elm327, log: &mut Log, command: &str, what: &str) -> Result<(), Error> {
    guard(command)?;

    let clock = Instant::now();
    let reply = elm.send(command, Duration::from_secs(6), true, 0)?;
    let elapsed = clock.elapsed();

    log.line(&format!("-> {command:<12} {what}"));
    log.line(&format!("<- {}   [{} ms]", readable(&reply), elapsed.as_millis()));
    log.line("");

    Ok(())
}

/// An adapter setting, whose only interesting outcome is OK or not.
fn setup(elm: &mut Elm327, log: &mut Log, command: &str, what: &str) -> Result<(), Error> {
    guard(command)?;

    let reply = elm.send(command, Duration::from_secs(2), false, 0)?;

    log.line(&format!("-> {command:<12} {what}  ->  {}", readable(&reply)));

    Ok(())
}

/// Refuses anything that is not a read.
///
/// The SSM command set can write to arbitrary addresses in a running ECU, and this tool exists to
/// find out what a car says rather than to change it. Rather than trusting every string above to
/// be harmless, the allowed commands are listed and everything else is an error, so a careless
/// edit to this file fails loudly instead of quietly sending a write to somebody's engine.
fn guard(command: &str) -> Result<(), Error> {
    let text = command.to_uppercase();

    if text.starts_with("AT") {
        return Ok(());
    }

    // The STN chips' own commands. STPX carries a vehicle request inside it ("STPX H:7E0, D:A8
    // 00 ...") so the payload is pulled out and held to the same rule as anything else. An
    // adapter command that can smuggle an arbitrary write past the guard would defeat the point
    // of having one.
    if text.starts_with("ST") {
        let Some(data) = text.find("D:") else {
            return Ok(());
        };

        let payload: String = text[data + 2..].chars().filter(char::is_ascii_hexdigit).collect();

        return guard(&payload);
    }

    // OBD2 services 01, 05 and 09 read; 22 reads by identifier. 05 is also the ISO-TP
    // single-frame marker for a five-byte payload, which is how a raw SSM read looks with the
    // adapter's auto-formatting off -- harmless under either reading, which is exactly why it is
    // the one worth testing with. KWP2000 service 21 is readDataByLocalIdentifier, which is what
    // Toyota puts its extra parameters behind, read-only by definition of the service. SSM 0xA8
    // and 0xA0 read addresses and blocks, 0xBF asks what is supported.
    //
    // Absent on purpose: SSM 0xB8 writes an address, and KWP 0x31 starts a routine. Neither
    // belongs in a tool for finding out what a car will say.
    let reads = ["01", "05", "09", "21", "22", "A8", "A0", "BF"]
        .iter()
        .any(|p| text.starts_with(p));

    if !reads {
        return Err(Error::InvalidOperation(format!(
            "'{command}' is not one of the read commands this tool is allowed to send."
        )));
    }

    Ok(())
}

/// The reply on one line, with the line breaks shown rather than obeyed.
fn readable(reply: &str) -> String {
    let mut text = reply.replace('\r', " | ").replace('\n', "").trim().to_string();

    while text.contains("|  |") {
        text = text.replace("|  |", "|");
    }

    let trimmed = text.trim_matches(|c| c == ' ' || c == '|');

    if trimmed.is_empty() {
        "(nothing)".to_string()
    } else {
        trimmed.to_string()
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn reads_and_adapter_settings_are_allowed() {
        for command in [
            "0100",
            "010C",
            "22F190",
            "2100",
            "21FF",
            "BF",
            "A80000000E",
            "A0",
            "ATZ",
            "atsh7e0",
            "ATCRA",
            "ATFCSM1",
            "0902",
            "0501",
        ] {
            assert!(guard(command).is_ok(), "{command}");
        }
    }

    #[test]
    fn a_write_is_refused_however_it_is_dressed() {
        for command in [
            "B8000000FF",
            "b8000000ff",
            "3101",
            "2E0102",
            "0E",
            "",
            "FF",
            "STPX H:7E0, D:B8 00 00 0E FF",
            "STPX H:7E0, D:31 01",
            "stpx d:b80000",
        ] {
            assert!(guard(command).is_err(), "{command:?} should be refused");
        }
    }

    #[test]
    fn a_stn_command_carrying_a_read_is_allowed() {
        assert!(guard("STPX H:7E0, D:A8 00 00 00 0E").is_ok());
        assert!(guard("STI").is_ok());
    }

    #[test]
    fn a_reply_is_shown_on_one_line() {
        assert_eq!(readable("41 0C 1A F8\r\r>"), "41 0C 1A F8 | >");
        assert_eq!(readable(""), "(nothing)");
        assert_eq!(readable("\r\r"), "(nothing)");
        assert_eq!(readable("NO DATA\r"), "NO DATA");
    }

    #[test]
    fn the_port_is_the_argument_that_is_neither_a_switch_nor_a_switchs_value() {
        let args = |list: &[&str]| list.iter().map(|s| s.to_string()).collect::<Vec<_>>();

        assert_eq!(port_in(&args(&["--out", "t.txt", "COM10"])).as_deref(), Some("COM10"));
        assert_eq!(port_in(&args(&["COM3", "--baud", "9600"])).as_deref(), Some("COM3"));
        assert_eq!(port_in(&args(&["--wifi", "auto"])), None);
        assert_eq!(port_in(&args(&["--sweep"])), None);
    }
}
