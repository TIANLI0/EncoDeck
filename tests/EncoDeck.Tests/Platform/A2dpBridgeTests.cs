// SPDX-License-Identifier: GPL-3.0-only
using System.Globalization;
using System.IO.Pipes;
using System.Text.Json.Nodes;
using EncoDeck.Core.Devices;
using EncoDeck.Core.Privacy;
using EncoDeck.Platform.A2dpBridge;

namespace EncoDeck.Tests.Platform;

public sealed class A2dpBridgeTests
{
    private static JsonObject State(string streamState = "streaming", string? address = "AA:BB:CC:DD:EE:FF", int volume = 100) => JsonNode.Parse($$"""
    {
      "adapter": { "status": "ready", "error": null, "adapters": [
        { "instance_id": "USB\\VID_2357&PID_0604\\00E04C239987", "vid": 9047, "pid": 1540, "display_name": "RTL8761BU (0x2357:0x0604)",
          "device_name": "TP-Link Bluetooth USB Adapter", "vendor": "Realtek", "driver": "winusb", "driver_service": "WinUSB",
          "realtek_chip": "RTL8761BU", "firmware_present": false, "firmware_files": ["rtl8761bu_fw.bin", "rtl8761bu_config.bin"],
          "firmware_invalid": null, "vendor_note": null },
        { "instance_id": "USB\\VID_8087&PID_0033\\5&1", "vid": 32903, "pid": 51, "display_name": "Intel", "device_name": "Intel(R) Wireless Bluetooth(R)",
          "vendor": "Intel", "driver": "bluetooth", "driver_service": "BTHUSB", "realtek_chip": null, "firmware_present": null, "firmware_files": [] } ] },
      "devices": [ { "address": "AA:BB:CC:DD:EE:FF", "name": "OPPO Enco X4", "saved": true, "audio": true, "rssi": -60, "codecs": ["ldac", "aac", "sbc"], "le_audio": false } ],
      "scanning": false, "le_scanning": false,
      "stream": { "state": "{{streamState}}", "message": { "key": "status.reconnect_attempt", "args": ["2", "10"] }, "profile": "X4",
        "info": { "codec": "LDAC", "bitrate_kbps": 990, "sample_rate": 96000, "channels": 2, "source_sample_rate": 96000, "source_channels": 2,
          "source_bits": 32, "requested_codec": null, "requested_in_use": true, "abr": true, "bitrate_capped": false,
          "source": { "kind": "loopback", "name": "", "waiting": false }, "le": null }, "volume": {{volume}} },
      "profiles": [ { "name": "X4", "device_address": "{{address}}", "device_name": "OPPO Enco X4", "codec": "ldac", "quality": "hq", "abr": true,
        "codec_fallback": true, "sample_rate": 0, "bit_depth": 0, "capture_mode": "loopback", "le_audio": false, "unknown_future_field": 1 } ],
      "active_profile": "X4",
      "settings": { "language": "zh-CN", "start_with_windows": true, "le_audio_experimental": false },
      "update": { "checking": false, "available": { "version": "2.0.2", "url": "u", "prerelease": false, "manifest_url": "m" }, "current_version": "2.0.1" },
      "config_dir": "C:\\Users\\me\\AppData\\Roaming\\A2DPWB", "log_dir": "C:\\logs", "capture": null, "clock_ms": null, "v1_running": false
    }
    """)!.AsObject();

    [Fact]
    public void ReadsTheCoresSnapshot()
    {
        var s = BridgeJson.Snapshot(State());
        Assert.Equal(BridgeAdapterStatus.Ready, s.AdapterStatus);
        Assert.Equal(2, s.Adapters.Count);
        Assert.Equal(BridgeDriver.WinUsb, s.Adapters[0].Driver);
        Assert.False(s.Adapters[0].FirmwarePresent);
        Assert.Null(s.Adapters[1].FirmwarePresent);
        Assert.True(s.HasUsableAdapter);
        Assert.Equal(-60, s.Devices[0].Rssi);
        Assert.Equal(BridgeStreamState.Streaming, s.Stream.State);
        Assert.Equal(990, s.Stream.Info?.BitrateKbps);
        Assert.Equal(100, s.Stream.Volume);
        Assert.Equal("X4", s.ActiveStreamProfile?.Name);
        Assert.True(s.Setting("start_with_windows"));
        Assert.Equal("2.0.2", s.UpdateVersion);
        // settings.set must send the core's settings back whole.
        Assert.Equal("zh-CN", s.SettingsCopy()["language"]?.GetValue<string>());
    }

    [Fact]
    public void AppliesEventsAndIgnoresKindsItDoesNotShow()
    {
        var s = BridgeJson.Snapshot(State());
        var stopped = BridgeJson.Apply(s, Event("stream", JsonNode.Parse("""{"state":"idle","message":{"key":"status.idle","args":[]},"profile":null,"info":null,"volume":null}""")), out bool bye);
        Assert.False(bye);
        Assert.Equal(BridgeStreamState.Idle, stopped?.Stream.State);
        Assert.Null(stopped?.ActiveStreamProfile);
        Assert.Null(BridgeJson.Apply(s, Event("capture", null), out _));
        Assert.Null(BridgeJson.Apply(s, Event("bye", null), out bye));
        Assert.True(bye);
    }

    [Fact]
    public void MatchesTheStreamToEncoDecksDeviceByAddress()
    {
        var x4 = new DeviceIdentity("BTHENUM#x4", "OPPO Enco X4", new BluetoothAddress(0xAABBCCDDEEFF));
        var other = new DeviceIdentity("BTHENUM#other", "Other", new BluetoothAddress(0x112233445566));
        var connected = new BridgeStatus(BridgeLink.Connected, BridgeJson.Snapshot(State()), "a2dpwb-core/2.0.1");
        var link = BridgeDeviceMatch.LinkFor(x4, connected);
        Assert.Equal(new EncoDeck.Core.State.BridgeAudioLink(true, "LDAC", 990, 96000, "X4"), link);
        Assert.Null(BridgeDeviceMatch.LinkFor(other, connected));
        Assert.Null(BridgeDeviceMatch.LinkFor(x4 with { Transport = DeviceTransport.Usb }, connected));
        Assert.Null(BridgeDeviceMatch.LinkFor(x4, BridgeStatus.NotRunning));
        var connecting = new BridgeStatus(BridgeLink.Connected, BridgeJson.Snapshot(State("connecting")), "a2dpwb-core/2.0.1");
        Assert.Equal(new EncoDeck.Core.State.BridgeAudioLink(false, null, 0, 0, "X4"), BridgeDeviceMatch.LinkFor(x4, connecting));
        Assert.Equal("2.0.1", connected.ServerVersion);
        // A core with RFCOMM can carry the control channel of a Classic stream.
        Assert.True(BridgeDeviceMatch.LinkFor(x4, connected with { Rfcomm = true })?.CanCarryControl);
        Assert.False(BridgeDeviceMatch.LinkFor(x4, connected)?.CanCarryControl);
    }

    [Fact]
    public void ListsEachProfiledAddressAsADeviceOfItsOwn()
    {
        var state = State();
        var profiles = state["profiles"]!.AsArray();
        // A second profile of the same earbuds, and one without a usable address.
        profiles.Add(JsonNode.Parse("""{ "name": "X4 2", "device_address": "aa:bb:cc:dd:ee:ff", "device_name": "", "codec": "aac" }"""));
        profiles.Add(JsonNode.Parse("""{ "name": "Bad", "device_address": "nope", "device_name": "Bad" }"""));
        var connected = new BridgeStatus(BridgeLink.Connected, BridgeJson.Snapshot(state), "a2dpwb-core/2.0.2", Rfcomm: true);

        var device = Assert.Single(BridgeDeviceMatch.Devices(connected));
        Assert.Equal(new DeviceIdentity("A2DPWB#AABBCCDDEEFF", "OPPO Enco X4", new BluetoothAddress(0xAABBCCDDEEFF)), device.Identity);
        Assert.True(device.IsStreaming);
        // The badge and the control channel match it by address, like a Windows pairing.
        Assert.True(BridgeDeviceMatch.LinkFor(device.Identity, connected)?.CanCarryControl);

        var connecting = connected with { Snapshot = BridgeJson.Snapshot(State("connecting")) };
        Assert.False(Assert.Single(BridgeDeviceMatch.Devices(connecting)).IsStreaming);
        Assert.Empty(BridgeDeviceMatch.Devices(BridgeStatus.NotRunning));
    }

    [Theory]
    [InlineData("AA:BB:CC:DD:EE:FF", 0xAABBCCDDEEFFUL)]
    [InlineData("aa-bb-cc-dd-ee-ff", 0xAABBCCDDEEFFUL)]
    [InlineData("AABBCCDDEEFF", 0xAABBCCDDEEFFUL)]
    [InlineData("AA:BB:CC:DD:EE", null)]
    [InlineData("", null)]
    public void ParsesAddresses(string text, ulong? expected) => Assert.Equal(expected, BridgeDeviceMatch.ParseAddress(text));

    [Fact]
    public void FormatsCoreMessagesLikeA2dpwb()
    {
        var zh = CultureInfo.GetCultureInfo("zh-CN");
        var en = CultureInfo.GetCultureInfo("en-US");
        Assert.Equal("正在尝试重新连接 2/10...", A2dpBridgeText.Format(new BridgeMessage("status.reconnect_attempt", ["2", "10"]), zh));
        Assert.Equal("Reconnect attempt 2/10...", A2dpBridgeText.Format(new BridgeMessage("status.reconnect_attempt", ["2", "10"]), en));
        Assert.Equal("no.such.key", A2dpBridgeText.Format(new BridgeMessage("no.such.key", []), en));
        Assert.Equal("1.5 dB, 100%, +3, x", A2dpBridgeText.Printf("%.1f dB, %d%%, %+d, %s", ["1.48", "100", "3", "x"]));
        Assert.Equal("LHDC V5", A2dpBridgeText.Codec("lhdcv5"));
    }

    [Fact]
    public void FindsTheInstanceIdOfWindowsAdapter()
    {
        Assert.Equal(@"USB\VID_8087&PID_0033\5&1A2B3C&0&10",
            WindowsAdapterGuard.InstanceIdOf(@"\\?\USB#VID_8087&PID_0033#5&1A2B3C&0&10#{92383b0e-f90e-4ac9-8d44-8c2d0d0ebda2}"));
        Assert.True(WindowsAdapterGuard.IsSameDevice(@"usb\vid_8087&pid_0033\5&1a2b3c&0&10", @"USB\VID_8087&PID_0033\5&1A2B3C&0&10"));
        Assert.False(WindowsAdapterGuard.IsSameDevice(null, "x"));
    }

    [Fact]
    public async Task HandshakeSendsHelloAndReadsTheSnapshot()
    {
        await using var core = new FakeCore();
        var accept = core.AcceptAsync(State(), rev: 5);
        await using var connection = await A2dpBridgeConnection.OpenAsync(core.PipeName, "encodeck/test", TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        var hello = await accept;
        Assert.Equal(1, hello["protocol"]?.GetValue<int>());
        Assert.Equal("encodeck/test", hello["client"]?.GetValue<string>());
        Assert.Equal("bluecaster-core/3.0.0-beta.3", connection.Server);
        Assert.Equal("X4", connection.Snapshot.ActiveProfile);
    }

    [Fact]
    public async Task ADifferentProtocolIsRefused()
    {
        await using var core = new FakeCore();
        var accept = core.AcceptAsync(null, reject: "protocol 1 not supported (core speaks 2)");
        var ex = await Assert.ThrowsAsync<BridgeRejectedException>(() =>
            A2dpBridgeConnection.OpenAsync(core.PipeName, "encodeck/test", TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
        Assert.Contains("core speaks 2", ex.Message);
        await accept;
    }

    [Fact]
    public async Task EventsFollowRevisionsAndALostOneResynchronises()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var core = new FakeCore();
        var accept = core.AcceptAsync(State(volume: 10), rev: 5);
        await using var connection = await A2dpBridgeConnection.OpenAsync(core.PipeName, "encodeck/test", TimeSpan.FromSeconds(5), ct);
        await accept;
        var changes = new List<int?>();
        var changed = new SemaphoreSlim(0);
        connection.Changed += s => { lock (changes) changes.Add(s.Stream.Volume); changed.Release(); };

        // Already in the snapshot (sent between subscribe and snapshot): ignored.
        await core.SendAsync(StreamEvent(4, volume: 1));
        // The next revision: applied.
        await core.SendAsync(StreamEvent(6, volume: 20));
        Assert.True(await changed.WaitAsync(TimeSpan.FromSeconds(5), ct));
        Assert.Equal(20, connection.Snapshot.Stream.Volume);

        // Revision 7 was lost: 8 is applied and the client asks for the whole state.
        await core.SendAsync(StreamEvent(8, volume: 30));
        var request = await core.ReadAsync(ct);
        Assert.Equal("state.snapshot", request?["method"]?.GetValue<string>());
        // An event that arrives before the answer is replayed on top of the (older) snapshot.
        await core.SendAsync(StreamEvent(9, volume: 40));
        await core.SendAsync(new JsonObject { ["type"] = "response", ["id"] = request!["id"]!.GetValue<long>(), ["result"] = State(volume: 25), ["error"] = null });
        for (int i = 0; i < 20 && connection.Snapshot.Stream.Volume != 40; i++) await Task.Delay(50, ct);
        Assert.Equal(40, connection.Snapshot.Stream.Volume);
        lock (changes) Assert.DoesNotContain(1, changes);
    }

    [Fact]
    public async Task RequestsGetTheirAnswersAndErrors()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var core = new FakeCore();
        var accept = core.AcceptAsync(State(), rev: 1);
        await using var connection = await A2dpBridgeConnection.OpenAsync(core.PipeName, "encodeck/test", TimeSpan.FromSeconds(5), ct);
        await accept;

        var start = connection.RequestAsync("stream.start", new JsonObject { ["profile"] = "X4" }, TimeSpan.FromSeconds(5), ct);
        var request = await core.ReadAsync(ct);
        Assert.Equal("stream.start", request?["method"]?.GetValue<string>());
        Assert.Equal("X4", request?["params"]?["profile"]?.GetValue<string>());
        await core.SendAsync(new JsonObject { ["type"] = "response", ["id"] = request!["id"]!.GetValue<long>(), ["result"] = null, ["error"] = null });
        Assert.Null(await start);

        var assign = connection.RequestAsync("adapter.assign_winusb", new JsonObject { ["instance_id"] = "USB\\X" }, TimeSpan.FromSeconds(5), ct);
        request = await core.ReadAsync(ct);
        await core.SendAsync(new JsonObject
        {
            ["type"] = "response", ["id"] = request!["id"]!.GetValue<long>(), ["result"] = null,
            ["error"] = JsonNode.Parse("""{"code":"adapter.uac_cancelled","message":{"key":"adapter.uac_cancelled","args":[]}}"""),
        });
        var error = await Assert.ThrowsAsync<BridgeRequestException>(() => assign);
        Assert.Equal("adapter.uac_cancelled", error.Code);

        // The stream.stop request has no params at all, as the core's serde expects.
        var stop = connection.RequestAsync("stream.stop", null, TimeSpan.FromSeconds(5), ct);
        request = await core.ReadAsync(ct);
        Assert.False(request!.ContainsKey("params"));
        // The core exits: a pending request fails, the session completes.
        await core.SendAsync(new JsonObject { ["type"] = "event", ["rev"] = 1, ["event"] = new JsonObject { ["kind"] = "bye" } });
        await connection.Completion.WaitAsync(TimeSpan.FromSeconds(5), ct);
        await Assert.ThrowsAnyAsync<IOException>(() => stop);
    }

    [Fact]
    public async Task MonitorFindsTheCoreByItsMutexAndFollowsItsLife()
    {
        var ct = TestContext.Current.CancellationToken;
        string instance = "encodeck-test-" + Guid.NewGuid().ToString("N")[..8];
        await using var monitor = new A2dpBridgeMonitor(instance: instance) { IdleInterval = TimeSpan.FromMilliseconds(100) };
        Assert.False(A2dpBridgeMonitor.IsCoreRunning(instance));
        monitor.Start();
        Assert.False(await monitor.WaitForConnectionAsync(TimeSpan.FromMilliseconds(300), ct));

        var core = new FakeCore(instance);
        var accept = core.AcceptAsync(State(), rev: 1);
        Assert.True(A2dpBridgeMonitor.IsCoreRunning(instance));
        Assert.True(await monitor.WaitForConnectionAsync(TimeSpan.FromSeconds(10), ct));
        await accept;
        Assert.Equal("3.0.0-beta.3", monitor.Status.ServerVersion);
        // Checking for the core never holds its mutex: once the core is gone, it is gone.
        await core.DisposeAsync();
        for (int i = 0; i < 50 && monitor.Status.Link != BridgeLink.NotRunning; i++) await Task.Delay(100, ct);
        Assert.Equal(BridgeLink.NotRunning, monitor.Status.Link);
        Assert.False(A2dpBridgeMonitor.IsCoreRunning(instance));
        await Assert.ThrowsAsync<BridgeUnavailableException>(() => monitor.RequestAsync("stream.stop", ct: ct));
    }

    [Fact]
    public async Task MonitorStillFindsAnA2dpwbCoreFromBeforeTheRename()
    {
        // A 2.0.x core holds only its own mutex and serves only the old pipe name.
        var ct = TestContext.Current.CancellationToken;
        string instance = "encodeck-test-" + Guid.NewGuid().ToString("N")[..8];
        await using var monitor = new A2dpBridgeMonitor(instance: instance) { IdleInterval = TimeSpan.FromMilliseconds(100) };
        monitor.Start();
        await using var core = new FakeCore(instance, legacy: true);
        var accept = core.AcceptAsync(State(), rev: 1);
        Assert.True(A2dpBridgeMonitor.IsCoreRunning(instance));
        Assert.True(await monitor.WaitForConnectionAsync(TimeSpan.FromSeconds(10), ct));
        await accept;
        Assert.Equal("a2dpwb-core/2.0.1", monitor.Status.Server);
    }

    [Fact]
    public async Task ACoreStartedRightAfterAWakeIsFoundWithoutWaitingAnInterval()
    {
        var ct = TestContext.Current.CancellationToken;
        string instance = "encodeck-test-" + Guid.NewGuid().ToString("N")[..8];
        await using var monitor = new A2dpBridgeMonitor(instance: instance) { IdleInterval = TimeSpan.FromMinutes(5) };
        monitor.Start();
        await Task.Delay(200, ct);
        // As after "Start": the wait (and its wake) comes first, the core's mutex and pipe a moment later.
        var wait = monitor.WaitForConnectionAsync(TimeSpan.FromSeconds(5), ct);
        await Task.Delay(600, ct);
        await using var core = new FakeCore(instance);
        var accept = core.AcceptAsync(State(), rev: 1);
        Assert.True(await wait);
        await accept;
    }

    private static readonly Guid ControlService = new("00001107-D102-11E1-9B23-00025B00A5A5");

    [Fact]
    public async Task AnRfcommChannelCarriesDataBothWays()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var core = new FakeCore();
        var accept = core.AcceptAsync(State(), rev: 3, features: ["rfcomm"]);
        await using var connection = await A2dpBridgeConnection.OpenAsync(core.PipeName, "encodeck/test", TimeSpan.FromSeconds(5), ct);
        await accept;
        Assert.Contains("rfcomm", connection.Features);

        var open = connection.OpenRfcommAsync("AA:BB:CC:DD:EE:FF", ControlService, ct);
        var request = await core.ReadAsync(ct);
        Assert.Equal("rfcomm.open", request?["method"]?.GetValue<string>());
        Assert.Equal("AA:BB:CC:DD:EE:FF", request?["params"]?["address"]?.GetValue<string>());
        Assert.Equal(ControlService, Guid.Parse(request!["params"]!["uuid"]!.GetValue<string>()));
        // The earbuds answer at once: their data comes right behind the answer, with the current revision.
        await Respond(core, request, new JsonObject { ["channel_id"] = 3, ["mtu"] = 990 });
        await core.SendAsync(RfcommEvent("rfcomm_data", 3, data: "AQID"));
        var channel = await open;
        Assert.Equal(990, channel.Mtu);
        var buffer = new byte[2];
        Assert.Equal(2, await channel.ReadAsync(buffer, ct));
        Assert.Equal(new byte[] { 1, 2 }, buffer);
        Assert.Equal(1, await channel.ReadAsync(buffer, ct));
        Assert.Equal(3, buffer[0]);

        // A full send queue is waited out; the same data goes again.
        var write = channel.WriteAsync(new byte[] { 0xAA, 0x55 }, ct).AsTask();
        request = await core.ReadAsync(ct);
        Assert.Equal("rfcomm.send", request?["method"]?.GetValue<string>());
        Assert.Equal(3, request?["params"]?["channel_id"]?.GetValue<int>());
        Assert.Equal("qlU=", request?["params"]?["data"]?.GetValue<string>());
        await Respond(core, request, null, Error("rfcomm.busy"));
        request = await core.ReadAsync(ct);
        Assert.Equal("qlU=", request?["params"]?["data"]?.GetValue<string>());
        await Respond(core, request, null);
        await write.WaitAsync(TimeSpan.FromSeconds(5), ct);

        // Closed by the earbuds: the reader sees the end; events of other channels change nothing.
        await core.SendAsync(RfcommEvent("rfcomm_data", 9, data: "AQID"));
        await core.SendAsync(RfcommEvent("rfcomm_closed", 3, reason: "remote"));
        Assert.Equal(0, await channel.ReadAsync(buffer, ct).AsTask().WaitAsync(TimeSpan.FromSeconds(5), ct));
        Assert.Equal("remote", channel.CloseReason);
        // A closed channel is not closed again.
        await channel.DisposeAsync();
        _ = connection.RequestAsync("diag.mark", new JsonObject { ["note"] = "x" }, TimeSpan.FromSeconds(5), ct);
        Assert.Equal("diag.mark", (await core.ReadAsync(ct))?["method"]?.GetValue<string>());
    }

    [Fact]
    public async Task RfcommOpenErrorsAndClosesAreRequests()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var core = new FakeCore();
        var accept = core.AcceptAsync(State(), rev: 1, features: ["rfcomm"]);
        await using var connection = await A2dpBridgeConnection.OpenAsync(core.PipeName, "encodeck/test", TimeSpan.FromSeconds(5), ct);
        await accept;

        var open = connection.OpenRfcommAsync("AA:BB:CC:DD:EE:FF", ControlService, ct);
        await Respond(core, await core.ReadAsync(ct), null, Error("rfcomm.service_not_found"));
        var error = await Assert.ThrowsAsync<BridgeRequestException>(() => open);
        Assert.Equal("rfcomm.service_not_found", error.Code);

        open = connection.OpenRfcommAsync("AA:BB:CC:DD:EE:FF", ControlService, ct);
        await Respond(core, await core.ReadAsync(ct), new JsonObject { ["channel_id"] = 4, ["mtu"] = 127 });
        var channel = await open;
        var dispose = channel.DisposeAsync().AsTask();
        var close = await core.ReadAsync(ct);
        Assert.Equal("rfcomm.close", close?["method"]?.GetValue<string>());
        Assert.Equal(4, close?["params"]?["channel_id"]?.GetValue<int>());
        await Respond(core, close, null);
        await dispose.WaitAsync(TimeSpan.FromSeconds(5), ct);

        // Given up before the answer: the channel the answer opens is closed at once.
        using var giveUp = CancellationTokenSource.CreateLinkedTokenSource(ct);
        open = connection.OpenRfcommAsync("AA:BB:CC:DD:EE:FF", ControlService, giveUp.Token);
        var late = await core.ReadAsync(ct);
        await giveUp.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => open);
        await Respond(core, late, new JsonObject { ["channel_id"] = 5, ["mtu"] = 127 });
        close = await core.ReadAsync(ct);
        Assert.Equal("rfcomm.close", close?["method"]?.GetValue<string>());
        Assert.Equal(5, close?["params"]?["channel_id"]?.GetValue<int>());

        // The session ends: an open channel ends with it.
        open = connection.OpenRfcommAsync("AA:BB:CC:DD:EE:FF", ControlService, ct);
        await Respond(core, await core.ReadAsync(ct), new JsonObject { ["channel_id"] = 6, ["mtu"] = 127 });
        channel = await open;
        await core.SendAsync(new JsonObject { ["type"] = "event", ["rev"] = 1, ["event"] = new JsonObject { ["kind"] = "bye" } });
        Assert.Equal(0, await channel.ReadAsync(new byte[4], ct).AsTask().WaitAsync(TimeSpan.FromSeconds(5), ct));
        Assert.Equal("disconnected", channel.CloseReason);
    }

    [Fact]
    public async Task TheBridgeConnectorTriesEachServiceOnTheStreamedDevice()
    {
        var ct = TestContext.Current.CancellationToken;
        string instance = "encodeck-test-" + Guid.NewGuid().ToString("N")[..8];
        await using var monitor = new A2dpBridgeMonitor(instance: instance) { IdleInterval = TimeSpan.FromMilliseconds(100) };
        await using var core = new FakeCore(instance);
        var accept = core.AcceptAsync(State(), rev: 1, features: ["rfcomm"]);
        monitor.Start();
        Assert.True(await monitor.WaitForConnectionAsync(TimeSpan.FromSeconds(10), ct));
        await accept;
        Assert.True(monitor.Status.Rfcomm);

        var other = Guid.NewGuid();
        var connector = new A2dpBridgeChannelConnector(() => monitor);
        var open = connector.OpenAsync("BTHENUM#x4", [other, ControlService], ct);
        var request = await core.ReadAsync(ct);
        Assert.Equal(other, Guid.Parse(request!["params"]!["uuid"]!.GetValue<string>()));
        // The address is the streamed profile's.
        Assert.Equal("AA:BB:CC:DD:EE:FF", request["params"]!["address"]!.GetValue<string>());
        await Respond(core, request, null, Error("rfcomm.service_not_found"));
        request = await core.ReadAsync(ct);
        Assert.Equal(ControlService, Guid.Parse(request!["params"]!["uuid"]!.GetValue<string>()));
        await Respond(core, request, new JsonObject { ["channel_id"] = 1, ["mtu"] = 990 });
        var result = await open;
        Assert.Equal(EncoDeck.Protocol.Transport.ChannelOpenStatus.Opened, result.Status);
        Assert.Equal(ControlService, result.ServiceUuid);

        // Not connected yet: retried later, not given up on.
        open = connector.OpenAsync("BTHENUM#x4", [ControlService], ct);
        await Respond(core, await core.ReadAsync(ct), null, Error("rfcomm.not_connected"));
        Assert.Equal(EncoDeck.Protocol.Transport.ChannelOpenStatus.DeviceUnreachable, (await open).Status);
        open = connector.OpenAsync("BTHENUM#x4", [ControlService], ct);
        await Respond(core, await core.ReadAsync(ct), null, Error("rfcomm.open_failed", "0x04"));
        var failed = await open;
        Assert.Equal(EncoDeck.Protocol.Transport.ChannelOpenStatus.ConnectFailed, failed.Status);
        Assert.Contains("0x04", failed.Detail);
    }

    [Fact]
    public async Task ACoreWithoutRfcommIsNeverAskedForIt()
    {
        var ct = TestContext.Current.CancellationToken;
        string instance = "encodeck-test-" + Guid.NewGuid().ToString("N")[..8];
        await using var monitor = new A2dpBridgeMonitor(instance: instance) { IdleInterval = TimeSpan.FromMilliseconds(100) };
        await using var core = new FakeCore(instance);
        var accept = core.AcceptAsync(State(), rev: 1);
        monitor.Start();
        Assert.True(await monitor.WaitForConnectionAsync(TimeSpan.FromSeconds(10), ct));
        await accept;
        Assert.False(monitor.Status.Rfcomm);
        await Assert.ThrowsAsync<NotSupportedException>(() => monitor.OpenRfcommAsync("AA:BB:CC:DD:EE:FF", ControlService, ct));
        var result = await new A2dpBridgeChannelConnector(() => monitor).OpenAsync("BTHENUM#x4", [ControlService], ct);
        Assert.Equal(EncoDeck.Protocol.Transport.ChannelOpenStatus.DeviceUnreachable, result.Status);
        // Nothing was sent: an old core would close the session on it.
        _ = monitor.RequestAsync("diag.mark", new JsonObject { ["note"] = "x" }, ct: ct);
        Assert.Equal("diag.mark", (await core.ReadAsync(ct))?["method"]?.GetValue<string>());
    }

    private static Task Respond(FakeCore core, JsonObject? request, JsonNode? result, JsonObject? error = null) => core.SendAsync(new JsonObject
    {
        ["type"] = "response", ["id"] = request!["id"]!.GetValue<long>(), ["result"] = result, ["error"] = error,
    });

    private static JsonObject Error(string code, params string[] args) => new()
    {
        ["code"] = code,
        ["message"] = new JsonObject { ["key"] = "ipc.error." + code.Replace('.', '_'), ["args"] = new JsonArray(args.Select(a => (JsonNode?)a).ToArray()) },
    };

    private static JsonObject RfcommEvent(string kind, uint channel, string? data = null, string? reason = null)
    {
        var value = new JsonObject { ["channel_id"] = channel };
        if (data is not null) value["data"] = data;
        if (reason is not null) value["reason"] = reason;
        return new JsonObject { ["type"] = "event", ["rev"] = 3, ["event"] = Event(kind, value) };
    }

    private static JsonObject Event(string kind, JsonNode? value) => new() { ["kind"] = kind, ["value"] = value };

    private static JsonObject StreamEvent(long rev, int volume) => new()
    {
        ["type"] = "event",
        ["rev"] = rev,
        ["event"] = Event("stream", State(volume: volume)["stream"]!.DeepClone()),
    };

    /// <summary>
    /// A stand-in for bluecaster-core (or a 2.0.x a2dpwb-core with <paramref name="legacy"/>): its mutex and its pipe
    /// (named for a test instance), speaking protocol 1.
    /// </summary>
    private sealed class FakeCore : IAsyncDisposable
    {
        private readonly Mutex _mutex;
        private readonly NamedPipeServerStream _pipe;
        private readonly string _server;

        public FakeCore(string? instance = null, bool legacy = false)
        {
            instance ??= "encodeck-test-" + Guid.NewGuid().ToString("N")[..8];
            PipeName = legacy ? A2dpBridgeMonitor.LegacyPipeName(instance) : A2dpBridgeMonitor.PipeName(instance);
            _mutex = new Mutex(false, legacy ? A2dpBridgeMonitor.LegacyMutexName(instance) : A2dpBridgeMonitor.MutexName(instance));
            _pipe = new NamedPipeServerStream(PipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
            _server = legacy ? "a2dpwb-core/2.0.1" : "bluecaster-core/3.0.0-beta.3";
        }

        public string PipeName { get; }

        /// <summary>Waits for the client, reads its hello, answers welcome + snapshot (or reject); returns the hello.</summary>
        public async Task<JsonObject> AcceptAsync(JsonObject? state, long rev = 1, string? reject = null, string[]? features = null)
        {
            await _pipe.WaitForConnectionAsync();
            var hello = await A2dpBridgeConnection.ReadFrameAsync(_pipe, CancellationToken.None) ?? throw new IOException("no hello");
            if (reject is not null)
            {
                await SendAsync(new JsonObject { ["type"] = "reject", ["reason"] = reject });
                return hello;
            }
            var welcome = new JsonObject { ["type"] = "welcome", ["protocol"] = 1, ["server"] = _server };
            // Cores before 2.0.2 send no features at all.
            if (features is not null) welcome["features"] = new JsonArray(features.Select(f => (JsonNode?)f).ToArray());
            await SendAsync(welcome);
            await SendAsync(new JsonObject { ["type"] = "snapshot", ["rev"] = rev, ["state"] = state });
            return hello;
        }

        public Task SendAsync(JsonObject message) => A2dpBridgeConnection.WriteFrameAsync(_pipe, message, CancellationToken.None);

        public Task<JsonObject?> ReadAsync(CancellationToken ct) => A2dpBridgeConnection.ReadFrameAsync(_pipe, ct).WaitAsync(TimeSpan.FromSeconds(5), ct);

        public async ValueTask DisposeAsync()
        {
            await _pipe.DisposeAsync();
            _mutex.Dispose();
        }
    }
}
