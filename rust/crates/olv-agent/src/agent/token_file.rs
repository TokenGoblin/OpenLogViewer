//! Where an agent finds the address and the token: `agent-api.json` in the workspace folder.
//!
//! Ported from `StartAgentApi` / `WriteAgentToken` / `StopAgentApi` in `AgentCommands.cs`.
//!
//! The token goes in the workspace rather than being shown and typed, for the same reason
//! Jupyter does it: a person copying a secret by hand picks a short one, and a short one on a
//! port any local program can reach is not worth having. The location is the C#'s on every OS
//! (the workspace folder, `OpenLogViewer` under the user's home), and the format is the same
//! three keys. The file is readable by its owner only. On Unix that is mode 0600. On Windows the
//! file inherits whatever the folder allows (on a shared machine, other accounts and groups), so
//! its inherited permissions are removed and the current user alone is granted access, with
//! `icacls` (the workspace denies `unsafe`, which the Win32 security calls would need). Best
//! effort: if the tool or the user name is not there the file is left as the folder has it, and
//! it is not an error.
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
    write_checked(workspace, port, token).map(|(file, _)| file)
}

/// What the application should say when the file could not be made private.
pub const NOT_RESTRICTED: &str = "Warning: the token file could not be restricted to your account, so other     accounts on this machine may be able to read it.";

/// As [`write`], and whether the file was made readable by its owner alone. Not being able to is a
/// warning on stderr and a `false` here, never a failure to start: the caller says so in the
/// status it shows.
pub fn write_checked(workspace: &Workspace, port: u16, token: &str) -> Result<(PathBuf, bool)> {
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

    // Before the token is written, so there is never a moment it is readable by anyone else.
    #[cfg(windows)]
    let restricted = restrict_to_owner(&file);
    // On Unix the mode is set below and a failure to set it is an error.
    #[cfg(not(windows))]
    let restricted = true;

    // A file that already existed keeps the mode it had; make it private either way.
    #[cfg(unix)]
    {
        use std::os::unix::fs::PermissionsExt;
        handle.set_permissions(fs::Permissions::from_mode(0o600))?;
    }

    handle.write_all(contents(port, token).as_bytes())?;
    handle.flush()?;

    if !restricted {
        eprintln!("olv-agent: {NOT_RESTRICTED}");
    }

    Ok((file, restricted))
}

/// The file describes a socket that is no longer there.
pub fn remove(workspace: &Workspace) {
    let _ = fs::remove_file(path(workspace));
}

/// The account's security identifier out of `whoami /user /fo csv /nh`, whose one line is
/// `"DOMAIN\user","S-1-5-21-..."`.
pub fn parse_sid(whoami: &str) -> Option<String> {
    let line = whoami.lines().map(str::trim).find(|l| !l.is_empty())?;
    let sid = line.rsplit(',').next()?.trim().trim_matches('"');

    let valid = sid.starts_with("S-1-") && sid.chars().all(|c| c.is_ascii_alphanumeric() || c == '-');

    valid.then(|| sid.to_string())
}

/// A tool in the system folder, by its full path: nothing earlier on the search path can stand in
/// for it.
#[cfg(windows)]
fn system_tool(name: &str) -> PathBuf {
    let root = std::env::var_os("SystemRoot").map_or_else(|| PathBuf::from(r"C:\Windows"), PathBuf::from);

    root.join("System32").join(name)
}

/// Removes inherited permissions from the file and grants the current account, by its SID, alone
/// full control. Best effort, by the exit status: the tools are run by full path from System32
/// (the workspace denies `unsafe`, which the Win32 security calls would need).
#[cfg(windows)]
pub fn restrict_to_owner(file: &std::path::Path) -> bool {
    use std::os::windows::process::CommandExt;

    const CREATE_NO_WINDOW: u32 = 0x0800_0000;

    let Ok(who) = std::process::Command::new(system_tool("whoami.exe"))
        .args(["/user", "/fo", "csv", "/nh"])
        .creation_flags(CREATE_NO_WINDOW)
        .stderr(std::process::Stdio::null())
        .output()
    else {
        return false;
    };

    let Some(sid) = who
        .status
        .success()
        .then(|| parse_sid(&String::from_utf8_lossy(&who.stdout)))
        .flatten()
    else {
        return false;
    };

    std::process::Command::new(system_tool("icacls.exe"))
        .arg(file)
        .arg("/inheritance:r")
        .arg("/grant:r")
        .arg(format!("*{sid}:(F)"))
        .creation_flags(CREATE_NO_WINDOW)
        .stdout(std::process::Stdio::null())
        .stderr(std::process::Stdio::null())
        .status()
        .is_ok_and(|status| status.success())
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
    fn the_sid_is_read_from_the_csv_whoami_prints() {
        assert_eq!(
            parse_sid("\"DESKTOP\\me\",\"S-1-5-21-111-222-333-1001\"\r\n").as_deref(),
            Some("S-1-5-21-111-222-333-1001")
        );
        assert_eq!(parse_sid(""), None);
        assert_eq!(parse_sid("\"a\",\"not a sid\""), None);
        // Nothing that could be taken for an option or a second argument gets through.
        assert_eq!(parse_sid("\"a\",\"S-1-5-21 /grant Everyone:F\""), None);
    }

    #[cfg(windows)]
    #[test]
    fn the_tools_are_run_by_full_path_from_the_system_folder() {
        let icacls = system_tool("icacls.exe");

        assert!(icacls.is_absolute(), "{icacls:?}");
        assert!(icacls.ends_with(r"System32\icacls.exe"), "{icacls:?}");
        assert!(icacls.exists());
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

    /// On Windows the file must not keep the folder's inherited permissions.
    #[cfg(windows)]
    #[test]
    fn on_windows_only_the_current_user_has_access() {
        let root = std::env::temp_dir().join(format!("olv-token-acl-{}", std::process::id()));
        let workspace = Workspace::new(Some(&root));

        let file = write(&workspace, 1, "t").unwrap();

        let listing = std::process::Command::new("icacls").arg(&file).output().unwrap();
        let text = String::from_utf8_lossy(&listing.stdout).to_string();

        // One entry, the user; no SYSTEM, no Administrators, no Users.
        assert_eq!(text.matches(":(").count(), 1, "{text}");
        let user = std::env::var("USERNAME").unwrap().to_lowercase();
        assert!(text.to_lowercase().contains(&user), "{text}");

        // And the owner can still read it.
        assert!(fs::read_to_string(&file).unwrap().contains("\"token\""));

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
