// SPDX-License-Identifier: GPL-3.0-only
using EncoDeck.Core.Capabilities;
using EncoDeck.Core.Commands;
using EncoDeck.Core.Devices;
using EncoDeck.Core.Profiles;
using EncoDeck.Core.State;
using EncoDeck.Protocol.Transport;

namespace EncoDeck.Protocol.Brands.Huawei;

/// <summary>
/// <see cref="IControllableProfile"/> for one HUAWEI model (or the generic HUAWEI fallback). Feature
/// switches and the EQ preset list are narrowed by what the earbuds report during the session.
/// </summary>
public sealed class HuaweiDeviceProfile : IControllableProfile
{
    private const string Evidence = "OppoPodsManager/OpenFreebuds/HuaweiPods per-model tables; not verified on hardware by EncoDeck.";

    private readonly IReadOnlyCollection<DeviceToggle> _reportedToggles;
    private readonly IReadOnlyList<byte>? _reportedEq;
    private readonly IReadOnlyCollection<SpatialAudioMode> _failedSpatial;

    /// <summary>
    /// Spatial modes offered on a model without community records: the feature-24 values are shared
    /// across the HUAWEI models that have them.
    /// </summary>
    public static IReadOnlyList<SpatialAudioMode> DefaultSpatialModes { get; } = [SpatialAudioMode.Off, SpatialAudioMode.Fixed, SpatialAudioMode.HeadTracking];

    public HuaweiDeviceProfile(HuaweiModel model, bool spatialConfirmed = false)
        : this(model, spatialConfirmed, [], null, [])
    {
    }

    private HuaweiDeviceProfile(HuaweiModel model, bool spatialConfirmed, IReadOnlyCollection<DeviceToggle> reportedToggles, IReadOnlyList<byte>? reportedEq,
        IReadOnlyCollection<SpatialAudioMode> failedSpatial)
    {
        Model = model;
        _reportedToggles = reportedToggles;
        _reportedEq = reportedEq;
        _failedSpatial = failedSpatial;
        NoiseControlOptions = BuildNoiseOptions(model);
        // The earbuds list the built-in presets they offer (EQ TLV 3); presets they do not list stay hidden.
        EqualizerPresets = [.. model.EqPresets
            .Where(p => reportedEq is null || reportedEq.Contains(p.ProtocolId))
            .Select(p => new EqualizerPreset(p.PresetId, p.DisplayName, p.ResourceKey, CapabilitySupport.Supported))];
        SpatialConfirmed = spatialConfirmed;
        SpatialModes = spatialConfirmed
            ? [.. (model.SpatialModes.Count > 0 ? model.SpatialModes : DefaultSpatialModes).Where(m => !failedSpatial.Contains(m))]
            : [];
        Capabilities = BuildCapabilities(model, SpatialModes, EqualizerPresets.Count > 0);
    }

    public HuaweiModel Model { get; }
    public bool SpatialConfirmed { get; }

    /// <summary>
    /// The spatial modes to offer. None until the earbuds report a valid spatial state; then the model's
    /// recorded modes (or <see cref="DefaultSpatialModes"/>), minus the modes the earbuds did not confirm
    /// during this connection. The built-in profile is used again on the next connection, so a failed mode
    /// is retried then.
    /// </summary>
    public IReadOnlyList<SpatialAudioMode> SpatialModes { get; }

    internal HuaweiDeviceProfile WithSpatial() => SpatialConfirmed ? this : new(Model, true, _reportedToggles, _reportedEq, _failedSpatial);

    /// <summary>Hides a spatial mode the earbuds rejected or did not read back. "Off" always stays available.</summary>
    internal HuaweiDeviceProfile WithFailedSpatial(SpatialAudioMode mode) =>
        mode == SpatialAudioMode.Off || _failedSpatial.Contains(mode) ? this : new(Model, SpatialConfirmed, _reportedToggles, _reportedEq, [.. _failedSpatial, mode]);

    /// <summary>Opens a feature switch the earbuds just reported a valid state for.</summary>
    internal HuaweiDeviceProfile WithReportedToggle(DeviceToggle toggle) =>
        _reportedToggles.Contains(toggle) ? this : new(Model, SpatialConfirmed, [.. _reportedToggles, toggle], _reportedEq, _failedSpatial);

    /// <summary>Limits the EQ presets to the built-in ids the earbuds reported.</summary>
    internal HuaweiDeviceProfile WithReportedEqualizer(IReadOnlyList<byte> ids) =>
        _reportedEq is not null && _reportedEq.SequenceEqual(ids) ? this : new(Model, SpatialConfirmed, _reportedToggles, [.. ids], _failedSpatial);

    public string Brand => "huawei";

    public bool IsGeneric => Model.IsGeneric;

    public string ModelId => Model.ModelId;

    public string DisplayName => Model.DisplayName;

    public string ImageKey => Model.ModelId;

    public DeviceForm Form => Model.HasChargingCase ? DeviceForm.Earbuds : DeviceForm.Headphones;

    public IReadOnlyCollection<DeviceToggle> Toggles =>
    [
        .. Model.WearDetection ? [DeviceToggle.InEarDetection] : Array.Empty<DeviceToggle>(),
        .. Model.LowLatency ? [DeviceToggle.GamingMode] : Array.Empty<DeviceToggle>(),
        .. Model.DualConnect ? [DeviceToggle.DualDeviceConnection] : Array.Empty<DeviceToggle>(),
        .. _reportedToggles,
    ];

    public IDeviceCapabilities Capabilities { get; }

    public IReadOnlyList<NoiseControlOption> NoiseControlOptions { get; }

    public IReadOnlyList<EqualizerPreset> EqualizerPresets { get; }

    public IReadOnlyList<Guid> ControlServiceUuids { get; } = [HuaweiCommands.SppServiceUuid];

    public int MatchScore(DeviceIdentity identity)
    {
        if (Model.IsGeneric) return HuaweiModels.IsFamilyName(identity.Name) ? 1 : 0;
        var detected = HuaweiModels.Detect(identity.Name, out bool exact);
        if (!ReferenceEquals(detected, Model)) return 0;
        return exact ? 100 : 50;
    }

    public IDeviceController CreateController(IDuplexByteChannel channel, ControllerContext context) =>
        new HuaweiDeviceController(channel, context, this, HuaweiWire.CreateCodec(Model.Protocol));

    private static NoiseControlOption[] BuildNoiseOptions(HuaweiModel model)
    {
        if (!model.NoiseControlUsable) return [];
        var options = new List<NoiseControlOption>
        {
            new(NoiseControlMode.Off, null, "Off", "NoiseMode_Off", CapabilitySupport.Supported),
        };
        if (model.AncLevels.Count > 0)
        {
            options.AddRange(model.AncLevels.Select(l => new NoiseControlOption(NoiseControlMode.NoiseCancellation, l.LevelId, l.DisplayName, l.ResourceKey, CapabilitySupport.Supported)));
        }
        else
        {
            options.Add(new(NoiseControlMode.NoiseCancellation, null, "Noise cancelling", "NoiseMode_NoiseCancellation", CapabilitySupport.Supported));
        }
        if (model.Transparency && model.TransparencyLevels.Count > 0)
        {
            options.AddRange(model.TransparencyLevels.Select(l => new NoiseControlOption(NoiseControlMode.Transparency, l.LevelId, l.DisplayName, l.ResourceKey, CapabilitySupport.Supported)));
        }
        else if (model.Transparency)
        {
            options.Add(new(NoiseControlMode.Transparency, null, "Transparency", "NoiseMode_Transparency", CapabilitySupport.Supported));
        }
        return [.. options];
    }

    private static DeviceCapabilities BuildCapabilities(HuaweiModel model, IReadOnlyList<SpatialAudioMode> spatialModes, bool hasEq)
    {
        static CapabilitySupport S(bool on) => on ? CapabilitySupport.Supported : CapabilitySupport.Unsupported;
        return DeviceCapabilities.CreateBuilder()
            .Set(DeviceCapability.BatteryLevel, S(model.RfcommBattery), Evidence)
            .Set(DeviceCapability.ChargingStatus, S(model.RfcommBattery), Evidence)
            .Set(DeviceCapability.NoiseCancellation, S(model.NoiseControlUsable), Evidence)
            .Set(DeviceCapability.NoiseCancellationLevel, S(model.NoiseControlUsable && model.AncLevels.Count > 1), Evidence)
            .Set(DeviceCapability.AdaptiveNoiseCancellation, CapabilitySupport.Unsupported, "Dynamic ANC is exposed as a noise-cancelling level.")
            .Set(DeviceCapability.Equalizer, S(hasEq), Evidence)
            .Set(DeviceCapability.CustomEqualizer, CapabilitySupport.Unsupported, "HUAWEI custom EQ is not ported; a selected custom preset is shown as its raw id.")
            .Set(DeviceCapability.InEarDetection, S(model.WearDetection), Evidence)
            .Set(DeviceCapability.GamingMode, S(model.LowLatency), Evidence)
            .Set(DeviceCapability.DualDeviceConnection, S(model.DualConnect), Evidence)
            .Set(DeviceCapability.SpatialAudio, S(spatialModes.Count > 0), "Feature 24, enabled after live readback.")
            .Set(DeviceCapability.HeadTracking, S(spatialModes.Contains(SpatialAudioMode.HeadTracking)), "Independent spatial audio mode 1, confirmed by device.")
            .Set(DeviceCapability.FindMyEarbuds, CapabilitySupport.Unsupported, "Find-device commands are unverified upstream.")
            .Build();
    }
}

/// <summary>Built-in HUAWEI profiles.</summary>
public static class HuaweiProfiles
{
    /// <summary>Every model in <see cref="HuaweiModels.All"/> plus the generic fallback.</summary>
    public static IReadOnlyList<IDeviceProfile> CreateBuiltIn() =>
        [.. HuaweiModels.All.Select(m => new HuaweiDeviceProfile(m)), new HuaweiDeviceProfile(HuaweiModels.Generic)];
}
