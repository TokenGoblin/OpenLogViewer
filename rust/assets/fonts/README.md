# OLV Retro 8x16

The pixel font for the "Retro DOS" theme. Monospaced, 8 px wide, 16 px tall, in the spirit of
1990s DOS/VGA text mode.

- `olv-retro-8x16.txt` is the source: one ASCII-art block per glyph (`#` lit, `.` clear), baseline
  under row 12. Edit this file.
- `OLVRetro-8x16.ttf` is generated from it. Do not edit it by hand.
- `preview.png` is generated too, by rasterizing the TTF (not the source) at 16 px, scaled 2x.

Regenerate the TTF and the preview in one command, from `rust/`:

```
cargo run -p olv-fontgen
```

`cargo test -p olv-fontgen` fails if the committed TTF no longer matches the source.

## Licence

Every glyph was drawn from scratch for this project; none is copied or traced from another font.
The font, the source and the generator are MIT-licensed like the rest of OpenLogViewer.
