// SPDX-License-Identifier: GPL-3.0-only
using EncoDeck.Core.Capabilities;
using EncoDeck.Core.Commands;
using EncoDeck.Core.Devices;
using EncoDeck.Core.Privacy;
using EncoDeck.Core.State;
using EncoDeck.Protocol.Oppo;
using EncoDeck.Protocol.Oppo.Models;
using EncoDeck.Tests.Fakes;

namespace EncoDeck.Tests.Protocol;

/// <summary>
/// The model catalog built from the official configuration. The hand-made, hardware-verified Enco
/// X4 definition is the reference: the X4's catalog entry must convert to the same protocol values.
/// </summary>
public class OppoCatalogTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static OppoModelDefinition CatalogX4 => OppoCatalog.Definitions.Single(d => d.ProductIds.Contains(0x06EC10u));

    private static OppoModelDefinition Catalog(uint productId) => OppoModels.FindByProductId(productId)!;

    [Fact]
    public void Catalog_HasEveryOfficialModel()
    {
        Assert.Equal(85, OppoCatalog.Models.Count);
        Assert.Equal(136, OppoModels.All.Count);
        Assert.Equal(OppoModels.All.Count, OppoModels.All.SelectMany(m => m.ProductIds).Distinct().Count());
        Assert.Same(OppoModels.EncoX4, OppoModels.FindByProductId(0x06EC10));
        Assert.All(OppoModels.All, m => Assert.False(m.IsGeneric));
    }

    [Fact]
    public void X4FromCatalog_MatchesTheVerifiedDefinition()
    {
        var hand = OppoModels.EncoX4;
        var x4 = CatalogX4;

        Assert.Equal(hand.FeatureStatusIds, x4.FeatureStatusIds);
        Assert.Equal(hand.ServiceUuids[0], x4.ServiceUuids[0]);
        Assert.Equal(hand.BluetoothNames, x4.BluetoothNames);

        Assert.Equal(
            hand.NoiseModes.Select(m => (m.Option.Mode, m.Option.LevelId, m.SetIndex, Reported: string.Join(',', m.ReportedIndices))).OrderBy(x => x.SetIndex),
            x4.NoiseModes.Select(m => (m.Option.Mode, m.Option.LevelId, m.SetIndex, Reported: string.Join(',', m.ReportedIndices))).OrderBy(x => x.SetIndex));
        Assert.Equal(hand.NoiseModeParents.OrderBy(p => p.Key).Select(p => (p.Key, p.Value.Index, p.Value.DefaultLevelId)),
            x4.NoiseModeParents.OrderBy(p => p.Key).Select(p => (p.Key, p.Value.Index, p.Value.DefaultLevelId)));
        Assert.Equal(hand.NoiseCycleIndices, x4.NoiseCycleIndices);
        Assert.False(x4.NoiseCycleShared);

        Assert.Equal(hand.EqualizerPresets.Keys.Order(), x4.EqualizerPresets.Keys.Order());
        Assert.Equal(hand.EqualizerPresets.Select(p => (p.Key, p.Value.ResourceKey)).OrderBy(p => p.Key), x4.EqualizerPresets.Select(p => (p.Key, p.Value.ResourceKey)).OrderBy(p => p.Key));
        Assert.Equal(hand.CustomEqualizer!.BandFrequencies, x4.CustomEqualizer!.BandFrequencies);
        Assert.Equal((hand.CustomEqualizer.MinGain, hand.CustomEqualizer.MaxGain, hand.CustomEqualizer.MaxPresets), (x4.CustomEqualizer.MinGain, x4.CustomEqualizer.MaxGain, x4.CustomEqualizer.MaxPresets));

        Assert.Equal(
            hand.GestureRows.Select(r => (r.Action, r.Button, r.IsCallControl, Functions: string.Join(',', r.AllowedFunctions), r.EditsNoiseCycle, r.DefaultFunction)),
            x4.GestureRows.Select(r => (r.Action, r.Button, r.IsCallControl, Functions: string.Join(',', r.AllowedFunctions), r.EditsNoiseCycle, r.DefaultFunction)));
        Assert.Equal(hand.GestureRows.Select(r => r.ResourceKey), x4.GestureRows.Select(r => r.ResourceKey));

        Assert.Equal(hand.GameSoundTypes, x4.GameSoundTypes);
        Assert.Equal(hand.SpatialTypes, x4.SpatialTypes);
        Assert.True(x4.SpatialHiResMutex);
        Assert.True(x4.TimeReplyIncludesOffset);

        // Every switch of the verified definition, with the same feature id (the catalog adds V1 spatial, feature 27).
        foreach (var feature in hand.FeatureSwitches)
        {
            Assert.Contains(x4.FeatureSwitches, f => f.Toggle == feature.Toggle && f.FeatureId == feature.FeatureId);
        }
        foreach (var (capability, entry) in hand.Capabilities.All.Where(c => c.Value.Support == CapabilitySupport.Supported))
        {
            Assert.Equal(CapabilitySupport.Supported, x4.Capabilities.Get(capability).Support);
        }
    }

    [Fact]
    public void X4FromCatalog_UsesV2SpatialWithTheX4Capabilities()
    {
        var caps = OppoCapabilityBitmap.TryParse(new OppoFrame(0x8100, 0, [0x00, .. Convert.FromHexString("FF775AEEEE8FA65F1700000000000000")]), out var bitmap) ? bitmap : null;
        var adapted = CatalogX4.Adapt(caps, 116);
        Assert.True(adapted.HasSpatialAudioV2);
        Assert.Contains(adapted.FeatureSwitches, f => f.Toggle == DeviceToggle.GamingMode && f.FeatureId == 0x28);
        Assert.NotNull(adapted.CustomEqualizer);
    }

    [Fact]
    public void FlatNoiseTree_UsesItsOwnIndices()
    {
        // Enco Air4 (069010): noise cancelling 0, off 1, transparency 2, no levels.
        var air4 = Catalog(0x069010);
        Assert.Equal(0, air4.NoiseModes.Single(m => m.Option.Mode == NoiseControlMode.NoiseCancellation).SetIndex);
        Assert.Equal(1, air4.NoiseModes.Single(m => m.Option.Mode == NoiseControlMode.Off).SetIndex);
        Assert.Equal(2, air4.NoiseModes.Single(m => m.Option.Mode == NoiseControlMode.Transparency).SetIndex);
        Assert.Equal(0, air4.NoiseModeParents[NoiseControlMode.NoiseCancellation].Index);

        // Enco X (061410): strong 3 and weak 2 are top-level; choosing "noise cancelling" picks strong.
        var x = Catalog(0x061410);
        Assert.Equal(3, x.NoiseModes.Single(m => m.Option.LevelId == "deep").SetIndex);
        Assert.Equal(2, x.NoiseModes.Single(m => m.Option.LevelId == "light").SetIndex);
        Assert.Equal("deep", x.NoiseModeParents[NoiseControlMode.NoiseCancellation].DefaultLevelId);
    }

    [Fact]
    public void AdaptiveWithoutRanges_SendsItsOwnIndex()
    {
        var free4 = Catalog(0x068C10);
        var adaptive = Assert.Single(free4.NoiseModes, m => m.Option.Mode == NoiseControlMode.Adaptive);
        Assert.Null(adaptive.Option.LevelId);
        Assert.Equal(11, adaptive.SetIndex);
    }

    [Fact]
    public void TapGestures_MapToWireBytes()
    {
        // OnePlus Buds 3 (063C14): taps (actions 1–3), touch-and-hold noise cycle (11), call rows 29/31.
        var buds3 = Catalog(0x063C14);
        var doubleTap = buds3.GestureRows.Single(r => !r.IsCallControl && r.Action == GestureAction.DoubleSqueeze);
        Assert.Equal(1, doubleTap.Button);
        Assert.Equal("Gesture_Tap2", doubleTap.ResourceKey);
        Assert.Equal([0, 1, 5, 6, 3, 17], doubleTap.AllowedFunctions);
        var call = buds3.GestureRows.Where(r => r.IsCallControl).ToList();
        Assert.All(call, r => Assert.Equal(KeyFunction.CallButton, r.Button));
        Assert.Contains(call, r => r.Action == GestureAction.LongHold);
        // Without longPressType the hold row only edits the shared cycle.
        var hold = buds3.GestureRows.Single(r => r.EditsNoiseCycle);
        Assert.False(hold.IsWritten);
        Assert.True(buds3.NoiseCycleShared);
        Assert.Equal(1, buds3.NoiseCycleMinimum); // the configuration sets minSelectCount 1
        Assert.Equal(1, Catalog(0x063C10).NoiseCycleMinimum); // Enco X2: OPPO, no long-press functions
        Assert.Equal(2, CatalogX4.NoiseCycleMinimum);          // long-press functions: at least two
    }

    [Fact]
    public void EqualizerNames_FollowTheOfficialRules()
    {
        Assert.Equal("Eq_Mode1Alt", Catalog(0x066810).EqualizerPresets[0].ResourceKey); // X3i: equalizer 2
        Assert.Equal("Eq_Mode11", Catalog(0x062014).EqualizerPresets[0].ResourceKey);   // Buds Pro 2: balanced
        // Free4's preset 7 needs firmware 118.
        var free4 = Catalog(0x068C10);
        Assert.Equal(118, free4.EqualizerMinFirmware[7]);
        Assert.DoesNotContain((byte)7, free4.Adapt(null, 117).EqualizerPresets.Keys);
        Assert.Contains((byte)7, free4.Adapt(null, 118).EqualizerPresets.Keys);
    }

    [Fact]
    public void EveryModel_HasLabelsAndConsistentOptions()
    {
        foreach (var model in OppoModels.All)
        {
            Assert.All(model.GestureRows, r => Assert.NotEqual("Gesture_Other", r.ResourceKey));
            Assert.All(model.NoiseCycleIndices, i => Assert.True(model.NoiseCycleLabels.ContainsKey(i), $"{model.ModelId} cycle {i}"));
            Assert.All(model.NoiseModes, m => Assert.Contains(m.SetIndex, m.ReportedIndices));
            Assert.Equal(model.NoiseModes.Count, model.NoiseModes.Select(m => (m.Option.Mode, m.Option.LevelId)).Distinct().Count());
            Assert.NotEmpty(model.ServiceUuids);
        }
    }

    // ---- end to end against the emulated device ------------------------------------------------

    private sealed class Harness : IAsyncDisposable
    {
        public FakeOppoDevice Device { get; } = new();
        public DeviceStateStore Store { get; } = new();
        public OppoSession Session { get; }
        public OppoDeviceController Controller { get; }

        public Harness(string name, uint productId)
        {
            var identity = new DeviceIdentity("dev", name, new BluetoothAddress(0x112233445566));
            Device.ProductId = productId;
            Store.Reset(DeviceState.Empty with { Device = identity, Connection = DeviceConnectionState.ControlConnecting });
            Session = new OppoSession(Device.Channel);
            Session.Start();
            var registry = new DeviceProfileRegistryAdapter();
            Controller = new OppoDeviceController(Session, registry.Match(identity), identity, Store);
        }

        public async ValueTask DisposeAsync()
        {
            await Controller.DisposeAsync();
            await Session.DisposeAsync();
        }
    }

    private sealed class DeviceProfileRegistryAdapter
    {
        private readonly EncoDeck.Core.Profiles.DeviceProfileRegistry _registry = new(OppoDeviceProfile.CreateBuiltIn());

        public OppoDeviceProfile Match(DeviceIdentity identity) => (OppoDeviceProfile)_registry.Match(identity)!;
    }

    [Fact]
    public async Task OtherModel_IsControlledThroughItsConfiguration()
    {
        // Enco Free4 named as such: matched by name, confirmed by product id, writes allowed.
        await using var h = new Harness("OPPO Enco Free4", 0x068C10);
        Assert.True(await h.Controller.InitializeAsync(Ct));
        Assert.Equal("oppo-068c10", h.Controller.Profile.ModelId);
        Assert.False(h.Controller.Profile.Definition.IsGeneric);

        var result = await h.Controller.SetNoiseControlAsync(new NoiseControlState(NoiseControlMode.Transparency), Ct);
        Assert.NotEqual(CommandStatus.Unsupported, result.Status);
        Assert.Contains(h.Device.Received, f => f.Command == OppoCommand.SetNoiseControl);
    }

    [Fact]
    public async Task RenamedEarbuds_SwitchFromGenericToTheirModelAndReadItsSettings()
    {
        await using var h = new Harness("My OPPO earbuds", 0x068C10);
        Assert.True(h.Controller.Profile.Definition.IsGeneric);
        Assert.True(await h.Controller.InitializeAsync(Ct));
        Assert.Equal("oppo-068c10", h.Controller.Profile.ModelId);
        // Model-specific state was read after the switch.
        Assert.Contains(h.Device.Received, f => f.Command == OppoCommand.QueryNoiseControl);
        Assert.Contains(h.Device.Received, f => f.Command == OppoCommand.QueryEqualizer);
        Assert.NotNull(h.Store.Current.NoiseControl);
    }

    [Fact]
    public async Task Reconnecting_WithWhatWasRemembered_KeepsTheSameSettings()
    {
        // First connection: learn what these earbuds report.
        byte[] capabilities;
        int firmware;
        string key;
        await using (var first = new Harness("OPPO Enco Free4", 0x068C10))
        {
            Assert.True(await first.Controller.InitializeAsync(Ct));
            capabilities = first.Store.Current.ControlCapabilities!;
            firmware = first.Store.Current.BudFirmware!.Value;
            key = first.Controller.Profile.Definition.ProfileKey;
        }
        Assert.Equal(116, firmware);

        // Next start: the profile shown before connecting already matches, and connecting changes nothing.
        var registry = new EncoDeck.Core.Profiles.DeviceProfileRegistry(OppoDeviceProfile.CreateBuiltIn());
        var identity = new DeviceIdentity("dev", "OPPO Enco Free4", new BluetoothAddress(0x112233445566));
        var remembered = OppoDeviceProfile.Remembered((OppoDeviceProfile)registry.Match(identity)!, 0x068C10, capabilities, firmware);
        Assert.Equal(key, remembered.Definition.ProfileKey);

        var device = new FakeOppoDevice { ProductId = 0x068C10 };
        var store = new DeviceStateStore();
        store.Reset(DeviceState.Empty with { Device = identity, Profile = remembered, ControlCapabilities = capabilities, BudFirmware = firmware });
        int profileChanges = 0;
        store.StateChanged += (_, e) => { if (!ReferenceEquals(e.Previous.Profile, e.Current.Profile)) profileChanges++; };
        await using var session = new OppoSession(device.Channel);
        session.Start();
        await using var controller = new OppoDeviceController(session, remembered, identity, store);
        Assert.True(await controller.InitializeAsync(Ct));
        Assert.Equal(0, profileChanges);
        Assert.Same(remembered, store.Current.Profile);
    }

    [Fact]
    public void RememberedProductId_PicksTheModelBeforeConnecting()
    {
        var registry = new EncoDeck.Core.Profiles.DeviceProfileRegistry(OppoDeviceProfile.CreateBuiltIn());
        var renamed = (OppoDeviceProfile)registry.Match(new DeviceIdentity("dev", "My OPPO earbuds", new BluetoothAddress(1)))!;
        Assert.True(renamed.Definition.IsGeneric);
        Assert.Equal("oppo-068c10", OppoDeviceProfile.Remembered(renamed, 0x068C10, null, null).ModelId);
    }

    [Fact]
    public async Task SharedNoiseCycle_UsesTypeOne()
    {
        await using var h = new Harness("OnePlus Buds 3", 0x063C14);
        Assert.True(await h.Controller.InitializeAsync(Ct));
        Assert.Contains(h.Device.Received, f => f.Command == OppoCommand.QueryNoiseControl && f.Payload.AsSpan().SequenceEqual(new byte[] { 0x02, 0x01 }));
        var result = await h.Controller.SetNoiseCycleAsync(EarSide.Both, 0b111, Ct);
        Assert.True(result.IsSuccess, result.ToString());
        Assert.Equal([0x02, 0x01, 0x07], h.Device.Received.Last(f => f.Command == OppoCommand.SetNoiseControl).Payload);
    }
}
