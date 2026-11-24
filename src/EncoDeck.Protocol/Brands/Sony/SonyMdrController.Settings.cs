// SPDX-License-Identifier: GPL-3.0-only
// Setting layouts follow mos9527/SonyHeadphonesClient (libmdr), MIT License, Copyright (c) 2026
// mos9527, Amr Satrio and other contributors; see SonySettings.cs and THIRD_PARTY_NOTICES.md.
using System.Globalization;
using EncoDeck.Core.Commands;
using EncoDeck.Core.State;

namespace EncoDeck.Protocol.Brands.Sony;

internal sealed partial class SonyMdrController
{
    private const string AutoOffOption = "sony-auto-power-off";
    private const string KeyOptionPrefix = "sony-key-";
    private IReadOnlyList<SonyBoolSetting>? _boolSettings;
    private readonly HashSet<DeviceToggle> _toggles = [];
    /// <summary>General-setting slot (0xD1–0xD4) → toggle, from the device's boolean setting names.</summary>
    private Dictionary<byte, DeviceToggle>? _generalSettings;
    private byte? _autoOffType;
    private IReadOnlyList<byte>? _autoOffCapability;
    private IReadOnlyList<SonySettings.AssignableKey>? _keys;
    private byte[]? _keyPresets;

    private async Task RefreshSettings(CancellationToken ct)
    {
        if (_functions is null) return;
        _boolSettings ??= SonySettings.BooleanSettings(_v2, _functions);
        foreach (var setting in _boolSettings)
        {
            if (await Query(setting.Get, (byte)(setting.Get + 1), setting.Type, ct).ConfigureAwait(false) is { } reply && setting.Parse(reply) is not null)
                _toggles.Add(setting.Toggle);
        }
        if (_generalSettings is null)
        {
            _generalSettings = [];
            foreach (byte type in new byte[] { 0xD1, 0xD2, 0xD3, 0xD4 })
            {
                if (!Has(type) || (!_v2 && type == 0xD4)) continue;
                if (await Query(0xD0, 0xD1, type, ct, [type, 1]).ConfigureAwait(false) is { } capability
                    && SonySettings.TryGeneralSettingName(capability, _v2, type) is { } name
                    && SonySettings.GeneralSettingToggle(name) is { } toggle && !_generalSettings.ContainsValue(toggle))
                    _generalSettings[type] = toggle;
            }
        }
        foreach (var (type, toggle) in _generalSettings)
        {
            if (await Query(0xD6, 0xD7, type, ct).ConfigureAwait(false) is { } reply && SonySettings.TryGeneralSettingValue(reply, _v2, type) is not null)
                _toggles.Add(toggle);
        }
        await RefreshAutoOff(ct).ConfigureAwait(false);
        if (_v2 && Has(SonySettings.V2AssignableSetting)) await RefreshKeys(ct).ConfigureAwait(false);
        _profile = _profile.WithToggles(_toggles);
        PublishProfile();
    }

    private async Task RefreshAutoOff(CancellationToken ct)
    {
        _autoOffType ??= !_v2 ? Has(SonySettings.V1AutoPowerOff) ? (byte)4 : null
            : Has(SonySettings.V2AutoPowerOff) ? (byte)4 : Has(SonySettings.V2AutoPowerOffWearing) ? (byte)5 : null;
        if (_autoOffType is not { } type) return;
        byte capability = _v2 ? (byte)0x20 : (byte)0xF0, get = _v2 ? (byte)0x26 : (byte)0xF6;
        if (_autoOffCapability is null && await Query(capability, (byte)(capability + 1), type, ct).ConfigureAwait(false) is { } reply)
            _autoOffCapability = SonySettings.TryAutoOffCapability(reply, (byte)(capability + 1), type)?.Where(e => IsAutoOffElement(e)).ToArray();
        await Query(get, (byte)(get + 1), type, ct).ConfigureAwait(false);
    }

    private bool IsAutoOffElement(byte element) => SonySettings.IsAutoOffElement(element, _v2, _autoOffType == 5);

    private async Task RefreshKeys(CancellationToken ct)
    {
        if (_keys is null && await Query(0xF0, 0xF1, 3, ct).ConfigureAwait(false) is { } capability)
            _keys = SonySettings.TryAssignableCapability(capability) ?? [];
        if (_keys is { Count: > 0 }) await Query(0xF6, 0xF7, 3, ct).ConfigureAwait(false);
    }

    /// <summary>Settings replies and notifications; returns true when the payload was recognised.</summary>
    private bool HandleSetting(byte[] p)
    {
        foreach (var setting in _boolSettings ?? [])
        {
            if (!setting.Matches(p) || setting.Parse(p) is not { } on) continue;
            SetToggle(setting.Toggle, on);
            return true;
        }
        if (_generalSettings is not null && p.Length >= 2 && _generalSettings.TryGetValue(p[1], out var toggle)
            && SonySettings.TryGeneralSettingValue(p, _v2, p[1]) is { } value)
        {
            SetToggle(toggle, value);
            return true;
        }
        if (_autoOffType is { } type && p.Length >= 4 && p[1] == type
            && (_v2 ? p[0] is 0x27 or 0x29 && p.Length == 4 : p[0] is 0xF7 or 0xF9 && p.Length == 5 && p[2] == 1))
        {
            byte current = p[_v2 ? 2 : 3];
            if (!IsAutoOffElement(current)) return false;
            // Without a capability list offer only what is certain: the current value and "off".
            var elements = (_autoOffCapability is { Count: > 0 } known ? known : [SonySettings.AutoOffDisabled]).Append(current).Distinct();
            SetOption(new DeviceOption(AutoOffOption, "AutoOff_Title", current, elements.Select(e => SonySettings.AutoOffChoice(e, _v2)).ToArray(), 10));
            return true;
        }
        if (_keys is { Count: > 0 } keys && SonySettings.TryAssignablePresets(p, keys.Count) is { } presets)
        {
            _keyPresets = presets;
            for (int i = 0; i < keys.Count; i++)
            {
                var choices = keys[i].Presets.Append(presets[i]).Distinct().Select(SonySettings.PresetChoice).ToArray();
                SetOption(new DeviceOption(KeyOptionPrefix + i.ToString(CultureInfo.InvariantCulture), SonySettings.KeyLabel(keys[i].Key), presets[i], choices, 20 + i));
            }
            return true;
        }
        return false;
    }

    private void SetToggle(DeviceToggle toggle, bool on) =>
        Store.TryUpdate(DeviceId, s => s with { Toggles = s.Toggles.SetItem(toggle, Observed.From(on, Now, StateSource.QueryResponse)) });

    private void SetOption(DeviceOption option) =>
        Store.TryUpdate(DeviceId, s => s with { Options = s.Options.SetItem(option.Id, Observed.From(option, Now, StateSource.QueryResponse)) });

    public override async Task<CommandResult> SetToggleAsync(DeviceToggle toggle, bool enabled, CancellationToken cancellationToken)
    {
        return await Exclusive(PendingKeys.Toggle(toggle), async ct =>
        {
            if (!_toggles.Contains(toggle)) return NotSupported(toggle.ToString());
            byte[]? reply;
            bool? actual;
            if (_boolSettings?.FirstOrDefault(s => s.Toggle == toggle) is { } setting)
            {
                if (!await Send(setting.Set, setting.Payload(enabled), ct).ConfigureAwait(false)) return new(CommandStatus.TransportError);
                reply = await Query(setting.Get, (byte)(setting.Get + 1), setting.Type, ct).ConfigureAwait(false);
                actual = reply is null ? null : setting.Parse(reply);
            }
            else if (_generalSettings?.FirstOrDefault(g => g.Value == toggle) is { Value: var match, Key: var type } && match == toggle)
            {
                if (!await Send(0xD8, SonySettings.GeneralSettingPayload(_v2, type, enabled), ct).ConfigureAwait(false)) return new(CommandStatus.TransportError);
                reply = await Query(0xD6, 0xD7, type, ct).ConfigureAwait(false);
                actual = reply is null ? null : SonySettings.TryGeneralSettingValue(reply, _v2, type);
            }
            else return NotSupported(toggle.ToString());
            return actual is null ? new CommandResult(CommandStatus.Timeout, "The setting was not confirmed by a device readback.")
                : actual == enabled ? CommandResult.Ok : new CommandResult(CommandStatus.Rejected, "The device kept its previous setting.");
        }, cancellationToken).ConfigureAwait(false);
    }

    public override async Task<CommandResult> SetOptionAsync(string id, int value, CancellationToken cancellationToken = default)
    {
        return await Exclusive(PendingKeys.Option(id), async ct =>
        {
            if (!Store.Current.Options.TryGetValue(id, out var option) || !option.Value.Accepts(value)) return NotSupported($"Option {id}");
            byte[]? reply;
            if (id == AmbientLevelOption)
            {
                var current = await ReadNoise(ct).ConfigureAwait(false);
                if (current is null || !TryReadNoise(current, out var mode, out _, out _)) return new(CommandStatus.Timeout, "Noise parameters unavailable.");
                if (mode != NoiseControlMode.Transparency) return CommandResult.Unsupported("The ambient level applies to ambient sound only.");
                var payload = _v2 ? SonyV2Noise.SetPayload(current, mode, null, value) : SonyV1Noise.SetPayload(current, mode, null, value);
                if (payload is null) return CommandResult.Unsupported("Unknown noise parameter layout.");
                if (!await Send(0x68, payload, ct).ConfigureAwait(false)) return new(CommandStatus.TransportError);
                reply = await ReadNoise(ct).ConfigureAwait(false);
            }
            else if (id == AutoOffOption && _autoOffType is { } type)
            {
                // v1 (WH-1000XM3) acknowledges SYSTEM_SET_EXTENDED_PARAM without a notification; the readback confirms it.
                bool sent = _v2 ? await Send(0x28, [type, (byte)value, 0], ct).ConfigureAwait(false)
                    : await Send(0xFC, [type, 1, (byte)value, (byte)value], ct).ConfigureAwait(false);
                if (!sent) return new(CommandStatus.TransportError);
                reply = await Query(_v2 ? (byte)0x26 : (byte)0xF6, _v2 ? (byte)0x27 : (byte)0xF7, type, ct).ConfigureAwait(false);
            }
            else if (id.StartsWith(KeyOptionPrefix, StringComparison.Ordinal) && _keyPresets is { } presets
                && int.TryParse(id.AsSpan(KeyOptionPrefix.Length), NumberStyles.None, CultureInfo.InvariantCulture, out int index) && index < presets.Length)
            {
                byte[] desired = (byte[])presets.Clone();
                desired[index] = (byte)value;
                if (!await Send(0xF8, [3, (byte)desired.Length, .. desired], ct).ConfigureAwait(false)) return new(CommandStatus.TransportError);
                reply = await Query(0xF6, 0xF7, 3, ct).ConfigureAwait(false);
            }
            else return NotSupported($"Option {id}");
            if (reply is null) return new CommandResult(CommandStatus.Timeout, "The setting was not confirmed by a device readback.");
            return Store.Current.Options.TryGetValue(id, out var actual) && actual.Value.Value == value
                ? CommandResult.Ok : new CommandResult(CommandStatus.Rejected, "The device kept its previous setting.");
        }, cancellationToken).ConfigureAwait(false);
    }
}
