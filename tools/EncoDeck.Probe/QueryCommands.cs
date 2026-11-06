// SPDX-License-Identifier: GPL-3.0-only
using EncoDeck.Protocol.Oppo;

namespace EncoDeck.Probe;

/// <summary>Read-only protocol queries. None of these change device settings.</summary>
internal static class QueryCommands
{
    /// <summary>Feature ids requested by the community status query (meaning of several is unknown).</summary>
    internal static readonly byte[] StatusQueryPayload = [0x0B, 0x05, 0x04, 0x0B, 0x11, 0x13, 0x18, 0x06, 0x1B, 0x1C, 0x27, 0x28];

    public static IReadOnlyList<(string Name, OppoRequest Request)> StandardQueries { get; } =
    [
        ("handshake", new OppoRequest(OppoCommand.Handshake, [])),
        ("product-id", new OppoRequest(OppoCommand.QueryProductId, [])),
        ("event-codes", new OppoRequest(OppoCommand.QueryEventCodes, [], ResponseCommands: [OppoCommand.ResponseOf(OppoCommand.QueryEventCodes), OppoCommand.EventCodesAltResponse])),
        ("feature-status", new OppoRequest(OppoCommand.QueryFeatureStatus, StatusQueryPayload, SequenceMode.FixedQuery)),
        ("battery", new OppoRequest(OppoCommand.QueryBattery, [], SequenceMode.FixedSet)),
        ("noise-control", new OppoRequest(OppoCommand.QueryNoiseControl, [0x01, 0x01])),
        ("equalizer", new OppoRequest(OppoCommand.QueryEqualizer, [])),
        ("firmware", new OppoRequest(OppoCommand.QueryFirmware, [])),
        ("wear-state", new OppoRequest(OppoCommand.QueryWearState, [])),
    ];

    public static OppoRequest EqualizerList { get; } = new(OppoCommand.QueryEqualizerList, [0x01, 0x05]);

    public static async Task<int> QueryAsync(ProbeOptions options, CancellationToken ct)
    {
        var trace = new ConsoleTrace(options.Has("raw"));
        var opened = await ProbeSession.OpenAsync(options, trace, ct);
        if (opened is null) return 4;
        var (session, _, _) = opened.Value;
        await using var _ = session;

        var unsolicited = new List<OppoFrame>();
        session.UnsolicitedFrame += (_, f) => { lock (unsolicited) unsolicited.Add(f); };

        var queries = StandardQueries.ToList();
        if (options.Has("codec")) queries.Add(("codec", new OppoRequest(OppoCommand.QueryCodec, [])));
        if (options.Has("eq-list")) queries.Add(("equalizer-list", EqualizerList));
        string? only = options.Get("only");

        var results = new List<(string Name, RequestResult Result)>();
        foreach (var (name, request) in queries)
        {
            if (only is not null && !only.Split(',').Contains(name) && name != "handshake") continue;
            var result = await session.RequestAsync(request, TimeSpan.FromMilliseconds(options.GetInt("timeout-ms", 3000)), ct);
            results.Add((name, result));
            if (!session.IsOpen) break;
        }

        // Give the device a moment for trailing notifications.
        await Task.Delay(TimeSpan.FromSeconds(1.5), ct);

        Console.WriteLine();
        Console.WriteLine("Summary:");
        foreach (var (name, result) in results)
        {
            string detail = result.Response is { } r ? $"{OppoCommand.Describe(r.Command)} seq={r.Sequence:X2} payload={ConsoleTrace.PayloadText(r)}" : result.Detail ?? string.Empty;
            Console.WriteLine($"  {name,-16} {result.Status,-15} {detail}");
            if (result.Response is { } response) Console.WriteLine($"  {"",-16} -> {OppoInterpreter.Describe(response)}");
        }
        lock (unsolicited)
        {
            Console.WriteLine($"Unsolicited frames: {unsolicited.Count}");
            foreach (var f in unsolicited) Console.WriteLine($"  {OppoCommand.Describe(f.Command)} seq={f.Sequence:X2} payload={ConsoleTrace.PayloadText(f)} -> {OppoInterpreter.Describe(f)}");
        }
        Console.WriteLine($"Discarded bytes while framing: {session.DiscardedBytes}");
        await ReportWriter.TryWriteAsync(options, "query", trace, results.Select(r => (r.Name, r.Result.Status.ToString())).ToList(), ct);
        return results.All(r => r.Result.IsSuccess) ? 0 : 5;
    }

    /// <summary>Subscribes to events and also polls battery / noise control / features, printing only changes.</summary>
    public static async Task<int> WatchAsync(ProbeOptions options, CancellationToken ct)
    {
        // Other brands are watched through the app's own runtime, which picks the model's control
        // service and protocol; OPPO frames are never sent to them.
        var resolved = await ProbeSession.ResolveAsync(options, ct);
        if (resolved is null) return 4;
        if (!ProbeSession.SpeaksOppoProtocol(resolved.Value.Profile))
        {
            return await MonitorCommand.RunAsync(options, ct, watch: true);
        }

        var trace = new ConsoleTrace(printRaw: false) { Quiet = true };
        var opened = await ProbeSession.OpenAsync(options, trace, ct);
        if (opened is null) return 4;
        var (session, _, _) = opened.Value;
        await using var _ = session;

        var start = DateTimeOffset.UtcNow;
        void Print(string source, OppoFrame f) =>
            Console.WriteLine($"{(DateTimeOffset.UtcNow - start).TotalSeconds,6:F1}s {source,-6} {OppoCommand.Describe(f.Command),-28} {ConsoleTrace.PayloadText(f)}  -> {OppoInterpreter.Describe(f)}");

        session.UnsolicitedFrame += (_, f) => Print("EVENT", f);
        await session.RequestAsync(StandardQueries[0].Request, cancellationToken: ct);
        var codes = await session.RequestAsync(StandardQueries[2].Request, cancellationToken: ct);
        if (codes.Response is { } codeFrame && OppoParsers.TryParseEventCodes(codeFrame, out var list))
        {
            await session.SendAsync(OppoCommand.SubscribeEvents, [(byte)list.Count, .. list], cancellationToken: ct);
        }

        int seconds = options.GetInt("seconds", 120);
        var interval = TimeSpan.FromMilliseconds(Math.Max(500, options.GetInt("interval-ms", 2000)));
        // battery, noise control, feature status, equalizer, wear state
        var polled = new List<OppoRequest> { StandardQueries[4].Request, StandardQueries[5].Request, StandardQueries[3].Request, StandardQueries[6].Request, StandardQueries[8].Request };
        if (options.Has("codec")) polled.Add(new OppoRequest(OppoCommand.QueryCodec, []));
        var last = new Dictionary<ushort, string>();
        Console.WriteLine($"Watching for {seconds}s (poll every {interval.TotalSeconds:F1}s, only changes are printed). Ctrl+C to stop.");
        var deadline = DateTimeOffset.UtcNow.AddSeconds(seconds);
        try
        {
            while (DateTimeOffset.UtcNow < deadline && session.IsOpen)
            {
                foreach (var request in polled)
                {
                    var result = await session.RequestAsync(request, cancellationToken: ct);
                    if (result.Response is not { } frame)
                    {
                        Console.WriteLine($"{(DateTimeOffset.UtcNow - start).TotalSeconds,6:F1}s POLL   {OppoCommand.Describe(request.Command)} -> {result.Status}");
                        continue;
                    }
                    string hex = Convert.ToHexString(frame.Payload);
                    if (!last.TryGetValue(frame.Command, out var previous) || previous != hex)
                    {
                        last[frame.Command] = hex;
                        Print("POLL", frame);
                    }
                }
                await Task.Delay(interval, ct);
            }
        }
        catch (OperationCanceledException)
        {
        }
        await ReportWriter.TryWriteAsync(options, "watch", trace, [], CancellationToken.None);
        return 0;
    }

    /// <summary>
    /// One explicit GET request (command group 0x01 only — the protocol's read group).
    /// This is for targeted diagnostics, not scanning: exactly one command per invocation.
    /// </summary>
    public static async Task<int> GetAsync(ProbeOptions options, CancellationToken ct)
    {
        if (options.Get("cmd") is not { } cmdText || !TryParseUShort(cmdText, out ushort command))
        {
            Console.Error.WriteLine("Usage: get --cmd 0x01NN [--payload HEX]");
            return 2;
        }
        if ((command & 0xFF00) != 0x0100)
        {
            Console.Error.WriteLine("Refused: 'get' only sends read commands in the 0x01xx group.");
            return 2;
        }
        byte[] payload = options.Get("payload") is { } hex ? Convert.FromHexString(hex.Replace(" ", string.Empty)) : [];

        var trace = new ConsoleTrace(options.Has("raw"));
        var opened = await ProbeSession.OpenAsync(options, trace, ct);
        if (opened is null) return 4;
        var (session, _, _) = opened.Value;
        await using var _ = session;
        await session.RequestAsync(StandardQueries[0].Request, cancellationToken: ct);
        var result = await session.RequestAsync(new OppoRequest(command, payload), TimeSpan.FromMilliseconds(options.GetInt("timeout-ms", 3000)), ct);
        Console.WriteLine($"Result: {result.Status} {(result.Response is { } r ? ConsoleTrace.PayloadText(r) + " -> " + OppoInterpreter.Describe(r) : result.Detail)}");
        return result.IsSuccess ? 0 : 5;
    }

    private static bool TryParseUShort(string text, out ushort value) =>
        text.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            ? ushort.TryParse(text[2..], System.Globalization.NumberStyles.HexNumber, null, out value)
            : ushort.TryParse(text, out value);

    public static async Task<int> ListenAsync(ProbeOptions options, CancellationToken ct)
    {
        var trace = new ConsoleTrace(options.Has("raw"));
        var opened = await ProbeSession.OpenAsync(options, trace, ct);
        if (opened is null) return 4;
        var (session, _, _) = opened.Value;
        await using var _ = session;

        session.UnsolicitedFrame += (_, f) => Console.WriteLine($"           -> {OppoInterpreter.Describe(f)}");

        await session.RequestAsync(StandardQueries[0].Request, cancellationToken: ct);
        var codes = await session.RequestAsync(StandardQueries[2].Request, cancellationToken: ct);
        if (codes.Response is { } codeFrame && OppoParsers.TryParseEventCodes(codeFrame, out var list))
        {
            Console.WriteLine($"Subscribing to {list.Count} event code(s): {ConsoleTrace.Spaced(list.ToArray())}");
            await session.SendAsync(OppoCommand.SubscribeEvents, [(byte)list.Count, .. list], cancellationToken: ct);
        }
        else
        {
            Console.WriteLine("Event code query failed; listening without subscription.");
        }

        int seconds = options.GetInt("seconds", 60);
        Console.WriteLine($"Listening for {seconds}s. Try: toggle noise control on the earbuds, take a bud out, open/close the case. Ctrl+C to stop.");
        var closed = new TaskCompletionSource();
        session.Closed += (_, e) =>
        {
            Console.WriteLine($"Channel closed: {e.Reason}");
            closed.TrySetResult();
        };
        try
        {
            await Task.WhenAny(Task.Delay(TimeSpan.FromSeconds(seconds), ct), closed.Task);
        }
        catch (OperationCanceledException)
        {
        }
        await ReportWriter.TryWriteAsync(options, "listen", trace, [], CancellationToken.None);
        return 0;
    }
}
