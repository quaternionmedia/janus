namespace Janus.Agent.Logging;

// What subsystem a log line came from. Producers pick one at emission
// time; the GUI's main-view filter uses it to decide what to show by
// default (Switch, Clipboard, Serial visible; Keyboard, Mouse, System
// hidden unless Level is Warn or Error).
//
// Keep this small. Fine-grained categorization goes in the message text,
// not here -- adding a new value costs a compile-time update everywhere,
// and a category too narrow to appear often is noise on the filter UI.

internal enum LogCategory
{
    // Serial port lifecycle: open, close, disconnect, reconnect, protocol
    // errors. Anything about the wire itself.
    Serial,

    // Active-target changes and switch actions (manual, hotkey, lock,
    // shutdown, edge, dead-peer). Anything about "which PC is receiving
    // input right now."
    Switch,

    // Clipboard sync: local changes, pushes, sends, receives, size caps.
    Clipboard,

    // Keyboard events not otherwise covered (future: forwarded keystrokes
    // if agent ever gains that role again, key-based diagnostics).
    Keyboard,

    // Mouse events (cursor sends, future button remapping, wheel edge
    // switching diagnostics).
    Mouse,

    // Everything else: startup banner, config load, GUI lifecycle, hotkey
    // registration, message-pump events, uncategorized fallback.
    System,
}