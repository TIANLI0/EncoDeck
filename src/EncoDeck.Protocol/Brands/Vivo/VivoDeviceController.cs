// SPDX-License-Identifier: GPL-3.0-only
// Ported from OppoPodsManager (https://github.com/Zhaoyi-ya/OppoPodsManager), GPL-3.0-or-later.
// Gestures, wear-detection switch and dual connection management ported from TWS-Pods-PC
// (https://github.com/Zhaoyi-ya/TWS-Pods-PC, 3350f42) and vivopods-mac
// (https://github.com/FxRayHughes/vivopods-mac, be25133), both GPL-3.0-only.
using EncoDeck.Core.Capabilities;
using EncoDeck.Core.Commands;
using EncoDeck.Core.State;
using EncoDeck.Protocol.Transport;
using Microsoft.Extensions.Logging;

namespace EncoDeck.Protocol.Brands.Vivo;

/// <summary>
/// Drives vivo / iQOO earbuds over the vivo GAIA RFCOMM channel: handshake, notification
/// registration, battery / wear / noise / sound effect / game mode / firmware reads, and the
/// writes the model declares. Writes are only reported successful once a device report shows the
/// new value (a SET echo alone is not trusted); otherwise the value is re-queried. Gestures, the
/// wear-detection switch and the dual connection list are offered only after the earbuds report them.
/// </summary>
public sealed class VivoDeviceController : BrandControllerBase
{
    private static readonly TimeSpan HandshakeTimeout = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan QueryTimeout = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan ConfirmTimeout = TimeSpan.FromSeconds(2);

    /// <summary>Connecting a source device takes a Bluetooth page; give the pushed list time to change.</summary>
    private static readonly TimeSpan SourceConnectTimeout = TimeSpan.FromSeconds(8);

    private readonly VivoDeviceProfile _profile;
    private readonly VivoFrameDecoder _decoder = new();

    /// <summary>GATT messages carry no <c>FF</c> header: one bare <c>vendor cmd payload</c> per notification.</summary>
    private readonly bool _gatt;
    private readonly Lock _waitersGate = new();
    private readonly List<Waiter> _waiters = [];
    private readonly Lock _stateGate = new();
    private readonly byte[]? _hostAddress;
    private VivoConfirmed _confirmed;
    private (byte Left, byte Right)? _doubleTap;
    private byte? _longPress;
    private (byte Left, byte Right)? _noiseCycle;
    private IReadOnlyList<VivoSourceDevice>? _sources;

    public VivoDeviceController(IDuplexByteChannel channel, ControllerContext context, VivoDeviceProfile profile)
        : base(channel, context)
    {
        _profile = profile;
        _gatt = channel.Description.Contains("GATT", StringComparison.OrdinalIgnoreCase);
        _hostAddress = VivoParsers.TryParseHostAddress(context.Identity.DeviceId, out var host) ? host : null;
    }

    private VivoModel Model => _profile.Model;

    /// <summary>The profile with what this session has confirmed (the one published to the store).</summary>
    private VivoDeviceProfile CurrentProfile
    {
        get
        {
            VivoConfirmed confirmed;
            lock (_stateGate) confirmed = _confirmed;
            return _profile.WithConfirmed(confirmed, Context.AllowExperimental());
        }
    }

    /// <summary>Gesture and wear-switch reports only mean that on earbuds from the catalog.</summary>
    private bool HasEarbudSettings => !Model.Headphones && !_profile.IsGeneric;

    private sealed record Waiter(Func<VivoFrame, bool> Match, TaskCompletionSource<VivoFrame> Completion);

    protected override async Task<bool> StartSessionAsync(CancellationToken cancellationToken)
    {
        var hello = await RequestAsync(VivoCommands.Handshake, [], f => f.Command == VivoCommands.HandshakeResponse, HandshakeTimeout, cancellationToken).ConfigureAwait(false);
        if (hello is null)
        {
            Logger.LogInformation("vivo handshake not answered");
            return false;
        }

        // Fire-and-forget registration; the earbuds then push reports on their own.
        foreach (ushort command in VivoCommands.RegisterNotifications)
        {
            if (!await SendAsync(command, [], cancellationToken).ConfigureAwait(false)) return false;
        }

        await QueryAllAsync(cancellationToken).ConfigureAwait(false);
        return true;
    }

    public override async Task<CommandResult> RefreshAsync(CancellationToken cancellationToken)
    {
        if (Closed.IsCompleted) return CommandResult.NotConnected;
        bool any = await QueryAllAsync(cancellationToken).ConfigureAwait(false);
        return any ? CommandResult.Ok : new CommandResult(CommandStatus.Timeout, "The earbuds did not answer.");
    }

    private async Task<bool> QueryAllAsync(CancellationToken ct)
    {
        bool any = await QueryAsync(VivoCommands.QueryBattery, [], VivoCommands.ReportBattery, ct).ConfigureAwait(false) is not null;
        if (!Model.Headphones)
        {
            any |= await QueryAsync(VivoCommands.QueryWearState, [], VivoCommands.ReportWearState, ct).ConfigureAwait(false) is not null;
        }
        if (Supported(DeviceCapability.NoiseCancellation))
        {
            any |= await QueryAsync(VivoCommands.QueryNoiseMode, Model.NoiseQueryPayload, VivoCommands.ReportNoiseMode, ct).ConfigureAwait(false) is not null;
        }
        if (Supported(DeviceCapability.Equalizer))
        {
            any |= await QueryAsync(VivoCommands.QueryAudioEffect, [], VivoCommands.ReportAudioEffect, ct).ConfigureAwait(false) is not null;
        }
        if (Supported(DeviceCapability.GamingMode))
        {
            any |= await QueryAsync(VivoCommands.QueryLowLatency, [], VivoCommands.ReportLowLatency, ct).ConfigureAwait(false) is not null;
        }
        if (Store.Current.Firmware is null)
        {
            any |= await QueryAsync(VivoCommands.QueryFirmware, [], VivoCommands.ReportFirmware, ct).ConfigureAwait(false) is not null;
        }
        any |= await QuerySettingsAsync(ct).ConfigureAwait(false);
        return any;
    }

    /// <summary>
    /// Gesture and dual connection reads. The double-tap and wear-switch values arrive as answers to
    /// the registration frames; the long-press reads follow only on earbuds that reported double-tap,
    /// so models without these settings see no extra queries.
    /// </summary>
    private async Task<bool> QuerySettingsAsync(CancellationToken ct)
    {
        bool any = false;
        VivoConfirmed confirmed;
        lock (_stateGate) confirmed = _confirmed;
        if (HasEarbudSettings && confirmed.HasFlag(VivoConfirmed.DoubleTap))
        {
            any |= await QueryAsync(VivoCommands.QueryLongPress, [], VivoCommands.ReportLongPress, ct).ConfigureAwait(false) is not null;
            byte? longPress;
            lock (_stateGate) longPress = _longPress;
            if (longPress is not null)
            {
                any |= await QueryAsync(VivoCommands.QueryNoiseCycle, [VivoConstants.NoiseCycleSubCommand], VivoCommands.ReportNoiseCycle, ct).ConfigureAwait(false) is not null;
            }
        }
        if (_profile.MayQuerySourceDevices(Context.AllowExperimental()))
        {
            any |= await QueryAsync(VivoCommands.QuerySourceDevices, [], VivoCommands.ReportSourceDevices, ct).ConfigureAwait(false) is not null;
        }
        return any;
    }

    public override async Task<CommandResult> SetNoiseControlAsync(NoiseControlState target, CancellationToken cancellationToken)
    {
        if (!Supported(DeviceCapability.NoiseCancellation) || !_profile.NoiseControlOptions.Any(o => o.Matches(target)))
        {
            return NotSupported("This noise control option");
        }
        byte mode = VivoParsers.NoiseModeByte(target.Mode);
        byte[] payload = [mode, .. Model.NoiseSetSuffix];
        return await RunCommandAsync(PendingKeys.NoiseControl, null, async ct =>
        {
            var result = await WriteConfirmedAsync(
                VivoCommands.SetNoiseMode,
                payload,
                f => f.Command == VivoCommands.ReportNoiseMode && VivoParsers.TryParseNoise(f.Payload, out var m) && m == target.Mode,
                VivoCommands.QueryNoiseMode,
                Model.NoiseQueryPayload,
                VivoCommands.ReportNoiseMode,
                ct).ConfigureAwait(false);
            if (result.IsSuccess)
            {
                Store.TryUpdate(DeviceId, s => s with { NoiseControl = Observed.From(new NoiseControlState(target.Mode), Now, StateSource.CommandConfirmation), LastUpdateSource = StateSource.CommandConfirmation });
            }
            return result;
        }, cancellationToken).ConfigureAwait(false);
    }

    public override async Task<CommandResult> SetEqualizerPresetAsync(string presetId, CancellationToken cancellationToken)
    {
        if (!Supported(DeviceCapability.Equalizer)
            || !VivoCatalog.TryParsePresetId(presetId, out byte effect)
            || !Model.AudioEffectIds.Contains(effect))
        {
            return NotSupported("This equalizer preset");
        }
        return await RunCommandAsync(PendingKeys.Equalizer, null, async ct =>
        {
            var result = await WriteConfirmedAsync(
                VivoCommands.SetAudioEffect,
                [effect],
                f => f.Command is VivoCommands.AckAudioEffect or VivoCommands.ReportAudioEffect
                    && VivoParsers.TryParseValue(f.Payload, out byte v) && VivoCatalog.NormalizeReportedEffect(v) == effect,
                VivoCommands.QueryAudioEffect,
                [],
                VivoCommands.ReportAudioEffect,
                ct).ConfigureAwait(false);
            if (result.IsSuccess)
            {
                Store.TryUpdate(DeviceId, s => s with { Equalizer = Observed.From(new EqualizerState(presetId, effect), Now, StateSource.CommandConfirmation), LastUpdateSource = StateSource.CommandConfirmation });
            }
            return result;
        }, cancellationToken).ConfigureAwait(false);
    }

    public override async Task<CommandResult> SetToggleAsync(DeviceToggle toggle, bool enabled, CancellationToken cancellationToken)
    {
        if (toggle == DeviceToggle.InEarDetection) return await SetWearDetectionAsync(enabled, cancellationToken).ConfigureAwait(false);
        if (toggle != DeviceToggle.GamingMode || !Supported(DeviceCapability.GamingMode)) return NotSupported(toggle.ToString());
        byte value = enabled ? (byte)1 : (byte)0;
        return await RunCommandAsync(PendingKeys.Toggle(toggle), null, async ct =>
        {
            var result = await WriteConfirmedAsync(
                VivoCommands.SetLowLatency,
                [value],
                f => f.Command is VivoCommands.AckLowLatency or VivoCommands.ReportLowLatency
                    && VivoParsers.TryParseValue(f.Payload, out byte v) && v == value,
                VivoCommands.QueryLowLatency,
                [],
                VivoCommands.ReportLowLatency,
                ct).ConfigureAwait(false);
            if (result.IsSuccess)
            {
                Store.TryUpdate(DeviceId, s => s with { Toggles = s.Toggles.SetItem(toggle, Observed.From(enabled, Now, StateSource.CommandConfirmation)), LastUpdateSource = StateSource.CommandConfirmation });
            }
            return result;
        }, cancellationToken).ConfigureAwait(false);
    }

    public override async Task<CommandResult> FindEarbudsAsync(bool ring, CancellationToken cancellationToken = default)
    {
        if (!Supported(DeviceCapability.FindMyEarbuds)) return NotSupported("Find earbuds");
        byte value = ring ? (byte)1 : (byte)0;
        var ack = await RequestAsync(VivoCommands.SetFindDevice, [value], f => f.Command == VivoCommands.AckFindDevice, ConfirmTimeout, cancellationToken).ConfigureAwait(false);
        CommandResult result;
        if (ack is null)
        {
            result = Closed.IsCompleted ? new CommandResult(CommandStatus.TransportError) : new CommandResult(CommandStatus.Timeout, "The earbuds did not acknowledge.");
        }
        else if (!VivoParsers.TryParseValue(ack.Payload, out byte state) || (state != 0) != ring)
        {
            result = new CommandResult(CommandStatus.Rejected, "The earbuds refused (are they in the case?).");
        }
        else
        {
            result = CommandResult.Ok;
        }
        bool active = ring && result.IsSuccess;
        Store.TryUpdate(DeviceId, s => s with { IsFindingEarbuds = active });
        return result;
    }

    /// <summary>Writes the wear-detection switch; confirmed by the <c>0x8203</c> report, not by the acknowledgement.</summary>
    private async Task<CommandResult> SetWearDetectionAsync(bool enabled, CancellationToken ct)
    {
        if (!CurrentProfile.OffersWearDetectionSwitch) return NotSupported(nameof(DeviceToggle.InEarDetection));
        byte value = enabled ? (byte)1 : (byte)0;
        return await RunCommandAsync(PendingKeys.Toggle(DeviceToggle.InEarDetection), null, async c =>
        {
            var result = await WriteConfirmedAsync(
                VivoCommands.SetWearDetection,
                [value],
                f => f.Command == VivoCommands.ReportWearDetection && VivoParsers.TryParseWearDetection(f.Payload, out bool on) && on == enabled,
                VivoCommands.QueryWearDetection,
                [],
                VivoCommands.ReportWearDetection,
                c).ConfigureAwait(false);
            if (result.IsSuccess)
            {
                Store.TryUpdate(DeviceId, s => s with { Toggles = s.Toggles.SetItem(DeviceToggle.InEarDetection, Observed.From(enabled, Now, StateSource.CommandConfirmation)), LastUpdateSource = StateSource.CommandConfirmation });
            }
            return result;
        }, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Double-tap (per earbud), long-press function (both earbuds) and long-press noise cycle (per
    /// earbud). Only values from the known code lists are written, and only for a slot the earbuds
    /// reported; the cycle write repeats the other earbud's reported byte unchanged.
    /// </summary>
    public override async Task<CommandResult> SetKeyFunctionAsync(KeyFunction assignment, CancellationToken cancellationToken = default)
    {
        if (!HasEarbudSettings || !Supported(DeviceCapability.TouchCustomization)) return NotSupported("Gestures");
        (byte Left, byte Right)? doubleTap, cycle;
        byte? longPress;
        lock (_stateGate)
        {
            doubleTap = _doubleTap;
            longPress = _longPress;
            cycle = _noiseCycle;
        }
        bool perEar = assignment.Side is EarSide.Left or EarSide.Right;

        if (assignment is { Button: 0, Action: GestureAction.DoubleSqueeze } && perEar
            && VivoGestures.DoubleTapCodes.Contains(assignment.Function) && doubleTap is not null)
        {
            bool right = assignment.Side == EarSide.Right;
            byte code = (byte)(assignment.Function + (right ? VivoConstants.RightDoubleTapBase : 0));
            return await RunCommandAsync(PendingKeys.KeyFunction, null, c => WriteConfirmedAsync(
                VivoCommands.SetDoubleTap,
                [code],
                f => f.Command == VivoCommands.ReportDoubleTap && VivoParsers.TryParseDoubleTap(f.Payload, out byte l, out byte r) && (right ? r : l) == code,
                VivoCommands.QueryDoubleTap,
                [],
                VivoCommands.ReportDoubleTap,
                c), cancellationToken).ConfigureAwait(false);
        }

        if (assignment is { Button: 0, Action: GestureAction.SqueezeAndHold, Side: EarSide.Both }
            && assignment.Function is VivoGestures.LongPressNone or VivoGestures.LongPressNoiseControl or VivoGestures.LongPressDeclineCall
            && longPress is not null)
        {
            byte function = (byte)assignment.Function;
            return await RunCommandAsync(PendingKeys.KeyFunction, null, c => WriteConfirmedAsync(
                VivoCommands.SetLongPress,
                [VivoConstants.LongPressSubCommand, function],
                f => f.Command == VivoCommands.ReportLongPress && VivoParsers.TryParseLongPress(f.Payload, out byte v) && v == function,
                VivoCommands.QueryLongPress,
                [],
                VivoCommands.ReportLongPress,
                c), cancellationToken).ConfigureAwait(false);
        }

        if (assignment is { Button: VivoGestures.CycleButton, Action: GestureAction.SqueezeAndHold } && perEar
            && VivoGestures.CycleCodes.Contains(assignment.Function) && cycle is { } current
            && longPress == VivoGestures.LongPressNoiseControl)
        {
            byte left = assignment.Side == EarSide.Left ? (byte)assignment.Function : current.Left;
            byte right = assignment.Side == EarSide.Right ? (byte)assignment.Function : current.Right;
            return await RunCommandAsync(PendingKeys.KeyFunction, null, c => WriteConfirmedAsync(
                VivoCommands.SetNoiseCycle,
                [VivoConstants.NoiseCycleSubCommand, left, right],
                f => f.Command == VivoCommands.ReportNoiseCycle && VivoParsers.TryParseNoiseCycle(f.Payload, out byte l, out byte r) && l == left && r == right,
                VivoCommands.QueryNoiseCycle,
                [VivoConstants.NoiseCycleSubCommand],
                VivoCommands.ReportNoiseCycle,
                c), cancellationToken).ConfigureAwait(false);
        }
        return NotSupported("This gesture setting");
    }

    /// <summary>
    /// Dual connection: connect / disconnect write the whole reported list with only the target's
    /// state changed (others keep their reported state byte, so the playing device is not demoted);
    /// remove names this PC's adapter address. Each is confirmed by the device list.
    /// </summary>
    public override async Task<CommandResult> MultiConnectAsync(MultiConnectOperation operation, byte[] address, CancellationToken cancellationToken = default)
    {
        if (!Supported(DeviceCapability.DualDeviceConnection)) return NotSupported("Dual connection");
        if (address.Length != 6 || !Enum.IsDefined(operation)) return CommandResult.Unsupported("Invalid device operation.");
        IReadOnlyList<VivoSourceDevice>? sources;
        lock (_stateGate) sources = _sources;
        var target = sources?.FirstOrDefault(d => d.Address.AsSpan().SequenceEqual(address));
        if (sources is null || target is null) return CommandResult.Unsupported("Device is not in the current paired-device list.");
        bool connected = target.State is VivoConstants.SourceConnected or VivoConstants.SourceActive;
        bool isHost = _hostAddress is not null && address.AsSpan().SequenceEqual(_hostAddress);

        byte[] payload;
        ushort command;
        Func<IReadOnlyList<VivoSourceDevice>, bool> done;
        TimeSpan window = ConfirmTimeout;
        switch (operation)
        {
            case MultiConnectOperation.Connect when target.State == VivoConstants.SourceDisconnected:
                command = VivoCommands.SetSourceDevices;
                payload = SourceTable(sources, target, VivoConstants.SourceActive);
                done = list => Find(list)?.State is VivoConstants.SourceConnected or VivoConstants.SourceActive;
                window = SourceConnectTimeout;
                break;
            case MultiConnectOperation.Disconnect when isHost:
                return CommandResult.Unsupported("Disconnect this PC from Windows Bluetooth settings.");
            case MultiConnectOperation.Disconnect when connected:
                command = VivoCommands.SetSourceDevices;
                payload = SourceTable(sources, target, VivoConstants.SourceDisconnected);
                done = list => Find(list)?.State == VivoConstants.SourceDisconnected;
                break;
            case MultiConnectOperation.Remove when connected:
                return new CommandResult(CommandStatus.Rejected, "remove-connected");
            case MultiConnectOperation.Remove when target.State == VivoConstants.SourceDisconnected && _hostAddress is not null:
                command = VivoCommands.RemoveSourceDevice;
                payload = [.. address, .. _hostAddress];
                done = list => Find(list) is null;
                break;
            default:
                return CommandResult.Unsupported("This device operation is not available.");
        }

        return await RunCommandAsync(PendingKeys.MultiConnect, null, c => WriteConfirmedAsync(
            command,
            payload,
            f => f.Command == VivoCommands.ReportSourceDevices && VivoParsers.TryParseSourceDevices(f.Payload, out var list) && done(list),
            VivoCommands.QuerySourceDevices,
            [],
            VivoCommands.ReportSourceDevices,
            c,
            window), cancellationToken).ConfigureAwait(false);

        VivoSourceDevice? Find(IReadOnlyList<VivoSourceDevice> list) => list.FirstOrDefault(d => d.Address.AsSpan().SequenceEqual(address));
    }

    /// <summary><c>[address][state]</c> for every reported device; only the target's state changes.</summary>
    private static byte[] SourceTable(IReadOnlyList<VivoSourceDevice> sources, VivoSourceDevice target, byte state)
    {
        var table = new List<byte>(sources.Count * 7);
        foreach (var device in sources)
        {
            table.AddRange(device.Address);
            table.Add(ReferenceEquals(device, target) ? state : device.State);
        }
        return [.. table];
    }

    /// <summary>
    /// Sends a write, then waits for a report that shows the new value; when none arrives, re-queries
    /// once. The device often reports the old value right after a write, so a mismatching read-back
    /// is only a rejection after the confirmation window.
    /// </summary>
    private async Task<CommandResult> WriteConfirmedAsync(
        ushort setCommand,
        byte[] payload,
        Func<VivoFrame, bool> confirms,
        ushort queryCommand,
        byte[] queryPayload,
        ushort reportCommand,
        CancellationToken ct,
        TimeSpan? window = null)
    {
        var waiter = Register(confirms);
        try
        {
            if (!await SendAsync(setCommand, payload, ct).ConfigureAwait(false)) return new CommandResult(CommandStatus.TransportError);
            if (await AwaitAsync(waiter, window ?? ConfirmTimeout, ct).ConfigureAwait(false) is not null) return CommandResult.Ok;
        }
        finally
        {
            Unregister(waiter);
        }

        var readBack = await RequestAsync(queryCommand, queryPayload, f => f.Command == reportCommand, QueryTimeout, ct).ConfigureAwait(false);
        if (readBack is null)
        {
            return Closed.IsCompleted ? new CommandResult(CommandStatus.TransportError) : new CommandResult(CommandStatus.Timeout, "The earbuds did not confirm the change.");
        }
        return confirms(readBack) ? CommandResult.Ok : new CommandResult(CommandStatus.Rejected, "The earbuds kept the previous setting.");
    }

    private async Task<VivoFrame?> QueryAsync(ushort command, byte[] payload, ushort report, CancellationToken ct) =>
        await RequestAsync(command, payload, f => f.Command == report, QueryTimeout, ct).ConfigureAwait(false);

    private async Task<VivoFrame?> RequestAsync(ushort command, byte[] payload, Func<VivoFrame, bool> match, TimeSpan timeout, CancellationToken ct)
    {
        var waiter = Register(match);
        try
        {
            if (!await SendAsync(command, payload, ct).ConfigureAwait(false)) return null;
            return await AwaitAsync(waiter, timeout, ct).ConfigureAwait(false);
        }
        finally
        {
            Unregister(waiter);
        }
    }

    private async Task<VivoFrame?> AwaitAsync(Waiter waiter, TimeSpan timeout, CancellationToken ct)
    {
        try
        {
            return await waiter.Completion.Task.WaitAsync(timeout, Context.Time, ct).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            return null;
        }
    }

    private Task<bool> SendAsync(ushort command, byte[] payload, CancellationToken ct)
    {
        var frame = new VivoFrame(command, payload);
        Logger.LogDebug("vivo TX {Frame}", frame);
        return WriteAsync(_gatt ? frame.EncodeGatt(Model.GaiaVersion) : frame.Encode(Model.GaiaVersion), ct);
    }

    private Waiter Register(Func<VivoFrame, bool> match)
    {
        var waiter = new Waiter(match, new TaskCompletionSource<VivoFrame>(TaskCreationOptions.RunContinuationsAsynchronously));
        lock (_waitersGate) _waiters.Add(waiter);
        return waiter;
    }

    private void Unregister(Waiter waiter)
    {
        lock (_waitersGate) _waiters.Remove(waiter);
    }

    protected override void OnReceived(ReadOnlySpan<byte> data)
    {
        IReadOnlyList<VivoFrame> frames = _gatt
            ? VivoFrame.DecodeGatt(data) is { } message ? [message] : []
            : _decoder.Feed(data);
        if (frames.Count == 0) return;
        Touch();
        foreach (var frame in frames)
        {
            Logger.LogDebug("vivo RX {Frame}", frame);
            Apply(frame);
            Waiter[] matched;
            lock (_waitersGate) matched = _waiters.Where(w => w.Match(frame)).ToArray();
            foreach (var w in matched) w.Completion.TrySetResult(frame);
        }
    }

    /// <summary>Applies a received report to the store. Source is QueryResponse when a query is waiting for it.</summary>
    private void Apply(VivoFrame frame)
    {
        StateSource source;
        lock (_waitersGate) source = _waiters.Count > 0 ? StateSource.QueryResponse : StateSource.DeviceNotification;
        var now = Now;
        var p = frame.Payload;
        switch (frame.Command)
        {
            case VivoCommands.ReportBattery when VivoParsers.TryParseBattery(p, out var l, out var r, out var c):
                Store.TryUpdate(DeviceId, s => s with
                {
                    Battery = new BatteryStatus(Wrap(l), Wrap(r), Wrap(c)),
                    LastUpdateSource = source,
                });
                break;
            case VivoCommands.ReportWearState when VivoParsers.TryParseWear(p, out var wear):
                Store.TryUpdate(DeviceId, s => s with { Wear = Observed.From(wear, now, source), LastUpdateSource = source });
                break;
            case VivoCommands.ReportNoiseMode when VivoParsers.TryParseNoise(p, out var mode):
                Store.TryUpdate(DeviceId, s => s with { NoiseControl = Observed.From(new NoiseControlState(mode), now, source), LastUpdateSource = source });
                break;
            case VivoCommands.ReportAudioEffect or VivoCommands.AckAudioEffect when VivoParsers.TryParseValue(p, out byte effect):
                byte? shown = VivoCatalog.NormalizeReportedEffect(effect);
                string? preset = shown is { } id && Model.AudioEffectIds.Contains(id) ? VivoCatalog.PresetId(id) : null;
                Store.TryUpdate(DeviceId, s => s with { Equalizer = Observed.From(new EqualizerState(preset, effect), now, source), LastUpdateSource = source });
                break;
            case VivoCommands.ReportLowLatency or VivoCommands.AckLowLatency when VivoParsers.TryParseValue(p, out byte game) && game <= 1:
                Store.TryUpdate(DeviceId, s => s with { Toggles = s.Toggles.SetItem(DeviceToggle.GamingMode, Observed.From(game == 1, now, source)), LastUpdateSource = source });
                break;
            case VivoCommands.ReportFirmware when VivoParsers.ParseFirmware(p) is { } version:
                var info = new FirmwareInfo(version, Model.ProjectName, new Dictionary<string, string>());
                Store.TryUpdate(DeviceId, s => s with { Firmware = Observed.From(info, now, StateSource.QueryResponse) });
                break;
            case VivoCommands.PeerTimeRequest:
                _ = AnswerTimeAsync();
                break;
            case VivoCommands.ReportWearDetection when HasEarbudSettings && VivoParsers.TryParseWearDetection(p, out bool wearSwitch):
                Confirm(VivoConfirmed.WearDetectionSwitch);
                Store.TryUpdate(DeviceId, s => s with { Toggles = s.Toggles.SetItem(DeviceToggle.InEarDetection, Observed.From(wearSwitch, now, source)), LastUpdateSource = source });
                break;
            case VivoCommands.ReportDoubleTap when HasEarbudSettings && VivoParsers.TryParseDoubleTap(p, out byte left, out byte right):
                lock (_stateGate) _doubleTap = (left, right);
                Confirm(VivoConfirmed.DoubleTap);
                PublishGestures(now, source);
                break;
            case VivoCommands.ReportLongPress when HasEarbudSettings && VivoParsers.TryParseLongPress(p, out byte longPress):
                lock (_stateGate) _longPress = longPress;
                Confirm(VivoConfirmed.LongPress);
                PublishGestures(now, source);
                break;
            case VivoCommands.ReportNoiseCycle when HasEarbudSettings && VivoParsers.TryParseNoiseCycle(p, out byte cycleLeft, out byte cycleRight):
                lock (_stateGate) _noiseCycle = (cycleLeft, cycleRight);
                Confirm(VivoConfirmed.NoiseCycle);
                PublishGestures(now, source);
                break;
            case VivoCommands.ReportSourceDevices when Model.DualConnection && VivoParsers.TryParseSourceDevices(p, out var devices):
                lock (_stateGate) _sources = devices;
                Confirm(VivoConfirmed.SourceDevices);
                var listed = devices.Select(ToSourceDevice).ToArray();
                Store.TryUpdate(DeviceId, s => s with { SourceDevices = Observed.From<IReadOnlyList<SourceDevice>>(listed, now, source), LastUpdateSource = source });
                break;
        }

        Observed<BatteryReading>? Wrap(BatteryReading? reading) => reading is null ? null : Observed.From(reading, now, source);
    }

    /// <summary>Records a newly confirmed setting and publishes the profile that offers it.</summary>
    private void Confirm(VivoConfirmed flag)
    {
        lock (_stateGate)
        {
            if (_confirmed.HasFlag(flag)) return;
            _confirmed |= flag;
        }
        var profile = CurrentProfile;
        Store.TryUpdate(DeviceId, s => s.Profile is VivoDeviceProfile current && current.ModelId == profile.ModelId ? s with { Profile = profile } : s);
    }

    /// <summary>Publishes every reported gesture setting as <see cref="KeyFunction"/> records.</summary>
    private void PublishGestures(DateTimeOffset now, StateSource source)
    {
        var list = new List<KeyFunction>(5);
        lock (_stateGate)
        {
            if (_doubleTap is { } tap)
            {
                list.Add(new KeyFunction(EarSide.Left, 0, GestureAction.DoubleSqueeze, VivoGestures.DoubleTapFunction(EarSide.Left, tap.Left)));
                list.Add(new KeyFunction(EarSide.Right, 0, GestureAction.DoubleSqueeze, VivoGestures.DoubleTapFunction(EarSide.Right, tap.Right)));
            }
            if (_longPress is { } hold) list.Add(new KeyFunction(EarSide.Both, 0, GestureAction.SqueezeAndHold, hold));
            if (_noiseCycle is { } cycle)
            {
                list.Add(new KeyFunction(EarSide.Left, VivoGestures.CycleButton, GestureAction.SqueezeAndHold, cycle.Left));
                list.Add(new KeyFunction(EarSide.Right, VivoGestures.CycleButton, GestureAction.SqueezeAndHold, cycle.Right));
            }
        }
        Store.TryUpdate(DeviceId, s => s with { KeyFunctions = Observed.From<IReadOnlyList<KeyFunction>>(list, now, source), LastUpdateSource = source });
    }

    /// <summary>
    /// Shown state: connected (1) and playing (2) are both "connected"; an unknown state byte is shown
    /// as busy so no action is offered for it (the raw byte is still repeated in list writes).
    /// </summary>
    private SourceDevice ToSourceDevice(VivoSourceDevice device) => new(
        device.Address,
        device.Name,
        device.State switch
        {
            VivoConstants.SourceDisconnected => 0,
            VivoConstants.SourceConnected or VivoConstants.SourceActive => 2,
            _ => 4,
        },
        _hostAddress is not null && device.Address.AsSpan().SequenceEqual(_hostAddress),
        device.State == VivoConstants.SourceActive,
        VivoParsers.DeviceType(device.DeviceClass));

    /// <summary>The earbuds ask for the local time: <c>[century][year%100][month][day][hour][minute][second]</c>.</summary>
    private async Task AnswerTimeAsync()
    {
        var t = Context.Time.GetLocalNow();
        byte[] payload = [(byte)(t.Year / 100), (byte)(t.Year % 100), (byte)t.Month, (byte)t.Day, (byte)t.Hour, (byte)t.Minute, (byte)t.Second];
        try
        {
            await SendAsync(VivoCommands.HostTimeResponse, payload, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Logger.LogDebug(ex, "Failed to answer the vivo time request");
        }
    }

    private bool Supported(DeviceCapability capability) =>
        CurrentProfile.Capabilities.IsAvailable(capability, Context.AllowExperimental());
}
