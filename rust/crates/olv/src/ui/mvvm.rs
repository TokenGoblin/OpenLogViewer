//! Ported from `Mvvm.cs`. Owned by package `w6-ui-base`.
//!
//! Nothing to port, on purpose.
//!
//! The C# file is `ObservableObject` (an `INotifyPropertyChanged` base so a view can be told that
//! a property changed) and `RelayCommand` (an `ICommand` whose `CanExecuteChanged` rides WPF's
//! `CommandManager.RequerySuggested`). Both exist because WPF keeps a tree of controls that holds
//! on to values and must be told when they are stale.
//!
//! egui is immediate mode: no control survives the frame, so there is nothing to notify. Every
//! screen reads the application state as it draws, and a change shows on the next frame (a
//! handler that changes state calls `Context::request_repaint`, which `ThemeManager::apply` and
//! `InterfaceScale::apply` already do). A command is a method on the state, and each `Can...`
//! rule in UI-SPEC is evaluated every frame to enable or disable its control, so no "requery"
//! mechanism is needed either. This matches UI-SPEC §1, "State plumbing (Mvvm)".
//!
//! Where a screen does cache something derived from state, it keys the cache on a generation
//! counter, as `ThemeManager::generation` does for the theme.
