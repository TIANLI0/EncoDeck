// SPDX-License-Identifier: GPL-3.0-only
using EncoDeck.Core.Capabilities;
using EncoDeck.Core.Commands;
using EncoDeck.Core.Devices;
using EncoDeck.Core.Privacy;
using EncoDeck.Core.Profiles;
using EncoDeck.Core.State;
using EncoDeck.Protocol.Brands;
using EncoDeck.Protocol.Oppo;
using EncoDeck.Protocol.Oppo.Models;
using EncoDeck.Tests.Fakes;

namespace EncoDeck.Tests.Protocol;

/// <summary>
/// realme earbuds on the OPPO protocol (issue #14, realme Buds T500 Pro): the noise tree and EQ
/// presets from the open-source realme tables (see OppoCatalog.Realme.cs).
/// </summary>
public class RealmeTests
{
    private const uint T500Pro = 0x066452;
    private static readonly DeviceIdentity T500ProIdentity = new("dev-t500pro", "realme Buds T500 Pro", new BluetoothAddress(0x0011223344BB));
    private static readonly DeviceProfileRegistry Registry = new(BuiltInProfiles.Create());

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static OppoDeviceProfile Match(string name) =>
        Assert.IsType<OppoDeviceProfile>(Registry.Match(new DeviceIdentity("dev", name, new BluetoothAddress(0x0011223344AA))));

    [Theory]
    [InlineData("realme Buds T500 Pro", 0x066452u)]
    [InlineData("REALME BUDS T500 PRO", 0x066452u)]
    [InlineData("realme Buds T500 Pro Harry Potter Edition", 0x067452u)]
    [InlineData("realme TechLife Buds T500 Pro", 0x067852u)]
    [InlineData("realme Buds T500", 0x067052u)]
    public void Names_MatchTheirRealmeModel(string name, uint productId)
    {
        var profile = Match(name);
        Assert.False(profile.IsGeneric);
        Assert.Equal("oppo", profile.Brand);
        Assert.Contains(productId, profile.Definition.ProductIds);
        Assert.Same(OppoModels.FindByProductId(productId), profile.Definition);
    }

    [Fact]
    public void T500Pro_HasTheRealmeNoiseTreeAndEqPresets()
    {
        var model = OppoModels.FindByProductId(T500Pro)!;

        Assert.Equal(new Guid("0000079A-D102-11E1-9B23-00025B00A5A5"), model.ServiceUuids[0]);
        Assert.Equal(CapabilitySupport.Supported, model.Capabilities.Get(DeviceCapability.NoiseCancellation).Support);
        Assert.Equal(CapabilitySupport.Supported, model.Capabilities.Get(DeviceCapability.NoiseCancellationLevel).Support);
        Assert.Equal(CapabilitySupport.Supported, model.Capabilities.Get(DeviceCapability.Equalizer).Support);
        Assert.NotEqual(CapabilitySupport.Supported, model.Capabilities.Get(DeviceCapability.AdaptiveNoiseCancellation).Support);

        // One bit table for setting and reporting: off 0, transparency 1, low 2, high 3, medium 4, smart 5.
        var modes = model.NoiseModes.ToDictionary(m => (m.Option.Mode, m.Option.LevelId), m => m.SetIndex);
        Assert.Equal(0, modes[(NoiseControlMode.Off, null)]);
        Assert.Equal(1, modes[(NoiseControlMode.Transparency, null)]);
        Assert.Equal(2, modes[(NoiseControlMode.NoiseCancellation, "light")]);
        Assert.Equal(3, modes[(NoiseControlMode.NoiseCancellation, "deep")]);
        Assert.Equal(4, modes[(NoiseControlMode.NoiseCancellation, "medium")]);
        Assert.Equal(5, modes[(NoiseControlMode.NoiseCancellation, "smart")]);
        Assert.Equal(6, modes.Count);
        Assert.Equal(3, model.NoiseModeParents[NoiseControlMode.NoiseCancellation].Index);
        Assert.Equal("deep", model.FindNoiseModeByReportedBitmap(1u << 3)!.Option.LevelId);
        Assert.Empty(model.NoiseCycleIndices);

        Assert.Equal(new byte[] { 0, 1, 2, 3 }, model.EqualizerPresets.Keys.Order());
        Assert.Equal(["eq-101", "eq-102", "eq-103", "eq-104"], model.EqualizerPresets.OrderBy(p => p.Key).Select(p => p.Value.Id));
        Assert.Equal(["Pure Raw", "Deep Bass", "Pure vocals", "Pure Bass"], model.EqualizerPresets.OrderBy(p => p.Key).Select(p => p.Value.DisplayName));
        Assert.Null(model.CustomEqualizer);
    }

    [Theory]
    [InlineData(0x067452u)] // T500 Pro Harry Potter Edition: same table, not confirmed on hardware
    [InlineData(0x067852u)] // TechLife Buds T500 Pro
    [InlineData(0x062012u)] // Buds Air 5
    public void UnconfirmedRealmeModels_AreExperimental(uint productId)
    {
        var model = OppoModels.FindByProductId(productId)!;
        Assert.NotEmpty(model.NoiseModes);
        Assert.Equal(4, model.EqualizerPresets.Count);
        Assert.Equal(CapabilitySupport.Experimental, model.Capabilities.Get(DeviceCapability.NoiseCancellation).Support);
        Assert.Equal(CapabilitySupport.Experimental, model.Capabilities.Get(DeviceCapability.Equalizer).Support);
        Assert.False(model.Capabilities.IsAvailable(DeviceCapability.NoiseCancellation, allowExperimental: false));
        Assert.True(model.Capabilities.IsAvailable(DeviceCapability.NoiseCancellation, allowExperimental: true));
    }

    [Theory]
    [InlineData(0x062412u)] // Buds T300: noise cancelling without levels
    [InlineData(0x067052u)] // Buds T500
    public void PlainAncModels_OfferOneNoiseCancellingMode(uint productId)
    {
        var model = OppoModels.FindByProductId(productId)!;
        var nc = Assert.Single(model.NoiseModes, m => m.Option.Mode == NoiseControlMode.NoiseCancellation);
        Assert.Null(nc.Option.LevelId);
        Assert.Equal(3, nc.SetIndex);
        Assert.NotEqual(CapabilitySupport.Supported, model.Capabilities.Get(DeviceCapability.NoiseCancellationLevel).Support);
    }

    [Fact]
    public void T310_HasLevelsWithoutSmart()
    {
        var model = OppoModels.FindByProductId(0x063C12)!;
        Assert.Equal(["deep", "medium", "light"], model.NoiseModes.Where(m => m.Option.Mode == NoiseControlMode.NoiseCancellation).Select(m => m.Option.LevelId!));
    }

    [Fact]
    public void OfficialOppoModels_AreUntouched()
    {
        // The realme EQ types never leak into OPPO / OnePlus models.
        Assert.All(OppoModels.All.Where(m => !m.DisplayName.StartsWith("realme", StringComparison.OrdinalIgnoreCase)),
            m => Assert.DoesNotContain(m.EqualizerPresets.Values, p => p.Id is "eq-101" or "eq-102" or "eq-103" or "eq-104"));
    }

    [Fact]
    public void NoiseSet_IsTheSharedOppoFrame()
    {
        // 0x0404 [01 01 mask], one-byte mask (bit 3 = noise cancelling), fixed set sequence 0xF0.
        var frame = new OppoFrame(OppoCommand.SetNoiseControl, OppoSequenceAllocator.FixedSet, [0x01, 0x01, 0x08]);
        Assert.Equal(Convert.FromHexString("AA0A00000404F003000101 08".Replace(" ", "")), frame.Encode());
    }

    [Fact]
    public void NoiseEventFromRealmeBuds_Decodes()
    {
        // A 0x0204 noise event with a two-byte little-endian mask (bit 5).
        Assert.True(OppoFrame.TryParse(Convert.FromHexString("AA0C0000040205050003010120 00".Replace(" ", "")), out var frame));
        Assert.Equal(OppoCommand.DeviceEvent, frame.Command);
        Assert.Equal(new byte[] { 0x03, 0x01, 0x01, 0x20, 0x00 }, frame.Payload);
        Assert.Equal("smart", OppoModels.FindByProductId(T500Pro)!.FindNoiseModeByReportedBitmap(0x20)!.Option.LevelId);
    }

    private sealed class Harness : IAsyncDisposable
    {
        public FakeOppoDevice Device { get; } = new() { ProductId = T500Pro, NoiseIndex = 0, ReportedIndexForSet = index => index };
        public DeviceStateStore Store { get; } = new();
        public OppoSession Session { get; }
        public OppoDeviceController Controller { get; }

        public Harness()
        {
            Store.Reset(DeviceState.Empty with { Device = T500ProIdentity, Connection = DeviceConnectionState.ControlConnecting });
            Session = new OppoSession(Device.Channel);
            Session.Start();
            Controller = new OppoDeviceController(Session, new OppoDeviceProfile(OppoModels.FindByProductId(T500Pro)!), T500ProIdentity, Store, () => false);
        }

        public async ValueTask DisposeAsync()
        {
            await Controller.DisposeAsync();
            await Session.DisposeAsync();
        }
    }

    [Fact]
    public async Task T500Pro_ReadsNoiseAndEq_ThenSetsThemWithReadBack()
    {
        await using var h = new Harness();
        h.Device.EqualizerPreset = 1;
        Assert.True(await h.Controller.InitializeAsync(Ct));

        Assert.Equal(new NoiseControlState(NoiseControlMode.Off), h.Store.Current.NoiseControl!.Value);
        Assert.Equal("eq-102", h.Store.Current.Equalizer!.Value.ActivePresetId);

        var noise = await h.Controller.SetNoiseControlAsync(new NoiseControlState(NoiseControlMode.NoiseCancellation, "medium"), Ct);
        Assert.Equal(CommandStatus.Success, noise.Status);
        var set = Assert.Single(h.Device.Received, f => f.Command == OppoCommand.SetNoiseControl);
        Assert.Equal(new byte[] { 0x01, 0x01, 0x10 }, set.Payload);
        Assert.Equal(new NoiseControlState(NoiseControlMode.NoiseCancellation, "medium"), h.Store.Current.NoiseControl!.Value);

        var transparency = await h.Controller.SetNoiseControlAsync(new NoiseControlState(NoiseControlMode.Transparency), Ct);
        Assert.Equal(CommandStatus.Success, transparency.Status);
        Assert.Equal(new byte[] { 0x01, 0x01, 0x02 }, h.Device.Received.Last(f => f.Command == OppoCommand.SetNoiseControl).Payload);

        var eq = await h.Controller.SetEqualizerPresetAsync("eq-104", Ct);
        Assert.Equal(CommandStatus.Success, eq.Status);
        Assert.Equal(new byte[] { 0x03 }, Assert.Single(h.Device.Received, f => f.Command == OppoCommand.SetEqualizer).Payload);
        Assert.Equal("eq-104", h.Store.Current.Equalizer!.Value.ActivePresetId);
    }
}
