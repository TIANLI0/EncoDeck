// SPDX-License-Identifier: GPL-3.0-only
// Ported from OppoPodsManager (https://github.com/Zhaoyi-ya/OppoPodsManager), GPL-3.0-or-later.
// Mutual auth sequence, device-info / status records and push acknowledgements ported from BudsLink
// (https://github.com/maniacx/BudsLink, GPL-3.0) and redmi-buds-bar by robin-liquidium
// (https://github.com/robin-liquidium/redmi-buds-bar, GPL-3.0); the exchange matches OpenXia's and
// android_packages_apps_XiaomiTWS's descriptions (MIT / Apache-2.0, see THIRD_PARTY_NOTICES.md).
using EncoDeck.Core.Commands;
using EncoDeck.Core.State;
using EncoDeck.Protocol.Transport;
using Microsoft.Extensions.Logging;

namespace EncoDeck.Protocol.Brands.Xiaomi;

/// <summary>
/// Controller for Xiaomi / Redmi earbuds over the Xiaomi SPP channel: mutual auth (when the
/// earbuds answer it), device info and battery, then the device-config settings, each read on
/// its own and enabled only after a valid readback.
/// </summary>
internal sealed partial class XiaomiDeviceController : BrandControllerBase
{
    /// <summary>The auth probe is short: models that do not require auth may never answer it.</summary>
    private static readonly TimeSpan AuthTimeout = TimeSpan.FromMilliseconds(1200);

    /// <summary>How long to wait for the earbuds' own challenge and verdict after ours.</summary>
    private static readonly TimeSpan AuthVerdictTimeout = TimeSpan.FromMilliseconds(1500);

    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan WriteTimeout = TimeSpan.FromSeconds(5);

    private readonly XiaomiFrameDecoder _decoder = new();
    private readonly Lock _gate = new();
    private readonly List<Waiter> _waiters = [];
    private XiaomiDeviceProfile _profile;
    private byte _sequence = XiaomiProtocol.ConfigSequenceStart;
    private TaskCompletionSource<bool>? _deviceVerdict;

    public XiaomiDeviceController(IDuplexByteChannel channel, ControllerContext context, XiaomiDeviceProfile profile)
        : base(channel, context)
    {
        _profile = profile.ForDevice(context.Identity.Name);
    }

    /// <summary>True when the earbuds proved the shared secret and did not reject ours.</summary>
    public bool IsAuthenticated { get; private set; }

    public XiaomiDeviceProfile Profile => _profile;

    private sealed record Waiter(Func<XiaomiFrame, bool> Accepts, TaskCompletionSource<XiaomiFrame> Completion);

    protected override async Task<bool> StartSessionAsync(CancellationToken cancellationToken)
    {
        Logger.LogInformation("Xiaomi handshake: authentication starting");
        await AuthenticateAsync(cancellationToken).ConfigureAwait(false);
        Logger.LogInformation("Xiaomi handshake: reading device info");
        if (!await ReadTargetInfoAsync(cancellationToken).ConfigureAwait(false))
        {
            Logger.LogInformation("Xiaomi earbuds did not answer the device-info query");
            return false;
        }
        Logger.LogInformation("Xiaomi handshake: discovering settings");
        await ReadFeaturesAsync(discover: true, cancellationToken).ConfigureAwait(false);
        Logger.LogInformation("Xiaomi handshake completed");
        return true;
    }

    public override Task<CommandResult> RefreshAsync(CancellationToken cancellationToken) => RunExclusiveAsync(_operations, RefreshCoreAsync, cancellationToken);

    private async Task<CommandResult> RefreshCoreAsync(CancellationToken cancellationToken)
    {
        bool info = await ReadTargetInfoAsync(cancellationToken).ConfigureAwait(false);
        await ReadFeaturesAsync(discover: false, cancellationToken).ConfigureAwait(false);
        return info ? CommandResult.Ok : new CommandResult(CommandStatus.Timeout, "The earbuds did not answer the device-info query.");
    }

    protected override void OnReceived(ReadOnlySpan<byte> data)
    {
        foreach (var raw in _decoder.Feed(data))
        {
            var frame = XiaomiConfigCodec.NormalizeResponse(raw);
            if (frame is null) continue;
            Logger.LogTrace("Xiaomi frame received: {Frame}", frame);
            Touch();
            if (!frame.IsResponse)
            {
                HandleEarbudRequest(frame);
                continue;
            }
            if (frame.Status == 0) ApplyResponse(frame);
            Waiter? match;
            lock (_gate)
            {
                match = _waiters.FirstOrDefault(w => w.Accepts(frame));
                if (match is not null) _waiters.Remove(match);
            }
            match?.Completion.TrySetResult(frame);
        }
    }

    /// <summary>Requests the earbuds send: their auth round, setting pushes and status pushes.</summary>
    private void HandleEarbudRequest(XiaomiFrame frame)
    {
        switch (frame.OpCode)
        {
            case XiaomiProtocol.OpAuthCheck when frame.Payload.Length >= 1 + XiaomiAuth.AuthRandomLength && frame.Payload[0] == XiaomiAuth.AuthVersion:
                var answer = XiaomiAuth.BuildAuthCheckPayload(XiaomiAuth.GetEncryptedAuthCheckData(frame.Payload.AsSpan(1, XiaomiAuth.AuthRandomLength)));
                Reply(frame, answer);
                break;
            case XiaomiProtocol.OpAuthResult:
                Reply(frame, [XiaomiAuth.AuthVersion]);
                bool accepted = frame.Payload is [XiaomiAuth.AuthVersion, 0, ..];
                TaskCompletionSource<bool>? verdict;
                lock (_gate) verdict = _deviceVerdict;
                verdict?.TrySetResult(accepted);
                break;
            case XiaomiProtocol.OpNotifyConfig:
                ApplyFeatures(frame);
                if (frame.NeedsReply) Reply(frame, []);
                break;
            case XiaomiProtocol.OpReportStatus:
                ApplyStatusReport(frame.Payload);
                if (frame.NeedsReply) Reply(frame, []);
                break;
        }
    }

    private void Reply(XiaomiFrame request, byte[] data) =>
        _ = WriteAsync(XiaomiFrame.Response(XiaomiProtocol.KindResponse, request.OpCode, request.Sequence, data).Encode(), CancellationToken.None);

    private void ApplyResponse(XiaomiFrame frame)
    {
        switch (frame.OpCode)
        {
            case XiaomiProtocol.OpTargetInfo when XiaomiProtocol.TryParseTargetInfo(frame.Payload, out var info):
                ApplyTargetInfo(info);
                break;
            case XiaomiProtocol.OpGetConfig:
                ApplyFeatures(frame);
                break;
            case XiaomiProtocol.OpRunInfo:
                ApplyRunInfo(frame.Payload);
                break;
        }
    }

    private void ApplyTargetInfo(XiaomiTargetInfo info)
    {
        if (info.VendorId is int vid && info.ProductId is int pid)
        {
            lock (_gate) _profile = _profile.WithProductId(vid, pid);
        }
        var profile = _profile;
        var now = Now;
        Store.TryUpdate(DeviceId, s =>
        {
            var next = s with { Profile = profile, LastUpdateSource = StateSource.QueryResponse };
            if (info.Battery is { } b) next = next with { Battery = MergeBattery(s.Battery, b[0], b[1], b[2], now, StateSource.QueryResponse) };
            if (info.Firmware is not null || info.VendorId is not null)
            {
                var extra = new Dictionary<string, string>();
                if (info.Firmware is { } fw) extra["version"] = fw;
                if (info.VendorId is int v && info.ProductId is int p) extra["vid-pid"] = $"{v:X4}:{p:X4}";
                next = next with { Firmware = Observed.From(new FirmwareInfo(info.Firmware, profile.ModelName, extra), now, StateSource.QueryResponse) };
            }
            return next;
        });
    }

    /// <summary>Status push: record 0 is the battery <c>L R C</c>. The noise mode record carries no strength; the config push that follows does.</summary>
    private void ApplyStatusReport(byte[] payload)
    {
        foreach (var (type, value) in XiaomiProtocol.ParseInfoRecords(payload))
        {
            if (type != XiaomiProtocol.StatusBattery || value.Length != 3) continue;
            var now = Now;
            Store.TryUpdate(DeviceId, s => s with
            {
                Battery = MergeBattery(s.Battery, XiaomiProtocol.DecodeBattery(value[0]), XiaomiProtocol.DecodeBattery(value[1]), XiaomiProtocol.DecodeBattery(value[2]), now, StateSource.DeviceNotification),
                LastUpdateSource = StateSource.DeviceNotification,
            });
        }
    }

    /// <summary>A component the earbuds do not report (0xFF) keeps its last timestamped reading.</summary>
    private static BatteryStatus MergeBattery(BatteryStatus current, BatteryReading? left, BatteryReading? right, BatteryReading? @case, DateTimeOffset now, StateSource source) => new(
        left is null ? current.Left : Observed.From(left, now, source),
        right is null ? current.Right : Observed.From(right, now, source),
        @case is null ? current.Case : Observed.From(@case, now, source));

    private async Task AuthenticateAsync(CancellationToken cancellationToken)
    {
        var verdict = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_gate) _deviceVerdict = verdict;
        var random = XiaomiAuth.CreateRandomFactor();
        var challenge = new XiaomiFrame(XiaomiProtocol.KindRequest, XiaomiProtocol.OpAuthCheck, NextSequence(), XiaomiAuth.BuildAuthCheckPayload(random));
        var answer = await RequestAsync(challenge, f => f.OpCode == XiaomiProtocol.OpAuthCheck, AuthTimeout, cancellationToken).ConfigureAwait(false);
        if (answer is null)
        {
            Logger.LogInformation("Xiaomi auth probe got no answer; continuing without auth");
            return;
        }
        if (answer.Status != 0 || !XiaomiAuth.TryParseAuthCheckResponse(answer.Payload, out _, out var result) || !XiaomiAuth.VerifyAuthResponse(random, result))
        {
            Logger.LogWarning("Xiaomi auth answer did not verify; later commands may be refused");
            return;
        }
        IsAuthenticated = true;
        var confirm = new XiaomiFrame(XiaomiProtocol.KindRequest, XiaomiProtocol.OpAuthResult, NextSequence(), [XiaomiAuth.AuthVersion, 0]);
        await RequestAsync(confirm, f => f.OpCode == XiaomiProtocol.OpAuthResult, AuthTimeout, cancellationToken).ConfigureAwait(false);
        try
        {
            // The earbuds now challenge us (answered in HandleEarbudRequest) and send their verdict.
            if (!await verdict.Task.WaitAsync(AuthVerdictTimeout, Context.Time, cancellationToken).ConfigureAwait(false))
            {
                IsAuthenticated = false;
                Logger.LogWarning("Xiaomi earbuds rejected our auth answer");
            }
        }
        catch (TimeoutException)
        {
            Logger.LogInformation("Xiaomi earbuds sent no auth verdict; continuing");
        }
    }

    private async Task<bool> ReadTargetInfoAsync(CancellationToken cancellationToken)
    {
        var query = new XiaomiFrame(XiaomiProtocol.KindRequest, XiaomiProtocol.OpTargetInfo, NextSequence(), XiaomiProtocol.TargetInfoQueryPayload.ToArray());
        var answer = await RequestAsync(query, f => f.OpCode == XiaomiProtocol.OpTargetInfo && f.Status == 0
            && XiaomiProtocol.TryParseTargetInfo(f.Payload, out _), RequestTimeout, cancellationToken).ConfigureAwait(false);
        return answer is not null;
    }

    /// <summary>Sends a frame and waits for a matching response. An unanswered request returns null;
    /// a stalled write ends the session because its frame may be incomplete.</summary>
    private async Task<XiaomiFrame?> RequestAsync(XiaomiFrame frame, Func<XiaomiFrame, bool> accepts, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var waiter = new Waiter(f => f.IsResponse && f.Sequence == frame.Sequence && accepts(f), new TaskCompletionSource<XiaomiFrame>(TaskCreationOptions.RunContinuationsAsynchronously));
        lock (_gate) _waiters.Add(waiter);
        try
        {
            using (var writeTimeout = new CancellationTokenSource(WriteTimeout, Context.Time))
            using (var writeCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, writeTimeout.Token))
            {
                try
                {
                    if (!await WriteAsync(frame.Encode(), writeCancellation.Token).ConfigureAwait(false)) return null;
                }
                catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested && writeTimeout.IsCancellationRequested)
                {
                    // A cancelled write may have sent only part of a frame. Reopen the transport
                    // instead of treating this as an unanswered optional auth/config request.
                    EndSession();
                    Logger.LogWarning("Xiaomi write timed out: opcode {Opcode:X2}, sequence {Sequence}", frame.OpCode, frame.Sequence);
                    throw new IOException("Xiaomi control write timed out; the session must be reopened.", ex);
                }
            }
            return await waiter.Completion.Task.WaitAsync(timeout, Context.Time, cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            return null;
        }
        finally
        {
            lock (_gate) _waiters.Remove(waiter);
        }
    }

    private byte NextSequence()
    {
        lock (_gate)
        {
            byte value = _sequence;
            _sequence = _sequence == byte.MaxValue ? XiaomiProtocol.ConfigSequenceStart : (byte)(_sequence + 1);
            return value;
        }
    }
}
