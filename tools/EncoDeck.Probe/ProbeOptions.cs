// SPDX-License-Identifier: GPL-3.0-only
namespace EncoDeck.Probe;

/// <summary>Tiny argument parser: first token is the command, then positional args and --key [value] options.</summary>
internal sealed class ProbeOptions
{
    private readonly Dictionary<string, string?> _options = new(StringComparer.OrdinalIgnoreCase);

    public string? Command { get; private set; }
    public List<string> Positional { get; } = [];

    // Options that never take a value.
    private static readonly HashSet<string> Flags = new(StringComparer.OrdinalIgnoreCase)
    {
        "help", "cached", "show-address", "show-sensitive", "raw", "confirm-write", "json", "verbose", "eq-list", "codec", "no-restore", "experimental", "software", "pair", "legacy-pairing", "restore", "no-call",
    };

    public static ProbeOptions Parse(string[] args)
    {
        var result = new ProbeOptions();
        for (int i = 0; i < args.Length; i++)
        {
            string arg = args[i];
            if (arg.StartsWith("--", StringComparison.Ordinal))
            {
                string key = arg[2..];
                string? value = null;
                int eq = key.IndexOf('=');
                if (eq >= 0)
                {
                    value = key[(eq + 1)..];
                    key = key[..eq];
                }
                else if (!Flags.Contains(key) && i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal))
                {
                    value = args[++i];
                }
                result._options[key] = value;
            }
            else if (arg is "-h" or "/?")
            {
                result._options["help"] = null;
            }
            else if (result.Command is null)
            {
                result.Command = arg.ToLowerInvariant();
            }
            else
            {
                result.Positional.Add(arg);
            }
        }
        return result;
    }

    public bool Has(string key) => _options.ContainsKey(key);

    public string? Get(string key) => _options.TryGetValue(key, out var v) ? v : null;

    public int GetInt(string key, int fallback) =>
        int.TryParse(Get(key), out int v) ? v : fallback;
}
