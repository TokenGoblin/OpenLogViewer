//! Ported from `LogReaderFactory.cs`. Owned by package `w2-logs`.

use super::delimited_log_reader::DelimitedLogReader;
use super::maxx_log_reader::MaxxLogReader;
use super::mlg_reader::MlgReader;
use crate::model::{LogDocument, LogReader};
use crate::{Error, Result};
use std::path::Path;

/// The open-file dialog's filter string, as the WPF dialog takes it.
pub const OPEN_FILE_FILTER: &str = "All datalogs|*.mlg;*.msl;*.csv;*.txt;*.log;*.tsv;*.dat;*.MaxxECU-Zip-log|\
Binary log \u{2014} MegaSquirt / rusEFI (*.mlg)|*.mlg|\
TunerStudio text log (*.msl)|*.msl|\
MaxxECU log (*.MaxxECU-Zip-log)|*.MaxxECU-Zip-log|\
Delimited text \u{2014} CSV / TSV (*.csv;*.tsv;*.txt;*.log)|*.csv;*.tsv;*.txt;*.log|\
All files|*.*";

/// Chooses a reader by sniffing file content, then decodes.
pub fn load(path: &Path) -> Result<LogDocument> {
    if !path.is_file() {
        return Err(Error::Io(std::io::Error::new(
            std::io::ErrorKind::NotFound,
            "Log file not found.",
        )));
    }

    // MaxxECU first: its logs are zips, and a zip offered to the delimited
    // reader is binary rubbish that it would refuse anyway, but sniffing for
    // the archive is cheaper and more certain than letting it try.
    let readers: [&dyn LogReader; 3] = [&MaxxLogReader, &MlgReader, &DelimitedLogReader];

    for reader in readers {
        if reader.can_read(path) {
            return reader.read(path);
        }
    }

    Err(Error::LogFormat(format!(
        "'{}' is not a recognised datalog format.",
        file_name(path)
    )))
}

pub(crate) fn file_name(path: &Path) -> String {
    path.file_name()
        .map(|n| n.to_string_lossy().into_owned())
        .unwrap_or_default()
}
