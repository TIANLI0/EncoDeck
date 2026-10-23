// SPDX-License-Identifier: GPL-3.0-only
using EncoDeck.Core.Profiles;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;

namespace EncoDeck.App.Services;

/// <summary>
/// Artwork for cup models, from <c>Assets\Cups\{ImageKey}</c>: a 3D model (<c>.vfxms</c> or a
/// turntable <c>.webp</c>) and a 2D image (<c>.png</c>). No cup model ships artwork yet, so both
/// fall back to the capsule placeholder.
/// </summary>
internal static class CupArtwork
{
    private static string Folder => Path.Combine(AppContext.BaseDirectory, "Assets", "Cups");

    /// <summary>3D model path, or null to show <see cref="CreateImage"/> instead.</summary>
    public static string? ModelPath(ICupProfile? profile) => Find(profile, ".vfxms", ".webp");

    /// <summary>The model's 2D image, or the capsule placeholder.</summary>
    public static Image CreateImage(ICupProfile? profile, double height)
    {
        ImageSource source = Find(profile, ".png") is { } png
            ? new BitmapImage(new Uri(png))
            : new SvgImageSource(new Uri(Path.Combine(Folder, "capsule.svg"))) { RasterizePixelHeight = height * 2 };
        return new Image { Source = source, Height = height, Stretch = Stretch.Uniform };
    }

    private static string? Find(ICupProfile? profile, params string[] extensions)
    {
        if (profile is null) return null;
        foreach (string extension in extensions)
        {
            string path = Path.Combine(Folder, profile.ImageKey + extension);
            if (File.Exists(path)) return path;
        }
        return null;
    }
}
