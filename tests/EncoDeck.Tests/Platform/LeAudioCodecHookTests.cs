// SPDX-License-Identifier: GPL-3.0-only
using EncoDeck.Platform.LeAudioCodec;

namespace EncoDeck.Tests.Platform;

public sealed class LeAudioCodecHookTests
{
    [Fact]
    public void PresetsAreTheBapOnesWithQosPresets()
    {
        // The hook DLL only applies these pairs (IsStandardPreset in lehook.c): 48_1 .. 48_5.
        var pairs = LeAudioCodecPreset.All.Where(p => !p.IsWindowsDefault).Select(p => (p.FrameUs, p.Octets)).ToHashSet();
        Assert.Equal([(7500, 75), (10000, 100), (7500, 90), (10000, 120), (7500, 117)], pairs);
        Assert.Single(LeAudioCodecPreset.All, p => p.IsWindowsDefault);
    }

    [Theory]
    [InlineData("48_1", 80)]
    [InlineData("48_2", 80)]
    [InlineData("48_3", 96)]
    [InlineData("48_4", 96)]
    [InlineData("48_5", 124.8)]
    public void BitratePerChannel(string id, double kbps) =>
        Assert.Equal(kbps, LeAudioCodecPreset.Find(id)!.Kbps, precision: 6);

    [Theory]
    [InlineData("default", 11.5, 15, 75)]
    [InlineData("48_3", 11.5, 15, 75)]
    [InlineData("48_5", 11.5, 15, 75)]
    [InlineData("48_2", 12.5, 20, 100)]
    [InlineData("48_4", 12.5, 20, 100)]
    public void LatencyFollowsTheFrameDuration(string id, double codecMs, int gameMs, int mediaMs)
    {
        // The latency limits match what the hook DLL writes into the QoS (ScaleLatency in lehook.c).
        var preset = LeAudioCodecPreset.Find(id)!;
        Assert.Equal(codecMs, preset.CodecDelayMs);
        Assert.Equal(gameMs, preset.GameLatencyLimitMs);
        Assert.Equal(mediaMs, preset.MediaLatencyLimitMs);
    }

    [Fact]
    public void FindsByIdAndByConfiguration()
    {
        Assert.Same(LeAudioCodecPreset.WindowsDefault, LeAudioCodecPreset.Find("DEFAULT"));
        Assert.Equal("48_4", LeAudioCodecPreset.Find(10000, 120)?.Id);
        Assert.Null(LeAudioCodecPreset.Find(10000, 155));
        Assert.Null(LeAudioCodecPreset.Find("48_6"));
    }

    [Theory]
    [InlineData(false, true, LeAudioHookResolveError.None, LeAudioCodecSupport.NotX64)]
    [InlineData(true, false, LeAudioHookResolveError.None, LeAudioCodecSupport.ComponentMissing)]
    [InlineData(true, true, LeAudioHookResolveError.Unsupported, LeAudioCodecSupport.UnsupportedWindows)]
    [InlineData(true, true, LeAudioHookResolveError.SymbolsUnavailable, LeAudioCodecSupport.SymbolsUnavailable)]
    [InlineData(true, true, LeAudioHookResolveError.None, LeAudioCodecSupport.Supported)]
    public void Support(bool x64, bool component, LeAudioHookResolveError resolve, LeAudioCodecSupport expected) =>
        Assert.Equal(expected, LeAudioCodecHook.Evaluate(x64, component, resolve));

    [Theory]
    // GetPreferredConfigurationFromCapabilities on 26340.9767: mov [rsp+18h],rbx / mov [rsp+10h],rdx / push rbp, rsi, rdi / push r12 | push r13
    [InlineData("48895C241848895424105556574154 4155 4156", 15)]
    // FindQosSettings: mov rax,rsp / mov [rax+20h],rbx / mov [rax+10h],rdx / push rbp, rsi, rdi | push r12
    [InlineData("488BC44889582048895010555657 4154 4157", 14)]
    // sub rsp,28h / mov [rsp+30h],rbx (SIB, disp8) / mov rbx,rcx | ...
    [InlineData("4883EC28 48895C2430 488BD9 33C0 C3", 14)]
    public void PrologueCoversTheJumpWithWholeInstructions(string hex, int expected) =>
        Assert.Equal(expected, LeAudioHookTargets.PrologueLength(Convert.FromHexString(hex.Replace(" ", ""))));

    [Theory]
    // lea rax,[rip+...]: points elsewhere once moved
    [InlineData("488D05000000004883EC2848895C2430")]
    // a call inside the moved bytes
    [InlineData("4883EC28E80000000048895C24304889")]
    // jmp
    [InlineData("EB0E4883EC2848895C243048895C2430")]
    public void RefusesCodeThatCannotMove(string hex) =>
        Assert.Null(LeAudioHookTargets.PrologueLength(Convert.FromHexString(hex)));

    [Fact]
    public void StateRoundTrips()
    {
        var state = new LeAudioCodecHook.AppliedState(2220, 7500, 117, LeAudioLatencyMode.LowLatency, NoCallMode: true);
        Assert.Equal(state, LeAudioCodecHook.ParseState(LeAudioCodecHook.FormatState(state)));
        Assert.Null(LeAudioCodecHook.ParseState(""));
        // A state written before the transport and call choices existed reads as automatic, calls allowed.
        var old = LeAudioCodecHook.ParseState("pid=1\nframeUs=7500\noctets=117\n")!;
        Assert.Equal(LeAudioLatencyMode.Auto, old.Mode);
        Assert.False(old.NoCallMode);
    }

    [Fact]
    public void AppliedChoiceIsActiveOnlyInTheSameServiceProcess()
    {
        var state = new LeAudioCodecHook.AppliedState(2220, 7500, 117, LeAudioLatencyMode.HighReliability);
        Assert.Equal(new LeAudioCodecApplied(LeAudioCodecPreset.Find("48_5")!, LeAudioLatencyMode.HighReliability, false, true),
            LeAudioCodecHook.Interpret(state, 2220));
        // The service restarted (new process) or is not running: the hook is gone.
        Assert.False(LeAudioCodecHook.Interpret(state, 4096).Active);
        Assert.False(LeAudioCodecHook.Interpret(state, 0).Active);
        // A transport alone (Windows' codec) is a choice too, and so is keeping calls off the link.
        Assert.False(LeAudioCodecHook.Interpret(new(2220, 0, 0, LeAudioLatencyMode.LowLatency), 2220).IsWindowsDefault);
        var noCall = LeAudioCodecHook.Interpret(new(2220, 0, 0, NoCallMode: true), 2220);
        Assert.False(noCall.IsWindowsDefault);
        Assert.True(noCall.NoCallMode);
        Assert.False(LeAudioCodecHook.Interpret(new(2220, 0, 0, NoCallMode: true), 4096).Active);
    }

    [Fact]
    public void NothingOrDefaultAppliedMeansWindowsDefault()
    {
        Assert.True(LeAudioCodecHook.Interpret(null, 2220).IsWindowsDefault);
        Assert.True(LeAudioCodecHook.Interpret(new(2220, 0, 0), 4096).IsWindowsDefault);
        // An unknown pair (hand-edited file) is not reported as applied.
        Assert.Same(LeAudioCodecPreset.WindowsDefault, LeAudioCodecHook.Interpret(new(2220, 10000, 155), 2220).Preset);
    }

    [Fact]
    public void ParsesTheLinkTheHookRecords()
    {
        var at = new DateTimeOffset(2026, 10, 31, 7, 30, 0, TimeSpan.Zero);
        var link = LeAudioCodecHook.ParseLink($"time={at.ToFileTime()}\nframeUs=7500\nmaxSdu=117\nlatencyMs=15\nretransmissions=5\n");
        Assert.NotNull(link);
        Assert.Equal(at, link.At);
        Assert.Equal(124.8, link.Kbps, precision: 6);
        Assert.Equal(LeAudioLatencyMode.LowLatency, link.Mode);
        Assert.Null(LeAudioCodecHook.ParseLink("frameUs=7500\n"));
    }

    [Theory]
    [InlineData("48_5", LeAudioLatencyMode.LowLatency, 7500, 117, 15, true)]
    [InlineData("48_5", LeAudioLatencyMode.Auto, 7500, 117, 75, true)]
    [InlineData("48_5", LeAudioLatencyMode.HighReliability, 7500, 117, 15, false)]
    [InlineData("48_5", LeAudioLatencyMode.Auto, 7500, 90, 75, false)]
    [InlineData("48_4", LeAudioLatencyMode.HighReliability, 10000, 120, 100, true)]
    [InlineData("48_4", LeAudioLatencyMode.LowLatency, 10000, 120, 20, true)]
    [InlineData("default", LeAudioLatencyMode.Auto, 7500, 90, 75, true)]
    [InlineData("default", LeAudioLatencyMode.LowLatency, 7500, 90, 75, false)]
    public void ALinkMatchesTheAppliedChoice(string id, LeAudioLatencyMode mode, int frameUs, int maxSdu, int latencyMs, bool matches) =>
        Assert.Equal(matches, new LeAudioLinkConfig(DateTimeOffset.Now, frameUs, maxSdu, latencyMs, 5).Matches(LeAudioCodecPreset.Find(id)!, mode));
}
