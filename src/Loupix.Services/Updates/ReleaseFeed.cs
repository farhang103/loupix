using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;

namespace Loupix.Services.Updates;

/// <summary>A published version newer than the running one. No installer means the release page is the way to get it.</summary>
public sealed record AvailableUpdate(Version Version, string PageUrl, string? InstallerUrl, string? Sha256, string? Highlight);

/// <summary>Reads Loupix's releases on GitHub.</summary>
public static class ReleaseFeed
{
    public const string LatestUrl = "https://api.github.com/repos/farhang103/loupix/releases/latest";

    /// <summary>Reads GitHub's "latest release" response. Null when that release isn't newer than <paramref name="current"/>.</summary>
    public static AvailableUpdate? Parse(string json, Version current, string arch)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (!Version.TryParse(root.GetProperty("tag_name").GetString()?.TrimStart('v', 'V'), out var published)) return null;
        var version = ThreeParts(published);
        if (version <= ThreeParts(current)) return null;

        string? installer = null, sha256 = null;
        foreach (var asset in root.GetProperty("assets").EnumerateArray())
        {
            if (!string.Equals(asset.GetProperty("name").GetString(), $"Loupix-Setup-{arch}.exe", StringComparison.OrdinalIgnoreCase)) continue;
            installer = asset.GetProperty("browser_download_url").GetString();
            if (asset.TryGetProperty("digest", out var digest) && digest.GetString() is { } d && d.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase))
                sha256 = d["sha256:".Length..];
        }
        return new AvailableUpdate(version, root.GetProperty("html_url").GetString()!, installer, sha256, Highlight(root.GetProperty("body").GetString()));
    }

    /// <summary>Saves the file at <paramref name="path"/>. Throws, leaving nothing behind, if it doesn't match the published checksum.</summary>
    public static async Task DownloadAsync(HttpClient http, string url, string path, string? sha256, IProgress<double>? progress, CancellationToken cancel)
    {
        using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancel);
        response.EnsureSuccessStatusCode();
        long total = response.Content.Headers.ContentLength ?? 0;
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        await using (var source = await response.Content.ReadAsStreamAsync(cancel))
        await using (var file = File.Create(path))
        {
            var buffer = new byte[81920];
            long done = 0;
            int read;
            while ((read = await source.ReadAsync(buffer, cancel)) > 0)
            {
                await file.WriteAsync(buffer.AsMemory(0, read), cancel);
                hash.AppendData(buffer, 0, read);
                done += read;
                if (total > 0) progress?.Report((double)done / total);
            }
        }
        if (sha256 != null && !Convert.ToHexString(hash.GetHashAndReset()).Equals(sha256, StringComparison.OrdinalIgnoreCase))
        {
            File.Delete(path);
            throw new InvalidDataException("The download doesn't match its published checksum.");
        }
    }

    // 1.2.0 and 1.2.0.0 are different System.Version values, and the assembly version always has four parts.
    private static Version ThreeParts(Version v) => new(v.Major, v.Minor, Math.Max(v.Build, 0));

    /// <summary>The first line of the release notes that isn't a heading.</summary>
    private static string? Highlight(string? notes) =>
        notes?.Split('\n').Select(line => line.Trim()).FirstOrDefault(line => line.Length > 0 && !line.StartsWith('#'))?.TrimStart('-', '*', ' ');
}
