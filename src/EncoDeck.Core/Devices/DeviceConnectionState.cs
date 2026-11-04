// SPDX-License-Identifier: GPL-3.0-only
namespace EncoDeck.Core.Devices;

/// <summary>
/// Combined view of the Windows Bluetooth link and EncoDeck's private control channel.
/// The audio link (A2DP/HFP, owned by Windows) and the control channel (RFCOMM, owned by us)
/// are deliberately separate: audio can work while control is unavailable.
/// </summary>
public enum DeviceConnectionState
{
    /// <summary>No supported device is paired with this PC.</summary>
    NotDetected,

    /// <summary>Paired, but Windows reports no Bluetooth connection.</summary>
    PairedNotConnected,

    /// <summary>Windows has the device connected; the control channel has not been opened yet.</summary>
    AudioConnected,

    /// <summary>Opening the RFCOMM control channel.</summary>
    ControlConnecting,

    /// <summary>Control channel open and the device answered.</summary>
    ControlReady,

    /// <summary>Device connected but the control channel cannot be used (missing service, refused, retries exhausted).</summary>
    ControlUnavailable,

    /// <summary>Control channel was lost and a bounded retry is scheduled or running.</summary>
    Reconnecting,

    /// <summary>The device was connected earlier in this session and is now disconnected.</summary>
    Disconnected,

    /// <summary>USB audio receiver is present; its presence does not prove a radio connection to the headset.</summary>
    UsbReceiverConnected,

    /// <summary>The USB receiver returned a validated active wireless headset link.</summary>
    UsbHeadsetConnected,

    /// <summary>
    /// The user disconnected the device in EncoDeck: its control channel stays closed and nothing reconnects it, even
    /// when Windows links it again, until the user connects it in EncoDeck.
    /// </summary>
    DisconnectedByUser,
}

public static class DeviceConnectionStateExtensions
{
    public static bool IsLinkUp(this DeviceConnectionState state) => state is
        DeviceConnectionState.AudioConnected or
        DeviceConnectionState.UsbHeadsetConnected or
        DeviceConnectionState.ControlConnecting or
        DeviceConnectionState.ControlReady or
        DeviceConnectionState.ControlUnavailable or
        DeviceConnectionState.Reconnecting;

    public static bool CanSendCommands(this DeviceConnectionState state) => state == DeviceConnectionState.ControlReady;
}
