// SPDX-License-Identifier: GPL-3.0-only
using System.IO;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using EncoDeck.Core.Commands;
using EncoDeck.Core.Privacy;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.Advertisement;
using Windows.Devices.Enumeration;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Storage.Streams;

namespace EncoDeck.Bluetooth.LeAudio;

public enum LeAudioPairingStage
{
    DisablingMultipoint,
    RemovingPairings,
    WaitingForCleanup,
    WaitingForPairingMode,
    PairingPrimary,
    /// <summary>Waiting for control over LE to report LE Audio (0x1F) before the other earbud pairs.</summary>
    ReportingLeTransport,
    PairingSecondary,
    WaitingForLinks,
}

public enum LeAudioPairingOutcome
{
    /// <summary>Multipoint could not be disabled and confirmed; no mode switch or pairing change was attempted.</summary>
    MultipointDisableFailed,
    /// <summary>Both earbuds are bonded over LE; Windows derived the Classic bond (control) from the primary.</summary>
    Paired,
    /// <summary>Both LE links are up, but Windows still exposes an enabled Classic A2DP render endpoint.</summary>
    PairedWithClassicAudio,
    /// <summary>Both LE links are up, but no LE Audio render endpoint was found; Classic endpoints were left untouched.</summary>
    PairedButLeAudioEndpointMissing,
    /// <summary>Both LE bonds exist, but both ACL connections have not yet been observed.</summary>
    PairedWaitingForLinks,
    /// <summary>Only the primary earbud is bonded: audio plays on one side.</summary>
    PrimaryOnly,
    /// <summary>This PC's pairings were removed; the user pairs the earbuds again in Windows Settings.</summary>
    Removed,
    /// <summary>A previously disabled Classic endpoint could not be re-enabled; pairings were kept.</summary>
    ClassicAudioEndpointRestoreFailed,
    PairingModeNotSeen,
    PairingFailed,
}

public sealed record LeAudioPairingResult(
    LeAudioPairingOutcome Outcome,
    string? Detail = null,
    LeAudioEndpointInspection? AudioEndpoint = null);

/// <summary>Bonds that make up the earbuds' LE Audio setup on this PC.</summary>
public sealed record LeAudioBondState(bool PrimaryBonded, bool PrimaryConnected, bool SecondaryBonded, bool ClassicBonded)
{
    public bool SecondaryConnected { get; init; }
    public bool BothConnected => PrimaryConnected && SecondaryConnected;

    /// <summary>Render-endpoint evidence; null when inspection was unavailable.</summary>
    public LeAudioEndpointInspection? AudioEndpoints { get; init; }
}

/// <summary>
/// Switches OPPO earbuds between Windows' Classic (A2DP) and LE Audio (LC3) connections.
/// The primary earbud answers at the same public address over Classic and LE, but is not
/// LE-discoverable, so Windows' "Add device" always pairs it over Classic. Pairing its LE
/// identity directly makes Windows derive the Classic bond (CTKD) into the same device
/// container, without Classic audio services. The secondary earbud does not advertise at all;
/// its address is in the primary's scan response (manufacturer 0xBF0D). Verified on the Enco X4.
/// </summary>
public static class LeAudioPairing
{
    /// <summary>Manufacturer data id whose payload <c>[00 00][address LE ×6]..</c> names the secondary earbud.</summary>
    public const ushort SecondaryAddressCompanyId = 0xBF0D;

    private static readonly TimeSpan PairingModeTimeout = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan ScanResponseWait = TimeSpan.FromSeconds(10);
    private const int PairAttempts = 5;
    private const int IllegalMethodCall = unchecked((int)0x8000000E);
    private static readonly ConcurrentDictionary<ulong, ulong> KnownSecondaryAddresses = new();

    /// <summary>Step-by-step trace of the LE Audio switch (addresses already redacted); included in diagnostics.</summary>
    public static string DiagnosticLogPath => Path.Combine(Path.GetTempPath(), "enco-leaudio.log");

    private static void Log(string message)
    {
        try
        {
            File.AppendAllText(DiagnosticLogPath,
                $"{DateTime.Now:O} {message}{Environment.NewLine}");
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    /// <summary>
    /// Reads the secondary earbud's address from the primary's 0xBF0D manufacturer data. Accepted
    /// only when it differs from the primary and shares its OUI, so a foreign advert cannot redirect pairing.
    /// </summary>
    public static bool TryParseSecondaryAddress(ulong primary, ReadOnlySpan<byte> data, out ulong secondary)
    {
        secondary = 0;
        if (data.Length < 8) return false;
        ulong address = 0;
        for (int i = 7; i >= 2; i--) address = (address << 8) | data[i];
        if (address == primary || address >> 24 != primary >> 24) return false;
        secondary = address;
        return true;
    }

    public static async Task<LeAudioBondState> GetStateAsync(ulong primary, CancellationToken cancellationToken = default)
    {
        using var le = await OpenLeAsync(primary, cancellationToken).ConfigureAwait(false);
        using var classic = await OpenClassicAsync(primary, cancellationToken).ConfigureAwait(false);
        bool primaryBonded = await LeAudioLink.IsBondedAsync(primary, cancellationToken).ConfigureAwait(false);
        var secondary = await FindSecondaryBondsAsync(primary, PairName(primary, classic, le), cancellationToken).ConfigureAwait(false);
        LeAudioEndpointInspection? endpoints = null;
        try
        {
            try
            {
                endpoints = await LeAudioClassicAudioEndpoint.InspectAsync(primary, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException)
            {
                Log($"Audio endpoint inspection failed: 0x{ex.HResult:X8}");
                endpoints = new(LeAudioAudioEndpointState.Unknown, Detail: ex.Message);
            }
            return new LeAudioBondState(
                primaryBonded,
                le?.ConnectionStatus == BluetoothConnectionStatus.Connected,
                secondary.Count > 0,
                classic?.DeviceInformation.Pairing.IsPaired == true)
            {
                SecondaryConnected = secondary.Any(d => d.ConnectionStatus == BluetoothConnectionStatus.Connected),
                AudioEndpoints = endpoints,
            };
        }
        finally
        {
            foreach (var other in secondary) other.Dispose();
        }
    }

    /// <summary>
    /// Waits for the user to open pairing mode, then removes this PC's pairings of the earbuds and
    /// bonds the primary and the secondary over LE. An optional callback sets and confirms the mode. A timeout or
    /// cancellation before pairing mode leaves the existing pairings untouched.
    /// </summary>
    public static async Task<LeAudioPairingResult> SwitchToLeAudioAsync(ulong primary, IProgress<LeAudioPairingStage>? progress, CancellationToken cancellationToken, Func<bool, CancellationToken, Task<CommandResult>>? setLeBrSwitch = null,
        Func<CancellationToken, Task<CommandResult>>? disableMultipoint = null,
        bool useSetPairing = true, Action<string>? diagnosticLog = null,
        Func<CancellationToken, Task<CommandResult>>? reportLeTransport = null)
    {
        void Log(string message)
        {
            LeAudioPairing.Log(message);
            diagnosticLog?.Invoke(message);
        }
        cancellationToken.ThrowIfCancellationRequested();
        using var pairingOperation = LeAudioLink.BeginPairingOperation(primary);
        progress?.Report(LeAudioPairingStage.DisablingMultipoint);
        CommandResult multipoint;
        try
        {
            // Required even when cached state says "off": only a fresh device read-back confirms it.
            multipoint = disableMultipoint is null ? CommandResult.NotConnected
                : await disableMultipoint(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException)
        {
            Log($"Disabling multipoint threw: 0x{ex.HResult:X8}");
            multipoint = new(CommandStatus.TransportError);
        }
        cancellationToken.ThrowIfCancellationRequested();
        Log($"Disable multipoint before LE Audio: {multipoint.Status}; confirmed={multipoint.IsSuccess}");
        if (!multipoint.IsSuccess) return new(LeAudioPairingOutcome.MultipointDisableFailed, multipoint.Status.ToString());

        // Official clients set the headset-side LE/BR switch before switching. The firmware syncs it
        // to both earbuds and persists it in NV, which keeps reconnect and dual-ear mode consistent
        // (X4: with it off the earbuds keep falling back to Classic).
        bool withoutLeBrSwitch = false;
        if (setLeBrSwitch is not null)
        {
            try
            {
                var modeResult = await setLeBrSwitch(true, cancellationToken).ConfigureAwait(false);
                withoutLeBrSwitch = modeResult.Status == CommandStatus.Unsupported;
                Log($"LE/BR mode request: {modeResult.Status}; confirmed={modeResult.IsSuccess}");
            }
            catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException)
            {
                Log($"LE/BR mode request threw: 0x{ex.HResult:X8}");
                // Best effort: the switch is an optimization, not a prerequisite for LE pairing.
            }
        }

        progress?.Report(LeAudioPairingStage.WaitingForPairingMode);
        // 0x0433 makes the earbuds re-apply their mode and can drop the link; give them time to
        // settle before looking for the pairing advertisement.
        await Task.Delay(TimeSpan.FromSeconds(3), cancellationToken).ConfigureAwait(false);
        var advert = await WaitForPairingModeAsync(primary, cancellationToken).ConfigureAwait(false);
        if (advert is null) return new(LeAudioPairingOutcome.PairingModeNotSeen);
        if (advert.Secondary is null) return new(LeAudioPairingOutcome.PairingFailed, "The other earbud's identity was not advertised; existing pairings were kept.");
        KnownSecondaryAddresses[primary] = advert.Secondary.Value;
        Log($"pairing mode: secondary={(advert.Secondary is { } seen ? new BluetoothAddress(seen).ToRedactedString() : "none")}");

        // Windows may publish the CTKD Classic entry before the LE bond is visible. Block control
        // opens throughout this interval so discovery cannot start RFCOMM while LE pairing runs.
        using var pairingChange = LeAudioLink.BeginPairingChange(primary);
        LeAudioLink.MarkPreferred(primary);
        progress?.Report(LeAudioPairingStage.RemovingPairings);
        await RemoveAllAsync(primary, cancellationToken).ConfigureAwait(false);
        // Pairing LE before Windows has torn down the old Classic device makes CTKD bring back its
        // A2DP/HFP services: the primary then stays on Classic audio and only the other earbud plays.
        progress?.Report(LeAudioPairingStage.WaitingForCleanup);
        await WaitForNodesRemovedAsync(primary, "BTH", cancellationToken, advert.Secondary).ConfigureAwait(false);

        bool transportReportAttempted = false;
        async Task PrepareSecondaryAsync(CancellationToken ct)
        {
            if (transportReportAttempted || !withoutLeBrSwitch || reportLeTransport is null) return;
            // Called after native primary PairAsync returns, before set-member PairAsync starts.
            // Opening control earlier would race CTKD and could select the Classic transport.
            // Native pairing may complete before the paired AEP is published. Do not release
            // members on that transient false result, or preparation would still be skipped.
            var bondWait = System.Diagnostics.Stopwatch.StartNew();
            while (!await LeAudioLink.IsBondedAsync(primary, ct).ConfigureAwait(false))
            {
                if (bondWait.Elapsed >= TimeSpan.FromSeconds(10))
                    throw new TimeoutException("Primary LE bond was not published; secondary pairing was not started.");
                await Task.Delay(TimeSpan.FromMilliseconds(250), ct).ConfigureAwait(false);
            }
            transportReportAttempted = true;
            pairingChange.Dispose();
            progress?.Report(LeAudioPairingStage.ReportingLeTransport);
            Log("Preparing primary LE control before secondary pairing (including Windows set members)");
            if (await ReportLeTransportAsync(reportLeTransport, Log, ct).ConfigureAwait(false))
                await Task.Delay(TwsBondSyncSettle, ct).ConfigureAwait(false);
            progress?.Report(LeAudioPairingStage.PairingSecondary);
        }

        Func<CancellationToken, Task>? prepareMembers = withoutLeBrSwitch && reportLeTransport is not null
            ? PrepareSecondaryAsync : null;
        var setPairing = useSetPairing ? new LeAudioSetPairing(primary, advert.Secondary.Value, Log, prepareMembers) : null;
        Log($"LE pairing strategy: {(useSetPairing ? "Windows set discovery + explicit peer fallback" : "legacy explicit addresses")}");
        progress?.Report(LeAudioPairingStage.PairingPrimary);
        Log("pairing primary");
        var status = await PairLeAsync(primary, cancellationToken, setPairing, Log).ConfigureAwait(false);
        if (status is not (DevicePairingResultStatus.Paired or DevicePairingResultStatus.AlreadyPaired))
        {
            Log($"primary failed ({status}), retrying once");
            await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken).ConfigureAwait(false);
            status = await PairLeAsync(primary, cancellationToken, setPairing, Log).ConfigureAwait(false);
        }
        setPairing?.LogSummary();
        bool primaryBonded = status is DevicePairingResultStatus.Paired or DevicePairingResultStatus.AlreadyPaired
            && await LeAudioLink.IsBondedAsync(primary, cancellationToken).ConfigureAwait(false);
        Log($"primary status: {status} bonded={primaryBonded}");
        if (!primaryBonded) return new(LeAudioPairingOutcome.PairingFailed, status.ToString());

        if (advert.Secondary is null) return new(LeAudioPairingOutcome.PrimaryOnly, "secondary address not advertised");
        var secondary = advert.Secondary.Value;
        // Set discovery may have bonded the other earbud under its LE identity, which is not always the
        // scan-response address (Enco X3); follow the bond that actually exists.
        if (setPairing?.PairedMember is { } member && member != secondary)
        {
            Log($"secondary bonded by set discovery at {new BluetoothAddress(member)} (advertised {new BluetoothAddress(secondary)})");
            secondary = member;
            KnownSecondaryAddresses[primary] = member;
        }

        // The secondary never advertises; it is reachable through the primary's TWS link. Wait for
        // the primary's LE link to come up before pairing it.
        for (int i = 0; i < 20; i++)
        {
            if (await LeAudioLink.IsConnectedAsync(primary, cancellationToken).ConfigureAwait(false)) break;
            await Task.Delay(TimeSpan.FromMilliseconds(500), cancellationToken).ConfigureAwait(false);
        }

        // Without set pairing, prepare LE control before the explicit peer attempt too. The 0x1F
        // transport report follows the phone's sequence; its effect on X3 bonding needs hardware validation.
        if (withoutLeBrSwitch && reportLeTransport is not null
            && !await IsLeBondedAsync(secondary, cancellationToken).ConfigureAwait(false))
        {
            await PrepareSecondaryAsync(cancellationToken).ConfigureAwait(false);
        }

        progress?.Report(LeAudioPairingStage.PairingSecondary);
        bool secondaryBonded = await IsLeBondedAsync(secondary, cancellationToken).ConfigureAwait(false);
        var secondaryStatus = DevicePairingResultStatus.AlreadyPaired;
        if (secondaryBonded) Log("Secondary already bonded; skipping explicit pairing");
        else
        {
            Log($"Secondary not yet bonded; pairing secondary {new BluetoothAddress(secondary)} explicitly");
            secondaryStatus = await PairLeAsync(secondary, cancellationToken, diagnosticLog: Log).ConfigureAwait(false);
            secondaryBonded = secondaryStatus is DevicePairingResultStatus.Paired or DevicePairingResultStatus.AlreadyPaired
                && await IsLeBondedAsync(secondary, cancellationToken).ConfigureAwait(false);
            Log($"secondary status: {secondaryStatus} bonded={secondaryBonded}");
        }
        if (!secondaryBonded)
        {
            await Task.Delay(TimeSpan.FromSeconds(3), cancellationToken).ConfigureAwait(false);
            Log($"secondary retry {new BluetoothAddress(secondary)}");
            secondaryStatus = await PairLeAsync(secondary, cancellationToken, diagnosticLog: Log).ConfigureAwait(false);
            secondaryBonded = secondaryStatus is DevicePairingResultStatus.Paired or DevicePairingResultStatus.AlreadyPaired
                && await IsLeBondedAsync(secondary, cancellationToken).ConfigureAwait(false);
            Log($"secondary retry status: {secondaryStatus} bonded={secondaryBonded}");
        }
        if (!secondaryBonded) return new(LeAudioPairingOutcome.PrimaryOnly, secondaryStatus.ToString());
        progress?.Report(LeAudioPairingStage.WaitingForLinks);
        bool bothConnected = await WaitForBothLinksAsync(primary, secondary, cancellationToken).ConfigureAwait(false);
        Log($"LE pair complete: both bonded=true; both ACL connected={bothConnected}; audio streaming unverified");

        // Before the audio endpoints: the derived Classic pairing would otherwise connect A2DP first.
        try
        {
            await RemoveClassicPairingAsync(primary, Log, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException)
        {
            Log($"Classic pairing removal failed: 0x{ex.HResult:X8}");
        }

        LeAudioEndpointResult endpointResult = new(LeAudioEndpointOutcome.BlockedLeLinks);
        if (bothConnected)
        {
            try
            {
                // Audio PnP enumeration can finish after both ACL links. Inspect without changing
                // endpoints while waiting, then call the existing guarded suppression only once.
                Log("Waiting up to 30 seconds for the LE Audio render endpoint");
                var endpointDeadline = System.Diagnostics.Stopwatch.StartNew();
                while (!(await LeAudioClassicAudioEndpoint.InspectAsync(primary, cancellationToken).ConfigureAwait(false)).LeAudioRenderPresent
                    && endpointDeadline.Elapsed < TimeSpan.FromSeconds(30))
                {
                    await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken).ConfigureAwait(false);
                }
                // Only the Classic A2DP render node is disabled, and only after both LE links are up.
                endpointResult = await LeAudioClassicAudioEndpoint.SuppressAsync(primary, true, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException)
            {
                Log($"Classic audio endpoint suppression failed: 0x{ex.HResult:X8}");
                endpointResult = new(LeAudioEndpointOutcome.Failed, ex.Message);
            }
        }
        Log($"Classic audio endpoint: {endpointResult.Outcome}; {endpointResult.Detail}");

        return new(ClassifyOutcome(bothConnected, endpointResult), endpointResult.Detail, endpointResult.Inspection);
    }

    /// <summary>
    /// Maps LE pairing and endpoint evidence to the user-visible outcome. A paired result is only
    /// returned when both LE links are up and no enabled Classic render endpoint remains; a clean
    /// re-pair (no Classic A2DP node) is therefore <see cref="LeAudioPairingOutcome.Paired"/>.
    /// </summary>
    public static LeAudioPairingOutcome ClassifyOutcome(bool bothConnected, LeAudioEndpointResult? endpoint)
    {
        if (!bothConnected) return LeAudioPairingOutcome.PairedWaitingForLinks;
        if (endpoint is null) return LeAudioPairingOutcome.PairedWithClassicAudio;
        if (endpoint.Inspection is null) return LeAudioPairingOutcome.PairedWithClassicAudio;
        // A missing/ambiguous LE endpoint is the more actionable diagnosis even when the Classic
        // endpoint is still enabled: nothing was changed, so it must not look like a Classic-only
        // fallback failure.
        if (endpoint.Inspection is { LeAudioRenderPresent: false })
            return LeAudioPairingOutcome.PairedButLeAudioEndpointMissing;
        if (endpoint.Outcome is LeAudioEndpointOutcome.BlockedLeAudioEndpointMissing or LeAudioEndpointOutcome.Ambiguous)
            return LeAudioPairingOutcome.PairedButLeAudioEndpointMissing;
        if (endpoint.Inspection?.HasResidualClassicAudio == true)
            return LeAudioPairingOutcome.PairedWithClassicAudio;
        return endpoint.Outcome switch
        {
            LeAudioEndpointOutcome.Disabled or LeAudioEndpointOutcome.AlreadyLeAudioOnly or LeAudioEndpointOutcome.NoClassicEndpoint =>
                LeAudioPairingOutcome.Paired,
            _ => LeAudioPairingOutcome.PairedWithClassicAudio,
        };
    }

    private static readonly TimeSpan LeTransportReportTimeout = TimeSpan.FromSeconds(45);
    private static readonly TimeSpan TwsBondSyncSettle = TimeSpan.FromSeconds(3);

    /// <summary>
    /// Retries the 0x1F report until control has reopened over LE and the earbuds acknowledge it. Best effort:
    /// false (logged) when it never got through; the secondary is still tried.
    /// </summary>
    internal static async Task<bool> ReportLeTransportAsync(Func<CancellationToken, Task<CommandResult>> report, Action<string> log,
        CancellationToken cancellationToken, TimeSpan? timeout = null, TimeSpan? interval = null)
    {
        var deadline = System.Diagnostics.Stopwatch.StartNew();
        var result = CommandResult.NotConnected;
        while (true)
        {
            try
            {
                result = await report(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException)
            {
                log($"LE transport report threw: 0x{ex.HResult:X8}");
                result = new(CommandStatus.TransportError);
            }
            // Unsupported: the earbuds have the LE/BR switch after all; Rejected: they answered and said no.
            if (result.Status is CommandStatus.Success or CommandStatus.Unsupported or CommandStatus.Rejected) break;
            if (deadline.Elapsed >= (timeout ?? LeTransportReportTimeout)) break;
            await Task.Delay(interval ?? TimeSpan.FromSeconds(1), cancellationToken).ConfigureAwait(false);
        }
        log($"LE transport report before secondary: {result.Status}; waited {deadline.Elapsed.TotalSeconds:0.0}s");
        return result.IsSuccess;
    }

    private static async Task<bool> WaitForBothLinksAsync(ulong primary, ulong secondary, CancellationToken ct)
    {
        using var first = await OpenLeAsync(primary, ct).ConfigureAwait(false);
        using var second = await OpenBondedLeAsync(secondary, ct).ConfigureAwait(false);
        if (first is null || second is null) return false;
        GattSession? firstSession = null, secondSession = null;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        try
        {
            firstSession = await GattSession.FromDeviceIdAsync(first.BluetoothDeviceId).AsTask(timeout.Token).ConfigureAwait(false);
            secondSession = await GattSession.FromDeviceIdAsync(second.BluetoothDeviceId).AsTask(timeout.Token).ConfigureAwait(false);
            if (firstSession is not null) firstSession.MaintainConnection = true;
            if (secondSession is not null) secondSession.MaintainConnection = true;
            while (true)
            {
                if (first.ConnectionStatus == BluetoothConnectionStatus.Connected && second.ConnectionStatus == BluetoothConnectionStatus.Connected) return true;
                await Task.Delay(500, timeout.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return false; }
        catch (Exception ex) when (ex is COMException or UnauthorizedAccessException or ArgumentException)
        {
            Log($"LE link readiness unavailable: 0x{ex.HResult:X8}");
            return false;
        }
        finally { firstSession?.Dispose(); secondSession?.Dispose(); }
    }

    /// <summary>
    /// Removes this PC's pairings of the earbuds (both LE bonds and the Classic bond Windows derived
    /// from them, which carries no audio). The user then pairs them again in Windows Settings, which
    /// sets up Classic audio (A2DP/HFP) with its own connection handling.
    /// </summary>
    public static async Task<LeAudioPairingResult> RemoveLeAudioAsync(ulong primary, IProgress<LeAudioPairingStage>? progress, CancellationToken cancellationToken, Func<bool, CancellationToken, Task<CommandResult>>? setLeBrSwitch = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var pairingOperation = LeAudioLink.BeginPairingOperation(primary);
        // Re-enable the Classic render endpoint before deleting the pairings. If this fails the user
        // would be left with no Classic output after the restore flow, so stop instead of unpairing.
        LeAudioEndpointResult restore;
        try
        {
            restore = await LeAudioClassicAudioEndpoint.RestoreAsync(primary, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException)
        {
            Log($"Classic audio endpoint restore failed: 0x{ex.HResult:X8}");
            restore = new(LeAudioEndpointOutcome.Failed, ex.Message);
        }
        if (restore.Outcome == LeAudioEndpointOutcome.Failed)
            return new(LeAudioPairingOutcome.ClassicAudioEndpointRestoreFailed, restore.Detail);

        // Only leave LE-preferred mode after the Classic endpoint is usable again.
        using var pairingChange = LeAudioLink.BeginPairingChange(primary);
        if (setLeBrSwitch is not null)
        {
            try
            {
                await setLeBrSwitch(false, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException)
            {
                // Best effort; removing the pairings must still proceed.
            }

            await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken).ConfigureAwait(false);
        }

        progress?.Report(LeAudioPairingStage.RemovingPairings);
        ulong? secondaryAddress = await RemoveAllAsync(primary, cancellationToken).ConfigureAwait(false);
        progress?.Report(LeAudioPairingStage.WaitingForCleanup);
        await WaitForNodesRemovedAsync(primary, "BTHLE", cancellationToken, secondaryAddress).ConfigureAwait(false);
        LeAudioLink.ClearPreferred(primary);
        LeAudioOnlyDevices.Remove(primary);
        return new(LeAudioPairingOutcome.Removed);
    }

    /// <summary>
    /// Waits until Windows removed the device nodes of <paramref name="primary"/> whose instance id starts
    /// with <paramref name="enumerator"/> (BTHENUM = Classic, BTHLE = LE), then a little longer.
    /// Verified on the Enco X4: pairing 1–2 s after unpairing is too early, 30 s later is fine.
    /// </summary>
    private static async Task WaitForNodesRemovedAsync(ulong primary, string enumerator, CancellationToken cancellationToken, ulong? secondary = null)
    {
        string address = primary.ToString("X12", System.Globalization.CultureInfo.InvariantCulture);
        string query = $"System.Devices.DeviceInstanceId:~~\"{address}\"";
        if (secondary is { } other)
            query = $"({query}) OR (System.Devices.DeviceInstanceId:~~\"{other:X12}\")";
        var deadline = DateTime.UtcNow + NodeRemovalTimeout;
        bool removed = false;
        while (DateTime.UtcNow < deadline)
        {
            var nodes = await Windows.Devices.Enumeration.Pnp.PnpObject.FindAllAsync(
                Windows.Devices.Enumeration.Pnp.PnpObjectType.Device, ["System.Devices.DeviceInstanceId"],
                query).AsTask(cancellationToken).ConfigureAwait(false);
            bool left = nodes.Any(n => n.Properties.TryGetValue("System.Devices.DeviceInstanceId", out var id)
                && id is string text && text.StartsWith(enumerator, StringComparison.OrdinalIgnoreCase));
            if (!left) { removed = true; break; }
            await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken).ConfigureAwait(false);
        }
        if (!removed) throw new TimeoutException("Windows has not removed the old Bluetooth nodes; pairing was stopped to avoid duplicate audio endpoints. Retry after cleanup finishes.");
        await Task.Delay(NodeRemovalSettle, cancellationToken).ConfigureAwait(false);
    }

    private static readonly TimeSpan NodeRemovalTimeout = TimeSpan.FromSeconds(40);
    private static readonly TimeSpan NodeRemovalSettle = TimeSpan.FromSeconds(5);

    private static async Task<ulong?> RemoveAllAsync(ulong primary, CancellationToken cancellationToken)
    {
        using var le = await OpenLeAsync(primary, cancellationToken).ConfigureAwait(false);
        using var classic = await OpenClassicAsync(primary, cancellationToken).ConfigureAwait(false);
        var secondaries = await FindSecondaryBondsAsync(primary, PairName(primary, classic, le), cancellationToken).ConfigureAwait(false);
        try
        {
            foreach (var secondary in secondaries)
                await UnpairAsync(secondary.DeviceInformation.Pairing, cancellationToken).ConfigureAwait(false);
            if (le is not null) await UnpairAsync(le.DeviceInformation.Pairing, cancellationToken).ConfigureAwait(false);
            if (classic is not null) await UnpairAsync(classic.DeviceInformation.Pairing, cancellationToken).ConfigureAwait(false);
            return secondaries.Count == 1 ? secondaries[0].BluetoothAddress : null;
        }
        finally { foreach (var secondary in secondaries) secondary.Dispose(); }
    }

    /// <summary>
    /// Removes the Classic pairing Windows derived from the LE bond (CTKD) and remembers the earbuds as LE-only
    /// (<see cref="LeAudioOnlyDevices"/>). With that pairing Windows and the earbuds keep bringing a Classic link up
    /// next to LC3: A2DP takes the audio before the LE Audio endpoint appears, or the LE links drop (Enco X4).
    /// Only runs while the primary and another earbud hold LE bonds; removing the Classic entry keeps both
    /// (verified on the X4). Returns false when nothing was removed.
    /// </summary>
    public static async Task<bool> RemoveClassicPairingAsync(ulong primary, Action<string>? log, CancellationToken cancellationToken)
    {
        void Log(string message)
        {
            LeAudioPairing.Log(message);
            log?.Invoke(message);
        }
        using var classic = await OpenClassicAsync(primary, cancellationToken).ConfigureAwait(false);
        if (classic is null) return false;
        // Already LE-only and Windows has not derived the Classic pairing again: nothing to do.
        if (!classic.DeviceInformation.Pairing.IsPaired && LeAudioOnlyDevices.Find(primary) is not null) return false;
        if (!await LeAudioLink.IsBondedAsync(primary, cancellationToken).ConfigureAwait(false)) return false;
        string? name = PairName(primary, classic, null);
        var secondaries = await FindSecondaryBondsAsync(primary, name, cancellationToken).ConfigureAwait(false);
        try
        {
            if (secondaries.Count == 0)
            {
                Log("Classic pairing kept: the other earbud has no LE bond yet");
                return false;
            }
            name ??= secondaries[0].Name;
        }
        finally { foreach (var secondary in secondaries) secondary.Dispose(); }

        // Remember first: the device must stay known while its Classic entry disappears.
        LeAudioOnlyDevices.Record(new LeAudioOnlyDevice(primary, name, classic.DeviceInformation.Id));
        if (!classic.DeviceInformation.Pairing.IsPaired) return false;
        await UnpairAsync(classic.DeviceInformation.Pairing, cancellationToken).ConfigureAwait(false);
        bool leKept = await LeAudioLink.IsBondedAsync(primary, cancellationToken).ConfigureAwait(false);
        Log($"Classic (CTKD) pairing removed; LE bond kept={leKept}");
        return true;
    }

    /// <summary>
    /// The earbuds' name: the Classic pairing's, else the remembered one of LE-only earbuds (an unpaired Classic
    /// device reports an empty name), else the LE identity's.
    /// </summary>
    private static string? PairName(ulong primary, BluetoothDevice? classic, BluetoothLEDevice? le) =>
        !string.IsNullOrEmpty(classic?.Name) ? classic.Name
        : LeAudioOnlyDevices.Find(primary)?.Name ?? (string.IsNullOrEmpty(le?.Name) ? null : le.Name);

    private const string AepContainerId = "System.Devices.Aep.ContainerId";

    /// <summary>
    /// Bonded LE devices that are the other earbud. A Windows set pairing puts both LE identities
    /// (and the CTKD Classic node) into one device container, which identifies the peer exactly.
    /// Older per-address pairings keep separate containers; those fall back to the address learned
    /// from the pairing advert, then to the same LE name and the same first four address bytes.
    /// </summary>
    internal static async Task<List<BluetoothLEDevice>> FindSecondaryBondsAsync(ulong primary, string? name, CancellationToken cancellationToken)
    {
        var found = new List<BluetoothLEDevice>();
        var bonded = await DeviceInformation.FindAllAsync(BluetoothLEDevice.GetDeviceSelectorFromPairingState(true), [AepContainerId], DeviceInformationKind.AssociationEndpoint)
            .AsTask(cancellationToken).ConfigureAwait(false);
        var leBonds = bonded.Select(i => (i.Id, Container: ContainerOf(i))).ToList();
        var container = leBonds.FirstOrDefault(b => IsAddress(b.Id, primary)).Container
            ?? await FindClassicContainerAsync(primary, cancellationToken).ConfigureAwait(false);
        var containerPeers = SelectContainerPeers(primary, container, leBonds).ToHashSet(StringComparer.OrdinalIgnoreCase);
        // BluetoothLEDevice.Name can be "Bluetooth <address>" for BOTH buds, even though their
        // paired AEPs both correctly say "OPPO Enco X4". Never match the transient device names.
        string? bondName = ResolveBondName(primary, name, bonded.Select(i => (i.Id, i.Name)));
        bool hasExactPeer = KnownSecondaryAddresses.TryGetValue(primary, out ulong exactPeer);
        string? peerSuffix = hasExactPeer ? "-" + new BluetoothAddress(exactPeer).ToFullString() : null;
        if (containerPeers.Count == 0 && string.IsNullOrEmpty(bondName) && !hasExactPeer) return found;
        // Open only candidate bonds; some bonded entries are not openable as BluetoothLEDevice at all.
        try
        {
            foreach (var info in bonded.Where(i => containerPeers.Count > 0 ? containerPeers.Contains(i.Id)
                : hasExactPeer ? i.Id.EndsWith(peerSuffix!, StringComparison.OrdinalIgnoreCase) : i.Name == bondName))
            {
                BluetoothLEDevice? device;
                try
                {
                    device = await BluetoothLEDevice.FromIdAsync(info.Id).AsTask(cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is ArgumentException or COMException)
                {
                    continue;
                }
                if (device is null) continue;
                if (containerPeers.Count > 0 ? device.BluetoothAddress != primary
                    : hasExactPeer ? device.BluetoothAddress == exactPeer
                    : device.BluetoothAddress != primary && device.BluetoothAddress >> 16 == primary >> 16) found.Add(device);
                else device.Dispose();
            }
            if (found.Count > 1) throw new InvalidOperationException("Multiple same-model LE peers match; enter pairing mode to identify the other earbud explicitly.");
            return found;
        }
        catch
        {
            foreach (var device in found) device.Dispose();
            throw;
        }
    }

    /// <summary>Other LE bonds in the primary's device container (empty when the container is unknown).</summary>
    internal static IEnumerable<string> SelectContainerPeers(ulong primary, Guid? container, IEnumerable<(string Id, Guid? Container)> leBonds) =>
        container is not { } c || c == Guid.Empty ? []
            : leBonds.Where(b => b.Container == c && !IsAddress(b.Id, primary)).Select(b => b.Id);

    private static bool IsAddress(string aepId, ulong address) =>
        aepId.EndsWith("-" + new BluetoothAddress(address).ToFullString(), StringComparison.OrdinalIgnoreCase);

    private static Guid? ContainerOf(DeviceInformation info) =>
        info.Properties.TryGetValue(AepContainerId, out var value) && value is Guid id ? id : null;

    /// <summary>The container of the Classic (CTKD) bond, for when the primary's LE bond itself is missing.</summary>
    private static async Task<Guid?> FindClassicContainerAsync(ulong primary, CancellationToken cancellationToken)
    {
        var classic = await DeviceInformation.FindAllAsync(BluetoothDevice.GetDeviceSelectorFromPairingState(true), [AepContainerId], DeviceInformationKind.AssociationEndpoint)
            .AsTask(cancellationToken).ConfigureAwait(false);
        return classic.Where(i => IsAddress(i.Id, primary)).Select(ContainerOf).FirstOrDefault(c => c is not null);
    }

    internal static string? ResolveBondName(ulong primary, string? deviceName, IEnumerable<(string Id, string Name)> bonds)
    {
        string suffix = "-" + new BluetoothAddress(primary).ToFullString();
        foreach (var bond in bonds)
            if (bond.Id.EndsWith(suffix, StringComparison.OrdinalIgnoreCase) && !string.IsNullOrEmpty(bond.Name)) return bond.Name;
        return deviceName;
    }

    private sealed record PairingAdvert(ulong? Secondary);

    /// <summary>Waits for the primary's connectable advert (pairing mode) and reads the secondary address from its scan response.</summary>
    private static async Task<PairingAdvert?> WaitForPairingModeAsync(ulong primary, CancellationToken cancellationToken)
    {
        var connectable = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondaryFound = new TaskCompletionSource<ulong>(TaskCreationOptions.RunContinuationsAsynchronously);
        var watcher = new BluetoothLEAdvertisementWatcher { ScanningMode = BluetoothLEScanningMode.Active };
        void OnReceived(BluetoothLEAdvertisementWatcher sender, BluetoothLEAdvertisementReceivedEventArgs args)
        {
            if (args.BluetoothAddress != primary) return;
            if (args.IsConnectable) connectable.TrySetResult();
            foreach (var data in args.Advertisement.GetManufacturerDataByCompanyId(SecondaryAddressCompanyId))
            {
                var bytes = new byte[data.Data.Length];
                using var reader = DataReader.FromBuffer(data.Data);
                reader.ReadBytes(bytes);
                if (TryParseSecondaryAddress(primary, bytes, out ulong secondary)) secondaryFound.TrySetResult(secondary);
            }
        }
        watcher.Received += OnReceived;
        try
        {
            watcher.Start();
            try
            {
                await connectable.Task.WaitAsync(PairingModeTimeout, cancellationToken).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                return null;
            }
            try
            {
                return new PairingAdvert(await secondaryFound.Task.WaitAsync(ScanResponseWait, cancellationToken).ConfigureAwait(false));
            }
            catch (TimeoutException)
            {
                return new PairingAdvert(null);
            }
        }
        finally
        {
            watcher.Received -= OnReceived;
            if (watcher.Status == BluetoothLEAdvertisementWatcherStatus.Started) watcher.Stop();
        }
    }

    /// <summary>The first attempt right after the advert turns connectable may fail, so retry a few times.</summary>
    private static async Task<DevicePairingResultStatus> PairLeAsync(ulong address, CancellationToken cancellationToken, LeAudioSetPairing? setPairing = null, Action<string>? diagnosticLog = null)
    {
        void Log(string message)
        {
            if (diagnosticLog is null) LeAudioPairing.Log(message);
            else diagnosticLog(message);
        }
        var status = DevicePairingResultStatus.Failed;
        for (int attempt = 0; attempt < PairAttempts; attempt++)
        {
            if (attempt > 0) await Task.Delay(TimeSpan.FromSeconds(1.5), cancellationToken).ConfigureAwait(false);
            using var device = await OpenLeAsync(address, cancellationToken).ConfigureAwait(false);
            if (device is null)
            {
                Log($"pair {new BluetoothAddress(address)} attempt {attempt}: device null");
                continue;
            }
            Log($"pair {new BluetoothAddress(address)} attempt {attempt}: conn={device.ConnectionStatus} canPair={device.DeviceInformation.Pairing.CanPair} paired={device.DeviceInformation.Pairing.IsPaired}");
            if (device.DeviceInformation.Pairing.IsPaired) return DevicePairingResultStatus.AlreadyPaired;
            using var attemptTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            // Set-member pairing now waits for LE control preparation inside the primary attempt.
            var attemptLimit = TimeSpan.FromSeconds(setPairing?.RequiresPreparation == true ? 120 : 60);
            attemptTimeout.CancelAfter(attemptLimit);
            try
            {
                status = setPairing is null
                    ? await PairAsync(device.DeviceInformation.Pairing, DevicePairingProtectionLevel.Encryption, attemptTimeout.Token).ConfigureAwait(false)
                    : await setPairing.PairAsync(device.DeviceInformation.Pairing, DevicePairingProtectionLevel.Encryption, attemptTimeout.Token).ConfigureAwait(false);
                if (status == DevicePairingResultStatus.Failed)
                {
                    Log($"pair {new BluetoothAddress(address)} attempt {attempt}: encryption failed, trying Just Works");
                    // A pairing object takes one PairAsync; a second call throws E_ILLEGAL_METHOD_CALL (Enco X3).
                    using var retry = await OpenLeAsync(address, cancellationToken).ConfigureAwait(false);
                    if (retry is null) Log($"pair {new BluetoothAddress(address)} attempt {attempt}: device null for Just Works");
                    else status = setPairing is null
                        ? await PairAsync(retry.DeviceInformation.Pairing, DevicePairingProtectionLevel.None, attemptTimeout.Token).ConfigureAwait(false)
                        : await setPairing.PairAsync(retry.DeviceInformation.Pairing, DevicePairingProtectionLevel.None, attemptTimeout.Token).ConfigureAwait(false);
                }
                if (status == DevicePairingResultStatus.OperationAlreadyInProgress)
                    status = await WaitForExistingPairingAsync(address, attemptTimeout.Token, Log).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                // Do not start another operation while Windows may still be finishing native pairing.
                Log("LE pairing attempt timed out; stopping without starting an overlapping retry");
                throw new TimeoutException($"Windows LE pairing did not finish within {attemptLimit.TotalSeconds:0} seconds. No overlapping retry was started; check the current bonds before trying again.");
            }
            catch (Exception ex) when (ex.HResult == IllegalMethodCall)
            {
                // Windows refused to start the call, so no native pairing is running; the next attempt opens a new object.
                Log($"pair {new BluetoothAddress(address)} attempt {attempt}: Windows refused the call (0x{ex.HResult:X8})");
                status = DevicePairingResultStatus.Failed;
                continue;
            }
            Log($"pair {new BluetoothAddress(address)} attempt {attempt}: {status}");
            if (status is DevicePairingResultStatus.Paired or DevicePairingResultStatus.AlreadyPaired) return status;
        }
        return status;
    }

    internal static async Task<DevicePairingResultStatus> WaitForExistingPairingAsync(ulong address, CancellationToken ct, Action<string> log)
    {
        log("LE pairing already in progress; waiting for its bond without submitting another PairAsync");
        for (int i = 0; i < 30; i++)
        {
            if (await LeAudioLink.IsBondedAsync(address, ct).ConfigureAwait(false))
                return DevicePairingResultStatus.AlreadyPaired;
            await Task.Delay(TimeSpan.FromSeconds(1), ct).ConfigureAwait(false);
        }
        throw new TimeoutException("An existing Windows pairing operation is still unresolved; no overlapping retry or fallback was started.");
    }

    /// <summary>Earbuds have no display or keypad: accept Just Works / numeric comparison without a Windows prompt.</summary>
    private static async Task<DevicePairingResultStatus> PairAsync(DeviceInformationPairing pairing, DevicePairingProtectionLevel level, CancellationToken cancellationToken)
    {
        void OnRequested(DeviceInformationCustomPairing sender, DevicePairingRequestedEventArgs args) => args.Accept();
        pairing.Custom.PairingRequested += OnRequested;
        try
        {
            var result = await pairing.Custom.PairAsync(DevicePairingKinds.ConfirmOnly | DevicePairingKinds.ConfirmPinMatch | DevicePairingKinds.DisplayPin, level)
                .AsTask(cancellationToken).ConfigureAwait(false);
            return result.Status;
        }
        finally
        {
            pairing.Custom.PairingRequested -= OnRequested;
        }
    }

    private static async Task UnpairAsync(DeviceInformationPairing pairing, CancellationToken cancellationToken)
    {
        if (!pairing.IsPaired) return;
        var result = await pairing.UnpairAsync().AsTask(cancellationToken).ConfigureAwait(false);
        if (result.Status is not (DeviceUnpairingResultStatus.Unpaired or DeviceUnpairingResultStatus.AlreadyUnpaired))
            throw new InvalidOperationException($"Windows could not remove the earbuds' pairing: {result.Status}");
    }

    /// <summary>
    /// Whether Windows holds an LE bond for this address of either type. The other earbud may bond under a
    /// random identity address (Enco X3), which <see cref="LeAudioLink.IsBondedAsync"/> (public only) misses.
    /// </summary>
    private static async Task<bool> IsLeBondedAsync(ulong address, CancellationToken cancellationToken) =>
        await FindLeBondIdAsync(address, cancellationToken).ConfigureAwait(false) is not null;

    private static async Task<string?> FindLeBondIdAsync(ulong address, CancellationToken cancellationToken)
    {
        var bonded = await DeviceInformation.FindAllAsync(BluetoothLEDevice.GetDeviceSelectorFromPairingState(true), [], DeviceInformationKind.AssociationEndpoint)
            .AsTask(cancellationToken).ConfigureAwait(false);
        return bonded.FirstOrDefault(i => IsAddress(i.Id, address))?.Id;
    }

    /// <summary>Opens a bonded LE identity by its paired endpoint (any address type), else by public address.</summary>
    private static async Task<BluetoothLEDevice?> OpenBondedLeAsync(ulong address, CancellationToken cancellationToken)
    {
        try
        {
            if (await FindLeBondIdAsync(address, cancellationToken).ConfigureAwait(false) is { } id)
                return await BluetoothLEDevice.FromIdAsync(id).AsTask(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is ArgumentException or COMException)
        {
        }
        return await OpenLeAsync(address, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<BluetoothLEDevice?> OpenLeAsync(ulong address, CancellationToken cancellationToken)
    {
        try
        {
            return await BluetoothLEDevice.FromBluetoothAddressAsync(address, BluetoothAddressType.Public).AsTask(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is ArgumentException or COMException)
        {
            return null;
        }
    }

    private static async Task<BluetoothDevice?> OpenClassicAsync(ulong address, CancellationToken cancellationToken)
    {
        try
        {
            return await BluetoothDevice.FromBluetoothAddressAsync(address).AsTask(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is ArgumentException or COMException)
        {
            return null;
        }
    }
}
