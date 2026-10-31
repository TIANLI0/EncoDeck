// SPDX-License-Identifier: GPL-3.0-only
// Model/protocol facts from EarKit a77acd18890a5f28746c2303bfff0e23fff8d8ca (MIT).
using EncoDeck.Core.Capabilities;
using EncoDeck.Core.Commands;
using EncoDeck.Core.Devices;
using EncoDeck.Core.Profiles;
using EncoDeck.Core.State;
using EncoDeck.Protocol.Transport;

namespace EncoDeck.Protocol.Brands.EarFun;

public sealed record EarFunModel(string Id, string Name, bool Gaia, string Variant, string Features, bool Headphones = false)
{
    public string ModelId => "earfun-" + Id.ToLowerInvariant().Replace('_', '-');
    public bool Has(string feature) => feature == "keys" ? Id != "HP1000" : Features.Split(' ').Contains(feature, StringComparer.Ordinal);
    public bool Tw700 => Variant == "tw700";
}

public static class EarFunProfiles
{
    private const string Q = "noise game dual prompt find mic quality ear";
    private const string E = "game dual prompt find quality autooff";
    public static IReadOnlyList<EarFunModel> Models { get; } =
    [
        new("TW600", "EarFun Air Pro 4", true, "tw600", Q + " sub ambient"),
        new("TW700", "EarFun Air Pro 4+", true, "tw700", Q + " autooff sub ambient"),
        new("TW700AI", "EarFun Air Pro 4+AI", true, "tw700", Q + " autooff sub ambient"),
        new("TW800", "EarFun Air Pro 5", true, "tw700", Q + " autooff sub ambient"),
        new("TW500", "EarFun Air Pro 3", true, "qcc", Q + " sub"),
        new("TW500_3072", "EarFun Air Pro 3 (3072)", true, "qcc", Q + " sub"),
        new("TW400", "EarFun Free Pro 3", true, "qcc", Q + " sub"),
        new("TWA01", "EarFun Air S", true, "qcc", "noise game"),
        new("TW201", "EarFun Air S (TW201)", true, "qcc", "noise game"),
        new("ATVEL_TW201", "ATVEL Tune S5 ProX", true, "qcc", "noise game"),
        new("TW101S", "EarFun Free 2S", true, "qcc", "game"),
        new("HP1000", "EarFun Wave Pro X", true, "hp1000", "noise game dual prompt quality autooff sub ambient", true),
        new("TW220", "EarFun Air 2 NC", false, "ef", E + " noise spatial"),
        new("TW220_VDL", "EarFun Air 2 NC (VDL)", false, "ef", E + " noise spatial"),
        new("TW205", "EarFun Air Life", false, "tw205", "game find autooff"),
        new("TW230", "EarFun Air Pro 4i", false, "tw230", E + " noise sub ambient"),
        new("OW100", "EarFun OpenJump", false, "ef", E + " spatial"),
        new("OW100_NEW", "EarFun OpenJump (新)", false, "ef", E + " spatial"),
        new("CW100", "EarFun Clip", false, "cw100", E),
        new("CW200", "EarFun Clip 2", false, "cw200", E),
        new("HP201", "EarFun Wave Life", false, "head", "noise game dual autooff", true),
        new("HP220", "EarFun Tune Pro", false, "head", "noise game dual autooff spatial", true),
        new("HP300", "EarFun Wave Pro 2", false, "head", "noise game dual autooff spatial", true),
    ];

    public static IReadOnlyList<IDeviceProfile> CreateBuiltIn() => [.. Models.Select(m => new EarFunProfile(m))];
    private static string Normalize(string name) => string.Concat(name.Normalize().Where(c => !char.IsWhiteSpace(c))).ToUpperInvariant();
    public static EarFunModel? Named(string name)
    {
        string value = Normalize(name);
        return Models.FirstOrDefault(m => Normalize(m.Name) == value || Normalize(m.Id) == value
            || (m.Id is "TW700" or "TW700AI" && Normalize(m.Name.Replace("+", " Plus", StringComparison.Ordinal)) == value));
    }
}

/// <summary>
/// Features follow the model definition, like every other brand; readback supplies the current values and confirms
/// each write. <paramref name="observed"/> lists the settings these earbuds answered, which refresh polls.
/// </summary>
public sealed class EarFunProfile(EarFunModel model, IReadOnlySet<ushort>? observed = null) : IControllableProfile, IBrandGestureProfile
{
    private const string Evidence = "EarKit protocol port, by model.";
    public EarFunModel Model => model;
    public string Brand => "earfun";
    public bool IsGeneric => false;
    public string ModelId => model.ModelId;
    public string DisplayName => model.Name;
    public string ImageKey => ModelId;
    public DeviceForm Form => model.Headphones ? DeviceForm.Headphones : DeviceForm.Earbuds;
    public IReadOnlyList<Guid> ControlServiceUuids => [new("00001101-0000-1000-8000-00805f9b34fb")];
    // Controlled over Classic SPP; Windows alone decides whether audio uses LE Audio (issue 16).
    public bool ManagesLeAudioLink => false;
    public bool Confirmed(ushort command) => observed?.Contains(command) == true;
    public IReadOnlyList<BrandGestureSlot> GestureSlots => EarFunGestures.Slots(model);
    public IDeviceCapabilities Capabilities
    {
        get
        {
            var b = DeviceCapabilities.CreateBuilder()
                .Set(DeviceCapability.BatteryLevel, CapabilitySupport.Supported, Evidence)
                .Set(DeviceCapability.FirmwareInformation, CapabilitySupport.Supported, Evidence);
            foreach (var (capability, feature) in new[] {
                (DeviceCapability.NoiseCancellation, "noise"), (DeviceCapability.GamingMode, "game"),
                (DeviceCapability.DualDeviceConnection, "dual"), (DeviceCapability.InEarDetection, "ear"),
                (DeviceCapability.PromptVolume, "prompt"), (DeviceCapability.FindMyEarbuds, "find"),
                (DeviceCapability.SpatialAudio, "spatial"), (DeviceCapability.TouchCustomization, "keys") })
                b.Set(capability, model.Has(feature) ? CapabilitySupport.Supported : CapabilitySupport.Unsupported, Evidence);
            return b.Build();
        }
    }
    public IReadOnlyCollection<DeviceToggle> Toggles =>
    [
        .. model.Has("game") ? new[] { DeviceToggle.GamingMode } : [],
        .. model.Has("dual") ? new[] { DeviceToggle.DualDeviceConnection } : [],
        .. model.Has("ear") ? new[] { DeviceToggle.InEarDetection } : [],
    ];
    public IReadOnlyList<NoiseControlOption> NoiseControlOptions =>
        [.. EarFunSettings.NoiseModes(model).Select(p => new NoiseControlOption(p.State.Mode, p.State.LevelId, p.Label, p.Key, CapabilitySupport.Supported))];
    // EarKit has EQ writes, but no device EQ readback. Never advertise saved host values as confirmed device EQ.
    public IReadOnlyList<EqualizerPreset> EqualizerPresets => [];
    public int MatchScore(DeviceIdentity identity) => EarFunProfiles.Named(identity.Name) == model ? 100 : 0;
    public IDeviceController CreateController(IDuplexByteChannel channel, ControllerContext context) => new EarFunDeviceController(channel, context, this);
}
