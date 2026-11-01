// SPDX-License-Identifier: GPL-3.0-only
using EncoDeck.Protocol.Transport;

namespace EncoDeck.Bluetooth.LeAudio;

/// <summary>Chooses the transport before any Classic access, SDP lookup or socket operation.</summary>
internal static class OppoControlChannelRouter
{
    /// <param name="hasClassicBond">
    /// Whether the Classic bond Windows derived from the LE bond (CTKD) exists. On LE Audio, control then runs over
    /// Classic RFCOMM first: it keeps the Classic link up, and the earbuds drop a host's LE links when its Classic link
    /// ends (Enco X4). Windows' audio choice, not this link, decides between LC3 and A2DP.
    /// </param>
    internal static async Task<ChannelOpenResult> OpenAsync(
        Func<Task<bool>> isLeBonded,
        Func<Task<ChannelOpenResult>> openGatt,
        Func<Task<ChannelOpenResult>> openClassic,
        Func<bool>? hasClassicBond = null)
    {
        bool leBonded;
        try
        {
            leBonded = await isLeBonded().ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException)
        {
            return ChannelOpenResult.Failed(ChannelOpenStatus.ConnectFailed,
                $"Could not determine the LE Audio pairing state (0x{ex.HResult:X8}): {ex.Message}; Classic control was not opened.");
        }

        if (!leBonded) return await openClassic().ConfigureAwait(false);

        ChannelOpenResult? classic = null;
        if (hasClassicBond?.Invoke() == true)
        {
            classic = await openClassic().ConfigureAwait(false);
            if (classic.Status == ChannelOpenStatus.Opened) return classic;
        }

        // Without the Classic bond (or with RFCOMM unavailable) GATT is the only way; a missing/busy LE service is retryable.
        var result = await openGatt().ConfigureAwait(false);
        return result.Status is ChannelOpenStatus.AccessDenied or ChannelOpenStatus.ServiceNotFound
            ? ChannelOpenResult.Failed(ChannelOpenStatus.ConnectFailed, result.Detail ?? classic?.Detail ?? "OPPO GATT is not ready.")
            : result;
    }
}
