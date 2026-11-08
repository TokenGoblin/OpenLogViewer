//! The C# `SettingsReviewTests`: the settings interface read against the awkward cases, each of
//! which passed every other test in this suite while being wrong.
//!
//! Ten of the twelve are here. The other two (`ADerivedChannelKeepsItsNumberRatherThanBecomingOne`
//! and `ADerivedChannelThatRefersToItselfStillAnswersNothing`) are about derived channels and are
//! ported in `olv_core::math::derived_channels`.

use olv_core::ini::ini_defines;
use olv_core::ini::msq_ini::SymbolSet;
use olv_core::ini::tune_dialogs::read_curves;
use olv_core::ini::tune_layout::{self, TuneConstant};

fn constant(ini: &str, name: &str) -> TuneConstant {
    tune_layout::read(ini, None)
        .constants
        .into_iter()
        .find(|c| c.name == name)
        .unwrap_or_else(|| panic!("no constant {name}"))
}

// ----- option lists -------------------------------------------------------

#[test]
fn a_bit_field_naming_nothing_offers_no_choices() {
    // Both list splitters end by yielding whatever follows the last comma, so an empty list came
    // back as one blank label. A setting with one blank option is drawn as a list to pick from
    // with nothing in it -- it cannot be read or set, where a plain number box would have worked.
    let ini = r#"[Constants]
page = 1
nPages = 1
pageSize = 64
   reserved = bits, U08, 5, [0:1]
"#;

    let reserved = constant(ini, "reserved");

    assert!(reserved.options.is_empty());
    assert!(!reserved.has_options());
}

#[test]
fn a_blank_label_inside_a_list_is_still_kept() {
    // The opposite case, and the reason the fix is about the whole list being empty rather than
    // about dropping blanks: the position of a label is the number the ECU stores, so a gap left
    // out renumbers every choice after it.
    let ini = r#"[Constants]
page = 1
nPages = 1
pageSize = 64
   mode = bits, U08, 5, [0:1], "Off", "", "On"
"#;

    let mode = constant(ini, "mode");

    assert_eq!(mode.options.len(), 3);
    assert_eq!(mode.option_name(2.0), "On");
}

#[test]
fn a_named_list_is_taken_from_the_branch_this_build_uses() {
    // A firmware commonly writes the same list once per board. Read without the preprocessor the
    // last one wins whatever build this is, which gives correctly numbered options carrying
    // another board's labels -- worse than none, because the page looks right and reads wrong.
    let ini = r#"#if CAN_COMMANDS
#define pinNames = "CAN0", "CAN1"
#else
#define pinNames = "Serial0", "Serial1"
#endif
"#;

    let with = SymbolSet::ordinal(["CAN_COMMANDS"]);
    assert_eq!(
        ini_defines::read(ini, Some(&with)).get("pinNames").unwrap(),
        &["CAN0", "CAN1"]
    );

    let without = SymbolSet::ordinal(Vec::<String>::new());
    assert_eq!(
        ini_defines::read(ini, Some(&without)).get("pinNames").unwrap(),
        &["Serial0", "Serial1"]
    );
}

// ----- curves -------------------------------------------------------------

#[test]
fn a_curve_does_not_pick_up_the_bins_of_a_table_below_it() {
    // [TableEditor] follows [CurveEditor] in every real definition and spells its bin constants
    // with the very same keys. Reading the whole file left the last curve open past the end of
    // its section, so each table after it overwrote the curve's axes with its own.
    let ini = r#"[CurveEditor]
   curve = warmupCurve, "Warmup enrichment"
      columnLabel = "Coolant", "Enrichment"
      xBins = wueBins, coolant
      yBins = wueRates

[TableEditor]
   table = veTable1Tbl, veTable1Map, "VE Table", 2
      xBins = rpmBins, rpm
      yBins = mapBins, map
      zBins = veTable
"#;

    let curves = read_curves(ini, None);
    let warmup = curves.get("warmupCurve").unwrap();

    assert_eq!(warmup.x_bins, "wueBins");
    assert_eq!(warmup.y_bins, "wueRates");
    assert_eq!(warmup.x_label, "Coolant");
}

#[test]
fn a_curve_only_in_the_other_build_is_not_read() {
    let ini = r#"[CurveEditor]
#if CELSIUS
   curve = warmupC, "Warmup (C)"
      xBins = wueBinsC
      yBins = wueRates
#else
   curve = warmupF, "Warmup (F)"
      xBins = wueBinsF
      yBins = wueRates
#endif
"#;

    let curves = read_curves(ini, Some(&SymbolSet::ordinal(["CELSIUS"])));

    assert!(curves.contains_key("warmupC"));
    assert!(!curves.contains_key("warmupF"));
}

// ----- found by reading the real definition files -------------------------

#[test]
fn a_redefined_list_means_the_one_it_had_a_moment_ago() {
    // How a firmware grows a list. MS3 declares eight CANPWM pins and then redefines the same
    // name as "INVALID", the old list, and some more. Gathering every definition first and
    // resolving afterwards makes that second one refer to itself, and the loop guard then leaves
    // the literal "$PIN_DIGOUT_CANPWM" standing where eight labels belong -- so every pin after
    // it is numbered seven short and choosing one sets another.
    let defines = ini_defines::read(
        r#"#define PIN_DIGOUT_CANPWM = "CANPWM1", "CANPWM2"
#define PIN_DIGOUT_CANPWM = "INVALID", $PIN_DIGOUT_CANPWM, "CANOUT1"
"#,
        None,
    );

    assert_eq!(
        defines.get("PIN_DIGOUT_CANPWM").unwrap(),
        &["INVALID", "CANPWM1", "CANPWM2", "CANOUT1"]
    );
}

#[test]
fn a_real_self_reference_is_still_left_as_written() {
    // With nothing declared before it there is no earlier meaning to take, and a definition with
    // a mistake in it should be visible rather than expanded until the stack is gone.
    let defines = ini_defines::read("#define loop = \"A\", $loop\n", None);

    assert_eq!(defines.get("loop").unwrap(), &["A", "$loop"]);
}

#[test]
fn a_semicolon_inside_a_label_is_not_the_start_of_a_comment() {
    // Speeduino's log separator, verbatim. Cutting at the first semicolon whatever is around it
    // left this setting with one option consisting of a single quote mark -- neither readable
    // nor settable.
    let ini = r#"[Constants]
page = 1
nPages = 1
pageSize = 128
   onboard_log_csv_separator = bits, U08, 116, [0:1], ";", ",", "tab", "space"
"#;

    let separator = constant(ini, "onboard_log_csv_separator");

    assert_eq!(separator.options, [";", ",", "tab", "space"]);
}

#[test]
fn a_real_comment_is_still_cut_off() {
    let ini = r#"[Constants]
page = 1
nPages = 1
pageSize = 128
   mode = bits, U08, 5, [0:1], "Off", "On" ; two of them
"#;

    assert_eq!(constant(ini, "mode").options, ["Off", "On"]);
}

#[test]
fn a_list_of_nothing_but_blanks_is_no_list_at_all() {
    // Speeduino spells one reserved field with a single empty label, which is a seven-bit number
    // wearing a dropdown with nothing in it.
    let ini = r#"[Constants]
page = 1
nPages = 1
pageSize = 256
   unused10_182 = bits, U08, 183, [1:7], ""
"#;

    assert!(constant(ini, "unused10_182").options.is_empty());
}
