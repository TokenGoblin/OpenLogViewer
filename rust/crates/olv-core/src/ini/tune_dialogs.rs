//! Ported from `TuneDialogs.cs`: the settings interface as the firmware describes
//! it (`[Menu]`, `[UserDefined]`, `[CurveEditor]`).
//!
//! This is the half of an INI that `tune_layout` does not read. `[Constants]` says
//! where every setting lives in the ECU's memory and how to decode it; this says
//! what to call it, which page to put it on, and when it is relevant at all.
//! Neither is any use alone: the constants without this are eight hundred numbers
//! in no order, and this without them is a menu of labels with nothing behind them.
//!
//! Conditions are kept as text. Whether "show this when knock detection is on" is
//! true depends on the tune currently in hand, which the file does not have, so
//! evaluating them is `dialog_condition`'s job and happens against a tune rather
//! than against a definition.

use super::msq_ini::{self, SymbolSet, default_symbols, grp};
use crate::model::IgnoreCaseMap;
use regex::Regex;
use std::sync::LazyLock;

/// What a line inside a dialog puts on screen.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum DialogItemKind {
    /// A setting: a label and the constant behind it.
    Field,
    /// A reading the firmware will not let you change.
    ReadOnlyField,
    /// A label or a spacer: a field with no constant behind it.
    Label,
    /// Another dialog, embedded in this one.
    Panel,
    /// A button that sends a command to the controller.
    Command,
    /// A live gauge.
    Gauge,
    /// Static prose.
    Text,
    /// A setting shown as a slider rather than a box.
    Slider,
    /// Something this does not draw yet: a live graph, a lamp, a curve.
    Unsupported,
}

/// One line of a dialog.
#[derive(Clone, Debug, PartialEq, Eq)]
pub struct DialogItem {
    /// What to draw.
    pub kind: DialogItemKind,
    /// The caption, which is empty for a spacer.
    pub label: String,
    /// What it refers to: a constant for a field or slider, another dialog for a
    /// panel, a command for a button, a gauge for a gauge. Empty for a label.
    pub target: String,
    /// The `{...}` expression deciding whether it is shown, or empty for always.
    /// Held as written rather than parsed here, because whether it is true depends
    /// on values this does not have.
    pub condition: String,
    /// Where a panel goes: North, South, Center and so on.
    pub position: String,
}

impl DialogItem {
    /// An item with no target, condition or position.
    pub fn new(kind: DialogItemKind, label: impl Into<String>) -> Self {
        Self {
            kind,
            label: label.into(),
            target: String::new(),
            condition: String::new(),
            position: String::new(),
        }
    }

    pub fn with_target(mut self, target: impl Into<String>) -> Self {
        self.target = target.into();
        self
    }

    pub fn with_condition(mut self, condition: impl Into<String>) -> Self {
        self.condition = condition.into();
        self
    }

    pub fn with_position(mut self, position: impl Into<String>) -> Self {
        self.position = position.into();
        self
    }

    pub fn has_condition(&self) -> bool {
        !self.condition.is_empty()
    }

    /// True for the two kinds that edit a constant.
    pub fn is_editable(&self) -> bool {
        matches!(self.kind, DialogItemKind::Field | DialogItemKind::Slider)
    }

    /// The constant this refers to, without any subscript.
    ///
    /// A field may address one element of an array (`psEnabled[0]` is the first of
    /// four programmable outputs, each with its own row of settings) and the
    /// constant to look up is the array, not the element.
    pub fn target_constant(&self) -> &str {
        match self.target.find('[') {
            Some(bracket) => &self.target[..bracket],
            None => &self.target,
        }
    }

    /// Which element of the array, or -1 when it addresses the whole of it.
    pub fn target_index(&self) -> i32 {
        let open = self.target.find('[');
        let close = self.target.rfind(']');

        match (open, close) {
            (Some(open), Some(close)) if close > open => {
                crate::fmt::parse_i32(&self.target[open + 1..close]).unwrap_or(-1)
            }
            _ => -1,
        }
    }
}

/// A dialog: a titled group of settings, which may embed others.
#[derive(Clone, Debug, PartialEq, Eq)]
pub struct TuneDialog {
    /// The identifier a menu or a panel refers to it by.
    pub name: String,
    /// What to put at the top, which is often empty for a dialog that exists only
    /// to be embedded.
    pub title: String,
    /// `xAxis` to lay its items out across, `yAxis` down. Declared per dialog, and
    /// the reason a settings page can be two columns wide.
    pub layout: String,
    pub items: Vec<DialogItem>,
    /// Documentation the firmware points at, where it does.
    pub help: String,
}

impl TuneDialog {
    pub fn lays_out_across(&self) -> bool {
        self.layout.eq_ignore_ascii_case("xAxis")
    }
}

/// An entry under a menu: a dialog to open, or a rule to draw.
#[derive(Clone, Debug, PartialEq, Eq)]
pub struct MenuEntry {
    /// The dialog's name, or a `std_` special.
    pub dialog: String,
    /// What the menu says.
    pub title: String,
    /// When it is offered at all.
    pub condition: String,
}

impl MenuEntry {
    pub fn new(dialog: impl Into<String>, title: impl Into<String>, condition: impl Into<String>) -> Self {
        Self {
            dialog: dialog.into(),
            title: title.into(),
            condition: condition.into(),
        }
    }

    /// True for the horizontal rule between groups, which is a menu entry in the
    /// file but not a thing to open.
    pub fn is_separator(&self) -> bool {
        self.dialog.eq_ignore_ascii_case("std_separator")
    }

    /// True for the editors TunerStudio supplies itself rather than the firmware
    /// describing: the generic constants editor, the thermistor generator and so
    /// on. Recognised so they can be left out rather than offered as a dialog that
    /// will not be found.
    pub fn is_built_in(&self) -> bool {
        msq_ini::starts_with_ignore_case(&self.dialog, "std_")
    }

    pub fn has_condition(&self) -> bool {
        !self.condition.is_empty()
    }
}

/// One top-level menu, with the entries under it.
#[derive(Clone, Debug, PartialEq, Eq)]
pub struct TuneMenu {
    pub title: String,
    pub entries: Vec<MenuEntry>,
}

/// The settings interface, as the firmware describes it.
///
/// It is a large thing to read (an MS3 definition declares 633 dialogs and 2,232
/// fields) but it is read rather than written, which is the point. Hard-coding a
/// settings screen would mean one screen per firmware and a new one every release;
/// every controller here describes its own.
#[derive(Clone, Debug)]
pub struct TuneInterface {
    pub menus: Vec<TuneMenu>,
    pub dialogs: IgnoreCaseMap<TuneDialog>,
}

impl TuneInterface {
    /// True when the file described no settings interface at all.
    pub fn is_empty(&self) -> bool {
        self.menus.is_empty() && self.dialogs.is_empty()
    }

    pub fn find(&self, name: &str) -> Option<&TuneDialog> {
        self.dialogs.get(name)
    }
}

/// A directive and its arguments: `field = "Cranking RPM", crankingRPM`. Leading
/// whitespace varies wildly between firmwares (rusEFI indents with tabs,
/// MegaSquirt with spaces) so it is simply discarded.
static DIRECTIVE: LazyLock<Regex> =
    LazyLock::new(|| Regex::new(r"^\s*(?<key>[A-Za-z][A-Za-z0-9_]*)\s*=\s*(?<rest>.*)$").expect("pattern"));

/// A section header, which ends whatever was being read.
static SECTION: LazyLock<Regex> = LazyLock::new(|| Regex::new(r"^\s*\[(?<name>[^\]]+)\]").expect("pattern"));

/// Reads `[Menu]` and `[UserDefined]`.
///
/// `symbols` are the firmware's compile-time symbols, deciding which side of each
/// `#if` is real. Defaults to the same set every other reader here uses.
#[allow(unused_assignments)]
pub fn read_interface(ini_text: &str, symbols: Option<&SymbolSet>) -> TuneInterface {
    let symbols = symbols.unwrap_or_else(|| default_symbols());

    let mut menus: Vec<TuneMenu> = Vec::new();
    let mut dialogs: IgnoreCaseMap<TuneDialog> = IgnoreCaseMap::new();

    // The dialog or menu currently being filled, since both are declared by a line
    // and then extended by the lines beneath it.
    let mut menu_title = String::new();
    let mut menu_entries: Vec<MenuEntry> = Vec::new();

    let mut dialog_name = String::new();
    let mut dialog_title = String::new();
    let mut dialog_layout = String::new();
    let mut dialog_help = String::new();
    let mut items: Vec<DialogItem> = Vec::new();

    // Both sections, through the shared reader so that #if is resolved against this
    // firmware's symbols the way every other reader here does it. Splitting the raw
    // text merges both branches of every conditional, and since a redeclared dialog
    // replaces the earlier one, a dialog written differently under #if and #else
    // would always come out as the #else version whatever the tune actually is.
    let mut lines = msq_ini::section(ini_text, "Menu", symbols);
    lines.push("[end of menu]".to_string());
    lines.extend(msq_ini::section(ini_text, "UserDefined", symbols));

    macro_rules! close_menu {
        () => {
            if !menu_title.is_empty() || !menu_entries.is_empty() {
                menus.push(TuneMenu {
                    title: std::mem::take(&mut menu_title),
                    entries: std::mem::take(&mut menu_entries),
                });
            }
            menu_title = String::new();
            menu_entries = Vec::new();
        };
    }

    macro_rules! close_dialog {
        () => {
            if !dialog_name.is_empty() {
                // Last declaration wins. A definition may redeclare a dialog to
                // replace it for a firmware variant, and the file is read top to
                // bottom the way the tool that wrote it expects.
                dialogs.insert(
                    &dialog_name,
                    TuneDialog {
                        name: dialog_name.clone(),
                        title: dialog_title.clone(),
                        layout: dialog_layout.clone(),
                        items: std::mem::take(&mut items),
                        help: dialog_help.clone(),
                    },
                );
            }
            dialog_name = String::new();
            dialog_title = String::new();
            dialog_layout = String::new();
            dialog_help = String::new();
            items = Vec::new();
        };
    }

    for raw in lines {
        let line = strip(&raw);
        if line.is_empty() {
            continue;
        }

        // The marker between the two, since the reader yields a section's own lines
        // and never its header.
        if SECTION.is_match(&line) {
            close_menu!();
            close_dialog!();
            continue;
        }

        let Some(m) = DIRECTIVE.captures(&line) else {
            continue;
        };

        let key = grp(&m, "key");
        let rest = grp(&m, "rest").trim();

        match key.to_lowercase().as_str() {
            // ----- menus ----------------------------------------------------
            "menudialog" => {
                // Names the menu bar being described. Several may appear; the menus
                // under each are collected the same way.
                close_menu!();
            }

            "menu" => {
                close_menu!();
                let first = split(rest).into_iter().next().unwrap_or_default();
                menu_title = caption(&unquote(&first));
            }

            "submenu" => {
                let (parts, condition, _) = arguments(rest);
                if parts.is_empty() || parts[0].is_empty() {
                    continue;
                }

                menu_entries.push(MenuEntry {
                    dialog: parts[0].clone(),
                    title: if parts.len() > 1 {
                        caption(&unquote(&parts[1]))
                    } else {
                        String::new()
                    },
                    condition,
                });
            }

            // ----- dialogs --------------------------------------------------
            "dialog" => {
                close_dialog!();

                let (parts, _, _) = arguments(rest);
                if parts.is_empty() || parts[0].is_empty() {
                    continue;
                }

                dialog_name = parts[0].clone();
                dialog_title = if parts.len() > 1 {
                    caption(&unquote(&parts[1]))
                } else {
                    String::new()
                };

                // The axis is optional and yAxis is what TunerStudio assumes, so a
                // dialog that does not say lays its items out downwards.
                dialog_layout = if parts.len() > 2 && !parts[2].is_empty() {
                    parts[2].clone()
                } else {
                    "yAxis".to_string()
                };
            }

            "topichelp" => dialog_help = unquote(rest),

            "field" | "displayonlyfield" => {
                let (parts, condition, condition_at) = arguments(rest);
                let label = if parts.is_empty() {
                    String::new()
                } else {
                    caption(&unquote(&parts[0]))
                };
                let constant = if parts.len() > 1 {
                    parts[1].clone()
                } else {
                    String::new()
                };

                // An expression standing alone where the constant belongs is a
                // computed caption, not a rule about when to show the line:
                //     displayOnlyField = "Injector A", { bitStringValue(...) }
                // Read as a condition it would hide the field whenever that text
                // happened to evaluate to zero.
                //
                // Standing alone is the test, not merely being in that position: a
                // condition may also trail a constant inside the same argument, and
                // there the constant is a constant.
                if condition_at == 1 && constant.is_empty() {
                    items.push(DialogItem::new(DialogItemKind::Label, label));
                    continue;
                }

                // A field naming no constant is a caption or a blank line between
                // groups, and there are a great many of both.
                let kind = if constant.is_empty() {
                    DialogItemKind::Label
                } else if key.eq_ignore_ascii_case("displayOnlyField") {
                    DialogItemKind::ReadOnlyField
                } else {
                    DialogItemKind::Field
                };

                items.push(
                    DialogItem::new(kind, label)
                        .with_target(constant)
                        .with_condition(condition),
                );
            }

            "panel" => {
                let (parts, condition, _) = arguments(rest);
                if parts.is_empty() || parts[0].is_empty() {
                    continue;
                }

                // A panel may carry a position, a condition, both or neither. The
                // condition has already been lifted out, so anything left in the
                // second argument is the position.
                let position = if parts.len() > 1 {
                    parts[1].clone()
                } else {
                    String::new()
                };

                items.push(
                    DialogItem::new(DialogItemKind::Panel, "")
                        .with_target(parts[0].clone())
                        .with_condition(condition)
                        .with_position(position),
                );
            }

            "commandbutton" => {
                let (parts, condition, _) = arguments(rest);
                if parts.len() < 2 || parts[1].is_empty() {
                    continue;
                }

                items.push(
                    DialogItem::new(DialogItemKind::Command, caption(&unquote(&parts[0])))
                        .with_target(parts[1].clone())
                        .with_condition(condition),
                );
            }

            "slider" => {
                let (parts, condition, _) = arguments(rest);
                if parts.len() < 2 || parts[1].is_empty() {
                    continue;
                }

                items.push(
                    DialogItem::new(DialogItemKind::Slider, caption(&unquote(&parts[0])))
                        .with_target(parts[1].clone())
                        .with_condition(condition),
                );
            }

            "gauge" => {
                let (parts, _, _) = arguments(rest);
                if parts.is_empty() || parts[0].is_empty() {
                    continue;
                }

                items.push(DialogItem::new(DialogItemKind::Gauge, "").with_target(parts[0].clone()));
            }

            "text" => items.push(DialogItem::new(DialogItemKind::Text, unquote(rest))),

            // Live graphs, status lamps and curve editors are described here too.
            // They are recorded rather than dropped so a dialog holding one can say
            // it is not showing everything, instead of silently presenting a
            // partial page as the whole of it.
            "graphline" | "livegraph" | "indicator" | "indicatorpanel" => {
                items.push(DialogItem::new(DialogItemKind::Unsupported, "").with_target(key));
            }

            _ => {}
        }
    }

    close_menu!();
    close_dialog!();

    TuneInterface { menus, dialogs }
}

/// The line without its comment or its line ending.
///
/// A semicolon inside quotes is part of a label rather than the start of a
/// comment: "Set the timing; carefully" is a caption, not a truncated one.
pub(crate) fn strip_comment(line: &str) -> String {
    strip(line)
}

fn strip(line: &str) -> String {
    let mut quoted = false;

    for (i, c) in line.char_indices() {
        if c == '"' {
            quoted = !quoted;
        } else if c == ';' && !quoted {
            return line[..i].trim_end().to_string();
        }
    }

    line.trim_end_matches(['\r', '\n', ' ', '\t']).to_string()
}

/// Arguments split on commas, with commas inside quotes or braces left alone.
///
/// Both matter: a label may contain a comma, and a condition almost always does:
/// `{ (a == 1) || (b == 2), }` is one argument however it looks.
fn split(text: &str) -> Vec<String> {
    let mut parts: Vec<String> = Vec::new();
    let mut quoted = false;
    let mut depth = 0_i32;
    let mut start = 0;

    for (i, c) in text.char_indices() {
        if c == '"' {
            quoted = !quoted;
        } else if !quoted && c == '{' {
            depth += 1;
        } else if !quoted && c == '}' {
            depth = (depth - 1).max(0);
        } else if c == ',' && !quoted && depth == 0 {
            parts.push(text[start..i].trim().to_string());
            start = i + 1;
        }
    }

    parts.push(text[start..].trim().to_string());

    // A trailing comma is common and leaves an empty argument behind it.
    while parts.last().is_some_and(String::is_empty) {
        parts.pop();
    }

    parts
}

/// A directive's arguments, with any condition lifted out of them.
///
/// Two things the obvious split gets wrong, both found in real definitions. A
/// condition is not always an argument of its own: Speeduino writes `field =
/// "Bypass pin", ignBypassPin   { ignBypassEnable }`, with the condition trailing
/// the constant inside one argument. And `{}` is used as a placeholder for an
/// argument that is not being given, so an empty pair of braces is not a condition
/// and must not be mistaken for the constant either.
fn arguments(rest: &str) -> (Vec<String>, String, i32) {
    let mut parts: Vec<String> = Vec::new();
    let mut condition = String::new();
    let mut condition_at = -1;

    for raw in split(rest) {
        let mut value = raw;

        if let Some(brace) = opening_brace(&value) {
            let found = value[brace..].trim().trim_matches(['{', '}']).trim().to_string();

            // The last one that says something. A line may carry several
            // placeholders before the real condition.
            if !found.is_empty() {
                condition = found;
                condition_at = parts.len() as i32;
            }

            value = value[..brace].trim().to_string();
        }

        // A quoted title may follow the name without a comma between them, as
        // rusEFI writes its menus:
        //     subMenu = dcMotorActuatorHw   "DC motor actuator(s) hardware"
        // Splitting it out here keeps the name a name; left alone it becomes an
        // identifier with a caption stuck to the end that matches nothing. Only
        // when what precedes the quote is a name. A label may be prefixed to mark
        // it (Speeduino writes !"..." for a warning and #"..." for a note) and
        // splitting on that would turn one caption into a target of "!" and a
        // stray string.
        if let Some(quote) = value.find('"')
            && quote > 0
            && is_identifier(value[..quote].trim())
        {
            parts.push(value[..quote].trim().to_string());
            value = value[quote..].trim().to_string();
        }

        parts.push(value);
    }

    while parts.last().is_some_and(String::is_empty) {
        parts.pop();
    }

    (parts, condition, condition_at)
}

/// Whether the text is a bare name, rather than punctuation or prose.
fn is_identifier(text: &str) -> bool {
    let mut chars = text.chars();
    match chars.next() {
        Some(first) if first.is_alphabetic() || first == '_' => {}
        _ => return false,
    }

    text.chars()
        .all(|c| c.is_alphanumeric() || matches!(c, '_' | '[' | ']'))
}

/// Where a condition begins in an argument, ignoring quoted braces.
fn opening_brace(text: &str) -> Option<usize> {
    let mut quoted = false;

    for (i, c) in text.char_indices() {
        if c == '"' {
            quoted = !quoted;
        } else if c == '{' && !quoted {
            return Some(i);
        }
    }

    None
}

fn unquote(text: &str) -> String {
    let trimmed = text.trim();

    if trimmed.len() >= 2 && trimmed.starts_with('"') && trimmed.ends_with('"') {
        trimmed[1..trimmed.len() - 1].to_string()
    } else {
        trimmed.to_string()
    }
}

/// A caption without the markers the format puts in front of it.
///
/// `&` marks the letter a menu would underline, so "F&uel Settings" is Fuel
/// Settings with the u as its shortcut; a doubled one is a literal ampersand. A
/// leading `!` marks a warning and `#` a note, both of which are about how to draw
/// the line rather than part of what it says.
pub(crate) fn caption(text: &str) -> String {
    let trimmed = text.trim_start_matches(['!', '#']).trim();

    if !trimmed.contains('&') {
        return trimmed.to_string();
    }

    let chars: Vec<char> = trimmed.chars().collect();
    let mut built = String::with_capacity(trimmed.len());
    let mut i = 0;

    while i < chars.len() {
        if chars[i] != '&' {
            built.push(chars[i]);
            i += 1;
            continue;
        }

        // A doubled ampersand is one real ampersand.
        if i + 1 < chars.len() && chars[i + 1] == '&' {
            built.push('&');
            i += 1;
        }
        i += 1;
    }

    built
}

/// A two-dimensional editor: one row of breakpoints against one row of values,
/// drawn as a line you drag.
///
/// The other half of tuning, beside the tables. Warmup enrichment, cranking
/// pulsewidth, the knock threshold against RPM: an MS3 declares 134 of these, and a
/// settings menu that could not open them would be missing most of what a tuner
/// actually changes.
#[derive(Clone, Debug, PartialEq, Eq)]
pub struct TuneCurve {
    /// What a menu refers to it by.
    pub name: String,
    /// What to put at the top.
    pub title: String,
    /// Constant holding the breakpoints along the bottom.
    pub x_bins: String,
    /// Constant holding the values.
    pub y_bins: String,
    /// Caption for the breakpoints.
    pub x_label: String,
    /// Caption for the values.
    pub y_label: String,
    /// The axis bounds as written, which may be a number or a `{...}` referring to
    /// a PC variable: an RPM axis is usually drawn to whatever the user set as the
    /// highest RPM they care about.
    pub x_low: String,
    pub x_high: String,
    pub y_low: String,
    pub y_high: String,
    pub help: String,
}

impl TuneCurve {
    /// True when both halves are named, without which there is nothing to draw.
    pub fn is_usable(&self) -> bool {
        !self.x_bins.is_empty() && !self.y_bins.is_empty()
    }
}

/// Reads `[CurveEditor]`.
pub fn read_curves(ini_text: &str, symbols: Option<&SymbolSet>) -> IgnoreCaseMap<TuneCurve> {
    let symbols = symbols.unwrap_or_else(|| default_symbols());

    let mut curves: IgnoreCaseMap<TuneCurve> = IgnoreCaseMap::new();

    let mut current = TuneCurve {
        name: String::new(),
        title: String::new(),
        x_bins: String::new(),
        y_bins: String::new(),
        x_label: String::new(),
        y_label: String::new(),
        x_low: String::new(),
        x_high: String::new(),
        y_low: String::new(),
        y_high: String::new(),
        help: String::new(),
    };
    let mut open = false;

    let blank = |name: &str| TuneCurve {
        name: name.to_string(),
        title: String::new(),
        x_bins: String::new(),
        y_bins: String::new(),
        x_label: String::new(),
        y_label: String::new(),
        x_low: String::new(),
        x_high: String::new(),
        y_low: String::new(),
        y_high: String::new(),
        help: String::new(),
    };

    fn close(
        curves: &mut IgnoreCaseMap<TuneCurve>,
        current: &mut TuneCurve,
        open: &mut bool,
        blank: &dyn Fn(&str) -> TuneCurve,
    ) {
        if *open && !current.name.is_empty() {
            let name = current.name.clone();
            curves.insert(&name, current.clone());
        }

        *current = blank("");
        *open = false;
    }

    // Only this section. Reading the whole file leaves the last curve open past the
    // end of it, and [TableEditor], which normally follows, spells its bin constants
    // with the very same keys, so xBins, yBins and columnLabel from every table
    // after it overwrite the curve's until what comes out points at the last
    // table's axes.
    for raw in msq_ini::section(ini_text, "CurveEditor", symbols) {
        // The quote-aware stripper, not msq_ini's: that one cuts at the first
        // semicolon whatever is around it, which truncates a curve title containing
        // one.
        let line = strip_comment(&raw);
        if line.is_empty() {
            continue;
        }

        let Some(equals) = line.find('=') else {
            continue;
        };

        let key = line[..equals].trim();
        let parts = split(&line[equals + 1..]);

        match key.to_lowercase().as_str() {
            "curve" => {
                close(&mut curves, &mut current, &mut open, &blank);
                if parts.is_empty() || parts[0].is_empty() {
                    continue;
                }

                current.name = parts[0].clone();
                current.title = if parts.len() > 1 {
                    unquote(&parts[1])
                } else {
                    String::new()
                };
                open = true;
            }

            "columnlabel" => {
                current.x_label = if parts.is_empty() {
                    String::new()
                } else {
                    unquote(&parts[0])
                };
                current.y_label = if parts.len() > 1 {
                    unquote(&parts[1])
                } else {
                    String::new()
                };
            }

            // The third argument is how many gridlines to draw, which is not needed
            // to read the curve.
            "xaxis" => {
                if parts.len() > 1 {
                    current.x_low = parts[0].clone();
                    current.x_high = parts[1].clone();
                }
            }

            "yaxis" => {
                if parts.len() > 1 {
                    current.y_low = parts[0].clone();
                    current.y_high = parts[1].clone();
                }
            }

            // The second argument names the output channel the ECU looks the curve
            // up by, which is not needed to edit it.
            "xbins" => {
                if !parts.is_empty() {
                    current.x_bins = parts[0].clone();
                }
            }

            "ybins" => {
                if !parts.is_empty() {
                    current.y_bins = parts[0].clone();
                }
            }

            "topichelp" => {
                current.help = if parts.is_empty() {
                    String::new()
                } else {
                    unquote(&parts[0])
                };
            }

            _ => {}
        }
    }

    close(&mut curves, &mut current, &mut open, &blank);
    curves
}

#[cfg(test)]
mod tests {
    use super::*;

    // Every awkward case here was found in a real definition file (MS2Extra, MS3,
    // rusEFI and Speeduino between them) and is reproduced in miniature rather than
    // by checking a copy of somebody's firmware into the repository.

    fn read(ini: &str) -> TuneInterface {
        read_interface(ini, None)
    }

    fn find<'a>(ui: &'a TuneInterface, name: &str) -> &'a TuneDialog {
        ui.find(name).expect("dialog")
    }

    // ----- menus ------------------------------------------------------------

    #[test]
    fn menus_and_their_entries_are_read() {
        let ui = read(
            r#"[Menu]
   menuDialog = main
      menu = "Basic/Load Settings"
        subMenu = base, "Engine and Sequential Settings"
        subMenu = revlimiter2, "Rev Limiter"
      menu = "Spark Settings"
        subMenu = sparkSettings, "Spark Settings"
"#,
        );

        assert_eq!(ui.menus.len(), 2);
        assert_eq!(ui.menus[0].title, "Basic/Load Settings");
        assert_eq!(ui.menus[0].entries.len(), 2);
        assert_eq!(ui.menus[0].entries[0].dialog, "base");
        assert_eq!(ui.menus[0].entries[0].title, "Engine and Sequential Settings");
        assert_eq!(ui.menus[1].entries.len(), 1);
    }

    #[test]
    fn a_separator_is_an_entry_but_not_a_thing_to_open() {
        let ui = read(
            r#"[Menu]
   menu = "Settings"
     subMenu = base, "Base"
     subMenu = std_separator
     subMenu = other, "Other"
"#,
        );

        assert_eq!(ui.menus[0].entries.len(), 3);
        assert!(ui.menus[0].entries[1].is_separator());
        assert!(!ui.menus[0].entries[0].is_separator());
    }

    #[test]
    fn the_tools_own_editors_are_recognised_as_such() {
        let ui = read(
            r#"[Menu]
   menu = "Tools"
     subMenu = std_realtime, "&Realtime Display"
     subMenu = std_ms2gentherm, "Thermistor tables"
     subMenu = mine, "Mine"
"#,
        );

        assert!(ui.menus[0].entries[0].is_built_in());
        assert!(ui.menus[0].entries[1].is_built_in());
        assert!(!ui.menus[0].entries[2].is_built_in());
    }

    /// The page number sits between the title and the condition, so the condition
    /// cannot be found by counting arguments.
    #[test]
    fn a_menu_entry_carries_the_condition_that_offers_it() {
        let ui = read(
            r#"[Menu]
   menu = "Settings"
     subMenu = barometerCorr, "Barometric Correction", 0, {baroCorr}
"#,
        );

        let entry = &ui.menus[0].entries[0];

        assert!(entry.has_condition());
        assert_eq!(entry.condition, "baroCorr");
        assert_eq!(entry.title, "Barometric Correction");
    }

    /// rusEFI writes its menus this way. Left alone the name comes out with a
    /// caption stuck to the end of it and matches no dialog at all.
    #[test]
    fn a_title_may_follow_the_name_with_no_comma_between_them() {
        let ui = read(
            "[Menu]\n   menu = \"Controller\"\n     subMenu = dcMotorActuatorHw\t\t\t\"DC motor actuator(s) hardware\", { 1 }, { uiMode == 0 }\n",
        );

        let entry = &ui.menus[0].entries[0];

        assert_eq!(entry.dialog, "dcMotorActuatorHw");
        assert_eq!(entry.title, "DC motor actuator(s) hardware");
    }

    // ----- dialogs and fields -----------------------------------------------

    #[test]
    fn a_dialog_carries_its_title_layout_and_fields() {
        let ui = read(
            r#"[UserDefined]
   dialog = base, "Engine Settings", xAxis
      field = "Cranking RPM", crankingRPM
      field = "Squirts Per Cycle", divider
"#,
        );

        let dialog = find(&ui, "base");

        assert_eq!(dialog.title, "Engine Settings");
        assert!(dialog.lays_out_across());
        assert_eq!(dialog.items.len(), 2);
        assert_eq!(dialog.items[0].kind, DialogItemKind::Field);
        assert_eq!(dialog.items[0].target, "crankingRPM");
    }

    #[test]
    fn a_dialog_that_does_not_say_lays_its_items_downwards() {
        let ui = read(
            r#"[UserDefined]
   dialog = etc_set, ""
      field = "A", a
"#,
        );

        assert!(!find(&ui, "etc_set").lays_out_across());
    }

    #[test]
    fn a_field_with_no_constant_is_a_caption_or_a_spacer() {
        let ui = read(
            r#"[UserDefined]
   dialog = d, ""
      field = "Per Gear Targets:"
      field = ""
      field = "Boost", boostTarget
"#,
        );

        let dialog = find(&ui, "d");

        assert_eq!(dialog.items[0].kind, DialogItemKind::Label);
        assert_eq!(dialog.items[0].label, "Per Gear Targets:");
        assert_eq!(dialog.items[1].kind, DialogItemKind::Label);
        assert_eq!(dialog.items[1].label, "");
        assert_eq!(dialog.items[2].kind, DialogItemKind::Field);
    }

    #[test]
    fn a_read_only_field_is_not_offered_for_editing() {
        let ui = read(
            r#"[UserDefined]
   dialog = d, ""
      displayOnlyField = "Injector test", dummyfield, {(status8 & 0x03) == 1}
"#,
        );

        let item = &find(&ui, "d").items[0];

        assert_eq!(item.kind, DialogItemKind::ReadOnlyField);
        assert!(!item.is_editable());
        assert_eq!(item.condition, "(status8 & 0x03) == 1");
    }

    /// Speeduino writes fields this way, with the condition separated from the
    /// constant by nothing but spaces.
    #[test]
    fn a_condition_may_follow_the_constant_with_no_comma_between_them() {
        let ui = read(
            r#"[UserDefined]
   dialog = d, ""
      field = "Bypass output pin", ignBypassPin               { ignBypassEnable }
"#,
        );

        let item = &find(&ui, "d").items[0];

        assert_eq!(item.target, "ignBypassPin");
        assert_eq!(item.condition, "ignBypassEnable");
    }

    /// Also Speeduino: {} stands in for an argument that is not being given, and
    /// the real condition is further along the line.
    #[test]
    fn empty_braces_are_placeholders_rather_than_conditions() {
        let ui = read(
            r#"[UserDefined]
   dialog = d, ""
      field = "!Warning: not enough channels", {}, {}, { injLayout == 3 }
"#,
        );

        let item = &find(&ui, "d").items[0];

        assert_eq!(item.kind, DialogItemKind::Label);
        assert_eq!(item.condition, "injLayout == 3");
        assert_eq!(item.target, "");
    }

    /// "!" prefixes a warning and "#" a note. Neither is an identifier, so neither
    /// may be taken as the name of something.
    #[test]
    fn a_marked_label_is_not_split_into_a_name_and_a_string() {
        let ui = read(
            r#"[UserDefined]
   dialog = d, ""
      displayOnlyField = !"No PWM Fan available on MCU", blankfield, {fanEnable == 2}
"#,
        );

        let item = &find(&ui, "d").items[0];

        assert_eq!(item.target, "blankfield");
        assert_eq!(item.condition, "fanEnable == 2");
    }

    /// MS3 builds a label from a lookup rather than naming a constant. Read as a
    /// condition it would hide the field whenever that text happened to come out as
    /// zero.
    #[test]
    fn an_expression_where_the_constant_belongs_is_a_computed_caption() {
        let ui = read(
            r#"[UserDefined]
   dialog = d, ""
      displayOnlyField = "Injector A", { bitStringValue( portLabels, portusage_a[0] ) }
"#,
        );

        let item = &find(&ui, "d").items[0];

        assert_eq!(item.kind, DialogItemKind::Label);
        assert!(!item.has_condition());
    }

    #[test]
    fn a_field_may_address_one_element_of_an_array() {
        let ui = read(
            r#"[UserDefined]
   dialog = d, ""
      field = "Enabled", psEnabled[2]
"#,
        );

        let item = &find(&ui, "d").items[0];

        assert_eq!(item.target, "psEnabled[2]");
        assert_eq!(item.target_constant(), "psEnabled");
        assert_eq!(item.target_index(), 2);
    }

    #[test]
    fn a_field_on_the_whole_array_has_no_index() {
        let ui = read(
            r#"[UserDefined]
   dialog = d, ""
      field = "Bins", rpmBins
"#,
        );

        assert_eq!(find(&ui, "d").items[0].target_index(), -1);
        assert_eq!(find(&ui, "d").items[0].target_constant(), "rpmBins");
    }

    // ----- the other kinds of line ------------------------------------------

    #[test]
    fn panels_carry_a_position_a_condition_both_or_neither() {
        let ui = read(
            r#"[UserDefined]
   dialog = d, ""
      panel = plain
      panel = placed, Center
      panel = gated, {opt_on}
      panel = both, South, {als_opt_fc}
"#,
        );

        let items = &find(&ui, "d").items;
        let shape = |i: usize| {
            (
                items[i].target.as_str(),
                items[i].position.as_str(),
                items[i].condition.as_str(),
            )
        };

        assert_eq!(shape(0), ("plain", "", ""));
        assert_eq!(shape(1), ("placed", "Center", ""));
        assert_eq!(shape(2), ("gated", "", "opt_on"));
        assert_eq!(shape(3), ("both", "South", "als_opt_fc"));
    }

    #[test]
    fn buttons_gauges_sliders_and_prose_are_each_recognised() {
        let ui = read(
            r#"[UserDefined]
   dialog = d, ""
      commandButton = "Reset ECU", cmdenginereset, {rpm == 0}
      gauge = throttleGauge
      slider = "Sensitivity", pitlim_sensitivity, horizontal, {pitlim_opt_on}
      text = "For current documentation, click the Web Help button,"
"#,
        );

        let items = &find(&ui, "d").items;

        assert_eq!(items[0].kind, DialogItemKind::Command);
        assert_eq!(items[0].target, "cmdenginereset");
        assert_eq!(items[0].condition, "rpm == 0");

        assert_eq!(items[1].kind, DialogItemKind::Gauge);
        assert_eq!(items[1].target, "throttleGauge");

        assert_eq!(items[2].kind, DialogItemKind::Slider);
        assert!(items[2].is_editable());
        assert_eq!(items[2].target, "pitlim_sensitivity");

        assert_eq!(items[3].kind, DialogItemKind::Text);
    }

    /// So a dialog holding one can say it is not showing everything, instead of
    /// presenting a partial page as the whole of it.
    #[test]
    fn what_is_not_drawn_yet_is_recorded_rather_than_dropped() {
        let ui = read(
            r#"[UserDefined]
   dialog = d, ""
      field = "A", a
      graphLine = someLine
      indicator = { ind }, "off", "on", white, black
"#,
        );

        let items = &find(&ui, "d").items;

        assert_eq!(items.len(), 3);
        assert_eq!(
            items.iter().filter(|i| i.kind == DialogItemKind::Unsupported).count(),
            2
        );
    }

    #[test]
    fn a_dialogs_help_link_is_kept() {
        let ui = read(
            r#"[UserDefined]
   dialog = etctest, "Throttle test", yAxis
        topicHelp = "file://$getProjectsDirPath()/docs/ref.pdf#throttletest"
      field = "A", a
"#,
        );

        assert!(find(&ui, "etctest").help.contains("throttletest"));
    }

    // ----- reading the file at all ------------------------------------------

    /// A field-looking line in another section must not become a dialog item.
    #[test]
    fn only_the_two_sections_that_matter_are_read() {
        let ui = read(
            r#"[Constants]
   field = "not a dialog", nope
   dialog = notADialog, "no"

[UserDefined]
   dialog = real, "Real"
      field = "A", a

[Datalog]
   field = "also not", nope2
"#,
        );

        assert_eq!(ui.dialogs.len(), 1);
        assert!(ui.find("real").is_some());
        assert_eq!(find(&ui, "real").items.len(), 1);
    }

    #[test]
    fn a_comment_is_ignored_but_a_semicolon_in_a_label_is_not() {
        let ui = read(
            r#"[UserDefined]
   dialog = d, ""
      field = "Set the timing; carefully", timing   ; this part is a comment
"#,
        );

        let item = &find(&ui, "d").items[0];

        assert_eq!(item.label, "Set the timing; carefully");
        assert_eq!(item.target, "timing");
    }

    /// rusEFI indents with tabs, MegaSquirt with spaces, and both appear at several
    /// depths.
    #[test]
    fn leading_whitespace_is_irrelevant_however_it_is_written() {
        let ui = read("[UserDefined]\ndialog = d, \"\"\n\t\t\tfield = \"A\", a\n    field = \"B\", b\n");

        assert_eq!(find(&ui, "d").items.len(), 2);
    }

    #[test]
    fn a_redeclared_dialog_replaces_the_earlier_one() {
        let ui = read(
            r#"[UserDefined]
   dialog = d, "First"
      field = "A", a
   dialog = d, "Second"
      field = "B", b
      field = "C", c
"#,
        );

        assert_eq!(ui.dialogs.len(), 1);
        assert_eq!(find(&ui, "d").title, "Second");
        assert_eq!(find(&ui, "d").items.len(), 2);
    }

    #[test]
    fn a_file_with_no_interface_is_empty_rather_than_a_failure() {
        assert!(read("[Constants]\npage = 1\n").is_empty());
        assert!(read("").is_empty());
    }

    #[test]
    fn looking_up_a_dialog_is_not_case_sensitive() {
        let ui = read("[UserDefined]\ndialog = MyDialog, \"T\"\nfield = \"A\", a\n");

        assert!(ui.find("mydialog").is_some());
        assert!(ui.find("nosuchdialog").is_none());
    }

    // ----- the preprocessor -------------------------------------------------

    const TEMPS: &str = r#"[UserDefined]
   #if CELSIUS
   dialog = temps, "Temperatures in C"
      field = "Coolant", cltC
   #else
   dialog = temps, "Temperatures in F"
      field = "Coolant", cltF
   #endif
"#;

    /// Both branches merged is bad enough; worse is that a redeclared dialog
    /// replaces the earlier one, so a dialog written differently under #if and
    /// #else always came out as the #else version whatever the firmware actually
    /// is. MS3 has 102 of these inside its menus and dialogs.
    #[test]
    fn only_the_live_branch_of_a_conditional_is_read() {
        let symbols = SymbolSet::ordinal(["CELSIUS"]);
        let ui = read_interface(TEMPS, Some(&symbols));

        let dialog = find(&ui, "temps");

        assert_eq!(dialog.title, "Temperatures in C");
        assert_eq!(dialog.items[0].target, "cltC");
    }

    #[test]
    fn the_other_branch_is_taken_when_the_symbol_is_absent() {
        let symbols = SymbolSet::ordinal(Vec::<String>::new());
        let ui = read_interface(TEMPS, Some(&symbols));

        assert_eq!(find(&ui, "temps").title, "Temperatures in F");
        assert_eq!(find(&ui, "temps").items[0].target, "cltF");
    }

    #[test]
    fn conditionals_in_the_menu_are_resolved_too() {
        let symbols = SymbolSet::ordinal(Vec::<String>::new());
        let ui = read_interface(
            r#"[Menu]
   menu = "Settings"
     #if CAN_COMMANDS
     subMenu = canSettings, "CAN"
     #endif
     subMenu = base, "Base"
"#,
            Some(&symbols),
        );

        assert_eq!(ui.menus[0].entries.len(), 1);
        assert_eq!(ui.menus[0].entries[0].dialog, "base");
    }

    // ----- captions ---------------------------------------------------------

    /// "&" marks the letter a menu would underline. Left in, the list reads
    /// "F&uel Settings".
    #[test]
    fn a_menu_mnemonic_is_not_part_of_its_name() {
        let ui = read(
            r#"[Menu]
   menu = "F&uel Settings"
     subMenu = ego, "&EGO Control"
"#,
        );

        assert_eq!(ui.menus[0].title, "Fuel Settings");
        assert_eq!(ui.menus[0].entries[0].title, "EGO Control");
    }

    #[test]
    fn a_doubled_ampersand_is_a_real_one() {
        let ui = read(
            r#"[Menu]
   menu = "Nuts && Bolts"
     subMenu = a, "A"
"#,
        );

        assert_eq!(ui.menus[0].title, "Nuts & Bolts");
    }

    /// "!" marks a warning and "#" a note. Both are about how to draw the line
    /// rather than part of what it says.
    #[test]
    fn a_marker_in_front_of_a_label_is_not_part_of_it() {
        let ui = read(
            r##"[UserDefined]
   dialog = d, "D"
      field = "!Warning: not enough channels"
      field = "#Sequential Siamese Hybrid Mode"
"##,
        );

        let items = &find(&ui, "d").items;

        assert_eq!(items[0].label, "Warning: not enough channels");
        assert_eq!(items[1].label, "Sequential Siamese Hybrid Mode");
    }

    // ----- curves -----------------------------------------------------------

    #[test]
    fn curves_are_read_and_do_not_bleed_into_the_next_section() {
        let curves = read_curves(
            r#"[CurveEditor]
   curve = warmup, "Warmup; enrichment"
      columnLabel = "Coolant", "%"
      xAxis = -40, 215, 6
      yAxis = 100, 250, 6
      xBins = wueBins, coolant
      yBins = wueRates
      topicHelp = "help.html"

[TableEditor]
   table = veTbl, veMap, "VE Table"
      xBins = veRpmBins, rpm
      yBins = veMapBins
"#,
            None,
        );

        let curve = curves.get("WARMUP").expect("curve");

        assert_eq!(curve.title, "Warmup; enrichment");
        assert_eq!(curve.x_bins, "wueBins");
        assert_eq!(curve.y_bins, "wueRates");
        assert_eq!(curve.x_label, "Coolant");
        assert_eq!(curve.y_label, "%");
        assert_eq!((curve.x_low.as_str(), curve.x_high.as_str()), ("-40", "215"));
        assert_eq!(curve.help, "help.html");
        assert!(curve.is_usable());
        assert_eq!(curves.len(), 1);
    }
}
