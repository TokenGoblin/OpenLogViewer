# The MLG (`MLVLG`) datalog format

This is a clean-room description of the binary datalog format written by
TunerStudio for MegaSquirt ECUs, derived by analysing sample `.mlg` files. The
container is self-describing — channel names, types, units and scaling all live
in the file — so the format can be read without any external definition.

All multi-byte values are **big-endian**.

## File header — 24 bytes

| Offset | Type      | Field |
|--------|-----------|-------|
| 0      | `char[6]` | Magic `"MLVLG\0"` |
| 6      | `u16`     | Format version (observed: 2) |
| 8      | `u32`     | Capture timestamp, Unix seconds |
| 12     | `u32`     | Offset of the info/metadata block |
| 16     | `u32`     | Offset of the first record |
| 20     | `u16`     | Payload size of one data record, in bytes |
| 22     | `u16`     | Channel descriptor count |

## Channel descriptors — 89 bytes each, starting at offset 24

| Offset | Type       | Field |
|--------|------------|-------|
| 0      | `u8`       | Data type (below) |
| 1      | `char[34]` | Name, NUL-padded UTF-8 |
| 35     | `char[11]` | Units, NUL-padded UTF-8 |
| 46     | `f32`      | Scale |
| 50     | `f32`      | Transform |
| 54     | `u8`       | Display decimal places |
| 55     | `byte[34]` | Reserved / zero |

There is **no** `displayStyle` byte before the scale — `scale` begins
immediately after the units field at offset 46.

### Data types

| Value | Type  | Size |
|-------|-------|------|
| 0     | `u8`  | 1 |
| 1     | `s8`  | 1 |
| 2     | `u16` | 2 |
| 3     | `s16` | 2 |
| 4     | `u32` | 4 |
| 5     | `s32` | 4 |
| 6     | `s64` | 8 |
| 7     | `f32` | 4 |
| 16    | packed flag byte | 1 |

Type 16 descriptors carry an **empty name** and units `"bits"`. They are easy to
overlook, but each still consumes one payload byte — skipping them misaligns
every channel that follows.

Channel values are decoded as:

```
value = raw * scale + transform
```

## Records

Every record starts with the same 4-byte header:

| Offset | Type  | Field |
|--------|-------|-------|
| 0      | `u8`  | Record type |
| 1      | `u8`  | Sequence counter, increments by 1 across *all* records |
| 2      | `u16` | Logger tick |

### Type 0 — sample

```
4-byte header | payload (header field @20 bytes) | 1 trailing checksum byte
```

The payload holds every declared channel packed in declaration order with no
alignment padding. The first channel is normally `Time` (`f32`, seconds), and it
is stored in the payload — not derived from the header tick.

Note that the header's record-length field counts **only the payload**, so the
on-disk stride is `4 + payload + 1`.

### Type 1 — marker

```
4-byte header | char[50] annotation text
```

A fixed 54 bytes. The text is truncated to fit and is *not* NUL-terminated when
it fills the field.

Markers are interleaved with samples, so records must be **walked** rather than
indexed at a fixed stride. A log with markers will not divide evenly by the
sample stride.

## Info block

Between the descriptors and the first record sits the info block, which begins
with quoted metadata strings:

```
"MS2Extra comms342a2: MS2/Extra 3.4.2 release ..."
"Capture Date: Fri May 29 15:31:20 EDT 2026, File author: TunerStudio MS ..."
```

followed by an embedded copy of the tune (`.msq` XML). The gap between the end
of the descriptor table and the info offset is not always zero, so trust the
header's info offset rather than computing it.

## Worked example

`2026-05-29_15.31.10.mlg`, 377,679 bytes:

```
version 2, 91 channels, declared payload 194
descriptors: 24 .. 24 + 91*89 = 8123  (== info offset)
channel sizes sum to 194, matching the declared payload
stride = 4 + 194 + 1 = 199
(377679 - 130521) / 199 = 1242 records exactly, 0 bytes left over
```

`2026-07-26_13.23.25.mlg`, 13,009,797 bytes, 179 channels:

```
155 named channels + 24 type-16 flag bytes
39*u8 + 2*s8 + 34*u16 + 59*s16 + 21*f32 = 311
311 + 24*1 = 335 == declared payload
stride = 4 + 335 + 1 = 340
37,328 samples * 340 + 22 markers * 54 = 12,692,708 == data region exactly
```

## Verifying a decode

Physical plausibility is the most reliable check that the layout is right; a
misaligned decode produces values that are obviously impossible:

- `Batt V` sits at 12–14.5 V on a running engine (and dips near 9 V on cranking)
- `CLT` reaches 160–200 °F once warm
- `MAP` stays within roughly 10–250 kPa
- `Time` increases monotonically

A useful trick when the alignment is unknown: scan every byte offset in the
record for one that decodes as `s16 * 0.1` within 11–15 across most records.
That locates battery voltage, and the rest of the layout follows from it.

## Related

- [Documentation index](Home)
- [User guide ▸ Supported log formats](User-guide#supported-log-formats)
- [Histogram and scatter ▸ Axis breakpoints from the tune](Histogram-and-scatter#axis-breakpoints-from-the-tune) — the `.msq` an MLG embeds
- [Development ▸ The dump tool](Development#the-dump-tool)
