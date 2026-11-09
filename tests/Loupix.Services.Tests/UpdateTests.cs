using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using Loupix.Services.Updates;

namespace Loupix.Services.Tests;

public class ReleaseFeedTests
{
    private static readonly Version Current = new(1, 2, 0, 0); // assembly versions have four parts

    private static string Release(string tag = "v1.3.0", string body = "null") => $$"""
        {
          "tag_name": "{{tag}}",
          "html_url": "https://github.com/farhang103/loupix/releases/tag/{{tag}}",
          "body": {{body}},
          "assets": [
            { "name": "Loupix-Setup-arm64.exe", "browser_download_url": "https://example.test/arm64.exe", "digest": "sha256:aaa" },
            { "name": "Loupix-Setup-x64.exe", "browser_download_url": "https://example.test/x64.exe", "digest": "sha256:bbb" },
            { "name": "Loupix-win-x64.zip", "browser_download_url": "https://example.test/x64.zip", "digest": "sha256:ccc" }
          ]
        }
        """;

    [Theory]
    [InlineData("x64", "https://example.test/x64.exe", "bbb")]
    [InlineData("arm64", "https://example.test/arm64.exe", "aaa")]
    public void A_newer_release_offers_the_installer_for_this_PC(string arch, string url, string sha)
    {
        var update = ReleaseFeed.Parse(Release(), Current, arch)!;
        Assert.Equal("1.3.0", update.Version.ToString(3));
        Assert.Equal(url, update.InstallerUrl);
        Assert.Equal(sha, update.Sha256);
    }

    [Theory]
    [InlineData("v1.2.0")]
    [InlineData("v1.1.9")]
    [InlineData("not-a-version")]
    public void The_same_or_an_older_release_offers_nothing(string tag) =>
        Assert.Null(ReleaseFeed.Parse(Release(tag), Current, "x64"));

    [Fact]
    public void A_release_without_an_installer_points_to_its_page()
    {
        var json = Release().Replace("Loupix-Setup-x64.exe", "something-else.exe");
        var update = ReleaseFeed.Parse(json, Current, "x64")!;
        Assert.Null(update.InstallerUrl);
        Assert.EndsWith("/v1.3.0", update.PageUrl);
    }

    [Theory]
    [InlineData("null", null)]
    [InlineData("\"\"", null)]
    [InlineData("\"## What's new\\r\\n\\r\\n- Hide your previous screenshot\\r\\n- More\"", "Hide your previous screenshot")]
    public void The_highlight_is_the_first_line_of_the_notes(string body, string? expected) =>
        Assert.Equal(expected, ReleaseFeed.Parse(Release(body: body), Current, "x64")!.Highlight);
}

public class ReleaseDownloadTests
{
    private sealed class Serve(byte[] bytes) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancel) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) });
    }

    private static readonly byte[] Installer = Encoding.UTF8.GetBytes("pretend installer");

    private static string TempPath() => Path.Combine(Path.GetTempPath(), "loupix-test-" + Guid.NewGuid().ToString("N") + ".exe");

    [Fact]
    public async Task A_file_matching_its_checksum_is_kept()
    {
        var path = TempPath();
        try
        {
            await ReleaseFeed.DownloadAsync(new HttpClient(new Serve(Installer)), "https://example.test/a.exe", path, Convert.ToHexString(SHA256.HashData(Installer)), null, CancellationToken.None);
            Assert.Equal(Installer, File.ReadAllBytes(path));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task A_file_not_matching_its_checksum_is_thrown_away()
    {
        var path = TempPath();
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            ReleaseFeed.DownloadAsync(new HttpClient(new Serve(Installer)), "https://example.test/a.exe", path, new string('0', 64), null, CancellationToken.None));
        Assert.False(File.Exists(path));
    }
}
