// SPDX-License-Identifier: GPL-3.0-only
// Facts only (no code copied): opcodes, the device-info layout and the ANC / ambient write order
// were confirmed on a real N400NC by tthayer/akg_earbud_control (184d2e2, no licence published).
using EncoDeck.Core.Commands;
using EncoDeck.Core.Profiles;
using EncoDeck.Core.State;
using EncoDeck.Protocol.Brands.Sony;
using EncoDeck.Protocol.Transport;

namespace EncoDeck.Protocol.Brands.Akg;

/// <summary>
/// AKG N400NC / N200NC / N700NCM2 / Y600 (BES command layer, <c>[AA][cmd][len][payload]</c>, no ACK).
/// Charge comes from the device-info reply (<c>0x11</c> → <c>0x12</c>); the battery opcode <c>0x25</c>
/// never answers on an N400NC. Status reads use a selector byte (<c>0x21 [sub]</c> → <c>0x22 [sub, …]</c>).
/// </summary>
internal sealed class AkgBesController(IDuplexByteChannel channel, ControllerContext context, SonyAkgProfile profile)
    : BrandControllerBase(channel, context)
{
    private const byte DeviceInfo = 0x11, DeviceInfoReply = 0x12, Status = 0x21, StatusReply = 0x22;
    private const byte StatusAll = 0x30, StatusAnc = 0x31, StatusAmbient = 0x32, StatusEq = 0x34, StatusAutoPlay = 0x38;
    private const byte SetAnc = 0x31, SetAmbient = 0x32, SetEqPreset = 0x40, SetAutoPlay = 0x35;
    /// <summary>EQ preset indices of the shared BES command layer (N400NC confirmed).</summary>
    private static readonly (string Key, string Name)[] EqPresets =
    [
        ("AkgEq_Off", "Off"), ("AkgEq_Jazz", "Jazz"), ("AkgEq_Vocal", "Vocal"), ("AkgEq_Bass", "Bass"), ("AkgEq_User", "User"),
        ("AkgEq_Rock", "Rock"), ("AkgEq_Piano", "Piano"), ("AkgEq_Club", "Club"), ("AkgEq_Studio", "Studio"),
    ];

    private readonly AkgFrameCodec _codec = new();
    private readonly SemaphoreSlim _commands = new(1, 1);
    private readonly object _gate = new();
    private TaskCompletionSource<byte[]>? _pending;
    private Func<byte[], bool>? _match;
    private SonyAkgProfile _profile = profile;
    private bool? _anc;
    private byte? _ambient;
    private byte? _eq;

    protected override async Task<bool> StartSessionAsync(CancellationToken cancellationToken) =>
        (await RefreshAsync(cancellationToken).ConfigureAwait(false)).Status == CommandStatus.Success;

    /// <summary>Sends one frame and waits up to 3 s for a reply accepted by <paramref name="match"/>.</summary>
    private async Task<byte[]?> Exchange(byte command, byte[] payload, Func<byte[], bool> match, CancellationToken ct)
    {
        var completion = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_gate) { _pending = completion; _match = match; }
        try
        {
            if (!await WriteAsync(AkgFrameCodec.Encode(command, payload), ct).ConfigureAwait(false)) return null;
            try
            {
                await Task.WhenAny(completion.Task, Closed).WaitAsync(TimeSpan.FromSeconds(3), ct).ConfigureAwait(false);
                return completion.Task.IsCompletedSuccessfully ? await completion.Task.ConfigureAwait(false) : null;
            }
            catch (TimeoutException) { return null; }
        }
        finally { lock (_gate) { if (_pending == completion) { _pending = null; _match = null; } } }
    }

    private Task<byte[]?> ReadStatus(byte sub, CancellationToken ct) =>
        Exchange(Status, [sub], f => f[1] == StatusReply && f.Length > 3 && f[3] == sub && ValidStatus(f), ct);

    public override async Task<CommandResult> RefreshAsync(CancellationToken cancellationToken)
    {
        await _commands.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (Closed.IsCompleted) return CommandResult.NotConnected;
            var info = await Exchange(DeviceInfo, [], f => f[1] == DeviceInfoReply && f.Length >= 23, cancellationToken).ConfigureAwait(false);
            // One ALL_STATUS read carries ANC, ambient and the EQ preset; single selectors are the fallback.
            if (await ReadStatus(StatusAll, cancellationToken).ConfigureAwait(false) is null)
            {
                await ReadStatus(StatusAnc, cancellationToken).ConfigureAwait(false);
                await ReadStatus(StatusAmbient, cancellationToken).ConfigureAwait(false);
                await ReadStatus(StatusEq, cancellationToken).ConfigureAwait(false);
            }
            bool wear = await ReadStatus(StatusAutoPlay, cancellationToken).ConfigureAwait(false) is not null;
            bool noise = _anc.HasValue && _ambient.HasValue;
            _profile = _profile.WithNoiseSupport(noise).WithAmbientModes(noise).WithWearDetection(wear)
                .WithEqualizers(_eq.HasValue ? EqPresets.Select((p, i) => new EqualizerPreset($"akg-eq-{i}", p.Name, p.Key, Core.Capabilities.CapabilitySupport.Supported)).ToArray() : []);
            Store.TryUpdate(DeviceId, s => s with { Profile = _profile });
            return info is null ? new(CommandStatus.Timeout, "No device-info response on the BES control service.") : CommandResult.Ok;
        }
        finally { _commands.Release(); }
    }

    public override async Task<CommandResult> SetNoiseControlAsync(NoiseControlState target, CancellationToken cancellationToken)
    {
        byte? ambient = target switch
        {
            { Mode: NoiseControlMode.Transparency, LevelId: null or SonyAkgProfile.AkgAmbientAware } => 2,
            { Mode: NoiseControlMode.Transparency, LevelId: SonyAkgProfile.AkgTalkThrough } => 1,
            { Mode: NoiseControlMode.Off or NoiseControlMode.NoiseCancellation, LevelId: null } => 0,
            _ => null,
        };
        if (ambient is null) return CommandResult.Unsupported("This protocol exposes off, noise cancelling, ambient aware and TalkThru only.");
        return await RunCommandAsync(PendingKeys.NoiseControl, _commands, async ct =>
        {
            if (!_anc.HasValue || !_ambient.HasValue) return CommandResult.Unsupported("ANC and ambient status have not been confirmed on this device.");
            // ANC and ambient are separate switches; turn the other one off first so the modes stay exclusive.
            (byte Command, byte Value)[] plan = target.Mode switch
            {
                NoiseControlMode.NoiseCancellation => [(SetAmbient, 0), (SetAnc, 1)],
                NoiseControlMode.Transparency => [(SetAnc, 0), (SetAmbient, ambient.Value)],
                _ => [(SetAnc, 0), (SetAmbient, 0)],
            };
            foreach (var (command, value) in plan)
                if (!await WriteAsync(AkgFrameCodec.Encode(command, [value]), ct).ConfigureAwait(false)) return new(CommandStatus.TransportError);
            bool all = await ReadStatus(StatusAll, ct).ConfigureAwait(false) is not null;
            if (!all && (await ReadStatus(StatusAnc, ct).ConfigureAwait(false) is null || await ReadStatus(StatusAmbient, ct).ConfigureAwait(false) is null))
                return new(CommandStatus.Timeout, "Write was not confirmed by a fresh device readback.");
            var actual = Store.Current.NoiseControl?.Value;
            return actual?.Mode == target.Mode && (target.LevelId is null || actual.LevelId == target.LevelId)
                ? CommandResult.Ok : new(CommandStatus.Rejected, "Device readback differs from requested mode.");
        }, cancellationToken).ConfigureAwait(false);
    }

    public override async Task<CommandResult> SetEqualizerPresetAsync(string presetId, CancellationToken cancellationToken)
    {
        if (!presetId.StartsWith("akg-eq-", StringComparison.Ordinal) || !byte.TryParse(presetId.AsSpan(7), out byte index) || index >= EqPresets.Length)
            return NotSupported("EQ preset");
        return await RunCommandAsync(PendingKeys.Equalizer, _commands, async ct =>
        {
            if (!_eq.HasValue) return NotSupported("EQ preset");
            if (!await WriteAsync(AkgFrameCodec.Encode(SetEqPreset, [index]), ct).ConfigureAwait(false)) return new(CommandStatus.TransportError);
            var reply = await ReadStatus(StatusAll, ct).ConfigureAwait(false) ?? await ReadStatus(StatusEq, ct).ConfigureAwait(false);
            if (reply is null) return new(CommandStatus.Timeout, "Write was not confirmed by a fresh device readback.");
            return _eq == index ? CommandResult.Ok : new(CommandStatus.Rejected, "The device kept a different EQ preset.");
        }, cancellationToken).ConfigureAwait(false);
    }

    public override async Task<CommandResult> SetToggleAsync(DeviceToggle toggle, bool enabled, CancellationToken cancellationToken)
    {
        if (toggle != DeviceToggle.InEarDetection) return NotSupported(toggle.ToString());
        return await RunCommandAsync(PendingKeys.Toggle(toggle), _commands, async ct =>
        {
            if (!_profile.Toggles.Contains(DeviceToggle.InEarDetection)) return NotSupported(toggle.ToString());
            if (!await WriteAsync(AkgFrameCodec.Encode(SetAutoPlay, [enabled ? (byte)1 : (byte)0]), ct).ConfigureAwait(false)) return new(CommandStatus.TransportError);
            var response = await ReadStatus(StatusAutoPlay, ct).ConfigureAwait(false);
            return response is null ? new CommandResult(CommandStatus.Timeout)
                : (response[4] == 1) == enabled ? CommandResult.Ok : new CommandResult(CommandStatus.Rejected, "The device kept its previous auto-play/pause setting.");
        }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>A status reply this controller understands: <c>AA 22 len sub data…</c>.</summary>
    private static bool ValidStatus(byte[] f) => f[3] switch
    {
        StatusAll => f.Length >= 8 && f[4] <= 1 && f[5] <= 2,
        StatusAnc => f.Length == 5 && f[4] <= 1,
        StatusAmbient => f.Length == 5 && f[4] <= 2,
        StatusEq => f.Length == 5 && f[4] < EqPresets.Length,
        StatusAutoPlay => f.Length == 5 && f[4] <= 1,
        _ => false,
    };

    protected override void OnReceived(ReadOnlySpan<byte> data)
    {
        foreach (var frame in _codec.Feed(data))
        {
            if (Handle(frame)) Store.TryUpdate(DeviceId, s => s with { LastResponseAt = Now, LastUpdateSource = StateSource.QueryResponse });
            lock (_gate)
                if (_match?.Invoke(frame) == true) _pending?.TrySetResult(frame);
        }
    }

    private bool Handle(byte[] f)
    {
        if (f[1] == DeviceInfoReply && f.Length >= 23)
        {
            // Offsets count the three header bytes: [22] bit 7 = charging, bits 0–6 = level;
            // [29..31] firmware major.minor.patch.
            byte charge = f[22];
            if ((charge & 0x7F) <= 100)
                Store.TryUpdate(DeviceId, s => s with { Battery = s.Battery.With(BatteryComponent.Left,
                    Observed.From(new BatteryReading(charge & 0x7F, (charge & 0x80) != 0), Now, StateSource.QueryResponse)) });
            if (f.Length >= 32)
            {
                string version = $"{f[29]}.{f[30]}.{f[31]}";
                Store.TryUpdate(DeviceId, s => s with { Firmware = Observed.From(new FirmwareInfo(version, _profile.DisplayName, new Dictionary<string, string>()), Now, StateSource.QueryResponse) });
            }
            return true;
        }
        if (f[1] != StatusReply || f.Length < 5 || !ValidStatus(f)) return false;
        switch (f[3])
        {
            case StatusAll: _anc = f[4] == 1; _ambient = f[5]; _eq = f[7] < EqPresets.Length ? f[7] : null; break;
            case StatusAnc: _anc = f[4] == 1; break;
            case StatusAmbient: _ambient = f[4]; break;
            case StatusEq: _eq = f[4]; break;
            case StatusAutoPlay:
                Store.TryUpdate(DeviceId, s => s with { Toggles = s.Toggles.SetItem(DeviceToggle.InEarDetection, Observed.From(f[4] == 1, Now, StateSource.QueryResponse)) });
                return true;
        }
        if (_anc.HasValue && _ambient.HasValue)
        {
            var state = _ambient is 1 or 2
                ? new NoiseControlState(NoiseControlMode.Transparency, _ambient == 1 ? SonyAkgProfile.AkgTalkThrough : SonyAkgProfile.AkgAmbientAware)
                : new NoiseControlState(_anc.Value ? NoiseControlMode.NoiseCancellation : NoiseControlMode.Off);
            Store.TryUpdate(DeviceId, s => s with { NoiseControl = Observed.From(state, Now, StateSource.QueryResponse) });
        }
        if (_eq is { } eq) Store.TryUpdate(DeviceId, s => s with { Equalizer = Observed.From(new EqualizerState($"akg-eq-{eq}", eq), Now, StateSource.QueryResponse) });
        return true;
    }
}
