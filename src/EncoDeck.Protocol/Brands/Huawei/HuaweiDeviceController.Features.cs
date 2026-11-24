// SPDX-License-Identifier: GPL-3.0-only
// Feature switches, wear state and the known-device list follow OpenFreebuds and its vicrodh fork
// (https://github.com/melianmiko/OpenFreebuds, https://github.com/vicrodh/OpenFreebuds; MelianMiko,
// vicrodh and contributors, GPL-3.0), HuaweiPods (https://github.com/Nshpiter/HuaweiPods, GPL-3.0) and
// EZBuds (https://github.com/CenSher/EZBuds, GPL-3.0) real-device notes.
using System.Text;
using EncoDeck.Core.Commands;
using EncoDeck.Core.State;

namespace EncoDeck.Protocol.Brands.Huawei;

internal sealed partial class HuaweiDeviceController
{
    private static readonly byte[] WearStateQuery = [0x01, 0x00, 0x02, 0x00];
    private static readonly byte[] DeviceListQuery = [0x01, 0x00];

    private sealed record SmartFeature(byte Id, DeviceToggle Toggle, HuaweiSmartFeatures Flag, byte[] Query);

    private static readonly SmartFeature[] SmartFeatureTable =
    [
        new(HuaweiCommands.FeatureAdaptiveVolume, DeviceToggle.AutoVolume, HuaweiSmartFeatures.AdaptiveVolume, [1, 1, HuaweiCommands.FeatureAdaptiveVolume, 2, 0]),
        new(HuaweiCommands.FeatureHeadGestures, DeviceToggle.HeadGestures, HuaweiSmartFeatures.HeadGestures, [1, 1, HuaweiCommands.FeatureHeadGestures]),
    ];

    private readonly SortedDictionary<int, (SourceDevice Device, bool Preferred)> _sources = [];
    private (byte Nod, byte Shake)? _headActions;

    private static SmartFeature? FeatureFor(DeviceToggle toggle) => SmartFeatureTable.FirstOrDefault(f => f.Toggle == toggle);

    private static byte[] FeatureWrite(byte feature, byte field, byte value) => [1, 1, feature, field, 1, value];

    /// <summary>
    /// Read-only probes with a short timeout. At session start every candidate the model lists is
    /// asked once; a refresh re-reads only what the earbuds already reported.
    /// </summary>
    private async Task ProbeFeaturesAsync(bool confirmedOnly, CancellationToken ct)
    {
        foreach (var feature in SmartFeatureTable)
        {
            if (!Model.SmartFeatures.HasFlag(feature.Flag) || (confirmedOnly && !Profile.Toggles.Contains(feature.Toggle))) continue;
            await QueryAsync(HuaweiCommands.FeatureState, feature.Query, ct, ProbeTimeout).ConfigureAwait(false);
        }
        if (Model.WearState) await QueryAsync(HuaweiCommands.WearState, WearStateQuery, ct, ProbeTimeout).ConfigureAwait(false);
        if (Model.DualConnect && (!confirmedOnly || Store.Current.SourceDevices is { Source: not StateSource.Cache }))
            await QueryAsync(HuaweiCommands.DualConnectDevices, DeviceListQuery, ct, ProbeTimeout).ConfigureAwait(false);
    }

    /// <summary>Adaptive volume (feature 2) and head gestures (feature 11): TLV 2 is the switch.</summary>
    private void ApplySmartFeature(byte[] payload, StateSource source)
    {
        foreach (var feature in SmartFeatureTable)
        {
            if (!Model.SmartFeatures.HasFlag(feature.Flag) || ParseFeature(payload, feature.Id) is not { } fields) continue;
            if (fields.TryGetValue(2, out var state) && state is [0 or 1])
            {
                if (!Profile.Toggles.Contains(feature.Toggle))
                {
                    Profile = Profile.WithReportedToggle(feature.Toggle);
                    Store.TryUpdate(DeviceId, s => s with { Profile = Profile });
                }
                ApplyToggle(feature.Toggle, state[0] == 1, source);
            }
            if (feature.Id == HuaweiCommands.FeatureHeadGestures) ApplyHeadActions(fields, source);
        }
    }

    /// <summary>
    /// TLV 3 = nod, 4 = shake (0 none, 1 answer, 2 decline). Only the two mirrored pairs map onto
    /// <see cref="DeviceState.HeadMotionType"/>; any other pair hides the choice instead of guessing.
    /// </summary>
    private void ApplyHeadActions(Dictionary<byte, byte[]> fields, StateSource source)
    {
        if (!fields.TryGetValue(3, out var nod) || !fields.TryGetValue(4, out var shake) || nod is not [<= 2] || shake is not [<= 2]) return;
        _headActions = (nod[0], shake[0]);
        int? type = (nod[0], shake[0]) switch
        {
            (HuaweiCommands.HeadActionAnswer, HuaweiCommands.HeadActionDecline) => 0,
            (HuaweiCommands.HeadActionDecline, HuaweiCommands.HeadActionAnswer) => 1,
            _ => null,
        };
        Store.TryUpdate(DeviceId, s => s with { HeadMotionType = type is { } t ? Observed.From(t, Now, source) : null, LastUpdateSource = source });
    }

    /// <summary>
    /// 0 = nod answers / shake declines, 1 = the reverse. Written as two separate fields (nod, then
    /// shake), each confirmed by reading the feature back.
    /// </summary>
    public override Task<CommandResult> SetHeadMotionTypeAsync(int type, CancellationToken cancellationToken = default)
    {
        if (!Profile.Toggles.Contains(DeviceToggle.HeadGestures) || type is not (0 or 1)) return Task.FromResult(NotSupported("Head gestures"));
        byte nod = type == 0 ? HuaweiCommands.HeadActionAnswer : HuaweiCommands.HeadActionDecline;
        byte shake = type == 0 ? HuaweiCommands.HeadActionDecline : HuaweiCommands.HeadActionAnswer;
        byte[] query = FeatureFor(DeviceToggle.HeadGestures)!.Query;
        return RunExclusiveAsync(_operations, async ct =>
        {
            foreach (var (field, value) in new[] { ((byte)3, nod), ((byte)4, shake) })
            {
                if (_headActions is { } current && (field == 3 ? current.Nod : current.Shake) == value) continue;
                var result = await ExecuteConfirmedCoreAsync(PendingKeys.HeadMotion, s => s, HuaweiCommands.FeatureState, FeatureWrite(HuaweiCommands.FeatureHeadGestures, field, value),
                    HuaweiCommands.FeatureState, query, _ => _headActions is { } read && (field == 3 ? read.Nod : read.Shake) == value, ct).ConfigureAwait(false);
                if (!result.IsSuccess) return result;
            }
            return CommandResult.Ok;
        }, cancellationToken);
    }

    /// <summary>Per-earbud wear state: TLV 1 = left, 2 = right (1 = in ear, 0 = out). Case flags are not reported.</summary>
    private void ApplyWearState(Dictionary<byte, byte[]> fields, StateSource source)
    {
        static bool? Flag(Dictionary<byte, byte[]> f, byte type) => f.TryGetValue(type, out var v) && v is [0 or 1] ? v[0] == 1 : null;
        bool? left = Flag(fields, 1), right = Flag(fields, 2);
        if (left is null && right is null) return;
        Store.TryUpdate(DeviceId, s => s with { Wear = Observed.From(new WearStatus(left, right, null, null, null), Now, source), LastUpdateSource = source });
    }

    /// <summary>
    /// One frame per known device. The list is published once every index up to the reported count
    /// has arrived; addresses arrive reversed and are stored in display order.
    /// </summary>
    private void ApplySourceDevice(Dictionary<byte, byte[]> fields, StateSource source)
    {
        if (!fields.TryGetValue(2, out var countField) || countField is not [var count]) return;
        if (count == 0)
        {
            _sources.Clear();
            Store.TryUpdate(DeviceId, s => s with
            {
                SourceDevices = Observed.From<IReadOnlyList<SourceDevice>>([], Now, source),
                PreferredDevice = Observed.From(new PreferredDeviceState(null), Now, source),
            });
            return;
        }
        if (!fields.TryGetValue(3, out var indexField) || indexField is not [var index] || index >= count
            || !fields.TryGetValue(4, out var raw) || raw.Length != 6) return;
        if (index == 0 || _sources.Keys.Any(k => k >= count)) _sources.Clear();
        byte state = fields.TryGetValue(5, out var st) && st.Length == 1 ? st[0] : (byte)0;
        string name = fields.TryGetValue(9, out var n) ? Encoding.UTF8.GetString(n).TrimEnd('\0') : string.Empty;
        bool preferred = fields.TryGetValue(7, out var p) && p is [1];
        byte[] address = [.. raw.Reverse()];
        // State: 0 = not connected, 9 = playing, other values = connected. SourceDevice uses 2 for connected.
        _sources[index] = (new SourceDevice(address, name, state == 0 ? 0 : 2, false, state == 9, 0), preferred);
        if (_sources.Count != count) return;
        var devices = _sources.Values.Select(v => v.Device).ToArray();
        byte[]? preferredAddress = _sources.Values.FirstOrDefault(v => v.Preferred).Device?.Address;
        Store.TryUpdate(DeviceId, s => s with
        {
            SourceDevices = Observed.From<IReadOnlyList<SourceDevice>>(devices, Now, source),
            PreferredDevice = Observed.From(new PreferredDeviceState(preferredAddress), Now, source),
            LastUpdateSource = source,
        });
    }
}
