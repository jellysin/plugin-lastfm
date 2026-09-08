using System;
using System.IO;
using System.Linq;
using MediaBrowser.Common.Configuration;

namespace Jellyfin.Plugin.Lastfm.Providers;

public static class LastfmHelper
{
    public static string? GetImageUrl(IHasLastFmImages data, out string size)
    {
        size = string.Empty;
        var images = data.image?.Where(image => !string.IsNullOrWhiteSpace(image.url)).ToArray() ?? [];
        var image = images.FirstOrDefault(image => string.Equals(image.size, "mega", StringComparison.OrdinalIgnoreCase))
            ?? images.FirstOrDefault(image => string.Equals(image.size, "extralarge", StringComparison.OrdinalIgnoreCase))
            ?? images.FirstOrDefault(image => string.Equals(image.size, "large", StringComparison.OrdinalIgnoreCase))
            ?? images.FirstOrDefault(image => string.Equals(image.size, "medium", StringComparison.OrdinalIgnoreCase))
            ?? images.FirstOrDefault();
        if (image is null)
        {
            return null;
        }
        size = image.size;
        return image.url;
    }

    public static string? GetImageCachePath(IApplicationPaths appPaths, string? musicBrainzId)
    {
        // Provider ids originate in media metadata, so they cannot be used as path fragments.
        return Guid.TryParse(musicBrainzId, out var id)
            ? Path.Combine(appPaths.CachePath, "lastfm", id.ToString("D"), "image.txt")
            : null;
    }

    public static void SaveImageInfo(IApplicationPaths appPaths, string musicBrainzId, string url, string size)
    {
        ArgumentNullException.ThrowIfNull(appPaths);
        ArgumentException.ThrowIfNullOrEmpty(url);
        var path = GetImageCachePath(appPaths, musicBrainzId)
            ?? throw new ArgumentException("A valid MusicBrainz id is required.", nameof(musicBrainzId));
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
        {
            return;
        }
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, url + "|" + size);
    }
}
