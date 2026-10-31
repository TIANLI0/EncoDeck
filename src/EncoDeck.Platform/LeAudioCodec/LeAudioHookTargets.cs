// SPDX-License-Identifier: GPL-3.0-only
using System.Globalization;
using System.Reflection.PortableExecutable;
using System.Runtime.InteropServices;

namespace EncoDeck.Platform.LeAudioCodec;

/// <summary>A function the hook patches: its offset in the module and the whole instructions it moves to its trampoline.</summary>
public sealed record LeAudioHookTarget(string Name, int Rva, byte[] Prologue);

/// <summary>Why <see cref="LeAudioHookTargets.ResolveAsync"/> found nothing to hook.</summary>
public enum LeAudioHookResolveError
{
    None,
    /// <summary>Microsoft's public symbols for this build could not be downloaded (offline, proxy, not published yet).</summary>
    SymbolsUnavailable,
    /// <summary>This build has no such functions, or they start with code the hook cannot move safely.</summary>
    Unsupported,
}

/// <summary>
/// Finds the two functions the hook patches in whatever build of Microsoft.Bluetooth.Audio.dll is installed: the
/// DLL's public symbols (Microsoft's symbol server, cached) name them, and their first instructions must be ones the
/// trampoline can run elsewhere. Nothing is tied to a Windows version; a build that renamed or rewrote them is
/// reported unsupported.
/// </summary>
public static partial class LeAudioHookTargets
{
    /// <summary>The functions, in the order the hook DLL expects them (Hook0, Hook1).</summary>
    public static readonly string[] Functions =
    [
        "Microsoft::Bluetooth::Profiles::Bap::UnicastClient::LC3ClientCodec::GetPreferredConfigurationFromCapabilities",
        "Microsoft::Bluetooth::Profiles::Bap::UnicastClient::LC3ClientCodec::FindQosSettings",
    ];

    /// <summary>The patch is an absolute jump of 14 bytes; at most this many bytes of prologue are moved.</summary>
    internal const int JumpSize = 14;
    internal const int MaxPrologue = 24;

    private const string SymbolServer = "https://msdl.microsoft.com/download/symbols";
    private static readonly SemaphoreSlim DbgHelpGate = new(1, 1);

    public static async Task<(IReadOnlyList<LeAudioHookTarget>? Targets, LeAudioHookResolveError Error)> ResolveAsync(
        string dllPath, string cacheDirectory, CancellationToken ct = default)
    {
        byte[] image;
        CodeViewDebugDirectoryData codeView;
        try
        {
            image = await File.ReadAllBytesAsync(dllPath, ct).ConfigureAwait(false);
            using var pe = new PEReader(new MemoryStream(image));
            var entry = pe.ReadDebugDirectory().FirstOrDefault(e => e.Type == DebugDirectoryEntryType.CodeView);
            if (entry.DataSize == 0) return (null, LeAudioHookResolveError.Unsupported);
            codeView = pe.ReadCodeViewDebugDirectoryData(entry);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or BadImageFormatException)
        {
            return (null, LeAudioHookResolveError.Unsupported);
        }

        string pdbName = Path.GetFileName(codeView.Path);
        string key = codeView.Guid.ToString("N").ToUpperInvariant() + codeView.Age.ToString("X", CultureInfo.InvariantCulture);
        string pdbDirectory = Path.Combine(cacheDirectory, pdbName, key);
        string pdbPath = Path.Combine(pdbDirectory, pdbName);
        if (!File.Exists(pdbPath))
        {
            try
            {
                Directory.CreateDirectory(pdbDirectory);
                using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(2) };
                using var response = await http.GetAsync($"{SymbolServer}/{pdbName}/{key}/{pdbName}", ct).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode) return (null, LeAudioHookResolveError.SymbolsUnavailable);
                string partial = pdbPath + ".partial";
                await using (var file = File.Create(partial))
                    await response.Content.CopyToAsync(file, ct).ConfigureAwait(false);
                File.Move(partial, pdbPath, overwrite: true);
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException or UnauthorizedAccessException or TaskCanceledException)
            {
                return (null, LeAudioHookResolveError.SymbolsUnavailable);
            }
        }

        int[]? rvas;
        await DbgHelpGate.WaitAsync(ct).ConfigureAwait(false);
        try { rvas = LookUp(dllPath, image.Length, pdbDirectory, Functions); }
        finally { DbgHelpGate.Release(); }
        if (rvas is null) return (null, LeAudioHookResolveError.Unsupported);

        var targets = new List<LeAudioHookTarget>();
        using (var pe = new PEReader(new MemoryStream(image)))
        {
            for (int i = 0; i < Functions.Length; i++)
            {
                var block = pe.GetSectionData(rvas[i]);
                if (block.Length < MaxPrologue) return (null, LeAudioHookResolveError.Unsupported);
                byte[] code = block.GetContent(0, MaxPrologue).ToArray();
                if (PrologueLength(code) is not { } length) return (null, LeAudioHookResolveError.Unsupported);
                targets.Add(new LeAudioHookTarget(Functions[i], rvas[i], code[..length]));
            }
        }
        return (targets, LeAudioHookResolveError.None);
    }

    /// <summary>
    /// Length of the whole instructions that cover the 14-byte jump, or null when one of them cannot run from a
    /// trampoline (RIP-relative, a branch, or anything this small decoder does not know). Covers the usual x64 function
    /// prologues: register saves to the stack, push, sub/add rsp, mov/lea between registers.
    /// </summary>
    internal static int? PrologueLength(ReadOnlySpan<byte> code)
    {
        int at = 0;
        while (at < JumpSize)
        {
            if (InstructionLength(code[at..]) is not { } length || at + length > MaxPrologue) return null;
            at += length;
        }
        return at;
    }

    private static int? InstructionLength(ReadOnlySpan<byte> code)
    {
        int at = 0;
        if (code.Length > 0 && code[0] is >= 0x40 and <= 0x4F) at++; // REX
        if (at >= code.Length) return null;
        byte opcode = code[at++];
        switch (opcode)
        {
            case >= 0x50 and <= 0x57: // push r64
                return at;
            case 0x89 or 0x8B or 0x8D or 0x01 or 0x03 or 0x29 or 0x2B or 0x31 or 0x33 or 0x85: // mov / lea / add / sub / xor / test r, r/m
                return ModRm(code, at, immediate: 0);
            case 0x83: // group 1 r/m, imm8 (sub rsp, 28h)
                return ModRm(code, at, immediate: 1);
            case 0x81: // group 1 r/m, imm32
                return ModRm(code, at, immediate: 4);
            default:
                return null;
        }
    }

    private static int? ModRm(ReadOnlySpan<byte> code, int at, int immediate)
    {
        if (at >= code.Length) return null;
        byte modRm = code[at++];
        int mod = modRm >> 6, rm = modRm & 7;
        if (mod == 3) return at + immediate;
        if (mod == 0 && rm == 5) return null; // RIP-relative: would point elsewhere from the trampoline
        if (rm == 4)
        {
            if (at >= code.Length) return null;
            byte sib = code[at++];
            if (mod == 0 && (sib & 7) == 5) at += 4;
        }
        at += mod switch { 1 => 1, 2 => 4, _ => 0 };
        return at + immediate;
    }

    /// <summary>The functions' offsets from the module base, read from its public symbols with the system's DbgHelp.</summary>
    private static unsafe int[]? LookUp(string dllPath, int size, string pdbDirectory, string[] names)
    {
        // Any unique value identifies the session when no process is attached.
        nint session = 0x4C48;
        const ulong Base = 0x10000000;
        SymSetOptions(SymOptUndName | SymOptFailCriticalErrors | SymOptNoPrompts | SymOptExactSymbols);
        if (!SymInitializeW(session, pdbDirectory, false)) return null;
        try
        {
            if (SymLoadModuleExW(session, 0, dllPath, null, Base, (uint)size, 0, 0) == 0) return null;
            const int MaxName = 512;
            int bufferSize = sizeof(SYMBOL_INFOW) + MaxName * sizeof(char);
            byte* buffer = stackalloc byte[bufferSize];
            var result = new int[names.Length];
            for (int i = 0; i < names.Length; i++)
            {
                new Span<byte>(buffer, bufferSize).Clear();
                var info = (SYMBOL_INFOW*)buffer;
                info->SizeOfStruct = (uint)sizeof(SYMBOL_INFOW);
                info->MaxNameLen = MaxName;
                if (!SymFromNameW(session, names[i], info)) return null;
                result[i] = checked((int)(info->Address - Base));
            }
            return result;
        }
        finally
        {
            SymCleanup(session);
        }
    }

    private const uint SymOptUndName = 0x2;
    private const uint SymOptFailCriticalErrors = 0x200;
    private const uint SymOptExactSymbols = 0x400;
    private const uint SymOptNoPrompts = 0x80000;

    [StructLayout(LayoutKind.Sequential)]
    private struct SYMBOL_INFOW
    {
        public uint SizeOfStruct;
        public uint TypeIndex;
        public ulong Reserved0;
        public ulong Reserved1;
        public uint Index;
        public uint Size;
        public ulong ModBase;
        public uint Flags;
        public ulong Value;
        public ulong Address;
        public uint Register;
        public uint Scope;
        public uint Tag;
        public uint NameLen;
        public uint MaxNameLen;
        public char Name;
    }

    [LibraryImport("dbghelp.dll")]
    private static partial uint SymSetOptions(uint options);

    [LibraryImport("dbghelp.dll", EntryPoint = "SymInitializeW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SymInitializeW(nint process, string? searchPath, [MarshalAs(UnmanagedType.Bool)] bool invadeProcess);

    [LibraryImport("dbghelp.dll", EntryPoint = "SymLoadModuleExW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial ulong SymLoadModuleExW(nint process, nint file, string imageName, string? moduleName, ulong baseOfDll, uint dllSize, nint data, uint flags);

    [LibraryImport("dbghelp.dll", EntryPoint = "SymFromNameW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static unsafe partial bool SymFromNameW(nint process, string name, SYMBOL_INFOW* symbol);

    [LibraryImport("dbghelp.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SymCleanup(nint process);
}
