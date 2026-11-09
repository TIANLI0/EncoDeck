// SPDX-License-Identifier: GPL-3.0-only
namespace EncoDeck.Bluetooth.Control;

/// <summary>Windows' view of whether the device's Bluetooth link (ACL) is up.</summary>
public interface ILinkStateSource : IDisposable
{
    bool IsConnected { get; }

    /// <summary>Raised on an arbitrary thread when <see cref="IsConnected"/> changes.</summary>
    event EventHandler<bool>? ConnectionChanged;

    /// <summary>At least one bonded LE earbud is connected; does not verify audio streaming.</summary>
    bool IsLeAudio => false;

    EncoDeck.Core.State.LeAudioLinkState LeAudioLinks => EncoDeck.Core.State.LeAudioLinkState.Empty;

    /// <summary>Per-ear bond/ACL changes, including one ear dropping while the other stays up.</summary>
    event EventHandler? LinkDetailsChanged { add { } remove { } }

    /// <summary>The earbuds were switched to LE Audio on this PC (connected or not).</summary>
    bool IsLeAudioBonded => false;

    /// <summary>Raised on an arbitrary thread when <see cref="IsLeAudio"/> changes.</summary>
    event EventHandler? TransportChanged { add { } remove { } }

    /// <summary>Only BlueCaster's own adapter is linked to the device, not Windows: control runs through the bridge.</summary>
    bool IsViaBridge => false;

    /// <summary>
    /// Stops asking Windows to keep (and re-establish) the earbuds' LE links, keeping the watch: they are being handed
    /// over to another device and must not be brought back.
    /// </summary>
    void ReleaseMaintainedLinks() { }
}
