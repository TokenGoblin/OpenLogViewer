//! Ported from `TuneCurveEdit.cs`. Owned by package `w4-tune`.
//!
//! A curve out of the ECU, ready to be looked at and changed.
//!
//! The other half of tuning, beside the tables, and the half this application has been unable
//! to open. Warmup enrichment, cranking pulsewidth, injector dead time against battery voltage,
//! the AFR target against RPM: an MS3 declares 135 of these and a MicroSquirt 37, and on the
//! latter 23 of the 131 entries in the settings menu point at one. Until now every one of
//! those opened nothing at all.
//!
//! **Both rows are editable, which is what makes a curve not a table.** A table's axes are
//! breakpoints you rarely touch and its values are the tuning; a curve is a line you drag, and
//! moving a point sideways is as ordinary as moving it up. So the breakpoints are an editable
//! row here rather than a fixed header, and they are written back like any other array.
//!
//! Nothing here reaches the ECU. It works out what the bytes would become and hands them over
//! as writes, exactly as the table editor does.

use super::ecu_tune::{EcuTune, TuneWrite};
use super::tune_edit::dotnet_clamp;
use crate::ini::tune_dialogs::TuneCurve;
use crate::ini::tune_layout::TuneConstant;
use crate::model::Array2;

/// One point of a curve: where it sits along the bottom, and how high.
#[derive(Clone, Copy, Debug, PartialEq)]
pub struct CurvePoint {
    /// The breakpoint.
    pub x: f64,
    /// The value there.
    pub y: f64,
}

/// A curve out of the ECU, ready to be looked at and changed. See the module docs.
#[derive(Clone, Debug)]
pub struct TuneCurveEdit {
    curve: TuneCurve,
    x: Vec<f64>,
    y: Vec<f64>,
    original_x: Vec<f64>,
    original_y: Vec<f64>,
    x_constant: Option<TuneConstant>,
    y_constant: Option<TuneConstant>,
}

impl TuneCurveEdit {
    /// Builds one, or nothing when the tune cannot produce this curve.
    ///
    /// Refused rather than half-drawn when the two rows are different lengths: a curve
    /// assembled from mismatched parts is not a near miss, it is a line whose points are in
    /// the wrong places.
    pub fn for_curve(curve: &TuneCurve, tune: &EcuTune) -> Option<TuneCurveEdit> {
        if !curve.is_usable() {
            return None;
        }

        let x = tune.array(&curve.x_bins)?;
        let y = tune.array(&curve.y_bins)?;

        if x.is_empty() || x.len() != y.len() {
            return None;
        }

        Some(TuneCurveEdit {
            curve: curve.clone(),
            original_x: x.clone(),
            original_y: y.clone(),
            x,
            y,
            x_constant: tune.constant(&curve.x_bins).cloned(),
            y_constant: tune.constant(&curve.y_bins).cloned(),
        })
    }

    pub fn curve(&self) -> &TuneCurve {
        &self.curve
    }

    /// The constant holding the breakpoints, for its units and its range.
    pub fn x_constant(&self) -> Option<&TuneConstant> {
        self.x_constant.as_ref()
    }

    pub fn y_constant(&self) -> Option<&TuneConstant> {
        self.y_constant.as_ref()
    }

    pub fn name(&self) -> &str {
        &self.curve.name
    }

    /// What to head it with, falling back to the name where it has no title.
    pub fn title(&self) -> &str {
        if self.curve.title.is_empty() {
            &self.curve.name
        } else {
            &self.curve.title
        }
    }

    pub fn x_label(&self) -> &str {
        if self.curve.x_label.is_empty() {
            &self.curve.x_bins
        } else {
            &self.curve.x_label
        }
    }

    pub fn y_label(&self) -> &str {
        if self.curve.y_label.is_empty() {
            &self.curve.y_bins
        } else {
            &self.curve.y_label
        }
    }

    pub fn x_units(&self) -> &str {
        self.x_constant.as_ref().map_or("", |c| c.units.as_str())
    }

    pub fn y_units(&self) -> &str {
        self.y_constant.as_ref().map_or("", |c| c.units.as_str())
    }

    pub fn count(&self) -> usize {
        self.x.len()
    }

    pub fn x_digits(&self) -> i32 {
        self.x_constant.as_ref().map_or(0, |c| c.digits)
    }

    pub fn y_digits(&self) -> i32 {
        self.y_constant.as_ref().map_or(0, |c| c.digits)
    }

    /// The points as they now stand.
    pub fn points(&self) -> Vec<CurvePoint> {
        self.x.iter().zip(&self.y).map(|(&x, &y)| CurvePoint { x, y }).collect()
    }

    pub fn x(&self, index: usize) -> f64 {
        self.x[index]
    }

    pub fn y(&self, index: usize) -> f64 {
        self.y[index]
    }

    pub fn original_x(&self, index: usize) -> f64 {
        self.original_x[index]
    }

    pub fn original_y(&self, index: usize) -> f64 {
        self.original_y[index]
    }

    pub fn is_changed(&self, index: usize) -> bool {
        (self.x[index] - self.original_x[index]).abs() > 1e-9 || (self.y[index] - self.original_y[index]).abs() > 1e-9
    }

    pub fn has_changes(&self) -> bool {
        (0..self.count()).any(|i| self.is_changed(i))
    }

    pub fn changed_count(&self) -> usize {
        (0..self.count()).filter(|&i| self.is_changed(i)).count()
    }

    /// Whether `index` is a point of this curve; the C#'s signed indexes may be anything.
    fn valid(&self, index: i32) -> Option<usize> {
        (index >= 0 && (index as usize) < self.count()).then_some(index as usize)
    }

    /// Moves a point's value, held to what the firmware says it may be.
    ///
    /// Clamped rather than refused, because this is a line being dragged: a pointer that runs
    /// past the top of the plot should stop at the top, not abandon the drag.
    pub fn set_y(&mut self, index: i32, value: f64) {
        let Some(i) = self.valid(index) else { return };
        if !value.is_finite() {
            return;
        }

        self.y[i] = clamp(value, self.y_constant.as_ref());
    }

    /// Moves a breakpoint, held both to the firmware's range and to its neighbours.
    ///
    /// **Breakpoints must stay in order.** The ECU interpolates between consecutive entries
    /// and takes the row as ascending; one that overtakes its neighbour makes the lookup read
    /// backwards over that span, which is not an error anywhere: it is a curve that does the
    /// opposite of what it shows for one stretch of its range.
    pub fn set_x(&mut self, index: i32, value: f64) {
        let Some(i) = self.valid(index) else { return };
        if !value.is_finite() {
            return;
        }

        let low = if i > 0 { self.x[i - 1] } else { f64::NEG_INFINITY };
        let high = if i < self.count() - 1 {
            self.x[i + 1]
        } else {
            f64::INFINITY
        };

        // The neighbours are not always the right way round. A row that was never configured,
        // or one a firmware genuinely writes descending, gives a lower bound above the upper,
        // and Math.Clamp throws on that rather than doing nothing, which on a drag is the
        // application going down under the pointer. This class's own doc says such a row is not
        // an error, so it has to be tolerated: where the bounds are inverted there is no room
        // between the neighbours to move into, and the point stays.
        if low > high {
            return;
        }

        self.x[i] = dotnet_clamp(clamp(value, self.x_constant.as_ref()), low, high);
    }

    /// Adds to a point's value.
    pub fn add_y(&mut self, index: i32, delta: f64) {
        if let Some(i) = self.valid(index) {
            self.set_y(index, self.y[i] + delta);
        }
    }

    /// Scales a point's value by a percentage, 100 meaning no change.
    pub fn scale_y(&mut self, index: i32, percent: f64) {
        if let Some(i) = self.valid(index)
            && percent.is_finite()
        {
            self.set_y(index, self.y[i] * percent / 100.0);
        }
    }

    /// Draws a straight line between two points, moving everything between them onto it.
    ///
    /// The one bulk operation a curve wants: a warmup line that has been poked at cell by cell
    /// ends up lumpy, and a lump in an enrichment curve is a stumble at one coolant
    /// temperature.
    pub fn interpolate(&mut self, from: i32, to: i32) -> bool {
        let (from, to) = if from > to { (to, from) } else { (from, to) };
        if from < 0 || to as i64 >= self.count() as i64 || to - from < 2 {
            return false;
        }

        let (from_i, to_i) = (from as usize, to as usize);
        let span = self.x[to_i] - self.x[from_i];

        for i in (from_i + 1)..to_i {
            // Along the breakpoints where they are spread unevenly, which is usual: a warmup
            // curve is dense where it is cold. Falling back to position where two breakpoints
            // coincide, since the line then has no width to interpolate along.
            let t = if span.abs() > 1e-9 {
                (self.x[i] - self.x[from_i]) / span
            } else {
                (i - from_i) as f64 / (to_i - from_i) as f64
            };

            self.set_y(i as i32, self.y[from_i] + ((self.y[to_i] - self.y[from_i]) * t));
        }

        true
    }

    /// Puts one point back to what the ECU holds.
    pub fn revert_point(&mut self, index: i32) {
        let Some(i) = self.valid(index) else { return };

        self.x[i] = self.original_x[i];
        self.y[i] = self.original_y[i];
    }

    /// Puts the whole curve back.
    pub fn revert(&mut self) {
        self.x.copy_from_slice(&self.original_x);
        self.y.copy_from_slice(&self.original_y);
    }

    /// The bytes this would write, or nothing where either row cannot be encoded.
    ///
    /// Both rows go together or neither does. Sending the values without the breakpoints they
    /// were drawn against leaves the ECU interpolating the new numbers over the old axis, which
    /// is a different curve from the one on screen and nothing would say so.
    pub fn encode(&self, tune: &EcuTune) -> Option<Vec<TuneWrite>> {
        let mut writes = Vec::new();

        if changed(&self.x, &self.original_x) {
            writes.push(tune.encode_table(&self.curve.x_bins, &to_grid(&self.x))?);
        }

        if changed(&self.y, &self.original_y) {
            writes.push(tune.encode_table(&self.curve.y_bins, &to_grid(&self.y))?);
        }

        Some(writes)
    }
}

fn changed(now: &[f64], was: &[f64]) -> bool {
    now.iter().zip(was).any(|(v, w)| (v - w).abs() > 1e-9)
}

/// A row as the one-column grid the array encoder takes.
fn to_grid(values: &[f64]) -> Array2<f64> {
    let mut grid = Array2::new(values.len(), 1);
    for (i, v) in values.iter().enumerate() {
        grid[(i, 0)] = *v;
    }

    grid
}

fn clamp(value: f64, constant: Option<&TuneConstant>) -> f64 {
    match constant {
        Some(c) if c.has_range() => dotnet_clamp(value, c.low, c.high),
        _ => value,
    }
}
