//! Where an agent finds the address and the token: `agent-api.json` in the workspace folder.
//!
//! Ported from `StartAgentApi` / `WriteAgentToken` / `StopAgentApi` in `AgentCommands.cs`.
//!
//! The token goes in the workspace rather than being shown and typed, for the same reason
//! Jupyter does it: a person copying a secret by hand picks a short one, and a short one on a
//! port any local program can reach is not worth having. The location is the C#'s on every OS
//! (the workspace folder, `OpenLogViewer` under the user's home), and the format is the same
//! three keys. On Unix the file is created readable by its owner only; on Windows the profile
//! folder's own permissions already do that.
//!
//! Written fresh each time the server starts, so a stale file never points an agent at a port
//! nobody is listening on with a token that no longer opens it, and deleted when the server
//! stops. The token is never logged: nothing here prints it.

use std::fs;
use std::io::Write;
use std::path::PathBuf;

use olv_core::{Error, Result};
use olv_store::store::workspace::Workspace;

/// The file's name inside the workspace.
pub const FILE_NAME: &str = "agent-api.json";

/// A fresh token: 32 random bytes from the operating system, as 64 lower-case hex digits.
pub fn new_token() -> Result<String> {
    let mut bytes = [0u8; 32];
    getrandom::fill(&mut bytes).map_err(|e| Error::InvalidOperation(format!("no random source: {e}")))?;

    Ok(bytes.iter().map(|b| format!("{b:02x}")).collect())
}

pub fn path(workspace: &Workspace) -> PathBuf {
    workspace.root().join(FILE_NAME)
}

/// The file's text for a server on `port`.
pub fn contents(port: u16, token: &str) -> String {
    format!(
        "{{\n  \"address\": \"http://127.0.0.1:{port}\",\n  \"websocket\": \"ws://127.0.0.1:{port}/live/stream\",\n  \"token\": \"{token}\"\n}}"
    )
}

/// Leaves the address and the token in the workspace, as JSON, and says where.
pub fn write(workspace: &Workspace, port: u16, token: &str) -> Result<PathBuf> {
    let file = path(workspace);

    fs::create_dir_all(workspace.root())?;

    let mut options = fs::OpenOptions::new();
    options.write(true).create(true).truncate(true);

    #[cfg(unix)]
    {
        use std::os::unix::fs::OpenOptionsExt;
        options.mode(0o600);
    }

    let mut handle = options.open(&file)?;

    // A file that already existed keeps the mode it had; make it private either way.
    #[cfg(unix)]
    {
        use std::os::unix::fs::PermissionsExt;
        handle.set_permissions(fs::Permissions::from_mode(0o600))?;
    }

    handle.write_all(contents(port, token).as_bytes())?;
    handle.flush()?;

    Ok(file)
}

/// The file describes a socket that is no longer there.
pub fn remove(workspace: &Workspace) {
    let _ = fs::remove_file(path(workspace));
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn a_token_is_sixty_four_hex_digits_and_never_repeats() {
        let a = new_token().unwrap();
        let b = new_token().unwrap();

        assert_eq!(a.len(), 64);
        assert!(a.bytes().all(|c| c.is_ascii_digit() || (b'a'..=b'f').contains(&c)));
        assert_ne!(a, b);
    }

    #[test]
    fn the_file_carries_the_three_keys_an_agent_reads() {
        let text = contents(8765, "abc");
        let json: serde_json::Value = serde_json::from_str(&text).unwrap();

        assert_eq!(json["address"], "http://127.0.0.1:8765");
        assert_eq!(json["websocket"], "ws://127.0.0.1:8765/live/stream");
        assert_eq!(json["token"], "abc");
    }

    #[test]
    fn written_fresh_and_removed_on_stop() {
        let root = std::env::temp_dir().join(format!("olv-token-{}", std::process::id()));
        let workspace = Workspace::new(Some(&root));

        let file = write(&workspace, 1234, "t0k3n").unwrap();
        assert!(fs::read_to_string(&file).unwrap().contains("\"t0k3n\""));

        // A second start replaces it.
        write(&workspace, 4321, "other").unwrap();
        let text = fs::read_to_string(&file).unwrap();
        assert!(text.contains("4321") && !text.contains("t0k3n"));

        remove(&workspace);
        assert!(!file.exists());

        let _ = fs::remove_dir_all(&root);
    }

    #[cfg(unix)]
    #[test]
    fn on_unix_only_the_owner_can_read_it() {
        use std::os::unix::fs::PermissionsExt;

        let root = std::env::temp_dir().join(format!("olv-token-mode-{}", std::process::id()));
        let workspace = Workspace::new(Some(&root));

        let file = write(&workspace, 1, "t").unwrap();
        assert_eq!(fs::metadata(&file).unwrap().permissions().mode() & 0o777, 0o600);

        let _ = fs::remove_dir_all(&root);
    }
}
