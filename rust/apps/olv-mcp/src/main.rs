//! `olv-mcp [agent-api.json]`: the MCP server, on stdin and stdout. See the library for what it is.

use std::io::{BufRead, Write};
use std::process::ExitCode;

use olv_mcp::McpServer;

fn main() -> ExitCode {
    // Where the application left its address and token. Overridable so a second instance, or a
    // workspace somewhere else, can still be reached.
    let details = std::env::args_os()
        .nth(1)
        .map_or_else(McpServer::default_details, std::path::PathBuf::from);

    eprintln!("openlogviewer-mcp: reading {}", details.display());

    let mut server = McpServer::new(details);
    let stdin = std::io::stdin();
    let mut stdout = std::io::stdout().lock();

    for line in stdin.lock().lines() {
        let Ok(line) = line else {
            break;
        };

        if let Some(answer) = server.handle_line(&line)
            && writeln!(stdout, "{answer}").and_then(|()| stdout.flush()).is_err()
        {
            break;
        }
    }

    ExitCode::SUCCESS
}
