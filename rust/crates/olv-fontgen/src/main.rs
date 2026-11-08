//! `cargo run -p olv-fontgen [-- <source.txt> <out.ttf> <preview.png>]` regenerates the font and
//! its preview. With no arguments it reads and writes `rust/assets/fonts`.

use std::path::PathBuf;

fn run() -> Result<(), Box<dyn std::error::Error>> {
    let fonts = PathBuf::from(env!("CARGO_MANIFEST_DIR")).join("../../assets/fonts");
    let mut args = std::env::args_os().skip(1);
    let mut next = |default: PathBuf| args.next().map_or(default, PathBuf::from);
    let source = next(fonts.join("olv-retro-8x16.txt"));
    let ttf_path = next(fonts.join("OLVRetro-8x16.ttf"));
    let png_path = next(fonts.join("preview.png"));

    let glyphs = olv_fontgen::parse_source(&std::fs::read_to_string(&source)?)?;
    let ttf = olv_fontgen::build_ttf(&glyphs)?;
    let png = olv_fontgen::encode_png(&olv_fontgen::render_preview(&ttf, &glyphs)?)?;
    std::fs::write(&ttf_path, &ttf)?;
    std::fs::write(&png_path, &png)?;
    println!(
        "{} glyphs -> {} ({} bytes), {}",
        glyphs.len(),
        ttf_path.display(),
        ttf.len(),
        png_path.display()
    );
    Ok(())
}

fn main() {
    if let Err(e) = run() {
        eprintln!("olv-fontgen: {e}");
        std::process::exit(1);
    }
}
