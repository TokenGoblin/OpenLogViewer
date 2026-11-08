//! Ported from `AgentStaging.cs`. Owned by package `w5-agent`.
//!
//! Where an agent leaves a file for a person to open, instead of (or as well as) writing to the
//! ECU's working memory.
//!
//! A second, independent output path. Not every change belongs in `/tune/apply`: sometimes the
//! point is to hand over a real `.msq` or table CSV that a person imports themselves in
//! TunerStudio, reviews, or simply keeps. Nothing in here is gated the way a write to the ECU is
//! (no armed-writes check, no connection check) because nothing in here can reach an engine. It
//! writes a file, or it does not.
//!
//! The path safety this needs (a bare file name, never anywhere but this folder) is
//! [`SafeFolder`]'s, shared with the definitions import rather than written twice.

use std::path::{Path, PathBuf};

use olv_core::base::safe_folder::{SafeFolder, SafeFolderError};
use olv_store::store::workspace::Workspace;

use super::agent_api::AgentStagedFile;

pub struct AgentStaging {
    folder: SafeFolder,
}

impl AgentStaging {
    pub fn new(workspace: &Workspace) -> Result<Self, SafeFolderError> {
        Ok(Self {
            folder: SafeFolder::new(workspace.staging())?,
        })
    }

    /// The folder itself. Created on first use, not up front.
    pub fn folder(&self) -> &Path {
        self.folder.folder()
    }

    /// Writes `content` under `filename` and hands back the full path it landed at.
    ///
    /// Errors with [`SafeFolderError::Argument`] when the name is empty, names something other
    /// than a plain file, or would resolve outside the staging folder.
    pub fn stage(&self, filename: &str, content: &str) -> Result<PathBuf, SafeFolderError> {
        self.folder.write(filename, content)
    }

    /// What is in the folder now, newest first.
    pub fn list_staged(&self) -> Vec<AgentStagedFile> {
        self.folder
            .files()
            .into_iter()
            .map(|f| AgentStagedFile {
                name: f
                    .path
                    .file_name()
                    .map(|n| n.to_string_lossy().into_owned())
                    .unwrap_or_default(),
                path: f.path.to_string_lossy().into_owned(),
                bytes: i64::try_from(f.len).unwrap_or(i64::MAX),
                written_at: f.last_write,
            })
            .collect()
    }
}
