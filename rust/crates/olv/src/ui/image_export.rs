//! Ported from `ImageExport.cs`. Owned by package `w6-ui-base`.
//!
//! Saves a view to a PNG.
//!
//! The C# renders the element from the visual tree onto a fresh surface at twice its size. egui
//! has no offscreen render of a widget: its painter only produces shapes for a window to draw. What
//! maps onto it is a screenshot of the viewport (`Context::request_screenshot`) cropped to the
//! view's rectangle, which this module does. The differences, all consequences of that:
//!
//! * The file is at the resolution the window is drawn at, not 2x. Raising the pixel density for
//!   a frame would re-lay out the window (points are what egui lays out in), so the view would not
//!   be the one the user was looking at. A user who wants more pixels raises the interface zoom
//!   first.
//! * A window overlapping the view (a menu, a dialog in an embedded viewport) is in the capture. A
//!   caller that cares closes it for the frame before asking.
//!
//! What is kept from the C# exactly: the crop is rounded outwards so the axis line in the last
//! row and column survives; the view is composited onto the given background first, so a
//! transparent view never saves see-through; the file is written to `<path>.tmp` and renamed over
//! `<path>`, so a half-written PNG is never left where the user asked for one; and a view with no
//! width or height fails with `The view has no size to save.`

use std::io::BufWriter;
use std::path::{Path, PathBuf};
use std::sync::{Arc, Mutex};

use egui::{ColorImage, Context, Rect};
use olv_core::{Error, Result};

use super::color_math::Color;

const NO_SIZE: &str = "The view has no size to save.";

/// Crops a screenshot to a view's rectangle (in points), rounding outwards to whole pixels and
/// clipping to the screenshot. Fails when nothing is left.
pub fn crop(screenshot: &ColorImage, view: Rect, pixels_per_point: f32) -> Result<ColorImage> {
    let [width, height] = screenshot.size;

    let edge = |v: f32, round: fn(f32) -> f32, limit: usize| (round(v * pixels_per_point).max(0.0) as usize).min(limit);
    let (x0, x1) = (edge(view.min.x, f32::floor, width), edge(view.max.x, f32::ceil, width));
    let (y0, y1) = (
        edge(view.min.y, f32::floor, height),
        edge(view.max.y, f32::ceil, height),
    );

    if !view.is_finite() || x1 <= x0 || y1 <= y0 {
        return Err(Error::InvalidOperation(NO_SIZE.to_owned()));
    }

    Ok(screenshot.region_by_pixels([x0, y0], [x1 - x0, y1 - y0]))
}

/// Writes `image` as an opaque RGBA PNG, over `background` when given (a pixel with any
/// transparency is blended onto it). Goes through `<path>.tmp` and a rename.
pub fn save(image: &ColorImage, path: &Path, background: Option<Color>) -> Result<()> {
    let [width, height] = image.size;
    if width == 0 || height == 0 {
        return Err(Error::InvalidOperation(NO_SIZE.to_owned()));
    }

    let mut rgba = Vec::with_capacity(width * height * 4);
    for pixel in &image.pixels {
        // egui pixels are premultiplied: colour + background * (1 - alpha) is "over".
        let (r, g, b, a) = (pixel.r(), pixel.g(), pixel.b(), pixel.a());
        match background {
            Some(bg) => {
                let behind = |bg: u8| ((u32::from(bg) * u32::from(255 - a) + 127) / 255) as u8;
                rgba.extend([
                    r.saturating_add(behind(bg.r)),
                    g.saturating_add(behind(bg.g)),
                    b.saturating_add(behind(bg.b)),
                    255,
                ]);
            }
            None => {
                rgba.extend(egui::Color32::from_rgba_premultiplied(r, g, b, a).to_srgba_unmultiplied());
            }
        }
    }

    // Through a temporary file: a half-written PNG left where the user asked for one is worse than
    // a failure they can see.
    let mut temporary = path.as_os_str().to_owned();
    temporary.push(".tmp");
    let temporary = PathBuf::from(temporary);

    let written = write_png(&temporary, width, height, &rgba);
    if let Err(e) = written {
        let _ = std::fs::remove_file(&temporary);
        return Err(e);
    }
    std::fs::rename(&temporary, path).inspect_err(|_| {
        let _ = std::fs::remove_file(&temporary);
    })?;
    Ok(())
}

fn write_png(path: &Path, width: usize, height: usize, rgba: &[u8]) -> Result<()> {
    let file = BufWriter::new(std::fs::File::create(path)?);
    let mut encoder = png::Encoder::new(file, width as u32, height as u32);
    encoder.set_color(png::ColorType::Rgba);
    encoder.set_depth(png::BitDepth::Eight);

    let to_io = |e: png::EncodingError| match e {
        png::EncodingError::IoError(e) => Error::Io(e),
        other => Error::Io(std::io::Error::other(other.to_string())),
    };
    let mut writer = encoder.write_header().map_err(to_io)?;
    writer.write_image_data(rgba).map_err(to_io)?;
    writer.finish().map_err(to_io)
}

/// The outcome of a [`request`], filled in when the integration has read the pixels (a frame or
/// two later).
#[derive(Clone, Default)]
pub struct Ticket(Arc<Mutex<Option<Result<PathBuf>>>>);

impl Ticket {
    /// The result, once there is one. Takes it: a second call returns `None`.
    pub fn take(&self) -> Option<Result<PathBuf>> {
        self.0.lock().unwrap_or_else(|p| p.into_inner()).take()
    }
}

/// Asks for the view in `view` (points) to be saved to `path` as a PNG, over `background`. The
/// file is written when the next frame's pixels arrive; poll the ticket. A view with no size
/// fails at once, without waiting for a frame.
pub fn request(ctx: &Context, view: Rect, path: PathBuf, background: Option<Color>) -> Ticket {
    let ticket = Ticket::default();

    if !(view.width() > 0.0 && view.height() > 0.0) {
        *ticket.0.lock().unwrap_or_else(|p| p.into_inner()) = Some(Err(Error::InvalidOperation(NO_SIZE.to_owned())));
        return ticket;
    }

    let pixels_per_point = ctx.pixels_per_point();
    let slot = ticket.clone();
    ctx.request_screenshot(move |screenshot| {
        let result = crop(&screenshot, view, pixels_per_point)
            .and_then(|image| save(&image, &path, background))
            .map(|()| path);
        *slot.0.lock().unwrap_or_else(|p| p.into_inner()) = Some(result);
    });
    ctx.request_repaint();
    ticket
}

#[cfg(test)]
mod tests {
    use super::*;
    use egui::{Color32, pos2};

    fn scratch(name: &str) -> PathBuf {
        let dir = std::env::temp_dir().join(format!("olv-export-{}", std::process::id()));
        std::fs::create_dir_all(&dir).unwrap();
        dir.join(name)
    }

    fn read(path: &Path) -> (png::OutputInfo, Vec<u8>) {
        let decoder = png::Decoder::new(std::io::BufReader::new(std::fs::File::open(path).unwrap()));
        let mut reader = decoder.read_info().unwrap();
        let mut buffer = vec![0; reader.output_buffer_size().unwrap()];
        let info = reader.next_frame(&mut buffer).unwrap();
        (info, buffer)
    }

    fn solid(width: usize, height: usize, colour: Color32) -> ColorImage {
        ColorImage::new([width, height], vec![colour; width * height])
    }

    #[test]
    fn a_view_is_written_as_a_png_of_its_own_size() {
        let path = scratch("size.png");
        save(&solid(5, 3, Color32::from_rgb(10, 20, 30)), &path, None).unwrap();

        let (info, pixels) = read(&path);
        assert_eq!((5, 3), (info.width, info.height));
        assert_eq!([10, 20, 30, 255], pixels[..4]);
    }

    #[test]
    fn a_transparent_view_is_composited_onto_the_background_first() {
        // Without this a see-through plot saves as a PNG most viewers show as black.
        let path = scratch("background.png");
        save(&solid(2, 2, Color32::TRANSPARENT), &path, Some(Color::rgb(1, 2, 3))).unwrap();

        let (_, pixels) = read(&path);
        assert_eq!([1, 2, 3, 255], pixels[..4]);
    }

    #[test]
    fn an_opaque_view_covers_the_background() {
        let path = scratch("opaque.png");
        save(
            &solid(2, 2, Color32::from_rgb(200, 100, 50)),
            &path,
            Some(Color::rgb(1, 2, 3)),
        )
        .unwrap();

        let (_, pixels) = read(&path);
        assert_eq!([200, 100, 50, 255], pixels[..4]);
    }

    #[test]
    fn a_half_transparent_view_blends_with_the_background() {
        let path = scratch("half.png");
        // 50% white, premultiplied.
        let half = Color32::from_rgba_premultiplied(128, 128, 128, 128);
        save(&solid(1, 1, half), &path, Some(Color::BLACK)).unwrap();

        let (_, pixels) = read(&path);
        assert_eq!([128, 128, 128, 255], pixels[..4]);
    }

    #[test]
    fn a_view_with_no_size_fails_with_the_message_the_user_sees() {
        let empty = ColorImage::new([0, 0], vec![]);
        let error = save(&empty, &scratch("empty.png"), None).unwrap_err();
        assert_eq!(NO_SIZE, error.to_string());

        let screenshot = solid(10, 10, Color32::WHITE);
        let flat = Rect::from_min_max(pos2(2.0, 2.0), pos2(2.0, 8.0));
        assert_eq!(NO_SIZE, crop(&screenshot, flat, 1.0).unwrap_err().to_string());
    }

    #[test]
    fn nothing_half_written_is_left_behind() {
        let path = scratch("tidy.png");
        save(&solid(4, 4, Color32::RED), &path, None).unwrap();

        let mut temporary = path.as_os_str().to_owned();
        temporary.push(".tmp");
        assert!(!PathBuf::from(temporary).exists());
        assert!(path.exists());
    }

    #[test]
    fn saving_over_an_existing_file_replaces_it() {
        let path = scratch("replace.png");
        save(&solid(2, 2, Color32::RED), &path, None).unwrap();
        save(&solid(3, 3, Color32::BLUE), &path, None).unwrap();

        let (info, pixels) = read(&path);
        assert_eq!((3, 3), (info.width, info.height));
        assert_eq!([0, 0, 255, 255], pixels[..4]);
    }

    #[test]
    fn a_failed_write_leaves_the_old_file_and_no_temporary() {
        let directory = scratch("missing-directory").join("nested");
        let path = directory.join("x.png");
        assert!(save(&solid(2, 2, Color32::RED), &path, None).is_err());
        assert!(!directory.exists());
    }

    #[test]
    fn the_crop_rounds_outwards_so_the_last_row_and_column_survive() {
        let mut screenshot = solid(20, 20, Color32::BLACK);
        screenshot.pixels[10 * 20 + 14] = Color32::WHITE; // x = 14, y = 10

        // 2 pixels per point; the view ends at 7.2 points = 14.4 pixels, so column 14 is in.
        let view = Rect::from_min_max(pos2(1.0, 1.0), pos2(7.2, 5.1));
        let cropped = crop(&screenshot, view, 2.0).unwrap();

        assert_eq!([13, 9], cropped.size, "from 2..15 across and 2..11 down");
        assert_eq!(Color32::WHITE, cropped.pixels[(10 - 2) * 13 + (14 - 2)]);
    }

    #[test]
    fn the_crop_is_clipped_to_the_screenshot() {
        let screenshot = solid(10, 10, Color32::WHITE);
        let view = Rect::from_min_max(pos2(-5.0, 4.0), pos2(50.0, 50.0));
        assert_eq!([10, 6], crop(&screenshot, view, 1.0).unwrap().size);
    }

    #[test]
    fn a_view_outside_the_screenshot_has_nothing_to_save() {
        let screenshot = solid(10, 10, Color32::WHITE);
        let view = Rect::from_min_max(pos2(20.0, 20.0), pos2(30.0, 30.0));
        assert!(crop(&screenshot, view, 1.0).is_err());
    }

    #[test]
    fn requesting_a_view_with_no_size_fails_without_waiting_for_a_frame() {
        let ctx = Context::default();
        let flat = Rect::from_min_max(pos2(0.0, 0.0), pos2(0.0, 40.0));
        let ticket = request(&ctx, flat, scratch("never.png"), None);

        let error = ticket.take().expect("an answer at once").unwrap_err();
        assert_eq!(NO_SIZE, error.to_string());
        assert!(ticket.take().is_none(), "the answer is taken once");
    }
}
