using Janus.Agent.Clipboard;
using Janus.Agent.Events;
using Janus.Agent.Platform;
using System.Windows;
using UserControl = System.Windows.Controls.UserControl;

namespace Janus.Agent.Gui.Shared;

// Reusable footer/actions block. Placed at the bottom of MainView's
// sidebar today; will also sit at the bottom of DiagnosticsView's
// sidebar once that lands in stage 2.
//
// Buttons call the same static entry points the tray menu uses. The
// "gui" source label makes GUI-initiated actions distinguishable from
// tray-click / hotkey / lock / shutdown sources in the log stream.
//
// The gear icon raises a bubbling RoutedEvent (SettingsRequested)
// instead of opening the modal directly. The root window catches the
// event (via AddHandler on the window's hwnd source) and opens the
// modal itself -- keeps the modal at window level so both tabs share
// a single instance.

public partial class ActionsPanel : UserControl
{
    // ---- RoutedEvent for settings open --------------------------------

    public static readonly RoutedEvent SettingsRequestedEvent =
        EventManager.RegisterRoutedEvent(
            name:             nameof(SettingsRequested),
            routingStrategy:  RoutingStrategy.Bubble,
            handlerType:      typeof(RoutedEventHandler),
            ownerType:        typeof(ActionsPanel));

    public event RoutedEventHandler SettingsRequested
    {
        add    => AddHandler(SettingsRequestedEvent, value);
        remove => RemoveHandler(SettingsRequestedEvent, value);
    }

    public ActionsPanel()
    {
        InitializeComponent();
    }

    // ---- Button handlers ----------------------------------------------

    private void SwitchAction_Click(object sender, RoutedEventArgs e)
    {
        try { Actions.SwitchToPeer("gui"); }
        catch (Exception ex) { Log.System.Error(ex, "GUI switch action error."); }
    }

    private void ClipboardAction_Click(object sender, RoutedEventArgs e)
    {
        try { ClipboardSync.Push("gui"); }
        catch (Exception ex) { Log.Clipboard.Error(ex, "GUI clipboard action error."); }
    }

    private void ReconnectAction_Click(object sender, RoutedEventArgs e)
    {
        try { Serial.RequestReconnect(); }
        catch (Exception ex) { Log.Serial.Error(ex, "GUI reconnect action error."); }
    }

    private void SettingsButton_Click(object sender, RoutedEventArgs e)
    {
        RaiseEvent(new RoutedEventArgs(SettingsRequestedEvent, this));
    }
}