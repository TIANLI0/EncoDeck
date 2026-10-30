// SPDX-License-Identifier: GPL-3.0-only
using EncoDeck.Core.Capabilities;
using EncoDeck.Core.Profiles;
using EncoDeck.Core.Commands;
using EncoDeck.Protocol.Oppo;
using EncoDeck.Protocol.Brands;
using EncoDeck.Bluetooth.Control;
using EncoDeck.Core.Connection;
using EncoDeck.Core.Devices;
using EncoDeck.Core.Privacy;
using EncoDeck.Core.State;
using EncoDeck.Protocol.Oppo.Models;
using EncoDeck.Protocol.Transport;
using EncoDeck.Tests.Fakes;
using EncoDeck.Tests.Protocol;

namespace EncoDeck.Tests.Bluetooth;

public class ControlChannelSupervisorTests
{
    private static readonly DeviceIdentity X4 = new("dev-x4", "OPPO Enco X4", new BluetoothAddress(0x112233445566));
    private static readonly ReconnectPolicy FastPolicy = new(TimeSpan.FromMilliseconds(10), 2, TimeSpan.FromMilliseconds(40), 3);

    private sealed class FakeLink(bool connected) : ILinkStateSource
    {
        public bool IsConnected { get; private set; } = connected;

        public event EventHandler<bool>? ConnectionChanged;

        public bool IsLeAudio { get; private set; }

        public event EventHandler? TransportChanged;

        public LeAudioLinkState LeAudioLinks { get; private set; } = LeAudioLinkState.Empty;
        public event EventHandler? LinkDetailsChanged;

        public void SetLinks(LeAudioLinkState links)
        {
            LeAudioLinks = links;
            LinkDetailsChanged?.Invoke(this, EventArgs.Empty);
        }

        public void Set(bool connected)
        {
            IsConnected = connected;
            ConnectionChanged?.Invoke(this, connected);
        }

        public void SetLeAudio(bool leAudio)
        {
            IsLeAudio = leAudio;
            TransportChanged?.Invoke(this, EventArgs.Empty);
        }

        public void Dispose()
        {
        }
    }

    private sealed class FakeConnector : IChannelConnector
    {
        public int Attempts;
        public ChannelOpenStatus? FailWith { get; set; }
        public FakeOppoDevice? LastDevice { get; private set; }
        public IReadOnlyList<Guid> LastServices { get; private set; } = [];
        public System.Collections.Concurrent.ConcurrentQueue<IReadOnlyList<Guid>> History { get; } = new();

        public Task<ChannelOpenResult> OpenAsync(string deviceId, IReadOnlyList<Guid> serviceUuids, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Attempts);
            LastServices = serviceUuids;
            History.Enqueue(serviceUuids);
            if (FailWith is { } status) return Task.FromResult(ChannelOpenResult.Failed(status, "fake failure"));
            LastDevice = new FakeOppoDevice();
            return Task.FromResult(new ChannelOpenResult(ChannelOpenStatus.Opened, LastDevice.Channel, serviceUuids[0], null));
        }
    }

    private static (ControlChannelSupervisor Supervisor, DeviceStateStore Store) Create(FakeLink link, FakeConnector connector, IControllableProfile? profile = null)
    {
        var store = new DeviceStateStore();
        store.Reset(DeviceState.Empty with { Device = X4 });
        var supervisor = new ControlChannelSupervisor(X4, profile ?? new OppoDeviceProfile(OppoModels.EncoX4), link, connector, store, FastPolicy, linkSettleDelay: TimeSpan.FromMilliseconds(20));
        return (supervisor, store);
    }

    private sealed class StartupProfile(Func<Task<bool>> initialize, bool throwOnCreate = false, IControllableProfile? inner = null) : IControllableProfile
    {
        private readonly IControllableProfile _inner = inner ?? new OppoDeviceProfile(OppoModels.EncoX4);
        public string Brand => _inner.Brand;
        public bool IsGeneric => false;
        public string ModelId => _inner.ModelId;
        public string DisplayName => _inner.DisplayName;
        public string ImageKey => _inner.ImageKey;
        public IDeviceCapabilities Capabilities => _inner.Capabilities;
        public IReadOnlyList<NoiseControlOption> NoiseControlOptions => _inner.NoiseControlOptions;
        public IReadOnlyList<EqualizerPreset> EqualizerPresets => _inner.EqualizerPresets;
        public IReadOnlyList<Guid> ControlServiceUuids => _inner.ControlServiceUuids;
        public int MatchScore(DeviceIdentity identity) => _inner.MatchScore(identity);
        public IDeviceController CreateController(IDuplexByteChannel channel, ControllerContext context) => throwOnCreate
            ? throw new IOException("controller creation failed") : new StartupController(channel, context, initialize);
    }

    private sealed class StartupController(IDuplexByteChannel channel, ControllerContext context, Func<Task<bool>> initialize)
        : BrandControllerBase(channel, context)
    {
        protected override Task<bool> StartSessionAsync(CancellationToken cancellationToken) => initialize();
        protected override void OnReceived(ReadOnlySpan<byte> data) { }
        public override Task<CommandResult> RefreshAsync(CancellationToken cancellationToken) => Task.FromResult(CommandResult.Ok);
    }

    private static Task WaitFor(DeviceStateStore store, DeviceConnectionState state, int timeoutMs = 3000) =>
        OppoDeviceControllerTests.WaitUntil(() => store.Current.Connection == state, timeoutMs);

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Xiaomi_UsesConnectedLeTransportBeforeClassicDiscovery(bool leAudio)
    {
        var link = new FakeLink(true);
        link.SetLeAudio(leAudio);
        var connector = new FakeConnector { FailWith = ChannelOpenStatus.ServiceNotFound };
        var profile = EncoDeck.Protocol.Brands.Xiaomi.XiaomiDeviceProfile.Redmi;
        var (supervisor, store) = Create(link, connector, profile);
        await using var _ = supervisor;
        supervisor.Start();
        await WaitFor(store, DeviceConnectionState.ControlUnavailable);

        var protocolServices = profile.ControlServiceUuids;
        Assert.Equal(leAudio ? protocolServices[2] : protocolServices[0], connector.LastServices[0]);
        Assert.Equal(protocolServices.Order(), connector.LastServices.Order());
        Assert.Equal(1, connector.Attempts);
    }

    [Fact]
    public async Task Xiaomi_HandshakeFailureOverLeGatt_NextAttemptTriesSppFirst_ThenAlternates()
    {
        var link = new FakeLink(true);
        link.SetLeAudio(true);
        var connector = new FakeConnector();
        var redmi = EncoDeck.Protocol.Brands.Xiaomi.XiaomiDeviceProfile.Redmi;
        // The fake connector opens the first service it is offered; every handshake stalls.
        var profile = new StartupProfile(() => throw new IOException("Xiaomi control write timed out"), inner: redmi);
        var (supervisor, store) = Create(link, connector, profile);
        await using var _ = supervisor;
        supervisor.Start();
        await WaitFor(store, DeviceConnectionState.ControlUnavailable);

        var mma = EncoDeck.Protocol.Brands.Xiaomi.XiaomiProtocol.MmaGattServiceUuid;
        var spp = EncoDeck.Protocol.Brands.Xiaomi.XiaomiProtocol.FastConnectSppUuid;
        var firsts = connector.History.Select(services => services[0]).ToArray();
        Assert.Equal([mma, spp, mma, spp], firsts);
        // Every attempt still offers all services, so GATT stays a fallback behind SPP.
        Assert.All(connector.History, services => Assert.Equal(redmi.ControlServiceUuids.Order(), services.Order()));
        Assert.Equal(spp, connector.History.ElementAt(1)[0]);
        Assert.Equal(mma, connector.History.ElementAt(1)[^1]);
    }

    [Fact]
    public async Task Xiaomi_SuccessfulHandshake_RestoresLeGattFirst()
    {
        var link = new FakeLink(true);
        link.SetLeAudio(true);
        var connector = new FakeConnector();
        int calls = 0;
        var redmi = EncoDeck.Protocol.Brands.Xiaomi.XiaomiDeviceProfile.Redmi;
        var profile = new StartupProfile(() => Interlocked.Increment(ref calls) == 1
            ? throw new IOException("Xiaomi control write timed out") : Task.FromResult(true), inner: redmi);
        var (supervisor, store) = Create(link, connector, profile);
        await using var _ = supervisor;
        supervisor.Start();
        await WaitFor(store, DeviceConnectionState.ControlReady);
        supervisor.RequestReconnect();
        await OppoDeviceControllerTests.WaitUntil(() => connector.Attempts >= 3, 3000);
        await WaitFor(store, DeviceConnectionState.ControlReady);

        var mma = EncoDeck.Protocol.Brands.Xiaomi.XiaomiProtocol.MmaGattServiceUuid;
        var spp = EncoDeck.Protocol.Brands.Xiaomi.XiaomiProtocol.FastConnectSppUuid;
        Assert.Equal([mma, spp, mma], connector.History.Take(3).Select(services => services[0]).ToArray());
    }

    [Fact]
    public async Task OnlyTheBridgeLinked_ControlRunsThroughIt()
    {
        var windows = new FakeLink(false);
        var windowsConnector = new FakeConnector();
        var bridgeConnector = new FakeConnector();
        var store = new DeviceStateStore();
        store.Reset(DeviceState.Empty with { Device = X4 });
        using var link = new BridgedLinkState(windows, store, X4.DeviceId);
        var connector = new BridgeRoutingConnector(windowsConnector, bridgeConnector, link);
        await using var supervisor = new ControlChannelSupervisor(X4, new OppoDeviceProfile(OppoModels.EncoX4), link, connector, store, FastPolicy, linkSettleDelay: TimeSpan.FromMilliseconds(20));
        supervisor.Start();
        await WaitFor(store, DeviceConnectionState.PairedNotConnected);

        // A core without RFCOMM (or still connecting) does not count as a link.
        store.TryUpdate(X4.DeviceId, s => s with { Bridge = new BridgeAudioLink(true, "LDAC", 990, 96000, "X4") });
        store.TryUpdate(X4.DeviceId, s => s with { Bridge = new BridgeAudioLink(false, null, 0, 0, "X4", CanCarryControl: true) });
        await Task.Delay(100, TestContext.Current.CancellationToken);
        Assert.False(link.IsConnected);
        Assert.Equal(0, bridgeConnector.Attempts);

        store.TryUpdate(X4.DeviceId, s => s with { Bridge = new BridgeAudioLink(true, "LDAC", 990, 96000, "X4", CanCarryControl: true) });
        await WaitFor(store, DeviceConnectionState.ControlReady);
        Assert.True(store.Current.LinkViaBridge);
        Assert.Equal(1, bridgeConnector.Attempts);
        Assert.Equal(0, windowsConnector.Attempts);
        Assert.Equal(new OppoDeviceProfile(OppoModels.EncoX4).ControlServiceUuids, bridgeConnector.LastServices);

        // The stream stops: the link is gone with it.
        store.TryUpdate(X4.DeviceId, s => s with { Bridge = null });
        await WaitFor(store, DeviceConnectionState.Disconnected);
        Assert.False(store.Current.LinkViaBridge);
        Assert.Null(supervisor.Controller);

        // Windows' link is preferred while it is up.
        windows.Set(true);
        await WaitFor(store, DeviceConnectionState.ControlReady);
        store.TryUpdate(X4.DeviceId, s => s with { Bridge = new BridgeAudioLink(true, "LDAC", 990, 96000, "X4", CanCarryControl: true) });
        await Task.Delay(100, TestContext.Current.CancellationToken);
        Assert.Equal(1, windowsConnector.Attempts);
        Assert.Equal(1, bridgeConnector.Attempts);
        Assert.False(store.Current.LinkViaBridge);
    }

    [Fact]
    public async Task NoWindowsRadio_ControlStillRunsThroughTheBridge()
    {
        // A2DPWB took the only adapter over: Windows cannot open the earbuds at all (0x800710DF).
        var bridgeConnector = new FakeConnector();
        var store = new DeviceStateStore();
        store.Reset(DeviceState.Empty with { Device = X4, Connection = DeviceConnectionState.PairedNotConnected });
        // Already streaming when EncoDeck selects the earbuds.
        store.TryUpdate(X4.DeviceId, s => s with { Bridge = new BridgeAudioLink(true, "LHDC V5", 500, 48000, "X4", CanCarryControl: true) });
        using var link = new BridgedLinkState(new NoWindowsLink(), store, X4.DeviceId);
        var connector = new BridgeRoutingConnector(new FakeConnector(), bridgeConnector, link);
        await using var supervisor = new ControlChannelSupervisor(X4, new OppoDeviceProfile(OppoModels.EncoX4), link, connector, store, FastPolicy, linkSettleDelay: TimeSpan.FromMilliseconds(20));
        supervisor.Start();

        await WaitFor(store, DeviceConnectionState.ControlReady);
        Assert.True(store.Current.LinkViaBridge);
        Assert.Equal(1, bridgeConnector.Attempts);
    }

    [Fact]
    public void OnlyRfcommServicesGoThroughTheBridge()
    {
        Assert.True(EncoDeck.Bluetooth.Rfcomm.RfcommChannelConnector.IsRfcommService(OppoServiceUuids.Primary));
        Assert.False(EncoDeck.Bluetooth.Rfcomm.RfcommChannelConnector.IsRfcommService(EncoDeck.Protocol.Brands.Sennheiser.SennheiserProfile.GattServiceId));
        Assert.False(EncoDeck.Bluetooth.Rfcomm.RfcommChannelConnector.IsRfcommService(EncoDeck.Protocol.Brands.Moondrop.MoondropConstants.GattServiceId));
        Assert.True(EncoDeck.Bluetooth.Rfcomm.RfcommChannelConnector.IsRfcommService(EncoDeck.Protocol.Brands.Moondrop.MoondropConstants.SppServiceId));
    }

    [Fact]
    public async Task LinkAlreadyUp_ConnectsToReady()
    {
        var link = new FakeLink(true);
        var connector = new FakeConnector();
        var (supervisor, store) = Create(link, connector);
        await using var _ = supervisor;
        supervisor.Start();
        await WaitFor(store, DeviceConnectionState.ControlReady);
        Assert.NotNull(supervisor.Controller);
        Assert.Equal(1, connector.Attempts);
        Assert.Equal(100, store.Current.Battery.Left!.Value.Level);
    }

    [Fact]
    public async Task SingleEarDrop_UpdatesStateWithoutRestartingHealthyControl()
    {
        var link = new FakeLink(true);
        link.SetLeAudio(true);
        link.SetLinks(new(true, true, true, true));
        var connector = new FakeConnector();
        var (supervisor, store) = Create(link, connector);
        await using var lifetime = supervisor;
        supervisor.Start();
        await WaitFor(store, DeviceConnectionState.ControlReady);
        var channel = connector.LastDevice!.Channel;
        link.SetLinks(new(true, true, true, false));
        await OppoDeviceControllerTests.WaitUntil(() => store.Current.LeAudioLinks.ConnectedEarbuds == 1);
        await Task.Delay(100, TestContext.Current.CancellationToken);
        Assert.Equal(DeviceConnectionState.ControlReady, store.Current.Connection);
        Assert.Equal(1, connector.Attempts);
        Assert.False(channel.IsDisposed);
        Assert.True(store.Current.LeAudioLinks.PrimaryConnected);
        Assert.False(store.Current.LeAudioLinks.SecondaryConnected);
    }

    [Fact]
    public async Task OtherEarReturns_ResumesExhaustedControlWithoutCyclingWholeLink()
    {
        var link = new FakeLink(true);
        link.SetLeAudio(true);
        link.SetLinks(new(true, false, true, true));
        var connector = new FakeConnector { FailWith = ChannelOpenStatus.ConnectFailed };
        var (supervisor, store) = Create(link, connector);
        await using var lifetime = supervisor;
        supervisor.Start();
        await WaitFor(store, DeviceConnectionState.ControlUnavailable);
        int attempts = connector.Attempts;
        connector.FailWith = null;
        link.SetLinks(new(true, true, true, true));
        await WaitFor(store, DeviceConnectionState.ControlReady);
        Assert.Equal(attempts + 1, connector.Attempts);
        Assert.True(store.Current.LeAudioLinks.BothConnected);
    }

    [Fact]
    public async Task OtherEarChangesDuringHandshake_DoesNotDiscardSuccessfulInitialization()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var profile = new StartupProfile(() => { entered.TrySetResult(); return release.Task; });
        var link = new FakeLink(true);
        link.SetLeAudio(true);
        var connector = new FakeConnector();
        var (supervisor, store) = Create(link, connector, profile);
        await using var lifetime = supervisor;
        supervisor.Start();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);
        link.SetLinks(new(true, true, true, true));
        release.TrySetResult(true);
        await WaitFor(store, DeviceConnectionState.ControlReady);
        await Task.Delay(100, TestContext.Current.CancellationToken);
        Assert.Equal(1, connector.Attempts);
        Assert.False(connector.LastDevice!.Channel.IsDisposed);
        Assert.True(store.Current.LeAudioLinks.BothConnected);
    }

    [Fact]
    public async Task LinkDown_WaitsWithoutConnecting()
    {
        var link = new FakeLink(false);
        var connector = new FakeConnector();
        var (supervisor, store) = Create(link, connector);
        await using var _ = supervisor;
        supervisor.Start();
        await WaitFor(store, DeviceConnectionState.PairedNotConnected);
        await Task.Delay(100, TestContext.Current.CancellationToken);
        Assert.Equal(0, connector.Attempts);
    }

    [Fact]
    public async Task LinkCycle_DisconnectsThenReconnects_AndRaisesLinkConnected()
    {
        var link = new FakeLink(true);
        var connector = new FakeConnector();
        var (supervisor, store) = Create(link, connector);
        await using var _ = supervisor;
        int linkEvents = 0;
        supervisor.LinkConnected += (_, _) => Interlocked.Increment(ref linkEvents);
        supervisor.Start();
        await WaitFor(store, DeviceConnectionState.ControlReady);

        link.Set(false);
        await WaitFor(store, DeviceConnectionState.Disconnected);
        Assert.Null(supervisor.Controller);

        link.Set(true);
        await WaitFor(store, DeviceConnectionState.ControlReady);
        Assert.Equal(2, connector.Attempts);
        Assert.Equal(1, linkEvents);
    }

    [Fact]
    public async Task ChannelDropWhileLinkUp_ReconnectsWithBackoff()
    {
        var link = new FakeLink(true);
        var connector = new FakeConnector();
        var (supervisor, store) = Create(link, connector);
        await using var _ = supervisor;
        supervisor.Start();
        await WaitFor(store, DeviceConnectionState.ControlReady);

        connector.LastDevice!.Channel.RemoteClose();
        await OppoDeviceControllerTests.WaitUntil(() => connector.Attempts == 2 && store.Current.Connection == DeviceConnectionState.ControlReady);
    }

    [Fact]
    public async Task RepeatedFailures_AreBounded_ThenUnavailable_UntilManualRetry()
    {
        var link = new FakeLink(true);
        var connector = new FakeConnector { FailWith = ChannelOpenStatus.ConnectFailed };
        var (supervisor, store) = Create(link, connector);
        await using var _ = supervisor;
        supervisor.Start();

        await WaitFor(store, DeviceConnectionState.ControlUnavailable);
        int attemptsWhenExhausted = connector.Attempts;
        Assert.Equal(FastPolicy.MaxAttempts + 1, attemptsWhenExhausted);
        await Task.Delay(200, TestContext.Current.CancellationToken);
        Assert.Equal(attemptsWhenExhausted, connector.Attempts); // no runaway retries
        Assert.NotNull(store.Current.LastError);

        connector.FailWith = null;
        supervisor.RequestReconnect();
        await WaitFor(store, DeviceConnectionState.ControlReady);
    }

    [Fact]
    public async Task MissingService_IsPermanent_WithoutRetries()
    {
        var link = new FakeLink(true);
        var connector = new FakeConnector { FailWith = ChannelOpenStatus.ServiceNotFound };
        var (supervisor, store) = Create(link, connector);
        await using var _ = supervisor;
        supervisor.Start();
        await WaitFor(store, DeviceConnectionState.ControlUnavailable);
        await Task.Delay(150, TestContext.Current.CancellationToken);
        Assert.Equal(1, connector.Attempts);
    }

    [Fact]
    public async Task ClassicInsteadOfLeAudio_RetriesWithBackoff_AndTheLeAudioLinkCanRecover()
    {
        var link = new FakeLink(true);
        var connector = new FakeConnector { FailWith = ChannelOpenStatus.ClassicInsteadOfLeAudio };
        var (supervisor, store) = Create(link, connector);
        await using var _ = supervisor;
        supervisor.Start();
        await WaitFor(store, DeviceConnectionState.ControlUnavailable);
        await Task.Delay(150, TestContext.Current.CancellationToken);
        Assert.Equal(FastPolicy.MaxAttempts + 1, connector.Attempts);
        Assert.Equal("Error_LeAudioOnClassic", store.Current.LastError?.ResourceKey);
        Assert.False(store.Current.IsLeAudio);

        connector.FailWith = null;
        link.SetLeAudio(true);
        await WaitFor(store, DeviceConnectionState.ControlReady);
        Assert.True(store.Current.IsLeAudio);
    }

    [Fact]
    public async Task LeAudioComesUpWhileServing_ReopensControlAndReleasesTheOldChannel()
    {
        var link = new FakeLink(true);
        var connector = new FakeConnector();
        var (supervisor, store) = Create(link, connector);
        await using var _ = supervisor;
        supervisor.Start();
        await WaitFor(store, DeviceConnectionState.ControlReady);
        var previous = connector.LastDevice!.Channel;

        link.SetLeAudio(true);
        await OppoDeviceControllerTests.WaitUntil(() => connector.Attempts == 2 && store.Current.Connection == DeviceConnectionState.ControlReady);
        Assert.True(previous.IsDisposed);
        Assert.True(store.Current.IsLeAudio);
    }

    [Fact]
    public async Task MissingLeEndpoint_StopsConnectingWithoutRetrying_AndManualRetryCanRecover()
    {
        var link = new FakeLink(true);
        var connector = new FakeConnector { FailWith = ChannelOpenStatus.ControlEndpointNotFound };
        var (supervisor, store) = Create(link, connector);
        await using var _ = supervisor;
        supervisor.Start();
        await WaitFor(store, DeviceConnectionState.ControlUnavailable);
        await Task.Delay(150, TestContext.Current.CancellationToken);
        Assert.True(link.IsConnected);
        Assert.Null(supervisor.Controller);
        Assert.Equal(1, connector.Attempts);
        Assert.Equal("fake failure", store.Current.LastError?.Message);
        Assert.Equal("Error_ControlEndpointNotFound", store.Current.LastError?.ResourceKey);

        connector.FailWith = null;
        supervisor.RequestReconnect();
        await WaitFor(store, DeviceConnectionState.ControlReady);
        Assert.Null(store.Current.LastError);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ControllerStartupThrows_ReleasesChannelAndEndsConnecting(bool throwOnCreate)
    {
        var link = new FakeLink(true);
        var connector = new FakeConnector();
        var profile = new StartupProfile(() => throw new IOException("handshake failed"), throwOnCreate);
        var (supervisor, store) = Create(link, connector, profile);
        await using var _ = supervisor;
        supervisor.Start();
        await WaitFor(store, DeviceConnectionState.ControlUnavailable);
        Assert.Null(supervisor.Controller);
        Assert.NotNull(store.Current.LastError);
        Assert.Equal(FastPolicy.MaxAttempts + 1, connector.Attempts);
        Assert.True(connector.LastDevice!.Channel.IsDisposed);
    }

    [Fact]
    public async Task LinkDropsDuringHandshake_DoesNotBecomeControlReady()
    {
        var link = new FakeLink(true);
        var connector = new FakeConnector();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var profile = new StartupProfile(() => { started.TrySetResult(); return finish.Task; });
        var (supervisor, store) = Create(link, connector, profile);
        await using var _ = supervisor;
        var transitions = new List<DeviceConnectionState>();
        store.StateChanged += (_, state) => { lock (transitions) transitions.Add(state.Current.Connection); };
        supervisor.Start();
        await started.Task.WaitAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);
        link.Set(false);
        finish.SetResult(true);
        await WaitFor(store, DeviceConnectionState.Disconnected);
        Assert.Null(supervisor.Controller);
        Assert.True(connector.LastDevice!.Channel.IsDisposed);
        lock (transitions) Assert.DoesNotContain(DeviceConnectionState.ControlReady, transitions);
    }

    [Fact]
    public async Task Dispose_ReleasesChannel()
    {
        var link = new FakeLink(true);
        var connector = new FakeConnector();
        var (supervisor, store) = Create(link, connector);
        supervisor.Start();
        await WaitFor(store, DeviceConnectionState.ControlReady);
        await supervisor.DisposeAsync();
        Assert.True(connector.LastDevice!.Channel.IsDisposed);
        Assert.Null(supervisor.Controller);
    }

    [Fact]
    public async Task LinkDrop_ClearsCodecReportBeforeReconnect()
    {
        var link = new FakeLink(true);
        var (supervisor, store) = Create(link, new FakeConnector());
        await using var _ = supervisor;
        supervisor.Start();
        await WaitFor(store, DeviceConnectionState.ControlReady);
        Assert.NotNull(store.Current.Codec);
        link.Set(false);
        await WaitFor(store, DeviceConnectionState.Disconnected);
        Assert.Null(store.Current.Codec);
        Assert.Null(store.Current.ReportedCodecId);
    }

    [Fact]
    public async Task TransportSwitch_ClearsClassicCodecInTheTransportSnapshot()
    {
        var link = new FakeLink(true);
        var (supervisor, store) = Create(link, new FakeConnector());
        await using var _ = supervisor;
        supervisor.Start();
        await WaitFor(store, DeviceConnectionState.ControlReady);
        DeviceState? switched = null;
        store.StateChanged += (_, e) => { if (!e.Previous.IsLeAudio && e.Current.IsLeAudio) switched = e.Current; };
        link.SetLeAudio(true);
        Assert.NotNull(switched);
        Assert.Null(switched.Codec);
        Assert.Null(switched.ReportedCodecId);
    }

    [Fact]
    public void ReconnectPolicy_IsExponentialAndBounded()
    {
        var policy = new ReconnectPolicy(TimeSpan.FromSeconds(1), 2, TimeSpan.FromSeconds(5), 4);
        Assert.Equal(TimeSpan.FromSeconds(1), policy.GetDelay(1));
        Assert.Equal(TimeSpan.FromSeconds(2), policy.GetDelay(2));
        Assert.Equal(TimeSpan.FromSeconds(4), policy.GetDelay(3));
        Assert.Equal(TimeSpan.FromSeconds(5), policy.GetDelay(4));
        Assert.Null(policy.GetDelay(5));
        Assert.Null(policy.GetDelay(0));
    }
}
