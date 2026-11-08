//! Starting and stopping the agent API, with its token: `StartAgentApi` and `StopAgentApi` from
//! `AgentCommands.cs`.
//!
//! The application owns one of these while the API is on. Starting it makes a fresh token (32
//! random bytes from the operating system, never reused, never chosen by a person, never shown),
//! starts the loopback server, and leaves the address and token where an agent can read them
//! ([`token_file`]); stopping it, or dropping it, deletes that file, because it would otherwise
//! describe a socket that is no longer there.
//!
//! Nothing here arms writing. Arming is [`AgentWriter::set_armed`](super::agent_writer::AgentWriter::set_armed),
//! a person's act in the application, and the service neither calls it nor can reach it.

use std::sync::Arc;

use olv_store::store::workspace::Workspace;

use super::agent_api::AgentBridge;
use super::agent_server::{AgentServer, AgentServerSettings};
use super::token_file;

pub struct AgentService {
    server: AgentServer,
    workspace: Workspace,
}

// The token lives in the server and in the file; this prints neither.
impl std::fmt::Debug for AgentService {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.debug_struct("AgentService")
            .field("address", &self.server.address())
            .finish_non_exhaustive()
    }
}

impl AgentService {
    /// Starts the API on `port` (zero for any free one) and writes `agent-api.json`. On success the
    /// sentence the application shows; on failure the one it shows instead, as the C# words them.
    /// A token file that could not be written stops the server again rather than leaving one
    /// running that nobody can be told how to reach.
    pub fn start(bridge: Arc<dyn AgentBridge>, workspace: Workspace, port: u16) -> Result<(Self, String), String> {
        let failed =
            |why: String| format!("The agent API could not start: {why} Another program may already have that port.");

        let token = token_file::new_token().map_err(|e| failed(e.to_string()))?;

        let mut server = AgentServer::new(
            bridge,
            AgentServerSettings {
                port,
                ..AgentServerSettings::new(token.clone())
            },
        )
        .map_err(|e| failed(e.to_string()))?;

        server.start().map_err(|e| failed(e.to_string()))?;

        let file = match token_file::write(&workspace, server.port(), &token) {
            Ok(file) => file,
            Err(e) => {
                server.dispose();
                return Err(failed(e.to_string()));
            }
        };

        let message = format!(
            "The agent API is listening on {}. Its token is in {}.",
            server.address(),
            file.display()
        );

        Ok((Self { server, workspace }, message))
    }

    pub fn server(&self) -> &AgentServer {
        &self.server
    }

    /// Where an agent points itself.
    pub fn address(&self) -> String {
        self.server.address()
    }
}

impl Drop for AgentService {
    fn drop(&mut self) {
        self.server.dispose();

        // The file describes a socket that is no longer there.
        token_file::remove(&self.workspace);
    }
}
