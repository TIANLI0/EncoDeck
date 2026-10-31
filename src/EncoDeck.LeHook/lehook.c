// SPDX-License-Identifier: GPL-3.0-only
//
// EncoDeck LE Audio hook. Loaded into the svchost that hosts BthAvctpSvc, where Windows' LE Audio unicast client
// (Microsoft.Bluetooth.Audio.dll) picks the LC3 configuration it asks the earbuds for. Two functions are hooked:
//   LC3ClientCodec::GetPreferredConfigurationFromCapabilities: the Config Codec request (frame duration, octets per
//     frame, target latency). The earbuds echo it and Windows sets up the offload data path from the echo.
//   LC3ClientCodec::FindQosSettings: the QoS Windows sizes the Config QoS request and the CIG from (SDU interval, max
//     SDU, retransmissions, max transport latency). It comes from Windows' own preset, so it has to follow the
//     rewritten codec request. Windows picks low latency while a game plays and high reliability otherwise; a
//     LatencyMode other than auto pins one of them. The resulting transport is recorded in link.txt.
// Both read the user's choice from HKLM\SOFTWARE\EncoDeck\LeHook for every new stream; with nothing chosen they
// only record.
//
// No Windows build is built in: the elevated injector (EncoDeck's --le-hook-apply, or EncoDeck.Probe le-hook apply)
// finds both functions in the installed Microsoft.Bluetooth.Audio.dll from its public symbols, and writes each one's
// offset and first whole instructions to HKLM (Hook0Rva/Hook0Prologue, Hook1Rva/Hook1Prologue). LeHookEnable patches
// only when the loaded module holds exactly those bytes there; otherwise Windows is left alone. The structures below
// are checked at every call (flags, codec id, LTV layout, element size, known presets); a build that changed them is
// passed through unchanged.
// The injector calls the exported LeHookEnable / LeHookDisable on a remote thread; loading the DLL by itself changes
// nothing. Disabling restores the original bytes but keeps the DLL (and its trampolines) loaded until the service
// restarts, so a thread that is still inside a hook can always return.
#include <windows.h>
#include <stdint.h>
#include <stdio.h>
#include <string.h>

#define TARGET_MODULE L"Microsoft.Bluetooth.Audio.dll"
#define CONFIG_KEY L"SOFTWARE\\EncoDeck\\LeHook"

// The patch is a 14-byte absolute jump; the injector moves 14 to 24 bytes of whole instructions to the trampoline.
#define JUMP_SIZE 14
#define MAX_PROLOGUE 24

// std::optional<ConfigCodecOperationASEParameters> returned by GetPreferredConfigurationFromCapabilities:
//   +0x00 ASE id, +0x01 target latency, +0x02 target PHY, +0x04 codec id (6 = LC3),
//   +0x10/+0x18 std::vector<uint8_t> codec specific configuration (begin/end), +0x28 has_value.
#define PREF_ASE_ID 0x00
#define PREF_TARGET_LATENCY 0x01
#define PREF_CODEC_ID 0x04
#define PREF_CONFIG_BEGIN 0x10
#define PREF_CONFIG_END 0x18
#define PREF_HAS_VALUE 0x28

// std::vector<ConfigQoSOperationASEParameters> returned by FindQosSettings, 0x1C bytes per element:
//   +0x04 SDU interval (us), +0x10 max SDU, +0x12 retransmissions, +0x14 max transport latency (ms),
//   +0x18 presentation delay (us).
#define QOS_ELEMENT_SIZE 0x1C
#define QOS_SDU_INTERVAL 0x04
#define QOS_MAX_SDU 0x10
#define QOS_RETRANSMISSIONS 0x12
#define QOS_MAX_LATENCY 0x14

// LC3 codec specific configuration LTV types and values (Bluetooth Assigned Numbers 6.12.5).
#define LTV_SAMPLING_FREQUENCY 0x01
#define LTV_FRAME_DURATION 0x02
#define LTV_OCTETS_PER_FRAME 0x04
#define SAMPLING_48000 0x08
#define DURATION_7500 0x00
#define DURATION_10000 0x01

typedef struct
{
    const char *name;
    void *detour;
    // From HKLM at LeHookEnable: offset in the module and the whole, position-independent instructions there.
    uint32_t rva;
    uint8_t prologue[MAX_PROLOGUE];
    size_t length;
    uint8_t *target;
    uint8_t *trampoline;
} Hook;

typedef void *(*GetPreferredFn)(void *self, void *result, void *pacs, void *ascs, uint32_t location);
typedef void *(*FindQosFn)(void *self, void *result);

static void *HookGetPreferred(void *self, void *result, void *pacs, void *ascs, uint32_t location);
static void *HookFindQos(void *self, void *result);

// In the injector's order (LeAudioHookTargets.Functions): Hook0, Hook1.
static Hook g_hooks[] = {
    {"LC3ClientCodec::GetPreferredConfigurationFromCapabilities", (void *)HookGetPreferred, 0, {0}, 0, NULL, NULL},
    {"LC3ClientCodec::FindQosSettings", (void *)HookFindQos, 0, {0}, 0, NULL, NULL},
};
#define HOOK_COUNT (sizeof g_hooks / sizeof g_hooks[0])

static LONG g_installed;
static SRWLOCK g_logLock = SRWLOCK_INIT;

static void Log(const char *format, ...)
{
    char line[512];
    SYSTEMTIME now;
    GetLocalTime(&now);
    int n = _snprintf(line, sizeof line, "%04u-%02u-%02u %02u:%02u:%02u.%03u ", now.wYear, now.wMonth, now.wDay,
                      now.wHour, now.wMinute, now.wSecond, now.wMilliseconds);
    va_list args;
    va_start(args, format);
    int m = _vsnprintf(line + n, sizeof line - n - 2, format, args);
    va_end(args);
    n = m < 0 ? (int)sizeof line - 2 : n + m;
    line[n++] = '\r';
    line[n++] = '\n';

    wchar_t path[MAX_PATH];
    if (!ExpandEnvironmentStringsW(L"%ProgramData%\\EncoDeck\\LeHook\\lehook.log", path, MAX_PATH)) return;
    AcquireSRWLockExclusive(&g_logLock);
    HANDLE file = CreateFileW(path, FILE_APPEND_DATA, FILE_SHARE_READ | FILE_SHARE_WRITE, NULL, OPEN_ALWAYS,
                              FILE_ATTRIBUTE_NORMAL, NULL);
    if (file != INVALID_HANDLE_VALUE)
    {
        DWORD written;
        WriteFile(file, line, (DWORD)n, &written, NULL);
        CloseHandle(file);
    }
    ReleaseSRWLockExclusive(&g_logLock);
}

static DWORD ReadConfig(const wchar_t *name)
{
    DWORD value = 0, size = sizeof value;
    if (RegGetValueW(HKEY_LOCAL_MACHINE, CONFIG_KEY, name, RRF_RT_REG_DWORD, NULL, &value, &size) != ERROR_SUCCESS)
        return 0;
    return value;
}

// The configurations a BAP 48 kHz QoS preset exists for (48_1 .. 48_5); anything else is not applied.
static BOOL IsStandardPreset(DWORD frameUs, DWORD octets)
{
    if (frameUs == 7500) return octets == 75 || octets == 90 || octets == 117;
    if (frameUs == 10000) return octets == 100 || octets == 120;
    return FALSE;
}

// LatencyMode: Windows' own choice, or one transport for every stream.
#define MODE_AUTO 0
#define MODE_LOW_LATENCY 1
#define MODE_HIGH_RELIABILITY 2

typedef struct
{
    BOOL preset; // frameUs/octets hold a preset to apply; otherwise Windows' own codec choice
    DWORD frameUs;
    DWORD octets;
    DWORD mode;
} Choice;

static Choice ReadChoice(void)
{
    Choice choice;
    choice.frameUs = ReadConfig(L"FrameDurationUs");
    choice.octets = ReadConfig(L"OctetsPerFrame");
    choice.preset = IsStandardPreset(choice.frameUs, choice.octets);
    choice.mode = ReadConfig(L"LatencyMode");
    if (choice.mode > MODE_HIGH_RELIABILITY) choice.mode = MODE_AUTO;
    return choice;
}

static BOOL IsReadWrite(const void *p, size_t size)
{
    MEMORY_BASIC_INFORMATION info;
    if (!VirtualQuery(p, &info, sizeof info) || info.State != MEM_COMMIT) return FALSE;
    if (!(info.Protect & (PAGE_READWRITE | PAGE_EXECUTE_READWRITE))) return FALSE;
    return (const uint8_t *)p + size <= (const uint8_t *)info.BaseAddress + info.RegionSize;
}

static void RewriteCodec(uint8_t *result, uint32_t location)
{
    if (!result || result[PREF_HAS_VALUE] != 1 || result[PREF_CODEC_ID] != 6) return;
    uint8_t *begin = *(uint8_t **)(result + PREF_CONFIG_BEGIN);
    uint8_t *end = *(uint8_t **)(result + PREF_CONFIG_END);
    if (!begin || end <= begin || end - begin > 64 || !IsReadWrite(begin, (size_t)(end - begin))) return;

    uint8_t *frequency = NULL, *duration = NULL, *octets = NULL;
    for (uint8_t *p = begin; p < end;)
    {
        uint8_t length = p[0];
        if (length == 0 || p + 1 + length > end) return; // not the layout we expect: leave it alone
        if (p[1] == LTV_SAMPLING_FREQUENCY && length == 2) frequency = p + 2;
        else if (p[1] == LTV_FRAME_DURATION && length == 2) duration = p + 2;
        else if (p[1] == LTV_OCTETS_PER_FRAME && length == 3) octets = p + 2;
        p += 1 + length;
    }
    if (!frequency || !duration || !octets) return;

    uint8_t oldDuration = *duration;
    unsigned oldOctets = octets[0] | octets[1] << 8;
    unsigned oldLatency = result[PREF_TARGET_LATENCY];
    Choice choice = ReadChoice();
    const char *note = "unchanged";
    if (*frequency != SAMPLING_48000)
    {
        note = "not 48 kHz, unchanged";
    }
    else
    {
        if (choice.preset)
        {
            *duration = choice.frameUs == 10000 ? DURATION_10000 : DURATION_7500;
            octets[0] = (uint8_t)choice.octets;
            octets[1] = (uint8_t)(choice.octets >> 8);
            note = "rewritten";
        }
        // ASCS target latency: 1 = low latency, 2 = balanced (what Windows asks for outside games).
        if (choice.mode == MODE_LOW_LATENCY) result[PREF_TARGET_LATENCY] = 1;
        else if (choice.mode == MODE_HIGH_RELIABILITY) result[PREF_TARGET_LATENCY] = 2;
        if (result[PREF_TARGET_LATENCY] != oldLatency) note = "rewritten";
    }
    Log("codec: ase %u location 0x%x: %s %s %u octets latency %u -> %s %u octets latency %u (%s)", result[PREF_ASE_ID],
        location, *frequency == SAMPLING_48000 ? "48 kHz" : "other rate", oldDuration == DURATION_10000 ? "10 ms" : "7.5 ms",
        oldOctets, oldLatency, *duration == DURATION_10000 ? "10 ms" : "7.5 ms", (unsigned)(octets[0] | octets[1] << 8),
        result[PREF_TARGET_LATENCY], note);
}

// BAP keeps the retransmissions and stretches the max transport latency with the frame: 15 <-> 20 ms (low latency),
// 75 <-> 100 ms (high reliability).
static uint16_t ScaleLatency(uint16_t latency, uint32_t fromUs, uint32_t toUs)
{
    if (fromUs == toUs) return latency;
    if (toUs == 10000) return latency == 15 ? 20 : latency == 75 ? 100 : latency;
    return latency == 20 ? 15 : latency == 100 ? 75 : latency;
}

// Records the transport Windows is about to use, for EncoDeck's "switched" notice and the card's current link:
// %ProgramData%\EncoDeck\LeHook\link.txt, rewritten for every new stream.
static void RecordLink(uint32_t interval, unsigned maxSdu, unsigned latency, unsigned retransmissions)
{
    wchar_t path[MAX_PATH];
    if (!ExpandEnvironmentStringsW(L"%ProgramData%\\EncoDeck\\LeHook\\link.txt", path, MAX_PATH)) return;
    FILETIME now;
    GetSystemTimeAsFileTime(&now);
    unsigned long long ticks = (unsigned long long)now.dwHighDateTime << 32 | now.dwLowDateTime;
    char text[160];
    int n = _snprintf(text, sizeof text, "time=%llu\nframeUs=%u\nmaxSdu=%u\nlatencyMs=%u\nretransmissions=%u\n", ticks,
                      interval, maxSdu, latency, retransmissions);
    if (n <= 0) return;
    AcquireSRWLockExclusive(&g_logLock);
    HANDLE file = CreateFileW(path, GENERIC_WRITE, FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE, NULL,
                              CREATE_ALWAYS, FILE_ATTRIBUTE_NORMAL, NULL);
    if (file != INVALID_HANDLE_VALUE)
    {
        DWORD written;
        WriteFile(file, text, (DWORD)n, &written, NULL);
        CloseHandle(file);
    }
    ReleaseSRWLockExclusive(&g_logLock);
}

static void RewriteQos(uint8_t *result)
{
    if (!result) return;
    uint8_t *begin = *(uint8_t **)(result + 0x00);
    uint8_t *end = *(uint8_t **)(result + 0x08);
    if (!begin || end <= begin || (end - begin) % QOS_ELEMENT_SIZE != 0 || end - begin > 8 * QOS_ELEMENT_SIZE ||
        !IsReadWrite(begin, (size_t)(end - begin)))
        return;
    Choice choice = ReadChoice();
    for (uint8_t *e = begin; e < end; e += QOS_ELEMENT_SIZE)
    {
        uint32_t *interval = (uint32_t *)(e + QOS_SDU_INTERVAL);
        uint16_t *maxSdu = (uint16_t *)(e + QOS_MAX_SDU);
        uint8_t *retransmissions = e + QOS_RETRANSMISSIONS;
        uint16_t *latency = (uint16_t *)(e + QOS_MAX_LATENCY);
        // Only Windows' own LC3 48 kHz presets are touched (and recorded).
        if (!IsStandardPreset(*interval, *maxSdu)) continue;
        uint32_t oldInterval = *interval;
        unsigned oldSdu = *maxSdu, oldLatency = *latency, oldRetransmissions = *retransmissions;
        if (choice.preset)
        {
            *latency = ScaleLatency(*latency, *interval, choice.frameUs);
            *interval = choice.frameUs;
            *maxSdu = (uint16_t)choice.octets;
        }
        // BAP's two transports for 48 kHz: low latency (5 retransmissions, 15 ms / 20 ms for 10 ms frames) and high
        // reliability (13, 75 ms / 100 ms).
        if (choice.mode == MODE_LOW_LATENCY)
        {
            *retransmissions = 5;
            *latency = *interval == 10000 ? 20 : 15;
        }
        else if (choice.mode == MODE_HIGH_RELIABILITY)
        {
            *retransmissions = 13;
            *latency = *interval == 10000 ? 100 : 75;
        }
        Log("qos: %u us, max SDU %u, latency %u ms, %u retransmissions -> %u us, max SDU %u, latency %u ms, %u retransmissions",
            oldInterval, oldSdu, oldLatency, oldRetransmissions, *interval, *maxSdu, *latency, *retransmissions);
        RecordLink(*interval, *maxSdu, *latency, *retransmissions);
    }
}

static void *HookGetPreferred(void *self, void *result, void *pacs, void *ascs, uint32_t location)
{
    void *returned = ((GetPreferredFn)(void *)g_hooks[0].trampoline)(self, result, pacs, ascs, location);
    RewriteCodec((uint8_t *)result, location);
    return returned;
}

static void *HookFindQos(void *self, void *result)
{
    void *returned = ((FindQosFn)(void *)g_hooks[1].trampoline)(self, result);
    RewriteQos((uint8_t *)result);
    return returned;
}

static BOOL WriteCode(uint8_t *at, const uint8_t *bytes, size_t size)
{
    DWORD old;
    if (!VirtualProtect(at, size, PAGE_EXECUTE_READWRITE, &old)) return FALSE;
    memcpy(at, bytes, size);
    VirtualProtect(at, size, old, &old);
    FlushInstructionCache(GetCurrentProcess(), at, size);
    return TRUE;
}

// jmp qword ptr [rip+0] followed by the absolute target: 14 bytes.
static void AbsoluteJump(uint8_t *at, const void *to)
{
    static const uint8_t jmp[6] = {0xFF, 0x25, 0x00, 0x00, 0x00, 0x00};
    memcpy(at, jmp, sizeof jmp);
    memcpy(at + sizeof jmp, &to, sizeof to);
}

__declspec(dllexport) DWORD WINAPI LeHookEnable(LPVOID unused)
{
    (void)unused;
    if (InterlockedCompareExchange(&g_installed, 1, 1) == 1) return 0;
    HMODULE module = GetModuleHandleW(TARGET_MODULE);
    if (!module)
    {
        Log("enable: %ls is not loaded", TARGET_MODULE);
        return 2;
    }
    IMAGE_NT_HEADERS *nt = (IMAGE_NT_HEADERS *)((uint8_t *)module + ((IMAGE_DOS_HEADER *)module)->e_lfanew);
    for (size_t i = 0; i < HOOK_COUNT; i++)
    {
        Hook *hook = &g_hooks[i];
        wchar_t name[32];
        uint8_t prologue[MAX_PROLOGUE];
        DWORD rva = 0, size = sizeof rva, length = sizeof prologue;
        _snwprintf(name, 32, L"Hook%uRva", (unsigned)i);
        name[31] = 0;
        BOOL ok = RegGetValueW(HKEY_LOCAL_MACHINE, CONFIG_KEY, name, RRF_RT_REG_DWORD, NULL, &rva, &size) == ERROR_SUCCESS;
        _snwprintf(name, 32, L"Hook%uPrologue", (unsigned)i);
        ok = ok && RegGetValueW(HKEY_LOCAL_MACHINE, CONFIG_KEY, name, RRF_RT_REG_BINARY, NULL, prologue, &length) == ERROR_SUCCESS;
        // The bytes the injector saw in the file must be exactly what the loaded module holds there.
        ok = ok && length >= JUMP_SIZE && length <= MAX_PROLOGUE && rva + length <= nt->OptionalHeader.SizeOfImage &&
             memcmp((uint8_t *)module + rva, prologue, length) == 0;
        // A trampoline built earlier in this process must be for the same code.
        ok = ok && (!hook->trampoline || (hook->rva == rva && hook->length == length && memcmp(hook->prologue, prologue, length) == 0));
        if (!ok)
        {
            Log("enable: %s does not match the target recorded for it (rva 0x%lx, %lu bytes); nothing patched", hook->name,
                (unsigned long)rva, (unsigned long)length);
            return 3;
        }
        hook->rva = rva;
        hook->length = length;
        memcpy(hook->prologue, prologue, length);
    }

    for (size_t i = 0; i < HOOK_COUNT; i++)
    {
        Hook *hook = &g_hooks[i];
        hook->target = (uint8_t *)module + hook->rva;
        if (!hook->trampoline)
        {
            hook->trampoline = VirtualAlloc(NULL, 64, MEM_COMMIT | MEM_RESERVE, PAGE_EXECUTE_READWRITE);
            if (!hook->trampoline) return 4;
            memcpy(hook->trampoline, hook->prologue, hook->length);
            AbsoluteJump(hook->trampoline + hook->length, hook->target + hook->length);
            FlushInstructionCache(GetCurrentProcess(), hook->trampoline, 64);
        }
    }
    for (size_t i = 0; i < HOOK_COUNT; i++)
    {
        uint8_t patch[MAX_PROLOGUE];
        memset(patch, 0x90, sizeof patch);
        AbsoluteJump(patch, g_hooks[i].detour);
        if (!WriteCode(g_hooks[i].target, patch, g_hooks[i].length))
        {
            Log("enable: VirtualProtect failed on %s (%lu)", g_hooks[i].name, GetLastError());
            for (size_t j = 0; j < i; j++) WriteCode(g_hooks[j].target, g_hooks[j].prologue, g_hooks[j].length);
            return 5;
        }
    }
    InterlockedExchange(&g_installed, 1);
    Log("enabled: frame %lu us, %lu octets, latency mode %lu (0 = keep Windows' choice)", ReadConfig(L"FrameDurationUs"),
        ReadConfig(L"OctetsPerFrame"), ReadConfig(L"LatencyMode"));
    return 0;
}

__declspec(dllexport) DWORD WINAPI LeHookDisable(LPVOID unused)
{
    (void)unused;
    if (InterlockedCompareExchange(&g_installed, 0, 1) != 1) return 0;
    DWORD result = 0;
    for (size_t i = 0; i < HOOK_COUNT; i++)
    {
        if (!WriteCode(g_hooks[i].target, g_hooks[i].prologue, g_hooks[i].length))
        {
            Log("disable: VirtualProtect failed on %s (%lu)", g_hooks[i].name, GetLastError());
            result = 5;
        }
    }
    Log("disabled");
    return result;
}

BOOL WINAPI DllMain(HINSTANCE instance, DWORD reason, LPVOID reserved)
{
    (void)reserved;
    if (reason == DLL_PROCESS_ATTACH) DisableThreadLibraryCalls(instance);
    return TRUE;
}
