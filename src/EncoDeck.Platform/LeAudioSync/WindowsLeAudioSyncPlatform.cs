// SPDX-License-Identifier: GPL-3.0-only
namespace EncoDeck.Platform.LeAudioSync;

/// <summary>The production <see cref="ILeAudioSyncPlatform"/>: WASAPI streams and session lists, no elevation.</summary>
public sealed class WindowsLeAudioSyncPlatform : ILeAudioSyncPlatform
{
    public LeAudioSyncSupport CheckSupport() => LeAudioSyncEligibility.Check();

    public IKeepAliveStream OpenKeepAlive(string renderEndpointId) => AudioKeepAlive.Open(renderEndpointId);

    public bool HasOtherActiveSessions(string renderEndpointId) => AudioKeepAlive.HasOtherActiveSessions(renderEndpointId);

    public bool HasActiveCapture(string captureEndpointId) => AudioKeepAlive.HasActiveCapture(captureEndpointId);
}
