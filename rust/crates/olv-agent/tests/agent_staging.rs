//! Where an agent leaves a file for a person instead of, or alongside, writing to the ECU's
//! working memory. Ported from `AgentStagingTests`.
//!
//! The property worth guarding here is not the content of the file (staging never touches an
//! engine, so a mistaken CSV is a mistaken CSV) but that a file name handed over by whatever is
//! on the far end of the agent API cannot land anywhere except inside this one folder.

mod support;

use std::path::PathBuf;
use std::thread::sleep;
use std::time::Duration;

use olv_agent::agent::agent_staging::AgentStaging;
use olv_core::base::safe_folder::SafeFolderError;
use olv_store::store::workspace::Workspace;
use support::Scratch;

fn new_staging(what: &str) -> (AgentStaging, Scratch) {
    let scratch = Scratch::new(what);
    let staging = AgentStaging::new(&Workspace::new(Some(&scratch.0))).expect("a staging folder");

    (staging, scratch)
}

// ----- the folder itself -----------------------------------------------------------------------

#[test]
fn the_folder_sits_under_the_workspace_root() {
    let scratch = Scratch::new("staging-root");
    let staging = AgentStaging::new(&Workspace::new(Some(&scratch.0))).unwrap();

    assert_eq!(staging.folder(), scratch.0.join("AgentStaging"));
}

#[test]
fn the_folder_is_not_created_until_something_is_staged() {
    let (staging, _scratch) = new_staging("staging-lazy");

    assert!(!staging.folder().exists());
}

// ----- writing -----------------------------------------------------------------------------------

#[test]
fn staging_writes_the_file_and_hands_back_its_full_path() {
    let (staging, _scratch) = new_staging("staging-write");

    let path = staging.stage("tune.msq", "<msq/>").unwrap();

    assert!(path.exists());
    assert_eq!(std::fs::read_to_string(&path).unwrap(), "<msq/>");
    assert_eq!(path, staging.folder().join("tune.msq"));
}

#[test]
fn staging_the_same_name_twice_overwrites_rather_than_failing() {
    let (staging, _scratch) = new_staging("staging-overwrite");

    staging.stage("tune.msq", "first").unwrap();
    let path = staging.stage("tune.msq", "second").unwrap();

    assert_eq!(std::fs::read_to_string(path).unwrap(), "second");
}

#[test]
fn no_temporary_file_is_left_behind_after_a_stage() {
    // The atomic-write discipline the rest of the application's exports already use: written to a
    // .tmp file and moved into place, so an interrupted stage never leaves a truncated file where
    // a person might find and open it.
    let (staging, _scratch) = new_staging("staging-tmp");

    staging.stage("tune.msq", "content").unwrap();

    let leftovers: Vec<PathBuf> = std::fs::read_dir(staging.folder())
        .unwrap()
        .filter_map(Result::ok)
        .map(|e| e.path())
        .filter(|p| p.extension().is_some_and(|e| e == "tmp"))
        .collect();

    assert!(leftovers.is_empty(), "{leftovers:?}");
}

// ----- path safety -------------------------------------------------------------------------------

#[test]
fn a_name_that_tries_to_leave_the_folder_is_refused() {
    for filename in [
        "../escaped.msq",
        "..\\escaped.msq",
        "sub/escaped.msq",
        "sub\\escaped.msq",
        "../../etc/passwd",
    ] {
        let (staging, scratch) = new_staging("staging-escape");

        let refused = staging.stage(filename, "anything");

        assert!(
            matches!(refused, Err(SafeFolderError::Argument(_))),
            "{filename}: {refused:?}"
        );
        assert!(!scratch.0.join("escaped.msq").exists());
    }
}

#[test]
fn an_absolute_path_is_refused_even_when_it_points_inside_the_folder() {
    let (staging, _scratch) = new_staging("staging-absolute");
    let trick = staging.folder().join("tune.msq");

    let refused = staging.stage(&trick.to_string_lossy(), "anything");

    assert!(matches!(refused, Err(SafeFolderError::Argument(_))), "{refused:?}");
}

#[test]
fn an_empty_name_is_refused() {
    let (staging, _scratch) = new_staging("staging-empty");

    assert!(matches!(
        staging.stage("", "anything"),
        Err(SafeFolderError::Argument(_))
    ));
    assert!(matches!(
        staging.stage("   ", "anything"),
        Err(SafeFolderError::Argument(_))
    ));
}

#[test]
fn nothing_is_written_when_the_name_is_refused() {
    let (staging, _scratch) = new_staging("staging-nothing");

    let _ = staging.stage("../escaped.msq", "anything");

    // Refused before the folder is even created: there is nowhere for a half-attempt to have
    // landed.
    assert!(!staging.folder().exists());
}

// ----- listing -----------------------------------------------------------------------------------

#[test]
fn listing_an_unused_folder_is_empty_rather_than_an_error() {
    let (staging, _scratch) = new_staging("staging-list-empty");

    assert!(staging.list_staged().is_empty());
}

#[test]
fn listing_reports_what_was_staged_with_its_size_and_path() {
    let (staging, _scratch) = new_staging("staging-list");

    let path = staging.stage("tune.msq", "0123456789").unwrap();

    let listed = staging.list_staged();
    assert_eq!(listed.len(), 1);
    assert_eq!(listed[0].name, "tune.msq");
    assert_eq!(listed[0].path, path.to_string_lossy());
    assert_eq!(listed[0].bytes, 10);
}

#[test]
fn listing_comes_back_newest_first() {
    let (staging, _scratch) = new_staging("staging-order");

    staging.stage("first.csv", "a").unwrap();
    sleep(Duration::from_millis(50));
    staging.stage("second.csv", "b").unwrap();

    let listed = staging.list_staged();

    assert_eq!(listed[0].name, "second.csv");
    assert_eq!(listed[1].name, "first.csv");
}
