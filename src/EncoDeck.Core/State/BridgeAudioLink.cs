// SPDX-License-Identifier: GPL-3.0-only
namespace EncoDeck.Core.State;

/// <summary>
/// BlueCaster streams to this device through its own USB adapter. Windows does not see that link, so it
/// says nothing about EncoDeck's control channel: the earbuds can be connected here while Windows reports them
/// disconnected (or both, with dual-device connection).
/// </summary>
/// <param name="Codec">As BlueCaster names it ("LDAC", "LHDC V5"); null until the stream is configured.</param>
/// <param name="CanCarryControl">The bridge can open RFCOMM channels to the device (A2DPWB 2.0.2, Classic audio): the
/// control channel can run through it while Windows has no link.</param>
public sealed record BridgeAudioLink(bool IsStreaming, string? Codec, int BitrateKbps, int SampleRate, string? Profile, bool CanCarryControl = false);
