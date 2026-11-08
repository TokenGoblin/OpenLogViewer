//! Ported from `MlgReader.cs`. Owned by package `w2-logs`.
//!
//! Reader for the binary "MLVLG" datalog written by TunerStudio / MegaSquirt.
//!
//! The layout below was derived clean-room by analysing sample .mlg files. The
//! container is self-describing, so no third-party code was consulted.
//!
//! ```text
//! File header (24 bytes, big-endian throughout)
//!   0   char[6]  "MLVLG\0"
//!   6   u16      format version (observed: 2)
//!   8   u32      capture timestamp (Unix seconds)
//!   12  u32      offset of the info/metadata block
//!   16  u32      offset of the first record
//!   20  u16      payload size of one data record, in bytes
//!   22  u16      channel descriptor count
//!
//! Channel descriptors follow immediately, 89 bytes each
//!   0   u8       data type (see MlgDataType)
//!   1   char[34] name, NUL-padded UTF-8
//!   35  char[11] units, NUL-padded UTF-8
//!   46  f32      scale
//!   50  f32      transform (added before scaling)
//!   54  u8       display decimal places
//!   55  byte[34] reserved
//! ```
//!
//! Records share a 4-byte header: type, an 8-bit sequence counter that
//! increments across every record, and a u16 logger tick.
//!
//! * type 0, sample: 4 + payload + 1 trailing checksum byte
//! * type 1, marker: 4 + char[50] annotation text, fixed 54 bytes
//!
//! Marker records are interleaved with samples, so records must be walked rather
//! than indexed at a fixed stride.
//!
//! Channel values are `(raw + transform) * scale`.

use crate::Error;
use crate::Result;
use crate::model::{LogChannel, LogDocument, LogMarker, LogReader};
use std::io::Read;
use std::path::Path;
use std::sync::Arc;
use std::time::{Duration, SystemTime};

const FILE_HEADER_SIZE: usize = 24;
const FIELD_DESCRIPTOR_SIZE: usize = 89;
const RECORD_HEADER_SIZE: usize = 4;
const MARKER_TEXT_LENGTH: usize = 50;
const MARKER_RECORD_SIZE: usize = RECORD_HEADER_SIZE + MARKER_TEXT_LENGTH;

const RECORD_TYPE_SAMPLE: u8 = 0;
const RECORD_TYPE_MARKER: u8 = 1;

const MAGIC: &[u8; 5] = b"MLVLG";

/// A channel's storage type in an MLG descriptor.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
#[repr(u8)]
pub enum MlgDataType {
    U08 = 0,
    S08 = 1,
    U16 = 2,
    S16 = 3,
    U32 = 4,
    S32 = 5,
    S64 = 6,
    F32 = 7,
    /// A packed byte of boolean flags. Descriptors carry no name, and the entry
    /// occupies a single payload byte.
    Bits = 16,
}

impl MlgDataType {
    /// The type for a descriptor byte, or `None` where the C# enum cast would
    /// land on an undefined value (which its `SizeOf` then rejects).
    pub fn from_byte(b: u8) -> Option<Self> {
        Some(match b {
            0 => Self::U08,
            1 => Self::S08,
            2 => Self::U16,
            3 => Self::S16,
            4 => Self::U32,
            5 => Self::S32,
            6 => Self::S64,
            7 => Self::F32,
            16 => Self::Bits,
            _ => return None,
        })
    }

    fn size(self) -> usize {
        match self {
            Self::U08 | Self::S08 | Self::Bits => 1,
            Self::U16 | Self::S16 => 2,
            Self::U32 | Self::S32 | Self::F32 => 4,
            Self::S64 => 8,
        }
    }
}

struct MlgField {
    ty: MlgDataType,
    name: String,
    units: String,
    scale: f64,
    transform: f64,
    digits: i32,
    offset: usize,
}

/// The reader for TunerStudio / MegaSquirt `.mlg` files.
#[derive(Clone, Copy, Debug, Default)]
pub struct MlgReader;

impl MlgReader {
    pub fn new() -> Self {
        Self
    }
}

impl LogReader for MlgReader {
    fn format_name(&self) -> &str {
        "MLG"
    }

    fn can_read(&self, path: &Path) -> bool {
        let Ok(file) = std::fs::File::open(path) else {
            return false;
        };
        let mut head = Vec::with_capacity(5);
        match file.take(5).read_to_end(&mut head) {
            Ok(_) => head == MAGIC,
            Err(_) => false,
        }
    }

    fn read(&self, path: &Path) -> Result<LogDocument> {
        let data = std::fs::read(path)?;
        if data.len() < FILE_HEADER_SIZE || &data[0..5] != MAGIC {
            return Err(Error::LogFormat("Not an MLVLG file: bad magic.".to_string()));
        }

        let version = be_u16(&data, 6);
        let stamp = i64::from(be_u32(&data, 8));
        let info_start = i64::from(be_u32(&data, 12) as i32);
        let data_start = i64::from(be_u32(&data, 16) as i32);
        let declared_payload = usize::from(be_u16(&data, 20));
        let field_count = usize::from(be_u16(&data, 22));

        if field_count == 0 || data_start <= 0 || data_start > data.len() as i64 {
            return Err(Error::LogFormat(format!(
                "MLG header is not usable (channels={field_count}, dataStart={data_start})."
            )));
        }
        let data_start = data_start as usize;

        if FILE_HEADER_SIZE + field_count * FIELD_DESCRIPTOR_SIZE > data.len() {
            return Err(Error::LogFormat(
                "MLG channel descriptors run past end of file.".to_string(),
            ));
        }

        let (fields, payload_size) = read_descriptors(&data, field_count)?;

        // The descriptor table and the declared record length must agree. If they
        // diverge, channel offsets are wrong and every value past the mismatch
        // would be silently garbage, so fail loudly instead.
        if declared_payload != payload_size {
            return Err(Error::LogFormat(format!(
                "MLG channel table sums to {payload_size} bytes but the header declares \
                 {declared_payload}. The file uses an unsupported channel layout."
            )));
        }

        let stride = resolve_record_stride(&data, data_start, payload_size)?;
        let walked = walk(&data, data_start, stride);
        let sample_count = walked.samples;

        // The time field is decoded a second time at full precision; every other
        // channel goes straight into its float column, so a log is never staged
        // as doubles.
        let time_field = find_time_field(&fields);

        let mut columns: Vec<Vec<f32>> = (0..field_count).map(|_| vec![0.0f32; sample_count]).collect();
        let mut precise_time = time_field.map(|_| vec![0.0f64; sample_count]);

        decode_samples(
            &data,
            data_start,
            stride,
            &fields,
            &mut columns,
            precise_time.as_deref_mut(),
            time_field,
            sample_count,
        );

        let channels: Vec<Arc<LogChannel>> = fields
            .iter()
            .zip(columns)
            .map(|(f, column)| Arc::new(LogChannel::adopt(&f.name, &f.units, f.digits, column)))
            .collect();

        let time = resolve_time_base(&fields, precise_time, time_field, sample_count);
        let (signature, info) = read_info_block(&data, info_start, data_start);
        let markers = read_markers(&data, data_start, stride, &walked.markers, &time);

        let recorded_at = (stamp > 0).then(|| SystemTime::UNIX_EPOCH + Duration::from_secs(stamp as u64));

        Ok(LogDocument::new(
            &path.to_string_lossy(),
            &format!("MLG v{version}"),
            channels,
            Arc::new(time),
        )
        .with_markers(markers)
        .with_signature(signature)
        .with_capture_info(info)
        .with_embedded_tune(read_embedded_tune(&data, info_start, data_start))
        .with_recorded_at(recorded_at))
    }
}

fn be_u16(b: &[u8], o: usize) -> u16 {
    u16::from_be_bytes([b[o], b[o + 1]])
}

fn be_u32(b: &[u8], o: usize) -> u32 {
    u32::from_be_bytes([b[o], b[o + 1], b[o + 2], b[o + 3]])
}

fn be_f32(b: &[u8], o: usize) -> f32 {
    f32::from_bits(be_u32(b, o))
}

#[allow(clippy::too_many_arguments)]
fn decode_samples(
    data: &[u8],
    data_start: usize,
    stride: usize,
    fields: &[MlgField],
    columns: &mut [Vec<f32>],
    mut precise_time: Option<&mut [f64]>,
    time_field: Option<usize>,
    sample_count: usize,
) {
    let mut row = 0usize;
    let mut o = data_start;
    while row < sample_count && o < data.len() {
        let ty = data[o];
        if ty == RECORD_TYPE_MARKER {
            o += MARKER_RECORD_SIZE;
            continue;
        }
        if ty != RECORD_TYPE_SAMPLE || o + stride > data.len() {
            break;
        }

        let payload = o + RECORD_HEADER_SIZE;
        for (fd, column) in fields.iter().zip(columns.iter_mut()) {
            column[row] = ((read_raw(data, payload + fd.offset, fd.ty) + fd.transform) * fd.scale) as f32;
        }

        // Repeated rather than branched on inside the loop above: one extra
        // field read per row costs less than a test against every field.
        if let (Some(times), Some(tf)) = (precise_time.as_deref_mut(), time_field) {
            let tf = &fields[tf];
            times[row] = (read_raw(data, payload + tf.offset, tf.ty) + tf.transform) * tf.scale;
        }

        row += 1;
        o += stride;
    }
}

struct Walked {
    samples: usize,
    markers: Vec<usize>,
    leftover: usize,
}

/// Walks the record stream, counting samples and noting marker positions.
/// Stops cleanly at an unrecognised record type so truncated logs still load.
fn walk(data: &[u8], data_start: usize, stride: usize) -> Walked {
    let mut samples = 0usize;
    let mut markers = Vec::new();

    let mut o = data_start;
    while o < data.len() {
        let ty = data[o];
        if ty == RECORD_TYPE_SAMPLE {
            if o + stride > data.len() {
                break;
            }
            samples += 1;
            o += stride;
        } else if ty == RECORD_TYPE_MARKER {
            if o + MARKER_RECORD_SIZE > data.len() {
                break;
            }
            markers.push(o);
            o += MARKER_RECORD_SIZE;
        } else {
            break;
        }
    }
    Walked {
        samples,
        markers,
        leftover: data.len() - o,
    }
}

/// The header's record-length field counts only the payload; observed files
/// append a trailing checksum byte. Rather than hard-coding that, pick the
/// stride whose record walk consumes the file most completely.
fn resolve_record_stride(data: &[u8], data_start: usize, payload_size: usize) -> Result<usize> {
    let mut best_stride: Option<usize> = None;
    let mut best_samples = 0usize;
    let mut best_leftover = usize::MAX;

    for extra in 0..=4 {
        let stride = RECORD_HEADER_SIZE + payload_size + extra;
        let w = walk(data, data_start, stride);
        if w.samples == 0 {
            continue;
        }

        if w.leftover < best_leftover || (w.leftover == best_leftover && w.samples > best_samples) {
            best_leftover = w.leftover;
            best_samples = w.samples;
            best_stride = Some(stride);
        }
    }

    best_stride.ok_or_else(|| Error::LogFormat("Could not determine the MLG record stride.".to_string()))
}

fn read_descriptors(data: &[u8], count: usize) -> Result<(Vec<MlgField>, usize)> {
    let mut fields = Vec::with_capacity(count);
    let mut offset = 0usize;
    let mut bit_field_seq = 0;

    for i in 0..count {
        let o = FILE_HEADER_SIZE + i * FIELD_DESCRIPTOR_SIZE;
        let Some(ty) = MlgDataType::from_byte(data[o]) else {
            return Err(Error::LogFormat(format!(
                "Channel {i} declares unknown data type {}.",
                data[o]
            )));
        };
        let size = ty.size();

        // Packed flag bytes carry no descriptor name, but still occupy a
        // payload byte and must be numbered so later offsets stay aligned.
        let is_bits = ty == MlgDataType::Bits;

        let fallback = if is_bits {
            bit_field_seq += 1;
            format!("Flags {bit_field_seq}")
        } else {
            format!("Channel {i}")
        };

        fields.push(MlgField {
            ty,
            name: read_string(data, o + 1, 34, &fallback),
            units: read_string(data, o + 35, 11, if is_bits { "bits" } else { "" }),
            scale: if is_bits { 1.0 } else { decimalise(be_f32(data, o + 46)) },
            transform: if is_bits { 0.0 } else { decimalise(be_f32(data, o + 50)) },
            digits: if is_bits { 0 } else { i32::from(data[o + 54]) },
            offset,
        });
        offset += size;
    }

    Ok((fields, offset))
}

/// Markers carry a logger tick rather than a seconds value, so each is dated
/// from the sample that immediately precedes it.
fn read_markers(data: &[u8], data_start: usize, stride: usize, offsets: &[usize], time: &LogChannel) -> Vec<LogMarker> {
    let mut markers = Vec::with_capacity(offsets.len());
    if offsets.is_empty() {
        return markers;
    }

    let mut row = 0usize;
    let mut next = 0usize;
    let mut o = data_start;
    while o < data.len() && next < offsets.len() {
        if o == offsets[next] {
            let from = o + RECORD_HEADER_SIZE;
            let end = from + MARKER_TEXT_LENGTH.min(data.len() - from);
            let text = String::from_utf8_lossy(&data[from..end]);
            let text = text.trim_end_matches('\0').trim();

            if !text.is_empty() {
                markers.push(LogMarker {
                    time: time.at(row.saturating_sub(1)),
                    text: text.to_string(),
                });
            }

            next += 1;
            o += MARKER_RECORD_SIZE;
        } else if data[o] == RECORD_TYPE_SAMPLE && o + stride <= data.len() {
            row += 1;
            o += stride;
        } else if data[o] == RECORD_TYPE_MARKER && o + MARKER_RECORD_SIZE <= data.len() {
            o += MARKER_RECORD_SIZE;
        } else {
            break;
        }
    }
    markers
}

fn find_time_field(fields: &[MlgField]) -> Option<usize> {
    fields
        .iter()
        .position(|f| crate::model::eq_ignore_case(&f.name, "Time"))
}

/// Built from the separately decoded doubles rather than a stored channel: a
/// time base keeps full precision, because it accumulates over the recording.
fn resolve_time_base(
    fields: &[MlgField],
    seconds: Option<Vec<f64>>,
    time_field: Option<usize>,
    sample_count: usize,
) -> LogChannel {
    // A time column that never moves is no use as a time base.
    if let (Some(seconds), Some(tf)) = (&seconds, time_field)
        && seconds.len() > 1
        && seconds[seconds.len() - 1] > seconds[0]
    {
        let f = &fields[tf];
        return LogChannel::new(&f.name, &f.units, f.digits, seconds, true);
    }

    // Fall back to a synthetic index when the log has no usable Time column.
    let synthetic: Vec<f64> = (0..sample_count).map(|i| i as f64).collect();
    LogChannel::new("Sample", "#", 0, &synthetic, true)
}

/// The info block holds quoted metadata strings followed by an embedded copy
/// of the tune; only the leading quoted strings are of interest here.
fn read_info_block(data: &[u8], start: i64, end: usize) -> (Option<String>, Option<String>) {
    if start <= 0 || start as usize >= end || end > data.len() {
        return (None, None);
    }
    let start = start as usize;

    let text = String::from_utf8_lossy(&data[start..start + (end - start).min(4096)]);

    let mut quoted: Vec<String> = Vec::new();
    let mut i = 0usize;
    while quoted.len() < 2 {
        let Some(open) = text[i..].find('"').map(|p| p + i) else {
            break;
        };
        let Some(close) = text[open + 1..].find('"').map(|p| p + open + 1) else {
            break;
        };
        quoted.push(text[open + 1..close].trim().to_string());
        i = close + 1;
    }

    let mut it = quoted.into_iter();
    (it.next(), it.next())
}

/// Pulls the MSQ tune out of the info block. It is declared as ISO-8859-1
/// and sits between the metadata strings and the first data record.
fn read_embedded_tune(data: &[u8], start: i64, end: usize) -> Option<String> {
    if start <= 0 || start as usize >= end || end > data.len() {
        return None;
    }
    let block = &data[start as usize..end];

    let open = find_bytes(block, b"<?xml", 0)?;
    let close = find_bytes(block, b"</msq>", open)?;
    Some(block[open..close + 6].iter().map(|&b| char::from(b)).collect())
}

fn find_bytes(haystack: &[u8], needle: &[u8], from: usize) -> Option<usize> {
    if from > haystack.len() {
        return None;
    }
    haystack[from..]
        .windows(needle.len())
        .position(|w| w == needle)
        .map(|p| p + from)
}

/// Recovers the decimal a scale was authored as.
///
/// Descriptors store the scale as a 32-bit float, so a channel scaled by 0.1
/// holds 0.100000001490116. Widening that to double and multiplying carries
/// the error into every sample: a raw 341 decodes to 34.10000228 rather than
/// the 34.1 the logger meant, which is a rounding step away and reads as
/// noise once it is written to a file. Round-tripping through the float's
/// shortest decimal gives back 0.1, and the sample lands on the nearest
/// float to 34.1.
fn decimalise(value: f32) -> f64 {
    // `{}` on an f32 is its shortest round-trip text, as .NET's "R" is.
    format!("{value}").parse::<f64>().unwrap_or(f64::NAN)
}

fn read_raw(b: &[u8], o: usize, ty: MlgDataType) -> f64 {
    match ty {
        MlgDataType::U08 | MlgDataType::Bits => f64::from(b[o]),
        MlgDataType::S08 => f64::from(b[o] as i8),
        MlgDataType::U16 => f64::from(be_u16(b, o)),
        MlgDataType::S16 => f64::from(be_u16(b, o) as i16),
        MlgDataType::U32 => f64::from(be_u32(b, o)),
        MlgDataType::S32 => f64::from(be_u32(b, o) as i32),
        MlgDataType::S64 => {
            let v = i64::from_be_bytes([
                b[o],
                b[o + 1],
                b[o + 2],
                b[o + 3],
                b[o + 4],
                b[o + 5],
                b[o + 6],
                b[o + 7],
            ]);
            v as f64
        }
        MlgDataType::F32 => f64::from(be_f32(b, o)),
    }
}

fn read_string(b: &[u8], offset: usize, max_length: usize, fallback: &str) -> String {
    let mut n = 0;
    while n < max_length && b[offset + n] != 0 {
        n += 1;
    }
    let s = String::from_utf8_lossy(&b[offset..offset + n]);
    let s = s.trim();
    if s.is_empty() {
        fallback.to_string()
    } else {
        s.to_string()
    }
}
