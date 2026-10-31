// SPDX-License-Identifier: GPL-3.0-only
namespace EncoDeck.Core.Resources;

/// <summary>Visual resources a device can have. Each is optional; the UI falls back gracefully.</summary>
public enum DeviceResourceRole
{
    /// <summary>Earbuds with case (main product image).</summary>
    Device,
    /// <summary>Charging case only.</summary>
    Case,
    LeftBud,
    RightBud,
    /// <summary>Connection pop-up animation (video, animated image or image).</summary>
    PopupAnimation,
    /// <summary>Night (dark theme) variant of the pop-up animation; falls back to <see cref="PopupAnimation"/>.</summary>
    PopupAnimationDark,
    /// <summary>
    /// 360° turntable of the device: an animated WebP whose frames are evenly spaced viewing
    /// angles, or a HeyMelody 3D scene (<c>.vfxms</c>) rendered into one. Shown on the home page, dragged to rotate.
    /// </summary>
    DeviceModel,
    /// <summary>Official product idle animation, separate from an interactive 3D model.</summary>
    DeviceAnimation,
    DeviceAnimationDark,
}

public enum DeviceResourceKind
{
    Image,
    AnimatedImage,
    Video,
    Lottie,
}

/// <summary>Where a resolved resource came from (shown to the user in Settings).</summary>
public enum DeviceResourceOrigin
{
    Custom,
    /// <summary>Downloaded from EncoAPI and cached on this PC.</summary>
    Downloaded,
    /// <summary>Existing local resource cache from an earlier version.</summary>
    Official,
    ImportedPack,
    BundledPack,
}

public sealed record DeviceResourceFile(string Path, DeviceResourceKind Kind, DeviceResourceOrigin Origin, string? PackName, bool RepresentativeArtwork = false);

/// <summary>Identifies the device a resource is for. Colour variants fall back to the default colour.</summary>
public sealed record DeviceResourceKey(string ModelId, uint? ProductId, int? ColorId, string? DeviceName = null, string? ModelName = null)
{
    public string ProductIdText => ProductId is { } pid ? pid.ToString("X6") : string.Empty;
    public string Brand => ModelId.Split('-', ':')[0].ToLowerInvariant();
    // USB receiver profiles ("<brand>-usb-<model>") never carry an OPLUS product ID, whatever the brand.
    public bool UsesOplusId => Brand is not ("vivo" or "iqoo" or "huawei" or "honor" or "earfun" or "xiaomi" or "redmi" or "edifier" or "realme" or "dizo" or "sony" or "akg" or "moondrop" or "sennheiser" or "dareu" or "rapoo" or "mchose" or "razer"
        // No artwork source for these brands yet: cache identity only, never downloaded.
        or "samsung" or "google" or "bose" or "nothing")
        && !ModelId.Contains("-usb-", StringComparison.Ordinal);
    public bool CanDownload => UsesOplusId
        ? ProductId is <= 0xFFFFFF && ColorId is >= 0 and <= 16
        : (Brand is "vivo" or "iqoo" or "huawei" or "honor" or "earfun" or "xiaomi" or "redmi" or "edifier" or "realme" or "dizo" or "sony" or "akg" or "moondrop" or "sennheiser" or "dareu" or "rapoo" or "mchose" or "razer")
          && (ColorId is null or (>= 0 and <= 255)) && (!string.IsNullOrWhiteSpace(DeviceName) || !string.IsNullOrWhiteSpace(ModelName));

    // Family profiles (e.g. Xiaomi) can serve many devices. Their caches must never share artwork.
    public string CacheIdentity => UsesOplusId ? ProductIdText : Brand + "-" + Convert.ToHexStringLower(
        System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes($"{ModelId}\n{DeviceName}\n{ModelName}")))[..24];

    public static DeviceResourceKey? FromState(EncoDeck.Core.State.DeviceState state, uint? fallbackProductId = null)
    {
        if (state.Device is null) return null;
        uint? productId = state.ProductId ?? fallbackProductId;
        string? modelId = state.Profile?.ModelId ?? (productId is { } pid ? $"oppo-{pid:X6}" : null);
        if (modelId is null) return null;
        if (state.Profile?.DisplayName.StartsWith("realme ", StringComparison.OrdinalIgnoreCase) == true && !modelId.StartsWith("realme-", StringComparison.Ordinal)) modelId = "realme-" + modelId;
        if (state.Profile?.DisplayName.StartsWith("DIZO ", StringComparison.OrdinalIgnoreCase) == true && !modelId.StartsWith("dizo-", StringComparison.Ordinal)) modelId = "dizo-" + modelId;
        var key = new DeviceResourceKey(modelId, productId, state.ColorId);
        return key.UsesOplusId ? key : key with { DeviceName = state.Device.Name, ModelName = state.Profile?.DisplayName };
    }
}

public interface IDeviceResourceProvider
{
    /// <summary>Raised when resources were added, removed or replaced.</summary>
    event EventHandler? Changed;

    DeviceResourceFile? Resolve(DeviceResourceKey key, DeviceResourceRole role);

    /// <summary>Copies <paramref name="sourceFile"/> as the user's custom resource for the model and role.</summary>
    Task SetCustomAsync(DeviceResourceKey key, DeviceResourceRole role, string sourceFile, CancellationToken cancellationToken = default);

    void ClearCustom(DeviceResourceKey key, DeviceResourceRole role);

    public static DeviceResourceKind KindOf(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".mp4" or ".webm" or ".mov" or ".m4v" => DeviceResourceKind.Video,
        ".gif" or ".apng" => DeviceResourceKind.AnimatedImage,
        ".json" => DeviceResourceKind.Lottie,
        _ => DeviceResourceKind.Image,
    };

    public static IReadOnlySet<string> SupportedExtensions { get; } =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".png", ".jpg", ".jpeg", ".bmp", ".gif", ".webp", ".mp4", ".webm", ".mov", ".m4v", ".vfxms", ".json" };
}
