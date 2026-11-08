//! olv-agent: the local API that lets an AI assistant read the session and, when a person has
//! armed it, stage and send tune changes. Ported from the C# `AgentServer`, `AgentApi`,
//! `AgentStaging`, and the write half of `AgentCommands`; see `rust/PLAN.md`.
//!
//! This is the security boundary between an AI and a real engine. In order from the outside:
//!
//! - [`agent::agent_server`]: loopback only, a token on every request (compared in constant time,
//!   never logged), a loopback `Host` and `Origin`, bounded in every direction.
//! - [`agent::agent_api`]: the one trait, [`AgentBridge`](agent::agent_api::AgentBridge), that is
//!   the whole of what an agent can reach. It has no burn and no controller command.
//! - [`agent::agent_writer`]: every gate in front of every byte an agent sends: arming, a stated
//!   reason, dangerous constants, magnitude, rate, idle, a real tune read off the controller. The
//!   only code in the application that sends an agent's bytes to an `EcuConnection` or a MaxxECU.
//! - [`agent::agent_staging`]: files for a person to pick up, confined to one folder.

pub mod agent;
