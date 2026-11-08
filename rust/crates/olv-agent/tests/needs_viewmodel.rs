//! The C# agent tests that can only be run against the application's view model, which is not
//! ported yet (package `w8-viewmodel`). Each is here so the list is not lost, with what it has to
//! prove once the view model exists.
//!
//! What the view model does *not* own is already proved without it: every gate (arming, the
//! disconnect, the rationale, dangerous constants, magnitude, rate, idle, placeholder and file
//! tunes, staging paths, the MaxxECU ceiling) in `agent_writes.rs`, `agent_end_to_end.rs` and
//! `agent_maxx_ceiling.rs`, against a real `EcuConnection` over a fake controller. What is left
//! is the view model's own wiring of them, which is exactly what must not be skipped: the view
//! model must call `AgentWriter::disconnect` on every disconnect, `connect` and `set_tune` on every
//! connection (all four paths: serial ECU, OBD2 adapter, Subaru over SSM, MaxxECU), and implement
//! `WriterHost::live_rpm` from the live session, not from the open log.

// ----- AgentActivityIndicatorTests (the view model's timers) ------------------------------------

#[test]
#[ignore = "needs w8-viewmodel"]
fn nothing_is_active_before_any_agent_request_arrives() {
    unimplemented!("AgentActivityIndicatorTests.NothingIsActiveBeforeAnyAgentRequestArrives");
}

#[test]
#[ignore = "needs w8-viewmodel"]
fn a_read_marks_the_indicator_active_but_not_writing() {
    unimplemented!("AgentActivityIndicatorTests.AReadMarksTheIndicatorActiveButNotWriting");
}

#[test]
#[ignore = "needs w8-viewmodel"]
fn a_write_marks_both_the_general_and_the_ecu_specific_indicator() {
    unimplemented!("AgentActivityIndicatorTests.AWriteMarksBothTheGeneralAndTheEcuSpecificIndicator");
}

#[test]
#[ignore = "needs w8-viewmodel"]
fn a_read_after_a_write_leaves_the_ecu_indicator_alone_until_it_decays() {
    unimplemented!("AgentActivityIndicatorTests.AReadAfterAWriteLeavesTheEcuIndicatorAloneUntilItDecays");
}

#[test]
#[ignore = "needs w8-viewmodel"]
fn stopping_the_agent_api_clears_the_indicator_immediately() {
    unimplemented!("AgentActivityIndicatorTests.StoppingTheAgentApiClearsTheIndicatorImmediately");
}

// ----- ConnectedEcuTests: the agent half --------------------------------------------------------

#[test]
#[ignore = "needs w8-viewmodel"]
fn connecting_feeds_the_agent_stream_whatever_was_connected_to() {
    unimplemented!("ConnectedEcuTests.ConnectingFeedsTheAgentStreamWhateverWasConnectedTo");
}

#[test]
#[ignore = "needs w8-viewmodel"]
fn disconnecting_disarms_the_agent_through_the_view_model() {
    // The writer's half (disconnect closes the gate, reconnecting does not reopen it) is
    // `disconnecting_disarms_and_reconnecting_does_not_arm_again`; this is the view model
    // calling it on every route out of a connection.
    unimplemented!("ConnectedEcuTests.DisconnectingDisarmsTheAgent");
}

#[test]
#[ignore = "needs w8-viewmodel"]
fn the_bridge_reports_what_is_connected_and_names_the_roles() {
    unimplemented!("ConnectedEcuTests.TheBridgeReportsWhatIsConnectedAndNamesTheRoles");
}

#[test]
#[ignore = "needs w8-viewmodel"]
fn and_names_the_role_of_each_channel_so_one_agent_works_across_firmwares() {
    unimplemented!("ConnectedEcuTests.AndNamesTheRoleOfEachChannelSoOneAgentWorksAcrossFirmwares");
}
