using Janus.Agent.Platform;
using Janus.Agent.Settings;
using System.IO.Ports;
using System.Text;

namespace Janus.Agent.Clipboard;

// Clipboard sync logic: handles inbound wire verbs (CLIPBOARD REQUEST /
// SET / CLEAR), reacts to local clipboard changes (OnChange, fired by
// MessageWindow's WM_CLIPBOARDUPDATE listener), and the manual Push
// action that broadcasts the current clipboard to the peer.
//
// State lives in ClipboardText (the hash-dedup tracking). This module
// is pure coordination: it reads/writes via ClipboardText, talks to
// the active serial port for outbound, and respects Config for size
// limits + outbound mode.

internal static class ClipboardSync
{
    // ---- Outbound: manual push ---------------------------------------

    // Single shared action for all manual push triggers (console key,
    // global hotkey, future tray). Grabs the current active port and
    // sends this PC's clipboard to the peer -- exactly what the
    // controller's 'c' command does via CLIPBOARD REQUEST, but
    // initiated locally. Safe to call from any thread; snapshots the
    // port first.
    public static void Push(string source)
    {
        SerialPort? port = Serial.ActivePort;
        if (port is null || !port.IsOpen)
        {
            Console.WriteLine($"clipboard push ({source}) ignored: no serial connection.");
            return;
        }

        Console.WriteLine($"clipboard push ({source}): sending clipboard to peer.");
        HandleRequest(port);
    }

    // ---- Inbound: wire-protocol handlers ------------------------------
    //
    // Called by Serial.HandleIncomingLine when it parses the matching
    // verb off the receive loop. Each handler is self-contained: talks
    // to ClipboardText for text/hash and to the SerialPort for any
    // outbound response.

    public static void HandleRequest(SerialPort port)
    {
        // Manual push triggered by the controller's 'c' command OR by a
        // local trigger via Push(). Always honored regardless of
        // outbound mode -- the user explicitly asked for this.
        string text = ClipboardText.GetText();
        byte[] rawBytes = Encoding.UTF8.GetBytes(text);

        if (rawBytes.Length > Config.ClipboardMaxBytes)
        {
            Console.WriteLine(
                $"clipboard refused (outbound): {rawBytes.Length} bytes exceeds {Config.ClipboardMaxBytes} limit");
            // Tell the other side to clear its clipboard so a stale value
            // doesn't silently paste.
            try
            {
                port.WriteLine("CLIPBOARD CLEAR");
            }
            catch (Exception ex) when (Serial.IsSerialException(ex))
            {
                Console.WriteLine($"clipboard clear send error: {ex.Message}");
            }
            return;
        }

        string encoded = Convert.ToBase64String(rawBytes);

        try
        {
            port.WriteLine($"CLIPBOARD DATA TEXT={encoded}");
        }
        catch (Exception ex) when (Serial.IsSerialException(ex))
        {
            Console.WriteLine($"clipboard send error: {ex.Message}");
            return;
        }

        ClipboardText.UpdateSyncedHash(rawBytes);
        Console.WriteLine($"clipboard sent ({rawBytes.Length} bytes)");
    }

    public static void HandleSet(string line)
    {
        // Inbound clipboard from the peer is always accepted regardless
        // of outbound policy. The policy controls what we BROADCAST, not
        // what we ACCEPT. Asymmetric by design: Personal-side wants to
        // receive from Work freely, but not leak its own clipboard to
        // Work.

        // Pull only the TEXT= payload; length check before any base64
        // decode allocation.
        const string marker = " TEXT=";
        int idx = line.IndexOf(marker, StringComparison.Ordinal);
        if (idx < 0)
        {
            return;
        }

        string encoded = line[(idx + marker.Length)..];

        byte[] rawBytes;
        try
        {
            rawBytes = Convert.FromBase64String(encoded);
        }
        catch (FormatException ex)
        {
            Console.WriteLine($"Clipboard decode error: {ex.Message}");
            return;
        }

        if (rawBytes.Length > Config.ClipboardMaxBytes)
        {
            // The other side violated the size contract (or the message
            // got corrupted). Clear local clipboard so nothing stale
            // lingers.
            Console.WriteLine(
                $"clipboard oversized inbound: {rawBytes.Length} bytes, clearing destination");
            ClipboardText.SetText(string.Empty);
            ClipboardText.UpdateSyncedHash(Array.Empty<byte>());
            return;
        }

        try
        {
            string text = Encoding.UTF8.GetString(rawBytes);
            // Record hash BEFORE we apply the change so the monitor event
            // that our own SetText is about to fire gets correctly
            // suppressed as a sync echo.
            ClipboardText.UpdateSyncedHash(rawBytes);
            ClipboardText.SetText(text);
            Console.WriteLine($"clipboard received ({rawBytes.Length} bytes)");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Clipboard set error: {ex.Message}");
        }
    }

    public static void HandleClear()
    {
        Console.WriteLine("clipboard clear received");
        ClipboardText.SetText(string.Empty);
        ClipboardText.UpdateSyncedHash(Array.Empty<byte>());
    }

    // ---- Outbound: monitor callback ----------------------------------
    //
    // Fired by MessageWindow when WM_CLIPBOARDUPDATE arrives. Decides
    // what to do based on outbound policy:
    //   Auto:   compare against the last synced hash; if different,
    //           push the new clipboard to the active port (subject to
    //           size tier limits).
    //   Manual: log a hint that the user can manually push via 'c' in
    //           the controller. Update the hash but do NOT send.
    //
    // The hash check is what keeps the two-agent loop from bouncing
    // forever in Auto mode: incoming CLIPBOARD SET on agent A updates
    // the hash before SetText fires, so A's own monitor callback sees
    // hash-match and suppresses the re-broadcast.

    public static void OnChange()
    {
        // Capture the port/device-id snapshot up front. The main loop
        // may null these out mid-callback on disconnect; we want a
        // consistent view for the whole send.
        SerialPort? port = Serial.ActivePort;
        string? deviceId = Serial.ActiveDeviceId;

        if (port is null || deviceId is null || !port.IsOpen)
        {
            return;
        }

        string text;
        try
        {
            text = ClipboardText.GetText();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Clipboard monitor read error: {ex.Message}");
            return;
        }

        byte[] rawBytes = Encoding.UTF8.GetBytes(text);

        // Suppress if this clipboard change was caused by our own sync
        // (either our SetText from an inbound line, or a previous
        // outbound we already broadcast). Applies in both Auto and
        // Manual modes.
        if (ClipboardText.IsAlreadySynced(rawBytes))
        {
            return;
        }

        // ---- MANUAL OUTBOUND MODE ------------------------------------
        //
        // Never auto-broadcast. Every local clipboard change is
        // announced to the console with a hint telling the user how to
        // push it manually. The hash is updated so we don't re-announce
        // the same content if the monitor fires again.
        //
        // Future extension point: this is where a tray notification or
        // popup with a "send to peer" button would hook in. Today it's
        // just a console message.
        if (Config.ClipboardOutboundMode == ClipboardOutboundMode.Manual)
        {
            if (rawBytes.Length > Config.ClipboardMaxBytes)
            {
                Console.WriteLine(
                    $"clipboard change ({rawBytes.Length} bytes) exceeds {Config.ClipboardMaxBytes} hard limit; cannot be sent.");
            }
            else
            {
                Console.WriteLine(
                    $"clipboard change ({rawBytes.Length} bytes); press 'c' in controller to send to peer.");
            }
            ClipboardText.UpdateSyncedHash(rawBytes);
            return;
        }

        // ---- AUTO OUTBOUND MODE --------------------------------------

        if (rawBytes.Length > Config.ClipboardMaxBytes)
        {
            // Over hard ceiling: refuse and tell the other side to clear
            // its clipboard so a stale value doesn't paste silently.
            Console.WriteLine(
                $"clipboard auto-sync refused: {rawBytes.Length} bytes exceeds {Config.ClipboardMaxBytes} hard limit");
            try
            {
                port.WriteLine("CLIPBOARD CLEAR");
            }
            catch (Exception ex) when (Serial.IsSerialException(ex))
            {
                Console.WriteLine($"clipboard clear send error: {ex.Message}");
            }
            // Record the oversized hash so we don't re-attempt every tick.
            ClipboardText.UpdateSyncedHash(rawBytes);
            return;
        }

        if (rawBytes.Length > Config.ClipboardAutoSyncBytes)
        {
            // Between auto-sync and hard ceiling: don't broadcast. User
            // can trigger a manual 'c' in the router if they want this
            // on the other side. Leave the other side's existing
            // clipboard alone (they can still paste whatever was there
            // before).
            Console.WriteLine(
                $"clipboard change {rawBytes.Length} bytes exceeds auto-sync threshold "
                + $"({Config.ClipboardAutoSyncBytes}); use manual 'c' to propagate.");
            ClipboardText.UpdateSyncedHash(rawBytes);
            return;
        }

        string encoded = Convert.ToBase64String(rawBytes);

        try
        {
            port.WriteLine($"CLIPBOARD DATA TEXT={encoded}");
            ClipboardText.UpdateSyncedHash(rawBytes);
            Console.WriteLine($"clipboard auto-sync sent ({rawBytes.Length} bytes)");
        }
        catch (Exception ex) when (Serial.IsSerialException(ex))
        {
            Console.WriteLine($"clipboard auto-sync send error: {ex.Message}");
        }
    }
}